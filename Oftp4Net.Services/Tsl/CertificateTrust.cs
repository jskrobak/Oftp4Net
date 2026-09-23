using System.Security.Cryptography.X509Certificates;

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
    public static CertificateTrustSource Check(X509Certificate2 certificate, IEnumerable<X509Certificate2> intermediates,
        X509Certificate2Collection anchors, out string? problem)
    {
        problem = null;
        var extra = intermediates.ToList();

        if (anchors.Count > 0 && Build(certificate, extra, anchors, out _))
            return CertificateTrustSource.Tsl;

        if (Build(certificate, extra, null, out problem))
            return CertificateTrustSource.System;

        return CertificateTrustSource.None;
    }

    private static bool Build(X509Certificate2 certificate, IReadOnlyList<X509Certificate2> intermediates,
        X509Certificate2Collection? anchors, out string? problem)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ExtraStore.AddRange(intermediates.ToArray());
        if (anchors is not null)
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(anchors);
        }

        if (chain.Build(certificate))
        {
            problem = null;
            return true;
        }

        problem = string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()).Where(s => s.Length > 0).Distinct());
        return false;
    }
}
