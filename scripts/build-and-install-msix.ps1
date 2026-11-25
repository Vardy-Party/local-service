<#
Build and install MSIX locally (self-signed) to reproduce CI packaging behavior.
Run from repo root in an elevated PowerShell (Run as Administrator).

What this script does:
 1. Reads version from VardyParty.LocalService.csproj and computes MSIX version
 2. dotnet publish (win-x64, self-contained true) to artifacts/publish/win-x64
 3. Prepare MSIX layout and stamp AppxManifest.xml
 4. Pack MSIX using makeappx.exe
 5. Create a self-signed code signing certificate and export PFX/CER
 6. Sign MSIX with signtool.exe
 7. Install certificate to TrustedPeople (LocalMachine)
 8. Install MSIX package (Add-AppxPackage)
 9. Create firewall rules (HTTP and UDP)

Usage:
  powershell -ExecutionPolicy Bypass -File .\scripts\build-and-install-msix.ps1
#>

param(
	[string]$Configuration = 'Release',
	[string]$Runtime = 'win-x64',
	[string]$Project = 'VardyParty.LocalService/VardyParty.LocalService.csproj',
	[string]$PfxPath = 'C:\Users\jonbr\source\repos\VardyParty-Client\certs\vardyparty.pfx',
	[string]$PfxPassword = '',
	[string]$CerPath = ''
)

Write-Host "Building MSIX installer locally (will use existing PFX/CER; no self-signed certs). You can pass -CerPath to point to ..\VardyParty\vardyparty.cer" -ForegroundColor Cyan

$repoRoot = (Get-Location).Path
$publishRoot = Join-Path $repoRoot 'artifacts\publish\win-x64'
$layoutRoot = Join-Path $repoRoot 'artifacts\msix-layout'
$outputDir = Join-Path $repoRoot 'artifacts\installers'
$signDir = Join-Path $repoRoot 'artifacts\signing'
$manifestSource = Join-Path $repoRoot 'packaging\windows\VardyParty.LocalService.Package\Package.appxmanifest'

# 1. Resolve version from csproj
$projectPath = Join-Path $repoRoot $Project
[xml]$proj = Get-Content -Path $projectPath
$rawVersion = $proj.Project.PropertyGroup.Version | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($rawVersion)) { $rawVersion = $proj.Project.PropertyGroup.VersionPrefix | Select-Object -First 1 }
if ([string]::IsNullOrWhiteSpace($rawVersion)) { $rawVersion = '1.0.0.0' }
$numericVersion = ($rawVersion -split '[-+]')[0]
$parts = $numericVersion.Split('.')
[int]$major = if ($parts.Count -ge 1) { $parts[0] } else { 1 }
[int]$minor = if ($parts.Count -ge 2) { $parts[1] } else { 0 }
[int]$build = if ($parts.Count -ge 3) { $parts[2] } else { 0 }
[int]$revision = if ($parts.Count -ge 4) { $parts[3] } else { 0 }
$msixVersion = "$major.$minor.$build.$revision"
Write-Host "MSIX version: $msixVersion" -ForegroundColor Green

# 2. Publish (Auth0 injected into appsettings for the pack, then restored)
Write-Host "Publishing project to: $publishRoot" -ForegroundColor Cyan
. (Join-Path $repoRoot 'scripts\import-auth0-env.ps1')
Import-VpAuth0Env -RepoRoot $repoRoot -ProjectPath $projectPath -AllowUserSecrets -RequireDomainAudience
Invoke-VpWithAuth0InjectedAppsettings -RepoRoot $repoRoot -BackupPrefix 'vp-msix-appsettings' -Action {
	dotnet publish $projectPath -c $Configuration -r $Runtime --self-contained true -o $publishRoot
	if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
}

# 3. Prepare layout
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $layoutRoot
New-Item -ItemType Directory -Force -Path $layoutRoot | Out-Null
$appRoot = Join-Path $layoutRoot 'VardyParty.LocalService'
New-Item -ItemType Directory -Force -Path $appRoot | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $layoutRoot 'Assets') | Out-Null
Copy-Item -Path (Join-Path $publishRoot '*') -Destination $appRoot -Recurse -Force
Copy-Item -Path (Join-Path $repoRoot 'packaging\windows\VardyParty.LocalService.Package\Assets\*') -Destination (Join-Path $layoutRoot 'Assets') -Recurse -Force

[xml]$manifest = Get-Content -Path $manifestSource
$manifest.Package.Identity.Version = $msixVersion
$manifest.Save((Join-Path $layoutRoot 'AppxManifest.xml'))
Write-Host "Prepared MSIX layout at $layoutRoot" -ForegroundColor Green

# 4. Pack MSIX using makeappx.exe
$makeappx = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
	Where-Object { $_.FullName -match '\\x64\\makeappx\.exe$' } | Sort-Object FullName -Descending | Select-Object -First 1
if ($null -eq $makeappx) { throw 'makeappx.exe not found. Install Windows SDK.' }
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$msixPath = Join-Path $outputDir "VardyParty.LocalService-win-x64.msix"
& $makeappx.FullName pack /d $layoutRoot /p $msixPath /o
if ($LASTEXITCODE -ne 0) { throw 'makeappx pack failed' }
Write-Host "Packed MSIX: $msixPath" -ForegroundColor Green

# Build the elevated bootstrapper and include it in the layout so users can run an elevated installer that also applies firewall rules
$bootstrapperProject = Join-Path $repoRoot 'packaging\windows\InstallerBootstrapper\InstallerBootstrapper.csproj'
$bootstrapperOut = Join-Path $repoRoot 'artifacts\bootstrapper\win-x64'
Write-Host "Building InstallerBootstrapper..." -ForegroundColor Cyan
dotnet publish $bootstrapperProject -c $Configuration -r win-x64 -o $bootstrapperOut
if ($LASTEXITCODE -ne 0) { throw 'bootstrapper publish failed' }
$bootstrapperExe = Join-Path $bootstrapperOut 'InstallerBootstrapper.exe'
if (-not (Test-Path $bootstrapperExe)) { throw 'bootstrapper exe not found after publish' }
Copy-Item -Path $bootstrapperExe -Destination (Join-Path $layoutRoot 'InstallerBootstrapper.exe') -Force
Write-Host "Included InstallerBootstrapper.exe in MSIX layout" -ForegroundColor Green

# also sign the bootstrapper with the PFX if available
if (Test-Path $pfxPath) {
    $signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } | Sort-Object FullName -Descending | Select-Object -First 1
    if ($signtool) {
        Write-Host "Signing InstallerBootstrapper.exe using PFX: $pfxPath" -ForegroundColor Cyan
        & $signtool.FullName sign /fd SHA256 /f $pfxPath /p $pwd (Join-Path $layoutRoot 'InstallerBootstrapper.exe')
        if ($LASTEXITCODE -ne 0) { Write-Host 'Warning: signing bootstrapper failed' -ForegroundColor Yellow }
    }
}

# 5. Use existing signing artifacts (no self-signed certs)
New-Item -ItemType Directory -Force -Path $signDir | Out-Null
$pfxPath = Join-Path $signDir 'vardyparty.pfx'
$cerPath = Join-Path $outputDir 'vardyparty.cer'
$pwd = $PfxPassword

# If user provided a PFX, prefer that
if (-not [string]::IsNullOrWhiteSpace($PfxPath) -and (Test-Path $PfxPath)) {
    Write-Host "Using provided PFX: $PfxPath" -ForegroundColor Green
    $pfxPath = $PfxPath
    if ([string]::IsNullOrWhiteSpace($pwd)) { Write-Host "Warning: PFX provided without password parameter; supply -PfxPassword if required." -ForegroundColor Yellow }
    # if a .cer next to the provided pfx exists, copy it to artifacts
    $providedCer = [System.IO.Path]::ChangeExtension($pfxPath, '.cer')
    if (Test-Path $providedCer) { Copy-Item -Path $providedCer -Destination $cerPath -Force }
}

# If a PFX exists in artifacts/signing, use it
if (Test-Path $pfxPath) {
    Write-Host "Found existing PFX at $pfxPath; will use it to sign MSIX" -ForegroundColor Green
    if ([string]::IsNullOrWhiteSpace($pwd)) { Write-Host "No PfxPassword provided; supply -PfxPassword if the PFX is protected." -ForegroundColor Yellow }
}

# If no PFX, try to use CerPath param or find an existing .cer in common locations and use it for trust (skip signing)
if (-not (Test-Path $pfxPath)) {
    $foundCer = $null

    if (-not [string]::IsNullOrWhiteSpace($CerPath) -and (Test-Path $CerPath)) {
        $foundCer = Get-Item -Path $CerPath
    }

    if (-not $foundCer) {
        # Prefer the user's VardyParty-Client certs folder (sibling repo)
        $locVardyPartyClient = 'C:\Users\jonbr\source\repos\VardyParty-Client\certs'
        $locVardyParty = Join-Path $repoRoot 'VardyParty'
        $locArtifacts = Join-Path $repoRoot 'artifacts\installers'
        $locPackaging = Join-Path $repoRoot 'packaging\windows\VardyParty.LocalService.Package'
        $searchLocations = @($locVardyPartyClient, $locVardyParty, $locArtifacts, $locPackaging, $repoRoot)

        foreach ($loc in $searchLocations) {
            if (Test-Path $loc) {
                $foundCer = Get-ChildItem -Path $loc -Recurse -Filter '*.cer' -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -like '*vardyparty*' } | Select-Object -First 1
                if ($foundCer) { break }
            }
        }

        if (-not $foundCer) {
            foreach ($loc in $searchLocations) {
                if (Test-Path $loc) {
                    $foundCer = Get-ChildItem -Path $loc -Recurse -Filter '*.cer' -ErrorAction SilentlyContinue | Select-Object -First 1
                    if ($foundCer) { break }
                }
            }
        }
    }

    if ($foundCer) {
        Write-Host "Found existing CER: $($foundCer.FullName). Will import to TrustedPeople for install; MSIX will remain unsigned locally." -ForegroundColor Green
        Copy-Item -Path $foundCer.FullName -Destination $cerPath -Force
    } elseif (Test-Path $cerPath) {
        Write-Host "Found CER at expected output path: $cerPath; will import to TrustedPeople." -ForegroundColor Green
    } else {
        Write-Host "No PFX or CER found in expected locations. Skipping signing and cert import. If you have a .cer, place it in VardyParty-Client\certs, VardyParty, or artifacts/installers." -ForegroundColor Yellow
    }
}

# 6. Sign MSIX with signtool if PFX available
if (Test-Path $pfxPath) {
	$signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
		Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } | Sort-Object FullName -Descending | Select-Object -First 1
	if ($null -eq $signtool) { throw 'signtool.exe not found. Install Windows SDK.' }

	Write-Host "Signing MSIX using PFX: $pfxPath" -ForegroundColor Cyan
	& $signtool.FullName sign /fd SHA256 /f $pfxPath /p $pwd $msixPath
	if ($LASTEXITCODE -ne 0) { throw 'signtool sign failed' }
	Write-Host "Signed MSIX: $msixPath" -ForegroundColor Green

	# 7. Trust certificate (install to LocalMachine TrustedPeople)
	if (Test-Path $cerPath) {
		Write-Host "Installing public cert to LocalMachine\TrustedPeople (requires elevation)" -ForegroundColor Cyan
		Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
	} else {
		Write-Host "Public CER not found beside PFX. Attempting to export from PFX." -ForegroundColor Yellow
		try {
			$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfxPath, $pwd)
			[System.IO.File]::WriteAllBytes($cerPath, $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
			Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
		} catch {
			Write-Host "Failed to extract .cer from PFX: $_" -ForegroundColor Red
		}
	}
} else {
	Write-Host "No PFX available. If you have a .cer, place it at $cerPath before running to trust the publisher." -ForegroundColor Yellow
}

# 8. Install the MSIX package using the elevated bootstrapper (preferred) if available
$embeddedBootstrapper = Join-Path $layoutRoot 'InstallerBootstrapper.exe'
if (Test-Path $embeddedBootstrapper) {
    Write-Host "Running elevated InstallerBootstrapper to install MSIX and apply firewall rules..." -ForegroundColor Cyan
    $proc = Start-Process -FilePath $embeddedBootstrapper -ArgumentList @("`"$msixPath`"") -Verb RunAs -PassThru
    $proc.WaitForExit()
    if ($proc.ExitCode -ne 0) { throw "InstallerBootstrapper failed with exit code $($proc.ExitCode)" }
    Write-Host "Bootstrapper install completed." -ForegroundColor Green
} else {
    Write-Host "InstallerBootstrapper not present; falling back to direct Add-AppxPackage + netsh steps (requires elevation)." -ForegroundColor Yellow
    Add-AppxPackage -Path $msixPath -ForceApplicationShutdown -ForceUpdateFromAnyVersion
    Write-Host "Installed package." -ForegroundColor Green
    Write-Host "Creating firewall rules for HTTP and UDP discovery..." -ForegroundColor Cyan
    netsh advfirewall firewall delete rule name="VardyParty.LocalService HTTP" 2>$null | Out-Null
    netsh advfirewall firewall delete rule name="VardyParty.LocalService UDP Discovery" 2>$null | Out-Null
    netsh advfirewall firewall add rule name="VardyParty.LocalService HTTP" dir=in action=allow protocol=tcp localport=5019-5573 profile=private,public enable=yes description="VardyParty.LocalService HTTP"
    netsh advfirewall firewall add rule name="VardyParty.LocalService UDP Discovery" dir=in action=allow protocol=udp localport=40129-40189 profile=private,public enable=yes description="VardyParty.LocalService UDP Discovery"
    Write-Host "Firewall rules created." -ForegroundColor Green
}

Write-Host "MSIX build and install complete." -ForegroundColor Cyan
Write-Host "To uninstall, run scripts/uninstall-msix-local.ps1" -ForegroundColor Cyan
