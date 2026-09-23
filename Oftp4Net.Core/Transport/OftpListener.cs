using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Oftp4Net.Core.Protocol;

namespace Oftp4Net.Core.Transport;

public sealed class OftpConnectionInfo
{
    public required EndPoint RemoteEndPoint { get; init; }
    public X509Certificate2? RemoteCertificate { get; init; }
}

/// <summary>
/// Accepts incoming OFTP connections over TCP, optionally secured with TLS, and hands each of them to a callback.
/// </summary>
public sealed class OftpListener : IAsyncDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    private readonly TcpListener _listener;
    private readonly OftpTlsOptions? _tls;
    private readonly Func<OftpTransport, OftpConnectionInfo, CancellationToken, Task> _onConnection;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Task, bool> _connections = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task? _acceptLoop;

    public OftpListener(IPEndPoint endPoint, OftpTlsOptions? tls,
        Func<OftpTransport, OftpConnectionInfo, CancellationToken, Task> onConnection, ILogger logger)
    {
        if (tls is { LocalCertificate: null })
            throw new ArgumentException("A server certificate is required for TLS.", nameof(tls));

        _listener = new TcpListener(endPoint);
        _tls = tls;
        _onConnection = onConnection;
        _logger = logger;
    }

    /// <summary>
    /// Raised when an accepted connection fails before it is handed over, e.g. when the TLS handshake fails.
    /// </summary>
    public event Action<EndPoint, Exception>? ConnectionFailed;

    /// <summary>The local end point, useful when listening on port 0.</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndpoint;

    public void Start()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_stopping.Token);
        _logger.LogInformation("OFTP listener started on {EndPoint} ({Security})", LocalEndPoint, _tls is null ? "plain TCP" : "TLS");
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptSocketAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "Accepting OFTP connection failed");
                continue;
            }

            var task = HandleConnectionAsync(socket, cancellationToken);
            _connections.TryAdd(task, true);
            _ = task.ContinueWith(t => _connections.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    private async Task HandleConnectionAsync(Socket socket, CancellationToken cancellationToken)
    {
        await Task.Yield();
        socket.NoDelay = true;
        var remote = socket.RemoteEndPoint!;
        _logger.LogInformation("OFTP connection accepted from {RemoteEndPoint}", remote);

        OftpTransport? transport = null;
        var handedOver = false;
        try
        {
            Stream stream = new NetworkStream(socket, ownsSocket: true);
            X509Certificate2? remoteCertificate = null;

            if (_tls is not null)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                stream = ssl;

                using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                handshakeTimeout.CancelAfter(HandshakeTimeout);

                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _tls.LocalCertificate,
                    EnabledSslProtocols = _tls.Protocols,
                    ClientCertificateRequired = _tls.RequireClientCertificate,
                    RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                        OftpCertificateValidator.Validate(certificate, errors, _tls.TrustedCertificates,
                            _tls.RequireClientCertificate, _tls.Revocation),
                }, handshakeTimeout.Token);

                remoteCertificate = ssl.RemoteCertificate as X509Certificate2;
            }

            transport = new OftpTransport(stream);
            handedOver = true;
            await _onConnection(transport, new OftpConnectionInfo
            {
                RemoteEndPoint = remote,
                RemoteCertificate = remoteCertificate
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OFTP connection from {RemoteEndPoint} failed", remote);
            if (!handedOver)
                ConnectionFailed?.Invoke(remote, ex);
        }
        finally
        {
            if (transport is not null)
                await transport.DisposeAsync();
            else
                socket.Dispose();

            _logger.LogInformation("OFTP connection from {RemoteEndPoint} closed", remote);
        }
    }

    public async Task StopAsync()
    {
        if (_stopping.IsCancellationRequested)
            return;

        await _stopping.CancelAsync();
        _listener.Stop();

        if (_acceptLoop is not null)
            await _acceptLoop;

        await Task.WhenAll(_connections.Keys);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _listener.Dispose();
        _stopping.Dispose();
    }
}
