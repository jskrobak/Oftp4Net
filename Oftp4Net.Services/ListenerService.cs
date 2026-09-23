using System.Collections.Concurrent;
using System.Net;
using System.Security.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Oftp4Net.Core.Session;
using Oftp4Net.Core.Transport;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;
using Oftp4Net.Services.Oftp;
using Oftp4Net.Services.TransferEvents;

using Oftp4Net.Services.Tsl;

namespace Oftp4Net.Services;

public sealed record ListenerStatus(int ListenerId, string Name, string EndPoint, bool Running, string? Error, int ActiveSessions);

/// <summary>
/// Runs an <see cref="OftpListener"/> for every enabled <see cref="Listener"/> and answers incoming OFTP sessions.
/// </summary>
public class ListenerService(
    ILogger<ListenerService> logger,
    IServiceScopeFactory serviceScopeFactory,
    GlobalSettingsService globalSettingsService,
    TslService tsl,
    ITransferEventLog transferEvents) : IHostedService, IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly List<OftpListener> _listeners = [];
    private readonly List<OftpTlsOptions> _tlsOptions = [];
    private readonly ConcurrentDictionary<int, ListenerStatus> _status = new();
    private CancellationTokenSource _stopping = new();
    private int _disposed;

    public IReadOnlyCollection<ListenerStatus> Status => _status.Values.OrderBy(s => s.Name).ToList();

    public event Action? StatusChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        tsl.Changed += OnTrustListChanged;
        try
        {
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            // Do not prevent the web UI from starting, e.g. when the database is not migrated yet.
            logger.LogError(ex, "Starting OFTP listeners failed");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        tsl.Changed -= OnTrustListChanged;
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await StopListenersAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Another trust list is in use: the listeners trust its certification authorities from now on.</summary>
    private async void OnTrustListChanged()
    {
        try
        {
            await RefreshTrustedCertificatesAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reloading the trusted certificates of the listeners failed");
        }
    }

    /// <summary>
    /// Reloads the certificates trusted for TLS client authentication (after partners changed) without restarting
    /// the listeners, so running transfers are not interrupted.
    /// </summary>
    public async Task RefreshTrustedCertificatesAsync()
    {
        List<Certificate> partnerCertificates;
        using (var scope = serviceScopeFactory.CreateScope())
        {
            partnerCertificates = await scope.ServiceProvider.GetRequiredService<IPartnerRepository>().GetTrustedCertificatesAsync();
        }

        await _lock.WaitAsync();
        try
        {
            foreach (var tls in _tlsOptions)
            {
                tls.TrustedCertificates = LoadTrustedCertificates(partnerCertificates);
                tls.VerificationOnlyCertificates = tsl.VerificationRoots;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Stops all listeners and starts the enabled ones again, e.g. after their configuration changed.</summary>
    public async Task ReloadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await StopListenersAsync();

            List<Listener> configured;
            List<Certificate> partnerCertificates;
            using (var scope = serviceScopeFactory.CreateScope())
            {
                configured = await scope.ServiceProvider.GetRequiredService<IListenerRepository>().GetEnabledWithRefsAsync();
                partnerCertificates = await scope.ServiceProvider.GetRequiredService<IPartnerRepository>().GetTrustedCertificatesAsync();
            }

            var revocation = (await globalSettingsService.GetGlobalSettingsAsync()).RevocationPolicy;
            foreach (var listener in configured)
                StartListener(listener, partnerCertificates, revocation);
        }
        finally
        {
            _lock.Release();
            StatusChanged?.Invoke();
        }
    }

    /// <param name="partnerCertificates">
    /// Trusted certificates of all partners; a TLS client certificate is accepted when it is one of them or issued by one
    /// of them (the partner is only known after SSID, so the TLS layer accepts any configured partner).
    /// </param>
    private void StartListener(Listener listener, IReadOnlyList<Certificate> partnerCertificates,
        CertificateRevocationPolicy revocation)
    {
        var endPoint = $"{listener.ListenIPAddress}:{listener.Port}";
        try
        {
            if (!IPAddress.TryParse(listener.ListenIPAddress, out var address))
                throw new InvalidOperationException($"Invalid IP address '{listener.ListenIPAddress}'.");
            if (listener.Identity is null)
                throw new InvalidOperationException("No identity is assigned to the listener.");

            OftpTlsOptions? tls = null;
            if (listener.UseTls)
            {
                if (listener.Certificate is not { HasPrivateKey: true })
                    throw new InvalidOperationException("TLS requires a server certificate with a private key.");

                tls = new OftpTlsOptions
                {
                    LocalCertificate = CertificateLoader.Load(listener.Certificate),
                    Protocols = listener.Tls == SslProtocols.None ? SslProtocols.Tls12 | SslProtocols.Tls13 : listener.Tls,
                    RequireClientCertificate = listener.RequireClientCertificate,
                    TrustedCertificates = LoadTrustedCertificates(partnerCertificates),
                    // The roots the trust list carries only to verify its authorities must not issue a partner's
                    // certificate themselves (Odette OP08 2.7).
                    VerificationOnlyCertificates = tsl.VerificationRoots,
                    Revocation = revocation,
                };
                _tlsOptions.Add(tls);
            }

            var oftpListener = new OftpListener(new IPEndPoint(address, listener.Port), tls,
                (transport, connection, ct) => HandleConnectionAsync(listener, transport, connection, ct), logger);
            oftpListener.ConnectionFailed += (remote, exception) => transferEvents.Record(new TransferEvent
            {
                Category = TransferEventCategory.Incoming,
                Type = TransferEventType.SessionFailed,
                Level = TransferEventLevel.Warning,
                RemoteEndPoint = remote.ToString(),
                Message = $"Connection from {remote} to listener {listener.Name} failed before the OFTP session: {exception.Message}",
                Details = exception.ToString(),
            });
            oftpListener.Start();
            _listeners.Add(oftpListener);
            _status[listener.Id] = new ListenerStatus(listener.Id, listener.Name, endPoint, true, null, 0);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Cannot start OFTP listener {Listener} on {EndPoint}", listener.Name, endPoint);
            _status[listener.Id] = new ListenerStatus(listener.Id, listener.Name, endPoint, false, ex.Message, 0);
        }
    }

    private System.Security.Cryptography.X509Certificates.X509Certificate2Collection LoadTrustedCertificates(
        IReadOnlyList<Certificate> certificates)
    {
        // Certificates issued by a certification authority of the Odette TSL are accepted from any partner.
        var collection = new System.Security.Cryptography.X509Certificates.X509Certificate2Collection(tsl.TrustAnchors);
        foreach (var certificate in certificates)
        {
            try
            {
                collection.Add(CertificateLoader.Load(certificate));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cannot load trusted certificate {Name}", certificate.Name);
            }
        }

        return collection;
    }

    private async Task HandleConnectionAsync(Listener listener, Core.Protocol.OftpTransport transport,
        OftpConnectionInfo connection, CancellationToken cancellationToken)
    {
        UpdateActiveSessions(listener.Id, +1);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
            var settings = await globalSettingsService.GetGlobalSettingsAsync();

            using var scope = serviceScopeFactory.CreateScope();
            using var handler = PartnerSessionHandler.ForResponder(scope.ServiceProvider, settings, logger, listener,
                connection.RemoteEndPoint.ToString() ?? "");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var session = new OftpSession(transport, new OftpSessionOptions
            {
                Role = OftpRole.Responder,
                ExchangeBufferSize = settings.ExchangeBufferSize,
                Credit = settings.Credit,
                ResponseTimeout = TimeSpan.FromSeconds(settings.ResponseTimeoutSeconds),
            }, handler, logger);

            try
            {
                await session.RunAsync(linked.Token);
                logger.LogInformation("Session with {Partner} ({RemoteEndPoint}) finished: {Received} file(s) received, {Sent} file(s) sent",
                    handler.Partner?.Name, connection.RemoteEndPoint, handler.FilesReceived, handler.FilesSent);
                handler.RecordSessionEnd(null, stopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Session with {Partner} ({RemoteEndPoint}) failed",
                    handler.Partner?.Name ?? "unknown partner", connection.RemoteEndPoint);
                handler.RecordSessionEnd(ex, stopwatch.Elapsed);
                await handler.OnSessionFailedAsync(ex, includeUnattempted: false, CancellationToken.None);
            }
        }
        finally
        {
            UpdateActiveSessions(listener.Id, -1);
        }
    }

    private void UpdateActiveSessions(int listenerId, int delta)
    {
        // The status is removed while listeners are being stopped; sessions finishing then are not counted.
        if (_status.TryGetValue(listenerId, out var status))
            _status.TryUpdate(listenerId, status with { ActiveSessions = Math.Max(0, status.ActiveSessions + delta) }, status);
        StatusChanged?.Invoke();
    }

    private async Task StopListenersAsync()
    {
        await _stopping.CancelAsync();
        foreach (var listener in _listeners)
            await listener.DisposeAsync();
        _listeners.Clear();
        _tlsOptions.Clear();
        _status.Clear();
        _stopping.Dispose();
        _stopping = new CancellationTokenSource();
    }

    public async ValueTask DisposeAsync()
    {
        // The instance is registered both as a singleton and as a hosted service, so the container disposes it twice.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await StopAsync(CancellationToken.None);
        _lock.Dispose();
    }
}
