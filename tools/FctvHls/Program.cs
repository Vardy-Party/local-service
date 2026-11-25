using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VardyParty.LocalService.Abstractions.Models;
using VardyParty.LocalService.V2.Scrape;

var url = args.FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal))
          ?? throw new ArgumentException("Usage: MpHls <match-url> [--stream NAME] [--list] [--headed]");

var listOnly = args.Any(a => a is "--list" or "-l");
var headed = args.Any(a => a is "--headed");
var stream = ArgValue(args, "--stream", "-s");

var engine = new MpPlayEngine(
    new SystemChromeOptions
    {
        Headless = !headed,
        ChipWait = TimeSpan.FromSeconds(25),
        PlaylistWait = TimeSpan.FromSeconds(25),
        SegmentWait = TimeSpan.FromSeconds(10)
    },
    NullLogger<MpPlayEngine>.Instance);

var outcome = await engine.ExecuteAsync(new MpPlayRequest
{
    PageUrl = url,
    Stream = stream,
    ListOnly = listOnly
});

Console.WriteLine(JsonSerializer.Serialize(outcome.Result, new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
}));

return outcome.StatusCode >= 400 ? 1 : 0;

static string? ArgValue(string[] args, params string[] names)
{
    for (var i = 0; i < args.Length; i++)
    {
        foreach (var name in names)
        {
            if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return i + 1 < args.Length ? args[i + 1] : null;
        }
    }

    return null;
}
