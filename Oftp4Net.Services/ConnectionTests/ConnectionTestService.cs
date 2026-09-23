using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json.Serialization;
using Havit.Services.TimeServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Oftp4Net.Core;
using Oftp4Net.Core.Protocol;
using Oftp4Net.Core.Session;
using Oftp4Net.Core.Transport;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;
using Oftp4Net.Services.Oftp;
using Oftp4Net.Services.TransferEvents;
using Oftp4Net.Services.Tsl;

namespace Oftp4Net.Services.ConnectionTests;

/// <summary>How far a connection test got; for a failed one, where it stopped.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConnectionTestStage>))]
public enum ConnectionTestStage
{
    /// <summary>The test was not run, e.g. because a session with the partner was running.</summary>
    Skipped,

    /// <summary>Preparing the connection here, e.g. loading the certificates.</summary>
    Setup,

    /// <summary>Opening the TCP connection.</summary>
    Connect,

    /// <summary>The TLS handshake, with the certificates of both sides.</summary>
    Tls,

    /// <summary>SSRM and SSID: codes, passwords, release, buffer size and credit.</summary>
    Start,

    /// <summary>SECD, AUCH and AURP: both sides prove that they hold their private keys.</summary>
    SecureAuthentication,

    /// <summary>The session started and was ended by us.</summary>
    Completed,
}

public sealed record ConnectionTestResult(
    int PartnerId,
    string Partner,
    string Identity,
    string EndPoint,
    DateTime Tested,
    TimeSpan Duration,
    ConnectionTestStage Stage,
    string Message,
    string? ReasonCode = null,
    string? Negotiated = null,
    string? Tls = null,
    string? RemoteCertificate = null,
    string? Details = null)
{
    public bool Success => Stage == ConnectionTestStage.Completed;
}

/// <summary>
/// Tests the connection to partners without transferring anything: the session is opened as for sending, runs
/// through TLS, SSID and the secure authentication, and is ended with ESID before the direction changes, so
/// neither side offers a file or an End to End Response. The send queue is not touched: a failed test does not
/// count as a failed attempt of the files waiting for the partner. Every test is written to the transfer log.
/// </summary>
public sealed class ConnectionTestService(
    IServiceScopeFactory serviceScopeFactory,
    GlobalSettingsService settingsService,
    TslService tsl,
    SendService sendService,
    ITransferEventLog transferEvents,
    ITimeService timeService,
    ILogger<ConnectionTestService> logger)
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<int, ConnectionTestResult> _results = new();
    private readonly Lock _lock = new();
    private CancellationTokenSource? _run;

    /// <summary>The last result of every partner tested since the start of the application.</summary>
    public IReadOnlyDictionary<int, ConnectionTestResult> Results => _results;

    /// <summary>A series of tests is running.</summary>
    public bool IsRunning => _run is not null;

    /// <summary>Tests done and planned in the running series.</summary>
    public (int Done, int Total) Progress { get; private set; }

    /// <summary>The partner being tested at the moment.</summary>
    public int? Current { get; private set; }

    /// <summary>Raised when a test starts or ends.</summary>
    public event Action? Changed;

    /// <summary>
    /// Tests the partners one after another in the background, so that they are not called all at once from the
    /// same code. Returns false when a series is running already.
    /// </summary>
    public bool Start(IReadOnlyList<int> partnerIds, int identityId)
    {
        CancellationTokenSource run;
        lock (_lock)
        {
            if (_run is not null)
                return false;
            _run = run = new CancellationTokenSource();
            Progress = (0, partnerIds.Count);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var partnerId in partnerIds)
                {
                    if (run.IsCancellationRequested)
                        break;

                    await TestAsync(partnerId, identityId, run.Token);
                    Progress = (Progress.Done + 1, Progress.Total);
                }
            }
            catch (OperationCanceledException) when (run.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Connection tests failed");
            }
            finally
            {
                lock (_lock)
                    _run = null;
                Current = null;
                run.Dispose();
                Changed?.Invoke();
            }
        });

        Changed?.Invoke();
        return true;
    }

    /// <summary>Stops the running series after the test in progress.</summary>
    public void Cancel()
    {
        lock (_lock)
            _run?.Cancel();
    }

    public async Task<ConnectionTestResult> TestAsync(int partnerId, int identityId, CancellationToken cancellationToken = default)
    {
        Current = partnerId;
        Changed?.Invoke();
        try
        {
            var result = await RunTestAsync(partnerId, identityId, cancellationToken);
            _results[partnerId] = result;
            Record(result);
            return result;
        }
        finally
        {
            Current = null;
            Changed?.Invoke();
        }
    }

    private async Task<ConnectionTestResult> RunTestAsync(int partnerId, int identityId, CancellationToken cancellationToken)
    {
        var tested = timeService.GetCurrentTime();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        using var scope = serviceScopeFactory.CreateScope();
        var partner = await scope.ServiceProvider.GetRequiredService<IPartnerRepository>().GetObjectAsync(partnerId, cancellationToken);
        var identity = await scope.ServiceProvider.GetRequiredService<IIdentityRepository>().GetObjectAsync(identityId, cancellationToken);
        var endPoint = $"{partner.Host}:{partner.Port}";

        ConnectionTestResult Result(ConnectionTestStage stage, string message, string? reasonCode = null, string? negotiated = null,
            string? tls = null, string? certificate = null, string? details = null) =>
            new(partner.Id, partner.Name, identity.SSID, endPoint, tested, stopwatch.Elapsed, stage, message, reasonCode,
                negotiated, tls, certificate, details);

        // Two sessions of ours with the same partner could collide, some partners allow only one per code.
        if (sendService.HasActiveSession(partner.Id))
            return Result(ConnectionTestStage.Skipped, "A session with the partner is running, test again when it ended.");

        var settings = await settingsService.GetGlobalSettingsAsync();
        var diagnostics = new OftpConnectDiagnostics();
        var stage = ConnectionTestStage.Setup;
        OftpSession? session = null;

        // The handler only authenticates here; the session ends before it would be asked for files.
        using var handler = PartnerSessionHandler.ForInitiator(scope.ServiceProvider, settings, logger, partner, identity);
        try
        {
            var tlsOptions = partner.UseTls
                ? await SendService.CreateTlsOptionsAsync(scope.ServiceProvider, partner, settings, tsl.TrustAnchors,
                    tsl.VerificationRoots, cancellationToken)
                : null;

            stage = ConnectionTestStage.Connect;
            OftpTransport transport;
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectTimeout.CancelAfter(ConnectTimeout);
                try
                {
                    transport = await OftpConnector.ConnectAsync(partner.Host, partner.Port, tlsOptions, connectTimeout.Token, diagnostics);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"No answer within {ConnectTimeout.TotalSeconds:F0} seconds.");
                }
            }

            await using (transport)
            {
                stage = ConnectionTestStage.Start;
                session = new OftpSession(transport, new OftpSessionOptions
                {
                    Role = OftpRole.Initiator,
                    LocalCode = identity.SSID,
                    LocalPassword = identity.Password ?? "",
                    ExchangeBufferSize = settings.ExchangeBufferSize,
                    Credit = settings.Credit,
                    ProtocolLevel = partner.ProtocolLevel,
                    SecureAuthentication = partner.SecureAuthentication,
                    BufferCompression = partner.BufferCompression,
                    Restart = partner.Restart,
                    ResponseTimeout = TimeSpan.FromSeconds(settings.ResponseTimeoutSeconds),
                    EndAfterStart = true,
                }, handler, logger);

                await session.RunAsync(cancellationToken);
            }

            return Result(ConnectionTestStage.Completed, "The session started and was ended without transferring anything.",
                negotiated: Negotiated(session), tls: Tls(diagnostics), certificate: Certificate(diagnostics));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (stage == ConnectionTestStage.Connect && diagnostics.Connected)
                stage = ConnectionTestStage.Tls;
            if (session is not null)
                stage = session.Phase switch
                {
                    OftpSessionPhase.SecureAuthentication => ConnectionTestStage.SecureAuthentication,
                    // The start succeeded; only ending it did not, e.g. because the partner closed the connection first.
                    OftpSessionPhase.Running => ConnectionTestStage.Completed,
                    _ => ConnectionTestStage.Start,
                };

            var (message, reasonCode) = Describe(ex, diagnostics, partner);
            logger.LogWarning("Connection test with {Partner} failed at {Stage}: {Message}", partner.Name, stage, message);
            // Buffer size and credit are known only once the SSID of the other side was accepted.
            return Result(stage, message, reasonCode, session is { ExchangeBufferSize: > 0 } ? Negotiated(session) : null, Tls(diagnostics),
                Certificate(diagnostics), ex.ToString());
        }
    }

    /// <summary>What went wrong, in the words an administrator can act on.</summary>
    public static (string Message, string? ReasonCode) Describe(Exception exception, OftpConnectDiagnostics diagnostics, Partner partner)
    {
        switch (exception)
        {
            case OftpSessionAbortedException aborted:
                return ($"{partner.Name} ended the session with reason {aborted.ReasonCode} ({ReasonText(aborted.ReasonCode)})" +
                        (string.IsNullOrWhiteSpace(aborted.ReasonText) ? "." : $": {aborted.ReasonText.Trim()}"), aborted.ReasonCode);
            case OftpProtocolException refused:
                return ($"Refused here with reason {refused.ReasonCode} ({ReasonText(refused.ReasonCode)}): {refused.Message}", refused.ReasonCode);
            case AuthenticationException when diagnostics.CertificateProblem is { } problem:
                return ($"The certificate of {partner.Name} is refused: {problem}", null);
            case AuthenticationException tls:
                // Our side accepted the certificate, so the partner refused ours or the protocols do not match.
                return ($"TLS failed: {Innermost(tls)} The partner may refuse our client certificate or the TLS version.", null);
            case SocketException socket:
                return ($"No connection to {partner.Host}:{partner.Port}: {socket.Message}", null);
            case TimeoutException timeout:
                return (diagnostics.Connected
                    ? $"The TLS handshake did not finish: {timeout.Message}"
                    : $"No connection to {partner.Host}:{partner.Port}: {timeout.Message} A firewall may drop the connection.", null);
            case OftpConnectionClosedException or IOException:
                return ($"The partner closed the connection: {Innermost(exception)} It may not accept our address or our certificate.", null);
            default:
                return (exception.Message, null);
        }
    }

    private static string Innermost(Exception exception)
    {
        var inner = exception;
        while (inner.InnerException is not null)
            inner = inner.InnerException;
        var text = inner.Message.Trim();
        return text.EndsWith('.') ? text : text + ".";
    }

    public static string ReasonText(string code) => code switch
    {
        ReasonCodes.NormalTermination => "normal termination",
        ReasonCodes.CommandNotRecognised => "command not recognised",
        ReasonCodes.ProtocolViolation => "protocol violation",
        ReasonCodes.UserCodeNotKnown => "user code not known",
        ReasonCodes.InvalidPassword => "invalid password",
        ReasonCodes.LocalSiteEmergencyCloseDown => "local site emergency close down",
        ReasonCodes.CommandContainedInvalidData => "command contained invalid data",
        ReasonCodes.ExchangeBufferSizeError => "exchange buffer size error",
        ReasonCodes.ResourcesNotAvailable => "resources not available",
        ReasonCodes.TimeOut => "time out",
        ReasonCodes.ModeOrCapabilitiesIncompatible => "mode or capabilities incompatible",
        ReasonCodes.InvalidChallengeResponse => "invalid challenge response",
        ReasonCodes.SecureAuthenticationRequirementsIncompatible => "secure authentication requirements incompatible",
        _ => "unspecified",
    };

    private static string Negotiated(OftpSession session) =>
        $"OFTP {ProtocolLevels.Name(session.Level)}, buffer {session.ExchangeBufferSize}, credit {session.Credit}" +
        (session.SecureAuthenticationAgreed ? ", secure authentication" : "") +
        (session.BufferCompressionAgreed ? ", buffer compression" : "") +
        (session.RestartAgreed ? ", restart" : "");

    private static string? Tls(OftpConnectDiagnostics diagnostics) =>
        diagnostics.TlsProtocol is { } protocol ? $"{protocol}, {diagnostics.CipherSuite}" : null;

    private static string? Certificate(OftpConnectDiagnostics diagnostics) =>
        diagnostics.RemoteCertificate is { } certificate
            ? $"{certificate.Subject}, issued by {certificate.Issuer}, valid {certificate.NotBefore:d} – {certificate.NotAfter:d}"
            : null;

    private void Record(ConnectionTestResult result)
    {
        var details = string.Join("\n", new[]
        {
            result.Negotiated is null ? null : $"Negotiated: {result.Negotiated}",
            result.Tls is null ? null : $"TLS: {result.Tls}",
            result.RemoteCertificate is null ? null : $"Certificate: {result.RemoteCertificate}",
            result.Details,
        }.Where(l => l is not null));

        transferEvents.Record(new TransferEvent
        {
            Timestamp = result.Tested,
            Category = TransferEventCategory.Outgoing,
            Type = TransferEventType.ConnectionTested,
            Level = result.Success ? TransferEventLevel.Information : TransferEventLevel.Warning,
            PartnerId = result.PartnerId,
            PartnerName = result.Partner,
            RemoteEndPoint = result.EndPoint,
            DurationMs = (long)result.Duration.TotalMilliseconds,
            Message = result.Success
                ? $"Connection test with {result.Partner} as {result.Identity} succeeded"
                : $"Connection test with {result.Partner} as {result.Identity} stopped at {result.Stage}: {result.Message}",
            Details = details.Length == 0 ? null : details,
        });
    }
}
