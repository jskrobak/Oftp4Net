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

namespace Oftp4Net.Services;

public sealed record ListenerStatus(int ListenerId, string Name, string EndPoint, bool Running, string? Error, int ActiveSessions);

/// <summary>
/// Runs an <see cref="OftpListener"/> for every enabled <see cref="Listener"/> and answers incoming OFTP sessions.
/// </summary>
public class ListenerService(
    ILogger<ListenerService> logger,
    IServiceScopeFactory serviceScopeFactory,
    GlobalSettingsService globalSettingsService) : IHostedService, IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly List<OftpListener> _listeners = [];
    private readonly ConcurrentDictionary<int, ListenerStatus> _status = new();
    private CancellationTokenSource _stopping = new();
    private int _disposed;

    public IReadOnlyCollection<ListenerStatus> Status => _status.Values.OrderBy(s => s.Name).ToList();

    public event Action? StatusChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
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

    /// <summary>Stops all listeners and starts the enabled ones again, e.g. after their configuration changed.</summary>
    public async Task ReloadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await StopListenersAsync();

            List<Listener> configured;
            using (var scope = serviceScopeFactory.CreateScope())
            {
                configured = await scope.ServiceProvider.GetRequiredService<IListenerRepository>().GetEnabledWithRefsAsync();
            }

            foreach (var listener in configured)
                StartListener(listener);
        }
        finally
        {
            _lock.Release();
            StatusChanged?.Invoke();
        }
    }

    private void StartListener(Listener listener)
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
                };
            }

            var oftpListener = new OftpListener(new IPEndPoint(address, listener.Port), tls,
                (transport, connection, ct) => HandleConnectionAsync(listener, transport, connection, ct), logger);
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

    private async Task HandleConnectionAsync(Listener listener, Core.Protocol.OftpTransport transport,
        OftpConnectionInfo connection, CancellationToken cancellationToken)
    {
        UpdateActiveSessions(listener.Id, +1);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
            var settings = await globalSettingsService.GetGlobalSettingsAsync();

            using var scope = serviceScopeFactory.CreateScope();
            using var handler = PartnerSessionHandler.ForResponder(scope.ServiceProvider, settings, logger, listener);

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
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Session with {Partner} ({RemoteEndPoint}) failed",
                    handler.Partner?.Name ?? "unknown partner", connection.RemoteEndPoint);
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
