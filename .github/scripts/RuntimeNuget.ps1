# Runtime NuGet contract between public local-service and private M3U8-resolver.
#
# local-service publishes one nupkg per RID:
#   VardyParty.LocalService.Runtime.<rid>
# The nupkg is the self-contained host. Strategy DLLs are plugins and are not
# referenced or packed by this repo. M3U8-resolver copies them in from the
# Strategies feed when it builds the release.

Set-StrictMode -Version Latest

function Get-StrategyPluginPackages {
    param([Parameter(Mandatory)][string]$DepsPath)

    $deps = Get-Content -LiteralPath $DepsPath -Raw | ConvertFrom-Json
    $packages = @()
    foreach ($entry in $deps.libraries.PSObject.Properties) {
        if ($entry.Value.type -ne 'package') { continue }
        $slash = $entry.Name.LastIndexOf('/')
        if ($slash -lt 1) { continue }
        $id = $entry.Name.Substring(0, $slash)
        $version = $entry.Name.Substring($slash + 1)
        if ($id -notlike 'VardyParty.LocalService.*') { continue }
        $packages += [pscustomobject]@{
            Id      = $id
            Version = $version
            Key     = $entry.Name
        }
    }
    return @($packages)
}

function Get-PluginAssemblyNames {
    param(
        [Parameter(Mandatory)]$Deps,
        [Parameter(Mandatory)][string[]]$PackageKeys
    )

    $names = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($targetEntry in $Deps.targets.PSObject.Properties) {
        $target = $targetEntry.Value
        foreach ($key in $PackageKeys) {
            $pkgProp = $target.PSObject.Properties[$key]
            if (-not $pkgProp) { continue }
            foreach ($section in @('runtime', 'native')) {
                $sectionNode = $pkgProp.Value.PSObject.Properties[$section]
                if (-not $sectionNode) { continue }
                foreach ($asset in $sectionNode.Value.PSObject.Properties.Name) {
                    [void]$names.Add([IO.Path]::GetFileName($asset))
                }
            }
        }
    }
    return @($names)
}

function Remove-StrategyPluginAssemblies {
    param([Parameter(Mandatory)][string]$PublishDir)

    $depsPath = Join-Path $PublishDir 'VardyParty.LocalService.deps.json'
    if (-not (Test-Path -LiteralPath $depsPath)) {
        throw "deps.json not found in $PublishDir"
    }

    $deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
    $packages = @(Get-StrategyPluginPackages -DepsPath $depsPath)
    $names = @(Get-PluginAssemblyNames -Deps $deps -PackageKeys @($packages.Key))
    $removed = @()
    foreach ($name in $names) {
        $matches = @(Get-ChildItem -LiteralPath $PublishDir -Recurse -File -Filter $name)
        foreach ($file in $matches) {
            Remove-Item -LiteralPath $file.FullName -Force
            $removed += $file.Name
            foreach ($extraExt in @('.pdb', '.xml')) {
                $extra = [IO.Path]::ChangeExtension($file.FullName, $extraExt)
                if (Test-Path -LiteralPath $extra) {
                    Remove-Item -LiteralPath $extra -Force
                }
            }
        }
    }
    return @($removed)
}

function Assert-PublishHasNoStrategyPlugins {
    param([Parameter(Mandatory)][string]$PublishDir)

    $depsPath = Join-Path $PublishDir 'VardyParty.LocalService.deps.json'
    $deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
    $packages = @(Get-StrategyPluginPackages -DepsPath $depsPath)
    if ($packages.Count -eq 0) {
        return
    }
    $names = @(Get-PluginAssemblyNames -Deps $deps -PackageKeys @($packages.Key))
    $left = @()
    foreach ($name in $names) {
        $left += @(Get-ChildItem -LiteralPath $PublishDir -Recurse -File -Filter $name)
    }
    if ($left.Count -gt 0) {
        $list = ($left | ForEach-Object { $_.FullName }) -join ', '
        throw "Strategy plugin assemblies are still in the publish output: $list"
    }
}

function New-RuntimeNupkg {
    param(
        [Parameter(Mandatory)][string]$PublishDir,
        [Parameter(Mandatory)][string]$PackageId,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$OutputPath
    )

    Assert-PublishHasNoStrategyPlugins -PublishDir $PublishDir
    $outDir = Split-Path -Parent $OutputPath
    if ($outDir) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
    if (Test-Path -LiteralPath $OutputPath) { Remove-Item -LiteralPath $OutputPath -Force }

    $nuspec = @"
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd">
  <metadata>
    <id>$PackageId</id>
    <version>$Version</version>
    <authors>VardyParty</authors>
    <description>Self-contained VardyParty Local Service host for one RID. Strategy plugin assemblies are not included.</description>
    <repository type="git" url="https://github.com/Vardy-Party/local-service" />
  </metadata>
</package>
"@
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::Open($OutputPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $nuspecEntry = $zip.CreateEntry("$PackageId.nuspec", [IO.Compression.CompressionLevel]::Optimal)
        $writer = New-Object IO.StreamWriter($nuspecEntry.Open(), [Text.UTF8Encoding]::new($false))
        try { $writer.Write($nuspec) } finally { $writer.Dispose() }

        $root = (Resolve-Path -LiteralPath $PublishDir).Path.TrimEnd('\', '/')
        foreach ($file in Get-ChildItem -LiteralPath $PublishDir -Recurse -File) {
            $relative = $file.FullName.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
            $entry = $zip.CreateEntry("tools/publish/$relative", [IO.Compression.CompressionLevel]::Optimal)
            $input = [IO.File]::OpenRead($file.FullName)
            try {
                $output = $entry.Open()
                try { $input.CopyTo($output) } finally { $output.Dispose() }
            }
            finally { $input.Dispose() }
        }
    }
    finally { $zip.Dispose() }
}

function Expand-RuntimeNupkg {
    param(
        [Parameter(Mandatory)][string]$Nupkg,
        [Parameter(Mandatory)][string]$Destination
    )

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Nupkg)
    try {
        $prefix = 'tools/publish/'
        $found = $false
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if (-not $name.StartsWith($prefix) -or $name.EndsWith('/')) { continue }
            $found = $true
            $relative = $name.Substring($prefix.Length)
            $target = Join-Path $Destination ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
            $parent = Split-Path -Parent $target
            if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
            $stream = $entry.Open()
            try {
                $file = [IO.File]::Create($target)
                try { $stream.CopyTo($file) } finally { $file.Dispose() }
            }
            finally { $stream.Dispose() }
        }
        if (-not $found) { throw "Nupkg has no tools/publish payload: $Nupkg" }
    }
    finally { $zip.Dispose() }
}

function Get-NupkgLibAssemblyNames {
    param([Parameter(Mandatory)][string]$Nupkg)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Nupkg)
    try {
        $preferred = @($zip.Entries | Where-Object {
            $name = $_.FullName.Replace('\', '/')
            $name -like 'lib/net11.0/*.dll'
        } | ForEach-Object { [IO.Path]::GetFileName($_.FullName) })
        if ($preferred.Count -gt 0) { return @($preferred) }
        return @($zip.Entries | Where-Object {
            $name = $_.FullName.Replace('\', '/')
            $name -like 'lib/*/*.dll'
        } | ForEach-Object { [IO.Path]::GetFileName($_.FullName) })
    }
    finally { $zip.Dispose() }
}

function Add-PluginAssemblies {
    param(
        [Parameter(Mandatory)][string]$PublishDir,
        [Parameter(Mandatory)][string]$PluginNupkg,
        [Parameter(Mandatory)][string[]]$AssemblyNames
    )

    $wanted = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $AssemblyNames) { [void]$wanted.Add($name) }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($PluginNupkg)
    try {
        foreach ($entry in $zip.Entries) {
            $leaf = [IO.Path]::GetFileName($entry.FullName)
            if (-not $wanted.Contains($leaf)) { continue }
            $target = Join-Path $PublishDir $leaf
            $stream = $entry.Open()
            try {
                $file = [IO.File]::Create($target)
                try { $stream.CopyTo($file) } finally { $file.Dispose() }
            }
            finally { $stream.Dispose() }
        }
    }
    finally { $zip.Dispose() }

    $missing = @()
    foreach ($name in $AssemblyNames) {
        if (-not (Test-Path -LiteralPath (Join-Path $PublishDir $name))) { $missing += $name }
    }
    if ($missing.Count -gt 0) {
        throw "Strategy nupkg is missing assemblies: $($missing -join ', ')"
    }
}

function Get-GitHubNuGetPackageUrl {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Version,
        [string]$Owner = 'Vardy-Party'
    )
    $idLower = $Id.ToLowerInvariant()
    $versionLower = $Version.ToLowerInvariant()
    return "https://nuget.pkg.github.com/$Owner/download/$idLower/$versionLower/$idLower.$versionLower.nupkg"
}

function Save-GitHubNuGetPackage {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$OutFile,
        [string]$Token,
        [string]$Owner = 'Vardy-Party'
    )
    $parent = Split-Path -Parent $OutFile
    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    $headers = @{ Accept = 'application/octet-stream'; 'X-GitHub-Api-Version' = '2022-11-28' }
    if (-not [string]::IsNullOrWhiteSpace($Token)) {
        $basic = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("x-access-token:$Token"))
        $headers.Authorization = "Basic $basic"
    }
    Invoke-WebRequest -Uri (Get-GitHubNuGetPackageUrl -Id $Id -Version $Version -Owner $Owner) -Headers $headers -OutFile $OutFile
}

function Assert-LocalServiceDispatch {
    param([string]$RunUrl, [string]$Sha)
    if ([string]::IsNullOrWhiteSpace($RunUrl) -and [string]::IsNullOrWhiteSpace($Sha)) { return $false }
    if ($RunUrl -notmatch '^https://github\.com/Vardy-Party/local-service/actions/runs/\d+$') {
        throw "run_url must be a Vardy-Party/local-service Actions run URL."
    }
    if ($Sha -notmatch '^[0-9a-fA-F]{40}$') {
        throw "source_sha must be a 40-character hex SHA."
    }
    return $true
}
