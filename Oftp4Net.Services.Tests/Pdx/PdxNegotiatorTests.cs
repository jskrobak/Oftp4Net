using Oftp4Net.Core.Protocol;
using Oftp4Net.Domain;
using Oftp4Net.Services.Pdx;
using static Oftp4Net.Domain.SecurityUsage;
using static Oftp4Net.Services.Pdx.NegotiationResult;

namespace Oftp4Net.Services.Tests.Pdx;

public class PdxNegotiatorTests
{
    /// <summary>The table of Odette OP08 part 3, "Interpreting the security settings".</summary>
    [Theory]
    [InlineData(Forbidden, Forbidden, Off)]
    [InlineData(Forbidden, Optional, Off)]
    [InlineData(Forbidden, Preferred, Off)]
    [InlineData(Forbidden, Required, Conflict)]
    [InlineData(Optional, Forbidden, Off)]
    [InlineData(Optional, Optional, Off)]
    [InlineData(Optional, Preferred, On)]
    [InlineData(Optional, Required, On)]
    [InlineData(Preferred, Forbidden, Off)]
    [InlineData(Preferred, Optional, On)]
    [InlineData(Preferred, Preferred, On)]
    [InlineData(Preferred, Required, On)]
    [InlineData(Required, Forbidden, Conflict)]
    [InlineData(Required, Optional, On)]
    [InlineData(Required, Preferred, On)]
    [InlineData(Required, Required, On)]
    public void UsagesAreResolvedAsInTheRecommendation(SecurityUsage own, SecurityUsage theirs, NegotiationResult expected)
    {
        Assert.Equal(expected, PdxNegotiator.Resolve(own, theirs));
    }

    [Fact]
    public void PrimaryCipherOfThePartnerIsPreferred()
    {
        var suite = PdxNegotiator.ResolveCipherSuite(new StationProfile(), new PdxCipherSetting("02", ["01"]));

        Assert.Equal(CipherSuites.Aes256Sha1, suite?.Code);
    }

    [Fact]
    public void AlternateCipherIsUsedWhenThePrimaryIsNotAccepted()
    {
        var own = new StationProfile { PrimaryCipherSuite = "04", AlternateCipherSuites = ["06"] };

        var suite = PdxNegotiator.ResolveCipherSuite(own, new PdxCipherSetting("02", ["06", "04"]));

        Assert.Equal(CipherSuites.Aes256Sha512, suite?.Code);
    }

    [Fact]
    public void NoCommonCipher()
    {
        var own = new StationProfile { PrimaryCipherSuite = "04", AlternateCipherSuites = ["06"] };

        Assert.Null(PdxNegotiator.ResolveCipherSuite(own, new PdxCipherSetting("01", ["03"])));
    }
}
