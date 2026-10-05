using System.Net;
using Microsoft.Extensions.Configuration;

namespace Oftp4Net.Services.Tests;

public class PasswordSignInPolicyTests
{
    private static PasswordSignInPolicy Create(bool entra, Dictionary<string, string?>? values = null) =>
        PasswordSignInPolicy.Create(new ConfigurationBuilder().AddInMemoryCollection(values ?? []).Build(), entra);

    [Fact]
    public void WithEntraId_PasswordsAreOnlyForLocalNetworksByDefault()
    {
        var policy = Create(entra: true);

        Assert.Equal(PasswordSignInMode.LocalNetworks, policy.Mode);
        Assert.Null(policy.Warning);
    }

    [Fact]
    public void WithoutEntraId_PasswordsWorkFromEverywhere_WhateverIsConfigured()
    {
        Assert.Equal(PasswordSignInMode.Everywhere, Create(entra: false).Mode);

        var never = Create(entra: false, new() { [PasswordSignInPolicy.ModeKey] = "Never" });
        Assert.Equal(PasswordSignInMode.Everywhere, never.Mode);
        Assert.Contains("not configured", never.Warning);
        Assert.True(never.Allows(IPAddress.Parse("203.0.113.7")));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("10.20.30.40", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.10", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("::ffff:192.168.1.10", true)]
    [InlineData("203.0.113.7", false)]
    [InlineData("2001:db8::1", false)]
    public void LocalNetworks_AreLocalhostAndPrivateNetworks(string address, bool allowed)
    {
        Assert.Equal(allowed, Create(entra: true).Allows(IPAddress.Parse(address)));
    }

    [Fact]
    public void FurtherNetworksAndAddresses_CanBeAllowed()
    {
        var policy = Create(entra: true, new() { [PasswordSignInPolicy.NetworksKey] = "203.0.113.0/24, 198.51.100.5" });

        Assert.True(policy.Allows(IPAddress.Parse("203.0.113.200")));
        Assert.True(policy.Allows(IPAddress.Parse("198.51.100.5")));
        Assert.False(policy.Allows(IPAddress.Parse("198.51.100.6")));
    }

    [Fact]
    public void FurtherNetworks_CanBeAList()
    {
        var policy = Create(entra: true, new()
        {
            [$"{PasswordSignInPolicy.NetworksKey}:0"] = "203.0.113.0/24",
            [$"{PasswordSignInPolicy.NetworksKey}:1"] = "2001:db8::/32",
        });

        Assert.True(policy.Allows(IPAddress.Parse("203.0.113.1")));
        Assert.True(policy.Allows(IPAddress.Parse("2001:db8::1")));
    }

    [Theory]
    [InlineData("Everywhere", "203.0.113.7", true)]
    [InlineData("everywhere", "203.0.113.7", true)]
    [InlineData("Never", "127.0.0.1", false)]
    public void TheModeCanBeSet(string mode, string address, bool allowed)
    {
        Assert.Equal(allowed, Create(entra: true, new() { [PasswordSignInPolicy.ModeKey] = mode }).Allows(IPAddress.Parse(address)));
    }

    [Fact]
    public void WithoutAnAddress_NoPasswordIsAcceptedFromLocalNetworksOnly()
    {
        Assert.False(Create(entra: true).Allows(null));
    }

    [Theory]
    [InlineData(PasswordSignInPolicy.ModeKey, "Sometimes")]
    [InlineData(PasswordSignInPolicy.NetworksKey, "not-a-network")]
    public void AWrongSetting_StopsTheStart(string key, string value)
    {
        Assert.Throws<InvalidOperationException>(() => Create(entra: true, new() { [key] = value }));
    }
}
