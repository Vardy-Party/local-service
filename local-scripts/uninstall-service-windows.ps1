param(
    [string]$ServiceName = "VardyPartyLocalService",
    [string]$InstallDirectory = "$(Join-Path $env:ProgramFiles 'VardyParty\LocalService')"
)

$ErrorActionPreference = "Stop"

function Ensure-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "This script must be run from an elevated PowerShell session (Run as Administrator)."
    }
}

Ensure-Admin

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Write-Host "Stopping service '$ServiceName'..." -ForegroundColor Yellow
        Stop-Service -Name $ServiceName -Force
    }

    Write-Host "Removing service '$ServiceName'..." -ForegroundColor Yellow
    sc.exe delete $ServiceName | Out-Null

    $maxWaitSeconds = 15
    $elapsed = 0
    while ($elapsed -lt $maxWaitSeconds) {
        Start-Sleep -Seconds 1
        $elapsed++
        if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
            break
        }
    }

    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Write-Warning "Service '$ServiceName' is marked for deletion and may disappear after process restart."
    }
}
else {
    Write-Host "Service '$ServiceName' was not found." -ForegroundColor DarkYellow
}

$installPath = [System.IO.Path]::GetFullPath($InstallDirectory)
if (Test-Path $installPath) {
    Write-Host "Removing install directory '$installPath'..." -ForegroundColor Yellow
    Remove-Item -Path $installPath -Recurse -Force
}

Write-Host "Uninstall complete." -ForegroundColor Green
