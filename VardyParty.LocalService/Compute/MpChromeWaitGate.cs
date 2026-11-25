using VardyParty.LocalService.V2.Scrape;

namespace VardyParty.LocalService.Compute;

/// <summary>
/// Phone-path Chrome waits. Published V2.Scrape reads <see cref="V2ScrapePlugin.RequestChromeOptions"/>
/// per request, so this no longer mutates the process singleton. OneResolve still
/// serializes Chrome harvest because <c>MpPlayEngine</c> owns one browser at a time.
/// </summary>
internal static class MpChromeWaitGate
{
    internal static readonly SemaphoreSlim OneResolve = new(1, 1);

    public static SystemChromeOptions? ForRequest(string? popProxy, SystemChromeOptions defaults)
    {
        if (popProxy is null)
        {
            return null;
        }

        return new SystemChromeOptions
        {
            Headless = defaults.Headless,
            ExecutablePath = defaults.ExecutablePath,
            CdpReadyTimeout = defaults.CdpReadyTimeout,
            ChipWait = PhonePathBudget.ChipWait,
            PlaylistWait = PhonePathBudget.PlaylistWait,
            SegmentWait = PhonePathBudget.SegmentWait,
            WarmPlaylistWait = PhonePathBudget.WarmPlaylistWait,
            WarmSegmentWait = PhonePathBudget.WarmSegmentWait,
            MinRewrittenSamples = defaults.MinRewrittenSamples,
            DumpScriptBodiesDir = defaults.DumpScriptBodiesDir,
            ProxyServer = defaults.ProxyServer
        };
    }
}
