# VardyParty.LocalService.Client

Client library for discovering and calling a VardyParty Local Service on the LAN.

## Play resolution (V1/FB)

```csharp
var client = new LocalServicePlayClient(httpClient);
var result = await client.ResolveAsync(new PlayRequest
{
    PageUrl = "https://player.example/match.html",
    StreamName = "stream-label-1" // optional → ?stream=stream-label-1
});
```

When `StreamName` is omitted on a multi-stream page, the local service selects the first listed stream.

## V2/MP resolution (`POST /mp`)

V1/FB pages stay on `GET /play/{url}`. V2/MP match pages need installed Chrome.
`MpPlayRequest` / `MpPlayResult` live in `VardyParty.LocalService.Abstractions.Models` (Abstractions 0.2.0+):

```csharp
using VardyParty.LocalService.Abstractions.Models;
var client = new LocalServicePlayClient(httpClient);
if (await client.SupportsMpChromeAsync())
{
    var mp = await client.ResolveMpAsync(new MpPlayRequest
    {
        PageUrl = "https://www.example.test/football/match.html",
        Stream = "Chip A"
    });
    // mp.Url = playlist the client can GET
    // mp.RequestHeaders = iframe Referer / Origin / User-Agent
    // mp.RewrittenSegments = video/mp2t URLs (do not probe playlist-relative .json)
}
```

Prefer catalog `resolutionStrategy` / `source` for routing. When `ResolveAsync` selects V2/`POST /mp`,
the returned `PlayResult` includes `RewrittenSegments` and `Streams` from the MP payload so callers
do not need a separate `ResolveMpAsync` just to keep playback data. Use `ResolveMpAsync` when you
need the full `MpPlayResult` (browser info, script URLs, etc.).

Bring-up on a desktop: [docs/MP_CHROME.md](docs/MP_CHROME.md). Contract: [docs/MP_PROTOCOL.md](docs/MP_PROTOCOL.md).
