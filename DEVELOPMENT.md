# Local Development

## Prerequisites

- **.NET 11 SDK** (11.0.100 or a later 11.0 feature band). The root `global.json` pins it, and CI reads the same file via `actions/setup-dotnet` `global-json-file`, so a machine that only has SDK 10 gets a clear "install SDK 11" error.
- **PowerShell** (`pwsh` or Windows PowerShell for Windows)
- Git
- **Google Chrome** if you will exercise `POST /mp` (V2/MP). Playwright Chromium is not enough for those pages.

V2/MP local bring-up (headed Chrome, `curl`, chip list, what the client can GET): **[docs/MP_CHROME.md](docs/MP_CHROME.md)**. Protocol vs `GET /play`: **[docs/MP_PROTOCOL.md](docs/MP_PROTOCOL.md)**.

## Quick Start

### 1. Clone and restore

```bash
git clone https://github.com/Vardy-Party/local-service.git
cd local-service
# Authenticate to GitHub Packages for VardyParty.* strategy NuGets (from Strategies).
# CI uses repo secrets NUGET_GITHUB_TOKEN + NUGET_GITHUB_USERNAME (read:packages PAT).
# Locally put the same keys in .env (see .env.example), then:
#   pwsh ./scripts/ensure-github-packages-auth.ps1
# Credentials must use source key github-vardy-party (NuGet.config <clear /> drops differently named user sources).
# Optional: pack Strategies to ../Strategies/artifacts/nuget — run-from-source.ps1 adds that via RestoreAdditionalProjectSources.
dotnet restore VardyParty.LocalService.slnx
```

#### Testing unreleased Strategies packages

The committed `NuGet.config` lists only nuget.org and the GitHub Packages feed, so every committed `<PackageReference>` pin must be a version that is actually published. CI restores from a clean checkout and fails otherwise. Pin only versions that Strategies' `publish-nugets` job pushed. The **Update Strategies packages** workflow does this for you (README, "Strategies package auto-update").

To try a locally packed Strategies build without committing a pin to it, opt in per shell. Either:

- run `pwsh ./scripts/run-from-source.ps1`, which adds `../Strategies/artifacts/nuget` with `RestoreAdditionalProjectSources`; or
- create a git-ignored `NuGet.local.config` next to `NuGet.config` and restore with it explicitly:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
    <add key="local-strategies" value="./local-nuget" />
    <add key="github-vardy-party" value="https://nuget.pkg.github.com/Vardy-Party/index.json" />
  </packageSources>
</configuration>
```

```powershell
dotnet restore VardyParty.LocalService.slnx --configfile NuGet.local.config
dotnet build VardyParty.LocalService.slnx --no-restore
```

The offline release script (`scripts/build-local-release.ps1`) already passes `local-nuget` explicitly with `/p:RestoreAdditionalProjectSources=…`, so it doesn't need the committed source. Before you commit a csproj, revert any pin that points at a local-only version.

### 2. Build

```bash
dotnet build VardyParty.LocalService.slnx -c Debug
```

### 3. Run locally

#### As a console application (from source)

Preferred wrappers (self-locating; `--no-launch-profile` so discovery ports `5019+` are used):

```powershell
pwsh ./scripts/run-from-source.ps1
# optional: -Configuration Release
```

```bash
chmod +x ./scripts/run-from-source.sh   # once
./scripts/run-from-source.sh
# optional: --configuration Release
```

Or invoke `dotnet` directly:

```bash
dotnet run --project VardyParty.LocalService/VardyParty.LocalService.csproj --no-launch-profile
```

#### As a Windows Service

##### Debug build (for local development)

```powershell
# Build & publish (Debug)
dotnet publish VardyParty.LocalService/VardyParty.LocalService.csproj `
  -c Debug `
  -r win-x64 `
  --self-contained true `
  -o ./artifacts/publish/debug/win-x64

# Copy installer script
Copy-Item scripts/install-service-windows.ps1 -Destination ./artifacts/publish/debug/win-x64/

# Run as Administrator
cd ./artifacts/publish/debug/win-x64
.\install-service-windows.ps1 -InstallDirectory 'C:\VardyParty.Dev'
```

##### Release build (production-like local testing)

```powershell
# Build & publish (Release)
dotnet publish VardyParty.LocalService/VardyParty.LocalService.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o ./artifacts/publish/release/win-x64

# Copy installer script
Copy-Item scripts/install-service-windows.ps1 -Destination ./artifacts/publish/release/win-x64/

# Run as Administrator
cd ./artifacts/publish/release/win-x64
.\install-service-windows.ps1 -InstallDirectory 'C:\Program Files\VardyParty\LocalService'
```

To uninstall:
```powershell
.\scripts/uninstall-service-windows.ps1
```

### Release Build Details

For a full release build matching CI pipeline outputs:

```powershell
# 1. Build & test the entire solution
dotnet build VardyParty.LocalService.slnx -c Release
dotnet test VardyParty.LocalService.slnx -c Release --no-build

# 2. Publish service with optimizations
dotnet publish VardyParty.LocalService/VardyParty.LocalService.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o ./artifacts/publish/release/win-x64

# 3. Install locally
Copy-Item scripts/install-service-windows.ps1 -Destination ./artifacts/publish/release/win-x64/
cd ./artifacts/publish/release/win-x64
.\install-service-windows.ps1
```

The release build includes:
- Full optimizations (`-c Release`)
- Self-contained runtime (no .NET dependency on target machine)
- All dependencies bundled in the output directory
- Minimal file size and maximum performance

## Testing

### Run unit tests
```bash
dotnet test VardyParty.LocalService.slnx -c Debug --filter "Category!=Integration"
```

Live Chrome fixture (requires installed Chrome; tests live in Strategies):

```bash
dotnet test ../Strategies/VardyParty.LocalService.V2.Scrape.Tests -c Debug --filter "Category=Integration"
```

### Run specific test
```bash
dotnet test VardyParty.LocalService.slnx -c Debug --filter "MethodName"
```

## Project Structure

- **VardyParty.LocalService** – Thin host: PackageReferences scrape plugins + registers `AddV1ScrapePlugin` / `AddV2ScrapePlugin`
- **VardyParty.LocalService.Client** – NuGet client library for connecting to the service

Portable strategy packages (`Abstractions` / `V1` / `V2` / `V1.Scrape` / `V2.Scrape`) are published from
[Vardy-Party/Strategies](https://github.com/Vardy-Party/Strategies) to GitHub Packages. Restore uses `NuGet.config`
(`github-vardy-party` + optional `../Strategies/artifacts/nuget` local feed).
- **scripts/** – Installation/management scripts for Windows, Linux, macOS

## Configuration

The service reads configuration from `appsettings.json` and platform-specific overrides (later sources win):
- `appsettings.Development.json` (development)
- Optional gitignored `VardyParty.LocalService/appsettings.Local.json` (copy from `appsettings.Local.json.example`). Loaded only when `ASPNETCORE_ENVIRONMENT` is not `Testing`. JWT is bound at startup — edit the file and restart. See [Local from-source Auth0](#local-from-source-auth0).
- User secrets (`dotnet user-secrets` — project id `vardyparty-localservice-auth0`) win over Local.json
- Environment variables (`Auth0__Domain`, `Auth0__Audience`, or Worker-style `AUTH0_DOMAIN` / `AUTH0_AUDIENCE` / `AUTH0_CLAIM_NAMESPACE`) and command-line / host `UseSetting` win over Local.json and user-secrets

### Auth0 (required)

LocalService validates the same Auth0 access tokens the Client already sends on `/play` and `/mp`
(via `Auth0ApiTokenHandler`). `/health` stays anonymous for LAN discovery.

| Setting | Purpose |
| --- | --- |
| `Auth0:Domain` | JWKS / login / issuer host (no scheme). Use the **custom Auth0 domain** (same as Worker `AUTH0_DOMAIN` / Client), not a stale `*.auth0.com` tenant when tokens use the custom `iss` |
| `Auth0:Audience` | API audience (must match Client + Worker) |
| `Auth0:ClaimNamespace` | Optional; roles/permissions under `{ns}/roles` and `{ns}/permissions` |
| `Auth0:RequiredRoleClaimType` | Optional Client-style role claim type |
| `Auth0:RequiredRole` | Default `stream-viewer` |
| `Auth0:RequiredPermission` | Default `read:streams` |

### Scrape health (`scrape.health`)

When the Client opts into stream data collection it sends `X-VP-Api-Base-Url` plus
match headers and its Bearer; LocalService POSTs telemetry to that API base.
Reporting is a no-op unless the base host is allowlisted.

| Setting / env | Purpose |
| --- | --- |
| `ScrapeHealth:AllowedApiHosts` | Exact hosts or leading-`.` suffixes (HTTPS only). Empty by default in shipped `appsettings.json`. |
| `SCRAPE_HEALTH_ALLOWED_API_HOSTS` | Comma-separated list merged into `AllowedApiHosts` by `merge-appsettings-auth0.ps1` (publish/install). Also loaded from `.env` via `Import-VpAuth0Env`. |
| `Auth0:Audience` | When this value is an `https://…` DNS URI, its host is added to the allowlist automatically. |

Audience alone is **not** enough when Client `HeadlessBaseUrl` (or the host sent as
`X-VP-Api-Base-Url`) differs from the Auth0 API identifier — for example a preview
Worker host vs a shared audience URL. Add every such host to
`SCRAPE_HEALTH_ALLOWED_API_HOSTS` / `AllowedApiHosts` (no real hostnames in source
or tests; use config / `.env` only).

Local from-source allowlist options (any one is enough; all fold into
`ScrapeHealth:AllowedApiHosts` before the allowlist is built):

| Option | How |
| --- | --- |
| `appsettings.Local.json` | `"ScrapeHealth": { "AllowedApiHosts": [ "preview.example.test", ".example.test" ] }` |
| Indexed env | `ScrapeHealth__AllowedApiHosts__0=…` (ASP.NET Core env binding) |
| Packaging-aligned CSV | Repo-root `.env` `SCRAPE_HEALTH_ALLOWED_API_HOSTS=host1,.suffix` — loaded by `Import-VpAuth0Env` / `run-from-source.sh` and expanded to indexed keys (Program also folds the CSV into configuration for systemd units that set only the CSV name) |

Packaged installs rely on the publish merge path (`merge-appsettings-auth0.ps1`) above.

Local Development setup (values must match Client user-secrets / Worker):

```powershell
cd VardyParty.LocalService
dotnet user-secrets set "Auth0:Domain" "<same as Client / Worker AUTH0_DOMAIN>"
dotnet user-secrets set "Auth0:Audience" "<same as Client>"
dotnet user-secrets set "Auth0:RequiredRoleClaimType" "<same as Client>"
dotnet user-secrets set "Auth0:RequiredRole" "stream-viewer"
```

Without Domain/Audience the host refuses to start (except `Testing` with auth bypass).

### Release packaging (MSIX / Snap / macOS)

Installer packaging, Auth0 injection, and signing run in M3U8-resolver from the published host packages and the Strategies packages. This repo does not hold those secrets and does not create the GitHub Release. Values are applied to the published `appsettings.json` at release time — never commit real Domain/Audience to source.

| Secret | Required | Notes |
| --- | --- | --- |
| `AUTH0_DOMAIN` | yes | Custom login/JWKS host — same as Client / Worker |
| `AUTH0_AUDIENCE` | yes | Same as Client / Worker |
| `AUTH0_REQUIREDROLECLAIMTYPE` | no | Same as Client when used |
| `AUTH0_REQUIREDROLE` | no | Defaults to `stream-viewer` in appsettings |
| `AUTH0_CLAIM_NAMESPACE` | no | Optional namespaced roles/permissions |
| `AUTH0_REQUIRED_PERMISSION` | no | Defaults to `read:streams` |
| `SCRAPE_HEALTH_ALLOWED_API_HOSTS` | no* | Comma-separated hosts for `ScrapeHealth:AllowedApiHosts`. *Required for scrape-health when Headless/API host ≠ Auth0 Audience host. |

If `AUTH0_DOMAIN` or `AUTH0_AUDIENCE` is missing, packaging fails instead of shipping a
broken installer. Prefer syncing from a local `.env` (same pattern as the Client):

```powershell
copy .env.example .env   # fill Auth0 (+ scrape-health hosts + optional signing / NuGet)
pwsh scripts/sync-github-secrets-from-env.ps1
```

Then run **Release from public runtime** on M3U8-resolver.

### Local from-source Auth0

`scripts/run-from-source.ps1` and `scripts/run-from-source.sh` load Auth0 (+ optional
scrape-health allowlist) in this order:

1. Process env already set
2. Repo-root `.env` (`AUTH0_*`, `SCRAPE_HEALTH_ALLOWED_API_HOSTS`) via `Import-VpAuth0Env` / the shell loader — mirrors `AUTH0_*` → `Auth0__*` and expands the scrape CSV into `ScrapeHealth__AllowedApiHosts__N`
3. LocalService user-secrets (`vardyparty-localservice-auth0`) when Domain/Audience are still missing
4. Optional `VardyParty.LocalService/appsettings.Local.json` (see `appsettings.Local.json.example`)

Values must match the Client tenant/audience or `/play` and `/mp` return 401/403 while
`/health` and UDP discovery still succeed.

Which local install/publish entrypoints inject Auth0 into `appsettings.json` (via
`Import-VpAuth0Env` + `merge-appsettings-auth0.ps1`, then restore the source file):

| Script | Injects Auth0 / scrape hosts? |
| --- | --- |
| `scripts/build-and-install-msix.ps1` | Yes (`-AllowUserSecrets` / `-RequireDomainAudience`) |
| `scripts/install-service-windows.ps1` | Yes |
| `local-scripts/install-service-windows.ps1` | Yes |
| `scripts/ci-mock-installer.ps1` | Yes |
| `scripts/create-msix-only.ps1` | **No** — pack/sign only; use user-secrets, `appsettings.Local.json`, or an inject-capable script above |

Offline local release (Actions paused): `pwsh ./scripts/build-local-release.ps1`.

Builds and publishes:
- Strategies NuGet → GitHub Packages (`dotnet nuget push`; PAT needs `write:packages`)
- LocalService Windows MSIX + Pi arm64 snap → R2 `vardyparty-installers` **and** a
  GitHub Release on M3U8-resolver
- Client Windows MSIX + Android APK → GitHub Release on Client (`/releases/latest`)
  + R2 `client-release-latest.json` snapshot

Prompts for the PFX password first. Injects Auth0 into LocalService via
`merge-appsettings-auth0.ps1` (`.env`) and Client/Android via `PatchAppSettings`
(user-secrets). Patch-bumps LocalService / Client / Strategies versions (left dirty).

Needs siblings `Strategies`, `VardyParty-Client`, `VardyParty-API` (for `wrangler r2`),
Windows SDK, WSL+snapcraft for Pi (`-SkipPi` to skip), `.env` Auth0 +
`NUGET_GITHUB_TOKEN`, Client `certs/vardyparty.pfx`. Outputs under
`artifacts/local-release/<timestamp>/`. When Actions returns, official CD with a
higher version supersedes Client latest; re-run the M3U8-resolver release for R2 helpers.
(`build-release-msix-pair.ps1` forwards here.)

## Troubleshooting

### LAN Clients Cannot Discover Service (After Windows Update)

**Symptoms:** Android TV, phones, or other LAN clients cannot find the service when running as a Windows Service, but it works fine in Visual Studio.

**Root Cause:** Windows Services run without elevation to create firewall rules. Firewall rules must be created at **install time** with administrator privileges.

**Solution:**

1. **Rebuild and publish:**
   ```powershell
   dotnet build VardyParty.LocalService.slnx -c Release
   dotnet publish VardyParty.LocalService/VardyParty.LocalService.csproj `
     -c Release -r win-x64 --self-contained true `
     -o ./artifacts/publish/release/win-x64
   ```

2. **Stop the current service:**
   ```powershell
   Stop-Service VardyPartyLocalService -Force
   ```

3. **Open PowerShell as Administrator and run the installer:**
   ```powershell
   # Right-click PowerShell and select "Run as Administrator", then:
   cd ./artifacts/publish/release/win-x64
   Copy-Item .\scripts\install-service-windows.ps1 -Destination .
   .\install-service-windows.ps1
   ```

   Expected output:
   ```
   ✓ HTTP firewall rule created (TCP 5019-5573, all profiles)
   ✓ UDP discovery firewall rule created (UDP 40129-40189, all profiles)
   Installed and started service 'VardyPartyLocalService' from 'C:\Program Files\VardyParty\LocalService'.
   ```

4. **Verify firewall rules were created:**
   ```powershell
   netsh advfirewall firewall show rule name="VardyParty*"
   ```

   Expected output:
   ```
   Rule Name                              Direction  Profile
   ──────────────────────────────────────────────────────────────
   VardyParty.LocalService HTTP           In         Private,Public
   VardyParty.LocalService UDP Discovery  In         Private,Public
   ```

5. **Test from a client on LAN:**
   - On your Android TV, phone, or another PC on the network
   - Open the VardyParty app and check for service discovery
   - Or manually test: `curl http://192.168.x.x:5019/health` (replace with your PC's IP)

**Manual Firewall Rule Creation (if installer fails):**
   ```powershell
   # Run PowerShell as Administrator:
   netsh advfirewall firewall add rule name="VardyParty.LocalService HTTP" `
     dir=in action=allow protocol=tcp localport=5019-5573 `
     profile=private,public enable=yes `
     description="VardyParty.LocalService HTTP"

   netsh advfirewall firewall add rule name="VardyParty.LocalService UDP Discovery" `
     dir=in action=allow protocol=udp localport=40129-40189 `
     profile=private,public enable=yes `
     description="VardyParty.LocalService UDP Discovery"
   ```

### Service fails to start on Windows
1. Check the Windows Event Viewer: `Event Viewer > Windows Logs > System`
2. Run the service as a console app first to see logs:
   ```powershell
   dotnet VardyParty.LocalService.dll
   ```

### Port already in use
The service binds to a default port. Check if another instance is running:
```powershell
netstat -ano | findstr :PORT_NUMBER
```

### Changes not reflected after rebuild
1. Stop the service: `Stop-Service -Name VardyPartyLocalService`
2. Republish and reinstall

## Contributing

See [README.md](./README.md) for service installation on production systems.

This repo does not build installers. CI is [ci.yml](.github/workflows/ci.yml). Host packages are [publish-runtime-nugets.yml](.github/workflows/publish-runtime-nugets.yml). Installer releases are cut from those packages in M3U8-resolver.

## Versioning

**Source of truth:** `<Version>` in `VardyParty.LocalService/VardyParty.LocalService.csproj`.
The runtime NuGet version is that value. This repo does not create a GitHub Release.

**Ship a new version number:**

1. Actions → **Bump Display Version** → Run workflow on `main` → choose major / minor / **patch**.
2. The workflow bumps the csproj and `snap/snapcraft.yaml` and pushes straight to `main`. Branch protection on this repo rejects that direct push, so land the version change through a pull request when the push is blocked.
3. CI on `main` builds and tests. **Publish runtime NuGets** then pushes `VardyParty.LocalService.Runtime.<rid>`.

> **Caution:** Do not put the bracketed skip-ci token in PR titles, PR bodies, or squash/merge commit messages. If that token appears in any commit message on a push, GitHub skips **all** Actions for the push.


`VardyParty.LocalService.Client` keeps its own NuGet version and publishes separately when Client files change on `main` (see CI). It is not coupled to LocalService release numbering. CI pushes with `--skip-duplicate`, so **bump `<Version>` in `VardyParty.LocalService.Client.csproj` whenever the package contents change** (including its target frameworks); otherwise the push is silently skipped and the feed keeps the old build. 0.5.0 targets `net11.0` and `netstandard2.1`.

**One-time setup**

- `NUGET_GITHUB_TOKEN` with `read:packages` so CI can restore the host's package references. Strategy plugin assemblies are not included in the published nupkg.
- Workflow `permissions.packages: write` on **Publish runtime NuGets** so `GITHUB_TOKEN` can push the host packages. This repo does not create tags or GitHub Releases.
