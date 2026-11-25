using VardyParty.LocalService.Client.Discovery;

namespace VardyParty.LocalService;

/// <summary>
/// Runtime switch for <c>POST /v2</c> and the <c>v2.chrome</c> cap.
/// Set <c>VARDYPARTY_ENABLE_V2=0</c> (or false/off/no) to disable; unset means enabled.
/// <c>VARDYPARTY_ENABLE_MP</c> is still read when the v2 variable is unset.
/// </summary>
public sealed record V2ScrapeGate(bool Enabled)
{
    public const string EnvironmentVariable = "VARDYPARTY_ENABLE_V2";

    public const string LegacyEnvironmentVariable = "VARDYPARTY_ENABLE_MP";

    public static V2ScrapeGate FromEnvironment()
    {
        var current = Environment.GetEnvironmentVariable(EnvironmentVariable);
        var value = string.IsNullOrWhiteSpace(current)
            ? Environment.GetEnvironmentVariable(LegacyEnvironmentVariable)
            : current;
        return FromValue(value);
    }

    public static V2ScrapeGate FromValue(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v))
            return new V2ScrapeGate(true);
        var disabled = v == "0"
            || v.Equals("false", StringComparison.OrdinalIgnoreCase)
            || v.Equals("off", StringComparison.OrdinalIgnoreCase)
            || v.Equals("no", StringComparison.OrdinalIgnoreCase);
        return new V2ScrapeGate(!disabled);
    }

    public string[] FilterCapabilities(IEnumerable<string> capabilities) =>
        Enabled
            ? capabilities.ToArray()
            : capabilities
                .Where(c => !string.Equals(c, DiscoveryCapabilities.V2Chrome, StringComparison.OrdinalIgnoreCase))
                .ToArray();
}