using System.Security.Authentication;

namespace Oftp4Net.Core;

public static class OftpDefaults
{
    /// <summary>Well known port for OFTP over TLS (RFC 5024, section 8).</summary>
    public const int TlsPort = 6619;

    /// <summary>Well known port for OFTP over plain TCP.</summary>
    public const int PlainPort = 3305;

    /// <summary>
    /// TLS versions a partner or a listener can use. TLS 1.0 and 1.1 are obsolete and offered only for partners whose
    /// software knows nothing newer (HUB-Master of NUMLOG); the image allows them in OpenSSL, the defaults stay 1.2 and 1.3.
    /// </summary>
#pragma warning disable SYSLIB0039 // obsolete TLS versions, needed for old partner software
    public static readonly SslProtocols[] AllowedSslProtocols =
    [
        SslProtocols.Tls,
        SslProtocols.Tls11,
        SslProtocols.Tls12,
        SslProtocols.Tls13
    ];

    /// <summary>Versions that are obsolete and only kept for old partners.</summary>
    public const SslProtocols ObsoleteSslProtocols = SslProtocols.Tls | SslProtocols.Tls11;
#pragma warning restore SYSLIB0039

    /// <summary>The name of a TLS version as administrators know it.</summary>
    public static string Name(SslProtocols protocol) => protocol switch
    {
#pragma warning disable SYSLIB0039
        SslProtocols.Tls => "TLS 1.0 (obsolete)",
        SslProtocols.Tls11 => "TLS 1.1 (obsolete)",
#pragma warning restore SYSLIB0039
        SslProtocols.Tls12 => "TLS 1.2",
        SslProtocols.Tls13 => "TLS 1.3",
        _ => protocol.ToString(),
    };

    /// <summary>The versions of a combination, oldest first, e.g. "TLS 1.2, TLS 1.3".</summary>
    public static string Names(SslProtocols protocols) =>
        string.Join(", ", AllowedSslProtocols.Where(p => protocols.HasFlag(p)).Select(p => Name(p).Replace(" (obsolete)", "")));
}
