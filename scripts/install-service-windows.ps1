<#
.SYNOPSIS
  Publish (optional) and install VardyParty.LocalService as a Windows service.

.DESCRIPTION
  Matches README: publishes from source unless -SkipPublish, installs to
  -InstallDirectory, creates firewall rules, and starts VardyPartyLocalService.

  Auth0 is injected into VardyParty.LocalService/appsettings.json for the
  publish (same as Main Packaging / merge-appsettings-auth0.ps1), then the
  source file is restored. Values come from env / .env (AUTH0_*), with
  fallback to LocalService user-secrets.

.EXAMPLE
  # Elevated: build Debug into C:\VardyParty.Dev and install
  .\scripts\install-service-windows.ps1 -Configuration Debug -InstallDirectory 'C:\VardyParty.Dev'

.EXAMPLE
  # Already published; only reinstall the service
  .\scripts\install-service-windows.ps1 -SkipPublish -SourceDirectory .\artifacts\publish\debug\win-x64 -InstallDirectory 'C:\VardyParty.Dev'
#>
param(
    [string]$SourceDirectory = '',
    [string]$InstallDirectory = 'C:\Program Files\VardyParty\LocalService',
    [string]$ServiceName = 'VardyPartyLocalService',
    [string]$DisplayName = 'VardyParty Local Service',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$ProjectPath = '',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $isAdmin) {
    throw 'Run this script from an elevated PowerShell session (Administrator).'
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $ProjectPath = Join-Path $repoRoot 'VardyParty.LocalService\VardyParty.LocalService.csproj'
}

. (Join-Path $PSScriptRoot 'import-auth0-env.ps1')

function Invoke-PublishWithAuth0([string]$outputDir) {
    Import-VpAuth0Env -RepoRoot $repoRoot -ProjectPath $ProjectPath -AllowUserSecrets -RequireDomainAudience

    $authScript = Join-Path $PSScriptRoot 'ensure-github-packages-auth.ps1'
    if (Test-Path -LiteralPath $authScript) {
        & $authScript -RepoRoot $repoRoot | Out-Null
    }

    $strategiesLocal = Join-Path (Split-Path $repoRoot -Parent) 'Strategies\artifacts\nuget'
    $publishArgs = @(
        'publish', $ProjectPath,
        '-c', $Configuration,
        '-r', $Runtime,
        '--self-contained', 'true',
        '-o', $outputDir
    )
    if (Test-Path -LiteralPath $strategiesLocal) {
        $publishArgs += "/p:RestoreAdditionalProjectSources=$strategiesLocal"
    }

    Invoke-VpWithAuth0InjectedAppsettings -RepoRoot $repoRoot -BackupPrefix 'vp-ls-appsettings' -Action {
        Write-Host "Publishing VardyParty.LocalService ($Configuration) with Auth0 injected..." -ForegroundColor Cyan
        & dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit code $LASTEXITCODE"
        }
    }

    # Assert published appsettings carries Auth0 (service account has no user-secrets)
    $publishedSettings = Join-Path $outputDir 'appsettings.json'
    if (-not (Test-Path -LiteralPath $publishedSettings)) {
        throw "Published appsettings.json missing at $publishedSettings"
    }
    $as = Get-Content -LiteralPath $publishedSettings -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$as.Auth0.Domain) -or [string]::IsNullOrWhiteSpace([string]$as.Auth0.Audience)) {
        throw "Published appsettings.json has empty Auth0 Domain/Audience at $publishedSettings"
    }
    Write-Host "Published Auth0 Domain=$($as.Auth0.Domain) Audience=NON_EMPTY Role=$($as.Auth0.RequiredRole)" -ForegroundColor Green
}

# ── Publish (unless skipped) ────────────────────────────────────────────────
if (-not $SkipPublish) {
    if (-not (Test-Path -LiteralPath $ProjectPath)) {
        throw "Project not found: $ProjectPath"
    }
    if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
        $SourceDirectory = Join-Path $repoRoot "artifacts\publish\$($Configuration.ToLowerInvariant())\$Runtime"
    }
    New-Item -ItemType Directory -Force -Path $SourceDirectory | Out-Null
    Invoke-PublishWithAuth0 -outputDir $SourceDirectory
}

# Determine source directory when SkipPublish / already published
if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
    if (Test-Path (Join-Path (Get-Location) 'VardyParty.LocalService.exe')) {
        $SourceDirectory = (Get-Location).Path
    }
    elseif (Test-Path (Join-Path $PSScriptRoot 'VardyParty.LocalService.exe')) {
        $SourceDirectory = $PSScriptRoot
    }
    else {
        $SourceDirectory = $PSScriptRoot
    }
}

if (-not (Test-Path (Join-Path $SourceDirectory 'VardyParty.LocalService.exe'))) {
    Write-Host "Error: VardyParty.LocalService.exe not found in: $SourceDirectory" -ForegroundColor Red
    Write-Host "Current directory: $(Get-Location)" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Usage:" -ForegroundColor Cyan
    Write-Host "  From repo (publish + install):" -ForegroundColor Green
    Write-Host "    .\scripts\install-service-windows.ps1 -Configuration Debug -InstallDirectory 'C:\VardyParty.Dev'"
    Write-Host ""
    Write-Host "  From publish directory:" -ForegroundColor Green
    Write-Host "    cd artifacts\publish\release\win-x64"
    Write-Host "    .\install-service-windows.ps1 -SkipPublish"
    Write-Host ""
    throw "Binary not found in: $SourceDirectory"
}

Write-Host "Installing VardyParty Local Service..."
Write-Host "Source: $SourceDirectory"
Write-Host "Destination: $InstallDirectory"
Write-Host ""

$exePath = Join-Path $InstallDirectory 'VardyParty.LocalService.exe'

New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
Copy-Item -Path (Join-Path $SourceDirectory '*') -Destination $InstallDirectory -Recurse -Force

# Create permanent firewall rules (administrator has elevated privileges to do this)
Write-Host ""
Write-Host "Creating firewall rules..." -ForegroundColor Yellow

# Remove any existing rules first
netsh advfirewall firewall delete rule name="VardyParty.LocalService HTTP" 2>$null | Out-Null
netsh advfirewall firewall delete rule name="VardyParty.LocalService UDP Discovery" 2>$null | Out-Null

# Create HTTP inbound rule for both Private and Public profiles
$httpRuleCmd = "advfirewall firewall add rule name=`"VardyParty.LocalService HTTP`" dir=in action=allow protocol=tcp localport=5019-5573 profile=private,public enable=yes description=`"VardyParty.LocalService HTTP`""
$httpResult = Invoke-Expression "netsh $httpRuleCmd" 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "  ✓ HTTP firewall rule created (TCP 5019-5573)" -ForegroundColor Green
} else {
    Write-Host "  ⚠ HTTP firewall rule: $httpResult" -ForegroundColor Yellow
}

# Create UDP inbound rule for both Private and Public profiles
$udpRuleCmd = "advfirewall firewall add rule name=`"VardyParty.LocalService UDP Discovery`" dir=in action=allow protocol=udp localport=40129-40189 profile=private,public enable=yes description=`"VardyParty.LocalService UDP Discovery`""
$udpResult = Invoke-Expression "netsh $udpRuleCmd" 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "  ✓ UDP firewall rule created (UDP 40129-40189)" -ForegroundColor Green
} else {
    Write-Host "  ⚠ UDP firewall rule: $udpResult" -ForegroundColor Yellow
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') {
        Write-Host "Stopping service..." -ForegroundColor Yellow
        Stop-Service -Name $ServiceName -Force
        Start-Sleep -Seconds 2
    }

    Write-Host "Updating service configuration..." -ForegroundColor Yellow
    # Use sc.exe with all required parameters
    sc.exe config $ServiceName binPath= "`"$exePath`"" start= auto obj= "NT AUTHORITY\NETWORK SERVICE" password= "" type= own | Out-Null
}
else {
    Write-Host "Creating new service..." -ForegroundColor Yellow
    # Create service with Network Service account directly
    sc.exe create $ServiceName binPath= "`"$exePath`"" start= auto obj= "NT AUTHORITY\NETWORK SERVICE" password= "" type= own DisplayName= $DisplayName | Out-Null
}

# Give Windows a moment to process the account change
Start-Sleep -Seconds 1

# Verify and show the current account
$serviceInfo = Get-WmiObject win32_service -Filter "name='$ServiceName'" -ErrorAction SilentlyContinue
if ($serviceInfo) {
    $currentAccount = $serviceInfo.StartName
    Write-Host ""
    Write-Host "Service configuration:" -ForegroundColor Green
    Write-Host "  Service Name: $ServiceName"
    Write-Host "  Binary Path: $exePath"
    Write-Host "  Service Account: $currentAccount"
    Write-Host ""

    if ($currentAccount -ne "NT AUTHORITY\NETWORK SERVICE") {
        Write-Host "ERROR: Service account is not Network Service!" -ForegroundColor Red
        Write-Host "Expected: NT AUTHORITY\NETWORK SERVICE" -ForegroundColor Yellow
        Write-Host "Actual: $currentAccount" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "Attempting PowerShell Set-Service method..." -ForegroundColor Yellow

        try {
            Set-Service -Name $ServiceName -StartupType Automatic
            Write-Host "  Set-Service succeeded" -ForegroundColor Green
        }
        catch {
            Write-Host "  Set-Service failed: $_" -ForegroundColor Red
        }
    }
}

Write-Host "Starting service..." -ForegroundColor Yellow
Start-Service -Name $ServiceName
Start-Sleep -Seconds 2

# Final verification
$serviceInfo = Get-WmiObject win32_service -Filter "name='$ServiceName'" -ErrorAction SilentlyContinue
if ($serviceInfo) {
    $finalAccount = $serviceInfo.StartName
    $serviceStatus = (Get-Service $ServiceName).Status
    Write-Host ""
    Write-Host "Final status:" -ForegroundColor Green
    Write-Host "  Service Status: $serviceStatus"
    Write-Host "  Service Account: $finalAccount"
}

Write-Host ""
Write-Host "The service will now manage firewall rules dynamically:"
Write-Host "  - Rules will be created when the service starts"
Write-Host "  - Rules will be deleted when the service stops"
Write-Host ""
Write-Host "Monitor firewall rule creation in Windows Event Log:"
Write-Host "  Get-EventLog -LogName Application -Source 'VardyParty.LocalService' -Newest 20"
