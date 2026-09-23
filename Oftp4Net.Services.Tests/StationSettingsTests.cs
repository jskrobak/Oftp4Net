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

    [Fact]
    public void RequiredCompressionIsChecked()
    {
        var settings = StationSettings.For(new Partner { SFID = "X", RequireCompressedFiles = true }, null);

        Assert.NotNull(settings.CheckIncoming(Security(SecurityLevels.None)));
        Assert.Null(settings.CheckIncoming(Security(SecurityLevels.None, compressed: true)));
    }
}
