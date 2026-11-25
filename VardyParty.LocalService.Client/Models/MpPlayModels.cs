namespace VardyParty.LocalService.Client.Models;

public sealed class MpPlayRequest
{
    public string PageUrl { get; set; } = string.Empty;
    public string? Stream { get; set; }
    public bool ListOnly { get; set; }
    public bool StreamResults { get; set; }
}

public sealed class MpPlayResult
{
    public string? Url { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, string>? RequestHeaders { get; set; }
    public List<string>? Streams { get; set; }
    public List<string>? RewrittenSegments { get; set; }
    public PlayTimings? Timings { get; set; }
    public MpBrowserInfo? Browser { get; set; }
}

public sealed class MpBrowserInfo
{
    public string Engine { get; set; } = "system-chrome";
    public bool CdpAttached { get; set; }
    public bool WipedToAboutBlank { get; set; }
    public string? FinalUrl { get; set; }
    public string? ChromePath { get; set; }
}
