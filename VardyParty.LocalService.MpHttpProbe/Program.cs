using System.Net;
using VardyParty.LocalService.V1.Scrape;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine(
        """
        Resolve every chip on an MP match page with MpHttpStreamResolver,
        then check each HLS playlist for actual video bytes.

        Usage:
          dotnet run --project VardyParty.LocalService.MpHttpProbe -- <match-page-url>

        Example:
          dotnet run --project VardyParty.LocalService.MpHttpProbe -- https://www.example.test/football/match-12345/home-vs-away.html
        """);
    return args.Length == 0 ? 1 : 0;
}

var pageUrl = args[0].Trim();
if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri)
    || pageUri.Scheme is not ("http" or "https"))
{
    Console.Error.WriteLine("First argument must be an http(s) match page URL.");
    return 1;
}

using var handler = new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    AutomaticDecompression = DecompressionMethods.All
};
using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
var resolver = new MpHttpStreamResolver(http);
var probe = new HlsVideoDataProbe(http);

Console.WriteLine($"Page: {pageUrl}");
Console.WriteLine("Resolver: MpHttpStreamResolver (all chips)");
Console.WriteLine();

IReadOnlyList<MpHttpResolvedStream> streams;
try
{
    streams = await resolver.ResolveAsync(pageUrl);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Resolve failed: {ex.Message}");
    return 1;
}

var anyVideo = false;
foreach (var stream in streams)
{
    Console.WriteLine($"Chip: {stream.Name} (streamId={stream.StreamId})");
    Console.WriteLine($"M3U8: {stream.M3u8Url}");

    var report = await probe.ProbeAsync(stream.M3u8Url);
    anyVideo |= report.HasVideoData;
    Console.WriteLine($"Video data: {(report.HasVideoData ? "YES" : "NO")}");

    foreach (var playlist in report.Playlists)
    {
        var kind = playlist.IsMaster ? "master" : "media";
        Console.WriteLine($"  [{kind}] HTTP {playlist.StatusCode} {playlist.RequestedUrl}");
        if (!string.Equals(playlist.RequestedUrl, playlist.FinalUrl, StringComparison.Ordinal))
        {
            Console.WriteLine($"         final {playlist.FinalUrl}");
        }

        foreach (var variant in playlist.VariantUrls)
        {
            Console.WriteLine($"         variant {variant}");
        }

        if (playlist.SegmentUrl is not null)
        {
            var segment = playlist.Segment;
            Console.WriteLine(
                $"         segment HTTP {segment?.StatusCode} {segment?.Body.Length ?? 0} bytes {playlist.Note}");
            Console.WriteLine($"         {playlist.SegmentUrl}");
        }
        else if (!string.IsNullOrWhiteSpace(playlist.Note))
        {
            Console.WriteLine($"         {playlist.Note}");
        }
    }

    Console.WriteLine();
}

Console.WriteLine(anyVideo
    ? "Result: at least one playlist served video bytes."
    : "Result: playlists resolved, but no video bytes were fetched.");

return anyVideo ? 0 : 2;
