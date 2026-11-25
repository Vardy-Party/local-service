#Requires -Version 7.0
<#
.SYNOPSIS
  Deprecated alias — forwards to build-local-release.ps1.

.DESCRIPTION
  Prefer: pwsh ./scripts/build-local-release.ps1
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Remaining
)

$ErrorActionPreference = 'Stop'
Write-Host 'build-release-msix-pair.ps1 is deprecated; forwarding to build-local-release.ps1' -ForegroundColor Yellow
& (Join-Path $PSScriptRoot 'build-local-release.ps1') @Remaining
exit $LASTEXITCODE
