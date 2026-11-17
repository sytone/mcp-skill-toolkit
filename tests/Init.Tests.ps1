Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Describe 'MCP CLI initializer helpers' {
    BeforeAll {
        $script:RepoRoot = Split-Path -Parent $PSScriptRoot
        $script:TempBase = Join-Path $script:RepoRoot '.test-tmp'
        $tokens = $null
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $script:RepoRoot 'init.ps1'), [ref]$tokens, [ref]$parseErrors
        )
        if ($parseErrors.Count -gt 0) {
            throw ($parseErrors | Out-String)
        }

        # Load definitions only. Never dot-source init.ps1 or execute its installer body.
        $definitions = $ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst]
        }, $false)
        foreach ($definition in $definitions) {
            # Preserve source locations without executing the installer body.
            Set-Item -Path ('Function:' + $definition.Name) -Value $definition.Body.GetScriptBlock()
        }

        $script:WriteConfig = {
            param([hashtable]$Config)
            $Config | ConvertTo-Json -Depth 100 | Set-Content -Encoding UTF8 -LiteralPath $script:ConfigPath
        }
    }

    BeforeEach {
        $script:TestRoot = Join-Path $script:TempBase ("init-{0}" -f [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $script:TestRoot | Out-Null
        $script:ConfigPath = Join-Path $script:TestRoot 'servers.json'
        $script:McpCliPath = Join-Path $script:TestRoot 'bin/mcp-cli'
        $script:AgencyPath = Join-Path $script:TestRoot 'bin/agency.exe'
        Mock Find-Agency { return $null }
        Mock Install-RequiredModule { throw 'The installer must not run in unit tests.' }
    }

    AfterEach {
        try {
            Should -Invoke Install-RequiredModule -Times 0 -Exactly -Scope It
        }
        finally {
            if (Test-Path -LiteralPath $script:TestRoot) {
                Remove-Item -Recurse -Force -LiteralPath $script:TestRoot
            }
        }
    }

    It 'detects Agency requirements: <Case>' -ForEach @(
        @{ Case = 'enabled Agency'; Command = @('agency', 'mcp', 'demo'); Enabled = $true; Expected = $true }
        @{ Case = 'Agency enabled by default'; Command = @('agency', 'mcp', 'demo'); Enabled = $null; Expected = $true }
        @{ Case = 'disabled Agency'; Command = @('agency', 'mcp', 'demo'); Enabled = $false; Expected = $false }
        @{ Case = 'Agency executable'; Command = @('agency.exe', 'mcp', 'demo'); Enabled = $true; Expected = $true }
        @{ Case = 'absolute Agency executable'; Command = @('C:\Tools\Agency.exe', 'mcp', 'demo'); Enabled = $true; Expected = $true }
        @{ Case = 'portable Agency path'; Command = @('~/tools/agency', 'mcp', 'demo'); Enabled = $true; Expected = $true }
        @{ Case = 'generic executable'; Command = @('npx', '-y', '@example/server'); Enabled = $true; Expected = $false }
        @{ Case = 'Agency only as an argument'; Command = @('node', 'agency'); Enabled = $true; Expected = $false }
        @{ Case = 'similar executable name'; Command = @('agency-helper', 'mcp', 'demo'); Enabled = $true; Expected = $false }
        @{ Case = 'no executable'; Command = @(); Enabled = $true; Expected = $false }
    ) {
        $server = @{ command = $Command }
        if ($null -ne $Enabled) {
            $server.enabled = $Enabled
        }
        & $script:WriteConfig @{ servers = @{ demo = $server } }

        Test-ConfigRequiresAgency -ConfigPath $script:ConfigPath | Should -Be $Expected
        Should -Invoke Find-Agency -Times 0 -Exactly -Scope It
    }

    It 'defaults to the local .NET backend and allows the invocation choice to override config' {
        & $script:WriteConfig @{ mcp_cli_backend = 'bun'; servers = @{} }

        Get-McpCliBackend -ConfigPath $script:ConfigPath | Should -Be 'bun'
        Get-McpCliBackend -RequestedBackend 'dotnet' -ConfigPath $script:ConfigPath | Should -Be 'dotnet'

        & $script:WriteConfig @{ servers = @{} }
        Get-McpCliBackend -ConfigPath $script:ConfigPath | Should -Be 'dotnet'
        { Get-McpCliBackend -RequestedBackend 'remote' -ConfigPath $script:ConfigPath } |
            Should -Throw '*Choose ''dotnet'' or ''bun''*'
    }

    It 'builds and resolves the local CLI apphost from source' {
        $cliPath = Find-DotNetMcpCli

        $cliPath | Should -Be (Join-Path $script:RepoRoot 'src\McpCli\bin\Release\net10.0\mcp-cli.exe')
        Test-Path -LiteralPath $cliPath | Should -BeTrue
    }

    It 'uses the local .NET CLI without looking up or installing Bun' {
        Mock Find-DotNetMcpCli { return 'fixture-dotnet-cli' }
        Mock Find-McpCli { throw 'Bun lookup must not run for the .NET backend.' }
        Mock Get-Command { throw 'Bun lookup must not run for the .NET backend.' } -ParameterFilter { $Name -eq 'bun' }
        Mock Invoke-RestMethod { throw 'Network access must not run for the .NET backend.' }

        Get-McpCliExecutable -Backend 'dotnet' | Should -Be 'fixture-dotnet-cli'

        Should -Invoke Find-DotNetMcpCli -Times 1 -Exactly -Scope It
        Should -Invoke Find-McpCli -Times 0 -Exactly -Scope It
        Should -Invoke Invoke-RestMethod -Times 0 -Exactly -Scope It
    }

    It 'uses the Bun CLI only when the Bun backend is selected' {
        Mock Find-McpCli { return 'fixture-bun-cli' }
        Mock Find-DotNetMcpCli { throw 'The .NET build must not run for the Bun backend.' }

        Get-McpCliExecutable -Backend 'bun' | Should -Be 'fixture-bun-cli'

        Should -Invoke Find-McpCli -Times 1 -Exactly -Scope It
        Should -Invoke Find-DotNetMcpCli -Times 0 -Exactly -Scope It
    }

    It 'initializes generic commands without Agency (Agency path: <AgencySetting>)' -ForEach @(
        @{ AgencySetting = 'absent' }
        @{ AgencySetting = 'preserved' }
    ) {
        $config = @{
            mcp_cli_path = 'mcp-cli'
            servers = @{
                demo = @{ command = @('npx', '-y', '@example/server'); enabled = $true }
                disabled = @{ command = @('agency', 'mcp', 'demo'); enabled = $false }
            }
        }
        if ($AgencySetting -eq 'preserved') {
            $config.agency_path = 'unused-local-agency'
        }
        & $script:WriteConfig $config

        { Initialize-ConfiguredToolPaths -McpCliPath $script:McpCliPath -ConfigPath $script:ConfigPath } | Should -Not -Throw

        $updated = Get-Content -Raw -LiteralPath $script:ConfigPath | ConvertFrom-Json -AsHashtable
        $updated.mcp_cli_path | Should -Be ([IO.Path]::GetFullPath($script:McpCliPath))
        $updated.mcp_cli_backend | Should -Be 'dotnet'
        if ($AgencySetting -eq 'preserved') {
            $updated.agency_path | Should -Be 'unused-local-agency'
        }
        else {
            $updated.Contains('agency_path') | Should -BeFalse
        }
        $updated.servers.disabled.enabled | Should -BeFalse
        Should -Invoke Find-Agency -Times 0 -Exactly -Scope It
        Test-Path "$($script:ConfigPath).tmp" | Should -BeFalse
    }

    It 'fails only when an enabled command requires missing Agency' {
        & $script:WriteConfig @{
            mcp_cli_path = 'mcp-cli'
            servers = @{ demo = @{ command = @('agency', 'mcp', 'demo'); enabled = $true } }
        }
        $original = Get-Content -Raw -LiteralPath $script:ConfigPath

        { Initialize-ConfiguredToolPaths -McpCliPath $script:McpCliPath -ConfigPath $script:ConfigPath } |
            Should -Throw '*Agency is required by an enabled server command*'

        Should -Invoke Find-Agency -Times 1 -Exactly -Scope It
        (Get-Content -Raw -LiteralPath $script:ConfigPath) | Should -BeExactly $original
    }

    It 'updates both paths when an enabled command requires available Agency' {
        Mock Find-Agency { return $script:AgencyPath }
        & $script:WriteConfig @{
            mcp_cli_path = 'mcp-cli'
            servers = @{ demo = @{ command = @('agency', 'mcp', 'demo') } }
        }

        Initialize-ConfiguredToolPaths -McpCliPath $script:McpCliPath -ConfigPath $script:ConfigPath

        $updated = Get-Content -Raw -LiteralPath $script:ConfigPath | ConvertFrom-Json -AsHashtable
        $updated.mcp_cli_path | Should -Be ([IO.Path]::GetFullPath($script:McpCliPath))
        $updated.mcp_cli_backend | Should -Be 'dotnet'
        $updated.agency_path | Should -Be ([IO.Path]::GetFullPath($script:AgencyPath))
        Should -Invoke Find-Agency -Times 1 -Exactly -Scope It
        Test-Path "$($script:ConfigPath).tmp" | Should -BeFalse
    }

    It 'persists an explicitly selected Bun backend with its resolved executable' {
        & $script:WriteConfig @{ mcp_cli_backend = 'dotnet'; servers = @{} }

        Initialize-ConfiguredToolPaths -McpCliPath $script:McpCliPath -McpCliBackend 'bun' -ConfigPath $script:ConfigPath

        $updated = Get-Content -Raw -LiteralPath $script:ConfigPath | ConvertFrom-Json -AsHashtable
        $updated.mcp_cli_backend | Should -Be 'bun'
        $updated.mcp_cli_path | Should -Be ([IO.Path]::GetFullPath($script:McpCliPath))
    }

    It 'keeps an already-current generic configuration byte-for-byte unchanged' {
        & $script:WriteConfig @{ mcp_cli_path = $script:McpCliPath; mcp_cli_backend = 'dotnet'; servers = @{} }
        $original = Get-Content -Raw -LiteralPath $script:ConfigPath

        Initialize-ConfiguredToolPaths -McpCliPath $script:McpCliPath -ConfigPath $script:ConfigPath

        (Get-Content -Raw -LiteralPath $script:ConfigPath) | Should -BeExactly $original
        Should -Invoke Find-Agency -Times 0 -Exactly -Scope It
    }

    It 'does not require Agency or create a configuration when servers.json is absent' {
        { Initialize-ConfiguredToolPaths -McpCliPath $script:McpCliPath -ConfigPath $script:ConfigPath } | Should -Not -Throw

        Test-Path -LiteralPath $script:ConfigPath | Should -BeFalse
        Should -Invoke Find-Agency -Times 0 -Exactly -Scope It
    }
}
