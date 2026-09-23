using System.Security.Cryptography.X509Certificates;
using Oftp4Net.Core.Transport;

namespace Oftp4Net.Services.Tests;

public class CertificateRevocationPolicyTests
{
    private static X509ChainPolicy Apply(CertificateRevocationPolicy policy)
    {
        var chainPolicy = new X509ChainPolicy();
        policy.ApplyTo(chainPolicy);
        return chainPolicy;
    }

    [Fact]
    public void CheckingReadsTheListsAndToleratesAnUnreachableOne()
    {
        var chainPolicy = Apply(new CertificateRevocationPolicy());

        Assert.Equal(X509RevocationMode.Online, chainPolicy.RevocationMode);
        // The root signs no list for itself.
        Assert.Equal(X509RevocationFlag.ExcludeRoot, chainPolicy.RevocationFlag);
        Assert.True(chainPolicy.VerificationFlags.HasFlag(X509VerificationFlags.IgnoreEndRevocationUnknown));
        Assert.True(chainPolicy.VerificationFlags.HasFlag(X509VerificationFlags.IgnoreCertificateAuthorityRevocationUnknown));
    }

    [Fact]
    public void RequiringTheStateDoesNotTolerateAnUnreachableList()
    {
        var chainPolicy = Apply(new CertificateRevocationPolicy { Require = true });

        Assert.Equal(X509RevocationMode.Online, chainPolicy.RevocationMode);
        Assert.False(chainPolicy.VerificationFlags.HasFlag(X509VerificationFlags.IgnoreEndRevocationUnknown));
    }

    [Fact]
    public void SwitchedOffNothingIsDownloaded()
    {
        var chainPolicy = Apply(CertificateRevocationPolicy.None);

        Assert.Equal(X509RevocationMode.NoCheck, chainPolicy.RevocationMode);
        Assert.False(chainPolicy.VerificationFlags.HasFlag(X509VerificationFlags.IgnoreEndRevocationUnknown));
    }

    [Fact]
    public void ARevokedCertificateIsNamedInTheProblem()
    {
        var status = new[]
        {
            new X509ChainStatus { Status = X509ChainStatusFlags.Revoked, StatusInformation = "The certificate is revoked." },
        };

        var problem = CertificateRevocationPolicy.Describe(status);

        Assert.NotNull(problem);
        Assert.Contains("revoked", problem);
    }

    [Fact]
    public void AValidChainHasNoProblem()
    {
        Assert.Null(CertificateRevocationPolicy.Describe([]));
    }
}
