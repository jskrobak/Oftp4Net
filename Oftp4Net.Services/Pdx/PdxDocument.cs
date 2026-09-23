using Oftp4Net.Domain;

namespace Oftp4Net.Services.Pdx;

/// <summary>
/// OFTP2 Communication Setup (PDX, "Partner Details Exchange via XML", Odette OP08 part 3): the datasheet of an
/// OFTP2 station with everything needed to set up a connection to it. Inbound and outbound are seen from the
/// station that issued the datasheet.
/// </summary>
public sealed record PdxDocument
{
    /// <summary>Schema version, e.g. "1.2".</summary>
    public required string Version { get; init; }

    /// <summary>Recreated with every change of the document.</summary>
    public required Guid DocId { get; init; }

    public required DateTimeOffset DocDate { get; init; }

    /// <summary>From when the described configuration is active on the issuing station.</summary>
    public required DateTimeOffset ValidFrom { get; init; }

    public required PdxStationInformation Station { get; init; }

    /// <summary>How the station can be called.</summary>
    public IReadOnlyList<PdxInboundConnection> InboundConnections { get; init; } = [];

    /// <summary>How the station calls its partners.</summary>
    public IReadOnlyList<PdxOutboundConnection> OutboundConnections { get; init; } = [];

    public PdxCipherSetting? CipherSetting { get; init; }

    public required PdxSessionSettings Session { get; init; }

    /// <summary>Settings of files the station sends.</summary>
    public PdxFileSettings? OutboundFileSettings { get; init; }

    /// <summary>Settings of files the station receives.</summary>
    public PdxFileSettings? InboundFileSettings { get; init; }

    /// <summary>Virtual file names the station sends.</summary>
    public IReadOnlyList<PdxDsnPattern>? OutboundDsnList { get; init; }

    /// <summary>Virtual file names the station receives.</summary>
    public IReadOnlyList<PdxDsnPattern>? InboundDsnList { get; init; }

    public IReadOnlyList<PdxSubStation> SubStations { get; init; } = [];

    public IReadOnlyList<PdxCertificate> Certificates { get; init; } = [];

    public PdxCertificate? FindCertificate(string? name) =>
        name is null ? null : Certificates.FirstOrDefault(c => c.Name == name);
}

public sealed record PdxStationInformation
{
    public string? Duns { get; init; }
    public required string Name { get; init; }
    public required PdxCompanyInfo Company { get; init; }

    /// <summary>Odette identification of the issuer, for information only.</summary>
    public required string IssuerIdCode { get; init; }

    public string? InfoDocumentUrl { get; init; }

    /// <summary><c>null</c> when the element is missing (the contacts stay as they are).</summary>
    public IReadOnlyList<PdxContact>? Contacts { get; init; }
}

public sealed record PdxCompanyInfo
{
    public required string Name { get; init; }
    public IReadOnlyList<string> AddressLines { get; init; } = [];
    public string? City { get; init; }
    public string? ZipCode { get; init; }
    public string? Country { get; init; }
}

public sealed record PdxContact
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Phones { get; init; } = [];
    public IReadOnlyList<string> Emails { get; init; } = [];
    public string? Url { get; init; }
}

/// <summary>Plain TCP or TLS.</summary>
public enum PdxConnectionType
{
    Tcp,
    Tls,
}

public sealed record PdxInboundConnection
{
    public required PdxConnectionType Type { get; init; }
    public IReadOnlyList<string> TlsVersions { get; init; } = [];
    public required string ServerHost { get; init; }
    public required int ServerPort { get; init; }

    /// <summary>The TLS server certificate of the station.</summary>
    public string? CertificateRef { get; init; }

    /// <summary>Whether the station asks callers for a TLS client certificate.</summary>
    public SecurityUsage? TlsClientAuth { get; init; }
}

public sealed record PdxOutboundConnection
{
    public required PdxConnectionType Type { get; init; }
    public IReadOnlyList<string> TlsVersions { get; init; } = [];

    /// <summary>Addresses the station calls from, e.g. for firewall rules.</summary>
    public IReadOnlyList<string> OutboundIps { get; init; } = [];

    public PdxSupport? TlsClientAuth { get; init; }
}

public sealed record PdxCipherSetting(string PrimaryCipher, IReadOnlyList<string> AlternateCiphers)
{
    /// <summary>The primary cipher followed by the alternates.</summary>
    public IEnumerable<string> All => AlternateCiphers.Prepend(PrimaryCipher);
}

public sealed record PdxSessionSettings
{
    /// <summary>Odette identification used in SSID (SSIDCODE).</summary>
    public required string Ssid { get; init; }

    public int? MinimumProtocolReleaseLevel { get; init; }
    public int? MaximumProtocolReleaseLevel { get; init; }

    /// <summary>Password the station sends in its SSID.</summary>
    public string? Password { get; init; }

    public PdxSupport? SecureAuthentication { get; init; }
}

/// <summary>Usage of a security feature together with the certificates used for it.</summary>
public sealed record PdxSupport(SecurityUsage Usage, IReadOnlyList<string> CertificateRefs)
{
    public PdxSupport(SecurityUsage usage) : this(usage, []) { }
}

public sealed record PdxFileSettings
{
    public required PdxSupport FileSignature { get; init; }
    public required PdxSupport FileEncryption { get; init; }
    public required PdxSupport FileCompression { get; init; }
    public required PdxSupport EerpSignature { get; init; }
}

public sealed record PdxDsnPattern
{
    public required string Pattern { get; init; }
    public required string FileFormat { get; init; }
    public int? MaximumRecordSize { get; init; }
    public required string DescriptionShort { get; init; }
    public string? DescriptionLong { get; init; }
}

public sealed record PdxSubStation
{
    public required string Name { get; init; }
    public required string Sfid { get; init; }
    public PdxFileSettings? OutboundFileSettings { get; init; }
    public PdxFileSettings? InboundFileSettings { get; init; }
    public IReadOnlyList<PdxDsnPattern>? OutboundDsnList { get; init; }
    public IReadOnlyList<PdxDsnPattern>? InboundDsnList { get; init; }
    public PdxCipherSetting? CipherSetting { get; init; }
    public IReadOnlyList<PdxContact>? Contacts { get; init; }
}

public sealed record PdxCertificate
{
    public required string Name { get; init; }
    public required string SubjectName { get; init; }

    /// <summary>DER encoded certificate.</summary>
    public required byte[] Certificate { get; init; }

    /// <summary>DER encoded certificates of the issuing CAs (partial or complete chain).</summary>
    public IReadOnlyList<byte[]> CaCertificates { get; init; } = [];
}
