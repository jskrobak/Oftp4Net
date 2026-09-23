using System.Security.Cryptography.X509Certificates;
using Oftp4Net.Core.Transport;

namespace Oftp4Net.Services.Tsl;

/// <summary>How a certificate is trusted.</summary>
public enum CertificateTrustSource
{
    /// <summary>Its chain ends with a certification authority of the Odette TSL.</summary>
    Tsl,

    /// <summary>Its chain ends with a root of the operating system.</summary>
    System,

    /// <summary>Neither: it has to be accepted by the administrator (e.g. a self-signed certificate).</summary>
    None,
}

/// <summary>
/// Checks the trust chain of a certificate (Odette OP08 2.7): the chain has to end with a certification authority of
/// the Odette TSL (or of the operating system). The validity dates are checked with the chain.
/// </summary>
public static class CertificateTrust
{
    /// <param name="intermediates">Further certificates of the chain, e.g. the ones sent with the certificate.</param>
    /// <param name="anchors">Trusted certification authorities, usually <see cref="TslService.TrustAnchors"/>.</param>
    /// <param name="problem">Why the certificate is not trusted.</param>
    /// <param name="verificationOnly">
    /// Certificates of <paramref name="anchors"/> that are only there to verify the authority above them and must
    /// not issue the certificate themselves, usually <see cref="TslService.VerificationRoots"/>.
    /// </param>
    public static CertificateTrustSource Check(X509Certificate2 certificate, IEnumerable<X509Certificate2> intermediates,
        X509Certificate2Collection anchors, out string? problem, CertificateRevocationPolicy? revocation = null,
        X509Certificate2Collection? verificationOnly = null)
    {
        var extra = intermediates.ToList();
        var policy = revocation ?? new CertificateRevocationPolicy();
        problem = null;

        if (anchors.Count > 0)
        {
            if (Build(certificate, extra, anchors, policy, verificationOnly, out problem))
                return CertificateTrustSource.Tsl;
        }

        if (Build(certificate, extra, null, policy, verificationOnly: null, out var fromSystem))
            return CertificateTrustSource.System;

        problem ??= fromSystem;
        return CertificateTrustSource.None;
    }

    /// <summary>
    /// Whether the issuer of the certificate put it on a revocation list (OP08 2.6). A certificate that is trusted
    /// by itself (self signed, stored here by the administrator) has no list and is never revoked this way, so the
    /// answer is <c>false</c> for it as well as for a certificate whose chain cannot be built at all.
    /// </summary>
    /// <param name="problem">What the revocation lists say about the certificate.</param>
    public static bool IsRevoked(X509Certificate2 certificate, IEnumerable<X509Certificate2> intermediates,
        X509Certificate2Collection anchors, CertificateRevocationPolicy? revocation, out string? problem)
    {
        var policy = revocation ?? new CertificateRevocationPolicy();
        problem = null;
        if (!policy.Check)
            return false;

        var extra = intermediates.ToList();
        return IsRevoked(certificate, extra, anchors.Count > 0 ? anchors : null, policy, out problem) ||
               IsRevoked(certificate, extra, null, policy, out problem);
    }

    private static bool IsRevoked(X509Certificate2 certificate, IReadOnlyList<X509Certificate2> intermediates,
        X509Certificate2Collection? anchors, CertificateRevocationPolicy revocation, out string? problem)
    {
        using var chain = new X509Chain();
        revocation.ApplyTo(chain.ChainPolicy);
        chain.ChainPolicy.ExtraStore.AddRange(intermediates.ToArray());
        if (anchors is not null)
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(anchors);
        }

        chain.Build(certificate);
        var revoked = chain.ChainStatus.Any(s => (s.Status & X509ChainStatusFlags.Revoked) != 0);
        problem = revoked ? CertificateRevocationPolicy.Describe(chain.ChainStatus) : null;
        return revoked;
    }

    private static bool Build(X509Certificate2 certificate, IReadOnlyList<X509Certificate2> intermediates,
        X509Certificate2Collection? anchors, CertificateRevocationPolicy revocation,
        X509Certificate2Collection? verificationOnly, out string? problem)
    {
        using var chain = new X509Chain();
        revocation.ApplyTo(chain.ChainPolicy);
        chain.ChainPolicy.ExtraStore.AddRange(intermediates.ToArray());
        if (anchors is not null)
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(anchors);
        }

        if (!chain.Build(certificate))
        {
            problem = CertificateRevocationPolicy.Describe(chain.ChainStatus);
            return false;
        }

        // A chain that only reaches a certificate the trust list carries to verify an authority is not enough: the
        // certificate has to come from the authority itself (Odette OP08 2.7).
        if (anchors is not null && verificationOnly is { Count: > 0 } && !IssuedByAnAuthority(chain, anchors, verificationOnly))
        {
            problem = "The certificate was not issued by a certification authority of the trust list, but by a " +
                      "certificate that is listed only to verify one.";
            return false;
        }

        problem = null;
        return true;
    }

    private static bool IssuedByAnAuthority(X509Chain chain, X509Certificate2Collection anchors,
        X509Certificate2Collection verificationOnly)
    {
        for (var i = 1; i < chain.ChainElements.Count; i++)
        {
            var element = chain.ChainElements[i].Certificate;
            if (Contains(anchors, element) && !Contains(verificationOnly, element))
                return true;
        }

        return false;
    }

    private static bool Contains(X509Certificate2Collection collection, X509Certificate2 certificate) =>
        collection.Any(c => c.RawDataMemory.Span.SequenceEqual(certificate.RawDataMemory.Span));
}
