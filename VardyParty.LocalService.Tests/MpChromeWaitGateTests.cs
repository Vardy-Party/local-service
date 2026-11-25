using VardyParty.LocalService.Compute;
using VardyParty.LocalService.V2.Scrape;

namespace VardyParty.LocalService.Tests;

public sealed class MpChromeWaitGateTests
{
    [Fact]
    public void ForRequest_PopProxyUsesPhonePathWaitsIncludingWarm()
    {
        // Arrange
        var defaults = new SystemChromeOptions
        {
            ChipWait = TimeSpan.FromSeconds(1),
            PlaylistWait = TimeSpan.FromSeconds(2),
            SegmentWait = TimeSpan.FromSeconds(3),
            WarmPlaylistWait = TimeSpan.FromSeconds(4),
            WarmSegmentWait = TimeSpan.FromSeconds(5)
        };

        // Act
        var options = MpChromeWaitGate.ForRequest("http://127.0.0.1:9", defaults);

        // Assert
        Assert.NotNull(options);
        Assert.Equal(PhonePathBudget.ChipWait, options.ChipWait);
        Assert.Equal(PhonePathBudget.PlaylistWait, options.PlaylistWait);
        Assert.Equal(PhonePathBudget.SegmentWait, options.SegmentWait);
        Assert.Equal(PhonePathBudget.WarmPlaylistWait, options.WarmPlaylistWait);
        Assert.Equal(PhonePathBudget.WarmSegmentWait, options.WarmSegmentWait);
        Assert.Equal(defaults.Headless, options.Headless);
        Assert.Equal(defaults.MinRewrittenSamples, options.MinRewrittenSamples);
    }

    [Fact]
    public void ForRequest_WithoutPopProxyKeepsSingleton()
    {
        // Arrange
        var defaults = new SystemChromeOptions
        {
            ChipWait = TimeSpan.FromSeconds(1),
            PlaylistWait = TimeSpan.FromSeconds(2),
            SegmentWait = TimeSpan.FromSeconds(3)
        };

        // Act
        var options = MpChromeWaitGate.ForRequest(null, defaults);

        // Assert
        Assert.Null(options);
    }
}
