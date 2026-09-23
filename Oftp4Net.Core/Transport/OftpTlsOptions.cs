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

    /// <summary>Listener only: require the client to present a certificate.</summary>
    public bool RequireClientCertificate { get; init; }

    /// <summary>
    /// How the revocation of the certificate of the other side is checked. A pinned certificate is trusted by
    /// itself, so the policy applies to certificates that are accepted through a chain.
    /// </summary>
    public CertificateRevocationPolicy Revocation { get; init; } = new();
}

internal static class OftpCertificateValidator
{
    public static bool Validate(X509Certificate? certificate, SslPolicyErrors errors,
        X509Certificate2Collection trusted, bool certificateRequired,
        CertificateRevocationPolicy? revocation = null, X509Certificate2Collection? verificationOnly = null) =>
        Validate(certificate, errors, trusted, certificateRequired, revocation, verificationOnly, out _);

    /// <param name="problem">Why the certificate is refused, for the log of a connection test.</param>
    public static bool Validate(X509Certificate? certificate, SslPolicyErrors errors,
        X509Certificate2Collection trusted, bool certificateRequired,
        CertificateRevocationPolicy? revocation, X509Certificate2Collection? verificationOnly, out string? problem)
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

        if (trusted.Count == 0)
        {
            problem = $"The certificate is not trusted by the operating system ({errors}) and no certificate is trusted for the partner here.";
            return false;
        }

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

        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
        {
            problem = "The certificate is not issued for the address that was called (name mismatch).";
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

        return true;
    }

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
