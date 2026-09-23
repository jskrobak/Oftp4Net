using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Oftp4Net.Domain;
using Oftp4Net.Services.Pdx;

namespace Oftp4Net.Services.Tests.Pdx;

public class PdxExportTests
{
    private static X509Certificate2 Certificate(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
    }

    [Theory]
    [InlineData("B")]
    [InlineData("C")]
    [InlineData("D")]
    [InlineData("F")]
    public void OdetteSamplesAreWrittenAsTheyAreRead(string sample)
    {
        var original = PdxParser.Parse(PdxParserTests.Sample(sample)).Document!;

        var written = PdxParser.Parse(PdxWriter.Write(original));

        Assert.True(written.Success, string.Join(Environment.NewLine, written.Errors));
        var copy = written.Document!;
        Assert.Equal(original.DocId, copy.DocId);
        Assert.Equal(original.DocDate, copy.DocDate);
        Assert.Equal(original.ValidFrom, copy.ValidFrom);
        Assert.Equal(original.Session, copy.Session with { SecureAuthentication = original.Session.SecureAuthentication });
        Assert.Equal(original.Session.SecureAuthentication?.Usage, copy.Session.SecureAuthentication?.Usage);
        Assert.Equal(original.InboundConnections.Select(c => (c.ServerHost, c.ServerPort, c.CertificateRef, c.TlsClientAuth)),
            copy.InboundConnections.Select(c => (c.ServerHost, c.ServerPort, c.CertificateRef, c.TlsClientAuth)));
        Assert.Equal(original.InboundFileSettings?.FileEncryption.CertificateRefs, copy.InboundFileSettings?.FileEncryption.CertificateRefs);
        Assert.Equal(original.SubStations.Select(s => (s.Sfid, s.CipherSetting?.PrimaryCipher)),
            copy.SubStations.Select(s => (s.Sfid, s.CipherSetting?.PrimaryCipher)));
        Assert.Equal(original.Station.Contacts?.Count, copy.Station.Contacts?.Count);
        Assert.Equal(original.Certificates.Select(c => (c.Name, c.Certificate.Length, c.CaCertificates.Count)),
            copy.Certificates.Select(c => (c.Name, c.Certificate.Length, c.CaCertificates.Count)));
    }

    private static OwnStation Station(X509Certificate2? fileCertificate = null) => new()
    {
        Identity = new Identity { Name = "Acme", SSID = "O0013ACME", SFID = "O0013ACME", Password = "SECRET" },
        SubStations = [new Identity { Name = "Plant 2", SSID = "O0013ACME", SFID = "O0013ACMEPLANT2" }],
        Profile = new StationProfile
        {
            CompanyName = "Acme Ltd.",
            City = "Praha",
            Country = "cz",
            PublicHost = "oftp.acme.example",
            ListenerId = 1,
            InboundFileEncryption = SecurityUsage.Required,
            OutboundFileSignature = SecurityUsage.Preferred,
            SecureAuthentication = SecurityUsage.Required,
            Contacts = [new PartnerContact { Name = "IT", Emails = ["it@acme.example"] }],
        },
        Listener = new Listener { Id = 1, Port = 6619, UseTls = true, Tls = SslProtocols.Tls12 | SslProtocols.Tls13 },
        TlsCertificate = Certificate("CN=oftp.acme.example"),
        FileCertificate = fileCertificate,
        ValidFrom = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void OwnDatasheetIsValidAndCanBeImported()
    {
        using var file = Certificate("CN=Acme file security");
        var (document, warnings) = PdxDatasheetBuilder.Build(Station(file));

        Assert.Empty(warnings);
        var parsed = PdxParser.Parse(PdxWriter.Write(document));
        Assert.True(parsed.Success, string.Join(Environment.NewLine, parsed.Errors));

        // The partner imports it with its own (default, all optional) profile.
        var plan = PdxPartnerPlanner.Plan(parsed.Document!, null, new PdxPlanContext
        {
            Profile = new StationProfile(),
            HasFileSecurityCertificate = true,
            HasTlsClientCertificate = true,
        });
        Assert.True(plan.CanApply, string.Join(Environment.NewLine, plan.Errors));

        var partner = new Partner();
        plan.Apply(partner, c => new Certificate { Name = c.Name });
        Assert.Equal("O0013ACME", partner.SSID);
        Assert.Equal("SECRET", partner.Password);
        Assert.Equal("oftp.acme.example", partner.Host);
        Assert.Equal(6619, partner.Port);
        Assert.True(partner.EncryptFiles);
        Assert.True(partner.RequireSignedFiles);
        Assert.True(partner.SecureAuthentication);
        Assert.Equal("file", partner.SecurityCertificate?.Name);
        Assert.Equal("tls", partner.TrustedCertificate?.Name);
        Assert.Equal("CZ", partner.Country);
        Assert.Equal("O0013ACMEPLANT2", Assert.Single(partner.SubStations).SFID);
        Assert.Equal(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), parsed.Document!.ValidFrom);
    }

    [Fact]
    public void FeaturesNeedingACertificateAreForbiddenWithoutIt()
    {
        var (document, warnings) = PdxDatasheetBuilder.Build(Station());

        Assert.Equal(SecurityUsage.Forbidden, document.InboundFileSettings!.FileEncryption.Usage);
        Assert.Equal(SecurityUsage.Forbidden, document.Session.SecureAuthentication!.Usage);
        Assert.Contains(warnings, w => w.Contains("Encryption of files we receive"));
        Assert.True(PdxParser.Parse(PdxWriter.Write(document)).Success);
    }

    [Fact]
    public void MissingPublicHostIsReported()
    {
        var station = Station();
        var (document, warnings) = PdxDatasheetBuilder.Build(station with { Profile = new StationProfile { ListenerId = 1 } });

        Assert.Empty(document.InboundConnections);
        Assert.Contains(warnings, w => w.Contains("public host"));
    }
}

public class PdxVersionTests
{
    [Fact]
    public void Version11IsWrittenAndValidated()
    {
        var original = PdxParser.Parse(PdxParserTests.Sample("C")).Document!;
        var withCipher07 = original with { CipherSetting = new PdxCipherSetting("07", ["04"]) };

        var content = PdxWriter.Write(withCipher07, "1.1");
        var xml = System.Text.Encoding.UTF8.GetString(content);

        Assert.Contains(PdxParser.Namespace11, xml);
        Assert.Contains("version=\"1.1\"", xml);
        var parsed = PdxParser.Parse(content);
        Assert.True(parsed.Success, string.Join(Environment.NewLine, parsed.Errors));
        Assert.Empty(parsed.Warnings);
        Assert.Equal("1.1", parsed.Document!.Version);
        // Cipher suite 07 does not exist in 1.1.
        Assert.Equal("04", parsed.Document.CipherSetting!.PrimaryCipher);
        Assert.Empty(parsed.Document.CipherSetting.AlternateCiphers);
    }

    [Fact]
    public void InvalidVersion11IsRefused()
    {
        var xml = System.Text.Encoding.UTF8.GetString(PdxWriter.Write(PdxParser.Parse(PdxParserTests.Sample("B")).Document!, "1.1"))
            .Replace("usage=\"optional\"", "usage=\"sometimes\"");

        Assert.False(PdxParser.Parse(System.Text.Encoding.UTF8.GetBytes(xml)).Success);
    }

    [Fact]
    public void PartnerGetsTheVersionOfItsDatasheet()
    {
        var document = PdxParser.Parse(PdxWriter.Write(PdxParser.Parse(PdxParserTests.Sample("B")).Document!, "1.1")).Document!;

        var plan = PdxPartnerPlanner.Plan(document, null, new PdxPlanContext
        {
            Profile = new StationProfile(),
            HasFileSecurityCertificate = true,
            HasTlsClientCertificate = true,
        });

        var partner = new Partner();
        plan.Apply(partner, c => new Certificate { Name = c.Name });
        Assert.Equal("1.1", partner.PdxVersion);
    }
}

public class PdxDateTests
{
    [Fact]
    public void DatesAreWrittenInUtc()
    {
        var original = PdxParser.Parse(PdxParserTests.Sample("C")).Document!;
        var document = original with { ValidFrom = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.FromHours(2)) };

        var xml = System.Text.Encoding.UTF8.GetString(PdxWriter.Write(document));

        Assert.Contains("validfrom=\"2030-01-01T10:00:00+00:00\"", xml);
        Assert.Equal(document.ValidFrom, PdxParser.Parse(System.Text.Encoding.UTF8.GetBytes(xml)).Document!.ValidFrom);
    }
}
