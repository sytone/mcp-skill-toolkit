Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Describe 'Build source unit behavior' {
    BeforeAll {
        $script:UnitRepoRoot = Split-Path -Parent $PSScriptRoot
        $script:BuildUnitRoot = Join-Path $script:UnitRepoRoot ('.test-tmp/unit-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $script:BuildUnitRoot | Out-Null

        $tokens = $null
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $script:UnitRepoRoot 'psakefile.ps1'), [ref]$tokens, [ref]$parseErrors
        )
        if ($parseErrors.Count -gt 0) {
            throw ($parseErrors | Out-String)
        }
        # Keep the source file and line locations for Pester coverage. Do not run psake tasks.
        $definitions = $ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst]
        }, $false)
        foreach ($definition in $definitions) {
            Set-Item -Path ('Function:' + $definition.Name) -Value $definition.Body.GetScriptBlock()
        }

        $script:WriteBuildUnitConfig = {
            param([hashtable]$Config)
            $Config | ConvertTo-Json -Depth 100 | Set-Content -Encoding UTF8 -LiteralPath $script:BuildUnitConfigPath
        }
        $script:AgencyUnitHelp = @(
            'MCP commands'
            'Commands:'
            '  local        Local utility'
            '  npx          Package utility'
            '  remote       Remote utility'
            '  alpha        Alpha MCP server'
            '  beta         Beta MCP server'
            '  help         Help utility'
            'Options:'
            '  --help       Print help'
        ) -join "`n"
    }

    BeforeEach {
        $script:BuildUnitCaseRoot = Join-Path $script:BuildUnitRoot ([Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $script:BuildUnitCaseRoot | Out-Null
        $script:BuildUnitConfigPath = Join-Path $script:BuildUnitCaseRoot 'servers.json'
        Mock Write-BuildLog {}
        Mock Write-Warning {}
        Mock Resolve-AgencyPath { throw 'Unexpected Agency platform lookup in an offline unit test.' }
        Mock Resolve-McpCliPath { throw 'Unexpected MCP CLI platform lookup in an offline unit test.' }
    }

    AfterAll {
        if (Test-Path -LiteralPath $script:BuildUnitRoot) {
            Remove-Item -Recurse -Force -LiteralPath $script:BuildUnitRoot
        }
    }

    Context 'Quoting, paths, and descriptions' {
        It 'quotes apostrophes as literal YAML and PowerShell strings without expanding expressions' {
            $text = 'O''Brien $HOME; $(throw "not evaluated")'
            $expected = "'O''Brien " + '$HOME; $(throw "not evaluated")' + "'"

            ConvertTo-YamlSingleQuoted -Text $text | Should -BeExactly $expected
            ConvertTo-PowerShellSingleQuoted -Text $text | Should -BeExactly $expected
            ConvertTo-YamlSingleQuoted -Text $null | Should -BeExactly "''"
            ConvertTo-PowerShellSingleQuoted -Text '' | Should -BeExactly "''"
        }

        It 'renders command names, home-relative paths, and prefix siblings with correct boundaries' {
            ConvertTo-PowerShellPathExpression -Path 'mcp-cli' | Should -BeExactly "'mcp-cli'"
            ConvertTo-PowerShellPathExpression -Path ' ' | Should -BeExactly "''"
            ConvertTo-PowerShellPathExpression -Path $HOME | Should -BeExactly '$HOME'
            $child = Join-Path $HOME "unit's/mcp-cli"
            $relative = [IO.Path]::GetRelativePath([IO.Path]::GetFullPath($HOME), [IO.Path]::GetFullPath($child))
            $expected = '(Join-Path $HOME ' + (ConvertTo-PowerShellSingleQuoted -Text $relative) + ')'
            ConvertTo-PowerShellPathExpression -Path $child | Should -BeExactly $expected
            ConvertTo-PowerShellPathExpression -Path (Join-Path '~' "unit's/mcp-cli") | Should -BeExactly $expected

            $sibling = [IO.Path]::GetFullPath((Join-Path ($HOME + '-sibling') 'mcp-cli'))
            ConvertTo-PowerShellPathExpression -Path $sibling |
                Should -BeExactly (ConvertTo-PowerShellSingleQuoted -Text $sibling)
        }

        It 'normalizes description tokens and whitespace with a fallback and a bounded length' {
            Format-SkillDescription -Description '  MCP  Search   tools for MCP users  ' -ServerName 'demo' |
                Should -BeExactly 'Search tools for users'
            Format-SkillDescription -Description 'MyMCP MCP-like tools' -ServerName 'demo' |
                Should -BeExactly 'MyMCP -like tools'
            Format-SkillDescription -Description ' MCP ' -ServerName 'demo' |
                Should -BeExactly 'demo tools through mcp-cli'
            $long = Format-SkillDescription -Description ('x' * 1100) -ServerName 'demo'
            $long.Length | Should -Be 1024
            $long | Should -BeExactly ('x' * 1024)
        }
    }

    Context 'Source server selection' {
        It 'enables servers by default but honors explicit disabled entries and an empty selection' {
            $config = [pscustomobject]@{ servers = [pscustomobject]@{
                alpha = [pscustomobject]@{ command = @('node') }
                beta = [pscustomobject]@{ command = @('node'); enabled = $true }
                disabled = [pscustomobject]@{ command = @('node'); enabled = $false }
            } }

            $result = Get-SourceServerMap -ConfigObject $config -SelectedServers @('', ' ')

            @($result.Keys | Sort-Object) | Should -Be @('alpha', 'beta')
            [object]::ReferenceEquals($result.alpha, $config.servers.alpha) | Should -BeTrue
        }

        It 'normalizes comma-separated selections, collapses duplicates, and never enables a disabled selection' {
            $config = '{"servers":{"alpha":{},"beta":{},"other":{},"disabled":{"enabled":false}}}' | ConvertFrom-Json

            $result = Get-SourceServerMap -ConfigObject $config -SelectedServers @('alpha, beta', '', 'beta', 'disabled')

            @($result.Keys | Sort-Object) | Should -Be @('alpha', 'beta')
        }

        It 'reports missing server configuration and unknown requested names' {
            { Get-SourceServerMap -ConfigObject ([pscustomobject]@{}) } |
                Should -Throw '*Source config does not contain a servers object*'
            { Get-SourceServerMap -ConfigObject ([pscustomobject]@{ servers = $null }) } |
                Should -Throw '*Source config does not contain a servers object*'
            $config = '{"servers":{"alpha":{}}}' | ConvertFrom-Json
            { Get-SourceServerMap -ConfigObject $config -SelectedServers @('missing') } |
                Should -Throw '*Unknown server: missing. Available servers: alpha*'
        }

        It 'rejects unsafe server names before they become output paths: <UnsafeName>' -ForEach @(
            @{ UnsafeName = '../outside' }
            @{ UnsafeName = '..\outside' }
            @{ UnsafeName = '/absolute-server' }
        ) {
            $servers = [pscustomobject]@{}
            $servers | Add-Member -MemberType NoteProperty -Name $UnsafeName -Value ([pscustomobject]@{ command = @('node') })
            $config = [pscustomobject]@{ servers = $servers }

            { Get-SourceServerMap -ConfigObject $config } | Should -Throw '*Invalid server name:*'
        }
    }

    Context 'MCP CLI configuration conversion' {
        It 'converts stdio commands and expands both port placeholders including the default port' {
            $config = [pscustomobject]@{ servers = [pscustomobject]@{
                explicit = [pscustomobject]@{
                    command = @('node', 'server.js', '{port}', '${PORT}', 'pair={port}/${PORT}')
                    transport = ' STDIO '
                    default_port = 4321
                }
                default = [pscustomobject]@{ command = @('node', '{port}', '${PORT}'); transport = 'stdio' }
            } }

            $result = ConvertTo-McpCliConfig -ConfigObject $config

            $result.mcpServers.explicit.command | Should -BeExactly 'node'
            $result.mcpServers.explicit.args | Should -Be @('server.js', '4321', '4321', 'pair=4321/4321')
            $result.mcpServers.default.args | Should -Be @('3500', '3500')
        }

        It 'infers stdio from command arguments and omits args for a singleton command' {
            $config = [pscustomobject]@{ servers = [pscustomobject]@{
                inferred = [pscustomobject]@{ command = @('node', '--transport', 'StDiO', 'server.js') }
                singleton = [pscustomobject]@{ command = @('standalone'); transport = 'stdio' }
            } }

            $result = ConvertTo-McpCliConfig -ConfigObject $config

            $result.mcpServers.inferred.args | Should -Be @('--transport', 'StDiO', 'server.js')
            $result.mcpServers.singleton.command | Should -BeExactly 'standalone'
            $result.mcpServers.singleton.Contains('args') | Should -BeFalse
        }

        It 'generates loopback HTTP URLs with explicit transport precedence and normalized endpoints' {
            $config = [pscustomobject]@{ servers = [pscustomobject]@{
                explicit = [pscustomobject]@{
                    command = @('node', '--transport', 'stdio'); transport = ' HTTP '; default_port = 4100; endpoint = 'mcp'
                }
                default = [pscustomobject]@{ command = @('node') }
                slash = [pscustomobject]@{ command = @('node'); default_port = 4200; endpoint = '/events' }
            } }

            $result = ConvertTo-McpCliConfig -ConfigObject $config

            $result.mcpServers.explicit.url | Should -BeExactly 'http://127.0.0.1:4100/mcp'
            $result.mcpServers.default.url | Should -BeExactly 'http://127.0.0.1:3500/'
            $result.mcpServers.slash.url | Should -BeExactly 'http://127.0.0.1:4200/events'
        }

        It 'maps allow and deny policies to string arrays without inventing absent policy fields' {
            $config = [pscustomobject]@{ servers = [pscustomobject]@{
                filtered = [pscustomobject]@{
                    command = @('node'); transport = 'stdio'
                    tool_policy = [pscustomobject]@{ allow = @('read', 7); deny = @('delete') }
                }
                denyOnly = [pscustomobject]@{
                    command = @('node'); transport = 'stdio'; tool_policy = [pscustomobject]@{ deny = @() }
                }
            } }

            $result = ConvertTo-McpCliConfig -ConfigObject $config

            $result.mcpServers.filtered.allowedTools | Should -Be @('read', '7')
            $result.mcpServers.filtered.disabledTools | Should -Be @('delete')
            $result.mcpServers.denyOnly.Contains('allowedTools') | Should -BeFalse
            @($result.mcpServers.denyOnly.disabledTools).Count | Should -Be 0
        }

        It 'skips disabled and commandless entries without resolving Agency or invoking a CLI' {
            $config = [pscustomobject]@{ servers = [pscustomobject]@{
                disabled = [pscustomobject]@{ command = @('agency', 'mcp', 'alpha'); enabled = $false }
                missing = [pscustomobject]@{ command = @() }
                active = [pscustomobject]@{ command = @('node'); transport = 'stdio' }
            } }

            $result = ConvertTo-McpCliConfig -ConfigObject $config

            @($result.mcpServers.Keys) | Should -Be @('active')
            Should -Invoke Write-Warning -Times 1 -Exactly -ParameterFilter { $Message -like "*Skipping 'missing'*no command*" }
            Should -Invoke Resolve-AgencyPath -Times 0 -Exactly
            Should -Invoke Resolve-McpCliPath -Times 0 -Exactly
        }

        It 'rejects a missing servers object but accepts an explicitly empty object' {
            { ConvertTo-McpCliConfig -ConfigObject ([pscustomobject]@{}) } |
                Should -Throw '*Source config does not contain a servers object*'
            { ConvertTo-McpCliConfig -ConfigObject ([pscustomobject]@{ servers = $null }) } |
                Should -Throw '*Source config does not contain a servers object*'
            $result = ConvertTo-McpCliConfig -ConfigObject ([pscustomobject]@{ servers = [pscustomobject]@{} })
            $result.mcpServers.Count | Should -Be 0
        }

        It 'converts local Agency commands to quiet stdio and resolves a configured executable only once' {
            $script:UnitAgencyExecutable = Join-Path $script:BuildUnitCaseRoot 'agency.exe'
            Mock Resolve-AgencyPath { return $script:UnitAgencyExecutable }
            $config = [pscustomobject]@{
                agency_path = 'configured-agency'
                agencyPath = 'ignored-alias'
                servers = [pscustomobject]@{
                    alpha = [pscustomobject]@{
                        command = @('agency', 'mcp', 'alpha', '--port', '{port}', '--transport=http', '--tenant', 'unit-tenant', '--port=8888', '--transport', 'http')
                    }
                    beta = [pscustomobject]@{ command = @('agency', 'mcp', 'beta', '--filter', 'x') }
                }
            }

            $result = ConvertTo-McpCliConfig -ConfigObject $config

            $result.mcpServers.alpha.command | Should -BeExactly $script:UnitAgencyExecutable
            $result.mcpServers.alpha.args | Should -Be @('--verbosity', 'error', 'mcp', 'alpha', '--tenant', 'unit-tenant')
            $result.mcpServers.beta.args | Should -Be @('--verbosity', 'error', 'mcp', 'beta', '--filter', 'x')
            Should -Invoke Resolve-AgencyPath -Times 1 -Exactly -ParameterFilter { $RequestedPath -eq 'configured-agency' }
        }

        It 'preserves Agency remote arguments and uses the default executable without platform discovery' {
            $config = [pscustomobject]@{ servers = [pscustomobject]@{
                remote = [pscustomobject]@{
                    command = @('agency', 'mcp', 'remote', 'remote-demo', '--port', '{port}', '--transport', 'http')
                }
            } }

            $result = ConvertTo-McpCliConfig -ConfigObject $config

            $result.mcpServers.remote.command | Should -BeExactly 'agency'
            $result.mcpServers.remote.args |
                Should -Be @('--verbosity', 'error', 'mcp', 'remote', 'remote-demo', '--port', '{port}', '--transport', 'http')
            Should -Invoke Resolve-AgencyPath -Times 0 -Exactly
        }
    }

    Context 'Discovery job lifetime with every job API mocked' {
        BeforeAll {
            # Fail-closed local signatures avoid creating a real Job just to mock
            # cmdlets whose Job parameters otherwise require live Job instances.
            function Start-Job {
                param([scriptblock]$ScriptBlock, [object[]]$ArgumentList)
                throw 'A real discovery job must never start in unit tests.'
            }
            function Wait-Job {
                [CmdletBinding()]
                param([Parameter(ValueFromPipeline = $true)][object]$Job, [int]$Timeout)
                process { throw 'A real discovery job must never be awaited in unit tests.' }
            }
            function Receive-Job {
                [CmdletBinding()]
                param([object]$Job)
                throw 'A real discovery job must never be received in unit tests.'
            }
            function Stop-Job {
                [CmdletBinding()]
                param([object]$Job)
                throw 'A real discovery job must never be stopped in unit tests.'
            }
            function Remove-Job {
                [CmdletBinding()]
                param([object]$Job, [switch]$Force)
                throw 'A real discovery job must never be removed in unit tests.'
            }
        }

        BeforeEach {
            $script:UnitFakeJob = [pscustomobject]@{ Name = 'unit-fake-job' }
            $script:UnitFakeDiscoveryResult = [pscustomobject]@{ ExitCode = 0; Output = @('Tools (0):') }
            Mock Start-Job { return $script:UnitFakeJob }
            Mock Wait-Job { return $script:UnitFakeJob }
            Mock Receive-Job { return $script:UnitFakeDiscoveryResult }
            Mock Stop-Job {}
            Mock Remove-Job {}
        }

        It 'returns a completed result and removes the job without running the captured CLI scriptblock' {
            $mcpPath = Join-Path $script:BuildUnitCaseRoot 'does-not-exist/mcp-cli.exe'

            $result = Invoke-McpCliInfo -McpCli $mcpPath -ConfigPath $script:BuildUnitConfigPath -ServerName 'alpha' -TimeoutSeconds 7

            [object]::ReferenceEquals($result, $script:UnitFakeDiscoveryResult) | Should -BeTrue
            Should -Invoke Start-Job -Times 1 -Exactly -ParameterFilter {
                $ScriptBlock -is [scriptblock] -and $ArgumentList.Count -eq 3 -and
                $ArgumentList[1] -eq $script:BuildUnitConfigPath -and $ArgumentList[2] -eq 'alpha'
            }
            Should -Invoke Wait-Job -Times 1 -Exactly -ParameterFilter { $Timeout -eq 7 }
            Should -Invoke Receive-Job -Times 1 -Exactly
            Should -Invoke Remove-Job -Times 1 -Exactly -ParameterFilter { $Force -and $Job.Name -eq 'unit-fake-job' }
            Should -Invoke Stop-Job -Times 0 -Exactly
        }

        It 'stops and removes a timed-out job without attempting to receive its result' {
            Mock Wait-Job { return $null }
            $mcpPath = Join-Path $script:BuildUnitCaseRoot 'does-not-exist/mcp-cli.exe'

            $result = Invoke-McpCliInfo -McpCli $mcpPath -ConfigPath $script:BuildUnitConfigPath -ServerName 'alpha' -TimeoutSeconds 7

            $result.ExitCode | Should -Be -1
            $result.Output | Should -BeExactly 'Timed out after 7 second(s).'
            Should -Invoke Stop-Job -Times 1 -Exactly -ParameterFilter { $Job.Name -eq 'unit-fake-job' }
            Should -Invoke Remove-Job -Times 1 -Exactly -ParameterFilter { $Force -and $Job.Name -eq 'unit-fake-job' }
            Should -Invoke Receive-Job -Times 0 -Exactly
        }
    }

    Context 'Tool information parsing' {
        It 'parses tool names and per-tool parameters while deduplicating repeated parameter lines' {
            $bullet = [char]0x2022
            $text = @(
                '  not_a_tool'
                'Tools (2):'
                '  search-items'
                '    Finds items'
                "    $bullet query (string, required)"
                "    $bullet query (string, repeated)"
                "    $bullet page_size (integer, optional)"
                '  _status2'
                "    $bullet verbose (boolean)"
            ) -join "`r`n"

            $tools = @(Get-ToolMetadataFromInfoText -InfoText $text)

            $tools.Count | Should -Be 2
            $tools[0].Name | Should -BeExactly 'search-items'
            @($tools[0].Parameters) | Should -Be @('query', 'page_size')
            $tools[1].Name | Should -BeExactly '_status2'
            @($tools[1].Parameters) | Should -Be @('verbose')
            @(Get-ToolNamesFromInfoText -InfoText $text) | Should -Be @('search-items', '_status2')
        }

        It 'returns no tools for empty, headerless, zero-tool, and malformed information' {
            foreach ($text in @('', ' ', '  tool_without_a_header', "Tools (0):`n", "Tools (1):`n  invalid/name`n  3invalid")) {
                @(Get-ToolMetadataFromInfoText -InfoText $text).Count | Should -Be 0
                @(Get-ToolNamesFromInfoText -InfoText $text).Count | Should -Be 0
            }
        }

        It 'repairs captured bullet encoding and ignores malformed or orphaned parameter lines' {
            $text = @(
                'Tools (1):'
                '    ΓÇó orphan (string)'
                '  valid_tool'
                '    ΓÇó good_parameter (string)'
                '    ΓÇó bad-parameter (string)'
                '    missing_bullet (string)'
                '    ΓÇó missing_type'
            ) -join "`n"

            $repaired = Repair-TextEncodingArtifacts -Text $text
            $tools = @(Get-ToolMetadataFromInfoText -InfoText $repaired)

            $repaired | Should -Not -Match 'ΓÇó'
            $tools.Count | Should -Be 1
            @($tools[0].Parameters) | Should -Be @('good_parameter')
        }
    }

    Context 'Agency help parsing and offline synchronization' {
        It 'reads only server commands in the Commands section and excludes utilities and malformed indentation' {
            $text = @(
                '  before       Not in Commands'
                $script:AgencyUnitHelp.Replace('Options:', "    nested       Not a top-level command`n  invalid/name  Not a valid command`nOptions:")
                '  after        Not in Commands'
            ) -join "`n"

            $servers = @(Get-AgencyMcpServersFromHelp -HelpText $text)

            @($servers.Name) | Should -Be @('alpha', 'beta')
            $servers[0].Description | Should -BeExactly 'Alpha MCP server'
        }

        It 'reports help without usable server commands rather than silently inventing an inventory' {
            foreach ($text in @('', 'No command section', "Commands:`n  local   Utility`n  remote  Utility`nOptions:")) {
                { Get-AgencyMcpServersFromHelp -HelpText $text } |
                    Should -Throw '*No Agency MCP servers were found*'
            }
        }

        It 'extracts distinct server-specific options and excludes shared launch and authentication options' {
            $text = @(
                'Options:'
                '    --tenant <TENANT>  Tenant to use'
                '    --feature         Enable feature'
                '    --tenant <TENANT>  Duplicate'
                '    --no-config-cache  Common'
                '    --transport <TRANSPORT> Common'
                '    --port <PORT> Common'
                '    --entra-client-id <CLIENT> Common'
                '    --help Common'
            ) -join "`n"

            @(Get-AgencyMcpConfigurationOptions -HelpText $text) | Should -Be @('--tenant <TENANT>', '--feature')
            @(Get-AgencyMcpConfigurationOptions -HelpText '').Count | Should -Be 0
        }

        It 'adds disabled servers to an empty config with unique default ports and filtered configuration options' {
            & $script:WriteBuildUnitConfig @{ servers = @{} }
            $options = "Options:`n    --tenant <TENANT>  Tenant`n    --port <PORT> Common"

            $changes = @(Sync-AgencyMcpServers -ConfigPath $script:BuildUnitConfigPath -HelpText $script:AgencyUnitHelp -ServerHelpByName @{ alpha = $options })

            $config = Get-Content -Raw -LiteralPath $script:BuildUnitConfigPath | ConvertFrom-Json -AsHashtable
            @($config.servers.Keys | Sort-Object) | Should -Be @('alpha', 'beta')
            $changes.Count | Should -Be 2
            @($changes.Action | Select-Object -Unique) | Should -Be @('Added')
            $config.servers.alpha.enabled | Should -BeFalse
            $config.servers.beta.enabled | Should -BeFalse
            $config.servers.alpha.default_port | Should -Be 3456
            $config.servers.beta.default_port | Should -Be 3457
            $config.servers.alpha.command | Should -Be @('agency', 'mcp', 'alpha', '--port', '{port}', '--transport', 'http')
            $config.servers.alpha.description | Should -BeExactly 'Alpha MCP server'
            $config.servers.alpha.configuration_options | Should -Be @('--tenant <TENANT>')
            $config.servers.alpha.added_by | Should -BeExactly 'SyncAgencyServers'
            { [DateTimeOffset]::Parse($config.servers.alpha.added_at_utc) } | Should -Not -Throw
            Test-Path -LiteralPath ($script:BuildUnitConfigPath + '.tmp') | Should -BeFalse
        }

        It 'keeps an enabled duplicate port and reallocates disabled collisions before adding servers' {
            & $script:WriteBuildUnitConfig @{ servers = @{
                active = @{ command = @('node'); default_port = 4000; enabled = $true }
                inactive = @{ command = @('node'); default_port = 4000; enabled = $false }
                highest = @{ command = @('node'); default_port = 4003; enabled = $false }
            } }
            $helpText = "Commands:`n  alpha  Alpha MCP server`nOptions:"

            $changes = @(Sync-AgencyMcpServers -ConfigPath $script:BuildUnitConfigPath -HelpText $helpText)

            $config = Get-Content -Raw -LiteralPath $script:BuildUnitConfigPath | ConvertFrom-Json -AsHashtable
            $config.servers.active.default_port | Should -Be 4000
            $config.servers.inactive.default_port | Should -Be 4004
            $config.servers.alpha.default_port | Should -Be 4005
            @($changes.Action) | Should -Be @('Reassigned', 'Added')
            $changes[0].Name | Should -BeExactly 'inactive'
            $changes[0].OldPort | Should -Be 4000
            @($config.servers.Values.default_port | Sort-Object -Unique).Count | Should -Be 4
        }

        It 'recognizes existing keys, executable aliases, and Agency subcommands and leaves repeated sync byte-identical' {
            & $script:WriteBuildUnitConfig @{ servers = @{
                alpha = @{ command = @('node'); default_port = 3500 }
                alias = @{ command = @('node'); exe_name = 'beta'; default_port = 3501 }
                other = @{ command = @('agency', 'mcp', 'gamma'); default_port = 3502 }
            } }
            $helpText = $script:AgencyUnitHelp.Replace('Options:', "  gamma        Gamma MCP server`nOptions:")
            $original = Get-Content -Raw -LiteralPath $script:BuildUnitConfigPath

            @(Sync-AgencyMcpServers -ConfigPath $script:BuildUnitConfigPath -HelpText $helpText).Count | Should -Be 0
            @(Sync-AgencyMcpServers -ConfigPath $script:BuildUnitConfigPath -HelpText $helpText).Count | Should -Be 0

            Get-Content -Raw -LiteralPath $script:BuildUnitConfigPath | Should -BeExactly $original
            Test-Path -LiteralPath ($script:BuildUnitConfigPath + '.tmp') | Should -BeFalse
        }

        It 'rejects an absent config or missing servers object without creating or rewriting a file' {
            { Sync-AgencyMcpServers -ConfigPath $script:BuildUnitConfigPath -HelpText $script:AgencyUnitHelp } | Should -Throw
            Test-Path -LiteralPath $script:BuildUnitConfigPath | Should -BeFalse
            & $script:WriteBuildUnitConfig @{}
            $original = Get-Content -Raw -LiteralPath $script:BuildUnitConfigPath

            { Sync-AgencyMcpServers -ConfigPath $script:BuildUnitConfigPath -HelpText $script:AgencyUnitHelp } |
                Should -Throw '*Source config does not contain a servers object*'

            Get-Content -Raw -LiteralPath $script:BuildUnitConfigPath | Should -BeExactly $original
        }
    }
}

# Keep initializer definitions in their own scope. Never execute its installation body.
Describe 'Initializer source unit behavior' {
    BeforeAll {
        # A local fail-closed stub lets Pester mock installation without requiring
        # PowerShellGet to be installed or permitting module auto-loading.
        function Install-Module {
            [CmdletBinding()]
            param(
                [string]$Name,
                [version]$RequiredVersion,
                [version]$MinimumVersion,
                [string]$Scope,
                [switch]$Force,
                [switch]$AllowClobber
            )
            throw 'A real module installer must never run in unit tests.'
        }
        $script:UnitInitRepoRoot = Split-Path -Parent $PSScriptRoot
        $script:InitUnitRoot = Join-Path $script:UnitInitRepoRoot ('.test-tmp/unit-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $script:InitUnitRoot | Out-Null
        $tokens = $null
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $script:UnitInitRepoRoot 'init.ps1'), [ref]$tokens, [ref]$parseErrors
        )
        if ($parseErrors.Count -gt 0) {
            throw ($parseErrors | Out-String)
        }
        $definitions = $ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst]
        }, $false)
        foreach ($definition in $definitions) {
            Set-Item -Path ('Function:' + $definition.Name) -Value $definition.Body.GetScriptBlock()
        }
    }

    BeforeEach {
        $script:InitUnitCaseRoot = Join-Path $script:InitUnitRoot ([Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $script:InitUnitCaseRoot | Out-Null
        $script:InitUnitConfigPath = Join-Path $script:InitUnitCaseRoot 'servers.json'
        Mock Write-InitLog {}
        Mock Install-Module { throw 'A real module installer must never run in unit tests.' }
        Mock Get-Module { return @() } -ParameterFilter { $Name -eq 'Unit.Dependency' }
    }

    AfterAll {
        if (Test-Path -LiteralPath $script:InitUnitRoot) {
            Remove-Item -Recurse -Force -LiteralPath $script:InitUnitRoot
        }
    }

    Context 'Required module versions (mocked installer)' {
        It 'accepts an installed exact version even when a newer version is also available' {
            Mock Get-Module {
                @('9.0.0', '5.0.4', '4.0.0') | ForEach-Object { [pscustomobject]@{ Version = [version]$_ } }
            } -ParameterFilter { $Name -eq 'Unit.Dependency' }

            Install-RequiredModule -Name 'Unit.Dependency' -RequiredVersion ([version]'5.0.4')

            Should -Invoke Install-Module -Times 0 -Exactly
            Should -Invoke Write-InitLog -Times 1 -Exactly -ParameterFilter { $Message -eq 'Unit.Dependency 5.0.4 is available.' }
        }

        It 'installs a missing exact version rather than accepting a newer installed version' {
            $script:UnitModuleLookups = 0
            Mock Get-Module {
                $script:UnitModuleLookups++
                [pscustomobject]@{ Version = [version]'9.0.0' }
                if ($script:UnitModuleLookups -gt 1) {
                    [pscustomobject]@{ Version = [version]'5.0.4' }
                }
            } -ParameterFilter { $Name -eq 'Unit.Dependency' }
            Mock Install-Module {}

            Install-RequiredModule -Name 'Unit.Dependency' -RequiredVersion ([version]'5.0.4')

            Should -Invoke Install-Module -Times 1 -Exactly -ParameterFilter {
                $Name -eq 'Unit.Dependency' -and $RequiredVersion -eq [version]'5.0.4' -and
                $Scope -eq 'CurrentUser' -and $Force -and $AllowClobber -and $null -eq $MinimumVersion
            }
            Should -Invoke Write-InitLog -Times 1 -Exactly -ParameterFilter { $Message -eq 'Unit.Dependency 5.0.4 is available.' }
        }

        It 'selects the highest installed version satisfying the minimum without reinstalling' {
            Mock Get-Module {
                @('2.0.0', '4.0.0', '3.0.0') | ForEach-Object { [pscustomobject]@{ Version = [version]$_ } }
            } -ParameterFilter { $Name -eq 'Unit.Dependency' }

            Install-RequiredModule -Name 'Unit.Dependency' -MinimumVersion ([version]'3.0.0')

            Should -Invoke Install-Module -Times 0 -Exactly
            Should -Invoke Write-InitLog -Times 1 -Exactly -ParameterFilter { $Message -eq 'Unit.Dependency 4.0.0 is available.' }
        }

        It 'passes minimum-version installation flags and verifies the installed module' {
            $script:UnitModuleLookups = 0
            Mock Get-Module {
                $script:UnitModuleLookups++
                [pscustomobject]@{ Version = [version]'1.0.0' }
                if ($script:UnitModuleLookups -gt 1) {
                    [pscustomobject]@{ Version = [version]'3.1.0' }
                }
            } -ParameterFilter { $Name -eq 'Unit.Dependency' }
            Mock Install-Module {}

            Install-RequiredModule -Name 'Unit.Dependency' -MinimumVersion ([version]'3.0.0')

            Should -Invoke Install-Module -Times 1 -Exactly -ParameterFilter {
                $Name -eq 'Unit.Dependency' -and $MinimumVersion -eq [version]'3.0.0' -and
                $Scope -eq 'CurrentUser' -and $Force -and $AllowClobber -and $null -eq $RequiredVersion
            }
            Should -Invoke Write-InitLog -Times 1 -Exactly -ParameterFilter { $Message -eq 'Unit.Dependency 3.1.0 is available.' }
        }

        It 'reports an installer that returns without providing the required exact version' {
            Mock Get-Module { [pscustomobject]@{ Version = [version]'9.0.0' } } -ParameterFilter { $Name -eq 'Unit.Dependency' }
            Mock Install-Module {}

            { Install-RequiredModule -Name 'Unit.Dependency' -RequiredVersion ([version]'5.0.4') } |
                Should -Throw '*Unable to install required module: Unit.Dependency*'

            Should -Invoke Install-Module -Times 1 -Exactly
        }

        It 'rejects a still-below-minimum module after installation instead of reporting success' {
            Mock Get-Module { [pscustomobject]@{ Version = [version]'1.0.0' } } -ParameterFilter { $Name -eq 'Unit.Dependency' }
            Mock Install-Module {}

            { Install-RequiredModule -Name 'Unit.Dependency' -MinimumVersion ([version]'3.0.0') } |
                Should -Throw '*Unable to install required module: Unit.Dependency*'

            Should -Invoke Install-Module -Times 1 -Exactly
        }
    }

    Context 'Executable discovery with all platform lookups mocked' {
        BeforeEach {
            Mock Get-Command { return $null } -ParameterFilter { $Name -in @('mcp-cli', 'agency') }
            Mock Test-Path { return $false }
            Mock Resolve-Path { throw 'Unexpected executable resolution in an offline unit test.' }
            Mock Get-Content { throw 'Executable discovery must not read a real configuration.' }
        }

        It 'returns MCP CLI from PATH before checking home or local configuration' {
            $script:UnitMcpExecutable = Join-Path $script:InitUnitCaseRoot 'path/mcp-cli.exe'
            Mock Get-Command { [pscustomobject]@{ Source = $script:UnitMcpExecutable } } -ParameterFilter { $Name -eq 'mcp-cli' }

            Find-McpCli | Should -BeExactly $script:UnitMcpExecutable

            Should -Invoke Test-Path -Times 0 -Exactly
            Should -Invoke Get-Content -Times 0 -Exactly
        }

        It 'uses Bun executable candidates in order without reading config when a candidate exists' {
            $script:UnitBunExe = Join-Path $HOME '.bun/bin/mcp-cli.exe'
            $script:UnitBunNoExtension = Join-Path $HOME '.bun/bin/mcp-cli'
            $script:UnitExistingBunCandidate = $script:UnitBunExe
            Mock Test-Path { $Path -eq $script:UnitExistingBunCandidate }
            Mock Resolve-Path { [pscustomobject]@{ Path = $Path } }

            Find-McpCli | Should -BeExactly $script:UnitBunExe
            $script:UnitExistingBunCandidate = $script:UnitBunNoExtension
            Find-McpCli | Should -BeExactly $script:UnitBunNoExtension

            Should -Invoke Get-Content -Times 0 -Exactly
        }

        It 'uses and expands a configured home-relative MCP CLI path without reading a real servers.json' {
            $script:UnitDefaultSourceConfig = Join-Path $script:UnitInitRepoRoot 'servers.json'
            $script:UnitConfiguredMcpExecutable = Join-Path $HOME 'unit-tools/mcp-cli.exe'
            Mock Test-Path { $Path -in @($script:UnitDefaultSourceConfig, $script:UnitConfiguredMcpExecutable) }
            Mock Get-Content { '{"mcp_cli_path":"~/unit-tools/mcp-cli.exe"}' }
            Mock Resolve-Path { [pscustomobject]@{ Path = $Path } }

            Find-McpCli | Should -BeExactly $script:UnitConfiguredMcpExecutable

            Should -Invoke Get-Content -Times 1 -Exactly -ParameterFilter { $Path -eq $script:UnitDefaultSourceConfig -and $Raw }
        }

        It 'returns null when MCP CLI is absent and does not resolve the configured command-name sentinel as a path' {
            Find-McpCli | Should -BeNullOrEmpty
            $script:UnitDefaultSourceConfig = Join-Path $script:UnitInitRepoRoot 'servers.json'
            Mock Test-Path { $Path -eq $script:UnitDefaultSourceConfig }
            Mock Get-Content { '{"mcp_cli_path":"mcp-cli"}' }

            Find-McpCli | Should -BeNullOrEmpty

            Should -Invoke Resolve-Path -Times 0 -Exactly
            Should -Invoke Test-Path -Times 0 -Exactly -ParameterFilter { $Path -eq 'mcp-cli' }
        }

        It 'returns Agency from PATH before probing installation directories' {
            $script:UnitAgencyExecutable = Join-Path $script:InitUnitCaseRoot 'path/agency.exe'
            Mock Get-Command { [pscustomobject]@{ Source = $script:UnitAgencyExecutable } } -ParameterFilter { $Name -eq 'agency' }

            Find-Agency | Should -BeExactly $script:UnitAgencyExecutable

            Should -Invoke Test-Path -Times 0 -Exactly
        }

        It 'uses the user Agency installation before the system fallback, with both paths mocked' {
            $script:UnitUserAgency = Join-Path $env:APPDATA 'agency/CurrentVersion/agency.exe'
            $script:UnitSystemAgency = 'C:\.Tools\agency\CurrentVersion\agency.exe'
            $script:UnitExistingAgencyCandidate = $script:UnitUserAgency
            Mock Test-Path { $Path -eq $script:UnitExistingAgencyCandidate }
            Mock Resolve-Path { [pscustomobject]@{ Path = $Path } }

            Find-Agency | Should -BeExactly $script:UnitUserAgency
            $script:UnitExistingAgencyCandidate = $script:UnitSystemAgency
            Find-Agency | Should -BeExactly $script:UnitSystemAgency
        }

        It 'returns null when Agency is absent without invoking an installer or a CLI' {
            Find-Agency | Should -BeNullOrEmpty

            Should -Invoke Resolve-Path -Times 0 -Exactly
            Should -Invoke Install-Module -Times 0 -Exactly
            Should -Invoke Get-Content -Times 0 -Exactly
        }
    }

    Context 'Local configuration path boundaries' {
        It 'abbreviates only actual home descendants, not the home root, prefix siblings, or traversals outside it' {
            $child = Join-Path $HOME 'unit-tools/mcp-cli.exe'
            $relative = [IO.Path]::GetRelativePath([IO.Path]::GetFullPath($HOME), [IO.Path]::GetFullPath($child))
            ConvertTo-LocalConfigPath -Path $child | Should -BeExactly ('~\' + $relative)
            ConvertTo-LocalConfigPath -Path $HOME | Should -BeExactly ([IO.Path]::GetFullPath($HOME))
            $sibling = Join-Path ($HOME + '-other') 'mcp-cli.exe'
            ConvertTo-LocalConfigPath -Path $sibling | Should -BeExactly ([IO.Path]::GetFullPath($sibling))
            $escaped = Join-Path $HOME '../outside-unit/mcp-cli.exe'
            ConvertTo-LocalConfigPath -Path $escaped | Should -BeExactly ([IO.Path]::GetFullPath($escaped))
        }

        It 'updates only an explicit temporary config, preserves an omitted Agency path, and is byte-idempotent' {
            @{ mcp_cli_path = 'old'; agency_path = 'preserve-agency'; servers = @{} } |
                ConvertTo-Json -Depth 100 | Set-Content -Encoding UTF8 -LiteralPath $script:InitUnitConfigPath
            $mcpPath = Join-Path $script:InitUnitCaseRoot 'tools/mcp-cli.exe'

            Set-LocalToolPaths -McpCliPath $mcpPath -ConfigPath $script:InitUnitConfigPath

            $updated = Get-Content -Raw -LiteralPath $script:InitUnitConfigPath | ConvertFrom-Json -AsHashtable
            $updated.mcp_cli_path | Should -BeExactly ([IO.Path]::GetFullPath($mcpPath))
            $updated.agency_path | Should -BeExactly 'preserve-agency'
            $original = Get-Content -Raw -LiteralPath $script:InitUnitConfigPath
            Set-LocalToolPaths -McpCliPath $mcpPath -ConfigPath $script:InitUnitConfigPath
            Get-Content -Raw -LiteralPath $script:InitUnitConfigPath | Should -BeExactly $original
            Test-Path -LiteralPath ($script:InitUnitConfigPath + '.tmp') | Should -BeFalse
        }

        It 'does not create an absent explicit config or attempt dependency installation' {
            Set-LocalToolPaths -McpCliPath (Join-Path $script:InitUnitCaseRoot 'mcp-cli.exe') -ConfigPath $script:InitUnitConfigPath

            Test-Path -LiteralPath $script:InitUnitConfigPath | Should -BeFalse
            Test-Path -LiteralPath ($script:InitUnitConfigPath + '.tmp') | Should -BeFalse
            Should -Invoke Install-Module -Times 0 -Exactly
        }
    }
}

Describe 'Build executable resolver boundaries (offline)' {
    BeforeAll {
        $script:ResolverRepo = Split-Path -Parent $PSScriptRoot
        $tokens = $null
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $script:ResolverRepo 'psakefile.ps1'), [ref]$tokens, [ref]$parseErrors
        )
        if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
        foreach ($definition in @($ast.EndBlock.Statements | Where-Object {
            $_ -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $_.Name -in @('Resolve-UserPath', 'Write-BuildLog', 'Resolve-McpCliPath', 'Resolve-AgencyPath')
        })) {
            Set-Item -Path ('Function:' + $definition.Name) -Value $definition.Body.GetScriptBlock()
        }
    }
    BeforeEach {
        Mock Get-Command { return $null } -ParameterFilter { $Name -in @('mcp-cli', 'custom-cli', 'agency') }
        Mock Test-Path { return $false }
        Mock Resolve-Path { [pscustomobject]@{ Path = [IO.Path]::GetFullPath($Path) } }
        Mock Write-BuildLog { }
    }
    It 'resolves a requested CLI command name before searching defaults' {
        Mock Get-Command { [pscustomobject]@{ Source = 'fixture-custom-cli' } } -ParameterFilter { $Name -eq 'custom-cli' }
        Resolve-McpCliPath -RequestedPath 'custom-cli' | Should -Be 'fixture-custom-cli'
        Should -Invoke Get-Command -Times 0 -Exactly -ParameterFilter { $Name -eq 'mcp-cli' }
    }
    It 'resolves an explicit existing CLI path without a PATH lookup' {
        $script:ResolverExplicit = Join-Path $script:ResolverRepo '.test-tmp/resolver/mcp-cli.exe'
        Mock Test-Path { $Path -eq $script:ResolverExplicit }
        Resolve-McpCliPath -RequestedPath $script:ResolverExplicit | Should -Be ([IO.Path]::GetFullPath($script:ResolverExplicit))
        Should -Invoke Get-Command -Times 0 -Exactly -ParameterFilter { $Name -eq 'mcp-cli' }
    }
    It 'falls back from a missing CLI path to PATH with a warning' {
        Mock Get-Command { [pscustomobject]@{ Source = 'fixture-path-cli' } } -ParameterFilter { $Name -eq 'mcp-cli' }
        Resolve-McpCliPath -RequestedPath './missing-cli' | Should -Be 'fixture-path-cli'
        Should -Invoke Write-BuildLog -Times 1 -Exactly -ParameterFilter { $Label -eq 'WARN' }
    }
    It 'uses an installed Bun candidate when no command can be resolved' {
        $script:ResolverCandidate = Join-Path $HOME '.bun/bin/mcp-cli.exe'
        Mock Test-Path { $Path -eq $script:ResolverCandidate }
        Resolve-McpCliPath | Should -Be ([IO.Path]::GetFullPath($script:ResolverCandidate))
    }
    It 'reports missing MCP CLI instead of returning an unusable executable' {
        { Resolve-McpCliPath -RequestedPath 'custom-cli' } | Should -Throw '*mcp-cli was not found*'
    }
    It 'resolves a requested home-relative Agency path without checking PATH' {
        $script:ResolverCandidate = Join-Path $HOME 'fixture-tools/agency.exe'
        Mock Test-Path { $Path -eq $script:ResolverCandidate }
        Resolve-AgencyPath -RequestedPath '~/fixture-tools/agency.exe' | Should -Be ([IO.Path]::GetFullPath($script:ResolverCandidate))
        Should -Invoke Get-Command -Times 0 -Exactly -ParameterFilter { $Name -eq 'agency' }
    }
    It 'falls back from stale Agency configuration to PATH with a warning' {
        Mock Get-Command { [pscustomobject]@{ Source = 'fixture-agency' } } -ParameterFilter { $Name -eq 'agency' }
        Resolve-AgencyPath -RequestedPath './missing-agency' | Should -Be 'fixture-agency'
        Should -Invoke Write-BuildLog -Times 1 -Exactly -ParameterFilter { $Label -eq 'WARN' }
    }
    It 'uses a standard Agency installation directory when PATH has no command' {
        $script:ResolverCandidate = Join-Path $env:APPDATA 'agency/CurrentVersion/agency.exe'
        Mock Test-Path { $Path -eq $script:ResolverCandidate }
        Resolve-AgencyPath | Should -Be ([IO.Path]::GetFullPath($script:ResolverCandidate))
    }
    It 'reports missing Agency without starting an installer or service' {
        { Resolve-AgencyPath } | Should -Throw '*Agency was not found*'
    }
    It 'expands home roots and preserves empty and ordinary user paths' {
        Resolve-UserPath -Path '~' | Should -Be $HOME
        Resolve-UserPath -Path '' | Should -Be ''
        Resolve-UserPath -Path 'plain-relative' | Should -Be 'plain-relative'
    }
}
