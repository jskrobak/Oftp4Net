using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Oftp4Net.Services;
using Oftp4Net.Services.Oftp;
using Oftp4Net.Services.Security;

namespace Oftp4Net.Services.Tests;

/// <summary>
/// A certificate that expired or was revoked by its issuer must not be used for file level security any more
/// (Odette OP08 2.6 and 1.10, interoperability test case 7.1).
/// </summary>
public class FileSecurityCertificateCheckTests
{
    private static X509Certificate2 CreateAuthority(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddYears(5));
    }

    private static X509Certificate2 CreateCertificate(bool expired = false, X509Certificate2? issuer = null,
        string? crlUrl = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Station", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (crlUrl is not null)
            request.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([crlUrl]));

        var from = expired ? DateTimeOffset.UtcNow.AddYears(-2) : DateTimeOffset.UtcNow.AddDays(-1);
        var to = expired ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddYears(1);

        return issuer is null
            ? request.CreateSelfSigned(from, to)
            : request.Create(issuer, from, to, Guid.NewGuid().ToByteArray());
    }

    /// <summary>A list of the authority, revoking what is given.</summary>
    private static byte[] CreateCrl(X509Certificate2 authority, params X509Certificate2[] revoked)
    {
        var builder = new CertificateRevocationListBuilder();
        foreach (var certificate in revoked)
            builder.AddEntry(certificate);

        return builder.Build(authority, crlNumber: 1, DateTimeOffset.UtcNow.AddDays(7), HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
    }

    /// <summary>The check reads no certificate from the database, so the repository is not needed here.</summary>
    private static SessionFileSecurity Create() => new(null!, new GlobalSettings());

    [Fact]
    public async Task ExpiredCertificateIsNotUsed()
    {
        using var certificate = CreateCertificate(expired: true);
        using var security = Create();

        var problem = await security.CheckUsableAsync(certificate, CancellationToken.None);

        Assert.NotNull(problem);
        Assert.Contains("valid from", problem);
    }

    [Fact]
    public async Task CertificateStoredByTheAdministratorIsUsed()
    {
        // A self signed certificate has no authority and no revocation list; it is trusted because it is
        // configured here.
        using var certificate = CreateCertificate();
        using var security = Create();

        Assert.Null(await security.CheckUsableAsync(certificate, CancellationToken.None));
        Assert.Null(await security.CheckUsableAsync(null, CancellationToken.None));
    }

    [Fact]
    public void RevocationListNamesTheCertificatesItRevokes()
    {
        using var authority = CreateAuthority("CN=Test CA");
        using var revoked = CreateCertificate(issuer: authority);
        using var valid = CreateCertificate(issuer: authority);

        var list = CertificateRevocationList.Read(CreateCrl(authority, revoked), authority);

        Assert.Equal(1, list.Count);
        Assert.True(list.Revokes(revoked));
        Assert.False(list.Revokes(valid));
        Assert.True(list.NextUpdate > DateTimeOffset.UtcNow);
        Assert.Equal(authority.SubjectName.Name, list.Issuer.Name);
    }

    [Fact]
    public void RevocationListOfAnotherAuthorityIsRefused()
    {
        using var authority = CreateAuthority("CN=Test CA");
        using var other = CreateAuthority("CN=Another CA");
        using var revoked = CreateCertificate(issuer: authority);
        var crl = CreateCrl(authority, revoked);

        // A list that somebody else signed would hide the revocation of the real issuer.
        var ex = Assert.Throws<CrlException>(() => CertificateRevocationList.Read(crl, other));
        Assert.Contains("not valid", ex.Message);
    }

    [Fact]
    public void ChangedRevocationListIsRefused()
    {
        using var authority = CreateAuthority("CN=Test CA");
        using var revoked = CreateCertificate(issuer: authority);
        var crl = CreateCrl(authority, revoked);
        crl[^1] ^= 0xFF;

        Assert.Throws<CrlException>(() => CertificateRevocationList.Read(crl, authority));
    }

    [Fact]
    public void AddressOfTheRevocationListIsRead()
    {
        using var authority = CreateAuthority("CN=Test CA");
        using var certificate = CreateCertificate(issuer: authority, crlUrl: "http://crl.example.com/ca.crl");
        using var without = CreateCertificate(issuer: authority);

        Assert.Equal(["http://crl.example.com/ca.crl"], CertificateRevocationList.DistributionPoints(certificate));
        Assert.Empty(CertificateRevocationList.DistributionPoints(without));
    }
}
