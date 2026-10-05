using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Oftp4Net.Domain;
using Oftp4Net.Services.Import;

namespace Oftp4Net.Services.Tests;

/// <summary>The certificates OS4X trusts for TLS, taken over as trusted for all partners.</summary>
public class Os4xCertificateImportTests
{
    private static readonly DateTime Now = new(2026, 10, 5);

    private static X509Certificate2 Create(string name, DateTime notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}, O=Partner", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(notAfter.AddYears(-2), notAfter);
    }

    private static string Pem(X509Certificate2 certificate) => certificate.ExportCertificatePem();

    [Fact]
    public void ValidCertificateIsSelected()
    {
        using var certificate = Create("oftp2.mahle.com", Now.AddYears(1));

        var candidate = Assert.Single(Os4xCertificateCandidate.Plan([Pem(certificate)], [], Now));

        Assert.Equal("oftp2.mahle.com", candidate.Name);
        Assert.True(candidate.Selected);
        Assert.Equal("new", candidate.Status);
        Assert.Equal(Convert.ToBase64String(certificate.RawData), candidate.Base64Data);
    }

    [Fact]
    public void ExpiredCertificateIsNotSelected()
    {
        using var certificate = Create("old.example.com", Now.AddDays(-1));

        var candidate = Assert.Single(Os4xCertificateCandidate.Plan([Pem(certificate)], [], Now));

        Assert.False(candidate.Selected);
        Assert.True(candidate.CanImport);
        Assert.Contains(candidate.Notes, n => n.Contains("Expired"));
    }

    [Fact]
    public void CertificateThatIsHereAlreadyIsLeftAlone()
    {
        using var certificate = Create("oftp2.volvo.com", Now.AddYears(1));
        var existing = new Certificate
        {
            Name = "Volvo", Base64Data = Convert.ToBase64String(certificate.RawData), HasPrivateKey = false,
        };

        var candidate = Assert.Single(Os4xCertificateCandidate.Plan([Pem(certificate)], [existing], Now));

        Assert.False(candidate.CanImport);
        Assert.False(candidate.Selected);
    }

    [Fact]
    public void SameCertificateTwiceAndUnreadableOnesAreTakenOnceOrLeftOut()
    {
        using var certificate = Create("oftp2.magna.com", Now.AddYears(1));

        var plan = Os4xCertificateCandidate.Plan([Pem(certificate), "not a certificate", Pem(certificate)], [], Now);

        Assert.Equal(2, plan.Count);
        Assert.Single(plan, c => c.CanImport);
    }
}
