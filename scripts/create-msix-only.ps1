<#
Create only the MSIX package and sign it if a PFX is provided.
Run from the repo root. Elevation is not required for packing or signing, but importing the CER to TrustedPeople requires elevation (not done here).

Usage:
  powershell -ExecutionPolicy Bypass -File .\scripts\create-msix-only.ps1 [-Configuration Release] [-Runtime win-x64] [-PfxPath <path>] [-PfxPassword <password>] [-CerPath <path>]

Outputs:
  artifacts/installers/VardyParty.LocalService-win-x64.msix
  artifacts/installers/vardyparty.cer (if PFX present or found)
#>

param(
	[string]$Configuration = 'Release',
	[string]$Runtime = 'win-x64',
	[string]$Project = 'VardyParty.LocalService/VardyParty.LocalService.csproj',
	[string]$PfxPath = 'C:\Users\jonbr\source\repos\VardyParty-Client\certs\vardyparty.pfx',
	[string]$PfxPassword = '',
	[string]$CerPath = ''
)

$repoRoot = (Get-Location).Path
$publishRoot = Join-Path $repoRoot 'artifacts\publish\win-x64'
$layoutRoot = Join-Path $repoRoot 'artifacts\msix-layout'
$outputDir = Join-Path $repoRoot 'artifacts\installers'
$signDir = Join-Path $repoRoot 'artifacts\signing'
$manifestSource = Join-Path $repoRoot 'packaging\windows\VardyParty.LocalService.Package\Package.appxmanifest'

Write-Host "Creating MSIX layout and package (no install)..." -ForegroundColor Cyan

# 1. Resolve version
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

# 2. Publish
Write-Host "Publishing project to: $publishRoot" -ForegroundColor Cyan
dotnet publish $projectPath -c $Configuration -r $Runtime --self-contained true -o $publishRoot
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

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

# 5. Signing: use provided PFX if present; otherwise try to locate a CER for trust (no install)
New-Item -ItemType Directory -Force -Path $signDir | Out-Null
$defaultPfx = $PfxPath
$cerOut = Join-Path $outputDir 'vardyparty.cer'

if (Test-Path $defaultPfx) {
	if ([string]::IsNullOrWhiteSpace($PfxPassword)) {
		# prompt for password securely
		$secure = Read-Host -AsSecureString "PFX password for $defaultPfx"
		$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
		$plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
		[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
		$PfxPassword = $plain
	}

	$signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
		Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } | Sort-Object FullName -Descending | Select-Object -First 1
	if ($null -eq $signtool) { throw 'signtool.exe not found. Install Windows SDK.' }

	Write-Host "Signing MSIX using PFX: $defaultPfx" -ForegroundColor Cyan
	& $signtool.FullName sign /fd SHA256 /f $defaultPfx /p $PfxPassword $msixPath
	if ($LASTEXITCODE -ne 0) { throw 'signtool sign failed' }

	# export public cert to artifacts
	try {
		$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($defaultPfx, $PfxPassword)
		[System.IO.File]::WriteAllBytes($cerOut, $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
		Write-Host "Exported public CER to $cerOut" -ForegroundColor Green
	} catch {
		Write-Host "Failed to export .cer from PFX: $_" -ForegroundColor Yellow
	}
} else {
	# try to locate existing .cer in sibling repo or packaging
	$locVPC = 'C:\Users\jonbr\source\repos\VardyParty-Client\certs'
	$locPackaging = Join-Path $repoRoot 'packaging\windows\VardyParty.LocalService.Package'
	$locRepoRoot = $repoRoot
	$searchLocations = @($locVPC, $locPackaging, $locRepoRoot)
	$foundCer = $null
	foreach ($loc in $searchLocations) {
		if (Test-Path $loc) {
			$foundCer = Get-ChildItem -Path $loc -Recurse -Filter '*.cer' -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*vardyparty*' } | Select-Object -First 1
			if ($foundCer) { break }
		}
	}
	if ($foundCer) {
		Copy-Item -Path $foundCer.FullName -Destination $cerOut -Force
		Write-Host "Copied existing CER to $cerOut (MSIX remains unsigned)" -ForegroundColor Yellow
	} else {
		Write-Host "No PFX or CER found. MSIX was created unsigned at $msixPath" -ForegroundColor Yellow
	}
}

Write-Host "MSIX package created: $msixPath" -ForegroundColor Green
if (Test-Path $cerOut) { Write-Host "Public certificate available: $cerOut" -ForegroundColor Green }
