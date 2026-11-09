using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Oftp4Net.Domain;
using Oftp4Net.Services.Api;
using Oftp4Net.Services.Security;

namespace Oftp4Net.Services.Tests;

public class PartnerProvisioningTests
{
    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0);

    private static readonly Certificate Valid = new() { Id = 1, Name = "valid", ValidTo = Now.AddYears(1) };
    private static readonly Certificate Expired = new() { Id = 2, Name = "expired", ValidTo = Now.AddDays(-1) };

    private static PartnerPlanContext Context(params Partner[] partners) => new(partners, [Valid, Expired],
    [
        new Identity { Id = 1, Name = "ARTIPA", SSID = "OUR", SFID = "OUR" },
        new Identity { Id = 2, Name = "LETOPLAST", SSID = "OUR", SFID = "OTHER" },
    ], 1, Now);

    private static PartnerChangeRequest NewPartner() => new() { Name = "BMW-TEST", Ssid = "O0013000045BMW-INT", Host = "ediint.bmwgroup.com" };

    private static Partner Active() => new()
    {
        Id = 5, Name = "ACTIVE", SSID = "ACT", SFID = "ACT", Host = "active.example.com", SecurityCertificateId = 1, TrustedCertificateId = 1,
    };

    private static Partner Apply(Partner partner, PartnerPlan plan)
    {
        plan.Apply(partner);
        return partner;
    }

    [Fact]
    public void NewPartner_TakesTheSfidFromTheSsidAndIsADraft()
    {
        var plan = PartnerProvisioningService.Plan(null, NewPartner(), Context());

        Assert.True(plan.IsValid, string.Join(", ", plan.Errors));
        Assert.Equal("O0013000045BMW-INT", Apply(new Partner(), plan).SFID);
        Assert.Contains(plan.Warnings, w => w.Contains("draft"));
    }

    [Fact]
    public void NewPartner_NeedsItsRequiredSettings()
    {
        var plan = PartnerProvisioningService.Plan(null, new PartnerChangeRequest { Name = "X" }, Context());

        Assert.False(plan.IsValid);
        Assert.Contains(plan.Errors, e => e.Contains("SSID"));
        Assert.Contains(plan.Errors, e => e.Contains("Host"));
    }

    [Fact]
    public void Plan_LeavesThePartnerUntouched()
    {
        var partner = Active();

        var plan = PartnerProvisioningService.Plan(partner, new PartnerChangeRequest { Host = "new.example.com", Port = 7000 }, Context(partner));

        Assert.Equal(["host", "port"], plan.Changes.Select(c => c.Field));
        Assert.Equal("active.example.com", partner.Host);
        Assert.Equal("new.example.com", Apply(partner, plan).Host);
    }

    [Fact]
    public void WhatIsLeftOutOrTheSameIsNoChange()
    {
        var partner = Active();

        var plan = PartnerProvisioningService.Plan(partner, new PartnerChangeRequest { Host = "active.example.com" }, Context(partner));

        Assert.True(plan.IsValid);
        Assert.Empty(plan.Changes);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("ourPassword")]
    public void Passwords_AreRefused(string field)
    {
        var request = NewPartner() with { UnknownFields = new() { [field] = JsonSerializer.SerializeToElement("x") } };

        var plan = PartnerProvisioningService.Plan(null, request, Context());

        Assert.Contains(plan.Errors, e => e.Contains("passwords are not accepted"));
    }

    [Fact]
    public void FieldsOutsideTheRequest_LandInUnknownFields()
    {
        var request = JsonSerializer.Deserialize<PartnerChangeRequest>("""{"name":"X","password":"secret","draft":false}""", JsonSerializerOptions.Web)!;

        Assert.Equal(["draft", "password"], request.UnknownFields!.Keys.Order());
    }

    [Fact]
    public void DuplicateCodeAndNameAreRefused()
    {
        var other = new Partner { Id = 9, Name = "bmw-test", SSID = "o0013000045bmw-int", SFID = "A", Host = "h" };

        var plan = PartnerProvisioningService.Plan(null, NewPartner(), Context(other));

        Assert.Equal(2, plan.Errors.Count);
    }

    [Fact]
    public void EncryptionNeedsTheCertificateOfThePartner()
    {
        var plan = PartnerProvisioningService.Plan(null, NewPartner() with { EncryptFiles = true }, Context());
        Assert.Contains(plan.Errors, e => e.Contains("securityCertificateId"));

        plan = PartnerProvisioningService.Plan(null, NewPartner() with { EncryptFiles = true, SecurityCertificateId = 1 }, Context());
        Assert.True(plan.IsValid, string.Join(", ", plan.Errors));
    }

    [Fact]
    public void FileSecurityNeedsOftp2()
    {
        var plan = PartnerProvisioningService.Plan(null, NewPartner() with { SignFiles = true, ProtocolLevel = 4 }, Context());

        Assert.Contains(plan.Errors, e => e.Contains("protocol level 5"));
    }

    [Fact]
    public void UnknownCertificateIsRefusedAndAnExpiredOneReported()
    {
        Assert.Contains(PartnerProvisioningService.Plan(null, NewPartner() with { SecurityCertificateId = 77 }, Context()).Errors,
            e => e.Contains("does not exist"));
        Assert.Contains(PartnerProvisioningService.Plan(null, NewPartner() with { SecurityCertificateId = 2 }, Context()).Warnings,
            w => w.Contains("expired"));
    }

    [Fact]
    public void CertificateOfAnActivePartnerGoesThroughACertificateChange()
    {
        var partner = Active();

        var plan = PartnerProvisioningService.Plan(partner, new PartnerChangeRequest { SecurityCertificateId = 2 }, Context(partner));

        Assert.Contains(plan.Errors, e => e.Contains("certificate-changes"));
    }

    [Fact]
    public void CertificateOfADraftCanBeReplacedAndCleared()
    {
        var partner = Active();
        partner.Draft = true;

        var plan = PartnerProvisioningService.Plan(partner, new PartnerChangeRequest { SecurityCertificateId = 2, TrustedCertificateId = 0 }, Context(partner));

        Assert.True(plan.IsValid, string.Join(", ", plan.Errors));
        Apply(partner, plan);
        Assert.Equal(2, partner.SecurityCertificateId);
        Assert.Null(partner.TrustedCertificateId);
    }

    [Fact]
    public void Identity_ByNameOrAnUnambiguousCode()
    {
        var plan = PartnerProvisioningService.Plan(null, NewPartner() with { InboundIdentity = "LETOPLAST" }, Context());
        Assert.Equal(2, Apply(new Partner(), plan).InboundIdentityId);

        plan = PartnerProvisioningService.Plan(null, NewPartner() with { InboundIdentity = "OUR" }, Context());
        Assert.Contains(plan.Errors, e => e.Contains("Several identities"));
    }

    [Fact]
    public void TlsVersions_AreParsed()
    {
        var plan = PartnerProvisioningService.Plan(null, NewPartner() with { TlsVersions = ["1.2", "1.3"] }, Context());
        Assert.Equal(System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13, Apply(new Partner(), plan).Tls);

        Assert.False(PartnerProvisioningService.Plan(null, NewPartner() with { TlsVersions = ["9.9"] }, Context()).IsValid);
        Assert.False(PartnerProvisioningService.Plan(null, NewPartner() with { TlsVersions = [] }, Context()).IsValid);
    }

    [Fact]
    public void CipherSuiteAndProtocolLevelAreChecked()
    {
        Assert.False(PartnerProvisioningService.Plan(null, NewPartner() with { CipherSuite = "99" }, Context()).IsValid);
        Assert.False(PartnerProvisioningService.Plan(null, NewPartner() with { ProtocolLevel = 3 }, Context()).IsValid);
        Assert.Equal("01", Apply(new Partner(), PartnerProvisioningService.Plan(null, NewPartner() with { CipherSuite = "01" }, Context())).FileCipherSuite);
    }

    [Theory]
    [InlineData(SendStatus.NEW, LoopTestStage.Queued)]
    [InlineData(SendStatus.ERROR, LoopTestStage.Retrying)]
    [InlineData(SendStatus.FAILED, LoopTestStage.SendFailed)]
    [InlineData(SendStatus.NOT_DELIVERED, LoopTestStage.Refused)]
    [InlineData(SendStatus.SENT, LoopTestStage.AwaitingEndResponse)]
    [InlineData(SendStatus.DELIVERED, LoopTestStage.AwaitingReturn)]
    public void LoopTest_WithoutAReturnedFile(SendStatus status, string stage) =>
        Assert.Equal(stage, LoopTestStage.From(status, []));

    [Fact]
    public void LoopTest_AGoodReturnedFileWinsOverAFailedOne()
    {
        Assert.Equal(LoopTestStage.ReturnFailed, LoopTestStage.From(SendStatus.DELIVERED, [ReceiveStatus.FAILED]));
        Assert.Equal(LoopTestStage.Returned, LoopTestStage.From(SendStatus.DELIVERED, [ReceiveStatus.FAILED, ReceiveStatus.CONFIRMED]));
        Assert.Equal(LoopTestStage.AwaitingReturn, LoopTestStage.From(SendStatus.DELIVERED, [ReceiveStatus.RECEIVING]));
    }

    [Fact]
    public void Scopes_APartnersTokenIsNotADefaultOne()
    {
        Assert.False(ApiTokenScopes.Default.HasFlag(ApiTokenScopes.Partners));
        Assert.Contains(ApiTokenScopes.Partners, (ApiTokenScopes.Partners | ApiTokenScopes.Inbox).Each());
    }

    private static (X509Certificate2 Certificate, RSA Key) CreateCertificate()
    {
        var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Partner", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return (request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1)), key);
    }

    [Fact]
    public void PublicCertificate_PemAndDerAreLoaded()
    {
        var (certificate, key) = CreateCertificate();
        using (key)
        using (certificate)
        {
            using var der = CertificateChangeService.LoadPublicCertificate(certificate.RawData, "a.cer");
            using var pem = CertificateChangeService.LoadPublicCertificate(Encoding.ASCII.GetBytes(certificate.ExportCertificatePem()), "a.pem");
            Assert.Equal(certificate.Thumbprint, der.Thumbprint);
            Assert.Equal(certificate.Thumbprint, pem.Thumbprint);
        }
    }

    [Fact]
    public void PublicCertificate_APemWithItsKeyIsRefused()
    {
        var (certificate, key) = CreateCertificate();
        using (key)
        using (certificate)
        {
            var data = Encoding.ASCII.GetBytes(certificate.ExportCertificatePem() + "\n" + key.ExportPkcs8PrivateKeyPem());

            Assert.Throws<InvalidOperationException>(() => CertificateChangeService.LoadPublicCertificate(data, "a.pem"));
        }
    }

    [Fact]
    public void PublicCertificate_SomethingElseIsRefused() =>
        Assert.Throws<InvalidOperationException>(() => CertificateChangeService.LoadPublicCertificate("hello"u8.ToArray(), "a.txt"));
}
