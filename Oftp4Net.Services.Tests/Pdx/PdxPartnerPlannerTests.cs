using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using Oftp4Net.Domain;
using Oftp4Net.Services.Pdx;

namespace Oftp4Net.Services.Tests.Pdx;

public class PdxPartnerPlannerTests
{
    private static readonly DateTime Now = new(2024, 1, 15, 12, 0, 0);

    private static PdxPlanContext Context(StationProfile? profile = null) => new()
    {
        Profile = profile ?? new StationProfile(),
        HasTlsClientCertificate = true,
        HasFileSecurityCertificate = true,
        Now = Now,
    };

    private static PdxDocument Document(string sample, Func<string, string>? edit = null)
    {
        var xml = Encoding.UTF8.GetString(PdxParserTests.Sample(sample));
        var result = PdxParser.Parse(Encoding.UTF8.GetBytes(edit?.Invoke(xml) ?? xml));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        return result.Document!;
    }

    /// <summary>Applies the plan to a new partner; certificates are created as the importer would do it.</summary>
    private static Partner Apply(PdxImportPlan plan, Partner? partner = null)
    {
        partner ??= new Partner();
        plan.Apply(partner, c => new Certificate { Name = c.Name, Base64Data = Convert.ToBase64String(c.Certificate) });
        return partner;
    }

    [Fact]
    public void NewPartnerIsCreatedFromTheSimpleSample()
    {
        var plan = PdxPartnerPlanner.Plan(Document("B"), null, Context());

        Assert.True(plan.CanApply, string.Join(Environment.NewLine, plan.Errors));
        var partner = Apply(plan);

        Assert.Equal("Wiley E. Coyote Corporation", partner.Name);
        Assert.Equal("O017700000000COYOTE000001", partner.SSID);
        Assert.Equal(partner.SSID, partner.SFID);
        Assert.Equal("MEEPMEEP", partner.Password);
        Assert.Equal("oftp.wileyecoyote.example", partner.Host);
        Assert.Equal(6619, partner.Port);
        Assert.True(partner.UseTls);
        Assert.Equal(SslProtocols.Tls12, partner.Tls);

        // Our profile is "optional" everywhere: only what the partner prefers is used.
        Assert.False(partner.SignFiles);
        Assert.False(partner.EncryptFiles);
        Assert.True(partner.CompressFiles);
        Assert.True(partner.RequestSignedEndResponse);
        Assert.False(partner.RequireSignedFiles);
        Assert.True(partner.RequireCompressedFiles);
        Assert.False(partner.SecureAuthentication);

        Assert.Equal("tlscert", partner.TrustedCertificate?.Name);
        Assert.Equal("tlscert", partner.SecurityCertificate?.Name);
        Assert.Equal("CAD", Assert.Single(partner.SubStations).Name);
        Assert.Equal(Guid.Parse("a7c6e9ab-97a2-4fb4-9100-c837ca13cea4"), partner.SetupDocumentId);
    }

    [Fact]
    public void ComplexSampleEnablesRequiredFeatures()
    {
        var plan = PdxPartnerPlanner.Plan(Document("C"), null, Context());

        Assert.True(plan.CanApply, string.Join(Environment.NewLine, plan.Errors));
        var partner = Apply(plan);

        Assert.True(partner.EncryptFiles);
        Assert.True(partner.RequireEncryptedFiles);
        Assert.False(partner.SignFiles);
        Assert.True(partner.SecureAuthentication);
        Assert.Equal("02", partner.FileCipherSuite);
        Assert.Equal("filecert", partner.SecurityCertificate?.Name);
        Assert.Equal("tlscert", partner.TrustedCertificate?.Name);
        Assert.Equal(2, partner.Contacts.Count);
        Assert.Equal("US", partner.Country);

        var edi = partner.SubStations.Single(s => s.Name == "EDI");
        Assert.True(edi.SignFiles);
        Assert.True(edi.RequireSignedFiles);
        Assert.Equal("01", partner.SubStations.Single(s => s.Name == "CAD").FileCipherSuite);
    }

    /// <summary>OP09 PDX test 2.8: the partner requires signed files, our profile forbids signing.</summary>
    [Fact]
    public void RequiredFeatureForbiddenByOurProfileIsAConflict()
    {
        var document = Document("B", xml => Regex.Replace(xml,
            "(<ocs:InboundFileSettings>\\s*<ocs:FileSignature usage=\")optional", "${1}required"));

        var plan = PdxPartnerPlanner.Plan(document, null, Context(new StationProfile { OutboundFileSignature = SecurityUsage.Forbidden }));

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Errors, e => e.Contains("Signing of files we send"));
    }

    [Fact]
    public void SubStationUpdateChangesOnlyTheSubStation()
    {
        var existing = Apply(PdxPartnerPlanner.Plan(Document("C"), null, Context()));
        existing.SSID = "O017700000000COYOTEGATEW1";
        existing.Host = "kept.example";
        // Sample C is dated later than sample F (the time zone -05:30).
        existing.SetupDocumentDate = new DateTime(2023, 1, 1);

        var plan = PdxPartnerPlanner.Plan(Document("F"), existing, Context());

        Assert.True(plan.CanApply, string.Join(Environment.NewLine, plan.Errors));
        Assert.Contains(plan.Changes, c => c.Field == "Sub-station O017700000000COYOTE000CAD");
        Assert.DoesNotContain(plan.Changes, c => c.Field is "Host" or "Encrypt sent files" or "Contacts");

        Apply(plan, existing);
        Assert.Equal("kept.example", existing.Host);
        var cad = existing.SubStations.Single(s => s.Name == "CAD");
        Assert.True(cad.EncryptFiles);
        Assert.True(cad.RequireEncryptedFiles);
        // The cipher of the station changes, the one of the sub-station is not mentioned and stays.
        Assert.Equal("04", existing.FileCipherSuite);
        Assert.Equal("01", cad.FileCipherSuite);
        Assert.Contains(existing.SubStations, s => s.Name == "EDI");
    }

    [Fact]
    public void UnchangedDatasheetChangesNothing()
    {
        var existing = Apply(PdxPartnerPlanner.Plan(Document("C"), null, Context()));

        var plan = PdxPartnerPlanner.Plan(Document("C"), existing, Context());

        Assert.Empty(plan.Changes);
        Assert.Contains(plan.Warnings, w => w.Contains("already been applied"));
    }

    [Fact]
    public void OlderDatasheetIsRefused()
    {
        var existing = Apply(PdxPartnerPlanner.Plan(Document("B"), null, Context()));

        var older = Document("B", xml => xml
            .Replace("a7c6e9ab-97a2-4fb4-9100-c837ca13cea4", "11111111-97a2-4fb4-9100-c837ca13cea4")
            .Replace("docdate=\"2023-08-03T11:16:00Z\"", "docdate=\"2023-01-01T00:00:00Z\""));

        Assert.False(PdxPartnerPlanner.Plan(older, existing, Context()).CanApply);
    }

    [Fact]
    public void ReplacedCertificateIsKeptAsPrevious()
    {
        var existing = Apply(PdxPartnerPlanner.Plan(Document("B"), null, Context()));
        var old = existing.SecurityCertificate;

        // Sample C uses another certificate for file security.
        var document = Document("C", xml => xml.Replace("O017700000000COYOTE000001", existing.SSID));
        var plan = PdxPartnerPlanner.Plan(document, existing, Context());
        Apply(plan, existing);

        Assert.Equal("filecert", existing.SecurityCertificate?.Name);
        Assert.Same(old, existing.PreviousSecurityCertificate);
        Assert.Contains(plan.Changes, c => c.Field == "Partner certificate");
    }

    [Fact]
    public void Oftp1PartnerGetsItsReleaseLevel()
    {
        var plan = PdxPartnerPlanner.Plan(Document("D"), null, Context());

        Assert.True(plan.CanApply, string.Join(Environment.NewLine, plan.Errors));
        var partner = Apply(plan);
        Assert.Equal(4, partner.ProtocolLevel);
        Assert.False(partner.UseTls);
        Assert.Equal(3305, partner.Port);
    }

    [Fact]
    public void FileSecurityNeedsOftp2()
    {
        var document = Document("D", xml => xml.Replace(
            "<ocs:FileSignature usage=\"optional\"/>", "<ocs:FileSignature usage=\"required\"/>"));

        var plan = PdxPartnerPlanner.Plan(document, null, Context());

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Errors, e => e.Contains("OFTP 2.0"));
    }

    [Fact]
    public void PartnerWithoutAddressCannotBeCreated()
    {
        var plan = PdxPartnerPlanner.Plan(Document("F"), null, Context());

        Assert.False(plan.CanApply);
    }

    [Fact]
    public void MissingFileSecurityCertificateIsReported()
    {
        var plan = PdxPartnerPlanner.Plan(Document("C"), null, Context() with { HasFileSecurityCertificate = false });

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Errors, e => e.Contains("file security certificate"));
    }

    [Fact]
    public void UntrustedCertificateIsOnlyAWarning()
    {
        // The certificates of the samples are issued by the example "ACME Ltd." CA.
        var plan = PdxPartnerPlanner.Plan(Document("B"), null, Context());

        Assert.Contains(plan.Warnings, w => w.Contains("not issued by a certification authority of the Odette TSL"));
        Assert.True(plan.CanApply);
    }
}
