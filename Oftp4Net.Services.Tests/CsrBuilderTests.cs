using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Oftp4Net.Services.Certificates;

namespace Oftp4Net.Services.Tests;

public class CsrBuilderTests
{
    private static CsrOptions Options() => new()
    {
        CommonName = "oftp.example.com",
        Organization = "Example Ltd.",
        OrganizationalUnit = "IT",
        Locality = "Praha",
        Country = "cz",
        OdetteId = "O0013EXAMPLE",
        Email = "oftp@example.com",
        AlternativeNames = ["oftp2.example.com", "192.0.2.10"],
        KeySize = 2048,
    };

    /// <summary>A certification authority signing requests the way a real one would (with the extensions asked for).</summary>
    private static (X509Certificate2 Ca, X509Certificate2 Signed) Sign(string csrPem)
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=Test OFTP2 CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var ca = caRequest.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));

        var request = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256,
            CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions, RSASignaturePadding.Pkcs1);
        var signed = request.Create(ca, DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1), [1, 2, 3, 4]);
        return (ca, signed);
    }

    [Fact]
    public void RequestHasTheOftp2Profile()
    {
        var (csr, key) = CsrBuilder.Create(Options());
        using var _ = key;

        var request = CertificateRequest.LoadSigningRequestPem(csr, HashAlgorithmName.SHA256,
            CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);

        var subject = request.SubjectName.Name;
        Assert.Contains("CN=oftp.example.com", subject);
        Assert.Contains("O=Example Ltd.", subject);
        Assert.Contains("C=CZ", subject);
        Assert.Contains("SERIALNUMBER=O0013EXAMPLE", subject);

        var usage = request.CertificateExtensions.OfType<X509KeyUsageExtension>().Single();
        Assert.Equal(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, usage.KeyUsages);

        var enhanced = request.CertificateExtensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.False(enhanced.Critical);
        Assert.Equal(["1.3.6.1.5.5.7.3.1", "1.3.6.1.5.5.7.3.2"], enhanced.EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value));

        var names = request.CertificateExtensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Equal(["oftp.example.com", "oftp2.example.com"], names.EnumerateDnsNames());
        Assert.Equal("192.0.2.10", Assert.Single(names.EnumerateIPAddresses()).ToString());
        Assert.Equal(2048, key.KeySize);
    }

    [Fact]
    public void SignedCertificateInPemWithChainIsMatchedWithTheKey()
    {
        var (csr, key) = CsrBuilder.Create(Options());
        using var _ = key;
        var (ca, signed) = Sign(csr);

        var pem = signed.ExportCertificatePem() + "\n" + ca.ExportCertificatePem();
        var (certificate, chain) = CsrBuilder.ReadSigned(Encoding.ASCII.GetBytes(pem), key);

        Assert.Equal(signed.Thumbprint, certificate.Thumbprint);
        Assert.Equal(ca.Thumbprint, Assert.Single(chain).Thumbprint);
        using var withKey = certificate.CopyWithPrivateKey(key);
        Assert.True(withKey.HasPrivateKey);
    }

    [Fact]
    public void SignedCertificateInDerAndPkcs7IsRead()
    {
        var (csr, key) = CsrBuilder.Create(Options());
        using var _ = key;
        var (ca, signed) = Sign(csr);

        Assert.Equal(signed.Thumbprint, CsrBuilder.ReadSigned(signed.RawData, key).Certificate.Thumbprint);

        var pkcs7 = new X509Certificate2Collection(new[] { ca, signed }).Export(X509ContentType.Pkcs7)!;
        var (fromBundle, chain) = CsrBuilder.ReadSigned(pkcs7, key);
        Assert.Equal(signed.Thumbprint, fromBundle.Thumbprint);
        Assert.Single(chain);
    }

    /// <summary>What the service stores: the PKCS#12 with the chain loads as our certificate with its key.</summary>
    [Fact]
    public void StoredBundleLoadsAsTheCertificateWithKey()
    {
        var (csr, key) = CsrBuilder.Create(Options());
        using var _ = key;
        var (ca, signed) = Sign(csr);
        using var withKey = signed.CopyWithPrivateKey(key);

        var bundle = new X509Certificate2Collection(withKey);
        bundle.Add(ca);
        var pfx = bundle.Export(X509ContentType.Pkcs12, "secret")!;

        var entity = Oftp4Net.Services.Oftp.CertificateLoader.CreateEntity(pfx, "request.pfx", "secret");
        using var loaded = Oftp4Net.Services.Oftp.CertificateLoader.Load(entity);

        Assert.True(entity.HasPrivateKey);
        Assert.Equal(signed.Thumbprint, loaded.Thumbprint);
        Assert.True(loaded.HasPrivateKey);
    }

    [Fact]
    public void CertificateOfAnotherKeyIsRefused()
    {
        var (csr, key) = CsrBuilder.Create(Options());
        key.Dispose();
        var (_, signed) = Sign(csr);
        using var otherKey = RSA.Create(2048);

        Assert.Throws<CryptographicException>(() => CsrBuilder.ReadSigned(signed.RawData, otherKey));
    }

    [Theory]
    [InlineData("", 2048)]
    [InlineData("oftp.example.com", 1024)]
    public void InvalidOptionsAreRefused(string commonName, int keySize)
    {
        Assert.Throws<ArgumentException>(() => CsrBuilder.Create(new CsrOptions { CommonName = commonName, KeySize = keySize }));
    }
}
