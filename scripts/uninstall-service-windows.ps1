param(
    [string]$InstallDirectory = 'C:\Program Files\VardyParty\LocalService',
    [string]$ServiceName = 'VardyPartyLocalService'
)

$ErrorActionPreference = 'Stop'

# Require true elevation
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $isAdmin) {
    throw 'Run this script must be run from an elevated PowerShell session (Administrator).'
}

Write-Host "Uninstalling service: $ServiceName" -ForegroundColor Cyan

# Helper to check if service exists
function ServiceExists($name) {
    return (Get-Service -Name $name -ErrorAction SilentlyContinue) -ne $null
}

# Remove firewall rules that the installer may have added
Write-Host "Removing firewall rules (if present)..." -ForegroundColor Yellow
try {
    netsh advfirewall firewall delete rule name="VardyParty.LocalService HTTP" 2>$null | Out-Null
    netsh advfirewall firewall delete rule name="VardyParty.LocalService UDP Discovery" 2>$null | Out-Null
    # Also attempt to remove any rules that start with the base name
    netsh advfirewall firewall delete rule name="VardyParty.LocalService HTTP %" 2>$null | Out-Null
    netsh advfirewall firewall delete rule name="VardyParty.LocalService UDP %" 2>$null | Out-Null
    Write-Host "Firewall rule removal attempted." -ForegroundColor Green
}
catch {
    Write-Host "Firewall rule removal encountered an error: $_" -ForegroundColor Yellow
}

# Attempt to stop the service if running
if (ServiceExists $ServiceName) {
    try {
        $svc = Get-Service -Name $ServiceName -ErrorAction Stop
        if ($svc.Status -ne 'Stopped') {
            Write-Host "Stopping service..." -ForegroundColor Yellow
            Stop-Service -Name $ServiceName -Force -ErrorAction Stop
            Start-Sleep -Seconds 2
        }
    }
    catch {
        Write-Host "Warning: Failed to stop service via Stop-Service: $_" -ForegroundColor Yellow
    }

    # Try sc.exe delete
    Write-Host "Deleting service via sc.exe..." -ForegroundColor Yellow
    $scOutput = & sc.exe delete $ServiceName 2>&1
    Write-Host $scOutput
    Start-Sleep -Seconds 2

    # If still exists, try WMI Delete
    if (ServiceExists $ServiceName) {
        Write-Host "Service still exists after sc.exe. Trying WMI deletion..." -ForegroundColor Yellow
        try {
            $wmi = Get-WmiObject -Class Win32_Service -Filter "Name='$ServiceName'" -ErrorAction Stop
            $result = $wmi.Delete()
            if ($result.ReturnValue -eq 0) {
                Write-Host "WMI: service deleted successfully." -ForegroundColor Green
            }
            else {
                Write-Host "WMI: Delete returned code $($result.ReturnValue)." -ForegroundColor Yellow
            }
        }
        catch {
            Write-Host "WMI deletion failed: $_" -ForegroundColor Red
        }

        Start-Sleep -Seconds 1
    }

    # Final check
    if (ServiceExists $ServiceName) {
        Write-Host "ERROR: Service '$ServiceName' still exists. You may need to reboot or remove it manually via Services.msc or with an elevated administrative account." -ForegroundColor Red
    }
    else {
        Write-Host "Service '$ServiceName' removed." -ForegroundColor Green
    }
}
else {
    Write-Host "Service '$ServiceName' not found." -ForegroundColor Cyan
}

# Remove installation directory if present
if (Test-Path $InstallDirectory) {
    try {
        Write-Host "Removing installation directory: $InstallDirectory" -ForegroundColor Yellow
        Remove-Item -Path $InstallDirectory -Recurse -Force -ErrorAction Stop
        Write-Host "Installation directory removed." -ForegroundColor Green
    }
    catch {
        Write-Host "Failed to remove installation directory: $_" -ForegroundColor Red
    }
}

Write-Host "Uninstall complete." -ForegroundColor Cyan
