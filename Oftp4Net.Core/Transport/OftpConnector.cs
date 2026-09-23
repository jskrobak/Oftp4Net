using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Oftp4Net.Core.Protocol;

namespace Oftp4Net.Core.Transport;

/// <summary>
/// Opens outgoing OFTP connections over TCP, optionally secured with TLS (RFC 5024, section 8).
/// </summary>
public static class OftpConnector
{
    public static async Task<OftpTransport> ConnectAsync(string host, int port, OftpTlsOptions? tls,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, cancellationToken);
            Stream stream = new NetworkStream(client.Client, ownsSocket: true);

            if (tls is not null)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                try
                {
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = host,
                        EnabledSslProtocols = tls.Protocols,
                        ClientCertificates = tls.LocalCertificate is null ? null : [tls.LocalCertificate],
                        RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                            OftpCertificateValidator.Validate(certificate, errors, tls.TrustedCertificates,
                                certificateRequired: true, tls.Revocation, tls.VerificationOnlyCertificates),
                    }, cancellationToken);
                }
                catch
                {
                    await ssl.DisposeAsync();
                    throw;
                }

                stream = ssl;
            }

            return new OftpTransport(stream);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
