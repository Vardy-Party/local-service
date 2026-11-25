<#
.SYNOPSIS
  Run VardyParty.LocalService from source (foreground, discovery HTTP ports).

.DESCRIPTION
  Self-locating wrapper around `dotnet run`. Uses --no-launch-profile so Program.cs
  binds the first free discovery port (5019, 5137, …) instead of launchSettings 5162.

.PARAMETER Configuration
  Build configuration. Default: Debug.

.PARAMETER NoBuild
  Pass --no-build to dotnet run (expects a prior build).

.PARAMETER SkipRestore
  Pass --no-restore to dotnet run.

.EXAMPLE
  pwsh ./scripts/run-from-source.ps1
  pwsh ./scripts/run-from-source.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$NoBuild,

    [switch]$SkipRestore,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$DotnetRunArgs
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot

$project = Join-Path $repoRoot 'VardyParty.LocalService\VardyParty.LocalService.csproj'
if (-not (Test-Path -LiteralPath $project)) {
    throw "Project not found: $project"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet SDK not found on PATH. Install .NET 11+ and retry.'
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
if (-not $env:ASPNETCORE_ENVIRONMENT) {
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
}

# Auth0 from repo .env (+ LocalService user-secrets fallback) — same secrets packaging injects.
$auth0Import = Join-Path $PSScriptRoot 'import-auth0-env.ps1'
if (Test-Path -LiteralPath $auth0Import) {
    . $auth0Import
    Import-VpAuth0Env -RepoRoot $repoRoot -ProjectPath $project -AllowUserSecrets
}

# Repo NuGet.config clears parent sources; credentials must target key github-vardy-party.
$authScript = Join-Path $PSScriptRoot 'ensure-github-packages-auth.ps1'
if (Test-Path -LiteralPath $authScript) {
    & $authScript -RepoRoot $repoRoot | Out-Null
}

# Sibling Strategies local packs (optional) so a just-built V2.Scrape can restore before GH Packages auth works.
$strategiesLocal = Join-Path (Split-Path $repoRoot -Parent) 'Strategies\artifacts\nuget'
$restoreExtra = @()
if (Test-Path -LiteralPath $strategiesLocal) {
    $restoreExtra += "/p:RestoreAdditionalProjectSources=$strategiesLocal"
    Write-Host "  NuGet: also using strategies-local at $strategiesLocal" -ForegroundColor DarkGray
}

$runArgs = @(
    'run',
    '--project', $project,
    '-c', $Configuration,
    '--no-launch-profile'
)
if ($NoBuild) { $runArgs += '--no-build' }
if ($SkipRestore) { $runArgs += '--no-restore' }
if ($restoreExtra -and -not $SkipRestore -and -not $NoBuild) { $runArgs += $restoreExtra }
if ($DotnetRunArgs) { $runArgs += $DotnetRunArgs }

$auth0State = if (-not [string]::IsNullOrWhiteSpace($env:AUTH0_DOMAIN) -and -not [string]::IsNullOrWhiteSpace($env:AUTH0_AUDIENCE)) {
    "Domain=$($env:AUTH0_DOMAIN) Audience=NON_EMPTY"
} else {
    'MISSING — set AUTH0_DOMAIN/AUTH0_AUDIENCE in .env or: dotnet user-secrets set Auth0:Domain … --project VardyParty.LocalService'
}

Write-Host "Running LocalService from source ($Configuration)…" -ForegroundColor Cyan
Write-Host "  project: $project"
Write-Host "  ASPNETCORE_ENVIRONMENT=$($env:ASPNETCORE_ENVIRONMENT)"
Write-Host "  Auth0: $auth0State (.env → AUTH0_*/Auth0__*; user-secrets id 'vardyparty-localservice-auth0'; appsettings.Local.json)"
Write-Host "  HTTP: first free of 5019, 5137, 5273, 5417, 5573 (see /health)"
Write-Host "  NuGet: github-vardy-party needs NUGET_GITHUB_TOKEN in .env (read:packages PAT)"
Write-Host ''

& dotnet @runArgs
exit $LASTEXITCODE
