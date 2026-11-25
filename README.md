# VardyParty Local Service

**For local development setup**, see [DEVELOPMENT.md](./DEVELOPMENT.md).

**V2/MP pages** use a different API than **V1/FB**: [docs/MP_CHROME.md](docs/MP_CHROME.md) (run it on your desktop) and [docs/MP_PROTOCOL.md](docs/MP_PROTOCOL.md) (`POST /mp`).

## Install as a background service

### Windows (Windows Service installer)

Run from an elevated PowerShell prompt:

```powershell
./scripts/install-service-windows.ps1
```

What it does:

- Publishes `VardyParty.LocalService`
- Installs files to `C:\Program Files\VardyParty\LocalService`
- Creates/recreates the Windows service
- Starts the service

Optional custom install directory:

```powershell
./scripts/install-service-windows.ps1 -InstallDirectory "C:\VardyParty\LocalService"
```

If you already published and only want to reinstall the service:

```powershell
./scripts/install-service-windows.ps1 -SkipPublish
```

### Linux (systemd)

First publish:

```bash
dotnet publish ./VardyParty.LocalService/VardyParty.LocalService.csproj -c Release
```

Then install:

```bash
chmod +x ./local-scripts/install-service-linux.sh
./local-scripts/install-service-linux.sh
```

Optional custom publish directory (for example an extracted self-contained release tarball):

```bash
./local-scripts/install-service-linux.sh /path/to/publish
```

The script starts the native `VardyParty.LocalService` executable only when the directory is a **self-contained** publish (its `VardyParty.LocalService.runtimeconfig.json` lists `includedFrameworks`, or `libcoreclr.so` is next to the dll), which needs no .NET runtime. For a framework-dependent publish such as the one above it runs `dotnet VardyParty.LocalService.dll` with the `dotnet` found on your `PATH`, even though that publish also contains an apphost; this needs the ASP.NET Core 11 runtime.

### macOS (launchd)

First publish:

```bash
dotnet publish ./VardyParty.LocalService/VardyParty.LocalService.csproj -c Release
```

Then install:

```bash
chmod +x ./local-scripts/install-service-macos.sh
./local-scripts/install-service-macos.sh
```

Optional custom publish directory (for example the extracted `VardyParty.LocalService-osx-x64-<version>.tar.gz` release asset, which is self-contained):

```bash
./local-scripts/install-service-macos.sh /path/to/publish
```

Like the Linux script, it launches the native `VardyParty.LocalService` executable only for a **self-contained** publish (`includedFrameworks` in the runtimeconfig, or `libcoreclr.dylib` next to the dll), which needs no .NET runtime. For a framework-dependent publish it runs `dotnet VardyParty.LocalService.dll` with the `dotnet` on your `PATH`, which needs the ASP.NET Core 11 runtime.

A tarball downloaded with a browser carries the Gatekeeper quarantine attribute, and launchd may refuse to start the (not notarized) executable until you clear it on the extracted folder:

```bash
xattr -dr com.apple.quarantine /path/to/publish
```

## Runtime notes

- Linux service unit name: `vardyparty-local-service`
- macOS launchd label: `com.vardyparty.localservice`
- Windows service name default: `VardyPartyLocalService`
- Service host integration is enabled in app startup:
  - `UseWindowsService()` on Windows
  - `UseSystemd()` on Linux

## Installer Bootstrapper

The project provides a Windows installer helper, the InstallerBootstrapper. See [docs/BOOTSTRAPPER.md](./docs/BOOTSTRAPPER.md) for details about what the bootstrapper does, how it affects the MSIX and build, firewall rule lifecycle, and guidance on why we keep it.

## Strategies package auto-update

LocalService consumes the `VardyParty.LocalService.*` strategy packages that the [Strategies](https://github.com/Vardy-Party/Strategies) repo publishes to GitHub Packages. Pins move forward automatically when Strategies publishes:

1. Strategies' `publish-nugets` job pushes the changed packages, then sends a `repository_dispatch` (`strategies-packages-published`) to this repo with the published ids and versions, its run URL and its commit SHA.
2. [`.github/workflows/update-strategies-packages.yml`](.github/workflows/update-strategies-packages.yml) first validates the payload strictly. `run_url` must be a `Vardy-Party/Strategies` Actions run URL and `source_sha` a 40-character hex SHA; anything else fails the run.
3. It resolves **all five** Strategies packages to the highest stable version on the feed. The payload versions are only minimums to wait for (GitHub Packages can lag a push by a few seconds). Because every id is resolved on every run, a dispatch that GitHub cancels while another run is queued loses nothing. It never pins a local-only pack.
4. It raises the matching `<PackageReference Version="…">` pins in every `.csproj` and never lowers one. A reference it can't rewrite is reported as a `::warning::` on the run and listed in the PR: one with no `Version` attribute, or a value that isn't `MAJOR.MINOR.PATCH` (`$(Prop)`, `0.2.*`).
5. It commits on a `bot/strategies-packages-<run number>` branch and opens a PR. If a bot PR is already open, or a re-run finds its own branch without a PR, it reuses that branch: it merges `main` in (no force-push), adds the new pins and updates the PR. Commits are authored as `Vardy-Party (strategies-packages bot)` with `Run:` / `Strategies-Run:` / `Strategies-Commit:` trailers.
6. CI runs on the bot PR like any other PR. **Nothing merges automatically**; review the green PR and merge it by hand. Main Packaging then cuts the release as usual.

You can also run **Actions → Update Strategies packages → Run workflow** by hand. Leave `packages` empty to pick up the latest stable versions, or pass minimums as a JSON map such as `{"VardyParty.LocalService.Abstractions":"0.2.7"}`.

The decisions live in [`.github/scripts/StrategiesPackages.ps1`](.github/scripts/StrategiesPackages.ps1) and are covered by Pester tests in `StrategiesPackages.Tests.ps1`. The **Script tests** job in `ci.yml` runs them on Linux, Windows and macOS for every PR and push to `main`, and the workflow re-runs them before it touches anything.

One limitation, by design: the bot token has no Workflows permission, and GitHub rejects a push whose new commits change `.github/workflows/`. If `main` changed a workflow since an open bot branch was created, the run stops with a message. Merge or close the bot PR and re-run; the next run starts a fresh branch from `main`.

### Required secrets

Two separate fine-grained personal access tokens, both owned by `Vardy-Party` (Settings → Developer settings → Fine-grained tokens). Give each one an expiry (90 days is a sensible default), and note the expiry date here when you create or rotate it.

| Repo that holds the secret | Secret | Repository access | Permissions | Used for |
|---|---|---|---|---|
| Strategies | `M3U8_RESOLVER_DISPATCH_TOKEN` | M3U8-resolver only | **Contents: Read and write** only (Metadata: read is added automatically). **No** Pull requests permission. | `POST /repos/Vardy-Party/M3U8-resolver/dispatches`. GitHub lists this endpoint under Contents: write for fine-grained tokens; nothing smaller works. Because GitHub has no narrower permission for dispatch, this token can also push to M3U8-resolver branches (including `main`); it cannot open PRs. Treat it as a write credential. |
| M3U8-resolver | `M3U8_RESOLVER_BOT_TOKEN` | M3U8-resolver only | **Contents: Read and write**, **Pull requests: Read and write** (Metadata: read automatic). No Workflows permission. | Pushing the bot branch and opening or editing the bot PR. |

Expiry dates: `M3U8_RESOLVER_DISPATCH_TOKEN`: _not set yet_. `M3U8_RESOLVER_BOT_TOKEN`: _not set yet_.

The workflow also uses the existing `NUGET_GITHUB_TOKEN` / `NUGET_GITHUB_USERNAME` secrets to read the feed.

Why PATs and not `GITHUB_TOKEN`: `GITHUB_TOKEN` cannot call another repo's dispatch API, and pushes and PRs it creates do not start other workflows, so CI would never run on the bot PR. If Strategies' secret is missing, or the dispatch call fails, Strategies only logs a warning (its publish still succeeds); run this workflow by hand. If `M3U8_RESOLVER_BOT_TOKEN` is missing, this workflow fails at its first step with an explanatory error.

### Skip-CI tokens

Never put a skip-CI token in a bot commit or PR. GitHub skips `push` and `pull_request` workflows for a head commit whose message contains `[skip ci]`, `[ci skip]`, `[no ci]`, `[skip actions]` or `[actions skip]` (any case), and a `skip-checks: true` trailer suppresses checks. Either would mean the bot PR never builds. The workflow enforces this: `Assert-NoSkipCiToken` checks every bot commit message, PR title and PR body and fails the run if one appears. Payload fields are validated before they reach any of that text.

The `[skip ci]` on Bump Display Version and Strategies release commits only stops CI from re-running on those bookkeeping commits. A `repository_dispatch` is not tied to a commit message, so `[skip ci]` never suppresses it.
