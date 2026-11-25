<#
.SYNOPSIS
  Ensure NuGet can restore Vardy-Party GitHub Packages (strategy plugins).

.DESCRIPTION
  Repo NuGet.config defines source key `github-vardy-party` and clears parent sources.
  Credentials must use that exact key (a differently named user source will 401).

  Resolves token/username from (first wins):
    1. NUGET_GITHUB_TOKEN / NUGET_GITHUB_USERNAME environment variables
    2. Repo-root .env (same keys)
    3. Existing user NuGet.config ClearTextPassword for github-vardy-party or Vardy-Party-GitHub

  Writes credentials into the user NuGet.config (not the tracked repo file).
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'

function Get-DotEnvValue([string]$envFile, [string]$key) {
    if (-not (Test-Path -LiteralPath $envFile)) { return $null }
    foreach ($line in Get-Content -LiteralPath $envFile) {
        $trim = $line.Trim()
        if ($trim.StartsWith('#') -or $trim.Length -eq 0) { continue }
        $eq = $trim.IndexOf('=')
        if ($eq -lt 1) { continue }
        $name = $trim.Substring(0, $eq).Trim()
        if ($name -ne $key) { continue }
        return $trim.Substring($eq + 1).Trim().Trim('"').Trim("'")
    }
    return $null
}

function Get-UserNuGetCredential([string]$sourceKey) {
    $cfgPath = Join-Path $env:APPDATA 'NuGet\NuGet.Config'
    if (-not (Test-Path -LiteralPath $cfgPath)) { return $null }
    [xml]$xml = Get-Content -LiteralPath $cfgPath
    $creds = $xml.configuration.packageSourceCredentials
    if (-not $creds) { return $null }
    $node = $creds[$sourceKey]
    if (-not $node) { return $null }
    $user = ($node.add | Where-Object { $_.key -eq 'Username' } | Select-Object -First 1).value
    $pass = ($node.add | Where-Object { $_.key -eq 'ClearTextPassword' } | Select-Object -First 1).value
    if ([string]::IsNullOrWhiteSpace($pass)) { return $null }
    return [pscustomobject]@{ Username = $user; Password = $pass }
}

$envFile = Join-Path $RepoRoot '.env'
$token = $env:NUGET_GITHUB_TOKEN
if ([string]::IsNullOrWhiteSpace($token)) { $token = Get-DotEnvValue $envFile 'NUGET_GITHUB_TOKEN' }
$username = $env:NUGET_GITHUB_USERNAME
if ([string]::IsNullOrWhiteSpace($username)) { $username = Get-DotEnvValue $envFile 'NUGET_GITHUB_USERNAME' }

if ([string]::IsNullOrWhiteSpace($token)) {
    foreach ($key in @('github-vardy-party', 'Vardy-Party-GitHub')) {
        $existing = Get-UserNuGetCredential $key
        if ($existing) {
            $token = $existing.Password
            if ([string]::IsNullOrWhiteSpace($username)) { $username = $existing.Username }
            break
        }
    }
}

if ([string]::IsNullOrWhiteSpace($username)) { $username = 'Vardy-Party' }

if ([string]::IsNullOrWhiteSpace($token)) {
    Write-Host @'
GitHub Packages auth missing for source github-vardy-party.

Add a classic PAT with read:packages (and SSO authorized for Vardy-Party) to .env:
  NUGET_GITHUB_USERNAME=YourGitHubLogin
  NUGET_GITHUB_TOKEN=ghp_...

Then re-run. CI uses the same keys as repo secrets NUGET_GITHUB_*.
'@ -ForegroundColor Yellow
    return $false
}

$userCfg = Join-Path $env:APPDATA 'NuGet\NuGet.Config'
if (-not (Test-Path -LiteralPath $userCfg)) {
    New-Item -ItemType Directory -Force -Path (Split-Path $userCfg) | Out-Null
    Set-Content -LiteralPath $userCfg -Value @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
"@
}

$listed = & dotnet nuget list source --configfile $userCfg 2>&1 | Out-String
if ($listed -notmatch 'github-vardy-party') {
    & dotnet nuget add source 'https://nuget.pkg.github.com/Vardy-Party/index.json' `
        --name github-vardy-party `
        --username $username `
        --password $token `
        --store-password-in-clear-text `
        --configfile $userCfg | Out-Null
} else {
    & dotnet nuget update source github-vardy-party `
        --username $username `
        --password $token `
        --store-password-in-clear-text `
        --configfile $userCfg | Out-Null
}

if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to write github-vardy-party credentials (dotnet exit $LASTEXITCODE)." -ForegroundColor Red
    return $false
}

Write-Host "GitHub Packages: credentials set for github-vardy-party (user=$username)." -ForegroundColor DarkGray
return $true
