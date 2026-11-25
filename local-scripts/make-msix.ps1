param(
    [string]$CertPath = (Join-Path $PSScriptRoot "..\..\VardyParty-Client\certs\vardyparty.pfx"),
    [string]$CertPasswordEnv = "MSIX_CERT_PASSWORD"
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")

# Resolve cert password
$certPassword = (Get-Item -Path "Env:\$CertPasswordEnv" -ErrorAction SilentlyContinue).Value
if (-not $certPassword) {
    $securePass = Read-Host "Certificate password" -AsSecureString
    $certPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePass)
    )
}

$CertPath = Resolve-Path $CertPath

# Resolve version from csproj
$csprojPath = Join-Path $repoRoot "VardyParty.LocalService\VardyParty.LocalService.csproj"
[xml]$csproj = Get-Content $csprojPath
$rawVersion = $csproj.Project.PropertyGroup.Version | Select-Object -First 1
$parts = $rawVersion -split '\.'
while ($parts.Count -lt 4) { $parts += "0" }
$msixVersion = ($parts[0..3] | ForEach-Object { [int]$_ }) -join "."
Write-Host "MSIX version: $msixVersion"

# Paths
$publishRoot   = Join-Path $repoRoot "artifacts\publish\win-x64"
$layoutRoot    = Join-Path $repoRoot "artifacts\msix-layout"
$appRoot       = Join-Path $layoutRoot "VardyParty.LocalService"
$assetsSource  = Join-Path $repoRoot "packaging\windows\VardyParty.LocalService.Package\Assets"
$manifestSource = Join-Path $repoRoot "packaging\windows\VardyParty.LocalService.Package\Package.appxmanifest"
$outputDir     = Join-Path $repoRoot "artifacts\installers"
$msixPath      = Join-Path $outputDir "VardyParty.LocalService-win-x64.msix"

# Step 1: dotnet publish
Write-Host "Publishing win-x64..."
dotnet publish "$csprojPath" -c Release -r win-x64 --self-contained true -o $publishRoot
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish failed"; exit 1 }

# Step 2: Build MSIX layout
Write-Host "Building MSIX layout..."
Remove-Item -Path $layoutRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $appRoot | Out-Null
New-Item -ItemType Directory -Force -Path "$layoutRoot\Assets" | Out-Null

Copy-Item -Path "$publishRoot\*" -Destination $appRoot -Recurse -Force
Copy-Item -Path "$assetsSource\*" -Destination "$layoutRoot\Assets" -Recurse -Force

[xml]$manifest = Get-Content $manifestSource
$manifest.Package.Identity.Version = $msixVersion
$manifest.Save("$layoutRoot\AppxManifest.xml")
Write-Host "AppxManifest version stamped to $msixVersion"

# Step 3: makeappx pack
Write-Host "Packing MSIX..."
$makeappx = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\makeappx\.exe$' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1

if (-not $makeappx) { Write-Error "makeappx.exe not found. Install Windows 10/11 SDK."; exit 1 }

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
& $makeappx.FullName pack /d $layoutRoot /p $msixPath /o
if ($LASTEXITCODE -ne 0) { Write-Error "makeappx failed"; exit 1 }

# Step 4: signtool sign
Write-Host "Signing MSIX..."
$signtool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1

if (-not $signtool) { Write-Error "signtool.exe not found. Install Windows 10/11 SDK."; exit 1 }

& $signtool.FullName sign /fd SHA256 /f $CertPath /p $certPassword $msixPath
if ($LASTEXITCODE -ne 0) { Write-Error "Signing failed"; exit 1 }

Write-Host "Done: $msixPath"
