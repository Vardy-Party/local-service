using VardyParty.LocalService.Client.Discovery;

namespace VardyParty.LocalService;

/// <summary>
/// Runtime switch for V2/MP scrape (<c>POST /mp</c> + the <c>mp.chrome</c> cap).
/// Set <c>VARDYPARTY_ENABLE_MP=0</c> (or false/off/no) to disable; unset means enabled.
/// The <c>mp</c> cap is kept because it also covers V1/FB multi-stream pages.
/// </summary>
public sealed record MpScrapeGate(bool Enabled)
{
    public const string EnvironmentVariable = "VARDYPARTY_ENABLE_MP";

    public static MpScrapeGate FromEnvironment() =>
        FromValue(Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static MpScrapeGate FromValue(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v))
            return new MpScrapeGate(true);
        var disabled = v == "0"
            || v.Equals("false", StringComparison.OrdinalIgnoreCase)
            || v.Equals("off", StringComparison.OrdinalIgnoreCase)
            || v.Equals("no", StringComparison.OrdinalIgnoreCase);
        return new MpScrapeGate(!disabled);
    }

    public string[] FilterCapabilities(IEnumerable<string> capabilities) =>
        Enabled
            ? capabilities.ToArray()
            : capabilities
                .Where(c => !string.Equals(c, DiscoveryCapabilities.MpChrome, StringComparison.OrdinalIgnoreCase))
                .ToArray();
}