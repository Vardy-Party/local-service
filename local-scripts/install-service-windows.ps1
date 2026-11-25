param(
    [string]$ServiceName = "VardyPartyLocalService",
    [string]$DisplayName = "VardyParty Local Service",
    [string]$ProjectPath = "$(Join-Path $PSScriptRoot '..\VardyParty.LocalService\VardyParty.LocalService.csproj')",
    [string]$InstallDirectory = "$(Join-Path $env:ProgramFiles 'VardyParty\LocalService')",
    [string]$Configuration = "Release",
    [string]$Framework = "net11.0",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

function Ensure-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "This installer must be run from an elevated PowerShell session (Run as Administrator)."
    }
}

function Get-DotnetPath {
    $dotnet = Get-Command dotnet -ErrorAction Stop
    return $dotnet.Source
}

# Stops the service (if installed and not already stopped) and waits until its
# process has exited, so the publish below can overwrite the exe/dlls it had
# loaded. An SCM stop does not trigger the sc.exe failure/restart actions.
function Stop-ExistingService([string]$Name, [int]$TimeoutSeconds = 60) {
    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if (-not $service -or $service.Status -eq 'Stopped') {
        return
    }

    $processId = (Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'").ProcessId
    Write-Host "Stopping service '$Name' (PID $processId)..." -ForegroundColor Yellow
    Stop-Service -Name $Name -Force
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds($TimeoutSeconds))

    if ($processId -and $processId -ne 0) {
        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($process -and -not $process.WaitForExit($TimeoutSeconds * 1000)) {
            throw "Service '$Name' stopped but process $processId did not exit within $TimeoutSeconds s; files in the install directory may still be locked."
        }
    }
}

Ensure-Admin

$projectFullPath = [System.IO.Path]::GetFullPath($ProjectPath)
if (-not (Test-Path $projectFullPath)) {
    throw "Could not find project file '$projectFullPath'."
}

$installPath = [System.IO.Path]::GetFullPath($InstallDirectory)
if (-not (Test-Path $installPath)) {
    New-Item -ItemType Directory -Path $installPath -Force | Out-Null
}

if (-not $SkipPublish) {
    # Stop first: a running service holds its exe/dlls open and the publish into
    # $installPath would fail (or half-overwrite) while it is loaded.
    Stop-ExistingService -Name $ServiceName
    $dotnetPath = Get-DotnetPath
    Write-Host "Publishing VardyParty.LocalService to '$installPath' (Auth0 injected)..." -ForegroundColor Cyan
    $repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    . (Join-Path $repoRoot 'scripts\import-auth0-env.ps1')
    Import-VpAuth0Env -RepoRoot $repoRoot -ProjectPath $projectFullPath -AllowUserSecrets -RequireDomainAudience
    Invoke-VpWithAuth0InjectedAppsettings -RepoRoot $repoRoot -BackupPrefix 'vp-local-svc-appsettings' -Action {
        & $dotnetPath publish $projectFullPath -c $Configuration -f $Framework -r win-x64 --self-contained true -o $installPath
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit code $LASTEXITCODE"
        }
    }
}

# Same rule as local-scripts/install-service-linux.sh and -macos.sh: a publish
# is self-contained when its runtimeconfig.json lists "includedFrameworks" or
# coreclr.dll sits next to the dll. Then the service runs the apphost and needs
# no shared .NET runtime. Anything else with the dll (e.g. an older
# framework-dependent install reused via -SkipPublish) runs `dotnet <dll>`.
$exePath = Join-Path $installPath "VardyParty.LocalService.exe"
$dllPath = Join-Path $installPath "VardyParty.LocalService.dll"
$runtimeConfigPath = Join-Path $installPath "VardyParty.LocalService.runtimeconfig.json"
$selfContained = (Test-Path -LiteralPath (Join-Path $installPath "coreclr.dll") -PathType Leaf) -or
    ((Test-Path -LiteralPath $runtimeConfigPath -PathType Leaf) -and
     (Select-String -LiteralPath $runtimeConfigPath -Pattern '"includedFrameworks"' -SimpleMatch -Quiet))

if ($selfContained) {
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
        throw "Self-contained publish in '$installPath' but '$exePath' is missing. Republish without -SkipPublish."
    }
    $binaryPath = "`"$exePath`""
}
elseif (Test-Path -LiteralPath $dllPath -PathType Leaf) {
    $binaryPath = "`"$(Get-DotnetPath)`" `"$dllPath`""
}
else {
    throw "Could not find a self-contained publish or '$dllPath' in '$installPath'. Publish failed or output path is incorrect."
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service '$ServiceName' already exists. Reinstalling..." -ForegroundColor Yellow
    # Already stopped before publishing; with -SkipPublish this is the first stop.
    Stop-ExistingService -Name $ServiceName
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
}

New-Service -Name $ServiceName -DisplayName $DisplayName -BinaryPathName $binaryPath -StartupType Automatic

sc.exe failure $ServiceName reset= 86400 actions= restart/5000 | Out-Null
Start-Service -Name $ServiceName

Write-Host "Installed and started Windows service '$ServiceName'" -ForegroundColor Green
Write-Host "Install directory: $installPath"
Write-Host "Binary: $binaryPath"
