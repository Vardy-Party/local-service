using VardyParty.LocalService.Client.Discovery;
using Xunit;

namespace VardyParty.LocalService.Tests;

public class V2ScrapeGateTests
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
        Assert.Equal(expected, V2ScrapeGate.FromValue(value).Enabled);

    [Fact]
    public void Disabled_DropsOnlyV2ChromeCapability()
    {
        var caps = new V2ScrapeGate(false).FilterCapabilities(DiscoveryCapabilities.All);
        Assert.DoesNotContain(DiscoveryCapabilities.V2Chrome, caps);
        Assert.Contains(DiscoveryCapabilities.Mp, caps);
        Assert.Contains(DiscoveryCapabilities.PlayStreamQuery, caps);
        Assert.Contains(DiscoveryCapabilities.ComputeHost, caps);
        Assert.False(DiscoveryCapabilities.SupportsV2Chrome(caps));
    }

    [Fact]
    public void Enabled_KeepsAllCapabilities() =>
        Assert.Equal(DiscoveryCapabilities.All, new V2ScrapeGate(true).FilterCapabilities(DiscoveryCapabilities.All));
}