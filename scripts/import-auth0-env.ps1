<#
.SYNOPSIS
  Shared Auth0 env + appsettings injection helpers for LocalService publish scripts.

.DESCRIPTION
  Loads AUTH0_* (and optional SCRAPE_HEALTH_ALLOWED_API_HOSTS) from process env /
  repo-root .env, optionally Auth0 from LocalService user-secrets, then can wrap
  publish actions with backup → merge-appsettings-auth0 → restore so source
  appsettings.json is never left committed with secrets.

  Keep Domain/Audience enforcement in merge-appsettings-auth0.ps1.
#>
[CmdletBinding()]
param()

function Get-VpDotEnvValue {
    param(
        [Parameter(Mandatory)][string]$EnvFile,
        [Parameter(Mandatory)][string]$Key
    )
    if (-not (Test-Path -LiteralPath $EnvFile)) { return $null }
    foreach ($line in Get-Content -LiteralPath $EnvFile) {
        $trim = $line.Trim()
        if ($trim.StartsWith('#') -or $trim.Length -eq 0) { continue }
        $eq = $trim.IndexOf('=')
        if ($eq -lt 1) { continue }
        if ($trim.Substring(0, $eq).Trim() -eq $Key) {
            return $trim.Substring($eq + 1).Trim().Trim('"').Trim("'")
        }
    }
    return $null
}

function Import-VpAuth0Env {
    <#
    .SYNOPSIS
      Populate AUTH0_* / scrape-health allowlist process env from .env and optionally LocalService user-secrets.
    #>
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [string]$ProjectPath,
        [switch]$AllowUserSecrets,
        [switch]$RequireDomainAudience
    )

    $envFile = Join-Path $RepoRoot '.env'
    $map = [ordered]@{
        AUTH0_DOMAIN                = 'Auth0:Domain'
        AUTH0_AUDIENCE              = 'Auth0:Audience'
        AUTH0_CLAIM_NAMESPACE       = 'Auth0:ClaimNamespace'
        AUTH0_REQUIREDROLECLAIMTYPE = 'Auth0:RequiredRoleClaimType'
        AUTH0_REQUIREDROLE          = 'Auth0:RequiredRole'
        AUTH0_REQUIRED_PERMISSION   = 'Auth0:RequiredPermission'
    }

    foreach ($envKey in $map.Keys) {
        if (-not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($envKey))) { continue }
        $fromEnv = Get-VpDotEnvValue -EnvFile $envFile -Key $envKey
        if ($fromEnv) { Set-Item -Path "Env:$envKey" -Value $fromEnv }
    }

    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('SCRAPE_HEALTH_ALLOWED_API_HOSTS'))) {
        $scrapeHosts = Get-VpDotEnvValue -EnvFile $envFile -Key 'SCRAPE_HEALTH_ALLOWED_API_HOSTS'
        if ($scrapeHosts) { Set-Item -Path 'Env:SCRAPE_HEALTH_ALLOWED_API_HOSTS' -Value $scrapeHosts }
    }

    if ($AllowUserSecrets) {
        $needSecrets = [string]::IsNullOrWhiteSpace($env:AUTH0_DOMAIN) -or [string]::IsNullOrWhiteSpace($env:AUTH0_AUDIENCE)
        if ($needSecrets -and -not [string]::IsNullOrWhiteSpace($ProjectPath) -and (Test-Path -LiteralPath $ProjectPath)) {
            $list = & dotnet user-secrets list --project $ProjectPath 2>$null
            $secretMap = @{}
            foreach ($line in $list) {
                $eq = $line.IndexOf('=')
                if ($eq -lt 1) { continue }
                $k = $line.Substring(0, $eq).Trim()
                $v = $line.Substring($eq + 1).Trim()
                $secretMap[$k] = $v
            }
            foreach ($envKey in $map.Keys) {
                if (-not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($envKey))) { continue }
                $secretKey = $map[$envKey]
                if ($secretMap.ContainsKey($secretKey) -and -not [string]::IsNullOrWhiteSpace($secretMap[$secretKey])) {
                    Set-Item -Path "Env:$envKey" -Value $secretMap[$secretKey]
                }
            }
        }
    }

    # ASP.NET Core config binds Auth0__* (not AUTH0_*). Mirror so appsettings
    # section + ScrapeHealth audience host see the same values as ResolveAuth0Settings.
    Sync-VpAuth0ConfigurationAliases

    if ($RequireDomainAudience) {
        if ([string]::IsNullOrWhiteSpace($env:AUTH0_DOMAIN) -or [string]::IsNullOrWhiteSpace($env:AUTH0_AUDIENCE)) {
            throw "Auth0 Domain/Audience missing. Set AUTH0_* in .env (or env), or LocalService user-secrets, before publish."
        }
    }
}

function Sync-VpAuth0ConfigurationAliases {
    <#
    .SYNOPSIS
      Copy AUTH0_* process env into Auth0__* so IConfiguration binds Domain/Audience.
    #>
    $aliases = [ordered]@{
        AUTH0_DOMAIN                = 'Auth0__Domain'
        AUTH0_AUDIENCE              = 'Auth0__Audience'
        AUTH0_CLAIM_NAMESPACE       = 'Auth0__ClaimNamespace'
        AUTH0_REQUIREDROLECLAIMTYPE = 'Auth0__RequiredRoleClaimType'
        AUTH0_REQUIREDROLE          = 'Auth0__RequiredRole'
        AUTH0_REQUIRED_PERMISSION   = 'Auth0__RequiredPermission'
        SCRAPE_HEALTH_ALLOWED_API_HOSTS = 'ScrapeHealth__AllowedApiHosts__0'
    }

    foreach ($authKey in $aliases.Keys) {
        $value = [Environment]::GetEnvironmentVariable($authKey)
        if ([string]::IsNullOrWhiteSpace($value)) { continue }
        $configKey = $aliases[$authKey]
        if ($authKey -eq 'SCRAPE_HEALTH_ALLOWED_API_HOSTS') {
            # Expand CSV into ScrapeHealth__AllowedApiHosts__N so IConfiguration binds
            # the same surface as appsettings / merge-appsettings (no ambient CSV in C#).
            $parts = @($value -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_.Length -gt 0 })
            if ($parts.Count -eq 0) { continue }
            $idx = 0
            while (-not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable("ScrapeHealth__AllowedApiHosts__$idx"))) {
                $idx++
                if ($idx -gt 256) { break }
            }
            foreach ($hostEntry in $parts) {
                Set-Item -Path "Env:ScrapeHealth__AllowedApiHosts__$idx" -Value $hostEntry
                $idx++
            }
            continue
        }
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($configKey))) {
            Set-Item -Path "Env:$configKey" -Value $value
        }
    }
}

function Invoke-VpWithAuth0InjectedAppsettings {
    <#
    .SYNOPSIS
      Backup LocalService appsettings, merge Auth0 from env, run Action, restore.
    #>
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][scriptblock]$Action,
        [string]$AppsettingsPath,
        [string]$BackupPrefix = 'vp-auth0-appsettings'
    )

    if ([string]::IsNullOrWhiteSpace($AppsettingsPath)) {
        $AppsettingsPath = Join-Path $RepoRoot 'VardyParty.LocalService\appsettings.json'
    }
    if (-not (Test-Path -LiteralPath $AppsettingsPath)) {
        throw "appsettings not found: $AppsettingsPath"
    }

    $mergeScript = Join-Path $RepoRoot 'scripts\merge-appsettings-auth0.ps1'
    if (-not (Test-Path -LiteralPath $mergeScript)) {
        throw "merge script not found: $mergeScript"
    }

    $backup = Join-Path $env:TEMP ("{0}-{1}.json" -f $BackupPrefix, [guid]::NewGuid().ToString('n'))
    Copy-Item -LiteralPath $AppsettingsPath -Destination $backup -Force
    try {
        & $mergeScript -AppsettingsPath $AppsettingsPath
        & $Action
    }
    finally {
        Copy-Item -LiteralPath $backup -Destination $AppsettingsPath -Force
        Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
        Write-Host "Restored source appsettings.json (Auth0 not left committed)." -ForegroundColor DarkGray
    }
}
