# MP vs V1/FB play protocol

LocalService exposes two play APIs. Mobile, TV, and desktop clients call the desktop machine; only the desktop runs a browser.

Strategy selection is **catalog-driven** (`resolutionStrategy` / `source`): **v1** / **fb** → `GET /play`, **v2** / **mp** → `POST /mp`. Do not route plug-ins by host sniffing.

## V1 / FB pages — unchanged

```http
GET /play/{url-escaped-page}?stream={optional}
Authorization: Bearer <Auth0 access token>
```

Playwright launches bundled Chromium, clicks a play button, returns:

```json
{
  "url": "https://cdn.example/playlist.m3u8",
  "requestHeaders": { "referer": "...", "user-agent": "..." }
}
```

Capability: `play.stream` (and `mp` for multi-stream chip pages that still use Playwright `/play`).

## V2 / MP match pages — new

```http
POST /mp
Authorization: Bearer <Auth0 access token>
Content-Type: application/json
```

```json
{
  "pageUrl": "https://www.example.test/football/match-12345/home-vs-away.html",
  "stream": "Chip A",
  "listOnly": false
}
```

| Field | Required | Notes |
| --- | --- | --- |
| `pageUrl` | yes | Absolute `http(s)` match URL from the API |
| `stream` | no | Chip label (e.g. `Chip A`) or 0-based index. Omitted = autoplay / first chip |
| `listOnly` | no | Return chip names without waiting for `.m3u8` |
| `streamResults` | no | Write NDJSON events as each chip playlist is captured. Also accepted as `X-Vardy-Mp-Stream: 1` or `Accept: application/x-ndjson`. |

Capability advertised on `GET /health`: **`mp.chrome`**.

### Response (200)

```json
{
  "url": "https://cdn.example/hedy/sx_805438/index.m3u8",
  "requestHeaders": {
    "user-agent": "Mozilla/5.0 … Chrome/…",
    "referer": "https://player.example/",
    "origin": "https://player.example"
  },
  "streams": ["Chip A", "Chip B"],
  "selectedStream": "Chip A",
  "playerIframe": "https://player.example/",
  "rewrittenSegments": [
    "https://edge.example/cfall/s2001/v3b/…/seg.exe?_ver=&_s1=&_s2="
  ],
  "clientCapable": {
    "getPlaylist": true,
    "getPlaylistRelativeSegments": false
  },
  "browser": {
    "engine": "system-chrome",
    "cdpAttached": true,
    "wipedToAboutBlank": false,
    "finalUrl": "https://player.example/football/…",
    "chromePath": "/usr/bin/google-chrome-stable"
  },
  "timings": { "start": 0, "total": 12345 }
}
```

`url` + `requestHeaders` are enough for existing `M3U8Response` deserializers. Extra fields are for health and later playback.

### Streaming response (`streamResults` / `X-Vardy-Mp-Stream: 1`)

`Content-Type: application/x-ndjson`. One JSON object per line, flushed as Chrome captures it. Page 2 stays open; chips after the first use a short warm wait.

```json
{"type":"chips","streams":["Chip A","Chip B"],"selectedStream":"Chip A"}
{"type":"chip","label":"Chip A","url":"https://cdn.example/a.m3u8","requestHeaders":{"referer":"https://player.example/"},"rewrittenSegments":["https://edge.example/seg.exe?_s2="]}
{"type":"chip","label":"Chip B","url":"https://cdn.example/b.m3u8","requestHeaders":{"referer":"https://player.example/"},"rewrittenSegments":["https://edge.example/seg2.exe?_s2="]}
{"type":"done"}
```

| `type` | When |
| --- | --- |
| `chips` | Labels are known. Clients can start overlay totals. |
| `chip` | One playlist (or a failed label with `url` omitted) |
| `done` | Chrome is finished |
| `error` | Failed before harvest (`statusCode` 404 / 409 / 422) |

Clients that omit the stream flag still receive one JSON body after the first chip only. The compute host forwards each NDJSON line as an `rpc-part` frame, then `rpc-result`.

### Status codes

| Code | Meaning |
| --- | --- |
| 200 | Playlist captured (or `listOnly` with chips) |
| 400 | Missing / invalid `pageUrl` |
| 401 | Missing / invalid Auth0 Bearer token |
| 403 | Authenticated but missing `stream-viewer` / `read:streams` |
| 404 | No chips / named stream / m3u8 |
| 409 | Tab wiped to `about:blank` |
| 422 | Chrome executable not found |
| 500 | Unexpected exception |

`GET /play` uses the same Auth0 gate. `GET /health` remains anonymous for discovery.

## Split of work

### Must run on the desktop (LocalService + Chrome)

1. Open the MP match URL in **installed** Chrome (no `--enable-automation`).
2. Survive JS navigation to the player + `player.html` iframe.
3. Select a chip when the client asked for a named stream.
4. Observe the real `.m3u8` request and the **rewritten** media requests (`_s2=` / `video/mp2t`).
5. Return playlist URL, iframe `Referer`/`Origin`/`User-Agent`, and sample rewritten segment URLs.

### Can run on phone / TV / desktop app (HttpClient)

1. `GET` the playlist with a Chrome UA. Referer is optional for some CDNs; send it anyway.
2. Parse `#EXTM3U` even when `Content-Type` is `text/html`.
3. Probe a **rewritten** segment URL with the same headers (include `Origin`). Do not GET playlist-relative `*.json`.

### Still not client-capable without more LocalService work

- Generating the next rewritten segment URL (hosts and decoy extensions rotate in page JS).
- Playing the origin playlist’s listed segments as MPEG-TS.
- A LAN HLS proxy (`http://<desktop>:5019/mp/hls/…`) if TV players cannot use rewritten URLs. Not in this drop; `/mp` hands the playlist back instead.

## How LocalService implements the browser step

1. Resolve Chrome via `CHROME`, `Chrome:ExecutablePath`, then OS-specific paths (Chrome, then Chromium, then Edge).
2. `Process.Start` with a throwaway `--user-data-dir`, `--remote-debugging-port`, `--exclude-switches=enable-automation`, and `--disable-blink-features=AutomationControlled`. Start tab is `about:blank`. Append `--dns-over-https-mode=automatic` and `--dns-over-https-templates=https://{ip}/dns-query` only when `X-Vardy-Doh` is `1` and `X-Vardy-Doh-Resolver` parses as an IP. A missing `X-Vardy-Doh` header, or `0`, means system DNS, so those flags are not passed. When `X-Vardy-Pop-Proxy` is accepted, do not pass `--dns-over-https-mode` or `--dns-over-https-templates` on that launch.
3. Wait until `http://127.0.0.1:{port}/json/version` is up.
4. Attach a **Network/Page/Input-only** CDP session (not Playwright `ConnectOverCDP`). Playwright enables Runtime and live MP tabs wipe to `about:blank`.
5. `Page.navigate` to the match URL. Capture `.m3u8` from Network events. Click the viewport via `Input.dispatchMouseEvent`. `Runtime.evaluate` is only used when `listOnly` or a named chip is requested.
6. Kill the Chrome process tree and delete the profile when the request ends.

## POST /dns

Same stream-viewer Auth0 requirement as `POST /mp`.

```http
POST /dns
Authorization: Bearer <Auth0 access token>
Content-Type: application/json
```

```json
{ "enabled": true, "resolver": "1.1.1.1" }
```

| Field | Required | Notes |
| --- | --- | --- |
| `enabled` | yes | `true` enables DoH. `false` uses system DNS. |
| `resolver` | when `enabled` is true | IP address. The launch template is `https://{ip}/dns-query`. |

This closes the long-lived Playwright browser so the next `GET /play` launches with that template. `POST /mp` does not read this preference; each `/mp` request applies DoH only from `X-Vardy-Doh` and `X-Vardy-Doh-Resolver` on that request. When `X-Vardy-Pop-Proxy` is accepted on a relayed `/play` or `/mp` call, that pop proxy wins: do not pass `--dns-over-https-mode` or `--dns-over-https-templates` on that launch.

## Client routing

Prefer catalog fields: `resolutionStrategy: "v2"` or `source: "mp"` → `POST /mp` when `GET /health` includes `mp.chrome`. Otherwise → `GET /play/{url}` for `v1` / `fb`.

Strategy packages (`Abstractions`, `V1`, `V2`) live in [Vardy-Party/Strategies](https://github.com/Vardy-Party/Strategies) and expose `IPlaybackTransportPlugin` for hosts and Client. URL host sniffing is not used for plug-in choice.
