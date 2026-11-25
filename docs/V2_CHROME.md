# MP Chrome on your machine

LocalService has a **second play protocol** for **V2/MP** match pages. **V1/FB** pages stay on `GET /play/{url}`. MP pages use **`POST /mp`**, which starts **installed Chrome** (Linux or Windows), not Playwright’s bundled Chromium.

Playwright-launched Chromium sets `navigator.webdriver`. Live MP pages then wipe the tab to `about:blank` before any `.m3u8` is requested. This path `Process.Start`s real Chrome and attaches **Network-only CDP** (not Playwright `ConnectOverCDP`). `--remote-debugging-port` would otherwise imply `--enable-automation`; `/mp` passes `--exclude-switches=enable-automation`.

Read this file to run the work on a desktop. The request/response contract is in [MP_PROTOCOL.md](./MP_PROTOCOL.md).

## What you need locally

- .NET 11 SDK
- This repo on the PR branch (not `main` until it merges)
- **Google Chrome** (preferred), or Chromium / Edge
- A LAN client (phone / TV / desktop app) is optional for the first check — `curl` is enough

| OS | Typical Chrome path |
| --- | --- |
| Windows | `C:\Program Files\Google\Chrome\Application\chrome.exe` |
| Linux | `/usr/bin/google-chrome-stable` or `/usr/bin/google-chrome` |
| macOS | `/Applications/Google Chrome.app/Contents/MacOS/Google Chrome` |

Override with environment variable `CHROME` or `appsettings` `Chrome:ExecutablePath`.

## Checkout and build

```bash
git clone <m3u8-resolver-remote>
cd m3u8-resolver   # or M3U8-resolver
git checkout cursor/fctv-hls-console-87f1
dotnet restore VardyParty.LocalService.slnx
dotnet build VardyParty.LocalService.slnx -c Debug
dotnet test VardyParty.LocalService.slnx -c Debug --filter "Category!=Integration"
```

(The branch name is historical; strategy naming in code/docs is **V1/FB** and **V2/MP** only.)

The Chrome fixture test is `[Trait("Category","Integration")]` in **Strategies** `V2.Scrape.Tests`. Run it when Chrome is installed:

```bash
dotnet test ../Strategies/VardyParty.LocalService.V2.Scrape.Tests -c Debug --filter "Category=Integration"
```

## Run LocalService as a console (best for first bring-up)

Do **not** install the Windows service until `POST /mp` works in a console. A console lets you see Chrome (if headed) and the logs.

Auth0 **Domain** + **Audience** are required (same tenant as the Client). Set via user-secrets or env (`AUTH0_DOMAIN` / `AUTH0_AUDIENCE`). See [DEVELOPMENT.md](../DEVELOPMENT.md#auth0-required).

```bash
dotnet run --project VardyParty.LocalService/VardyParty.LocalService.csproj
```

Listen port is the first free value in `5019, 5137, 5273, 5417, 5573`. Confirm:

```bash
curl -s http://127.0.0.1:5019/health
```

You want `"mp.chrome"` in `capabilities` (plus the existing `play.stream` / `mp`).

### Headed vs headless

`appsettings.json` defaults `Chrome:Headless` to `true` so a Windows service / systemd unit can run without a desktop session.

`appsettings.Development.json` sets `Chrome:Headless` to `false`, so **from-source** (`run-from-source.ps1`) shows a real Chrome window and any player/ad popups. That is intentional for debugging anti-bot wipes — not a packaging default.

On **your** interactive machine, if a live MP page wipes or never emits `.m3u8`, run headed:

```json
"Chrome": {
  "Headless": false,
  "ExecutablePath": null
}
```

Or for a one-off console:

```bash
# PowerShell
$env:Chrome__Headless = "false"
dotnet run --project VardyParty.LocalService/VardyParty.LocalService.csproj
```

```bash
# bash
Chrome__Headless=false dotnet run --project VardyParty.LocalService/VardyParty.LocalService.csproj
```

A Chrome window will flash per `/mp` request and then exit.

## Call `POST /mp`

Body is JSON (this is the protocol difference vs V1/FB `GET /play/...`). `/mp` and `/play` require an Auth0 access token (`Authorization: Bearer …`). `/health` does not.

```bash
curl -sS -X POST http://127.0.0.1:5019/mp \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"pageUrl":"https://www.example.test/football/match.html","stream":"Chip A"}'
```

Useful fields in a 200 body:

- `url` — CDN `.m3u8` the **client** can GET
- `requestHeaders` — `user-agent`, `referer`, `origin` from the player iframe
- `streams` / `selectedStream` — chip labels
- `rewrittenSegments` — live `video/mp2t` URLs the browser actually fetched (not playlist-relative `*.json`)
- `clientCapable.getPlaylist` — `true` when `url` is set
- `clientCapable.getPlaylistRelativeSegments` — always `false` for MP
- `browser.wipedToAboutBlank` — `true` means anti-debug still fired

List chips without waiting for media:

```bash
curl -sS -X POST http://127.0.0.1:5019/mp \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"pageUrl":"https://www.example.test/football/match.html","listOnly":true}'
```

The CLI wrapper does the same thing without the HTTP host:

```bash
dotnet run --project tools/FctvHls -- "https://www.example.test/football/match.html" --stream "Chip A" --headed
```

(`tools/FctvHls` is a historical project folder name; it talks V2/MP `POST /mp` semantics.)

`--list` prints chips only. Use a real match URL from the API when you try this live.

Give `/mp` about **60 seconds**. Opening Chrome, waiting for chips, and capturing HLS is slower than `GET /play`.

## What the client should do after `/mp`

Phone / TV / desktop **do not** need a browser. After LocalService returns:

1. `GET` `url` with Chrome `User-Agent`. Send `Referer` + `Origin` from `requestHeaders` when present.
2. Treat the playlist as real HLS (`#EXTM3U` / `#EXTINF`) even if `Content-Type` is `text/html`.
3. **Do not** health-check or play playlist-relative `*.json` on the playlist CDN host. Those 302 to HTML.
4. Health-check a URL from `rewrittenSegments` instead, using the same headers (including `Origin`). Those hosts rotate and use decoy extensions (`.exe`, `.swf`, …) with `Content-Type: video/mp2t`.

5. Read `streams` / `selectedStream`. The first `/mp` starts playback; enqueue every other chip and call `/mp` again with `stream: "Chip B"` (one Chrome pass each — LocalService is single-threaded).

Playback of the raw CDN playlist on TV will still fail for segments until the player uses rewritten URLs or LocalService grows a LAN HLS proxy. Getting the **m3u8** is the client-capable step this endpoint is built for.

V1/FB and other classic sites: keep using `GET /play/{escapedUrl}`.

## Config reference

| Key | Default | Meaning |
| --- | --- | --- |
| `Chrome:Headless` | `true` | `--headless=new` when true. Windows keeps Chromium's process sandbox (no `--no-sandbox`). |
| `Chrome:ExecutablePath` | unset | Full path to `chrome.exe` / `google-chrome` |
| env `CHROME` | unset | Same override as executable path |

Playwright Chromium is **still** installed for `GET /play`. `/mp` must not launch it.

## Troubleshooting on your machine

| Symptom | What to check |
| --- | --- |
| HTTP 422, Chrome not found | Install Chrome, or set `CHROME` / `Chrome:ExecutablePath` |
| HTTP 409, `wipedToAboutBlank` | Confirm logs say `system-chrome` and the path is Google Chrome, not `.playwright`. Do not open DevTools on that tab. `/mp` must not use Playwright `ConnectOverCDP`. Try `Headless: false`. DoH follows the client (`X-Vardy-Doh` / `X-Vardy-Doh-Resolver`); missing header means system DNS. |
| HTTP 404, no m3u8 | Match not live / countdown, wrong chip, or HLS only in a player popup/iframe. `/mp` blocks ad/popunder hosts and must not `--block-new-web-contents`. Try `--list` / `listOnly`. |
| Client health fails on `.json` | Expected. Probe `rewrittenSegments` with iframe `Origin`. |
| `/mp` times out from the app | Raise the HTTP timeout to ≥ 60s for this endpoint only. `GET /play` can stay shorter. |
| Service on Windows Session 0 | Headed Chrome will not show. Use a console for debugging; keep `Headless: true` for the installed service. |

## After it works in a console

Use the existing Windows / Linux service install docs in [DEVELOPMENT.md](../DEVELOPMENT.md) and [README.md](../README.md). Keep `Chrome:Headless` true for the service. The client discovers LocalService on the LAN the same way as before; it just needs a build that calls `POST /mp` when health advertises `mp.chrome`.
