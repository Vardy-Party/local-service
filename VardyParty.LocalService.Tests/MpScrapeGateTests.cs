using VardyParty.LocalService.Client.Discovery;
using Xunit;

namespace VardyParty.LocalService.Tests;

public class MpScrapeGateTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData(" 0 ", false)]
    [InlineData("false", false)]
    [InlineData("OFF", false)]
    [InlineData("no", false)]
    public void FromValue_ParsesSwitch(string? value, bool expected) =>
        Assert.Equal(expected, MpScrapeGate.FromValue(value).Enabled);

    [Fact]
    public void Disabled_DropsOnlyMpChromeCapability()
    {
        var caps = new MpScrapeGate(false).FilterCapabilities(DiscoveryCapabilities.All);
        Assert.DoesNotContain(DiscoveryCapabilities.MpChrome, caps);
        Assert.Contains(DiscoveryCapabilities.Mp, caps);
        Assert.Contains(DiscoveryCapabilities.PlayStreamQuery, caps);
        Assert.Contains(DiscoveryCapabilities.ComputeHost, caps);
        Assert.False(DiscoveryCapabilities.SupportsMpChrome(caps));
    }

    [Fact]
    public void Enabled_KeepsAllCapabilities() =>
        Assert.Equal(DiscoveryCapabilities.All, new MpScrapeGate(true).FilterCapabilities(DiscoveryCapabilities.All));
}