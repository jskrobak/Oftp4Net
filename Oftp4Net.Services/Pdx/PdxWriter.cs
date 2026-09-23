using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Oftp4Net.Domain;

namespace Oftp4Net.Services.Pdx;

/// <summary>Writes an OFTP2 Communication Setup (PDX) in the order of the schema, version 1.2.</summary>
public static class PdxWriter
{
    private static readonly XNamespace Ns = PdxParser.Namespace12;

    public static byte[] Write(PdxDocument document)
    {
        var root = new XElement(Ns + "OftpCommunicationSetup",
            new XAttribute(XNamespace.Xmlns + "ocs", Ns.NamespaceName),
            new XAttribute("version", "1.2"),
            new XAttribute("docid", document.DocId.ToString()),
            new XAttribute("docdate", Date(document.DocDate)),
            new XAttribute("validfrom", Date(document.ValidFrom)),
            Station(document.Station),
            document.InboundConnections.Count == 0
                ? null
                : new XElement(Ns + "InboundCommunicationSettings", document.InboundConnections.Select(Inbound)),
            document.OutboundConnections.Count == 0
                ? null
                : new XElement(Ns + "OutboundCommunicationSettings", document.OutboundConnections.Select(Outbound)),
            Cipher(document.CipherSetting),
            Session(document.Session),
            FileSettings("OutboundFileSettings", document.OutboundFileSettings),
            FileSettings("InboundFileSettings", document.InboundFileSettings),
            DsnList("OutboundDsnList", document.OutboundDsnList),
            DsnList("InboundDsnList", document.InboundDsnList),
            document.SubStations.Count == 0 ? null : new XElement(Ns + "SubStations", document.SubStations.Select(SubStation)),
            document.Certificates.Count == 0 ? null : new XElement(Ns + "Certificates", document.Certificates.Select(Certificate)));

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
            new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(writer);
        return stream.ToArray();
    }

    /// <summary>The schema allows seconds with optional milliseconds and a time zone.</summary>
    private static string Date(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) +
        (value.Offset == TimeSpan.Zero ? "Z" : value.ToString("zzz", CultureInfo.InvariantCulture));

    private static XElement Station(PdxStationInformation station) =>
        new(Ns + "StationInformation",
            Optional("DUNS", station.Duns),
            new XElement(Ns + "Name", station.Name),
            new XElement(Ns + "CompanyInfo",
                new XElement(Ns + "Name", station.Company.Name),
                station.Company.AddressLines.Select(l => new XElement(Ns + "AddressLine", l)),
                Optional("City", station.Company.City),
                Optional("ZIPCode", station.Company.ZipCode),
                Optional("Country", station.Company.Country)),
            new XElement(Ns + "IssuerIDCode", station.IssuerIdCode),
            Optional("InfoDocumentURL", station.InfoDocumentUrl),
            Contacts(station.Contacts));

    private static XElement Inbound(PdxInboundConnection connection) =>
        new(Ns + "InboundConnectionSettings",
            new XAttribute("type", connection.Type == PdxConnectionType.Tls ? "TLS" : "TCP"),
            connection.TlsVersions.Select(v => new XElement(Ns + "TLSVersion", v)),
            new XElement(Ns + "ServerHost", connection.ServerHost),
            new XElement(Ns + "ServerPort", connection.ServerPort.ToString(CultureInfo.InvariantCulture)),
            Optional("CertificateRef", connection.CertificateRef),
            connection.TlsClientAuth is { } usage ? new XElement(Ns + "TLSClientAuth", Usage(usage)) : null);

    private static XElement Outbound(PdxOutboundConnection connection) =>
        new(Ns + "OutboundConnectionSettings",
            new XAttribute("type", connection.Type == PdxConnectionType.Tls ? "TLS" : "TCP"),
            connection.TlsVersions.Select(v => new XElement(Ns + "TLSVersion", v)),
            connection.OutboundIps.Select(ip => new XElement(Ns + "OutboundIP", ip)),
            Support("TLSClientAuth", connection.TlsClientAuth));

    private static XElement? Cipher(PdxCipherSetting? cipher) => cipher is null
        ? null
        : new XElement(Ns + "CipherSetting",
            new XElement(Ns + "PrimaryCipher", cipher.PrimaryCipher),
            cipher.AlternateCiphers.Select(c => new XElement(Ns + "AlternateCipher", c)));

    private static XElement Session(PdxSessionSettings session) =>
        new(Ns + "SessionSettings",
            new XElement(Ns + "OdetteIDCodeSSID", session.Ssid),
            Optional("MinimumProtocolReleaseLevel", session.MinimumProtocolReleaseLevel?.ToString(CultureInfo.InvariantCulture)),
            Optional("MaximumProtocolReleaseLevel", session.MaximumProtocolReleaseLevel?.ToString(CultureInfo.InvariantCulture)),
            session.Password is null ? null : new XElement(Ns + "Password", session.Password),
            Support("SecureAuthentication", session.SecureAuthentication));

    private static XElement? FileSettings(string name, PdxFileSettings? settings) => settings is null
        ? null
        : new XElement(Ns + name,
            Support("FileSignature", settings.FileSignature),
            Support("FileEncryption", settings.FileEncryption),
            Support("FileCompression", settings.FileCompression),
            Support("EERPSignature", settings.EerpSignature));

    private static XElement? DsnList(string name, IReadOnlyList<PdxDsnPattern>? list) => list is null or { Count: 0 }
        ? null
        : new XElement(Ns + name, list.Select(d => new XElement(Ns + "dsn",
            new XElement(Ns + "pattern", d.Pattern),
            new XElement(Ns + "fileFormat", d.FileFormat),
            Optional("maximumRecordSize", d.MaximumRecordSize?.ToString(CultureInfo.InvariantCulture)),
            new XElement(Ns + "descriptionShort", d.DescriptionShort),
            Optional("descriptionLong", d.DescriptionLong))));

    private static XElement SubStation(PdxSubStation sub) =>
        new(Ns + "SubStationInformation",
            new XElement(Ns + "Name", sub.Name),
            new XElement(Ns + "OdetteIDCodeSFID", sub.Sfid),
            FileSettings("OutboundFileSettings", sub.OutboundFileSettings),
            FileSettings("InboundFileSettings", sub.InboundFileSettings),
            DsnList("OutboundDsnList", sub.OutboundDsnList),
            DsnList("InboundDsnList", sub.InboundDsnList),
            Cipher(sub.CipherSetting),
            Contacts(sub.Contacts));

    private static XElement? Contacts(IReadOnlyList<PdxContact>? contacts) => contacts is null or { Count: 0 }
        ? null
        : new XElement(Ns + "Contacts", contacts.Select(c => new XElement(Ns + "Contact",
            new XElement(Ns + "Name", c.Name),
            Optional("Description", c.Description),
            c.Phones.Select(p => new XElement(Ns + "Phone", p)),
            c.Emails.Select(e => new XElement(Ns + "Email", e)),
            Optional("URL", c.Url))));

    private static XElement Certificate(PdxCertificate certificate) =>
        new(Ns + "Certificate",
            new XAttribute("name", certificate.Name),
            new XElement(Ns + "SubjectName", certificate.SubjectName),
            new XElement(Ns + "X509Certificate", Convert.ToBase64String(certificate.Certificate)),
            certificate.CaCertificates.Select(c => new XElement(Ns + "X509CACertificate", Convert.ToBase64String(c))));

    private static XElement? Support(string name, PdxSupport? support) => support is null
        ? null
        : new XElement(Ns + name, Usage(support.Usage), support.CertificateRefs.Select(r => new XElement(Ns + "CertificateRef", r)));

    private static XAttribute Usage(SecurityUsage usage) => new("usage", usage.ToString().ToLowerInvariant());

    private static XElement? Optional(string name, string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new XElement(Ns + name, value);
}
