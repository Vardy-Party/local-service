<#
CI mock installer: reproduces what the YAML packaging+install steps do.

Usage (from repo root):
  powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\ci-mock-installer.ps1

What it does:
  1. dotnet publish VardyParty.LocalService project to artifacts/publish/release/win-x64
  2. Elevates and runs install-service-windows.ps1 using the publish output as SourceDirectory
  3. Verifies firewall rules and service status
  4. Elevates and runs uninstall-service-windows.ps1 to remove service and firewall rules
  5. Verifies cleanup

This script attempts to mimic the installer steps the CI YAML would exercise.
#>
param(
	[string]$Configuration = 'Release',
	[string]$Runtime = 'win-x64',
	[string]$PublishOutput = 'artifacts/publish/release/win-x64',
	[string]$Project = 'VardyParty.LocalService/VardyParty.LocalService.csproj'
)

Write-Host "CI-mock: publishing project $Project -> $PublishOutput (Auth0 injected)" -ForegroundColor Cyan
$repoRoot = (Get-Location).Path
. (Join-Path $repoRoot 'scripts\import-auth0-env.ps1')
$projectFullPath = Join-Path $repoRoot $Project
Import-VpAuth0Env -RepoRoot $repoRoot -ProjectPath $projectFullPath -AllowUserSecrets -RequireDomainAudience
try {
	Invoke-VpWithAuth0InjectedAppsettings -RepoRoot $repoRoot -BackupPrefix 'vp-ci-mock-appsettings' -Action {
		dotnet publish $Project -c $Configuration -r $Runtime --self-contained true -o $PublishOutput
		if ($LASTEXITCODE -ne 0) {
			throw "dotnet publish failed (exit $LASTEXITCODE)"
		}
	}
}
catch {
	Write-Host "$_ Aborting." -ForegroundColor Red
	exit 1
}

$publishDir = Join-Path (Get-Location) $PublishOutput
if (-not (Test-Path (Join-Path $publishDir 'VardyParty.LocalService.exe'))) {
	Write-Host "Published executable not found in $publishDir" -ForegroundColor Red
	exit 1
}

$installScript = Join-Path (Get-Location) 'scripts\install-service-windows.ps1'
$uninstallScript = Join-Path (Get-Location) 'scripts\uninstall-service-windows.ps1'

Write-Host "Will run installer (requires elevation). Installing from: $publishDir" -ForegroundColor Cyan
$installArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$installScript`" -SourceDirectory `"$publishDir`""
Start-Process -FilePath pwsh -ArgumentList $installArgs -Verb RunAs -Wait

Start-Sleep -Seconds 2
Write-Host "Post-install verification:" -ForegroundColor Cyan
try {
	$svc = Get-Service -Name VardyPartyLocalService -ErrorAction SilentlyContinue
	if ($svc) { Write-Host "Service status: $($svc.Status)" -ForegroundColor Green } else { Write-Host "Service not found after install." -ForegroundColor Yellow }
}
catch { Write-Host "Get-Service failed: $_" -ForegroundColor Yellow }

# Check firewall rules
Write-Host "Checking firewall rules (netsh)..." -ForegroundColor Cyan
$rules = & netsh advfirewall firewall show rule name="VardyParty*" 2>&1
Write-Host $rules

# Now run uninstall (elevated)
Write-Host "Running uninstaller (requires elevation)..." -ForegroundColor Cyan
$uninstallArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$uninstallScript`""
Start-Process -FilePath pwsh -ArgumentList $uninstallArgs -Verb RunAs -Wait

Start-Sleep -Seconds 2
Write-Host "Post-uninstall verification:" -ForegroundColor Cyan
$svc2 = Get-Service -Name VardyPartyLocalService -ErrorAction SilentlyContinue
if ($svc2) { Write-Host "Service still present: $($svc2.Status)" -ForegroundColor Red } else { Write-Host "Service removed." -ForegroundColor Green }

Write-Host "Check firewall rules after uninstall:" -ForegroundColor Cyan
$rules2 = & netsh advfirewall firewall show rule name="VardyParty*" 2>&1
Write-Host $rules2

Write-Host "CI-mock installer run complete." -ForegroundColor Cyan
