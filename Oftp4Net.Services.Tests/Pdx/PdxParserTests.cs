using System.Text;
using Oftp4Net.Domain;
using Oftp4Net.Services.Pdx;

namespace Oftp4Net.Services.Tests.Pdx;

public class PdxParserTests
{
    /// <summary>Examples published by Odette with the schema (OFTP2 Communication Setup 1.2).</summary>
    internal static byte[] Sample(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Pdx", "Samples", $"{name}-CommunicationSetup.xml"));

    [Theory]
    [InlineData("B")]
    [InlineData("C")]
    [InlineData("D")]
    [InlineData("F")]
    public void OdetteSamplesAreValid(string name)
    {
        var result = PdxParser.Parse(Sample(name));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal("1.2", result.Document!.Version);
    }

    [Fact]
    public void ComplexSampleIsRead()
    {
        var document = PdxParser.Parse(Sample("C")).Document!;

        Assert.Equal(Guid.Parse("c85bbb7d-ef51-4747-ba7d-04f0f3c9af24"), document.DocId);
        Assert.Equal(TimeSpan.FromMinutes(-330), document.DocDate.Offset);
        Assert.Equal("O017700000000COYOTE000001", document.Session.Ssid);
        Assert.Equal("MEEPMEEP", document.Session.Password);
        Assert.Equal(SecurityUsage.Required, document.Session.SecureAuthentication!.Usage);
        Assert.Equal(["filecert"], document.Session.SecureAuthentication.CertificateRefs);

        var inbound = Assert.Single(document.InboundConnections);
        Assert.Equal(PdxConnectionType.Tls, inbound.Type);
        Assert.Equal("oftp.wileyecoyote.example", inbound.ServerHost);
        Assert.Equal(6619, inbound.ServerPort);
        Assert.Equal("tlscert", inbound.CertificateRef);

        Assert.Equal("02", document.CipherSetting!.PrimaryCipher);
        Assert.Equal(["04", "06", "01"], document.CipherSetting.AlternateCiphers);

        Assert.Equal(SecurityUsage.Forbidden, document.OutboundFileSettings!.FileSignature.Usage);
        Assert.Equal(SecurityUsage.Required, document.InboundFileSettings!.FileEncryption.Usage);

        Assert.Equal(2, document.Station.Contacts!.Count);
        Assert.Equal("US", document.Station.Company.Country);

        Assert.Equal(["CAD", "EDI"], document.SubStations.Select(s => s.Name));
        Assert.Equal("01", document.SubStations[0].CipherSetting!.PrimaryCipher);

        var tls = document.FindCertificate("tlscert")!;
        Assert.Equal(2, tls.CaCertificates.Count);
    }

    [Fact]
    public void SchemaViolationIsReported()
    {
        var xml = Encoding.UTF8.GetString(Sample("B")).Replace("usage=\"optional\"", "usage=\"sometimes\"");

        var result = PdxParser.Parse(Encoding.UTF8.GetBytes(xml));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("sometimes"));
    }

    [Fact]
    public void MissingCertificateIsReported()
    {
        var xml = Encoding.UTF8.GetString(Sample("B")).Replace("<ocs:CertificateRef>tlscert</ocs:CertificateRef>",
            "<ocs:CertificateRef>other</ocs:CertificateRef>");

        var result = PdxParser.Parse(Encoding.UTF8.GetBytes(xml));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("'other'"));
    }

    [Fact]
    public void OtherXmlIsRefused()
    {
        var result = PdxParser.Parse("<Invoice xmlns=\"urn:x\"/>"u8.ToArray());

        Assert.False(result.Success);
        Assert.Null(result.Document);
    }

    [Fact]
    public void DocumentTypeDefinitionIsRefused()
    {
        var result = PdxParser.Parse("""
            <?xml version="1.0"?>
            <!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/passwd">]>
            <x>&e;</x>
            """u8.ToArray());

        Assert.False(result.Success);
    }

    [Fact]
    public void LegacyNamespaceIsReadWithWarning()
    {
        var xml = Encoding.UTF8.GetString(Sample("B"))
            .Replace(PdxParser.Namespace12, PdxParser.LegacyNamespace)
            .Replace("version=\"1.2\"", "version=\"1.0\"");

        var result = PdxParser.Parse(Encoding.UTF8.GetBytes(xml));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.NotEmpty(result.Warnings);
        Assert.Equal("O017700000000COYOTE000001", result.Document!.Session.Ssid);
    }
}
