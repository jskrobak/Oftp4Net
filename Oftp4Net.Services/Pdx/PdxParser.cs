using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Oftp4Net.Domain;

namespace Oftp4Net.Services.Pdx;

/// <summary>Outcome of reading a PDX: the document when it could be read, and what was wrong with it.</summary>
public sealed record PdxParseResult(PdxDocument? Document, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool Success => Document is not null && Errors.Count == 0;
}

/// <summary>
/// Reads an OFTP2 Communication Setup (PDX). Version 1.2 is validated against the schema published by Odette;
/// documents of older versions (namespace without the version) are read leniently, without the schema.
/// </summary>
public static class PdxParser
{
    public const string Namespace12 = "http://www.odette.org/OFTPCommunicationSetup/1.2";
    public const string Namespace11 = "http://www.odette.org/OFTPCommunicationSetup/1.1";

    /// <summary>The versions a datasheet can be written in.</summary>
    public static readonly IReadOnlyList<string> Versions = ["1.2", "1.1"];

    public static string NamespaceOf(string version) => version == "1.1" ? Namespace11 : Namespace12;
    public const string LegacyNamespace = "http://www.odette.org/OFTPCommunicationSetup";
    private const string RootName = "OftpCommunicationSetup";

    private static readonly Lazy<XmlSchemaSet> Schema = new(LoadSchema);

    public static PdxParseResult Parse(byte[] content)
    {
        using var stream = new MemoryStream(content);
        return Parse(stream);
    }

    public static PdxParseResult Parse(Stream content)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        XDocument xml;
        try
        {
            // No DTDs and no external resources: the document comes from outside.
            using var reader = XmlReader.Create(content, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            xml = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            return new PdxParseResult(null, [$"The file is not valid XML: {ex.Message}"], warnings);
        }

        var root = xml.Root;
        if (root is null || root.Name.LocalName != RootName)
            return new PdxParseResult(null, [$"The root element is not {RootName}, the file is not an OFTP2 Communication Setup."], warnings);

        var ns = root.Name.Namespace;
        if (ns == Namespace11)
        {
            // Version 1.1 has the structure of 1.2 (which only adds cipher suite 07): it is checked against the 1.2
            // schema with the namespace of 1.2.
            var copy = new XDocument(xml);
            foreach (var element in copy.Descendants().Where(e => e.Name.Namespace == Namespace11))
                element.Name = XName.Get(element.Name.LocalName, Namespace12);
            copy.Root!.SetAttributeValue("version", "1.2");
            copy.Validate(Schema.Value, (_, e) =>
            {
                if (e.Severity == XmlSeverityType.Error)
                    errors.Add(e.Message);
            });
        }
        else if (ns == Namespace12)
        {
            xml.Validate(Schema.Value, (_, e) =>
            {
                var position = e.Exception is { LineNumber: > 0 } x ? $" (line {x.LineNumber}, position {x.LinePosition})" : "";
                (e.Severity == XmlSeverityType.Error ? errors : warnings).Add(e.Message + position);
            });
        }
        else if (ns == LegacyNamespace || ns.NamespaceName.StartsWith(LegacyNamespace, StringComparison.Ordinal))
        {
            warnings.Add($"The document uses the namespace '{ns.NamespaceName}' of a version other than 1.2, it is read without schema validation.");
        }
        else
        {
            return new PdxParseResult(null, [$"Unknown namespace '{ns.NamespaceName}', expected '{Namespace12}'."], warnings);
        }

        if (errors.Count > 0)
            return new PdxParseResult(null, errors, warnings);

        try
        {
            var document = new Reader(ns, errors).ReadDocument(root);
            CheckReferences(document, errors);
            return new PdxParseResult(document, errors, warnings);
        }
        catch (FormatException ex)
        {
            errors.Add(ex.Message);
            return new PdxParseResult(null, errors, warnings);
        }
    }

    /// <summary>
    /// The document as text without the XML declaration, so that it can be stored and read again as UTF-8 whatever
    /// encoding it was sent in; <c>null</c> when it is not well-formed XML.
    /// </summary>
    public static string? ToText(byte[] content)
    {
        try
        {
            using var stream = new MemoryStream(content);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(reader).ToString(SaveOptions.DisableFormatting);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>Every certificate referenced anywhere must be contained in the document and readable.</summary>
    private static void CheckReferences(PdxDocument document, List<string> errors)
    {
        var references = new List<string?>();
        references.AddRange(document.InboundConnections.Select(c => c.CertificateRef));
        references.AddRange(document.OutboundConnections.SelectMany(c => c.TlsClientAuth?.CertificateRefs ?? []));
        references.AddRange(document.Session.SecureAuthentication?.CertificateRefs ?? []);
        foreach (var settings in new[] { document.InboundFileSettings, document.OutboundFileSettings }
                     .Concat(document.SubStations.SelectMany(s => new[] { s.InboundFileSettings, s.OutboundFileSettings })))
        {
            if (settings is null)
                continue;
            references.AddRange(settings.FileSignature.CertificateRefs);
            references.AddRange(settings.FileEncryption.CertificateRefs);
            references.AddRange(settings.FileCompression.CertificateRefs);
            references.AddRange(settings.EerpSignature.CertificateRefs);
        }

        foreach (var name in references.OfType<string>().Distinct())
        {
            if (document.FindCertificate(name) is null)
                errors.Add($"The certificate '{name}' is referenced, but the document does not contain it.");
        }

        foreach (var certificate in document.Certificates)
        {
            foreach (var data in certificate.CaCertificates.Prepend(certificate.Certificate))
            {
                try
                {
                    using var _ = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(data);
                }
                catch (System.Security.Cryptography.CryptographicException ex)
                {
                    errors.Add($"The certificate '{certificate.Name}' cannot be read: {ex.Message}");
                }
            }
        }
    }

    private static XmlSchemaSet LoadSchema()
    {
        using var stream = typeof(PdxParser).Assembly.GetManifestResourceStream("OftpCommunicationSetup-1p2.xsd")
            ?? throw new InvalidOperationException("The PDX schema is not embedded in the assembly.");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });

        var set = new XmlSchemaSet { XmlResolver = null };
        set.Add(Namespace12, reader);
        set.Compile();
        return set;
    }

    /// <summary>Maps the XML to the document; works with the element names of all versions.</summary>
    private sealed class Reader(XNamespace ns, List<string> errors)
    {
        public PdxDocument ReadDocument(XElement root)
        {
            var station = Required(root, "StationInformation");
            var company = Required(station, "CompanyInfo");

            return new PdxDocument
            {
                Version = (string?)root.Attribute("version") ?? "",
                DocId = Guid.TryParse((string?)root.Attribute("docid"), out var docId)
                    ? docId
                    : throw new FormatException("The attribute docid is not a valid GUID."),
                DocDate = DateAttribute(root, "docdate"),
                ValidFrom = DateAttribute(root, "validfrom"),
                Station = new PdxStationInformation
                {
                    Duns = Text(station, "DUNS"),
                    Name = RequiredText(station, "Name"),
                    Company = new PdxCompanyInfo
                    {
                        Name = RequiredText(company, "Name"),
                        AddressLines = Texts(company, "AddressLine"),
                        City = Text(company, "City"),
                        ZipCode = Text(company, "ZIPCode"),
                        Country = Text(company, "Country"),
                    },
                    IssuerIdCode = Text(station, "IssuerIDCode") ?? "",
                    InfoDocumentUrl = Text(station, "InfoDocumentURL"),
                    Contacts = Contacts(station.Element(ns + "Contacts")),
                },
                InboundConnections = Children(root.Element(ns + "InboundCommunicationSettings"), "InboundConnectionSettings")
                    .Select(c => new PdxInboundConnection
                    {
                        Type = ConnectionType(c),
                        TlsVersions = Texts(c, "TLSVersion"),
                        ServerHost = RequiredText(c, "ServerHost"),
                        ServerPort = int.Parse(RequiredText(c, "ServerPort"), CultureInfo.InvariantCulture),
                        CertificateRef = Text(c, "CertificateRef"),
                        TlsClientAuth = Support(c.Element(ns + "TLSClientAuth"))?.Usage,
                    })
                    .ToList(),
                OutboundConnections = Children(root.Element(ns + "OutboundCommunicationSettings"), "OutboundConnectionSettings")
                    .Select(c => new PdxOutboundConnection
                    {
                        Type = ConnectionType(c),
                        TlsVersions = Texts(c, "TLSVersion"),
                        OutboundIps = Texts(c, "OutboundIP"),
                        TlsClientAuth = Support(c.Element(ns + "TLSClientAuth")),
                    })
                    .ToList(),
                CipherSetting = CipherSetting(root.Element(ns + "CipherSetting")),
                Session = Session(Required(root, "SessionSettings")),
                OutboundFileSettings = FileSettings(root.Element(ns + "OutboundFileSettings")),
                InboundFileSettings = FileSettings(root.Element(ns + "InboundFileSettings")),
                OutboundDsnList = DsnList(root.Element(ns + "OutboundDsnList")),
                InboundDsnList = DsnList(root.Element(ns + "InboundDsnList")),
                SubStations = Children(root.Element(ns + "SubStations"), "SubStationInformation")
                    .Select(s => new PdxSubStation
                    {
                        Name = RequiredText(s, "Name"),
                        Sfid = RequiredText(s, "OdetteIDCodeSFID"),
                        OutboundFileSettings = FileSettings(s.Element(ns + "OutboundFileSettings")),
                        InboundFileSettings = FileSettings(s.Element(ns + "InboundFileSettings")),
                        OutboundDsnList = DsnList(s.Element(ns + "OutboundDsnList")),
                        InboundDsnList = DsnList(s.Element(ns + "InboundDsnList")),
                        CipherSetting = CipherSetting(s.Element(ns + "CipherSetting")),
                        Contacts = Contacts(s.Element(ns + "Contacts")),
                    })
                    .ToList(),
                Certificates = Children(root.Element(ns + "Certificates"), "Certificate")
                    .Select(c => new PdxCertificate
                    {
                        Name = (string?)c.Attribute("name") ?? "",
                        SubjectName = Text(c, "SubjectName") ?? "",
                        Certificate = Base64(RequiredText(c, "X509Certificate")),
                        CaCertificates = Texts(c, "X509CACertificate").Select(Base64).ToList(),
                    })
                    .ToList(),
            };
        }

        private PdxSessionSettings Session(XElement session) => new()
        {
            Ssid = RequiredText(session, "OdetteIDCodeSSID"),
            MinimumProtocolReleaseLevel = Level(Text(session, "MinimumProtocolReleaseLevel")),
            MaximumProtocolReleaseLevel = Level(Text(session, "MaximumProtocolReleaseLevel")),
            // An empty password is allowed (length 0 to 8) and differs from a missing one.
            Password = session.Element(ns + "Password")?.Value,
            SecureAuthentication = Support(session.Element(ns + "SecureAuthentication")),
        };

        private PdxFileSettings? FileSettings(XElement? element) => element is null
            ? null
            : new PdxFileSettings
            {
                FileSignature = RequiredSupport(element, "FileSignature"),
                FileEncryption = RequiredSupport(element, "FileEncryption"),
                FileCompression = RequiredSupport(element, "FileCompression"),
                EerpSignature = RequiredSupport(element, "EERPSignature"),
            };

        private PdxCipherSetting? CipherSetting(XElement? element) => element is null
            ? null
            : new PdxCipherSetting(RequiredText(element, "PrimaryCipher"), Texts(element, "AlternateCipher"));

        private List<PdxDsnPattern>? DsnList(XElement? element) => element?
            .Elements(ns + "dsn")
            .Select(d => new PdxDsnPattern
            {
                Pattern = RequiredText(d, "pattern"),
                FileFormat = RequiredText(d, "fileFormat"),
                MaximumRecordSize = Text(d, "maximumRecordSize") is { } size ? int.Parse(size, CultureInfo.InvariantCulture) : null,
                DescriptionShort = Text(d, "descriptionShort") ?? "",
                DescriptionLong = Text(d, "descriptionLong"),
            })
            .ToList();

        private List<PdxContact>? Contacts(XElement? element) => element?
            .Elements(ns + "Contact")
            .Select(c => new PdxContact
            {
                Name = RequiredText(c, "Name"),
                Description = Text(c, "Description"),
                Phones = Texts(c, "Phone"),
                Emails = Texts(c, "Email"),
                Url = Text(c, "URL"),
            })
            .ToList();

        private PdxSupport RequiredSupport(XElement parent, string name) =>
            Support(parent.Element(ns + name)) ?? throw new FormatException($"The element {parent.Name.LocalName}/{name} is missing.");

        private PdxSupport? Support(XElement? element)
        {
            if (element is null)
                return null;

            var usage = (string?)element.Attribute("usage");
            if (!Enum.TryParse<SecurityUsage>(usage, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                throw new FormatException($"'{usage}' is not a valid usage of {element.Name.LocalName}.");

            return new PdxSupport(parsed, Texts(element, "CertificateRef"));
        }

        private static PdxConnectionType ConnectionType(XElement element) =>
            string.Equals((string?)element.Attribute("type"), "TLS", StringComparison.OrdinalIgnoreCase)
                ? PdxConnectionType.Tls
                : PdxConnectionType.Tcp;

        private int? Level(string? value)
        {
            if (value is null)
                return null;
            if (int.TryParse(value, CultureInfo.InvariantCulture, out var level))
                return level;

            errors.Add($"'{value}' is not a valid protocol release level.");
            return null;
        }

        private static DateTimeOffset DateAttribute(XElement element, string name)
        {
            var value = (string?)element.Attribute(name) ?? throw new FormatException($"The attribute {name} is missing.");
            try
            {
                return XmlConvert.ToDateTimeOffset(value);
            }
            catch (FormatException)
            {
                throw new FormatException($"The attribute {name} ('{value}') is not a valid date and time.");
            }
        }

        private static byte[] Base64(string value)
        {
            try
            {
                return Convert.FromBase64String(value);
            }
            catch (FormatException)
            {
                throw new FormatException("A certificate is not valid base64 data.");
            }
        }

        private IEnumerable<XElement> Children(XElement? parent, string name) =>
            parent?.Elements(ns + name) ?? [];

        private XElement Required(XElement parent, string name) =>
            parent.Element(ns + name) ?? throw new FormatException($"The element {parent.Name.LocalName}/{name} is missing.");

        private string RequiredText(XElement parent, string name) =>
            Text(parent, name) ?? throw new FormatException($"The element {parent.Name.LocalName}/{name} is missing.");

        private string? Text(XElement parent, string name) =>
            parent.Element(ns + name)?.Value.Trim() is { Length: > 0 } value ? value : null;

        private List<string> Texts(XElement parent, string name) =>
            parent.Elements(ns + name).Select(e => e.Value.Trim()).Where(v => v.Length > 0).ToList();
    }
}
