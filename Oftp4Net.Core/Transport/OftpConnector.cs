using System.Net.Security;
using System.Security.Authentication;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Oftp4Net.Core.Protocol;

namespace Oftp4Net.Core.Transport;

/// <summary>What happened while a connection was opened, e.g. to tell a failed connection test where it stopped.</summary>
public sealed class OftpConnectDiagnostics
{
    /// <summary>The TCP connection was established; a failure after it is one of TLS.</summary>
    public bool Connected { get; internal set; }

    /// <summary>The certificate the other side presented in TLS.</summary>
    public X509Certificate2? RemoteCertificate { get; internal set; }

    /// <summary>Why the certificate of the other side was refused.</summary>
    public string? CertificateProblem { get; internal set; }

    public SslProtocols? TlsProtocol { get; internal set; }
    public TlsCipherSuite? CipherSuite { get; internal set; }
}

/// <summary>
/// Opens outgoing OFTP connections over TCP, optionally secured with TLS (RFC 5024, section 8).
/// </summary>
public static class OftpConnector
{
    public static async Task<OftpTransport> ConnectAsync(string host, int port, OftpTlsOptions? tls,
        CancellationToken cancellationToken, OftpConnectDiagnostics? diagnostics = null)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, cancellationToken);
            diagnostics?.Connected = true;
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
                        {
                            var valid = OftpCertificateValidator.Validate(certificate, errors, tls.TrustedCertificates,
                                certificateRequired: true, tls.Revocation, tls.VerificationOnlyCertificates, out var problem);
                            if (diagnostics is not null)
                            {
                                diagnostics.RemoteCertificate = certificate is null
                                    ? null
                                    : X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                                diagnostics.CertificateProblem = problem;
                            }

                            return valid;
                        },
                    }, cancellationToken);
                    if (diagnostics is not null)
                    {
                        diagnostics.TlsProtocol = ssl.SslProtocol;
                        diagnostics.CipherSuite = ssl.NegotiatedCipherSuite;
                    }
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
