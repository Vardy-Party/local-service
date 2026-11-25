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

    $depsPath = Join-Path $Destination 'VardyParty.LocalService.deps.json'
    $deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
    $plugins = @(Get-StrategyPluginPackages -DepsPath $depsPath)
    if ($plugins.Count -eq 0) { throw "Runtime nupkg deps.json has no strategy packages." }

    foreach ($plugin in $plugins) {
        $pluginNupkg = Join-Path $work "$($plugin.Id).$($plugin.Version).nupkg"
        Save-GitHubNuGetPackage -Id $plugin.Id -Version $plugin.Version -OutFile $pluginNupkg -Token $env:NUGET_TOKEN
        $names = @(Get-PluginAssemblyNames -Deps $deps -PackageKeys @($plugin.Key))
        if ($names.Count -eq 0) { throw "No runtime assemblies listed for $($plugin.Id)." }
        Add-PluginAssemblies -PublishDir $Destination -PluginNupkg $pluginNupkg -AssemblyNames $names
        Write-Host "Restored $($plugin.Id) $($plugin.Version): $($names -join ', ')"
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
