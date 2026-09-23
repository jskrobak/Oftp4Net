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

    /// <summary>
    /// Highest ODETTE-FTP release we offer the partner in SSIDLEV: 5 = OFTP 2.0, 4 = 1.4, 2 = 1.3, 1 = 1.2.
    /// The session runs at the lower of the two announced levels; file level security and secure authentication
    /// need level 5.
    /// </summary>
    [Range(1, 5)]
    public int ProtocolLevel { get; set; } = 5;

    /// <summary>
    /// Offers ODETTE-FTP buffer compression (SSIDCMPR): runs of equal octets are compressed in the data exchange
    /// buffers. Used only when the partner offers it too; buffers from the partner are always accepted compressed.
    /// </summary>
    public bool BufferCompression { get; set; }

    /// <summary>
    /// Offers restart of interrupted transfers (SSIDREST): an interrupted file continues at the last complete
    /// 1K block instead of being sent again. Used only when the partner offers it too.
    /// </summary>
    public bool Restart { get; set; }

    /// <summary>Compresses the content of sent files (SFIDCOMP, CMS CompressedData with zlib).</summary>
    public bool CompressFiles { get; set; }

    /// <summary>Signs the content of sent files with our file security certificate (SFIDSEC).</summary>
    public bool SignFiles { get; set; }

    /// <summary>Encrypts the content of sent files for <see cref="SecurityCertificate"/> (SFIDSEC).</summary>
    public bool EncryptFiles { get; set; }

    /// <summary>
    /// Requires secure authentication (SSIDAUTH): after the SSID exchange both sides prove that they hold the
    /// private key of the certificate the other one knows. Both sides have to be configured the same way.
    /// </summary>
    public bool SecureAuthentication { get; set; }

    /// <summary>Asks the partner to sign the End to End Response of our files (SFIDSIGN).</summary>
    public bool RequestSignedEndResponse { get; set; }

    /// <summary>
    /// Cipher suite used for signing, encryption and hashes exchanged with this partner (SFIDCIPH).
    /// Suites 01 (3DES) and 02 (AES-256) are supported by every OFTP2 node.
    /// </summary>
    [StringLength(2)]
    public string FileCipherSuite { get; set; } = "02";

    /// <summary>
    /// The partner's certificate (without private key): files are encrypted for it and signatures of the partner,
    /// including signed End to End Responses, are verified against it.
    /// </summary>
    public int? SecurityCertificateId { get; set; }
    public Certificate? SecurityCertificate { get; set; }

    /// <summary>
    /// The partner's certificate before the last replacement. Files and responses that were already on their way
    /// when the certificate was replaced may still be signed with it.
    /// </summary>
    public int? PreviousSecurityCertificateId { get; set; }
    public Certificate? PreviousSecurityCertificate { get; set; }

    /// <summary>Files received from the partner must be signed (SFIDSEC).</summary>
    public bool RequireSignedFiles { get; set; }

    /// <summary>Files received from the partner must be encrypted (SFIDSEC).</summary>
    public bool RequireEncryptedFiles { get; set; }

    /// <summary>Files received from the partner must be compressed (SFIDCOMP).</summary>
    public bool RequireCompressedFiles { get; set; }

    [StringLength(20)]
    public string? Duns { get; set; }

    [StringLength(200)]
    public string? CompanyName { get; set; }

    /// <summary>Street address, one line per line of the address.</summary>
    [StringLength(500)]
    public string? Address { get; set; }

    [StringLength(100)]
    public string? City { get; set; }

    [StringLength(20)]
    public string? ZipCode { get; set; }

    /// <summary>ISO 3166 two letter country code.</summary>
    [StringLength(2)]
    public string? Country { get; set; }

    /// <summary>Document of the partner describing its OFTP setup.</summary>
    [StringLength(500)]
    public string? InfoDocumentUrl { get; set; }

    public List<PartnerContact> Contacts { get; set; } = [];

    public List<PartnerSubStation> SubStations { get; set; } = [];

    /// <summary>Virtual file names the partner receives (files we send to it).</summary>
    public List<PartnerDsnPattern> InboundDsnPatterns { get; set; } = [];

    /// <summary>Virtual file names the partner sends to us.</summary>
    public List<PartnerDsnPattern> OutboundDsnPatterns { get; set; } = [];

    /// <summary>Identification (docid) of the last OFTP2 Communication Setup of the partner that was applied.</summary>
    public Guid? SetupDocumentId { get; set; }

    /// <summary>Creation date (docdate) of the last applied OFTP2 Communication Setup, older ones are refused.</summary>
    public DateTime? SetupDocumentDate { get; set; }

    /// <summary>When the last OFTP2 Communication Setup of the partner was applied.</summary>
    public DateTime? SetupAppliedDate { get; set; }
}
