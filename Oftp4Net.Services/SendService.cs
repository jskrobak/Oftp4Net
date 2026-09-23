using System.Collections.Concurrent;
using System.Security.Authentication;
using Havit.Services.TimeServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Oftp4Net.Core.Session;
using Oftp4Net.Core.Transport;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;
using Oftp4Net.Services.Oftp;

using Oftp4Net.Services.Tsl;

namespace Oftp4Net.Services;

/// <summary>
/// Periodically connects to partners that have files waiting in the send queue (or End to End Responses
/// waiting to be delivered) and runs an OFTP session as initiator.
/// </summary>
/// <remarks>
/// Sessions with different partners run in parallel, up to <see cref="GlobalSettings.MaxParallelSessions"/>; a
/// partner has at most one session of ours at a time. A session does not wait for the others, so a large transfer
/// to one partner does not hold up the files of the rest.
/// </remarks>
public class SendService(ILogger<SendService> logger,
    IServiceScopeFactory serviceScopeFactory,
    GlobalSettingsService globalSettingsService,
    TslService tsl,
    ITimeService timeService) : BackgroundService
{
    /// <summary>Received files are confirmed in the partner's own session; connect only when that did not happen.</summary>
    private static readonly TimeSpan EerpGracePeriod = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _trigger = new(0, 1);

    /// <summary>Running sessions by partner.</summary>
    private readonly ConcurrentDictionary<int, Task> _active = new();

    /// <summary>A session was not started because all the slots were taken; run again when one is free.</summary>
    private volatile bool _slotWanted;

    /// <summary>Partners with more to send than their running session took; run again when it ends.</summary>
    private readonly ConcurrentDictionary<int, bool> _partnerWanted = new();

    public bool IsRunning { get; private set; }
    public bool IsPaused { get; private set; }
    public DateTime? LastRun { get; private set; }

    /// <summary>When the service started, the reference for the first run.</summary>
    public DateTime? Started { get; private set; }

    /// <summary>Number of sessions with partners running at the moment.</summary>
    public int ActiveSessions => _active.Count;

    /// <summary>A session of ours with the partner is running.</summary>
    public bool HasActiveSession(int partnerId) => _active.ContainsKey(partnerId);

    public void Pause() => IsPaused = true;

    public void Resume()
    {
        IsPaused = false;
        Trigger();
    }

    /// <summary>Processes the queue now instead of waiting for the next interval.</summary>
    public void Trigger()
    {
        try
        {
            _trigger.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already triggered.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Send service started");

        Started = timeService.GetCurrentTime();
        IsRunning = true;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var settings = await globalSettingsService.GetGlobalSettingsAsync();

                if (!IsPaused)
                {
                    try
                    {
                        await ProcessQueueAsync(settings, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "Processing the send queue failed");
                    }

                    LastRun = timeService.GetCurrentTime();
                }

                try
                {
                    await _trigger.WaitAsync(TimeSpan.FromSeconds(settings.SendIntervalSeconds), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            // The running sessions end with the stopping token, each of them records its outcome.
            await Task.WhenAll(_active.Values);
            IsRunning = false;
        }
    }

    private async Task ProcessQueueAsync(GlobalSettings settings, CancellationToken stoppingToken)
    {
        List<(Partner Partner, Identity Identity)> sessions;

        using (var scope = serviceScopeFactory.CreateScope())
        {
            var sendQueue = scope.ServiceProvider.GetRequiredService<ISendQueueItemRepository>();
            var receivedFiles = scope.ServiceProvider.GetRequiredService<IReceivedFileRepository>();
            var identities = scope.ServiceProvider.GetRequiredService<IIdentityRepository>();
            var partners = scope.ServiceProvider.GetRequiredService<IPartnerRepository>();

            var items = await sendQueue.GetAllToProcessAsync();
            sessions = items
                .GroupBy(i => (i.PartnerId, i.IdentityId))
                .Select(g => (g.First().Partner, g.First().Identity))
                .ToList();

            // Received files whose EERP could not be delivered in the partner's session.
            var unconfirmed = await receivedFiles.GetAllUnconfirmedAsync(timeService.GetCurrentTime() - EerpGracePeriod, stoppingToken);
            foreach (var group in unconfirmed.GroupBy(f => (f.PartnerId!.Value, f.Destination)))
            {
                if (sessions.Any(s => s.Partner.Id == group.Key.Value))
                    continue;

                var partner = await partners.GetObjectAsync(group.Key.Value, stoppingToken);
                var identity = await identities.FindBySfidAsync(group.Key.Destination, stoppingToken);
                if (identity is not null)
                    sessions.Add((partner, identity));
            }
        }

        _slotWanted = false;
        var started = 0;
        foreach (var (partner, identity) in sessions)
        {
            if (stoppingToken.IsCancellationRequested || IsPaused)
                break;

            // The partner is served by a running session (or another of our identities waits for it), or all the
            // slots are taken: the next run comes when a session ends.
            if (_active.ContainsKey(partner.Id))
            {
                _partnerWanted[partner.Id] = true;
                continue;
            }

            if (_active.Count >= settings.MaxParallelSessions)
            {
                _slotWanted = true;
                continue;
            }

            StartSession(partner, identity, settings, stoppingToken);
            started++;
        }

        if (started > 0)
            logger.LogInformation("Send queue: {Started} partner session(s) started, {Active} running", started, _active.Count);
    }

    private void StartSession(Partner partner, Identity identity, GlobalSettings settings, CancellationToken stoppingToken)
    {
        var session = Task.Run(async () =>
        {
            try
            {
                await RunSessionAsync(partner, identity, settings, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Session with {Partner} failed", partner.Name);
            }
        }, CancellationToken.None);

        _active[partner.Id] = session;
        // Registered after the session is added, so it is removed even when it ends right away.
        _ = session.ContinueWith(ended =>
        {
            _active.TryRemove(new KeyValuePair<int, Task>(partner.Id, ended));
            var wanted = _partnerWanted.TryRemove(partner.Id, out _);
            if ((wanted || _slotWanted) && !stoppingToken.IsCancellationRequested)
                Trigger();
        }, TaskScheduler.Default);
    }

    private async Task RunSessionAsync(Partner partner, Identity identity, GlobalSettings settings, CancellationToken stoppingToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        using var handler = PartnerSessionHandler.ForInitiator(scope.ServiceProvider, settings, logger, partner, identity);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var tls = partner.UseTls ? await CreateTlsOptionsAsync(scope.ServiceProvider, partner, settings, tsl.TrustAnchors, tsl.VerificationRoots, stoppingToken) : null;

            logger.LogInformation("Connecting to {Partner} at {Host}:{Port} ({Security})",
                partner.Name, partner.Host, partner.Port, tls is null ? "plain TCP" : "TLS");

            await using var transport = await OftpConnector.ConnectAsync(partner.Host, partner.Port, tls, stoppingToken);

            var session = new OftpSession(transport, new OftpSessionOptions
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
            }, handler, logger);

            await session.RunAsync(stoppingToken);

            logger.LogInformation("Session with {Partner} finished: {Sent} file(s) sent, {Received} file(s) received",
                partner.Name, handler.FilesSent, handler.FilesReceived);
            handler.RecordSessionEnd(null, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            var stopped = new OperationCanceledException("Service stopped.");
            handler.RecordSessionEnd(stopped, stopwatch.Elapsed);
            await handler.OnSessionFailedAsync(stopped, includeUnattempted: false, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Session with {Partner} failed", partner.Name);
            handler.RecordSessionEnd(ex, stopwatch.Elapsed);
            // A failure before any file was offered (connection, TLS, authentication) affects all waiting files
            // of the partner, a failure during a transfer only the file being transferred.
            await handler.OnSessionFailedAsync(ex, includeUnattempted: !handler.FileTransferStarted, CancellationToken.None);
        }
    }

    /// <param name="tslAnchors">Certification authorities of the Odette TSL, trusted for every partner.</param>
    /// <param name="tslVerificationRoots">
    /// Certificates of the TSL that are only there to verify the authorities above them.
    /// </param>
    internal static async Task<OftpTlsOptions> CreateTlsOptionsAsync(IServiceProvider services, Partner partner,
        GlobalSettings settings, System.Security.Cryptography.X509Certificates.X509Certificate2Collection tslAnchors,
        System.Security.Cryptography.X509Certificates.X509Certificate2Collection tslVerificationRoots,
        CancellationToken cancellationToken)
    {
        var certificates = services.GetRequiredService<ICertificateRepository>();

        var trusted = new System.Security.Cryptography.X509Certificates.X509Certificate2Collection(tslAnchors);
        // The certificate the partner used before a roll-over is accepted as well: it may still present it in TLS
        // until it starts using the new one (Odette OP08 2.5 F).
        foreach (var id in new[] { partner.TrustedCertificateId, partner.PreviousSecurityCertificateId }.OfType<int>().Distinct())
            trusted.Add(CertificateLoader.Load(await certificates.GetObjectAsync(id, cancellationToken)));

        var clientCertificate = settings.OftpClientCertificateId is { } clientId
            ? CertificateLoader.Load(await certificates.GetObjectAsync(clientId, cancellationToken))
            : null;

        return new OftpTlsOptions
        {
            Protocols = partner.Tls == SslProtocols.None ? SslProtocols.Tls12 | SslProtocols.Tls13 : partner.Tls,
            TrustedCertificates = trusted,
            VerificationOnlyCertificates = tslVerificationRoots,
            LocalCertificate = clientCertificate,
            Revocation = settings.RevocationPolicy,
        };
    }
}
