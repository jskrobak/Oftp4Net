using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Oftp4Net.Domain;
using Oftp4Net.Services.Security;

namespace Oftp4Net.Services.Tests;

public class PublicCertificateTests
{
    [Fact]
    public void Collect_PublishesOurCertificatesInUseAndTheScheduledOnes()
    {
        var settings = new GlobalSettings
        {
            FileSecurityCertificateId = 1, OftpClientCertificateId = 1,
            // Kept only to decrypt around a roll-over: not to be given to partners any more.
            PreviousFileSecurityCertificateId = 9, PreviousFileSecurityCertificateUntil = new DateTime(2026, 12, 31),
        };
        var identity = new Identity { Certificates = [new CertificateAssignment { Usage = CertificateUsage.Authentication, CertificateId = 3 }] };
        Listener[] listeners =
        [
            new() { Enabled = true, UseTls = true, Port = 6619, CertificateId = 1 },
            new() { Enabled = false, UseTls = true, Port = 7000, CertificateId = 4 },
            new() { Enabled = true, UseTls = false, Port = 3305, CertificateId = 5 },
        ];
        var activateAt = new DateTime(2026, 12, 14, 10, 0, 0);
        CertificateChange[] scheduled =
        [
            new() { Target = CertificateChangeTarget.Own, Status = CertificateChangeStatus.Scheduled, CertificateId = 2,
                    ReplacedCertificateId = 1, Usage = PartnerCertificateUsage.SecurityAndTls, ActivateAt = activateAt },
            // A certificate of a partner is never ours to publish.
            new() { Target = CertificateChangeTarget.Partner, Status = CertificateChangeStatus.Scheduled, CertificateId = 7 },
        ];

        var uses = PublicCertificateService.Collect(settings, [identity], listeners, scheduled);

        Assert.Equal([1, 2, 3], uses.Select(u => u.CertificateId).Distinct().Order());
        Assert.Equal(["file signing and encryption", "TLS client", "TLS server (port 6619)"],
            uses.Where(u => u.CertificateId == 1).Select(u => u.UsedFor));
        var next = Assert.Single(uses, u => u.CertificateId == 2);
        Assert.Equal((activateAt, 1), (next.UsedFrom!.Value, next.Replaces!.Value));
        Assert.Equal("secure authentication", Assert.Single(uses, u => u.CertificateId == 3).UsedFor);
    }

    [Fact]
    public void Collect_LeavesOutAppliedAndCancelledChanges()
    {
        CertificateChange[] changes =
        [
            new() { Target = CertificateChangeTarget.Own, Status = CertificateChangeStatus.Applied, CertificateId = 2 },
            new() { Target = CertificateChangeTarget.Own, Status = CertificateChangeStatus.Cancelled, CertificateId = 3 },
        ];

        Assert.Empty(PublicCertificateService.Collect(new GlobalSettings(), [], [], changes));
    }

    [Fact]
    public void Key_IsTheBeginningOfTheSha256Fingerprint()
    {
        using var key = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=oftp.example.com", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        var published = PublicCertificateService.Key(certificate);

        Assert.Equal(16, published.Length);
        Assert.StartsWith(published, Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)));
    }
}
