using VardyParty.LocalService.Compute;

namespace VardyParty.LocalService.Tests;

public sealed class PlayDnsTests
{
    [Fact]
    public void ForPlay_PopProxyClearsTemplateEvenWhenDohIsOn()
    {
        var template = PlayDns.ForPlay("1", "192.0.2.8", "http://127.0.0.1:9", out var error);

        Assert.Null(error);
        Assert.Null(template);
    }

    [Fact]
    public void ForPlay_DohOnWithIp_UsesResolverTemplate()
    {
        var template = PlayDns.ForPlay("1", "192.0.2.8", null, out var error);

        Assert.Null(error);
        Assert.Equal("https://192.0.2.8/dns-query", template);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    public void ForPlay_DohOffOrMissing_IsNull(string? flag)
    {
        var template = PlayDns.ForPlay(flag, "192.0.2.8", null, out var error);

        Assert.Null(error);
        Assert.Null(template);
    }

    [Fact]
    public void Flags_AreOnOffAndSetUnset()
    {
        Assert.Equal("on", PlayDns.DohFlag("https://192.0.2.8/dns-query"));
        Assert.Equal("off", PlayDns.DohFlag(null));
        Assert.Equal("set", PlayDns.PopFlag("http://127.0.0.1:9"));
        Assert.Equal("unset", PlayDns.PopFlag(null));
    }
}
