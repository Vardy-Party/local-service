using VardyParty.LocalService.Abstractions;

namespace VardyParty.LocalService.Tests;

public sealed class ScrapePluginResolverTests
{
    [Fact]
    public void TryResolve_PrefersMatchingStrategyId_OverFirstTyped()
    {
        var preferred = new FakePlugin(StreamStrategyIds.V2);
        var other = new FakePlugin("other");
        var scrapes = new IStreamScrapePlugin[] { other, preferred };

        var ok = ScrapePluginResolver.TryResolve<FakePlugin>(scrapes, StreamStrategyIds.V2, out var plugin);

        Assert.True(ok);
        Assert.Same(preferred, plugin);
    }

    [Fact]
    public void TryResolve_FallsBackToFirstTyped_WhenStrategyIdMissing()
    {
        var only = new FakePlugin("custom");
        var scrapes = new IStreamScrapePlugin[] { only };

        var ok = ScrapePluginResolver.TryResolve<FakePlugin>(scrapes, StreamStrategyIds.V2, out var plugin);

        Assert.True(ok);
        Assert.Same(only, plugin);
    }

    [Fact]
    public void TryResolve_ReturnsFalse_WhenNoTypedPlugin()
    {
        var scrapes = new IStreamScrapePlugin[] { new OtherPlugin() };

        var ok = ScrapePluginResolver.TryResolve<FakePlugin>(scrapes, StreamStrategyIds.V1, out var plugin);

        Assert.False(ok);
        Assert.Null(plugin);
    }

    [Fact]
    public void TryResolve_IgnoresSameStrategyId_WhenWrongType()
    {
        var wrongType = new OtherPlugin { StrategyIdOverride = StreamStrategyIds.V2 };
        var scrapes = new IStreamScrapePlugin[] { wrongType };

        var ok = ScrapePluginResolver.TryResolve<FakePlugin>(scrapes, StreamStrategyIds.V2, out var plugin);

        Assert.False(ok);
        Assert.Null(plugin);
    }

    private sealed class FakePlugin : IStreamScrapePlugin
    {
        public FakePlugin(string strategyId) => StrategyId = strategyId;
        public string StrategyId { get; }
        public bool Matches(string? resolutionStrategy, string? source) => false;
    }

    private sealed class OtherPlugin : IStreamScrapePlugin
    {
        public string? StrategyIdOverride { get; init; }
        public string StrategyId => StrategyIdOverride ?? "other";
        public bool Matches(string? resolutionStrategy, string? source) => false;
    }
}
