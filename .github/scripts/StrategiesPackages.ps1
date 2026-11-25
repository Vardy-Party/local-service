# Shared logic for the "Update Strategies packages" workflow.
# Pure functions (validation, version maths, branch choice, csproj rewrite)
# are unit-tested in StrategiesPackages.Tests.ps1 and run in ci.yml on
# Linux, Windows and macOS. Only Get-FeedPackageVersions talks to the feed.

Set-StrictMode -Version Latest

$script:FeedResources = $null

$script:StrategiesPackageIds = @(
    'VardyParty.LocalService.Abstractions'
    'VardyParty.LocalService.V1'
    'VardyParty.LocalService.V2'
    'VardyParty.LocalService.V1.Scrape'
    'VardyParty.LocalService.V2.Scrape'
)

# Every token that stops GitHub from building a commit, plus the checks
# trailer. Matched case-insensitively; see README "Skip-CI tokens".
$script:SkipCiTokens = @('[skip ci]', '[ci skip]', '[no ci]', '[skip actions]', '[actions skip]')

function Get-StrategiesPackageIds { $script:StrategiesPackageIds }

function ConvertTo-StableVersion([string]$Version) {
    if ($Version -match '^(\d+)\.(\d+)\.(\d+)$') { return [version]$Version }
    return $null
}

# Throws if Text contains a skip-CI token (any case) or a skip-checks trailer.
# Run on every bot commit message, PR title and PR body before they are used.
function Assert-NoSkipCiToken([string]$Text, [string]$What = 'text') {
    foreach ($token in $script:SkipCiTokens) {
        if ($Text.IndexOf($token, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Refusing to use $What containing skip-CI token '$token': CI would never build it."
        }
    }
    if ($Text -match '(?im)^\s*skip-checks\s*:\s*true\s*$') {
        throw "Refusing to use $What containing a 'skip-checks: true' trailer."
    }
}

# Validates repository_dispatch provenance. Both empty = manual run ($false).
# Both valid = dispatched by a Strategies run ($true). Anything else throws,
# so arbitrary payload text never reaches a commit message or PR body.
function Assert-DispatchSource([string]$RunUrl, [string]$SourceSha) {
    $hasUrl = -not [string]::IsNullOrEmpty($RunUrl)
    $hasSha = -not [string]::IsNullOrEmpty($SourceSha)
    if (-not $hasUrl -and -not $hasSha) { return $false }
    if ($RunUrl -cnotmatch '^https://github\.com/Vardy-Party/Strategies/actions/runs/\d+$') {
        throw "client_payload.run_url is not a Vardy-Party/Strategies Actions run URL."
    }
    if ($SourceSha -cnotmatch '^[0-9a-f]{40}$') {
        throw "client_payload.source_sha is not a 40-character lowercase hex commit SHA."
    }
    return $true
}

# Minimum version per id for ALL Strategies packages. The payload (or manual
# input) only raises minimums to wait for; every id is still resolved to its
# highest feed version, so a coalesced or cancelled run never loses a bump.
function Get-RequestedVersions([string]$PayloadJson, [string]$InputJson) {
    $result = [ordered]@{}
    foreach ($id in $script:StrategiesPackageIds) { $result[$id] = '0.0.0' }
    foreach ($json in @($PayloadJson, $InputJson)) {
        if ([string]::IsNullOrWhiteSpace($json) -or $json.Trim() -eq 'null') { continue }
        $map = $json | ConvertFrom-Json -AsHashtable
        if ($null -eq $map -or $map.Count -eq 0) { continue }
        foreach ($id in @($map.Keys | Sort-Object)) {
            if ($id -notin $script:StrategiesPackageIds) { throw "Unknown Strategies package id '$id'." }
            if ($null -eq (ConvertTo-StableVersion ([string]$map[$id]))) { throw "Version for $id must be MAJOR.MINOR.PATCH (got '$($map[$id])')." }
            $result[$id] = [string]$map[$id]
        }
        break
    }
    return $result
}

# Highest stable feed version per id. Throws if the feed does not (yet) have
# the requested minimum, so a pin is always a version that is really published.
function Select-TargetVersions([System.Collections.IDictionary]$Requested, [System.Collections.IDictionary]$FeedVersions) {
    $targets = [ordered]@{}
    foreach ($id in $Requested.Keys) {
        $stable = @($FeedVersions[$id] | Where-Object { ConvertTo-StableVersion $_ } | Sort-Object { ConvertTo-StableVersion $_ })
        if ($stable.Count -eq 0) { throw "No stable versions of $id on the feed." }
        $highest = $stable[-1]
        if ((ConvertTo-StableVersion $highest) -lt (ConvertTo-StableVersion $Requested[$id])) {
            throw "$id $($Requested[$id]) is not on the feed yet (highest is $highest)."
        }
        $targets[$id] = $highest
    }
    return $targets
}

# Calls Select-TargetVersions until the feed shows every requested version.
# GitHub Packages can lag a few seconds behind a push.
function Resolve-TargetVersions([System.Collections.IDictionary]$Requested, [scriptblock]$GetVersions, [int]$TimeoutSeconds = 600, [int]$PollSeconds = 20) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        $feed = @{}
        foreach ($id in $Requested.Keys) { $feed[$id] = @(& $GetVersions $id) }
        try { return Select-TargetVersions $Requested $feed }
        catch {
            if ((Get-Date) -ge $deadline) { throw }
            Write-Host "Waiting for the feed: $($_.Exception.Message)"
            Start-Sleep -Seconds $PollSeconds
        }
    }
}

# Which branch this run writes to. Never force-pushes, so an existing branch
# is always reused rather than recreated:
#   open-pr          an open bot PR exists: update its branch
#   existing-branch  this run's branch exists without a PR (re-run after a
#                    failed 'gh pr create'): reuse it and open the PR
#   new              start <prefix><run number> from main
function Select-BotBranch([string]$OpenPrBranch, [string[]]$RemoteBranches, [string]$Prefix, [string]$RunNumber) {
    if (-not [string]::IsNullOrEmpty($OpenPrBranch)) {
        if (-not $OpenPrBranch.StartsWith($Prefix)) { throw "Open PR branch '$OpenPrBranch' does not start with '$Prefix'." }
        return [pscustomobject]@{ Branch = $OpenPrBranch; Mode = 'open-pr' }
    }
    $own = "$Prefix$RunNumber"
    if (@($RemoteBranches) -contains $own) { return [pscustomobject]@{ Branch = $own; Mode = 'existing-branch' } }
    return [pscustomobject]@{ Branch = $own; Mode = 'new' }
}

function Get-FeedPackageVersions([string]$Id, [string]$FeedIndexUrl, [string]$User, [string]$Token) {
    $headers = @{}
    if (-not [string]::IsNullOrEmpty($Token)) {
        $basic = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("${User}:${Token}"))
        $headers['Authorization'] = "Basic $basic"
    }
    if (-not $script:FeedResources) { $script:FeedResources = (Invoke-RestMethod -Uri $FeedIndexUrl -Headers $headers).resources }
    $lower = $Id.ToLowerInvariant()
    $flat = $script:FeedResources | Where-Object { $_.'@type' -like 'PackageBaseAddress/3.0.0*' } | Select-Object -First 1
    if ($flat) {
        try { return @((Invoke-RestMethod -Uri "$($flat.'@id'.TrimEnd('/'))/$lower/index.json" -Headers $headers).versions) }
        catch { if ($_.Exception.Response.StatusCode.value__ -eq 404) { return @() }; throw }
    }
    $reg = $script:FeedResources | Where-Object { $_.'@type' -like 'RegistrationsBaseUrl*' } | Select-Object -First 1
    if (-not $reg) { throw "Feed index has neither PackageBaseAddress nor RegistrationsBaseUrl." }
    try { $index = Invoke-RestMethod -Uri "$($reg.'@id'.TrimEnd('/'))/$lower/index.json" -Headers $headers }
    catch { if ($_.Exception.Response.StatusCode.value__ -eq 404) { return @() }; throw }
    $versions = foreach ($page in $index.items) {
        $items = if ($page.PSObject.Properties['items'] -and $page.items) { $page.items } else { (Invoke-RestMethod -Uri $page.'@id' -Headers $headers).items }
        foreach ($leaf in $items) { $leaf.catalogEntry.version }
    }
    return @($versions)
}

# One <PackageReference ...> start tag. Quoted attribute values may contain
# '>' (e.g. Condition="'$(A)' > '1'"), so they are skipped as a unit.
$script:PackageReferenceTag = '<PackageReference\b(?:"[^"]*"|''[^'']*''|[^''">])*>'

# Raises <PackageReference Include="id" Version="x"> to the target version in
# every csproj under Root. Never lowers a pin; keeps BOM and line endings.
# Returns Changes (raised pins) and Unmovable (references for a Strategies id
# that the bot cannot rewrite: no Version attribute, or a non-MAJOR.MINOR.PATCH
# value such as $(Prop), 0.2.7.0 or 0.2.*). The workflow prints Unmovable as
# ::warning:: so a pin that silently stays behind is visible on the run.
function Update-PackageReferences([string]$Root, [System.Collections.IDictionary]$Targets) {
    $changes = @()
    $unmovable = @()
    $files = Get-ChildItem -Path $Root -Recurse -Filter *.csproj -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj|\.git)[\\/]' } | Sort-Object FullName
    foreach ($file in $files) {
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
        $offset = if ($bom) { 3 } else { 0 }
        $text = [Text.UTF8Encoding]::new($false).GetString($bytes, $offset, $bytes.Length - $offset)
        $updated = $text
        foreach ($id in $Targets.Keys) {
            $target = ConvertTo-StableVersion $Targets[$id]
            $includeAttr = '\sInclude\s*=\s*"' + [regex]::Escape($id) + '"'
            $found = @([regex]::Matches($updated, $script:PackageReferenceTag, [Text.RegularExpressions.RegexOptions]::IgnoreCase) |
                Where-Object { $_.Value -match $includeAttr })
            # Right to left so earlier match indexes stay valid after each splice.
            for ($i = $found.Count - 1; $i -ge 0; $i--) {
                $m = $found[$i]
                $vm = [regex]::Match($m.Value, '\sVersion\s*=\s*"([^"]*)"')
                if (-not $vm.Success) {
                    $unmovable += [pscustomobject]@{ File = $file.FullName; Id = $id; Value = $null; Reason = 'no Version attribute' }
                    continue
                }
                $current = ConvertTo-StableVersion $vm.Groups[1].Value
                if ($null -eq $current) {
                    $unmovable += [pscustomobject]@{ File = $file.FullName; Id = $id; Value = $vm.Groups[1].Value; Reason = 'Version is not MAJOR.MINOR.PATCH' }
                    continue
                }
                if ($current -ge $target) { continue }
                $start = $m.Index + $vm.Groups[1].Index
                $updated = $updated.Substring(0, $start) + $Targets[$id] + $updated.Substring($start + $vm.Groups[1].Length)
                $changes += [pscustomobject]@{ File = $file.FullName; Id = $id; From = $vm.Groups[1].Value; To = $Targets[$id] }
            }
        }
        if ($updated -ne $text) {
            $out = [Text.UTF8Encoding]::new($false).GetBytes($updated)
            if ($bom) { $out = [byte[]](0xEF, 0xBB, 0xBF) + $out }
            [IO.File]::WriteAllBytes($file.FullName, $out)
        }
    }
    return [pscustomobject]@{ Changes = @($changes); Unmovable = @($unmovable) }
}
