#Requires -Version 7.0
<#
.SYNOPSIS
  Offline local Release: Client Windows MSIX + Android APK + LocalService Windows MSIX
  + Raspberry Pi (linux-arm64) snap — then publish so example.invalid serves them.

.DESCRIPTION
  While GitHub Actions minutes are paused:
    1. Fail-fast preflight (PFX, Auth0, validated ``write:packages`` PAT, wrangler, gh)
    2. Patch-bump LocalService Version + Client ApplicationDisplayVersion
    3. Patch-bump Strategies packable Versions, pack, and ``dotnet nuget push`` to
       GitHub Packages (same feed CI uses) — needs a PAT with ``write:packages``
    4. Retarget LocalService PackageReference pins to those published versions
    5. Build installers with Auth0/appsettings injected
    6. Write provenance.json (repo SHAs, pins, artifact hashes)
    7. Upload LocalService helpers to R2 AND create a GitHub Release on M3U8-resolver
       targeting the LocalService HEAD commit that matches the stamped Version/pins
    8. Create a GitHub Release on Client targeting Client HEAD; refresh R2 snapshot

  Publish refuses when version-bearing files are still dirty relative to HEAD, so a
  GitHub tag cannot point at a commit whose Version/pins disagree with the MSIX/APK.
  Commit those bumps (LocalService csproj, Client Version.props, Strategies Versions)
  before -PublishOnly / the publish phase, or pass -AllowDirtyPublish (not recommended).
  Publish also asserts committed Version/display match artifact versions, stamps
  InformationalVersion=Version+HEAD on builds, and recreates a release when an
  existing tag points at the wrong commit.

  When Actions returns: official CD with a higher version becomes latest; re-upload
  helpers from Main Packaging as usual.

.PARAMETER SkipPublish
  Build artifacts only; do not upload R2, GitHub Releases, or NuGet packages.

.PARAMETER SkipNugetPublish
  Pack Strategies into local-nuget only (no GitHub Packages push / pin retarget).

.PARAMETER SkipPi
  Skip linux-arm64 snap (WSL/snapcraft).

.PARAMETER SkipLocalServiceWindows
  Skip rebuilding the LocalService Windows MSIX; reuse the newest matching
  ``artifacts/local-release/**/VardyParty.LocalService-win-x64-<version>.msix``
  for publish (use after a successful Win MSIX when retrying Pi / Client).

.PARAMETER SkipAndroid
  Skip Android APK.

.PARAMETER SkipClient
  Skip Client Windows MSIX + Android + Client GitHub Release.

.PARAMETER SkipLocalService
  Skip LocalService Windows + Pi + LocalService GitHub Release / R2 helpers.

.EXAMPLE
  pwsh ./scripts/build-local-release.ps1

.EXAMPLE
  # Resume after NuGet + Win MSIX already succeeded (retry Pi → Client → publish)
  pwsh ./scripts/build-local-release.ps1 -SkipVersionBump -SkipStrategiesPack -SkipLocalServiceWindows

.EXAMPLE
  # Re-publish latest artifacts/local-release folder (R2 + GitHub Releases only)
  pwsh ./scripts/build-local-release.ps1 -PublishOnly
#>
[CmdletBinding()]
param(
    [string]$ClientRepo = '',
    [string]$StrategiesRepo = '',
    [string]$ApiRepo = '',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipClient,
    [switch]$SkipLocalService,
    [switch]$SkipLocalServiceWindows,
    [switch]$SkipStrategiesPack,
    [switch]$SkipVersionBump,
    [switch]$SkipPi,
    [switch]$SkipAndroid,
    [switch]$SkipPublish,
    [switch]$SkipNugetPublish,
    [switch]$PublishOnly,
    [string]$PublishFromDir = '',
    [string]$PfxPath = '',
    [string]$PfxPassword = '',
    [ValidateSet('local', 'preview', 'production')]
    [string]$AndroidApi = 'production',
    # Allow GH/R2 publish while Version.props / csproj pins are still dirty.
    # Tags will still target HEAD (which will NOT include those bumps) — avoid.
    [switch]$AllowDirtyPublish
)

if ($PublishOnly -and [string]::IsNullOrWhiteSpace($PublishFromDir)) {
    $latest = Get-ChildItem (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')) 'artifacts\local-release') `
        -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        Select-Object -First 1
    if (-not $latest) { throw 'PublishOnly: no artifacts/local-release folders found. Pass -PublishFromDir.' }
    $PublishFromDir = $latest.FullName
}
if (-not [string]::IsNullOrWhiteSpace($PublishFromDir)) {
    $PublishOnly = $true
}
if ($PublishOnly) {
    # Before preflight so NuGet / WSL / PFX checks are skipped.
    $SkipVersionBump = $true
    $SkipStrategiesPack = $true
    $SkipPi = $true
    $SkipNugetPublish = $true
}

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot

function Get-DotEnvValue([string]$envFile, [string]$key) {
    if (-not (Test-Path -LiteralPath $envFile)) { return $null }
    foreach ($line in Get-Content -LiteralPath $envFile) {
        $trim = $line.Trim()
        if ($trim.StartsWith('#') -or $trim.Length -eq 0) { continue }
        $eq = $trim.IndexOf('=')
        if ($eq -lt 1) { continue }
        if ($trim.Substring(0, $eq).Trim() -ne $key) { continue }
        return $trim.Substring($eq + 1).Trim().Trim('"').Trim("'")
    }
    return $null
}

function Resolve-SiblingRepo([string]$explicit, [string[]]$candidates) {
    if (-not [string]::IsNullOrWhiteSpace($explicit)) {
        return (Resolve-Path -LiteralPath $explicit -ErrorAction Stop).Path
    }
    $parent = Split-Path $repoRoot -Parent
    foreach ($name in $candidates) {
        $try = Join-Path $parent $name
        if (Test-Path -LiteralPath $try) { return (Resolve-Path -LiteralPath $try).Path }
    }
    throw "Could not find sibling repo among: $($candidates -join ', '). Pass an explicit path."
}

function Get-PackagePin([string]$csprojPath, [string]$packageId) {
    [xml]$xml = Get-Content -LiteralPath $csprojPath
    $hit = @($xml.Project.ItemGroup.PackageReference) | Where-Object { $_.Include -eq $packageId } | Select-Object -First 1
    if (-not $hit) { throw "PackageReference $packageId not found in $csprojPath" }
    return [string]$hit.Version
}

function Set-PackagePin([string]$csprojPath, [string]$packageId, [string]$newVersion) {
    $text = [IO.File]::ReadAllText($csprojPath)
    $pattern = "(PackageReference\s+Include=`"$([regex]::Escape($packageId))`"\s+Version=)`"[^`"]+`""
    if ($text -notmatch $pattern) {
        throw "Could not find PackageReference $packageId in $csprojPath"
    }
    $updated = [regex]::Replace($text, $pattern, "`${1}`"$newVersion`"", 1)
    [IO.File]::WriteAllText($csprojPath, $updated)
}

function Test-GithubPackagesWriteToken([string]$token) {
    <#
      Returns @{ Ok=bool; Login=string; Scopes=string; Reason=string }
      Classic PATs must include write:packages. Fine-grained (empty X-OAuth-Scopes)
      are accepted only when /user succeeds — Packages: Write must be granted on the org.
    #>
    $result = [ordered]@{ Ok = $false; Login = $null; Scopes = $null; Reason = 'empty token' }
    if ([string]::IsNullOrWhiteSpace($token)) { return [pscustomobject]$result }
    try {
        $resp = Invoke-WebRequest -Uri 'https://api.github.com/user' -Headers @{
            Authorization = "Bearer $token"
            'User-Agent'  = 'vardyparty-local-release'
            Accept        = 'application/vnd.github+json'
        } -UseBasicParsing
    }
    catch {
        $code = $null
        if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
        $result.Reason = if ($code -eq 401) { 'token invalid or expired (GitHub 401)' } else { "GitHub /user failed: $($_.Exception.Message)" }
        return [pscustomobject]$result
    }
    $login = ($resp.Content | ConvertFrom-Json).login
    $scopesRaw = $resp.Headers['X-OAuth-Scopes']
    if ($scopesRaw -is [array]) { $scopesRaw = ($scopesRaw -join ', ') }
    $scopes = [string]$scopesRaw
    $result.Login = $login
    $result.Scopes = $scopes
    if ([string]::IsNullOrWhiteSpace($scopes)) {
        # Fine-grained PAT — scope header empty; caller must have Packages Write on Vardy-Party.
        $result.Ok = $true
        $result.Reason = "fine-grained as $login (ensure Packages: Write on Vardy-Party)"
        return [pscustomobject]$result
    }
    if ($scopes -match '(?i)(^|,\s*)write:packages(\s*,|$)') {
        $result.Ok = $true
        $result.Reason = "classic PAT as $login with write:packages"
        return [pscustomobject]$result
    }
    $result.Reason = "classic PAT as $login lacks write:packages (scopes: $scopes). Create a PAT with write:packages and authorize SSO for Vardy-Party."
    return [pscustomobject]$result
}

function Set-DotEnvValue([string]$envFile, [string]$key, [string]$value) {
    $lines = @()
    $found = $false
    if (Test-Path -LiteralPath $envFile) {
        foreach ($line in Get-Content -LiteralPath $envFile) {
            if ($line -match "^\s*$([regex]::Escape($key))\s*=") {
                $lines += "$key=$value"
                $found = $true
            }
            else { $lines += $line }
        }
    }
    if (-not $found) { $lines += "$key=$value" }
    $dir = Split-Path $envFile -Parent
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    Set-Content -LiteralPath $envFile -Value $lines -Encoding utf8
}

function Get-NugetPushApiKey([string]$envFile) {
    $candidates = [System.Collections.Generic.List[object]]::new()
    if (-not [string]::IsNullOrWhiteSpace($env:NUGET_GITHUB_TOKEN)) {
        $candidates.Add([pscustomobject]@{ Source = 'env:NUGET_GITHUB_TOKEN'; Token = $env:NUGET_GITHUB_TOKEN.Trim() })
    }
    $fromFile = Get-DotEnvValue $envFile 'NUGET_GITHUB_TOKEN'
    if (-not [string]::IsNullOrWhiteSpace($fromFile)) {
        $candidates.Add([pscustomobject]@{ Source = '.env NUGET_GITHUB_TOKEN'; Token = $fromFile.Trim() })
    }
    try {
        $gh = (gh auth token --user Vardy-Party).Trim()
        if ($gh) { $candidates.Add([pscustomobject]@{ Source = 'gh auth Vardy-Party'; Token = $gh }) }
    }
    catch { }

    $lastReason = 'no NUGET_GITHUB_TOKEN candidates'
    foreach ($c in $candidates) {
        $probe = Test-GithubPackagesWriteToken $c.Token
        if ($probe.Ok) {
            Write-Host "  NuGet push auth: $($c.Source) — $($probe.Reason)" -ForegroundColor DarkGray
            return $c.Token
        }
        $lastReason = "$($c.Source): $($probe.Reason)"
        Write-Host "  skip $($c.Source): $($probe.Reason)" -ForegroundColor DarkYellow
    }
    throw "Need a GitHub PAT with write:packages (SSO for Vardy-Party). $lastReason"
}

function Find-SdkTool([string]$exeName) {
    $tool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter $exeName -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\x64\\$([regex]::Escape($exeName))$" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if (-not $tool) { throw "$exeName not found under Windows Kits. Install the Windows SDK." }
    return $tool.FullName
}

function Step-PatchSemVer([string]$version) {
    $core = ($version -split '[-+]')[0].Trim()
    $parts = $core.Split('.')
    if ($parts.Count -lt 2) { throw "Expected MAJOR.MINOR.PATCH (got '$version')" }
    [int]$major = $parts[0]
    [int]$minor = $parts[1]
    [int]$patch = if ($parts.Count -ge 3) { $parts[2] } else { 0 }
    $patch++
    return "$major.$minor.$patch"
}

function Set-CsprojVersion([string]$csprojPath, [string]$newVersion) {
    $text = [IO.File]::ReadAllText($csprojPath)
    if ($text -notmatch '<Version>[^<]+</Version>') { throw "No <Version> in $csprojPath" }
    $updated = [regex]::Replace($text, '<Version>[^<]+</Version>', "<Version>$newVersion</Version>", 1)
    [IO.File]::WriteAllText($csprojPath, $updated)
}

function Set-ApplicationDisplayVersion([string]$versionPropsPath, [string]$newDisplay) {
    $text = [IO.File]::ReadAllText($versionPropsPath)
    if ($text -notmatch '<ApplicationDisplayVersion>[^<]+</ApplicationDisplayVersion>') {
        throw "No <ApplicationDisplayVersion> in $versionPropsPath"
    }
    $updated = [regex]::Replace(
        $text,
        '<ApplicationDisplayVersion>[^<]+</ApplicationDisplayVersion>',
        "<ApplicationDisplayVersion>$newDisplay</ApplicationDisplayVersion>",
        1)
    [IO.File]::WriteAllText($versionPropsPath, $updated)
}

function Get-CsprojVersion([string]$csprojPath) {
    [xml]$proj = Get-Content -LiteralPath $csprojPath
    $v = @($proj.Project.PropertyGroup.Version) | Where-Object { $_ } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($v)) { throw "No <Version> in $csprojPath" }
    return [string]$v
}

function Get-GitHeadSha([string]$repoPath) {
    $sha = (& git -C $repoPath rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sha)) {
        return $null
    }
    return $sha.Trim()
}

function Get-GitShortSha([string]$repoPath) {
    $sha = Get-GitHeadSha $repoPath
    if (-not $sha) { return 'unknown' }
    return $sha.Substring(0, [Math]::Min(12, $sha.Length))
}

function Test-GitPathDirty([string]$repoPath, [string[]]$relativePaths) {
    $dirty = [System.Collections.Generic.List[string]]::new()
    foreach ($rel in $relativePaths) {
        if ([string]::IsNullOrWhiteSpace($rel)) { continue }
        $full = Join-Path $repoPath $rel
        if (-not (Test-Path -LiteralPath $full)) { continue }
        $status = (& git -C $repoPath status --porcelain -- $rel 2>$null)
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($status)) {
            $dirty.Add($rel)
        }
    }
    return @($dirty)
}

function Get-FileSha256([string]$path) {
    if (-not $path -or -not (Test-Path -LiteralPath $path)) { return $null }
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-ReleaseProvenance {
    param(
        [string]$OutDir,
        [hashtable]$Payload
    )
    $path = Join-Path $OutDir 'provenance.json'
    $Payload | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8
    Write-Host "Wrote $path" -ForegroundColor Green
    return $path
}

function Get-TagCommitSha([string]$repo, [string]$tag) {
    # Peel annotated tags to the commit SHA the release should target.
    $sha = (gh api "repos/$repo/commits/$tag" --jq .sha 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sha)) { return $null }
    return $sha.Trim()
}

function Publish-GitHubReleaseWithTarget {
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$TargetCommit,
        [Parameter(Mandatory)][string]$Title,
        [Parameter(Mandatory)][string]$Notes,
        [Parameter(Mandatory)][string[]]$Assets,
        [switch]$Latest
    )
    $assetArgs = @($Assets | Where-Object { $_ -and (Test-Path -LiteralPath $_) })
    if ($assetArgs.Count -eq 0) { throw "No assets to publish for $Repo $Tag" }

    $exists = $false
    if (gh release view $Tag --repo $Repo 2>$null) { $exists = $true }

    $currentTarget = $null
    if ($exists) {
        $currentTarget = Get-TagCommitSha $Repo $Tag
    }

    $recreate = $false
    if ($exists -and $currentTarget -and ($currentTarget.ToLowerInvariant() -ne $TargetCommit.ToLowerInvariant())) {
        Write-Host "Tag $Tag currently points at $currentTarget; recreating release to target $TargetCommit" -ForegroundColor Yellow
        $recreate = $true
    }
    elseif ($exists -and -not $currentTarget) {
        Write-Host "Could not resolve current target for $Tag; recreating release at $TargetCommit" -ForegroundColor Yellow
        $recreate = $true
    }

    if ($recreate) {
        gh release delete $Tag --repo $Repo --yes --cleanup-tag
        if ($LASTEXITCODE -ne 0) { throw "gh release delete $Repo $Tag failed" }
        $exists = $false
    }

    if ($exists) {
        # Wrap: a single path string character-splats under @.
        gh release upload $Tag @assetArgs --repo $Repo --clobber
        if ($LASTEXITCODE -ne 0) { throw "gh release upload $Repo $Tag failed" }
        $editArgs = @('release', 'edit', $Tag, '--repo', $Repo, '--notes', $Notes, '--title', $Title)
        if ($Latest) { $editArgs += '--latest' }
        & gh @editArgs
        if ($LASTEXITCODE -ne 0) { throw "gh release edit $Repo $Tag failed" }
    }
    else {
        $createArgs = @(
            'release', 'create', $Tag
        ) + $assetArgs + @(
            '--repo', $Repo,
            '--title', $Title,
            '--notes', $Notes,
            '--target', $TargetCommit
        )
        if ($Latest) { $createArgs += '--latest' }
        & gh @createArgs
        if ($LASTEXITCODE -ne 0) { throw "gh release create $Repo $Tag failed" }
    }
}

function Get-ProvenancePublishProps([string]$version, [string]$commitSha) {
    if ([string]::IsNullOrWhiteSpace($commitSha)) {
        throw 'Cannot stamp InformationalVersion without a commit SHA (clean HEAD required for publish-quality builds).'
    }
    $info = "$version+$commitSha"
    return @(
        "/p:SourceRevisionId=$commitSha",
        "/p:InformationalVersion=$info"
    )
}

function ConvertTo-MsixFourPart([string]$semver) {
    $parts = (($semver -split '[-+]')[0]).Split('.')
    return '{0}.{1}.{2}.{3}' -f `
        $(if ($parts.Count -ge 1) { $parts[0] } else { 1 }),
        $(if ($parts.Count -ge 2) { $parts[1] } else { 0 }),
        $(if ($parts.Count -ge 3) { $parts[2] } else { 0 }),
        $(if ($parts.Count -ge 4) { $parts[3] } else { 0 })
}

function Import-Auth0EnvFromDotEnv([string]$envFile) {
    foreach ($key in @(
            'AUTH0_DOMAIN', 'AUTH0_AUDIENCE', 'AUTH0_CLAIM_NAMESPACE',
            'AUTH0_REQUIREDROLECLAIMTYPE', 'AUTH0_REQUIREDROLE', 'AUTH0_REQUIRED_PERMISSION',
            'SCRAPE_HEALTH_ALLOWED_API_HOSTS'
        )) {
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($key))) {
            $fromEnv = Get-DotEnvValue $envFile $key
            if ($fromEnv) { Set-Item -Path "Env:$key" -Value $fromEnv }
        }
    }
}

function Invoke-WithAuth0InjectedAppsettings {
    param(
        [string]$AppsettingsPath,
        [scriptblock]$Action
    )
    $backup = Join-Path $env:TEMP ("vp-appsettings-{0}.json" -f [guid]::NewGuid().ToString('n'))
    Copy-Item -LiteralPath $AppsettingsPath -Destination $backup -Force
    try {
        & (Join-Path $PSScriptRoot 'merge-appsettings-auth0.ps1') -AppsettingsPath $AppsettingsPath
        & $Action
    }
    finally {
        Copy-Item -LiteralPath $backup -Destination $AppsettingsPath -Force
        Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
    }
}

function Publish-R2Object {
    param(
        [string]$ApiRepoPath,
        [string]$Key,
        [string]$FilePath,
        [string]$ContentType,
        [string]$ContentDisposition
    )
    Push-Location $ApiRepoPath
    try {
        Write-Host "R2 put $Key ← $FilePath" -ForegroundColor Cyan
        # Current wrangler: objectPath is {bucket}/{key} — no --bucket flag.
        $objectPath = "vardyparty-installers/$Key"
        $wranglerArgs = @(
            'wrangler', 'r2', 'object', 'put', $objectPath,
            '--file', $FilePath,
            '--content-type', $ContentType,
            '--remote'
        )
        if ($ContentDisposition) {
            $wranglerArgs += @('--content-disposition', $ContentDisposition)
        }
        & npx --yes @wranglerArgs
        if ($LASTEXITCODE -ne 0) { throw "wrangler r2 object put failed for $Key" }
    }
    finally {
        Pop-Location
    }
}

# ── Fail-fast preflight (ask / load everything before any build) ────────────
$envFile = Join-Path $repoRoot '.env'
$preflightErrors = [System.Collections.Generic.List[string]]::new()
$pfx = $null

Write-Host "`n=== Preflight (fail-fast) ===" -ForegroundColor Cyan
Write-Host "Loading from $envFile when present; prompting only for missing secrets." -ForegroundColor DarkGray

function Require-Ok([bool]$ok, [string]$message) {
    if ($ok) { Write-Host "  OK  $message" -ForegroundColor Green }
    else {
        Write-Host "  FAIL  $message" -ForegroundColor Red
        $preflightErrors.Add($message)
    }
}

# Sibling repos
try {
    $ClientRepo = Resolve-SiblingRepo $ClientRepo @('VardyParty-Client', 'Client')
    Require-Ok $true "Client repo: $ClientRepo"
}
catch {
    Require-Ok $false "Client repo: $($_.Exception.Message)"
}

if (-not $SkipLocalService -or -not $SkipStrategiesPack) {
    try {
        $StrategiesRepo = Resolve-SiblingRepo $StrategiesRepo @('Strategies')
        Require-Ok $true "Strategies repo: $StrategiesRepo"
    }
    catch {
        Require-Ok $false "Strategies repo: $($_.Exception.Message)"
    }
}

if (-not $SkipPublish) {
    try {
        $ApiRepo = Resolve-SiblingRepo $ApiRepo @('VardyParty-API', 'API', 'Api')
        Require-Ok $true "API repo (wrangler R2): $ApiRepo"
    }
    catch {
        Require-Ok $false "API repo: $($_.Exception.Message)"
    }
}

# Record tip SHAs early (preflight) so provenance survives later bumps / builds.
$provenanceShas = [ordered]@{
    localservice = Get-GitHeadSha $repoRoot
    client       = if ($ClientRepo) { Get-GitHeadSha $ClientRepo } else { $null }
    strategies   = if ($StrategiesRepo) { Get-GitHeadSha $StrategiesRepo } else { $null }
}
Write-Host ("  SHAs  LS={0} Client={1} Strategies={2}" -f `
        (Get-GitShortSha $repoRoot), `
        $(if ($ClientRepo) { Get-GitShortSha $ClientRepo } else { 'n/a' }), `
        $(if ($StrategiesRepo) { Get-GitShortSha $StrategiesRepo } else { 'n/a' })) -ForegroundColor DarkGray

# PFX — ask first if not in env/.env (not needed for publish-only)
if (-not $PublishOnly) {
    if ([string]::IsNullOrWhiteSpace($PfxPath)) {
        $PfxPath = Join-Path $ClientRepo 'certs\vardyparty.pfx'
    }
    Require-Ok (Test-Path -LiteralPath $PfxPath) "PFX file: $PfxPath"

    if ([string]::IsNullOrWhiteSpace($PfxPassword)) { $PfxPassword = $env:WINDOWS_CERT_PASSWORD }
    if ([string]::IsNullOrWhiteSpace($PfxPassword)) { $PfxPassword = $env:MSIX_CERT_PASSWORD }
    if ([string]::IsNullOrWhiteSpace($PfxPassword)) { $PfxPassword = Get-DotEnvValue $envFile 'WINDOWS_CERT_PASSWORD' }
    if ([string]::IsNullOrWhiteSpace($PfxPassword) -and (Test-Path -LiteralPath $PfxPath)) {
        Write-Host ''
        $secure = Read-Host -AsSecureString 'PFX password (WINDOWS_CERT_PASSWORD)'
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        $PfxPassword = [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
    if ([string]::IsNullOrWhiteSpace($PfxPassword)) {
        Require-Ok $false 'PFX password missing (set WINDOWS_CERT_PASSWORD in .env or enter at prompt)'
    }
    else {
        try {
            $pfx = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($PfxPath, $PfxPassword)
            $env:MSIX_CERT_PASSWORD = $PfxPassword
            $env:WINDOWS_CERT_PASSWORD = $PfxPassword
            Require-Ok $true "PFX opens (thumbprint $($pfx.Thumbprint))"
        }
        catch {
            Require-Ok $false "PFX password rejected: $($_.Exception.Message)"
        }
    }
}

# LocalService Auth0 from .env
Import-Auth0EnvFromDotEnv $envFile
if (-not $PublishOnly -and -not $SkipLocalService) {
    $domain = $env:AUTH0_DOMAIN
    $audience = $env:AUTH0_AUDIENCE
    if ([string]::IsNullOrWhiteSpace($domain)) {
        $domain = Read-Host 'AUTH0_DOMAIN (custom login host, e.g. auth.example.com)'
        if ($domain) { $env:AUTH0_DOMAIN = $domain.Trim() }
    }
    if ([string]::IsNullOrWhiteSpace($audience)) {
        $audience = Read-Host 'AUTH0_AUDIENCE'
        if ($audience) { $env:AUTH0_AUDIENCE = $audience.Trim() }
    }
    Require-Ok (-not [string]::IsNullOrWhiteSpace($env:AUTH0_DOMAIN)) "AUTH0_DOMAIN=$($env:AUTH0_DOMAIN)"
    Require-Ok (-not [string]::IsNullOrWhiteSpace($env:AUTH0_AUDIENCE)) 'AUTH0_AUDIENCE is set'
}

# Client user-secrets (PatchAppSettings)
if (-not $PublishOnly -and -not $SkipClient) {
    $clientCsproj = Join-Path $ClientRepo 'VardyParty\VardyParty.csproj'
    $secretsList = & dotnet user-secrets list --project $clientCsproj 2>$null | Out-String
    $hasDomain = $secretsList -match '(?m)^Auth0:Domain\s*='
    $hasClientId = $secretsList -match '(?m)^Auth0:ClientId\s*='
    $hasAudience = $secretsList -match '(?m)^Auth0:Audience\s*='
    Require-Ok $hasDomain 'Client user-secrets Auth0:Domain'
    Require-Ok $hasClientId 'Client user-secrets Auth0:ClientId'
    Require-Ok $hasAudience 'Client user-secrets Auth0:Audience'
}

# NuGet write PAT — validate before any version bump / pack
if (-not $SkipPublish -and -not $SkipNugetPublish -and -not $SkipLocalService -and -not $SkipStrategiesPack) {
    $nugetKey = $null
    try { $nugetKey = Get-NugetPushApiKey $envFile } catch {
        Write-Host "  $($_.Exception.Message)" -ForegroundColor DarkYellow
    }
    if ([string]::IsNullOrWhiteSpace($nugetKey)) {
        Write-Host ''
        Write-Host 'GitHub Packages push needs a PAT with write:packages (classic) or Packages: Write (fine-grained),' -ForegroundColor Yellow
        Write-Host 'authorized for SSO on the Vardy-Party org. The read:packages restore token is not enough;' -ForegroundColor Yellow
        Write-Host 'gh auth as Vardy-Party currently has repo/workflow only — not write:packages.' -ForegroundColor Yellow
        Write-Host 'Create: GitHub → Settings → Developer settings → Personal access tokens.' -ForegroundColor DarkGray
        $nugetKey = Read-Host 'Paste NUGET_GITHUB_TOKEN (write:packages)'
        if ($nugetKey) {
            $nugetKey = $nugetKey.Trim()
            $probe = Test-GithubPackagesWriteToken $nugetKey
            if ($probe.Ok) {
                $env:NUGET_GITHUB_TOKEN = $nugetKey
                $save = Read-Host "Save to $envFile as NUGET_GITHUB_TOKEN? [Y/n]"
                if ($save -notmatch '^(n|no)$') {
                    Set-DotEnvValue $envFile 'NUGET_GITHUB_TOKEN' $nugetKey
                    Write-Host "  wrote NUGET_GITHUB_TOKEN to .env ($($probe.Reason))" -ForegroundColor Green
                }
            }
            else {
                Write-Host "  pasted token rejected: $($probe.Reason)" -ForegroundColor Red
                $nugetKey = $null
            }
        }
    }
    Require-Ok (-not [string]::IsNullOrWhiteSpace($nugetKey)) `
        'NUGET_GITHUB_TOKEN with write:packages (validated against api.github.com/user)'
    if ($nugetKey) { $env:NUGET_GITHUB_TOKEN = $nugetKey }

    $nugetUser = $env:NUGET_GITHUB_USERNAME
    if ([string]::IsNullOrWhiteSpace($nugetUser)) { $nugetUser = Get-DotEnvValue $envFile 'NUGET_GITHUB_USERNAME' }
    if ([string]::IsNullOrWhiteSpace($nugetUser)) {
        $nugetUser = Read-Host 'NUGET_GITHUB_USERNAME (GitHub login that owns the PAT)'
        if ($nugetUser) {
            $env:NUGET_GITHUB_USERNAME = $nugetUser.Trim()
            Set-DotEnvValue $envFile 'NUGET_GITHUB_USERNAME' $env:NUGET_GITHUB_USERNAME
        }
    }
    Require-Ok (-not [string]::IsNullOrWhiteSpace($nugetUser)) 'NUGET_GITHUB_USERNAME set (for restore auth)'
}

# gh as Vardy-Party (Releases)
if (-not $SkipPublish) {
    $ghToken = $null
    try { $ghToken = (gh auth token --user Vardy-Party).Trim() } catch { }
    if ([string]::IsNullOrWhiteSpace($ghToken)) {
        Require-Ok $false 'gh auth as Vardy-Party (needed for GitHub Releases). Run: gh auth login'
    }
    else {
        Require-Ok $true 'gh auth token --user Vardy-Party'
    }
}

# Wrangler / Cloudflare for R2
if (-not $SkipPublish) {
    if ([string]::IsNullOrWhiteSpace($env:CLOUDFLARE_API_TOKEN)) {
        $cf = Get-DotEnvValue $envFile 'CLOUDFLARE_API_TOKEN'
        if ($cf) { $env:CLOUDFLARE_API_TOKEN = $cf }
    }
    $wranglerOk = $false
    Push-Location $ApiRepo
    try {
        $who = & npx --yes wrangler whoami 2>&1 | Out-String
        if ($LASTEXITCODE -eq 0 -and $who -notmatch '(?i)not logged|error|unauthorized|invalid access token') {
            $wranglerOk = $true
        }
        elseif ($who -match '(?i)invalid access token' -and $env:CLOUDFLARE_API_TOKEN) {
            # Stale/bad CLOUDFLARE_API_TOKEN overrides OAuth login — retry without it.
            Write-Host '  CLOUDFLARE_API_TOKEN rejected; retrying wrangler whoami with OAuth session…' -ForegroundColor DarkYellow
            Remove-Item Env:CLOUDFLARE_API_TOKEN -ErrorAction SilentlyContinue
            $who = & npx --yes wrangler whoami 2>&1 | Out-String
            if ($LASTEXITCODE -eq 0 -and $who -notmatch '(?i)not logged|error|unauthorized|invalid access token') {
                $wranglerOk = $true
            }
        }
    }
    catch { }
    finally { Pop-Location }
    Require-Ok $wranglerOk 'wrangler logged in (or valid CLOUDFLARE_API_TOKEN) for R2 uploads'
}

# Pi / WSL
if (-not $SkipLocalService -and -not $SkipPi) {
    $wslCmd = Get-Command wsl.exe -ErrorAction SilentlyContinue
    if (-not $wslCmd) {
        Require-Ok $false 'WSL required for Pi snap (or pass -SkipPi)'
    }
    else {
        $sc = & wsl.exe -e bash -lc 'command -v snapcraft' 2>$null
        Require-Ok (-not [string]::IsNullOrWhiteSpace($sc)) 'snapcraft available in WSL (or pass -SkipPi)'
    }
}

# SDK tools for MSIX
if (-not $PublishOnly -and (-not $SkipLocalService -or -not $SkipClient)) {
    try {
        $null = Find-SdkTool 'makeappx.exe'
        Require-Ok $true 'makeappx.exe (Windows SDK)'
    }
    catch { Require-Ok $false $_.Exception.Message }
    try {
        $null = Find-SdkTool 'signtool.exe'
        Require-Ok $true 'signtool.exe (Windows SDK)'
    }
    catch { Require-Ok $false $_.Exception.Message }
}

if ($preflightErrors.Count -gt 0) {
    Write-Host ''
    Write-Host 'Preflight failed. Fix these before building:' -ForegroundColor Red
    foreach ($e in $preflightErrors) { Write-Host "  - $e" -ForegroundColor Red }
    Write-Host ''
    Write-Host 'Typical sources: M3U8-resolver/.env (Auth0, NUGET_*, WINDOWS_CERT_PASSWORD),' -ForegroundColor Yellow
    Write-Host 'Client user-secrets, gh auth --user Vardy-Party, wrangler login, WSL snapcraft.' -ForegroundColor Yellow
    throw "Preflight failed with $($preflightErrors.Count) issue(s)."
}

Write-Host "`nPreflight passed." -ForegroundColor Green

$localServiceCsproj = Join-Path $repoRoot 'VardyParty.LocalService\VardyParty.LocalService.csproj'
$localNuget = Join-Path $repoRoot 'local-nuget'
$appsettings = Join-Path $repoRoot 'VardyParty.LocalService\appsettings.json'

if ($PublishOnly) {
    $outRoot = (Resolve-Path -LiteralPath $PublishFromDir).Path
    $stamp = Split-Path $outRoot -Leaf
    Write-Host "Publish-only from: $outRoot" -ForegroundColor Cyan
}
else {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $outRoot = Join-Path $repoRoot "artifacts\local-release\$stamp"
    New-Item -ItemType Directory -Force -Path $outRoot | Out-Null
    Write-Host "Output folder: $outRoot" -ForegroundColor Cyan
}

# ── Version bumps ───────────────────────────────────────────────────────────
$bumpedLocalService = $null
$bumpedClientDisplay = $null
$clientBuild = $null
if (-not $SkipVersionBump) {
    Write-Host "`n=== Patch version bumps ===" -ForegroundColor Cyan
    if (-not $SkipLocalService) {
        $oldLs = Get-CsprojVersion $localServiceCsproj
        $bumpedLocalService = Step-PatchSemVer $oldLs
        Set-CsprojVersion $localServiceCsproj $bumpedLocalService
        Write-Host "LocalService Version: $oldLs → $bumpedLocalService" -ForegroundColor Green
    }
    if (-not $SkipClient) {
        $versionProps = Join-Path $ClientRepo 'Version.props'
        $propsText = Get-Content -LiteralPath $versionProps -Raw
        if ($propsText -notmatch '<ApplicationDisplayVersion>([^<]+)</ApplicationDisplayVersion>') {
            throw "Could not read ApplicationDisplayVersion from $versionProps"
        }
        $oldDisplay = $Matches[1]
        $bumpedClientDisplay = Step-PatchSemVer $oldDisplay
        Set-ApplicationDisplayVersion $versionProps $bumpedClientDisplay
        if ($propsText -match '<ApplicationVersion>([^<]+)</ApplicationVersion>') {
            $clientBuild = $Matches[1]
        }
        Write-Host "Client ApplicationDisplayVersion: $oldDisplay → $bumpedClientDisplay (build $clientBuild)" -ForegroundColor Green
    }
}

if (-not $clientBuild -and -not $SkipClient) {
    $vp = Get-Content -LiteralPath (Join-Path $ClientRepo 'Version.props') -Raw
    if ($vp -match '<ApplicationVersion>([^<]+)</ApplicationVersion>') { $clientBuild = $Matches[1] }
}
if (-not $bumpedClientDisplay -and -not $SkipClient) {
    $vp = Get-Content -LiteralPath (Join-Path $ClientRepo 'Version.props') -Raw
    if ($vp -match '<ApplicationDisplayVersion>([^<]+)</ApplicationDisplayVersion>') {
        $bumpedClientDisplay = $Matches[1]
    }
}
$lsVersion = if ($bumpedLocalService) { $bumpedLocalService } else { Get-CsprojVersion $localServiceCsproj }

# ── Strategies pack (+ optional GitHub Packages publish) ────────────────────
$publishedStrategyVersions = @{}
if (-not $SkipLocalService -and -not $SkipStrategiesPack) {
    $nugetLabel = if (-not $SkipPublish -and -not $SkipNugetPublish) { ' → GitHub Packages' } else { '' }
    Write-Host "`n=== Strategies NuGet (tip → pack$nugetLabel) ===" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $localNuget | Out-Null
    $strategiesArtifacts = Join-Path $StrategiesRepo 'artifacts\nuget'
    New-Item -ItemType Directory -Force -Path $strategiesArtifacts | Out-Null
    $strategyPacks = @(
        @{ Id = 'VardyParty.LocalService.Abstractions'; Rel = 'VardyParty.LocalService.Abstractions\VardyParty.LocalService.Abstractions.csproj' }
        @{ Id = 'VardyParty.LocalService.V1'; Rel = 'VardyParty.LocalService.V1\VardyParty.LocalService.V1.csproj' }
        @{ Id = 'VardyParty.LocalService.V2'; Rel = 'VardyParty.LocalService.V2\VardyParty.LocalService.V2.csproj' }
        @{ Id = 'VardyParty.LocalService.V1.Scrape'; Rel = 'VardyParty.LocalService.V1.Scrape\VardyParty.LocalService.V1.Scrape.csproj' }
        @{ Id = 'VardyParty.LocalService.V2.Scrape'; Rel = 'VardyParty.LocalService.V2.Scrape\VardyParty.LocalService.V2.Scrape.csproj' }
    )

    $doNugetPush = -not $SkipPublish -and -not $SkipNugetPublish
    $nugetApiKey = $null
    if ($doNugetPush) {
        $nugetApiKey = Get-NugetPushApiKey $envFile
    }

    foreach ($pack in $strategyPacks) {
        $proj = Join-Path $StrategiesRepo $pack.Rel
        if (-not (Test-Path -LiteralPath $proj)) { Write-Host "Skip missing $($pack.Id)"; continue }

        $restoreCsprojVersion = $null
        if ($doNugetPush -and -not $SkipVersionBump) {
            # Patch-bump Strategies csproj Version (left dirty for commit), then pack that version.
            $oldVer = Get-CsprojVersion $proj
            $newVer = Step-PatchSemVer $oldVer
            Set-CsprojVersion $proj $newVer
            Write-Host "$($pack.Id) Version: $oldVer → $newVer" -ForegroundColor Green
            $packVersion = $newVer
        }
        else {
            # Offline restore only: align this project's csproj Version to the LocalService pin.
            $packVersion = Get-PackagePin $localServiceCsproj $pack.Id
            $restoreCsprojVersion = Get-CsprojVersion $proj
            Set-CsprojVersion $proj $packVersion
            Write-Host "Pack $($pack.Id) as pin $packVersion (no Strategies Version bump)" -ForegroundColor DarkGray
        }

        # Do NOT pass /p:Version= or /p:PackageVersion= — those flow through the ProjectReference
        # graph and stamp every dependency as the leaf package's version (e.g. V1.Scrape 0.1.10
        # declaring V1 >= 0.1.10 while V1 was only 0.1.4 → NU1605 downgrade).
        try {
            & dotnet pack $proj -c $Configuration -o $strategiesArtifacts --nologo
            if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed for $($pack.Id)" }
        }
        finally {
            if ($null -ne $restoreCsprojVersion) {
                Set-CsprojVersion $proj $restoreCsprojVersion
            }
        }
        $nupkg = Get-ChildItem -LiteralPath $strategiesArtifacts -Filter "$($pack.Id).$packVersion.nupkg" -File |
            Select-Object -First 1
        if (-not $nupkg) { throw "Missing nupkg $($pack.Id).$packVersion" }
        Copy-Item $nupkg.FullName $localNuget -Force
        $publishedStrategyVersions[$pack.Id] = $packVersion

        if ($doNugetPush) {
            Write-Host "nuget push $($nupkg.Name) → github-vardy-party" -ForegroundColor Cyan
            & dotnet nuget push $nupkg.FullName `
                --source 'https://nuget.pkg.github.com/Vardy-Party/index.json' `
                --api-key $nugetApiKey `
                --skip-duplicate
            if ($LASTEXITCODE -ne 0) {
                throw "dotnet nuget push failed for $($pack.Id) $packVersion (need write:packages on the PAT)."
            }
        }
    }

    if ($doNugetPush -and $publishedStrategyVersions.Count -gt 0) {
        Write-Host "Retargeting LocalService PackageReference pins…" -ForegroundColor Cyan
        foreach ($id in $publishedStrategyVersions.Keys) {
            $ver = $publishedStrategyVersions[$id]
            $oldPin = Get-PackagePin $localServiceCsproj $id
            if ($oldPin -ne $ver) {
                Set-PackagePin $localServiceCsproj $id $ver
                Write-Host "  $id $oldPin → $ver" -ForegroundColor Green
            }
        }
    }

    & dotnet nuget locals http-cache --clear | Out-Null
}

& (Join-Path $PSScriptRoot 'ensure-github-packages-auth.ps1') -RepoRoot $repoRoot | Out-Null

$localMsixDest = $null
$piSnapDest = $null
$clientMsixDest = $null
$clientApkDest = $null
$cerDest = $null

function Find-LatestLocalReleaseFile([string]$filter) {
    $root = Join-Path $repoRoot 'artifacts\local-release'
    if (-not (Test-Path -LiteralPath $root)) { return $null }
    return Get-ChildItem -LiteralPath $root -Recurse -Filter $filter -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

if ($PublishOnly) {
    Write-Host "`n=== Publish-only: wiring artifacts from $outRoot ===" -ForegroundColor Cyan
    $localMsixDest = @(Get-ChildItem -LiteralPath $outRoot -Filter 'VardyParty.LocalService-win-x64-*.msix' -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
    $piSnapDest = @(Get-ChildItem -LiteralPath $outRoot -Filter 'vardyparty-local-service_arm64-*.snap' -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
    $clientMsixDest = @(Get-ChildItem -LiteralPath $outRoot -Filter 'VardyParty-windows-v*-b*.msix' -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
    $clientApkDest = @(Get-ChildItem -LiteralPath $outRoot -Filter 'VardyParty-android-v*-b*.apk' -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
    $cerPath = Join-Path $outRoot 'vardyparty.cer'
    if (Test-Path -LiteralPath $cerPath) { $cerDest = $cerPath }

    if ($localMsixDest -and $localMsixDest -match 'win-x64-(\d+\.\d+\.\d+)\.msix$') {
        $lsVersion = $Matches[1]
    }
    if ($clientMsixDest -and $clientMsixDest -match 'windows-v(\d+\.\d+\.\d+)-b(\d+)\.msix$') {
        $bumpedClientDisplay = $Matches[1]
        $clientBuild = $Matches[2]
    }
    elseif ($clientApkDest -and $clientApkDest -match 'android-v(\d+\.\d+\.\d+)-b(\d+)\.apk$') {
        $bumpedClientDisplay = $Matches[1]
        $clientBuild = $Matches[2]
    }

    Write-Host "  LocalService MSIX: $localMsixDest"
    Write-Host "  Pi snap:           $(if ($piSnapDest) { $piSnapDest } else { '(none)' })"
    Write-Host "  Client MSIX:       $clientMsixDest"
    Write-Host "  Client APK:        $clientApkDest"
    Write-Host "  CER:               $cerDest"
    Write-Host "  Versions: LS=$lsVersion Client=$bumpedClientDisplay build=$clientBuild"
}

# ── LocalService Windows MSIX ───────────────────────────────────────────────
if (-not $PublishOnly -and -not $SkipLocalService -and $SkipLocalServiceWindows) {
    Write-Host "`n=== LocalService Windows MSIX (skipped — reuse prior artifact) ===" -ForegroundColor Cyan
    $prior = Find-LatestLocalReleaseFile "VardyParty.LocalService-win-x64-$lsVersion.msix"
    if (-not $prior) {
        $prior = Find-LatestLocalReleaseFile 'VardyParty.LocalService-win-x64-*.msix'
    }
    if (-not $prior) {
        throw "SkipLocalServiceWindows: no prior LocalService MSIX under artifacts/local-release. Drop -SkipLocalServiceWindows or build once."
    }
    $script:localMsixDest = Join-Path $outRoot $prior.Name
    if ($prior.FullName -ne $script:localMsixDest) {
        Copy-Item $prior.FullName $script:localMsixDest -Force
    }
    Write-Host "Reusing $($prior.FullName) → $($script:localMsixDest)" -ForegroundColor Green
}
elseif (-not $PublishOnly -and -not $SkipLocalService) {
    Write-Host "`n=== LocalService Windows MSIX (Auth0 injected) ===" -ForegroundColor Cyan
    Invoke-WithAuth0InjectedAppsettings -AppsettingsPath $appsettings -Action {
        $publishRoot = Join-Path $repoRoot 'artifacts\publish\win-x64'
        $layoutRoot = Join-Path $repoRoot 'artifacts\msix-layout'
        $installers = Join-Path $repoRoot 'artifacts\installers'
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $publishRoot, $layoutRoot
        New-Item -ItemType Directory -Force -Path $publishRoot, $installers | Out-Null

        $lsStampSha = Get-GitHeadSha $repoRoot
        $lsStampProps = Get-ProvenancePublishProps $lsVersion $lsStampSha
        & dotnet publish $localServiceCsproj -c $Configuration -r win-x64 --self-contained true `
            -o $publishRoot --force /p:RestoreAdditionalProjectSources=$localNuget @lsStampProps
        if ($LASTEXITCODE -ne 0) { throw 'LocalService win-x64 publish failed' }

        $scrapeDll = Get-ChildItem $publishRoot -Filter 'VardyParty.LocalService.V2.Scrape.dll' -Recurse -File | Select-Object -First 1
        if (-not $scrapeDll) { throw 'V2.Scrape.dll missing from publish output' }

        $msixVersion = ConvertTo-MsixFourPart $lsVersion
        $appRoot = Join-Path $layoutRoot 'VardyParty.LocalService'
        New-Item -ItemType Directory -Force -Path $appRoot, (Join-Path $layoutRoot 'Assets') | Out-Null
        Copy-Item (Join-Path $publishRoot '*') $appRoot -Recurse -Force
        Copy-Item (Join-Path $repoRoot 'packaging\windows\VardyParty.LocalService.Package\Assets\*') `
            (Join-Path $layoutRoot 'Assets') -Recurse -Force

        $bootstrapperOut = Join-Path $repoRoot 'artifacts\bootstrapper\win-x64'
        & dotnet publish (Join-Path $repoRoot 'packaging\windows\InstallerBootstrapper\InstallerBootstrapper.csproj') `
            -c $Configuration -r win-x64 -o $bootstrapperOut
        if ($LASTEXITCODE -ne 0) { throw 'InstallerBootstrapper publish failed' }
        Copy-Item (Join-Path $bootstrapperOut 'InstallerBootstrapper.exe') (Join-Path $layoutRoot 'InstallerBootstrapper.exe') -Force
        $signtool = Find-SdkTool 'signtool.exe'
        # Sign the embedded bootstrapper before makeappx packs the layout (signing it after pack left the MSIX copy unsigned).
        & $signtool sign /fd SHA256 /f $PfxPath /p $PfxPassword (Join-Path $layoutRoot 'InstallerBootstrapper.exe')
        if ($LASTEXITCODE -ne 0) { throw 'signtool sign failed (InstallerBootstrapper)' }

        [xml]$manifest = Get-Content (Join-Path $repoRoot 'packaging\windows\VardyParty.LocalService.Package\Package.appxmanifest')
        $manifest.Package.Identity.Version = $msixVersion
        $manifest.Save((Join-Path $layoutRoot 'AppxManifest.xml'))

        $makeappx = Find-SdkTool 'makeappx.exe'
        $msixPath = Join-Path $installers 'VardyParty.LocalService-win-x64.msix'
        & $makeappx pack /d $layoutRoot /p $msixPath /o
        if ($LASTEXITCODE -ne 0) { throw 'makeappx pack failed' }
        & $signtool sign /fd SHA256 /f $PfxPath /p $PfxPassword $msixPath
        if ($LASTEXITCODE -ne 0) { throw 'signtool sign failed (LocalService MSIX)' }

        $script:localMsixDest = Join-Path $outRoot "VardyParty.LocalService-win-x64-$lsVersion.msix"
        Copy-Item $msixPath $script:localMsixDest -Force
        Write-Host "LocalService Windows MSIX: $($script:localMsixDest)" -ForegroundColor Green
    }
}

# ── LocalService Pi (linux-arm64 snap via WSL) ──────────────────────────────
if (-not $PublishOnly -and -not $SkipLocalService -and -not $SkipPi) {
    Write-Host "`n=== LocalService linux-arm64 snap (WSL) ===" -ForegroundColor Cyan
    $wsl = Get-Command wsl.exe -ErrorAction SilentlyContinue
    if (-not $wsl) { throw 'WSL is required to build the Raspberry Pi snap. Install WSL + snapcraft, or pass -SkipPi.' }

    Invoke-WithAuth0InjectedAppsettings -AppsettingsPath $appsettings -Action {
        $armPublish = Join-Path $repoRoot 'artifacts\publish\linux-arm64'
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $armPublish
        New-Item -ItemType Directory -Force -Path $armPublish | Out-Null
        $lsStampSha = Get-GitHeadSha $repoRoot
        $lsStampProps = Get-ProvenancePublishProps $lsVersion $lsStampSha
        & dotnet publish $localServiceCsproj -c $Configuration -r linux-arm64 --self-contained true `
            -o $armPublish --force /p:RestoreAdditionalProjectSources=$localNuget @lsStampProps
        if ($LASTEXITCODE -ne 0) { throw 'LocalService linux-arm64 publish failed' }

        $snapDir = Join-Path $repoRoot 'snap'
        $payload = Join-Path $snapDir 'local-service'
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $payload
        New-Item -ItemType Directory -Force -Path $payload | Out-Null
        Copy-Item (Join-Path $armPublish '*') $payload -Recurse -Force

        $snapYaml = Join-Path $snapDir 'snapcraft.yaml'
        $yaml = Get-Content -LiteralPath $snapYaml -Raw
        $yaml = [regex]::Replace($yaml, '(?m)^version:\s*".*"', "version: `"$lsVersion`"")
        # CI stamps build-on:[arm64] on ubuntu-24.04-arm. Local WSL is amd64, so use
        # core22 cross plan: build-on amd64 / build-for arm64 (payload already linux-arm64).
        $yaml = [regex]::Replace(
            $yaml,
            '(?ms)^architectures:\s*\r?\n(?:[ \t]*-[^\r\n]*\r?\n)+',
            "architectures:`n  - build-on: [amd64]`n    build-for: [arm64]`n`n"
        )
        $yaml = $yaml -replace 'x86_64-linux-gnu', 'aarch64-linux-gnu'
        $snapYamlBackup = Join-Path $env:TEMP "snapcraft-$stamp.yaml"
        Copy-Item $snapYaml $snapYamlBackup -Force
        try {
            Set-Content -LiteralPath $snapYaml -Value $yaml -Encoding utf8 -NoNewline
            # Ensure unix line endings for snapcraft
            $unix = (Get-Content -LiteralPath $snapYaml -Raw) -replace "`r`n", "`n"
            [IO.File]::WriteAllText($snapYaml, $unix)

            # Forward slashes: bare backslashes are eaten before wslpath sees them
            # (e.g. C:\Users\… → C:Users… → null stdout → .Trim() NRE).
            $repoWinForWsl = ($repoRoot -replace '\\', '/')
            $repoWslOut = & wsl.exe wslpath -a $repoWinForWsl 2>&1
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace("$repoWslOut")) {
                throw "wslpath failed for '$repoWinForWsl' (exit $LASTEXITCODE): $repoWslOut. Pass -SkipPi to continue without the Pi snap."
            }
            $repoWsl = ("$repoWslOut").Trim()
            Write-Host "Running snapcraft pack (amd64→arm64) in WSL at $repoWsl/snap …" -ForegroundColor Cyan
            # One line only (avoid CRLF → bash "pipefail: invalid option name").
            # Prefer passwordless sudo so apt indexes refresh (else superseded .deb URLs 404).
            # SNAPCRAFT_OK: wsl.exe often returns exit 0 even when bash fails.
            $bashLc = "set -euo pipefail; cd '$repoWsl/snap'; chmod +x local-service/VardyParty.LocalService; command -v snapcraft >/dev/null || { echo 'snapcraft not installed in WSL' >&2; exit 1; }; rm -rf `"`$HOME/.cache/snapcraft/download`"; if sudo -n true 2>/dev/null; then sudo -n apt-get update -qq || true; sudo -n snapcraft clean --destructive-mode 2>/dev/null || true; sudo -n snapcraft pack --destructive-mode --build-for=arm64; else echo 'WARN: no passwordless sudo; stage-package 404s are common — use -SkipPi' >&2; snapcraft clean --destructive-mode 2>/dev/null || true; snapcraft pack --destructive-mode --build-for=arm64; fi; echo SNAPCRAFT_OK"
            $snapcraftOut = & wsl.exe -e bash -lc $bashLc 2>&1
            $snapcraftText = ($snapcraftOut | Out-String)
            Write-Host $snapcraftText
            if ($snapcraftText -notmatch 'SNAPCRAFT_OK') {
                throw @"
snapcraft pack failed in WSL (amd64→arm64). Pi .snap normally comes from CI ubuntu-24.04-arm; local cross-pack needs passwordless sudo + apt update in WSL.

Finish Win helper + Client without Pi:
  ./scripts/build-local-release.ps1 -SkipVersionBump -SkipStrategiesPack -SkipLocalServiceWindows -SkipPi

$snapcraftText
"@
            }
            $snapFile = Get-ChildItem -LiteralPath $snapDir -Filter '*.snap' -File |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if (-not $snapFile) { throw 'No .snap produced under snap/' }
            $script:piSnapDest = Join-Path $outRoot "vardyparty-local-service_arm64-$lsVersion.snap"
            Copy-Item $snapFile.FullName $script:piSnapDest -Force
            Write-Host "Pi snap: $($script:piSnapDest)" -ForegroundColor Green
        }
        finally {
            Copy-Item $snapYamlBackup $snapYaml -Force
            Remove-Item $snapYamlBackup -Force -ErrorAction SilentlyContinue
        }
    }
}

# ── Client Windows MSIX (signed + PatchAppSettings) ─────────────────────────
if (-not $PublishOnly -and -not $SkipClient) {
    Write-Host "`n=== Client Windows MSIX (PatchAppSettings + sign) ===" -ForegroundColor Cyan
    Push-Location $ClientRepo
    try {
        $sdkVersion = (& dotnet --version)
        if (-not $sdkVersion.StartsWith('11.')) {
            throw "Client needs .NET 11 SDK (got $sdkVersion)"
        }
        $certPath = $PfxPath
        $clientStampSha = Get-GitHeadSha $ClientRepo
        $clientStampProps = Get-ProvenancePublishProps "$bumpedClientDisplay.$clientBuild" $clientStampSha
        $publishArgs = @(
            'publish', './VardyParty/VardyParty.csproj',
            '-f', 'net11.0-windows10.0.19041.0',
            '-c', 'Release',
            '-p:SelfContained=true',
            '-p:RunGenerateBuildInfo=true',
            '-p:RunGenerateSplash=true',
            '-p:PatchAppSettings=true',
            '-p:WindowsPackageType=MSIX',
            '-p:AppxPackageSigningEnabled=true',
            "-p:PackageCertificateKeyFile=$certPath",
            "-p:PackageCertificatePassword=$PfxPassword"
        ) + $clientStampProps
        Write-Host "dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray
        & dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) { throw "Client Windows publish failed ($LASTEXITCODE)" }

        $searchRoots = @(
            (Join-Path $ClientRepo 'VardyParty\bin\Release\net11.0-windows10.0.19041.0\win-x64\AppPackages'),
            (Join-Path $ClientRepo 'VardyParty\bin\Release\net11.0-windows10.0.19041.0\win-x64\publish'),
            (Join-Path $ClientRepo 'VardyParty\AppPackages'),
            (Join-Path $ClientRepo 'VardyParty\bin\Release\net11.0-windows10.0.19041.0\win-x64')
        )
        $msix = $null
        foreach ($root in $searchRoots) {
            if (-not (Test-Path -LiteralPath $root)) { continue }
            $hit = Get-ChildItem -Path $root -Filter '*.msix' -Recurse -File -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($hit) { $msix = $hit; break }
        }
        if (-not $msix) {
            throw 'No Client .msix after signed publish. Check PackageCertificate* / Windows SDK packaging.'
        }

        # Auth0 assert on packaged appsettings
        $winOut = Join-Path $ClientRepo 'VardyParty\bin\Release\net11.0-windows10.0.19041.0\win-x64'
        $appsettingsPath = @(
            (Join-Path $winOut 'appsettings.json'),
            (Join-Path $winOut 'AppX\appsettings.json')
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($appsettingsPath) {
            $as = Get-Content $appsettingsPath -Raw | ConvertFrom-Json
            if ([string]::IsNullOrWhiteSpace([string]$as.Auth0.ClientId) -or [string]::IsNullOrWhiteSpace([string]$as.Auth0.Domain)) {
                throw "Packaged Client appsettings has empty Auth0 ClientId/Domain at $appsettingsPath"
            }
            Write-Host "[WIN CHECK] Auth0 Domain=$($as.Auth0.Domain) ClientId=NON_EMPTY" -ForegroundColor Green
        }

        $name = "VardyParty-windows-v$bumpedClientDisplay-b$clientBuild.msix"
        $script:clientMsixDest = Join-Path $outRoot $name
        Copy-Item $msix.FullName $script:clientMsixDest -Force
        Write-Host "Client MSIX: $($script:clientMsixDest)" -ForegroundColor Green

        # Export CER next to artifacts
        $script:cerDest = Join-Path $outRoot 'vardyparty.cer'
        [IO.File]::WriteAllBytes($script:cerDest, $pfx.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    }
    finally {
        Pop-Location
        Write-Host "NOTE: restore Client appsettings if dirty: git -C `"$ClientRepo`" restore VardyParty/appsettings.json" -ForegroundColor Yellow
    }
}

# ── Android APK ─────────────────────────────────────────────────────────────
if (-not $PublishOnly -and -not $SkipClient -and -not $SkipAndroid) {
    Write-Host "`n=== Android APK (PatchAppSettings) ===" -ForegroundColor Cyan
    Push-Location $ClientRepo
    try {
        & pwsh -NoProfile -File (Join-Path $ClientRepo 'package-android.ps1') -Api $AndroidApi
        if ($LASTEXITCODE -ne 0) { throw "package-android.ps1 failed ($LASTEXITCODE)" }
        $apk = Join-Path $ClientRepo 'VardyParty\bin\Release\net11.0-android\com.vardyparty-Signed.apk'
        if (-not (Test-Path -LiteralPath $apk)) { throw "Expected APK at $apk" }
        $apkName = "VardyParty-android-v$bumpedClientDisplay-b$clientBuild.apk"
        $script:clientApkDest = Join-Path $outRoot $apkName
        Copy-Item $apk $script:clientApkDest -Force
        Write-Host "Android APK: $($script:clientApkDest)" -ForegroundColor Green
    }
    finally {
        Pop-Location
    }
}

# ── Provenance manifest (always, even when -SkipPublish) ────────────────────
$lsPins = [ordered]@{}
foreach ($pkgId in @(
        'VardyParty.LocalService.Abstractions',
        'VardyParty.LocalService.V1',
        'VardyParty.LocalService.V2',
        'VardyParty.LocalService.V1.Scrape',
        'VardyParty.LocalService.V2.Scrape'
    )) {
    try { $lsPins[$pkgId] = Get-PackagePin $localServiceCsproj $pkgId } catch { }
}
# Refresh SHAs after bumps (may still be dirty — recorded separately).
$provenanceShas.localservice = Get-GitHeadSha $repoRoot
if ($ClientRepo) { $provenanceShas.client = Get-GitHeadSha $ClientRepo }
if ($StrategiesRepo) { $provenanceShas.strategies = Get-GitHeadSha $StrategiesRepo }

$artifactHashes = [ordered]@{}
foreach ($pair in @(
        @{ Key = 'localservice_win_msix'; Path = $localMsixDest },
        @{ Key = 'localservice_pi_snap'; Path = $piSnapDest },
        @{ Key = 'client_win_msix'; Path = $clientMsixDest },
        @{ Key = 'client_android_apk'; Path = $clientApkDest },
        @{ Key = 'certificate_cer'; Path = $cerDest }
    )) {
    $h = Get-FileSha256 $pair.Path
    if ($h) {
        $artifactHashes[$pair.Key] = [ordered]@{
            path   = $pair.Path
            sha256 = $h
            bytes  = (Get-Item -LiteralPath $pair.Path).Length
        }
    }
}

$dirtyPaths = [ordered]@{
    localservice = @(Test-GitPathDirty $repoRoot @(
            'VardyParty.LocalService/VardyParty.LocalService.csproj'
        ))
    client       = if ($ClientRepo) {
        @(Test-GitPathDirty $ClientRepo @('Version.props'))
    }
    else { @() }
    strategies   = if ($StrategiesRepo) {
        @(Test-GitPathDirty $StrategiesRepo @(
                'VardyParty.LocalService.Abstractions/VardyParty.LocalService.Abstractions.csproj',
                'VardyParty.LocalService.V1/VardyParty.LocalService.V1.csproj',
                'VardyParty.LocalService.V2/VardyParty.LocalService.V2.csproj',
                'VardyParty.LocalService.V1.Scrape/VardyParty.LocalService.V1.Scrape.csproj',
                'VardyParty.LocalService.V2.Scrape/VardyParty.LocalService.V2.Scrape.csproj'
            ))
    }
    else { @() }
}

$provenancePath = Write-ReleaseProvenance -OutDir $outRoot -Payload ([ordered]@{
        stamp              = $stamp
        generatedAtUtc     = (Get-Date).ToUniversalTime().ToString('o')
        localserviceVersion = $lsVersion
        clientDisplayVersion = $bumpedClientDisplay
        clientBuild        = $clientBuild
        commits            = $provenanceShas
        packagePins        = $lsPins
        artifacts          = $artifactHashes
        dirtyVersionFiles  = $dirtyPaths
        channels           = [ordered]@{
            clientInstallers = 'GitHub Releases on Vardy-Party/Client (site reads /releases/latest + R2 client-release-latest.json fallback)'
            helpers          = 'R2 keys VardyParty.LocalService-win-x64.msix / linux snaps (site /downloads/helper-*); mirrored on Vardy-Party/M3U8-resolver Releases'
        }
    })

# ── Publish: R2 helpers + GitHub Releases ───────────────────────────────────
if (-not $SkipPublish) {
    Write-Host "`n=== Publish overrides ===" -ForegroundColor Cyan

    $dirtyFlat = @($dirtyPaths.localservice + $dirtyPaths.client + $dirtyPaths.strategies) | Where-Object { $_ }
    if ($dirtyFlat.Count -gt 0 -and -not $AllowDirtyPublish) {
        Write-Host 'Refusing publish: version-bearing files are dirty. Commit them so the release tag targets the same Version/pins as the artifacts.' -ForegroundColor Red
        foreach ($d in $dirtyFlat) { Write-Host "  dirty: $d" -ForegroundColor Yellow }
        Write-Host 'Then re-run with -PublishOnly (or pass -AllowDirtyPublish to override — tags will NOT include those bumps).' -ForegroundColor Yellow
        throw "Publish blocked: dirty version files ($($dirtyFlat -join ', '))"
    }
    if ($dirtyFlat.Count -gt 0 -and $AllowDirtyPublish) {
        Write-Host 'WARNING: -AllowDirtyPublish set; GitHub tags target HEAD without the dirty Version/pin bumps.' -ForegroundColor Yellow
    }

    # Committed Version must match artifact versions (catches "bump left dirty" after a force override, and tag/name drift).
    if (-not $SkipLocalService) {
        $committedLs = Get-CsprojVersion $localServiceCsproj
        if ($committedLs -ne $lsVersion) {
            throw "Committed LocalService Version '$committedLs' != artifact version '$lsVersion'. Commit the csproj bump (or rebuild) before publish."
        }
    }
    if (-not $SkipClient -and $ClientRepo) {
        $vpText = Get-Content -LiteralPath (Join-Path $ClientRepo 'Version.props') -Raw
        if ($vpText -notmatch '<ApplicationDisplayVersion>([^<]+)</ApplicationDisplayVersion>') {
            throw 'Could not read committed Client ApplicationDisplayVersion'
        }
        $committedDisplay = $Matches[1]
        if ($committedDisplay -ne $bumpedClientDisplay) {
            throw "Committed Client ApplicationDisplayVersion '$committedDisplay' != artifact display '$bumpedClientDisplay'. Commit Version.props before publish."
        }
    }

    $token = $null
    try {
        $token = (gh auth token --user Vardy-Party).Trim()
        $env:GH_TOKEN = $token
    }
    catch {
        Write-Host 'gh auth token --user Vardy-Party failed; using current gh auth.' -ForegroundColor Yellow
    }

    $lsTarget = $provenanceShas.localservice
    $clientTarget = $provenanceShas.client
    if (-not $lsTarget) { throw 'Could not resolve LocalService HEAD for --target' }

    $pinLines = ($lsPins.GetEnumerator() | ForEach-Object { "- $($_.Key): $($_.Value)" }) -join "`n"
    $hashLines = ($artifactHashes.GetEnumerator() | ForEach-Object {
            "- $($_.Key): sha256:$($_.Value.sha256) ($($_.Value.bytes) bytes)"
        }) -join "`n"

    # Helpers → R2 (site) + M3U8-resolver GitHub Release
    if (-not $SkipLocalService) {
        if ($localMsixDest) {
            Publish-R2Object -ApiRepoPath $ApiRepo -Key 'VardyParty.LocalService-win-x64.msix' `
                -FilePath $localMsixDest -ContentType 'application/msix' `
                -ContentDisposition "attachment; filename=`"VardyParty.LocalService-win-x64-$lsVersion.msix`""
        }
        if ($piSnapDest) {
            Publish-R2Object -ApiRepoPath $ApiRepo -Key 'vardyparty-local-service_arm64.snap' `
                -FilePath $piSnapDest -ContentType 'application/vnd.snap' `
                -ContentDisposition "attachment; filename=`"vardyparty-local-service_arm64-$lsVersion.snap`""
        }

        $lsTag = "v$lsVersion"
        # Wrap in @() — a single path is a [string]; @string character-splats ("no matches found for C").
        $lsAssets = @( @($localMsixDest, $piSnapDest, $provenancePath) | Where-Object { $_ -and (Test-Path $_) } )
        if ($lsAssets.Count -gt 0) {
            Write-Host "GitHub Release M3U8-resolver $lsTag (target $lsTarget)" -ForegroundColor Cyan
            $notes = @"
Offline local release $stamp (Actions paused). Helpers also uploaded to R2 for example.invalid.

## Provenance
- LocalService commit: ``$lsTarget``
- Client commit: ``$clientTarget``
- Strategies commit: ``$($provenanceShas.strategies)``
- LocalService Version: $lsVersion
- InformationalVersion: $lsVersion+$lsTarget

### Package pins
$pinLines

### Artifact hashes
$hashLines

See attached ``provenance.json`` and docs/INSTALLER_CHANNELS.md.
"@
            Publish-GitHubReleaseWithTarget -Repo 'Vardy-Party/M3U8-resolver' -Tag $lsTag `
                -TargetCommit $lsTarget -Title "LocalService $lsVersion (local offline)" `
                -Notes $notes -Assets $lsAssets
            Write-Host "https://github.com/Vardy-Party/M3U8-resolver/releases/tag/$lsTag" -ForegroundColor Green
        }
    }

    # Client → GitHub Release + R2 snapshot
    if (-not $SkipClient) {
        if (-not $clientTarget) { throw 'Could not resolve Client HEAD for --target' }
        $clientTag = "$bumpedClientDisplay-b$clientBuild"
        $clientAssets = @( @($clientMsixDest, $clientApkDest, $cerDest, $provenancePath) | Where-Object { $_ -and (Test-Path $_) } )
        if ($clientAssets.Count -eq 0) { throw 'No Client assets to publish' }
        Write-Host "GitHub Release Client $clientTag (target $clientTarget)" -ForegroundColor Cyan
        $notes = @"
Offline local release $stamp (Actions paused). Becomes /releases/latest until a newer CD release.

## Provenance
- Client commit: ``$clientTarget``
- LocalService commit: ``$lsTarget``
- Display version: $bumpedClientDisplay (build $clientBuild)
- InformationalVersion: $bumpedClientDisplay.$clientBuild+$clientTarget

### Artifact hashes
$hashLines

See attached ``provenance.json`` and M3U8-resolver docs/INSTALLER_CHANNELS.md.
"@
        Publish-GitHubReleaseWithTarget -Repo 'Vardy-Party/Client' -Tag $clientTag `
            -TargetCommit $clientTarget `
            -Title "VardyParty $bumpedClientDisplay (build $clientBuild, local offline)" `
            -Notes $notes -Assets $clientAssets -Latest

        $releaseJson = gh api "repos/Vardy-Party/Client/releases/tags/$clientTag" | ConvertFrom-Json
        $snapshotAssets = @($releaseJson.assets | ForEach-Object {
                [pscustomobject]@{ name = $_.name; browser_download_url = $_.browser_download_url }
            })
        $snapshotPath = Join-Path $outRoot 'client-release-latest.json'
        @{
            assets     = $snapshotAssets
            provenance = [ordered]@{
                clientCommit         = $clientTarget
                localserviceCommit   = $lsTarget
                displayVersion       = $bumpedClientDisplay
                build                = $clientBuild
                stamp                = $stamp
            }
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $snapshotPath -Encoding utf8
        Publish-R2Object -ApiRepoPath $ApiRepo -Key 'client-release-latest.json' `
            -FilePath $snapshotPath -ContentType 'application/json' -ContentDisposition $null

        Write-Host "https://github.com/Vardy-Party/Client/releases/tag/$clientTag" -ForegroundColor Green
    }

    if ($token) { Remove-Item Env:GH_TOKEN -ErrorAction SilentlyContinue }
}

Write-Host "`n=== Done ===" -ForegroundColor Cyan
Write-Host "Artifacts: $outRoot"
Get-ChildItem $outRoot -File | ForEach-Object {
    Write-Host ("  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB))
}
if ($bumpedLocalService -and (@(Test-GitPathDirty $repoRoot @('VardyParty.LocalService/VardyParty.LocalService.csproj')).Count -gt 0)) {
    Write-Host "Commit LocalService version before publish: $lsVersion in $localServiceCsproj" -ForegroundColor Yellow
}
if ($bumpedClientDisplay -and $ClientRepo -and (@(Test-GitPathDirty $ClientRepo @('Version.props')).Count -gt 0)) {
    Write-Host "Commit Client Version.props before publish: $bumpedClientDisplay (build $clientBuild)" -ForegroundColor Yellow
}
if ($publishedStrategyVersions.Count -gt 0 -and -not $SkipNugetPublish) {
    Write-Host "Commit Strategies Version bumps + LocalService PackageReference pins before publish." -ForegroundColor Yellow
    foreach ($id in ($publishedStrategyVersions.Keys | Sort-Object)) {
        Write-Host "  $id → $($publishedStrategyVersions[$id])"
    }
}
Write-Host @"

Site: https://example.invalid/  (helpers from R2; Client from GitHub latest)
Channels: docs/INSTALLER_CHANNELS.md
NuGet: github-vardy-party (Strategies packs pushed when not -SkipNugetPublish)
When Actions returns: official CD with a higher version supersedes /releases/latest;
re-run Main Packaging to refresh R2 helpers.
"@
