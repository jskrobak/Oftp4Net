using System.ComponentModel.DataAnnotations;
using System.Security.Authentication;

namespace Oftp4Net.Domain;

public class Listener
{
    public int Id { get; set; }
    public bool Enabled { get; set; } = false;
    
    [Required]
    [StringLength(50)]
    public string Name { get; set; } = string.Empty;

    public bool UseTls { get; set; } = true;
    
    public SslProtocols Tls { get; set; } = SslProtocols.Tls12 | SslProtocols.Tls13;

    [Required]
    [StringLength(50)]
    public string ListenIPAddress { get; set; } = "0.0.0.0";

    [Range(1, 65535)]
    public int Port { get; set; } = 6619;
    
    /// <summary>Server certificate (with private key) used for TLS.</summary>
    public int? CertificateId { get; set; }
    public Certificate? Certificate { get; set; }

    /// <summary>Identity presented to partners connecting to this listener (responder SSID).</summary>
    public int? IdentityId { get; set; }
    public Identity? Identity { get; set; }

    /// <summary>Require partners to authenticate with a TLS client certificate.</summary>
    public bool RequireClientCertificate { get; set; }
}
