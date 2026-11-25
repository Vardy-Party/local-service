namespace VardyParty.LocalService.Compute;

/// <summary>
/// Phone-path Chrome waits. The V2 plugin reads its own per-request options.
/// This gate only decides the wait values. It does not reference the plugin assembly.
/// </summary>
internal static class MpChromeWaitGate
{
    internal static readonly SemaphoreSlim OneResolve = new(1, 1);

    public static ChromeLaunchDefaults? ForRequest(string? popProxy, ChromeLaunchDefaults defaults)
    {
        if (popProxy is null)
        {
            return null;
        }

        return new ChromeLaunchDefaults
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

internal sealed class ChromeLaunchDefaults
{
    public bool Headless { get; init; } = true;
    public string? ExecutablePath { get; init; }
    public TimeSpan CdpReadyTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan ChipWait { get; init; } = TimeSpan.FromSeconds(25);
    public TimeSpan PlaylistWait { get; init; } = TimeSpan.FromSeconds(25);
    public TimeSpan SegmentWait { get; init; } = TimeSpan.FromSeconds(12);
    public TimeSpan WarmPlaylistWait { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan WarmSegmentWait { get; init; } = TimeSpan.FromSeconds(10);
    public int MinRewrittenSamples { get; init; } = 3;
    public string? DumpScriptBodiesDir { get; init; }
    public string? ProxyServer { get; init; }
}
