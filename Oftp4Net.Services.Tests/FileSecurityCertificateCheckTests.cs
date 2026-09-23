using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Oftp4Net.Services;
using Oftp4Net.Services.Oftp;
using Oftp4Net.Services.Tsl;

namespace Oftp4Net.Services.Tests;

/// <summary>
/// A certificate that expired or was revoked by its issuer must not be used for file level security any more
/// (Odette OP08 2.6 and 1.10, interoperability test case 7.1).
/// </summary>
public class FileSecurityCertificateCheckTests
{
    private static X509Certificate2 CreateCertificate(bool expired = false)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Station", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return expired
            ? request.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-2), DateTimeOffset.UtcNow.AddDays(-1))
            : request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    /// <summary>The check reads no certificate from the database, so the repository is not needed here.</summary>
    private static SessionFileSecurity Create() => new(null!, new GlobalSettings());

    [Fact]
    public void ExpiredCertificateIsNotUsed()
    {
        using var certificate = CreateCertificate(expired: true);
        using var security = Create();

        var problem = security.CheckUsable(certificate);

        Assert.NotNull(problem);
        Assert.Contains("valid from", problem);
    }

    [Fact]
    public void CertificateStoredByTheAdministratorIsUsed()
    {
        // A self signed certificate has no revocation list; it is trusted because it is configured here.
        using var certificate = CreateCertificate();
        using var security = Create();

        Assert.Null(security.CheckUsable(certificate));
        Assert.Null(security.CheckUsable(null));
    }

    [Fact]
    public void CertificateWithoutRevocationInformationIsNotTakenAsRevoked()
    {
        using var certificate = CreateCertificate();

        var revoked = CertificateTrust.IsRevoked(certificate, [], [], new Core.Transport.CertificateRevocationPolicy(),
            out var problem);

        Assert.False(revoked);
        Assert.Null(problem);
    }
}
