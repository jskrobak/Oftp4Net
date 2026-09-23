using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Oftp4Net.Core.Protocol;
using Oftp4Net.Domain;
using Oftp4Net.Services.Oftp;
using Oftp4Net.Services.Security;
using Oftp4Net.Services.Tsl;

namespace Oftp4Net.Services.Pdx;

/// <summary>One value of the partner changed by the import.</summary>
public sealed record PdxChange(string Field, string? OldValue, string? NewValue);

/// <summary>What we know about our own station when a datasheet is compared with it.</summary>
public sealed record PdxPlanContext
{
    public required StationProfile Profile { get; init; }

    /// <summary>A TLS client certificate is configured (settings), so we can authenticate when calling partners.</summary>
    public bool HasTlsClientCertificate { get; init; }

    /// <summary>A file security certificate is configured (settings): signing, decryption, signed responses.</summary>
    public bool HasFileSecurityCertificate { get; init; }

    public DateTime Now { get; init; } = DateTime.Now;

    /// <summary>Certification authorities of the Odette TSL; certificates issued by them are trusted.</summary>
    public X509Certificate2Collection TrustAnchors { get; init; } = [];
}

/// <summary>
/// What importing a datasheet does to a partner: the changes to show before it is applied, and why it cannot be
/// applied. Nothing is changed until <see cref="Apply"/> is called.
/// </summary>
public sealed class PdxImportPlan
{
    public PdxDocument? Document { get; init; }

    /// <summary>The datasheet as text, kept with the partner's history.</summary>
    public string? Content { get; set; }

    /// <summary>The partner the datasheet belongs to (same SSID), <c>null</c> when a new partner is created.</summary>
    public Partner? Existing { get; init; }

    public bool IsNew => Existing is null;

    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<PdxChange> Changes { get; } = [];

    public bool CanApply => Document is not null && Errors.Count == 0;

    public string PartnerName => Existing?.Name ?? Document?.Station.Name ?? "";

    internal List<Action<Partner, Func<PdxCertificate, Certificate>>> Setters { get; } = [];

    /// <summary>
    /// Writes the changes to <paramref name="partner"/>; <paramref name="certificates"/> returns the stored
    /// certificate for a certificate of the datasheet (an existing one or a new one).
    /// </summary>
    public void Apply(Partner partner, Func<PdxCertificate, Certificate> certificates)
    {
        if (!CanApply)
            throw new InvalidOperationException("The datasheet cannot be applied: " + string.Join(" ", Errors));

        foreach (var setter in Setters)
            setter(partner, certificates);
    }
}

/// <summary>
/// Compares an OFTP2 Communication Setup (PDX) of a partner with our station profile and works out how the
/// partner has to be set up. Elements missing in the datasheet leave the partner as it is, so a datasheet with
/// only a changed sub-station (as allowed by OP08) changes only that sub-station.
/// </summary>
public static class PdxPartnerPlanner
{
    private const int NameLength = 50;

    public static PdxImportPlan Plan(PdxDocument document, Partner? existing, PdxPlanContext context) =>
        new Planner(document, existing, context).Build();

    /// <summary>The settings of files resulting from the negotiation, <c>null</c> where the datasheet says nothing.</summary>
    private sealed record FileFlags
    {
        public bool? Sign { get; init; }
        public bool? Encrypt { get; init; }
        public bool? Compress { get; init; }
        public bool? RequestSignedEndResponse { get; init; }
        public bool? RequireSigned { get; init; }
        public bool? RequireEncrypted { get; init; }
        public bool? RequireCompressed { get; init; }

        /// <summary>Some feature needs a cipher suite (anything but compression).</summary>
        public bool NeedsCipher => Sign == true || Encrypt == true || RequestSignedEndResponse == true ||
                                   RequireSigned == true || RequireEncrypted == true;
    }

    private sealed class Planner(PdxDocument document, Partner? existing, PdxPlanContext context)
    {
        private readonly PdxImportPlan _plan = new() { Document = document, Existing = existing };
        private StationProfile Own => context.Profile;
        private bool IsNew => existing is null;

        public PdxImportPlan Build()
        {
            CheckDocument();
            PlanStation();
            PlanConnection();
            var flags = PlanFileSecurity(out var secureAuthentication);
            PlanProtocolLevel(flags, secureAuthentication);
            PlanCertificates(flags);
            PlanSubStations();

            // The partner gets our datasheet in the version it sends its own in.
            if (PdxParser.Versions.Contains(document.Version))
                Set("PDX version", existing?.PdxVersion, document.Version, (p, v) => p.PdxVersion = v!);

            var docDate = document.DocDate.LocalDateTime;
            Set("Datasheet", existing?.SetupDocumentId, document.DocId, (p, v) =>
            {
                p.SetupDocumentId = v;
                p.SetupDocumentDate = docDate;
                p.SetupAppliedDate = DateTime.Now;
            }, record: false);

            return _plan;
        }

        private void CheckDocument()
        {
            if (existing?.SetupDocumentId == document.DocId)
                Warning($"This datasheet ({document.DocId}) has already been applied to the partner.");
            else if (existing?.SetupDocumentDate is { } applied && document.DocDate.LocalDateTime < applied)
                Error($"The datasheet was created {document.DocDate.LocalDateTime:g}, before the one applied to the partner ({applied:g}).");

            if (document.ValidFrom.LocalDateTime > context.Now)
                Warning($"The datasheet is valid from {document.ValidFrom.LocalDateTime:g}.");

            var session = document.Session;
            if (session.MinimumProtocolReleaseLevel is { } min && min > ProtocolLevels.Oftp2)
                Error($"The partner requires OFTP release level {min} at least, the highest supported one is {ProtocolLevels.Oftp2}.");
        }

        /// <summary>
        /// The highest release both sides support: the partner's maximum, or the next lower one we know (there is no
        /// level 3). File level security and secure authentication need OFTP 2.0.
        /// </summary>
        private void PlanProtocolLevel(FileFlags flags, bool? secureAuthentication)
        {
            if (document.Session.MaximumProtocolReleaseLevel is not { } max)
                return;

            var level = ProtocolLevels.All.Where(l => l <= max).DefaultIfEmpty(0).Max();
            if (level == 0)
            {
                Error($"The partner supports ODETTE-FTP release level {max} only, which is not supported.");
                return;
            }

            Set("ODETTE-FTP release", existing?.ProtocolLevel, level, (p, v) => p.ProtocolLevel = v!.Value);

            if (!ProtocolLevels.HasOftp2Features(level) && (flags.NeedsCipher || flags.Compress == true ||
                                                             flags.RequireCompressed == true || secureAuthentication == true))
                Error($"The settings need OFTP 2.0 (file security, compression or secure authentication), but the partner " +
                      $"supports ODETTE-FTP {ProtocolLevels.Name(level)} only.");
        }

        private void PlanStation()
        {
            var station = document.Station;
            var ssid = document.Session.Ssid;

            var name = station.Name.Length > NameLength ? station.Name[..NameLength] : station.Name;
            if (name.Length < station.Name.Length)
                Warning($"The name '{station.Name}' is shortened to {NameLength} characters.");

            if (IsNew)
            {
                Set("Name", null, name, (p, v) => p.Name = v!);
                Set("SSID", null, ssid, (p, v) => p.SSID = v!);
                // The datasheet has the SFID of sub-stations only; the main station uses its SSID code.
                Set("SFID", null, ssid, (p, v) => p.SFID = v!);
                Set("Description", null, "Imported from an OFTP2 Communication Setup", (p, v) => p.Description = v);
            }

            var company = station.Company;
            Set("Company", existing?.CompanyName, company.Name, (p, v) => p.CompanyName = v);
            Set("DUNS", existing?.Duns, station.Duns, (p, v) => p.Duns = v);
            Set("Address", existing?.Address, company.AddressLines.Count == 0 ? null : string.Join(Environment.NewLine, company.AddressLines),
                (p, v) => p.Address = v);
            Set("City", existing?.City, company.City, (p, v) => p.City = v);
            Set("ZIP code", existing?.ZipCode, company.ZipCode, (p, v) => p.ZipCode = v);
            Set("Country", existing?.Country, company.Country, (p, v) => p.Country = v);
            Set("Info document", existing?.InfoDocumentUrl, station.InfoDocumentUrl, (p, v) => p.InfoDocumentUrl = v);

            if (station.Contacts is { } contacts)
            {
                var mapped = contacts.Select(MapContact).ToList();
                SetList("Contacts", existing?.Contacts, mapped, (p, v) => p.Contacts = v, Describe);
            }

            if (document.Session.Password is { } password)
                Set("Password", existing?.Password, password, (p, v) => p.Password = v, secret: true);

            if (document.InboundDsnList is { } inbound)
                SetList("Virtual files the partner receives", existing?.InboundDsnPatterns, inbound.Select(MapDsn).ToList(),
                    (p, v) => p.InboundDsnPatterns = v, Describe);
            if (document.OutboundDsnList is { } outbound)
                SetList("Virtual files the partner sends", existing?.OutboundDsnPatterns, outbound.Select(MapDsn).ToList(),
                    (p, v) => p.OutboundDsnPatterns = v, Describe);
        }

        /// <summary>The address we call the partner at, from the first TLS connection it offers (or the first plain one).</summary>
        private void PlanConnection()
        {
            var connection = document.InboundConnections.FirstOrDefault(c => c.Type == PdxConnectionType.Tls)
                             ?? document.InboundConnections.FirstOrDefault();

            if (connection is null)
            {
                if (IsNew)
                    Error("The datasheet contains no address the partner can be called at (InboundCommunicationSettings).");
                return;
            }

            Set("Host", existing?.Host, connection.ServerHost, (p, v) => p.Host = v!);
            Set("Port", existing?.Port, connection.ServerPort, (p, v) => p.Port = v!.Value);

            var useTls = connection.Type == PdxConnectionType.Tls;
            Set("TLS", existing?.UseTls, useTls, (p, v) => p.UseTls = v!.Value);

            if (useTls)
            {
                var tls = TlsVersions(connection.TlsVersions);
                Set("TLS versions", existing?.Tls, tls, (p, v) => p.Tls = v!.Value);
            }
            else
            {
                Warning("The partner is called without TLS.");
            }

            if (connection.TlsClientAuth is { } clientAuth)
            {
                var result = PdxNegotiator.Resolve(Own.TlsClientAuthentication, clientAuth);
                if (result == NegotiationResult.Conflict)
                    Error($"TLS client authentication: the partner {Verb(clientAuth)} it, our station profile {Verb(Own.TlsClientAuthentication)} it.");
                else if (result == NegotiationResult.On && !context.HasTlsClientCertificate)
                {
                    var message = "The partner asks for a TLS client certificate, but none is configured in the settings.";
                    if (clientAuth == SecurityUsage.Required)
                        Error(message);
                    else
                        Warning(message);
                }
            }
        }

        private SslProtocols TlsVersions(IReadOnlyList<string> versions)
        {
            var result = SslProtocols.None;
            foreach (var version in versions)
            {
                switch (version)
                {
                    case "TLSv1.2":
                        result |= SslProtocols.Tls12;
                        break;
                    case "TLSv1.3":
                        result |= SslProtocols.Tls13;
                        break;
                    default:
                        Warning($"{version} is not supported and is ignored.");
                        break;
                }
            }

            if (result != SslProtocols.None)
                return result;

            if (versions.Count > 0)
                Error("The partner offers no TLS version that is supported (TLS 1.2 or 1.3).");
            return SslProtocols.Tls12 | SslProtocols.Tls13;
        }

        private FileFlags PlanFileSecurity(out bool? secureAuthentication)
        {
            var flags = Negotiate(document.InboundFileSettings, document.OutboundFileSettings, "");

            Set("Sign sent files", existing?.SignFiles, flags.Sign, (p, v) => p.SignFiles = v!.Value);
            Set("Encrypt sent files", existing?.EncryptFiles, flags.Encrypt, (p, v) => p.EncryptFiles = v!.Value);
            Set("Compress sent files", existing?.CompressFiles, flags.Compress, (p, v) => p.CompressFiles = v!.Value);
            Set("Ask for signed EERP", existing?.RequestSignedEndResponse, flags.RequestSignedEndResponse,
                (p, v) => p.RequestSignedEndResponse = v!.Value);
            Set("Require signed files", existing?.RequireSignedFiles, flags.RequireSigned, (p, v) => p.RequireSignedFiles = v!.Value);
            Set("Require encrypted files", existing?.RequireEncryptedFiles, flags.RequireEncrypted,
                (p, v) => p.RequireEncryptedFiles = v!.Value);
            Set("Require compressed files", existing?.RequireCompressedFiles, flags.RequireCompressed,
                (p, v) => p.RequireCompressedFiles = v!.Value);

            secureAuthentication = null;
            if (document.Session.SecureAuthentication is { } auth)
            {
                secureAuthentication = Result(Own.SecureAuthentication, auth.Usage, "Secure authentication") == NegotiationResult.On;
                Set("Secure authentication", existing?.SecureAuthentication, secureAuthentication,
                    (p, v) => p.SecureAuthentication = v!.Value);
            }

            var cipherNeeded = flags.NeedsCipher || secureAuthentication == true;
            if (document.CipherSetting is { } ciphers)
            {
                if (PdxNegotiator.ResolveCipherSuite(Own, ciphers) is { } suite)
                    Set("Cipher suite", existing?.FileCipherSuite, suite.Code, (p, v) => p.FileCipherSuite = v!);
                else if (cipherNeeded)
                    Error($"None of the cipher suites of the partner ({string.Join(", ", ciphers.All)}) is accepted by our station profile.");
                else
                    Warning($"None of the cipher suites of the partner ({string.Join(", ", ciphers.All)}) is accepted, the current one is kept.");
            }

            if (!context.HasFileSecurityCertificate &&
                (flags.Sign == true || flags.RequireEncrypted == true || secureAuthentication == true))
                Error("The settings require signing, decryption or authentication with our certificate, but no file security certificate is configured in the settings.");

            return flags;
        }

        /// <summary>
        /// Compares the settings of the partner with our profile: what the partner receives with what we send and
        /// what the partner sends with what we receive.
        /// </summary>
        private FileFlags Negotiate(PdxFileSettings? theirInbound, PdxFileSettings? theirOutbound, string where)
        {
            var flags = new FileFlags();

            if (theirInbound is not null)
            {
                flags = flags with
                {
                    Sign = On(Own.OutboundFileSignature, theirInbound.FileSignature, "Signing of files we send", where),
                    Encrypt = On(Own.OutboundFileEncryption, theirInbound.FileEncryption, "Encryption of files we send", where),
                    Compress = On(Own.OutboundFileCompression, theirInbound.FileCompression, "Compression of files we send", where),
                    RequestSignedEndResponse = On(Own.OutboundEerpSignature, theirInbound.EerpSignature,
                        "Signed EERP for files we send", where),
                };
            }

            if (theirOutbound is not null)
            {
                flags = flags with
                {
                    RequireSigned = On(Own.InboundFileSignature, theirOutbound.FileSignature, "Signing of files we receive", where),
                    RequireEncrypted = On(Own.InboundFileEncryption, theirOutbound.FileEncryption, "Encryption of files we receive", where),
                    RequireCompressed = On(Own.InboundFileCompression, theirOutbound.FileCompression, "Compression of files we receive", where),
                };

                // The partner asks for signed responses in every file (SFIDSIGN); only a conflict matters here.
                if (On(Own.InboundEerpSignature, theirOutbound.EerpSignature, "Signed EERP for files we receive", where) &&
                    !context.HasFileSecurityCertificate)
                    Error("The partner wants signed EERPs, but no file security certificate is configured in the settings to sign them with.");
            }

            return flags;
        }

        private bool On(SecurityUsage own, PdxSupport theirs, string feature, string where) =>
            Result(own, theirs.Usage, feature + where) == NegotiationResult.On;

        private NegotiationResult Result(SecurityUsage own, SecurityUsage theirs, string feature)
        {
            var result = PdxNegotiator.Resolve(own, theirs);
            if (result == NegotiationResult.Conflict)
                Error($"{feature}: the partner {Verb(theirs)} it, our station profile {Verb(own)} it.");
            return result;
        }

        /// <summary>
        /// The TLS certificate is trusted for the partner's server (pinned), the file security certificate is the
        /// one files are encrypted for and signatures are verified against.
        /// </summary>
        private void PlanCertificates(FileFlags flags)
        {
            var tlsRef = document.InboundConnections.FirstOrDefault(c => c.Type == PdxConnectionType.Tls)?.CertificateRef;
            var tls = document.FindCertificate(tlsRef);
            if (tls is not null)
            {
                CheckTrust(tls);
                SetCertificate("TLS certificate", existing?.TrustedCertificate, tls, (p, c) => p.TrustedCertificate = c);
            }

            // One certificate serves all file security features here; the datasheet may name one per feature.
            var references = new[]
                {
                    document.InboundFileSettings?.FileEncryption,
                    document.OutboundFileSettings?.FileSignature,
                    document.Session.SecureAuthentication,
                    document.InboundFileSettings?.EerpSignature,
                }
                .Where(s => s is not null)
                .SelectMany(s => s!.CertificateRefs)
                .Select(document.FindCertificate)
                .OfType<PdxCertificate>()
                .DistinctBy(c => Thumbprint(c.Certificate))
                .ToList();

            if (references.Count > 1)
                Warning($"The partner uses different certificates for file security ({string.Join(", ", references.Select(r => r.Name))}); " +
                        $"only one partner certificate is supported, '{references[0].Name}' is used.");

            var security = references.FirstOrDefault();
            if (security is null && (flags.Encrypt == true || flags.RequireSigned == true || flags.RequestSignedEndResponse == true))
            {
                security = tls;
                if (security is not null)
                    Warning($"The datasheet names no certificate for file security, the TLS certificate '{security.Name}' is used.");
                else if (existing?.SecurityCertificateId is null)
                    Error("The settings need the partner's certificate, but the datasheet contains none.");
            }

            if (security is null)
                return;

            if (!ReferenceEquals(security, tls))
                CheckTrust(security);

            var previous = existing?.SecurityCertificate;
            if (SetCertificate("Partner certificate", previous, security, (p, c) => p.SecurityCertificate = c) && previous is not null)
                _plan.Setters.Add((p, _) => p.PreviousSecurityCertificate = previous);
        }

        /// <summary>Validity and trust: an untrusted certificate is only a warning, the user decides (OP09 PDX 1.1).</summary>
        private void CheckTrust(PdxCertificate certificate)
        {
            using var x509 = X509CertificateLoader.LoadCertificate(certificate.Certificate);

            if (x509.NotAfter < context.Now)
                Error($"The certificate '{certificate.Name}' ({x509.Subject}) expired on {x509.NotAfter:d}.");
            else if (x509.NotBefore > context.Now)
                Warning($"The certificate '{certificate.Name}' ({x509.Subject}) is valid from {x509.NotBefore:d} only.");

            var intermediates = certificate.CaCertificates.Select(X509CertificateLoader.LoadCertificate).ToList();
            try
            {
                if (CertificateTrust.Check(x509, intermediates, context.TrustAnchors, out var problem) == CertificateTrustSource.None)
                    Warning($"The certificate '{certificate.Name}' ({x509.Subject}) is not issued by a certification authority of the " +
                            "Odette TSL or of the system" + (string.IsNullOrEmpty(problem) ? "." : $": {problem}"));
            }
            finally
            {
                foreach (var intermediate in intermediates)
                    intermediate.Dispose();
            }
        }

        private void PlanSubStations()
        {
            foreach (var sub in document.SubStations)
            {
                var current = existing?.SubStations.FirstOrDefault(s => string.Equals(s.SFID, sub.Sfid, StringComparison.OrdinalIgnoreCase));
                var where = $" (sub-station {sub.Name})";
                var flags = Negotiate(sub.InboundFileSettings, sub.OutboundFileSettings, where);

                string? cipher = null;
                if (sub.CipherSetting is { } ciphers)
                {
                    cipher = PdxNegotiator.ResolveCipherSuite(Own, ciphers)?.Code;
                    if (cipher is null)
                        Error($"None of the cipher suites of sub-station {sub.Name} ({string.Join(", ", ciphers.All)}) is accepted by our station profile.");
                }

                var updated = new PartnerSubStation
                {
                    Name = sub.Name,
                    SFID = sub.Sfid,
                    // Missing settings are inherited from the partner; settings of an update replace the old ones.
                    SignFiles = flags.Sign ?? current?.SignFiles,
                    EncryptFiles = flags.Encrypt ?? current?.EncryptFiles,
                    CompressFiles = flags.Compress ?? current?.CompressFiles,
                    RequestSignedEndResponse = flags.RequestSignedEndResponse ?? current?.RequestSignedEndResponse,
                    RequireSignedFiles = flags.RequireSigned ?? current?.RequireSignedFiles,
                    RequireEncryptedFiles = flags.RequireEncrypted ?? current?.RequireEncryptedFiles,
                    RequireCompressedFiles = flags.RequireCompressed ?? current?.RequireCompressedFiles,
                    FileCipherSuite = cipher ?? current?.FileCipherSuite,
                    Contacts = sub.Contacts?.Select(MapContact).ToList() ?? current?.Contacts ?? [],
                    InboundDsnPatterns = sub.InboundDsnList?.Select(MapDsn).ToList() ?? current?.InboundDsnPatterns ?? [],
                    OutboundDsnPatterns = sub.OutboundDsnList?.Select(MapDsn).ToList() ?? current?.OutboundDsnPatterns ?? [],
                };

                var oldText = current is null ? null : Describe(current);
                var newText = Describe(updated);
                if (oldText == newText)
                    continue;

                _plan.Changes.Add(new PdxChange($"Sub-station {sub.Sfid}", oldText, newText));
                _plan.Setters.Add((p, _) =>
                {
                    // A new list, so that the change of the JSON column is detected.
                    p.SubStations = p.SubStations
                        .Where(s => !string.Equals(s.SFID, updated.SFID, StringComparison.OrdinalIgnoreCase))
                        .Append(updated)
                        .ToList();
                });
            }
        }

        private void Set<T>(string field, T? current, T? value, Action<Partner, T?> apply, bool secret = false, bool record = true)
        {
            // Nothing in the datasheet: the partner keeps its value.
            if (value is null)
                return;
            if (!IsNew && EqualityComparer<T>.Default.Equals(current, value))
                return;

            if (record)
                _plan.Changes.Add(new PdxChange(field, IsNew ? null : Format(current, secret), Format(value, secret)));
            _plan.Setters.Add((p, _) => apply(p, value));
        }

        private void SetList<T>(string field, List<T>? current, List<T> value, Action<Partner, List<T>> apply, Func<T, string> describe)
        {
            var oldText = current is null || current.Count == 0 ? null : string.Join("; ", current.Select(describe));
            var newText = value.Count == 0 ? null : string.Join("; ", value.Select(describe));
            if (!IsNew && oldText == newText)
                return;
            if (IsNew && newText is null)
                return;

            _plan.Changes.Add(new PdxChange(field, oldText, newText ?? "(none)"));
            _plan.Setters.Add((p, _) => apply(p, value));
        }

        /// <summary>Returns true when the certificate changes.</summary>
        private bool SetCertificate(string field, Certificate? current, PdxCertificate value, Action<Partner, Certificate> apply)
        {
            if (current is not null && CurrentThumbprint(current) == Thumbprint(value.Certificate))
                return false;

            using var x509 = X509CertificateLoader.LoadCertificate(value.Certificate);
            _plan.Changes.Add(new PdxChange(field, current is null ? null : $"{current.Name} (valid to {current.ValidTo:d})",
                $"{x509.Subject} (valid to {x509.NotAfter:d})"));
            _plan.Setters.Add((p, certificates) => apply(p, certificates(value)));
            return true;
        }

        private static string? CurrentThumbprint(Certificate certificate)
        {
            try
            {
                using var x509 = CertificateLoader.Load(certificate);
                return x509.GetCertHashString(HashAlgorithmName.SHA256);
            }
            catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or FormatException)
            {
                return null;
            }
        }

        private void Error(string message) => _plan.Errors.Add(message);
        private void Warning(string message) => _plan.Warnings.Add(message);
    }

    internal static string Thumbprint(byte[] certificate)
    {
        using var x509 = X509CertificateLoader.LoadCertificate(certificate);
        return x509.GetCertHashString(HashAlgorithmName.SHA256);
    }

    private static string Verb(SecurityUsage usage) => usage switch
    {
        SecurityUsage.Forbidden => "forbids",
        SecurityUsage.Optional => "allows",
        SecurityUsage.Preferred => "prefers",
        _ => "requires",
    };

    private static string? Format<T>(T? value, bool secret) => value switch
    {
        null => null,
        _ when secret => "********",
        bool b => b ? "yes" : "no",
        SslProtocols p => p.ToString().Replace("Tls12", "TLS 1.2").Replace("Tls13", "TLS 1.3"),
        _ => value.ToString(),
    };

    private static PartnerContact MapContact(PdxContact contact) => new()
    {
        Name = contact.Name,
        Description = contact.Description,
        Phones = contact.Phones.ToList(),
        Emails = contact.Emails.ToList(),
        Url = contact.Url,
    };

    private static PartnerDsnPattern MapDsn(PdxDsnPattern dsn) => new()
    {
        Pattern = dsn.Pattern,
        FileFormat = dsn.FileFormat,
        MaximumRecordSize = dsn.MaximumRecordSize,
        Description = dsn.DescriptionShort,
        LongDescription = dsn.DescriptionLong,
    };

    private static string Describe(PartnerContact contact) =>
        string.Join(", ", new[] { contact.Name, contact.Description }.Concat(contact.Phones).Concat(contact.Emails)
            .Append(contact.Url).Where(s => !string.IsNullOrEmpty(s)));

    private static string Describe(PartnerDsnPattern dsn) => $"{dsn.Pattern} ({dsn.FileFormat}, {dsn.Description})";

    private static string Describe(PartnerSubStation sub)
    {
        var settings = new[]
        {
            Flag("signed", sub.SignFiles),
            Flag("encrypted", sub.EncryptFiles),
            Flag("compressed", sub.CompressFiles),
            Flag("signed EERP", sub.RequestSignedEndResponse),
            Flag("require signed", sub.RequireSignedFiles),
            Flag("require encrypted", sub.RequireEncryptedFiles),
            Flag("require compressed", sub.RequireCompressedFiles),
            sub.FileCipherSuite is null ? null : $"cipher {sub.FileCipherSuite}",
            sub.Contacts.Count == 0 ? null : "contacts: " + string.Join("; ", sub.Contacts.Select(Describe)),
            sub.InboundDsnPatterns.Count + sub.OutboundDsnPatterns.Count == 0
                ? null
                : "virtual files: " + string.Join("; ", sub.InboundDsnPatterns.Concat(sub.OutboundDsnPatterns).Select(Describe)),
        }.Where(s => s is not null);

        var text = string.Join(", ", settings);
        return $"{sub.Name}: {(text.Length > 0 ? text : "as the partner")}";

        static string? Flag(string name, bool? value) => value switch
        {
            true => name,
            false => "not " + name,
            null => null,
        };
    }
}
