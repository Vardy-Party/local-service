BeforeAll {
    . (Join-Path $PSScriptRoot 'RuntimeNuget.ps1')

    function New-FixtureDeps {
        param([string]$Dir)
        $deps = @{
            targets   = @{
                '.NETCoreApp,Version=v11.0/win-x64' = @{
                    'VardyParty.LocalService.V1/0.1.5' = @{
                        runtime = @{ 'lib/net11.0/VardyParty.LocalService.V1.dll' = @{} }
                    }
                    'Microsoft.Playwright/1.58.0'     = @{
                        runtime = @{ 'lib/net11.0/Microsoft.Playwright.dll' = @{} }
                    }
                }
            }
            libraries = @{
                'VardyParty.LocalService/1.1.30'    = @{ type = 'project' }
                'VardyParty.LocalService.V1/0.1.5'  = @{ type = 'package' }
                'Microsoft.Playwright/1.58.0'       = @{ type = 'package' }
            }
        }
        $path = Join-Path $Dir 'VardyParty.LocalService.deps.json'
        $deps | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8
        return $path
    }
}

Describe 'Strategy plugins stay out of the public runtime nupkg' {
    It 'lists only VardyParty.LocalService package references' {
        $dir = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('n'))
        New-Item -ItemType Directory -Path $dir | Out-Null
        try {
            $path = New-FixtureDeps $dir
            $packages = @(Get-StrategyPluginPackages -DepsPath $path)
            $packages.Id | Should -Be @('VardyParty.LocalService.V1')
            $packages.Version | Should -Be @('0.1.5')
        }
        finally { Remove-Item -LiteralPath $dir -Recurse -Force }
    }

    It 'removes the plugin DLL and packs a nupkg without it' {
        $dir = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('n'))
        New-Item -ItemType Directory -Path $dir | Out-Null
        try {
            New-FixtureDeps $dir | Out-Null
            [IO.File]::WriteAllBytes((Join-Path $dir 'VardyParty.LocalService.dll'), [byte[]](1, 2, 3))
            [IO.File]::WriteAllBytes((Join-Path $dir 'VardyParty.LocalService.V1.dll'), [byte[]](9, 9))
            [IO.File]::WriteAllBytes((Join-Path $dir 'VardyParty.LocalService.V1.pdb'), [byte[]](8))
            [IO.File]::WriteAllBytes((Join-Path $dir 'Microsoft.Playwright.dll'), [byte[]](4))

            $removed = @(Remove-StrategyPluginAssemblies -PublishDir $dir)
            $removed | Should -Contain 'VardyParty.LocalService.V1.dll'
            Test-Path (Join-Path $dir 'VardyParty.LocalService.V1.dll') | Should -BeFalse
            Test-Path (Join-Path $dir 'VardyParty.LocalService.V1.pdb') | Should -BeFalse
            Test-Path (Join-Path $dir 'VardyParty.LocalService.dll') | Should -BeTrue
            Test-Path (Join-Path $dir 'Microsoft.Playwright.dll') | Should -BeTrue

            $nupkg = Join-Path ([IO.Path]::GetTempPath()) ("host-" + [guid]::NewGuid().ToString('n') + '.nupkg')
            New-RuntimeNupkg -PublishDir $dir -PackageId 'VardyParty.LocalService.Runtime.win-x64' -Version '1.1.30' -OutputPath $nupkg

            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $zip = [IO.Compression.ZipFile]::OpenRead($nupkg)
            try {
                $names = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
            }
            finally { $zip.Dispose() }
            $names | Should -Contain 'tools/publish/VardyParty.LocalService.dll'
            $names | Should -Not -Contain 'tools/publish/VardyParty.LocalService.V1.dll'

            $restored = Join-Path $dir 'restored'
            Expand-RuntimeNupkg -Nupkg $nupkg -Destination $restored
            Test-Path (Join-Path $restored 'VardyParty.LocalService.dll') | Should -BeTrue
            Test-Path (Join-Path $restored 'VardyParty.LocalService.V1.dll') | Should -BeFalse

            $plugin = Join-Path $dir 'plugin.nupkg'
            $pluginZip = [IO.Compression.ZipFile]::Open($plugin, [IO.Compression.ZipArchiveMode]::Create)
            try {
                $entry = $pluginZip.CreateEntry('lib/net11.0/VardyParty.LocalService.V1.dll')
                $stream = $entry.Open()
                try { $stream.Write([byte[]](9, 9), 0, 2) } finally { $stream.Dispose() }
            }
            finally { $pluginZip.Dispose() }

            Add-PluginAssemblies -PublishDir $restored -PluginNupkg $plugin -AssemblyNames @('VardyParty.LocalService.V1.dll')
            [IO.File]::ReadAllBytes((Join-Path $restored 'VardyParty.LocalService.V1.dll')) | Should -Be @([byte]9, [byte]9)
        }
        finally {
            Remove-Item -LiteralPath $dir -Recurse -Force
            if ($nupkg) { Remove-Item -LiteralPath $nupkg -Force -ErrorAction SilentlyContinue }
        }
    }
}

Describe 'Assert-LocalServiceDispatch' {
    It 'accepts a local-service Actions run URL and a 40-hex SHA' {
        $sha = 'a' * 40
        Assert-LocalServiceDispatch "https://github.com/Vardy-Party/local-service/actions/runs/12" $sha | Should -BeTrue
    }

    It 'rejects a dispatch that points at another repository' {
        { Assert-LocalServiceDispatch 'https://github.com/Vardy-Party/M3U8-resolver/actions/runs/12' ('b' * 40) } | Should -Throw
    }
}
