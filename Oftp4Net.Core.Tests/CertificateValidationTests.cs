using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Oftp4Net.Core.Transport;

namespace Oftp4Net.Core.Tests;

/// <summary>
/// Which remote certificates are accepted in TLS. The Odette trust list carries the root of every listed
/// certification authority so that the authority itself can be verified; a certificate issued directly by such a
/// root is not a valid OFTP2 certificate (Odette OP08 2.7, interoperability test case 7.3).
/// </summary>
public class CertificateValidationTests
{
    /// <summary>An authority with its private key, so that it can issue the certificates below it.</summary>
    private static X509Certificate2 CreateAuthority(string subject, X509Certificate2? issuer = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        if (issuer is null)
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));

        using var issued = request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(3),
            Guid.NewGuid().ToByteArray());
        return issued.CopyWithPrivateKey(key);
    }

    private static X509Certificate2 CreateEndEntity(string subject, X509Certificate2 issuer)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));

        return request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1),
            Guid.NewGuid().ToByteArray());
    }

    private static bool Validate(X509Certificate2 leaf, X509Certificate2Collection trusted,
        X509Certificate2Collection? verificationOnly, X509Certificate2Collection? intermediates = null)
    {
        // The certificate is not known to the operating system, so the handshake reports a chain error.
        var errors = SslPolicyErrors.RemoteCertificateChainErrors;
        var chain = new X509Certificate2Collection(trusted);
        if (intermediates is not null)
            chain.AddRange(intermediates);

        return OftpCertificateValidator.Validate(leaf, errors, chain, certificateRequired: true,
            new CertificateRevocationPolicy { Check = false }, verificationOnly);
    }

    [Fact]
    public void CertificateOfAListedAuthorityIsAccepted()
    {
        using var root = CreateAuthority("CN=Test Root CA");
        using var authority = CreateAuthority("CN=Test OFTP2 CA", root);
        using var partner = CreateEndEntity("CN=partner.example.com", authority);

        // The list carries both, the root only to verify the authority.
        Assert.True(Validate(partner, [authority, root], [root]));
    }

    [Fact]
    public void CertificateIssuedDirectlyByTheRootIsRefused()
    {
        using var root = CreateAuthority("CN=Test Root CA");
        using var authority = CreateAuthority("CN=Test OFTP2 CA", root);
        using var partner = CreateEndEntity("CN=partner.example.com", root);

        // The chain is sound, but the root may only verify the authority, not issue certificates itself.
        Assert.False(Validate(partner, [authority, root], [root]));
        // Without that restriction the same certificate is accepted.
        Assert.True(Validate(partner, [authority, root], null));
    }

    [Fact]
    public void CertificateOfAPartnersOwnAuthorityIsAccepted()
    {
        using var authority = CreateAuthority("CN=Partner CA");
        using var partner = CreateEndEntity("CN=partner.example.com", authority);

        // A partner's own authority is trusted as it is; the restriction applies to the trust list only.
        Assert.True(Validate(partner, [authority], []));
    }
}
