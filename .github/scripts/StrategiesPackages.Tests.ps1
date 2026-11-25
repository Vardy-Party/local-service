# Invoke-Pester ./.github/scripts/StrategiesPackages.Tests.ps1
# Also run by ci.yml (job "Script tests") on Linux, Windows and macOS.
BeforeAll {
    . (Join-Path $PSScriptRoot 'StrategiesPackages.ps1')

    function New-Csproj([string]$Dir, [string]$Name, [string]$Body, [switch]$Bom, [switch]$Crlf) {
        $text = "<Project Sdk=`"Microsoft.NET.Sdk`">`n  <ItemGroup>`n$Body`n  </ItemGroup>`n</Project>`n"
        if ($Crlf) { $text = $text.Replace("`n", "`r`n") }
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($text)
        if ($Bom) { $bytes = [byte[]](0xEF, 0xBB, 0xBF) + $bytes }
        $path = Join-Path $Dir $Name
        New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
        [IO.File]::WriteAllBytes($path, $bytes)
        return $path
    }
}

Describe 'Assert-DispatchSource' {
    It 'accepts a Strategies run URL with a 40-hex SHA' {
        # Arrange
        $runUrl = 'https://github.com/Vardy-Party/Strategies/actions/runs/44051798038'
        $sha = '5f47388' + ('0' * 33)

        # Act
        $dispatched = Assert-DispatchSource $runUrl $sha

        # Assert
        $dispatched | Should -BeTrue
    }

    It 'treats an empty URL and SHA as a manual run' {
        # Arrange
        $runUrl = ''
        $sha = ''

        # Act
        $dispatched = Assert-DispatchSource $runUrl $sha

        # Assert
        $dispatched | Should -BeFalse
    }

    It 'rejects a run URL carrying a skip-CI token' {
        # Arrange
        $runUrl = 'https://github.com/Vardy-Party/Strategies/actions/runs/1 [skip ci]'
        $sha = 'a' * 40

        # Act
        $act = { Assert-DispatchSource $runUrl $sha }

        # Assert
        $act | Should -Throw '*run_url*'
    }

    It 'rejects a run URL for another repository' {
        # Arrange
        $runUrl = 'https://github.com/Example-Owner/Example-Repo/actions/runs/1'
        $sha = 'a' * 40

        # Act
        $act = { Assert-DispatchSource $runUrl $sha }

        # Assert
        $act | Should -Throw '*run_url*'
    }

    It 'rejects a SHA that is not 40 lowercase hex characters' {
        # Arrange
        $runUrl = 'https://github.com/Vardy-Party/Strategies/actions/runs/1'
        $sha = 'g' * 40

        # Act
        $act = { Assert-DispatchSource $runUrl $sha }

        # Assert
        $act | Should -Throw '*source_sha*'
    }

    It 'rejects a URL without a SHA' {
        # Arrange
        $runUrl = 'https://github.com/Vardy-Party/Strategies/actions/runs/1'
        $sha = ''

        # Act
        $act = { Assert-DispatchSource $runUrl $sha }

        # Assert
        $act | Should -Throw '*source_sha*'
    }
}

Describe 'Assert-NoSkipCiToken' {
    It 'rejects <Token> in any case' -ForEach @(
        @{ Token = '[ACTIONS SKIP]' }
        @{ Token = '[Skip CI]' }
        @{ Token = '[ci skip]' }
        @{ Token = '[no ci]' }
        @{ Token = '[skip actions]' }
    ) {
        # Arrange
        $message = "chore(deps): pin Strategies packages $Token"

        # Act
        $act = { Assert-NoSkipCiToken $message 'commit message' }

        # Assert
        $act | Should -Throw '*skip-CI token*'
    }

    It 'rejects a skip-checks trailer' {
        # Arrange
        $message = "chore(deps): pin`n`nskip-checks: true"

        # Act
        $act = { Assert-NoSkipCiToken $message 'commit message' }

        # Assert
        $act | Should -Throw '*skip-checks*'
    }

    It 'accepts a normal bot commit message' {
        # Arrange
        $message = "chore(deps): pin Strategies packages (Abstractions 0.2.7)`n`nRun: https://github.com/Vardy-Party/M3U8-resolver/actions/runs/1"

        # Act
        $act = { Assert-NoSkipCiToken $message 'commit message' }

        # Assert
        $act | Should -Not -Throw
    }
}

Describe 'Get-RequestedVersions' {
    It 'returns every Strategies id, raising only the payload minimums' {
        # Arrange
        $payload = '{"VardyParty.LocalService.Abstractions":"0.2.7"}'
        $inputJson = '{"VardyParty.LocalService.V1":"0.1.1"}'

        # Act
        $result = Get-RequestedVersions -PayloadJson $payload -InputJson $inputJson

        # Assert
        @($result.Keys) | Should -Be @(Get-StrategiesPackageIds)
        $result['VardyParty.LocalService.Abstractions'] | Should -Be '0.2.7'
        $result['VardyParty.LocalService.V1'] | Should -Be '0.0.0'
    }

    It 'falls back to zero minimums when payload and input are empty' {
        # Arrange
        $payload = 'null'
        $inputJson = ''

        # Act
        $result = Get-RequestedVersions -PayloadJson $payload -InputJson $inputJson

        # Assert
        @($result.Keys) | Should -Be @(Get-StrategiesPackageIds)
        @($result.Values | Select-Object -Unique) | Should -Be @('0.0.0')
    }

    It 'rejects ids that are not Strategies packages' {
        # Arrange
        $payload = '{"Example.Other.Package":"1.0.0"}'

        # Act
        $act = { Get-RequestedVersions -PayloadJson $payload -InputJson '' }

        # Assert
        $act | Should -Throw '*Unknown Strategies package id*'
    }
}

Describe 'Select-TargetVersions' {
    It 'picks the highest stable feed version, ignoring prereleases' {
        # Arrange
        $requested = [ordered]@{ 'VardyParty.LocalService.Abstractions' = '0.2.7' }
        $feed = @{ 'VardyParty.LocalService.Abstractions' = @('0.2.2', '0.2.10', '0.2.7', '0.3.0-preview.1') }

        # Act
        $targets = Select-TargetVersions $requested $feed

        # Assert
        $targets['VardyParty.LocalService.Abstractions'] | Should -Be '0.2.10'
    }

    It 'includes ids the payload did not mention (coalesced dispatches)' {
        # Arrange
        $requested = Get-RequestedVersions -PayloadJson '{"VardyParty.LocalService.Abstractions":"0.2.8"}' -InputJson ''
        $feed = @{
            'VardyParty.LocalService.Abstractions' = @('0.2.7', '0.2.8')
            'VardyParty.LocalService.V1'           = @('0.1.1')
            'VardyParty.LocalService.V2'           = @('0.1.3')
            'VardyParty.LocalService.V1.Scrape'    = @('0.1.19', '0.1.20')
            'VardyParty.LocalService.V2.Scrape'    = @('0.1.27')
        }

        # Act
        $targets = Select-TargetVersions $requested $feed

        # Assert
        $targets['VardyParty.LocalService.V1.Scrape'] | Should -Be '0.1.20'
        $targets['VardyParty.LocalService.Abstractions'] | Should -Be '0.2.8'
    }

    It 'throws when the requested version is not on the feed yet' {
        # Arrange
        $requested = [ordered]@{ 'VardyParty.LocalService.V1.Scrape' = '0.1.19' }
        $feed = @{ 'VardyParty.LocalService.V1.Scrape' = @('0.1.13') }

        # Act
        $act = { Select-TargetVersions $requested $feed }

        # Assert
        $act | Should -Throw '*not on the feed yet*'
    }
}

Describe 'Resolve-TargetVersions' {
    It 'polls until the feed shows the requested version' {
        # Arrange
        $script:calls = 0
        $getVersions = { param($id) $script:calls++; if ($script:calls -lt 2) { @('0.2.2') } else { @('0.2.2', '0.2.7') } }
        $requested = [ordered]@{ 'VardyParty.LocalService.Abstractions' = '0.2.7' }

        # Act
        $targets = Resolve-TargetVersions $requested $getVersions -TimeoutSeconds 30 -PollSeconds 0

        # Assert
        $targets['VardyParty.LocalService.Abstractions'] | Should -Be '0.2.7'
        $script:calls | Should -Be 2
    }
}

Describe 'Select-BotBranch' {
    It 'reuses the branch of an open bot PR' {
        # Arrange
        $open = 'bot/strategies-packages-7'
        $remote = @('main', 'bot/strategies-packages-7')

        # Act
        $choice = Select-BotBranch $open $remote 'bot/strategies-packages-' '9'

        # Assert
        $choice.Branch | Should -Be 'bot/strategies-packages-7'
        $choice.Mode | Should -Be 'open-pr'
    }

    It 'reuses this run''s branch when a re-run finds it without a PR' {
        # Arrange
        $remote = @('main', 'bot/strategies-packages-9')

        # Act
        $choice = Select-BotBranch '' $remote 'bot/strategies-packages-' '9'

        # Assert
        $choice.Branch | Should -Be 'bot/strategies-packages-9'
        $choice.Mode | Should -Be 'existing-branch'
    }

    It 'creates a new branch when neither exists' {
        # Arrange
        $remote = @('main', 'bot/strategies-packages-3')

        # Act
        $choice = Select-BotBranch '' $remote 'bot/strategies-packages-' '9'

        # Assert
        $choice.Branch | Should -Be 'bot/strategies-packages-9'
        $choice.Mode | Should -Be 'new'
    }
}

Describe 'Update-PackageReferences' {
    BeforeEach {
        $root = Join-Path $TestDrive ([guid]::NewGuid())
    }

    It 'raises pins, keeps attributes after Version, BOM and CRLF' {
        # Arrange
        $body = @(
            '    <PackageReference Include="VardyParty.LocalService.Abstractions" Version="0.2.2" />'
            '    <PackageReference Include="VardyParty.LocalService.V1.Scrape" Version="0.1.12" Condition="!Exists(''x'')" />'
            '    <ProjectReference Include="..\VardyParty.LocalService.V1.Scrape.csproj" />'
        ) -join "`n"
        $path = New-Csproj $root 'A/A.csproj' $body -Bom -Crlf
        $targets = [ordered]@{ 'VardyParty.LocalService.Abstractions' = '0.2.7'; 'VardyParty.LocalService.V1.Scrape' = '0.1.19' }

        # Act
        $result = Update-PackageReferences $root $targets

        # Assert
        $result.Changes.Count | Should -Be 2
        $bytes = [IO.File]::ReadAllBytes($path)
        $bytes[0..2] | Should -Be @(0xEF, 0xBB, 0xBF)
        $text = [IO.File]::ReadAllText($path)
        $text | Should -Match 'Include="VardyParty.LocalService.Abstractions" Version="0.2.7" />\r\n'
        $text | Should -Match 'Include="VardyParty.LocalService.V1.Scrape" Version="0.1.19" Condition="!Exists\(''x''\)" />'
        $text | Should -Match '<ProjectReference Include="\.\.\\VardyParty.LocalService.V1.Scrape.csproj" />'
        ($text -split "`r`n").Count | Should -Be (($text -split "`n").Count)
    }

    It 'handles a ">" inside a quoted attribute before Version' {
        # Arrange
        $body = '    <PackageReference Condition="''$(A)'' > ''1''" Include="VardyParty.LocalService.V2" Version="0.1.0" />'
        $path = New-Csproj $root 'Q/Q.csproj' $body
        $targets = [ordered]@{ 'VardyParty.LocalService.V2' = '0.1.3' }

        # Act
        $result = Update-PackageReferences $root $targets

        # Assert
        $result.Changes.Count | Should -Be 1
        [IO.File]::ReadAllText($path) | Should -Match 'Include="VardyParty.LocalService.V2" Version="0.1.3" />'
    }

    It 'reports references it cannot move instead of skipping them silently' {
        # Arrange
        $body = @(
            '    <PackageReference Include="VardyParty.LocalService.V1" Version="$(StrategiesVersion)" />'
            '    <PackageReference Include="VardyParty.LocalService.V2" Version="0.1.*" />'
            '    <PackageReference Include="VardyParty.LocalService.Abstractions">'
            '      <Version>0.2.2</Version>'
            '    </PackageReference>'
        ) -join "`n"
        $path = New-Csproj $root 'U/U.csproj' $body
        $before = [IO.File]::ReadAllBytes($path)
        $targets = [ordered]@{ 'VardyParty.LocalService.Abstractions' = '0.2.7'; 'VardyParty.LocalService.V1' = '0.1.1'; 'VardyParty.LocalService.V2' = '0.1.3' }

        # Act
        $result = Update-PackageReferences $root $targets

        # Assert
        $result.Changes.Count | Should -Be 0
        @($result.Unmovable.Id | Sort-Object) | Should -Be @('VardyParty.LocalService.Abstractions', 'VardyParty.LocalService.V1', 'VardyParty.LocalService.V2')
        [IO.File]::ReadAllBytes($path) | Should -Be $before
    }

    It 'never lowers a pin and leaves untouched files byte-identical' {
        # Arrange
        $path = New-Csproj $root 'B/B.csproj' '    <PackageReference Include="VardyParty.LocalService.V1" Version="0.1.5" />'
        $before = [IO.File]::ReadAllBytes($path)
        $targets = [ordered]@{ 'VardyParty.LocalService.V1' = '0.1.1' }

        # Act
        $result = Update-PackageReferences $root $targets

        # Assert
        $result.Changes.Count | Should -Be 0
        $result.Unmovable.Count | Should -Be 0
        [IO.File]::ReadAllBytes($path) | Should -Be $before
    }

    It 'does not touch a package whose id only shares a prefix' {
        # Arrange
        $path = New-Csproj $root 'C/C.csproj' '    <PackageReference Include="VardyParty.LocalService.V1.Scrape" Version="0.1.12" />'
        $targets = [ordered]@{ 'VardyParty.LocalService.V1' = '0.1.9' }

        # Act
        $result = Update-PackageReferences $root $targets

        # Assert
        $result.Changes.Count | Should -Be 0
        [IO.File]::ReadAllText($path) | Should -Match 'Version="0.1.12"'
    }

    It 'skips bin and obj folders' {
        # Arrange
        $path = New-Csproj $root 'D/obj/D.csproj' '    <PackageReference Include="VardyParty.LocalService.V2" Version="0.1.0" />'
        $targets = [ordered]@{ 'VardyParty.LocalService.V2' = '0.1.3' }

        # Act
        $result = Update-PackageReferences $root $targets

        # Assert
        $result.Changes.Count | Should -Be 0
        [IO.File]::ReadAllText($path) | Should -Match 'Version="0.1.0"'
    }
}
