using Oftp4Net.Domain;

namespace Oftp4Net.Services.Tests;

/// <summary>Our password in the SSID: the identity's, unless the partner knows us by its own one.</summary>
public class PartnerOurPasswordTests
{
    private static readonly Identity Artipa = new() { Name = "ARTIPA", SSID = "O0177", SFID = "O0177", Password = "ARTIPA01" };

    [Fact]
    public void PartnerWithoutOwnPasswordGetsTheIdentitys()
    {
        Assert.Equal("ARTIPA01", new Partner { Name = "MAHLE" }.OurPasswordFor(Artipa));
        Assert.Equal("ARTIPA01", new Partner { Name = "MAHLE", OurPassword = "" }.OurPasswordFor(Artipa));
    }

    [Fact]
    public void PartnerWithOwnPasswordGetsIt()
    {
        Assert.Equal("KOI00001", new Partner { Name = "VW", OurPassword = "KOI00001" }.OurPasswordFor(Artipa));
    }

    [Fact]
    public void IdentityWithoutPasswordSendsNone()
    {
        Assert.Equal("", new Partner { Name = "MAHLE" }.OurPasswordFor(new Identity { Name = "X", SSID = "X", SFID = "X" }));
    }
}
