using Oftp4Net.Core.Protocol;
using Oftp4Net.Domain;
using Oftp4Net.Services.Oftp;
using Oftp4Net.Services.Security;

namespace Oftp4Net.Services.Tests;

public class StationSettingsTests
{
    private static Partner Partner() => new()
    {
        SFID = "O0013PARTNER",
        EncryptFiles = true,
        RequireSignedFiles = true,
        FileCipherSuite = CipherSuites.Aes256Sha256,
        SubStations =
        [
            new PartnerSubStation
            {
                Name = "Plant",
                SFID = "O0013PLANT",
                EncryptFiles = false,
                RequireSignedFiles = false,
                RequireEncryptedFiles = true,
                FileCipherSuite = CipherSuites.Aes256Sha512,
            },
        ],
    };

    private static FileSecurityDescriptor Security(string level, bool compressed = false) => new()
    {
        SecurityLevel = level,
        Compression = compressed ? FileCompressionAlgorithms.Zlib : FileCompressionAlgorithms.None,
        Enveloping = level == SecurityLevels.None && !compressed ? FileEnvelopingFormats.None : FileEnvelopingFormats.Cms,
        CipherSuiteCode = CipherSuites.Aes256Sha256,
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("O0013PARTNER")]
    [InlineData("O0013UNKNOWN")]
    public void PartnerSettingsAreUsedOutsideSubStations(string? sfid)
    {
        var settings = StationSettings.For(Partner(), sfid);

        Assert.Null(settings.SubStation);
        Assert.Equal("O0013PARTNER", settings.Sfid);
        Assert.True(settings.EncryptFiles);
        Assert.True(settings.RequireSignedFiles);
        Assert.False(settings.RequireEncryptedFiles);
        Assert.Equal(CipherSuites.Aes256Sha256, settings.FileCipherSuite);
    }

    [Fact]
    public void SubStationOverridesThePartner()
    {
        var settings = StationSettings.For(Partner(), " o0013plant ");

        Assert.Equal("O0013PLANT", settings.Sfid);
        Assert.False(settings.EncryptFiles);
        Assert.False(settings.RequireSignedFiles);
        Assert.True(settings.RequireEncryptedFiles);
        Assert.Equal(CipherSuites.Aes256Sha512, settings.FileCipherSuite);
        // Not overridden: taken over from the partner.
        Assert.False(settings.SignFiles);
    }

    [Fact]
    public void RequiredSecurityIsChecked()
    {
        var partner = StationSettings.For(Partner(), null);

        Assert.Equal(AnswerReasonCodes.UnsignedFileNotAllowed, partner.CheckIncoming(Security(SecurityLevels.None))?.ReasonCode);
        Assert.Equal(AnswerReasonCodes.UnsignedFileNotAllowed, partner.CheckIncoming(Security(SecurityLevels.Encrypted))?.ReasonCode);
        Assert.Null(partner.CheckIncoming(Security(SecurityLevels.Signed)));

        var plant = StationSettings.For(Partner(), "O0013PLANT");
        Assert.Equal(AnswerReasonCodes.UnencryptedFileNotAllowed, plant.CheckIncoming(Security(SecurityLevels.Signed))?.ReasonCode);
        Assert.Null(plant.CheckIncoming(Security(SecurityLevels.Encrypted)));
    }

    /// <summary>
    /// Certificates of a partner are resolved from the most specific assignment: the one of the station, then the
    /// one of the partner, then its single certificate (Odette OP08 2.5).
    /// </summary>
    [Fact]
    public void CertificateOfTheStationBeatsTheOneOfThePartner()
    {
        var partner = Partner();
        partner.SecurityCertificateId = 1;
        partner.PreviousSecurityCertificateId = 8;
        partner.Certificates =
        [
            new CertificateAssignment { Usage = CertificateUsage.FileEncryption, CertificateId = 2 },
            new CertificateAssignment
            {
                Usage = CertificateUsage.FileEncryption, Sfid = "O0013PLANT", CertificateId = 3, PreviousCertificateId = 4,
            },
        ];

        var main = StationSettings.For(partner, null);
        var plant = StationSettings.For(partner, "O0013PLANT");

        Assert.Equal(3, plant.CertificateFor(CertificateUsage.FileEncryption));
        Assert.Equal(4, plant.PreviousCertificateFor(CertificateUsage.FileEncryption));
        Assert.Equal(2, main.CertificateFor(CertificateUsage.FileEncryption));
        // Nothing is assigned for signatures, so the single certificate of the partner is used.
        Assert.Equal(1, plant.CertificateFor(CertificateUsage.FileSignature));
        Assert.Equal(8, plant.PreviousCertificateFor(CertificateUsage.FileSignature));
    }

    [Fact]
    public void StationWithoutAssignmentsUsesTheSingleCertificate()
    {
        var partner = Partner();
        partner.SecurityCertificateId = 1;

        var settings = StationSettings.For(partner, "O0013PLANT");

        foreach (var usage in Enum.GetValues<CertificateUsage>())
            Assert.Equal(1, settings.CertificateFor(usage));
    }

    [Fact]
    public void RequiredCompressionIsChecked()
    {
        var settings = StationSettings.For(new Partner { SFID = "X", RequireCompressedFiles = true }, null);

        Assert.NotNull(settings.CheckIncoming(Security(SecurityLevels.None)));
        Assert.Null(settings.CheckIncoming(Security(SecurityLevels.None, compressed: true)));
    }
}
