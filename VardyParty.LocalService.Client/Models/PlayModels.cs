namespace VardyParty.LocalService.Client.Models;

public sealed class PlayResult
{
    public string? Url { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, string>? RequestHeaders { get; set; }
    public PlayTimings? Timings { get; set; }

    /// <summary>
    /// V2/MP rewritten segment URLs when <see cref="LocalServicePlayClient.ResolveAsync"/>
    /// routes through <c>POST /mp</c>. Empty/null for V1 <c>/play</c> responses.
    /// </summary>
    public List<string>? RewrittenSegments { get; set; }

    /// <summary>
    /// V2/MP chip/stream labels when routed through <c>POST /mp</c>.
    /// </summary>
    public List<string>? Streams { get; set; }
}

public sealed class PlayRequest
{
    public string PageUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional V1/FB stream label. Passed as <c>?stream=</c> to the local service.
    /// </summary>
    public string? StreamName { get; set; }

    /// <summary>
    /// Catalog <c>resolutionStrategy</c> (<c>v1</c> / <c>v2</c>). Drives <c>/play</c> vs <c>/mp</c>.
    /// </summary>
    public string? ResolutionStrategy { get; set; }

    /// <summary>
    /// Catalog <c>source</c> (<c>fb</c> / <c>mp</c>) when strategy is omitted.
    /// </summary>
    public string? Source { get; set; }
}

public sealed class PlayTimings
{
    public long Start { get; set; }
    public long Total { get; set; }
}
