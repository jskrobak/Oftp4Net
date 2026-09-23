using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Oftp4Net.Domain;

namespace Oftp4Net.Services.Pdx;

/// <summary>Writes an OFTP2 Communication Setup (PDX) in the order of the schema, version 1.2 or 1.1.</summary>
public static class PdxWriter
{
    /// <param name="version">"1.2", or "1.1" for software that does not know 1.2 (cipher suite 07 is left out).</param>
    public static byte[] Write(PdxDocument document, string version = "1.2")
    {
        if (!PdxParser.Versions.Contains(version))
            throw new ArgumentException($"Version {version} of the OFTP2 Communication Setup is not supported.", nameof(version));

        var writer = new Writer(PdxParser.NamespaceOf(version), version);
        return writer.Write(document);
    }

    private sealed class Writer(XNamespace ns, string version)
    {
        private readonly string[] _unsupportedCiphers = version == "1.1" ? ["07"] : [];

        public byte[] Write(PdxDocument document)
        {
            var root = new XElement(ns + "OftpCommunicationSetup",
                new XAttribute(XNamespace.Xmlns + "ocs", ns.NamespaceName),
                new XAttribute("version", version),
                new XAttribute("docid", document.DocId.ToString()),
                new XAttribute("docdate", Date(document.DocDate)),
                new XAttribute("validfrom", Date(document.ValidFrom)),
                Station(document.Station),
                document.InboundConnections.Count == 0
                    ? null
                    : new XElement(ns + "InboundCommunicationSettings", document.InboundConnections.Select(Inbound)),
                document.OutboundConnections.Count == 0
                    ? null
                    : new XElement(ns + "OutboundCommunicationSettings", document.OutboundConnections.Select(Outbound)),
                Cipher(document.CipherSetting),
                Session(document.Session),
                FileSettings("OutboundFileSettings", document.OutboundFileSettings),
                FileSettings("InboundFileSettings", document.InboundFileSettings),
                DsnList("OutboundDsnList", document.OutboundDsnList),
                DsnList("InboundDsnList", document.InboundDsnList),
                document.SubStations.Count == 0 ? null : new XElement(ns + "SubStations", document.SubStations.Select(SubStation)),
                document.Certificates.Count == 0 ? null : new XElement(ns + "Certificates", document.Certificates.Select(Certificate)));

            using var stream = new MemoryStream();
            using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
                new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(writer);
            return stream.ToArray();
        }

        /// <summary>
        /// Always in UTC written as +00:00: the schema allows any time zone, but OS4X (2025) cannot read "Z" and takes
        /// the local time of another offset such as +02:00 for UTC, so it would activate the datasheet hours late.
        /// </summary>
        private static string Date(DateTimeOffset value) =>
            value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture);

        private XElement Station(PdxStationInformation station) =>
            new(ns + "StationInformation",
                Optional("DUNS", station.Duns),
                new XElement(ns + "Name", station.Name),
                new XElement(ns + "CompanyInfo",
                    new XElement(ns + "Name", station.Company.Name),
                    station.Company.AddressLines.Select(l => new XElement(ns + "AddressLine", l)),
                    Optional("City", station.Company.City),
                    Optional("ZIPCode", station.Company.ZipCode),
                    Optional("Country", station.Company.Country)),
                new XElement(ns + "IssuerIDCode", station.IssuerIdCode),
                Optional("InfoDocumentURL", station.InfoDocumentUrl),
                Contacts(station.Contacts));

        private XElement Inbound(PdxInboundConnection connection) =>
            new(ns + "InboundConnectionSettings",
                new XAttribute("type", connection.Type == PdxConnectionType.Tls ? "TLS" : "TCP"),
                connection.TlsVersions.Select(v => new XElement(ns + "TLSVersion", v)),
                new XElement(ns + "ServerHost", connection.ServerHost),
                new XElement(ns + "ServerPort", connection.ServerPort.ToString(CultureInfo.InvariantCulture)),
                Optional("CertificateRef", connection.CertificateRef),
                connection.TlsClientAuth is { } usage ? new XElement(ns + "TLSClientAuth", Usage(usage)) : null);

        private XElement Outbound(PdxOutboundConnection connection) =>
            new(ns + "OutboundConnectionSettings",
                new XAttribute("type", connection.Type == PdxConnectionType.Tls ? "TLS" : "TCP"),
                connection.TlsVersions.Select(v => new XElement(ns + "TLSVersion", v)),
                connection.OutboundIps.Select(ip => new XElement(ns + "OutboundIP", ip)),
                Support("TLSClientAuth", connection.TlsClientAuth));

        private XElement? Cipher(PdxCipherSetting? cipher)
        {
            var ciphers = cipher?.All.Where(c => !_unsupportedCiphers.Contains(c)).ToList();
            return ciphers is null or { Count: 0 }
                ? null
                : new XElement(ns + "CipherSetting",
                    new XElement(ns + "PrimaryCipher", ciphers[0]),
                    ciphers.Skip(1).Select(c => new XElement(ns + "AlternateCipher", c)));
        }

        private XElement Session(PdxSessionSettings session) =>
            new(ns + "SessionSettings",
                new XElement(ns + "OdetteIDCodeSSID", session.Ssid),
                Optional("MinimumProtocolReleaseLevel", session.MinimumProtocolReleaseLevel?.ToString(CultureInfo.InvariantCulture)),
                Optional("MaximumProtocolReleaseLevel", session.MaximumProtocolReleaseLevel?.ToString(CultureInfo.InvariantCulture)),
                session.Password is null ? null : new XElement(ns + "Password", session.Password),
                Support("SecureAuthentication", session.SecureAuthentication));

        private XElement? FileSettings(string name, PdxFileSettings? settings) => settings is null
            ? null
            : new XElement(ns + name,
                Support("FileSignature", settings.FileSignature),
                Support("FileEncryption", settings.FileEncryption),
                Support("FileCompression", settings.FileCompression),
                Support("EERPSignature", settings.EerpSignature));

        private XElement? DsnList(string name, IReadOnlyList<PdxDsnPattern>? list) => list is null or { Count: 0 }
            ? null
            : new XElement(ns + name, list.Select(d => new XElement(ns + "dsn",
                new XElement(ns + "pattern", d.Pattern),
                new XElement(ns + "fileFormat", d.FileFormat),
                Optional("maximumRecordSize", d.MaximumRecordSize?.ToString(CultureInfo.InvariantCulture)),
                new XElement(ns + "descriptionShort", d.DescriptionShort),
                Optional("descriptionLong", d.DescriptionLong))));

        private XElement SubStation(PdxSubStation sub) =>
            new(ns + "SubStationInformation",
                new XElement(ns + "Name", sub.Name),
                new XElement(ns + "OdetteIDCodeSFID", sub.Sfid),
                FileSettings("OutboundFileSettings", sub.OutboundFileSettings),
                FileSettings("InboundFileSettings", sub.InboundFileSettings),
                DsnList("OutboundDsnList", sub.OutboundDsnList),
                DsnList("InboundDsnList", sub.InboundDsnList),
                Cipher(sub.CipherSetting),
                Contacts(sub.Contacts));

        private XElement? Contacts(IReadOnlyList<PdxContact>? contacts) => contacts is null or { Count: 0 }
            ? null
            : new XElement(ns + "Contacts", contacts.Select(c => new XElement(ns + "Contact",
                new XElement(ns + "Name", c.Name),
                Optional("Description", c.Description),
                c.Phones.Select(p => new XElement(ns + "Phone", p)),
                c.Emails.Select(e => new XElement(ns + "Email", e)),
                Optional("URL", c.Url))));

        private XElement Certificate(PdxCertificate certificate) =>
            new(ns + "Certificate",
                new XAttribute("name", certificate.Name),
                new XElement(ns + "SubjectName", certificate.SubjectName),
                new XElement(ns + "X509Certificate", Convert.ToBase64String(certificate.Certificate)),
                certificate.CaCertificates.Select(c => new XElement(ns + "X509CACertificate", Convert.ToBase64String(c))));

        private XElement? Support(string name, PdxSupport? support) => support is null
            ? null
            : new XElement(ns + name, Usage(support.Usage), support.CertificateRefs.Select(r => new XElement(ns + "CertificateRef", r)));

        private static XAttribute Usage(SecurityUsage usage) => new("usage", usage.ToString().ToLowerInvariant());

        private XElement? Optional(string name, string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : new XElement(ns + name, value);
    }
}
