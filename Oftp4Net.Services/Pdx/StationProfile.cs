using Oftp4Net.Core.Protocol;
using Oftp4Net.Domain;

namespace Oftp4Net.Services.Pdx;

/// <summary>
/// Our own security policy in the terms of the OFTP2 Communication Setup (PDX), the "own datasheet" of Odette OP08
/// part 3. A datasheet received from a partner is compared with it to get the settings of the connection.
/// Inbound means files we receive, outbound files we send.
/// </summary>
public class StationProfile
{
    /// <summary>Signatures of files we receive.</summary>
    public SecurityUsage InboundFileSignature { get; set; } = SecurityUsage.Optional;

    /// <summary>Encryption of files we receive.</summary>
    public SecurityUsage InboundFileEncryption { get; set; } = SecurityUsage.Optional;

    /// <summary>Compression of files we receive.</summary>
    public SecurityUsage InboundFileCompression { get; set; } = SecurityUsage.Optional;

    /// <summary>Signing the End to End Responses we send for received files.</summary>
    public SecurityUsage InboundEerpSignature { get; set; } = SecurityUsage.Optional;

    /// <summary>Signing files we send.</summary>
    public SecurityUsage OutboundFileSignature { get; set; } = SecurityUsage.Optional;

    /// <summary>Encrypting files we send.</summary>
    public SecurityUsage OutboundFileEncryption { get; set; } = SecurityUsage.Optional;

    /// <summary>Compressing files we send.</summary>
    public SecurityUsage OutboundFileCompression { get; set; } = SecurityUsage.Optional;

    /// <summary>Signed End to End Responses for files we send.</summary>
    public SecurityUsage OutboundEerpSignature { get; set; } = SecurityUsage.Optional;

    /// <summary>OFTP2 secure authentication of sessions.</summary>
    public SecurityUsage SecureAuthentication { get; set; } = SecurityUsage.Optional;

    /// <summary>TLS client certificate when we call the partner.</summary>
    public SecurityUsage TlsClientAuthentication { get; set; } = SecurityUsage.Optional;

    /// <summary>The cipher suite we prefer (SFIDCIPH).</summary>
    public string PrimaryCipherSuite { get; set; } = CipherSuites.Aes256Sha256;

    /// <summary>Other cipher suites we accept; empty means every suite supported by the platform.</summary>
    public List<string> AlternateCipherSuites { get; set; } = [];

    // What our own datasheet says about us (export); not used when a partner's datasheet is compared.

    /// <summary>Name of the company operating the station; the name of the identity when empty.</summary>
    public string? CompanyName { get; set; }

    public List<string> AddressLines { get; set; } = [];
    public string? City { get; set; }
    public string? ZipCode { get; set; }

    /// <summary>ISO 3166 two letter country code.</summary>
    public string? Country { get; set; }

    public string? Duns { get; set; }

    /// <summary>Document describing our OFTP setup for partners.</summary>
    public string? InfoDocumentUrl { get; set; }

    /// <summary>Contacts partners may turn to with problems of the connection.</summary>
    public List<PartnerContact> Contacts { get; set; } = [];

    /// <summary>The listener partners call; its port, TLS versions and server certificate are published.</summary>
    public int? ListenerId { get; set; }

    /// <summary>Host name or address partners call (the listener usually listens on all addresses).</summary>
    public string? PublicHost { get; set; }

    /// <summary>Port partners call when it differs from the listener's port (e.g. behind port forwarding).</summary>
    public int? PublicPort { get; set; }

    /// <summary>Addresses we call partners from, for their firewall rules.</summary>
    public List<string> OutboundIps { get; set; } = [];
}
