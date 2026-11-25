# Sync selected .env entries to GitHub Actions secrets for this repo.
# Requires: gh auth, .env in the repo root (copy from .env.example).
# Usage: pwsh scripts/sync-github-secrets-from-env.ps1
#
# Auth0 secrets are required by Main Packaging (merge-appsettings-auth0.ps1).
# Signing / NuGet secrets are optional for partial packaging runs.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$repo = "Vardy-Party/local-service"
$envFile = Join-Path $root ".env"
if (-not (Test-Path $envFile)) {
    Write-Error "Missing $envFile — copy .env.example and fill values."
}

function Get-DotEnvValue([string]$key) {
    foreach ($line in Get-Content $envFile) {
        $trim = $line.Trim()
        if ($trim.StartsWith("#") -or $trim.Length -eq 0) { continue }
        $eq = $trim.IndexOf("=")
        if ($eq -lt 1) { continue }
        $name = $trim.Substring(0, $eq).Trim()
        if ($name -ne $key) { continue }
        return $trim.Substring($eq + 1).Trim().Trim('"').Trim("'")
    }
    return $null
}

function Set-SecretFromValue([string]$name, [string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        Write-Host "Skip $name (empty)"
        return
    }
    $value | gh secret set $name --repo $repo
    Write-Host "Set $name"
}

Push-Location $root
try {
    # Auth0 — required for release packaging (same tenant/audience as Client / Worker)
    $auth0Domain = Get-DotEnvValue "AUTH0_DOMAIN"
    $auth0Audience = Get-DotEnvValue "AUTH0_AUDIENCE"
    Set-SecretFromValue "AUTH0_DOMAIN" $auth0Domain
    Set-SecretFromValue "AUTH0_AUDIENCE" $auth0Audience
    Set-SecretFromValue "AUTH0_CLAIM_NAMESPACE" (Get-DotEnvValue "AUTH0_CLAIM_NAMESPACE")
    Set-SecretFromValue "AUTH0_REQUIREDROLECLAIMTYPE" (Get-DotEnvValue "AUTH0_REQUIREDROLECLAIMTYPE")
    Set-SecretFromValue "AUTH0_REQUIREDROLE" (Get-DotEnvValue "AUTH0_REQUIREDROLE")
    Set-SecretFromValue "AUTH0_REQUIRED_PERMISSION" (Get-DotEnvValue "AUTH0_REQUIRED_PERMISSION")
    Set-SecretFromValue "SCRAPE_HEALTH_ALLOWED_API_HOSTS" (Get-DotEnvValue "SCRAPE_HEALTH_ALLOWED_API_HOSTS")

    if ([string]::IsNullOrWhiteSpace($auth0Domain) -or [string]::IsNullOrWhiteSpace($auth0Audience)) {
        Write-Error "AUTH0_DOMAIN and AUTH0_AUDIENCE are required in .env (Main Packaging). Fill them and re-run."
    }

    # Windows MSIX signing
    Set-SecretFromValue "WINDOWS_CERT_BASE64" (Get-DotEnvValue "WINDOWS_CERT_BASE64")
    Set-SecretFromValue "WINDOWS_CERT_PASSWORD" (Get-DotEnvValue "WINDOWS_CERT_PASSWORD")

    # GitHub Packages restore
    Set-SecretFromValue "NUGET_GITHUB_TOKEN" (Get-DotEnvValue "NUGET_GITHUB_TOKEN")
    Set-SecretFromValue "NUGET_GITHUB_USERNAME" (Get-DotEnvValue "NUGET_GITHUB_USERNAME")
}
finally {
    Pop-Location
}
