using System.Security.Cryptography.X509Certificates;

namespace Oftp4Net.Core.Transport;

/// <summary>
/// How the revocation of a certificate is checked. OFTP2 partners exchange long living certificates, so the
/// Odette certificate policy expects the revocation lists (CRL) of their issuers to be read regularly; a
/// certificate that appears on one must not be used any more.
/// </summary>
public sealed record CertificateRevocationPolicy
{
    /// <summary>Revocation lists are read and a revoked certificate is refused.</summary>
    public bool Check { get; init; } = true;

    /// <summary>
    /// A certificate is refused when its revocation state cannot be found out at all (the list is unreachable or
    /// the issuer publishes none). Off by default, so that an unreachable list does not stop the transfers.
    /// </summary>
    public bool Require { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Checks nothing, for a pinned certificate that is trusted by itself.</summary>
    public static readonly CertificateRevocationPolicy None = new() { Check = false };

    /// <summary>Applies the policy to a chain that is about to be built.</summary>
    public void ApplyTo(X509ChainPolicy policy)
    {
        if (!Check)
        {
            policy.RevocationMode = X509RevocationMode.NoCheck;
            return;
        }

        policy.RevocationMode = X509RevocationMode.Online;
        // The root of the chain signs no revocation list for itself.
        policy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        policy.UrlRetrievalTimeout = Timeout;

        if (!Require)
        {
            policy.VerificationFlags |= X509VerificationFlags.IgnoreEndRevocationUnknown
                                        | X509VerificationFlags.IgnoreCertificateAuthorityRevocationUnknown
                                        | X509VerificationFlags.IgnoreRootRevocationUnknown;
        }
    }

    /// <summary>
    /// Describes the problem of a chain that could not be built, or <c>null</c> when the only complaint is a
    /// revocation state that could not be found out and that is tolerated.
    /// </summary>
    public static string? Describe(X509ChainStatus[] status)
    {
        if (status.Length == 0)
            return null;

        var revoked = status.Any(s => s.Status.HasFlag(X509ChainStatusFlags.Revoked));
        var problem = string.Join(", ", status
            .Select(s => s.StatusInformation.Trim())
            .Where(s => s.Length > 0)
            .Distinct());

        return revoked ? $"the certificate is revoked ({problem})" : problem.Length > 0 ? problem : "the chain is not valid";
    }
}
