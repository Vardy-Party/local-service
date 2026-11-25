param(
	[string]$PackageFullName = 'VardyParty.LocalService',
	[switch]$RemoveCert
)

# Uninstall MSIX package and optionally remove trusted cert
Write-Host "Removing MSIX package(s) matching '$PackageFullName'" -ForegroundColor Cyan
$pkgs = Get-AppxPackage -Name "$PackageFullName*"
if ($pkgs -eq $null) { Write-Host "No installed MSIX packages found matching '$PackageFullName'" -ForegroundColor Yellow }
foreach ($p in $pkgs) {
	Write-Host "Removing package: $($p.PackageFullName)"
	Remove-AppxPackage -Package $p.PackageFullName -AllUsers -ErrorAction SilentlyContinue
}

# Remove firewall rules
Write-Host "Removing firewall rules..." -ForegroundColor Cyan
# Remove explicit VardyParty firewall rules
Get-NetFirewallRule -DisplayName 'VardyParty.LocalService*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
Write-Host "Firewall rules removed." -ForegroundColor Green

if ($RemoveCert) {
	Write-Host "Removing certificate from LocalMachine TrustedPeople..." -ForegroundColor Cyan
	$certs = Get-ChildItem -Path Cert:\LocalMachine\TrustedPeople | Where-Object { $_.Subject -like '*VardyParty-LocalDev*' }
	foreach ($c in $certs) { Remove-Item -Path "Cert:\LocalMachine\TrustedPeople\$($c.Thumbprint)" -Force }
}

Write-Host "Uninstall complete." -ForegroundColor Green
