internal interface IStreamScrapePlugin
{
    string StrategyId { get; }

    bool Matches(string? resolutionStrategy, string? source);
}

internal static class StreamStrategyIds
{
    public const string V1 = "v1";
    public const string V2 = "v2";
}

/// <summary>
/// Resolves a typed scrape plugin from the registered <see cref="IStreamScrapePlugin"/> set.
/// Prefers a strategy-id match, then falls back to the first plugin of the requested type.
/// </summary>
internal static class ScrapePluginResolver
{
    public static bool TryResolve<T>(
        IEnumerable<IStreamScrapePlugin> scrapes,
        string strategyId,
        out T plugin) where T : class, IStreamScrapePlugin
    {
        var typed = scrapes.OfType<T>().ToList();
        if (typed.FirstOrDefault(p =>
                string.Equals(p.StrategyId, strategyId, StringComparison.OrdinalIgnoreCase)) is { } byId)
        {
            plugin = byId;
            return true;
        }

        if (typed.FirstOrDefault() is { } first)
        {
            plugin = first;
            return true;
        }

        plugin = null!;
        return false;
    }
}
