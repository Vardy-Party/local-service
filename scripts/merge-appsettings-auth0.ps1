#Requires -Version 7.0
<#
.SYNOPSIS
  Merge Auth0 settings from environment into LocalService appsettings.json for release packaging.

.DESCRIPTION
  Used by Main Packaging (MSIX / Snap / macOS) so installers ship configured Auth0
  without committing secrets. Same pattern as Client CD (Generate appsettings.json).

  Required env:
    AUTH0_DOMAIN, AUTH0_AUDIENCE

  Optional env (empty leaves appsettings defaults):
    AUTH0_CLAIM_NAMESPACE, AUTH0_REQUIREDROLECLAIMTYPE,
    AUTH0_REQUIREDROLE, AUTH0_REQUIRED_PERMISSION,
    SCRAPE_HEALTH_ALLOWED_API_HOSTS (comma-separated host / .suffix entries
      for ScrapeHealth:AllowedApiHosts — required when Client Headless/API
      host differs from the Auth0 Audience host)

  AUTH0_DOMAIN must be the custom login/JWKS host, same as Worker AUTH0_DOMAIN —
  not a legacy *.auth0.com tenant when tokens use the custom iss.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$AppsettingsPath
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $AppsettingsPath)) {
    throw "appsettings not found: $AppsettingsPath"
}

$domain = ($env:AUTH0_DOMAIN ?? "").Trim()
$audience = ($env:AUTH0_AUDIENCE ?? "").Trim()
if ([string]::IsNullOrWhiteSpace($domain) -or [string]::IsNullOrWhiteSpace($audience)) {
    throw "AUTH0_DOMAIN and AUTH0_AUDIENCE must be set (repo Actions secrets). Refusing to pack an unusable LocalService."
}

$raw = Get-Content -LiteralPath $AppsettingsPath -Raw
$appsettings = $raw | ConvertFrom-Json
if (-not $appsettings.Auth0) {
    $appsettings | Add-Member -NotePropertyName Auth0 -NotePropertyValue ([pscustomobject]@{})
}

$appsettings.Auth0.Domain = $domain
$appsettings.Auth0.Audience = $audience

$claimNs = ($env:AUTH0_CLAIM_NAMESPACE ?? "").Trim()
if (-not [string]::IsNullOrWhiteSpace($claimNs)) {
    $appsettings.Auth0.ClaimNamespace = $claimNs
}

$roleClaim = ($env:AUTH0_REQUIREDROLECLAIMTYPE ?? "").Trim()
if (-not [string]::IsNullOrWhiteSpace($roleClaim)) {
    $appsettings.Auth0.RequiredRoleClaimType = $roleClaim
}

$role = ($env:AUTH0_REQUIREDROLE ?? "").Trim()
if (-not [string]::IsNullOrWhiteSpace($role)) {
    $appsettings.Auth0.RequiredRole = $role
}

$permission = ($env:AUTH0_REQUIRED_PERMISSION ?? "").Trim()
if (-not [string]::IsNullOrWhiteSpace($permission)) {
    $appsettings.Auth0.RequiredPermission = $permission
}

$allowedHostsRaw = ($env:SCRAPE_HEALTH_ALLOWED_API_HOSTS ?? "").Trim()
if (-not [string]::IsNullOrWhiteSpace($allowedHostsRaw)) {
    if (-not $appsettings.ScrapeHealth) {
        $appsettings | Add-Member -NotePropertyName ScrapeHealth -NotePropertyValue ([pscustomobject]@{ AllowedApiHosts = @() })
    }
    $hosts = @(
        $allowedHostsRaw -split ',' |
            ForEach-Object { $_.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    $appsettings.ScrapeHealth.AllowedApiHosts = $hosts
}

$json = $appsettings | ConvertTo-Json -Depth 10
# UTF-8 without BOM — matches typical ASP.NET published appsettings
[System.IO.File]::WriteAllText((Resolve-Path -LiteralPath $AppsettingsPath), $json + "`n")

$hostCount = if ($appsettings.ScrapeHealth -and $appsettings.ScrapeHealth.AllowedApiHosts) {
    @($appsettings.ScrapeHealth.AllowedApiHosts).Count
} else { 0 }
Write-Host "Merged Auth0 into $AppsettingsPath (Domain=$domain, Audience set, Role=$($appsettings.Auth0.RequiredRole), ScrapeHealth hosts=$hostCount)"
