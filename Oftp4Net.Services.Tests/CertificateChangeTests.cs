using Microsoft.Extensions.Diagnostics.HealthChecks;
using Oftp4Net.Domain;
using Oftp4Net.Services.Api;
using Oftp4Net.Services.Health;
using Oftp4Net.Services.Security;

namespace Oftp4Net.Services.Tests;

public class CertificateChangeTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 12, 0, 0);

    [Fact]
    public void Security_KeepsTheReplacedCertificateAsThePreviousOne()
    {
        var partner = new Partner { SecurityCertificateId = 1, PreviousSecurityCertificateId = 7, TrustedCertificateId = null };

        var changed = CertificateChangeService.Apply(partner, 2, PartnerCertificateUsage.Security);

        Assert.Equal(2, partner.SecurityCertificateId);
        Assert.Equal(1, partner.PreviousSecurityCertificateId);
        // The TLS trust stays with the system trust store.
        Assert.Null(partner.TrustedCertificateId);
        Assert.Equal(["file security certificate"], changed);
    }

    [Fact]
    public void Security_ReplacesTheSameCertificateOfStationsAndPurposes()
    {
        var partner = new Partner
        {
            SecurityCertificateId = 1,
            Certificates =
            [
                new CertificateAssignment { Sfid = "SUB1", Usage = CertificateUsage.FileSignature, CertificateId = 1 },
                new CertificateAssignment { Sfid = "SUB2", Usage = CertificateUsage.FileSignature, CertificateId = 5 },
            ],
        };

        CertificateChangeService.Apply(partner, 2, PartnerCertificateUsage.Security);

        Assert.Equal(2, partner.Certificates[0].CertificateId);
        Assert.Equal(1, partner.Certificates[0].PreviousCertificateId);
        // Another certificate of a station is not the partner's one.
        Assert.Equal(5, partner.Certificates[1].CertificateId);
        Assert.Null(partner.Certificates[1].PreviousCertificateId);
    }

    [Fact]
    public void Tls_ChangesOnlyTheTrustedCertificate()
    {
        var partner = new Partner { SecurityCertificateId = 1, TrustedCertificateId = 3 };

        CertificateChangeService.Apply(partner, 4, PartnerCertificateUsage.Tls);

        Assert.Equal(4, partner.TrustedCertificateId);
        Assert.Equal(1, partner.SecurityCertificateId);
        Assert.Null(partner.PreviousSecurityCertificateId);
    }

    [Fact]
    public void SecurityAndTls_ChangesBoth()
    {
        var partner = new Partner { SecurityCertificateId = 1, TrustedCertificateId = 1 };

        CertificateChangeService.Apply(partner, 2, PartnerCertificateUsage.SecurityAndTls);

        Assert.Equal((2, 1, 2), (partner.SecurityCertificateId!.Value, partner.PreviousSecurityCertificateId!.Value, partner.TrustedCertificateId!.Value));
    }

    [Fact]
    public void ACertificateInUseAlready_ChangesNothing()
    {
        var partner = new Partner { SecurityCertificateId = 2, PreviousSecurityCertificateId = 1, TrustedCertificateId = 2 };

        var changed = CertificateChangeService.Apply(partner, 2, PartnerCertificateUsage.SecurityAndTls);

        Assert.Empty(changed);
        Assert.Equal(1, partner.PreviousSecurityCertificateId);
    }

    [Fact]
    public void HealthCheck_ReportsOverdueFailedAndExpiringChanges()
    {
        var overdue = Change(1, Now.AddHours(-1), Certificate(10, Now.AddYears(1)));
        var expiring = Change(2, Now.AddDays(5), Certificate(11, Now.AddDays(3)));
        var failed = Change(3, Now.AddDays(-2), null, CertificateChangeStatus.Failed, "The partner was deleted.");

        var result = CertificateChangesHealthCheck.Evaluate([overdue, expiring], [failed], Now);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("has not been applied", result.Description);
        Assert.Contains("expires before", result.Description);
        Assert.Contains("The partner was deleted.", result.Description);
    }

    [Fact]
    public void HealthCheck_IsHealthyWithAChangeScheduledInTheFuture()
    {
        var result = CertificateChangesHealthCheck.Evaluate([Change(1, Now.AddDays(3), Certificate(10, Now.AddYears(1)))], [], Now);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void ExpiringCertificate_ReplacedInTime_IsNotReported()
    {
        var expiring = Certificate(1, Now.AddDays(10));

        var replaced = CertificatesHealthCheck.Evaluate(
            [new CertificatesHealthCheck.CertificateUse(expiring, "file security certificate of partner P", Now.AddDays(5))], Now);
        var tooLate = CertificatesHealthCheck.Evaluate(
            [new CertificatesHealthCheck.CertificateUse(expiring, "file security certificate of partner P", Now.AddDays(20))], Now);
        var usedElsewhereToo = CertificatesHealthCheck.Evaluate(
        [
            new CertificatesHealthCheck.CertificateUse(expiring, "file security certificate of partner P", Now.AddDays(5)),
            new CertificatesHealthCheck.CertificateUse(expiring, "TLS certificate trusted for partner P"),
        ], Now);

        Assert.Equal(HealthStatus.Healthy, replaced.Status);
        Assert.Equal(HealthStatus.Degraded, tooLate.Status);
        Assert.Equal(HealthStatus.Degraded, usedElsewhereToo.Status);
    }

    [Theory]
    [InlineData("2026-11-06T06:00", 2026, 11, 6, 6)]
    [InlineData("2026-11-06T05:00:00Z", 2026, 11, 6, 6)]
    [InlineData("2026-07-01T04:00:00+00:00", 2026, 7, 1, 6)]
    public void ActivationTime_WithAnOffsetIsAnInstant(string value, int year, int month, int day, int hour)
    {
        Assert.True(new ApplicationTimeService().TryParseTime(value, out var time));
        Assert.Equal(new DateTime(year, month, day, hour, 0, 0), time);
    }

    [Fact]
    public void TokenScopes_InboxWebhookNeedsTheScopeInbox()
    {
        var token = new ApiToken { Enabled = true, Scopes = ApiTokenScopes.Certificates, InboxWebhookUrl = "https://erp.example.com/oftp" };

        Assert.False(ApiTokenService.NotifiedAboutReceivedFiles(token));
        token.Scopes = ApiTokenScopes.Default;
        Assert.True(ApiTokenService.NotifiedAboutReceivedFiles(token));
        Assert.Equal([ApiTokenScopes.Outbox, ApiTokenScopes.Inbox, ApiTokenScopes.Certificates], token.Scopes.Each());
    }

    [Fact]
    public async Task TokenScopes_CreatingAnInboxWebhookWithoutTheScopeIsRefused()
    {
        var service = new ApiTokenService(null!, null!, null!);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync("cert-watch", ApiTokenScopes.Certificates, inboxWebhookUrl: "https://erp.example.com/oftp"));
    }

    private static CertificateChange Change(int id, DateTime activateAt, Certificate? certificate,
        CertificateChangeStatus status = CertificateChangeStatus.Scheduled, string? error = null) => new()
    {
        Id = id, PartnerName = "P", ActivateAt = activateAt, Certificate = certificate, CertificateId = certificate?.Id, Status = status,
        LastError = error,
    };

    private static Certificate Certificate(int id, DateTime validTo) =>
        new() { Id = id, Name = $"cert {id}", ValidFrom = validTo.AddYears(-2), ValidTo = validTo };
}
