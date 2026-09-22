using System.Security.Authentication;

namespace Oftp4Net.Core;

public static class OftpDefaults
{
    /// <summary>Well known port for OFTP over TLS (RFC 5024, section 8).</summary>
    public const int TlsPort = 6619;

    /// <summary>Well known port for OFTP over plain TCP.</summary>
    public const int PlainPort = 3305;

    public static readonly SslProtocols[] AllowedSslProtocols =
    [
        SslProtocols.Tls12,
        SslProtocols.Tls13
    ];
}
