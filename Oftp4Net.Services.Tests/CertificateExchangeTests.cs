using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Oftp4Net.Domain;
using Oftp4Net.Services.Security;

namespace Oftp4Net.Services.Tests;

/// <summary>
/// Automatic exchange of certificates (Odette OP08 2.5): the logical identification data in SFIDDESC and the
/// assignment of a received certificate to the partner.
/// </summary>
public class CertificateExchangeTests
{
    private static X509Certificate2 CreateCertificate(string subject,
        X509KeyUsageFlags usage = X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
        bool serverAuthentication = true)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        if (serverAuthentication)
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], critical: false));

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static Certificate Entity(int id, X509Certificate2 certificate) => new()
    {
        Id = id,
        Name = certificate.Subject,
        Base64Data = Convert.ToBase64String(certificate.RawData),
        ValidFrom = certificate.NotBefore,
        ValidTo = certificate.NotAfter,
    };

    [Theory]
    [InlineData("ODETTE_CERTIFICATE_REQUEST", CertificateExchangeKind.Request)]
    [InlineData("odette_certificate_deliver", CertificateExchangeKind.Deliver)]
    [InlineData("ODETTE_CERTIFICATE_REPLACE ", CertificateExchangeKind.Replace)]
    public void CertificateFilesAreRecognised(string virtualFileName, CertificateExchangeKind expected)
    {
        Assert.Equal(expected, CertificateExchange.KindOf(virtualFileName));
        Assert.Null(CertificateExchange.KindOf("CADDATA"));
    }

    [Fact]
    public void FileDescriptionKeepsTheIdentificationData()
    {
        using var certificate = CreateCertificate("CN=Station A0, O=Company A, C=CZ");

        var description = CertificateLogicalId.From(certificate).ToFileDescription();
        var parsed = CertificateLogicalId.Parse(description);

        Assert.NotNull(parsed);
        // The fields in the order of OP08 2.5 G, separated by LF, without spaces around the equal sign.
        Assert.Equal(['I', 'N', 'A', 'B', 'E', 'S'], description.Split('\n').Select(f => f[0]));
        Assert.DoesNotContain(" =", description);
        Assert.DoesNotContain("= ", description);
        Assert.Equal(certificate.SerialNumber, parsed.SerialNumber, ignoreCase: true);
        // Digital signature and key encipherment, as the example in the specification.
        Assert.Equal("a0", parsed.KeyUsage);
        Assert.Equal(["1.3.6.1.5.5.7.3.1", "1.3.6.1.5.5.7.3.2"], parsed.ExtendedKeyUsage.Order());
        Assert.True(parsed.Identifies(certificate));
        Assert.True(parsed.IsInstance(certificate));
    }

    [Fact]
    public void FileDescriptionOfLongNamesFitsIntoSfidDesc()
    {
        var name = new string('x', 60);
        using var certificate = CreateCertificate($"CN={name}, O={name}, OU={name}, L={name}, C=CZ");

        var description = CertificateLogicalId.From(certificate).ToFileDescription();

        Assert.True(Encoding.UTF8.GetByteCount(description) <= CertificateLogicalId.MaxLength,
            $"SFIDDESC has {Encoding.UTF8.GetByteCount(description)} octets.");
        // The mandatory fields are always there, the subject is the first one to be left out.
        Assert.StartsWith("I=", description);
        Assert.Contains("\nB=", description);
    }

    [Fact]
    public void IdentificationDataWrittenInAnotherOrderStillMatches()
    {
        using var certificate = CreateCertificate("CN=Station A0, O=Company A, C=CZ");
        var id = CertificateLogicalId.From(certificate);

        // Implementations write distinguished names in different orders and with different spacing.
        var rewritten = id with { Issuer = "C=CZ,O=Company A,CN=Station A0", Subject = "C=CZ, O=Company A, CN=Station A0" };

        Assert.True(rewritten.Identifies(certificate));
        Assert.True(rewritten.IsInstance(certificate));
    }

    [Fact]
    public void RenewedCertificateHasTheSameIdentification()
    {
        using var current = CreateCertificate("CN=Station A0, O=Company A, C=CZ");
        using var renewed = CreateCertificate("CN=Station A0, O=Company A, C=CZ");
        var id = CertificateLogicalId.From(current);

        // Another physical certificate of the same owner for the same purpose (OP08 1.9).
        Assert.True(id.Identifies(renewed));
        Assert.False(id.IsInstance(renewed));
    }

    [Fact]
    public void CertificateForAnotherPurposeIsNotTheSame()
    {
        using var signing = CreateCertificate("CN=Station A0", X509KeyUsageFlags.DigitalSignature, serverAuthentication: false);
        using var encryption = CreateCertificate("CN=Station A0", X509KeyUsageFlags.KeyEncipherment, serverAuthentication: false);

        Assert.False(CertificateLogicalId.From(signing).Identifies(encryption));
    }

    [Fact]
    public void ReplacedCertificateIsFoundByTheFileDescription()
    {
        using var old = CreateCertificate("CN=Station A0, O=Company A");
        // A replacement with another subject can only be assigned through SFIDDESC (OP08 2.5 G).
        using var replacement = CreateCertificate("CN=Station A0 new, O=Company A");
        var stored = new List<(Certificate, X509Certificate2)> { (Entity(1, old), old) };

        var description = CertificateLogicalId.From(old).ToFileDescription();

        Assert.Equal(1, CertificateExchangeService.Match(replacement, description, stored)?.Id);
        Assert.Null(CertificateExchangeService.Match(replacement, null, stored));
    }

    [Fact]
    public void DeliveredCertificateKeepsThePreviousOneValid()
    {
        using var old = CreateCertificate("CN=Station A0");
        var partner = new Partner { SecurityCertificateId = 1, TrustedCertificateId = 1 };
        var received = new Certificate { Id = 2, Name = "new" };

        CertificateExchangeService.Assign(partner, received, CertificateExchangeKind.Deliver, Entity(1, old));

        Assert.Equal(2, partner.SecurityCertificateId);
        Assert.Equal(1, partner.PreviousSecurityCertificateId);
        // One certificate is commonly used for everything, so TLS follows the roll-over.
        Assert.Equal(2, partner.TrustedCertificateId);
    }

    [Fact]
    public void ReplacedCertificateIsNotUsedAnyMore()
    {
        using var old = CreateCertificate("CN=Station A0");
        var partner = new Partner { SecurityCertificateId = 1, PreviousSecurityCertificateId = 3, TrustedCertificateId = 1 };
        var received = new Certificate { Id = 2, Name = "new" };

        CertificateExchangeService.Assign(partner, received, CertificateExchangeKind.Replace, Entity(1, old));

        Assert.Equal(2, partner.SecurityCertificateId);
        Assert.Null(partner.PreviousSecurityCertificateId);
        Assert.Equal(2, partner.TrustedCertificateId);
    }

    [Fact]
    public void FirstCertificateFillsTheEmptyPlaces()
    {
        using var old = CreateCertificate("CN=Station B");
        // Only the TLS certificate of the partner is known, e.g. from its datasheet.
        var partner = new Partner { TrustedCertificateId = 1 };
        var received = new Certificate { Id = 2, Name = "delivered" };

        CertificateExchangeService.Assign(partner, received, CertificateExchangeKind.Deliver, Entity(1, old));

        Assert.Equal(2, partner.SecurityCertificateId);
        Assert.Null(partner.PreviousSecurityCertificateId);
        Assert.Equal(2, partner.TrustedCertificateId);
    }

    [Fact]
    public void UnassignedCertificateLeavesTheTlsCertificateAlone()
    {
        var partner = new Partner { SecurityCertificateId = 1, TrustedCertificateId = 5 };
        var received = new Certificate { Id = 2, Name = "new" };

        // The certificate does not replace one we know: it becomes the one for file security, TLS stays as it is.
        CertificateExchangeService.Assign(partner, received, CertificateExchangeKind.Deliver, replaced: null);

        Assert.Equal(2, partner.SecurityCertificateId);
        Assert.Equal(1, partner.PreviousSecurityCertificateId);
        Assert.Equal(5, partner.TrustedCertificateId);
    }
}
