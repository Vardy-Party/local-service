namespace VardyParty.LocalService.Client.Discovery;

public static class DiscoveryCapabilities
{
    /// <summary>
    /// Local service accepts <c>/play/{url}?stream={name}</c> for multi-stream player pages.
    /// </summary>
    public const string PlayStreamQuery = "play.stream";

    /// <summary>
    /// Local service can resolve multi-stream player pages (V1/FB and legacy MP chip pages).
    /// </summary>
    public const string Mp = "mp";

    /// <summary>
    /// Local service can resolve V2/MP match pages via system Chrome
    /// (<c>POST /mp</c>), not Playwright-launched Chromium.
    /// </summary>
    public const string MpChrome = "mp.chrome";

    /// <summary>
    /// Local service can share this machine as a compute host
    /// (<c>/compute/host/*</c>) so another device can use it via an invite.
    /// </summary>
    public const string ComputeHost = "compute.host";

    /// <summary>
    /// Local service can POST scrape outcomes to the Worker stream-health DO
    /// when the client opts in and supplies match/API context on <c>/play</c>/<c>/mp</c>.
    /// </summary>
    public const string ScrapeHealth = "scrape.health";

    public static readonly string[] All = [PlayStreamQuery, Mp, MpChrome, ComputeHost, ScrapeHealth];

    public static bool SupportsPlayStreamQuery(IEnumerable<string>? capabilities) =>
        Has(capabilities, PlayStreamQuery);

    public static bool SupportsMpChrome(IEnumerable<string>? capabilities) =>
        Has(capabilities, MpChrome);

    public static bool SupportsComputeHost(IEnumerable<string>? capabilities) =>
        Has(capabilities, ComputeHost);

    public static bool SupportsScrapeHealth(IEnumerable<string>? capabilities) =>
        Has(capabilities, ScrapeHealth);

    /// <summary>
    /// True when the service advertises <c>mp</c> (or legacy <c>madplay</c>).
    /// </summary>
    public static bool SupportsMp(IEnumerable<string>? capabilities) =>
        Has(capabilities, Mp) || Has(capabilities, "madplay");

    private static bool Has(IEnumerable<string>? capabilities, string name) =>
        capabilities?.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)) == true;
}
