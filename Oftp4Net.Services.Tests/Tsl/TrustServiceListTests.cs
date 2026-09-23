using System.Text;
using Oftp4Net.Services.Tsl;

namespace Oftp4Net.Services.Tests.Tsl;

public class TrustServiceListTests
{
    /// <summary>The Odette test TSL (http://www.odette.org/TSL/TSL_Test.xml) as published, sequence 151.</summary>
    private static byte[] Sample() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Tsl", "Samples", "TSL_Test.xml"));

    [Fact]
    public void OdetteTestListIsRead()
    {
        var list = TrustServiceList.Parse(Sample());

        Assert.Equal("Test TSL", list.SchemeName);
        Assert.Equal(151, list.SequenceNumber);
        Assert.Contains("Odette", list.Signer.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(46, list.Providers.Count);
        Assert.Equal(44, list.Providers.Count(p => p.Trusted));
        Assert.NotEmpty(list.TrustAnchors);
    }

    [Fact]
    public void ChangedListIsRefused()
    {
        var xml = Encoding.UTF8.GetString(Sample()).Replace("Aventum CA", "Evil CA");

        var ex = Assert.Throws<TslException>(() => TrustServiceList.Parse(Encoding.UTF8.GetBytes(xml)));
        Assert.Contains("not valid", ex.Message);
    }

    [Fact]
    public void UnsignedListIsRefused()
    {
        var xml = Encoding.UTF8.GetString(Sample());
        var start = xml.IndexOf("<Signature ", StringComparison.Ordinal);
        var end = xml.IndexOf("</Signature>", StringComparison.Ordinal) + "</Signature>".Length;
        Assert.True(start > 0 && end > start);

        Assert.Throws<TslException>(() => TrustServiceList.Parse(Encoding.UTF8.GetBytes(xml.Remove(start, end - start))));
    }

    [Fact]
    public void OtherXmlIsRefused()
    {
        Assert.Throws<TslException>(() => TrustServiceList.Parse("<Invoice/>"u8.ToArray()));
    }
}

public class CertificateTrustTests
{
    private static TrustServiceList List() => TrustServiceList.Parse(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Tsl", "Samples", "TSL_Test.xml")));

    [Fact]
    public void CertificateOfTheListIsTrusted()
    {
        var list = List();
        var certificate = list.Providers.First(p => p.Trusted && p.Certificates.Any(c => c.NotAfter > DateTime.Now))
            .Certificates.First(c => c.NotAfter > DateTime.Now);

        Assert.Equal(CertificateTrustSource.Tsl, CertificateTrust.Check(certificate, [], list.TrustAnchors, out _));
    }

    [Fact]
    public void SelfSignedCertificateIsNotTrusted()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Stranger", key,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));

        Assert.Equal(CertificateTrustSource.None, CertificateTrust.Check(certificate, [], List().TrustAnchors, out var problem));
        Assert.False(string.IsNullOrEmpty(problem));
    }
}
