#Requires -Version 7.0
<#
.SYNOPSIS
  Download a public Local Service runtime nupkg and copy private Strategy plugin DLLs into it.

.DESCRIPTION
  Used by the private M3U8-resolver release workflow. The runtime nupkg is published
  by Vardy-Party/local-service and does not contain Strategy assemblies. Those DLLs
  are restored from the private GitHub Packages feed using NUGET_TOKEN.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Rid,
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..' '.github' 'scripts' 'RuntimeNuget.ps1')

if ([string]::IsNullOrWhiteSpace($env:NUGET_TOKEN)) {
    throw 'NUGET_TOKEN is required (read:packages on Vardy-Party).'
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("runtime-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
try {
    $packageId = "VardyParty.LocalService.Runtime.$Rid"
    $runtimeNupkg = Join-Path $work "$packageId.$Version.nupkg"
    Save-GitHubNuGetPackage -Id $packageId -Version $Version -OutFile $runtimeNupkg -Token $env:NUGET_TOKEN
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Recurse -Force }
    Expand-RuntimeNupkg -Nupkg $runtimeNupkg -Destination $Destination

    $manifestPath = Join-Path $PSScriptRoot '..' 'packaging' 'strategy-plugins.json'
    $plugins = @(Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json)
    if ($plugins.Count -eq 0) { throw "No strategy plugins listed in $manifestPath." }

    foreach ($plugin in $plugins) {
        $pluginNupkg = Join-Path $work "$($plugin.id).$($plugin.version).nupkg"
        Save-GitHubNuGetPackage -Id $plugin.id -Version $plugin.version -OutFile $pluginNupkg -Token $env:NUGET_TOKEN
        $names = @(Get-NupkgLibAssemblyNames -Nupkg $pluginNupkg)
        if ($names.Count -eq 0) { throw "No lib assemblies in $($plugin.id)." }
        Add-PluginAssemblies -PublishDir $Destination -PluginNupkg $pluginNupkg -AssemblyNames $names
        Write-Host "Restored $($plugin.id) $($plugin.version): $($names -join ', ')"
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
