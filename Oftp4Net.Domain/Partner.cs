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

    /// <summary>
    /// Encoding the content of a virtual file is converted to while it is sent to the partner. Files are stored
    /// locally in <see cref="AnsiCodePage"/>, so <see cref="FileCharacterEncoding.ANSI"/> sends them unchanged.
    /// </summary>
    public FileCharacterEncoding OutgoingEncoding { get; set; } = FileCharacterEncoding.ANSI;

    /// <summary>
    /// Converts the content of files received from the partner from <see cref="EbcdicCodePage"/>
    /// to <see cref="AnsiCodePage"/>. Set it for partners that send EBCDIC.
    /// </summary>
    public bool ConvertIncomingEbcdicToAnsi { get; set; }

    /// <summary>Code page of the ANSI side of the conversion, i.e. of the files as stored locally.</summary>
    [Range(1, 65535)]
    public int AnsiCodePage { get; set; } = 1252;

    /// <summary>EBCDIC code page of the partner.</summary>
    [Range(1, 65535)]
    public int EbcdicCodePage { get; set; } = 500;
}
