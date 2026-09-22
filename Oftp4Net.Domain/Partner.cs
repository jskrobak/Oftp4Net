using System.ComponentModel.DataAnnotations;
using System.Security.Authentication;

namespace Oftp4Net.Domain;

/// <summary>
/// A remote OFTP node. <see cref="BaseParty.Password"/> is the password the partner sends to us
/// and that we expect in its SSID.
/// </summary>
public class Partner: BaseParty
{   
    [Required]
    [StringLength(100)]
    public string Host { get; set; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; set; } = 6619;
    public bool UseTls { get; set; } = true;
    public SslProtocols Tls { get; set; } = SslProtocols.Tls12;

    /// <summary>
    /// Optional certificate trusted for the partner's TLS server certificate: either the partner's own
    /// (pinned) certificate or the CA that issued it. When empty, the system trust store is used.
    /// </summary>
    public int? TrustedCertificateId { get; set; }
    public Certificate? TrustedCertificate { get; set; }
}
