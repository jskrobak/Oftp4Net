using System.Formats.Asn1;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Oftp4Net.Core.Transport;

public sealed class OftpTlsOptions
{
    public SslProtocols Protocols { get; init; } = SslProtocols.Tls12 | SslProtocols.Tls13;

    /// <summary>
    /// Certificate with private key presented by this side: the server certificate of a listener,
    /// or the optional client certificate of a connecting client.
    /// </summary>
    public X509Certificate2? LocalCertificate { get; init; }

    /// <summary>
    /// Additional trust anchors. The remote certificate is accepted when it is valid according to the operating
    /// system trust store, when it chains up to one of these certificates, or when it equals one of them (pinning).
    /// The collection is read on every handshake; assign a new collection to change it while a listener is running.
    /// </summary>
    public X509Certificate2Collection TrustedCertificates { get; set; } = [];

    /// <summary>
    /// Certificates of <see cref="TrustedCertificates"/> that are trusted only to verify the certification
    /// authority above them and must not issue the certificate of a partner: the roots that the Odette trust list
    /// carries for verification. A certificate whose chain reaches no other trusted certificate is refused, even
    /// though the chain itself is sound. The collection is read on every handshake.
    /// </summary>
    public X509Certificate2Collection VerificationOnlyCertificates { get; set; } = [];

    /// <summary>
    /// Listener only: require the client to present a certificate and check it. Without it a certificate the
    /// client presents is not checked; the partner is authenticated by its SSID and password.
    /// </summary>
    public bool RequireClientCertificate { get; init; }

    /// <summary>
    /// Connecting side only: accept the certificate of the other side even when it is not valid. The problem is
    /// still reported in <see cref="OftpConnectDiagnostics.CertificateProblem"/>.
    /// </summary>
    public bool AcceptInvalidCertificate { get; init; }

    /// <summary>
    /// How the revocation of the certificate of the other side is checked. A pinned certificate is trusted by
    /// itself, so the policy applies to certificates that are accepted through a chain.
    /// </summary>
    public CertificateRevocationPolicy Revocation { get; init; } = new();

    /// <summary>
    /// Connecting side only: the Odette ID (SSID) of the other side. A certificate that is issued for another name
    /// than the host that was called is accepted when its chain is trusted and it carries this ID, in the serial
    /// number of its subject or as <c>oftp://</c> URI among its alternative names, as certificates of the Odette CA
    /// do. The ID names the OFTP node, so it stays when the certificate is replaced. <c>null</c> checks the name only.
    /// </summary>
    public string? OdetteId { get; init; }
}

/// <summary>What a certificate of the Odette CA says about the OFTP node it is issued for.</summary>
public static class OdetteCertificate
{
    /// <summary>
    /// The Odette IDs a certificate is issued for: the serial number of its subject and the <c>oftp://</c> URIs
    /// among its alternative names, where the Odette CA puts the SSID of the node.
    /// </summary>
    public static IEnumerable<string> Ids(X509Certificate2 certificate)
    {
        foreach (var name in certificate.SubjectName.EnumerateRelativeDistinguishedNames())
        {
            if (!name.HasMultipleElements && name.GetSingleElementType().Value == SerialNumberOid &&
                name.GetSingleElementValue() is { Length: > 0 } serial)
                yield return serial.Trim();
        }

        foreach (var extension in certificate.Extensions)
        {
            if (extension.Oid?.Value != SubjectAlternativeNameOid)
                continue;
            foreach (var uri in Uris(extension.RawData))
                if (uri.StartsWith(OftpScheme, StringComparison.OrdinalIgnoreCase))
                    yield return uri[OftpScheme.Length..].TrimEnd('/').Trim();
        }
    }

    private const string SerialNumberOid = "2.5.4.5";
    private const string SubjectAlternativeNameOid = "2.5.29.17";
    private const string OftpScheme = "oftp://";

    /// <summary>The uniformResourceIdentifier entries of GeneralNames (RFC 5280, 4.2.1.6).</summary>
    private static List<string> Uris(byte[] subjectAlternativeName)
    {
        var uris = new List<string>();
        var uriTag = new Asn1Tag(TagClass.ContextSpecific, 6);
        try
        {
            var names = new AsnReader(subjectAlternativeName, AsnEncodingRules.DER).ReadSequence();
            while (names.HasData)
            {
                if (names.PeekTag() == uriTag)
                    uris.Add(names.ReadCharacterString(UniversalTagNumber.IA5String, uriTag));
                else
                    names.ReadEncodedValue();
            }
        }
        catch (AsnContentException)
        {
            // A malformed extension carries no usable ID.
        }

        return uris;
    }
}

/// <summary>Problems of a certificate of the other side that callers act on.</summary>
public static class OftpCertificateProblems
{
    /// <summary>The certificate is trusted, but issued for another name than the host that was called.</summary>
    public const string NameMismatch = "The certificate is not issued for the address that was called (name mismatch).";
}

internal static class OftpCertificateValidator
{
    public static bool Validate(X509Certificate? certificate, SslPolicyErrors errors,
        X509Certificate2Collection trusted, bool certificateRequired,
        CertificateRevocationPolicy? revocation = null, X509Certificate2Collection? verificationOnly = null,
        string? odetteId = null) =>
        Validate(certificate, errors, trusted, certificateRequired, revocation, verificationOnly, odetteId, out _);

    /// <param name="odetteId">See <see cref="OftpTlsOptions.OdetteId"/>.</param>
    /// <param name="problem">Why the certificate is refused, for the log of a connection test.</param>
    public static bool Validate(X509Certificate? certificate, SslPolicyErrors errors,
        X509Certificate2Collection trusted, bool certificateRequired,
        CertificateRevocationPolicy? revocation, X509Certificate2Collection? verificationOnly, string? odetteId,
        out string? problem)
    {
        problem = null;
        if (certificate is null)
        {
            if (certificateRequired)
                problem = "No certificate was presented.";
            return !certificateRequired;
        }

        if (errors == SslPolicyErrors.None)
            return true;

        var leaf = certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());

        // Pinned end entity certificate: trust it regardless of chain and host name, but not when it expired.
        foreach (var pinned in trusted)
        {
            if (pinned.RawDataMemory.Span.SequenceEqual(leaf.RawDataMemory.Span))
            {
                if (!IsTimeValid(leaf))
                    problem = TimeProblem(leaf);
                return problem is null;
            }
        }

        // The name is checked last, after the chain: only a trusted certificate can stand for the Odette ID it carries.
        var nameMismatch = (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0;
        if (errors == SslPolicyErrors.RemoteCertificateNameMismatch)
        {
            // The operating system trusts the chain.
            if (CarriesOdetteId(leaf, odetteId))
                return true;
            problem = OftpCertificateProblems.NameMismatch;
            return false;
        }

        if (trusted.Count == 0)
        {
            problem = $"The certificate is not trusted by the operating system ({errors}) and no certificate is trusted for the partner here.";
            return false;
        }

        if (!IsTimeValid(leaf))
        {
            problem = TimeProblem(leaf);
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(trusted);
        (revocation ?? new CertificateRevocationPolicy()).ApplyTo(chain.ChainPolicy);
        if (!chain.Build(leaf))
        {
            problem = "The certificate chain is not trusted: " + string.Join("; ", chain.ChainStatus
                .Select(s => s.StatusInformation.Trim().TrimEnd('.')).Where(s => s.Length > 0).Distinct()) + ".";
            return false;
        }

        if (!IssuedByAnAuthority(chain, trusted, verificationOnly))
        {
            problem = "The certificate is issued by a root the trust list carries only to verify an authority.";
            return false;
        }

        if (nameMismatch && !CarriesOdetteId(leaf, odetteId))
        {
            problem = OftpCertificateProblems.NameMismatch;
            return false;
        }

        return true;
    }

    /// <summary>The certificate is one of the trusted ones itself, so neither its chain nor its name is checked.</summary>
    public static bool IsPinned(X509Certificate certificate, X509Certificate2Collection trusted)
    {
        var raw = certificate.GetRawCertData();
        return trusted.Any(t => t.RawDataMemory.Span.SequenceEqual(raw));
    }

    private static bool CarriesOdetteId(X509Certificate2 certificate, string? odetteId) =>
        !string.IsNullOrWhiteSpace(odetteId) &&
        OdetteCertificate.Ids(certificate).Any(id => string.Equals(id, odetteId.Trim(), StringComparison.OrdinalIgnoreCase));


    private static string TimeProblem(X509Certificate2 certificate) =>
        $"The certificate is valid from {certificate.NotBefore:d} to {certificate.NotAfter:d}, not today.";

    /// <summary>
    /// Whether the chain passes through a certificate that may issue: a chain that only reaches certificates which
    /// are trusted to verify an authority (the roots of the Odette trust list) is not enough, the certificate has
    /// to come from the authority itself (Odette OP08 2.7).
    /// </summary>
    private static bool IssuedByAnAuthority(X509Chain chain, X509Certificate2Collection trusted,
        X509Certificate2Collection? verificationOnly)
    {
        if (verificationOnly is not { Count: > 0 })
            return true;

        for (var i = 1; i < chain.ChainElements.Count; i++)
        {
            var element = chain.ChainElements[i].Certificate;
            if (Contains(trusted, element) && !Contains(verificationOnly, element))
                return true;
        }

        return false;
    }

    private static bool Contains(X509Certificate2Collection collection, X509Certificate2 certificate) =>
        collection.Any(c => c.RawDataMemory.Span.SequenceEqual(certificate.RawDataMemory.Span));

    /// <summary>A certificate that is not valid yet or expired is refused whatever else speaks for it.</summary>
    private static bool IsTimeValid(X509Certificate2 certificate)
    {
        var now = DateTime.Now;
        return now >= certificate.NotBefore && now <= certificate.NotAfter;
    }
}
