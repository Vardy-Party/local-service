# Installer channels (what the website serves)

Three channels can disagree. After the offline release script, they must not.

| Channel | What it serves | Authoritative for |
| --- | --- | --- |
| **GitHub Releases (`Vardy-Party/Client`)** | Windows MSIX, Android APK, `vardyparty.cer` | Client installers. The site reads `/releases/latest` (with R2 `client-release-latest.json` as a rate-limit fallback). |
| **GitHub Releases (`Vardy-Party/M3U8-resolver`)** | LocalService Windows MSIX (+ optional Pi snap) | Helper installers mirrored for humans; the **site** does **not** pull helpers from here. |
| **R2 (`vardyparty-installers`)** | Stable keys: `VardyParty.LocalService-win-x64.msix`, linux snaps, `client-release-latest.json` | Website helper downloads (`/downloads/helper-*`). Client snapshot JSON only. |
| **Local source / `dotnet` / `package-android.ps1`** | Whatever is on disk | Dev only. Not what the website serves until you publish. |

## Offline local release (`scripts/build-local-release.ps1`)

Used while GitHub Actions minutes are paused. It builds installers, pushes Strategies packs, uploads helpers to R2, and creates GitHub Releases.

**Hard rule:** do not publish while Version / PackageReference bumps are still dirty.

1. Let the script patch-bump LocalService `Version`, Client `Version.props`, and Strategies pack versions.
2. **Commit** those bumps on each repo (Vardy-Party author) so HEAD matches the artifacts.
3. Publish (or `-PublishOnly` from `artifacts/local-release/<stamp>`).

The script writes `provenance.json` (commit SHAs, package pins, SHA-256 of artifacts) into the stamp folder and attaches it to the GitHub Releases. Release notes include the same summary. `gh release create` uses `--target <commit>` so the tag cannot float onto an older commit than the build. If a release tag already exists but points at a different commit, the script **deletes and recreates** the release (with `--cleanup-tag`) so the tag moves.

Assemblies are stamped with `/p:SourceRevisionId=<HEAD>` and `/p:InformationalVersion=<Version>+<HEAD>` so `ProductVersion` on the installed LocalService matches the tagged commit (not a squash-preimage SHA from a dirty PR branch).

Before upload, publish also asserts that the **committed** LocalService `<Version>` and Client `ApplicationDisplayVersion` equal the artifact versions being published.

`-AllowDirtyPublish` overrides the dirty check but tags still point at HEAD **without** the dirty bumps — that is how `v1.1.17` previously pointed at a tree whose csproj still said `1.1.13` and older scrape pins while the MSIX carried `1.1.17` + newer pins.

## Checking what a visitor installs

1. Site Client APK/MSIX URL → should match `gh release view --repo Vardy-Party/Client` latest asset digest.
2. Site helper → R2 object behind `/downloads/helper-windows` (Auth0 session required); size/hash should match the M3U8-resolver release asset for that LocalService version.
3. Installed LocalService `/health` `version` and `deps.json` package pins should match `provenance.json` / the LocalService csproj on the tagged commit.
