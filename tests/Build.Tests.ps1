Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Describe 'MCP CLI build pipeline' {
    BeforeAll {
        $script:RepoRoot = Split-Path -Parent $PSScriptRoot
        $script:PsakeFile = Join-Path $script:RepoRoot 'psakefile.ps1'
        $script:TempBase = Join-Path $script:RepoRoot '.test-tmp'
        $script:SuiteRoot = Join-Path $script:TempBase ("suite-{0}" -f [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $script:SuiteRoot | Out-Null
        $script:ServersConfig = Join-Path $script:SuiteRoot 'servers.json'
        Copy-Item -Path (Join-Path $PSScriptRoot 'fixtures/servers.json') -Destination $script:ServersConfig
        $script:InvokeBuildTask = {
            param(
                [string]$Task,
                [hashtable]$Properties = @{},
                [string]$BuildFile = $script:PsakeFile
            )

            $taskRoot = Join-Path $script:SuiteRoot ([Guid]::NewGuid().ToString('N'))
            $safeProperties = @{
                SourceConfig = $script:ServersConfig
                OutputDir = (Join-Path $taskRoot 'dist')
                McpConfigPath = (Join-Path $taskRoot 'mcp_servers.json')
                SourceSkillsDir = (Join-Path $taskRoot 'dist/skills')
                TargetSkillsDir = (Join-Path $taskRoot 'deployed-skills')
                PackagePath = (Join-Path $taskRoot 'setup.zip')
            }
            foreach ($entry in $Properties.GetEnumerator()) {
                $safeProperties[$entry.Key] = $entry.Value
            }
            $testPrefix = [IO.Path]::GetFullPath($script:TempBase).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
            foreach ($name in @('SourceConfig', 'OutputDir', 'McpConfigPath', 'SourceSkillsDir', 'TargetSkillsDir', 'PackagePath')) {
                if (-not [IO.Path]::GetFullPath([string]$safeProperties[$name]).StartsWith($testPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Test path must stay under .test-tmp: $name"
                }
            }

            $command = @'
Import-Module psake -RequiredVersion 5.0.4 -Force
$properties = ConvertFrom-Json -InputObject $env:MCP_CLI_PSAKE_PROPERTIES -AsHashtable
Invoke-psake -BuildFile $env:MCP_CLI_PSAKE_FILE -TaskList $env:MCP_CLI_PSAKE_TASK -Properties $properties -NoLogo -NoTimeReport
if (-not $psake.build_success) {
    exit 1
}
'@
            try {
                $env:MCP_CLI_PSAKE_FILE = $BuildFile
                $env:MCP_CLI_PSAKE_TASK = $Task
                $env:MCP_CLI_PSAKE_PROPERTIES = $safeProperties | ConvertTo-Json -Compress
                $output = & pwsh -NoLogo -NoProfile -Command $command 2>&1
                [pscustomobject]@{
                    ExitCode = $LASTEXITCODE
                    Output = $output
                }
            }
            finally {
                Remove-Item Env:MCP_CLI_PSAKE_FILE, Env:MCP_CLI_PSAKE_TASK, Env:MCP_CLI_PSAKE_PROPERTIES -ErrorAction SilentlyContinue
            }
        }
        $script:RemoveTestRoot = {
            param([string]$Path)

            if (-not (Test-Path $Path)) {
                return
            }

            try {
                Get-ChildItem -Force -Recurse -Path $Path -ErrorAction SilentlyContinue |
                    ForEach-Object { $_.Attributes = 'Normal' }
                Remove-Item -Recurse -Force -Path $Path -ErrorAction Stop
            }
            catch {
                Write-Warning "Could not remove test directory '$Path': $($_.Exception.Message)"
            }
        }

        Test-Path $script:PsakeFile | Should -BeTrue
        Test-Path $script:ServersConfig | Should -BeTrue
    }

    AfterAll {
        & $script:RemoveTestRoot $script:SuiteRoot
    }

    It 'defines the expected psake workflow tasks' {
        $taskNames = @(Get-PSakeScriptTasks -BuildFile $script:PsakeFile | ForEach-Object Name)

        $taskNames | Should -Contain 'Config'
        $taskNames | Should -Contain 'Discover'
        $taskNames | Should -Contain 'GenerateSkills'
        $taskNames | Should -Contain 'Build'
        $taskNames | Should -Contain 'Test'
        $taskNames | Should -Contain 'Deploy'
        $taskNames | Should -Contain 'SyncAgencyServers'
        $taskNames | Should -Contain 'Package'
    }

    It 'packages only public files (seed private files: <SeedPrivateFiles>)' -ForEach @(
        @{ SeedPrivateFiles = $false }
        @{ SeedPrivateFiles = $true }
    ) {
        $tempRoot = Join-Path $script:TempBase ("mcp-cli-pester-{0}" -f [Guid]::NewGuid().ToString('N'))
        $sourceRoot = Join-Path $tempRoot 'source'
        $packagePath = Join-Path $tempRoot 'mcp-cli-setup.zip'
        $publicFiles = @(
            '.gitignore', '.gitattributes', 'init.ps1', 'psakefile.ps1', 'README.md', 'docs/creating-skills.md', 'servers.example.json',
            'tests/Build.Tests.ps1', 'tests/Init.Tests.ps1', 'tests/fixtures/servers.json'
        )
        $privateFiles = @(
            'servers.json', 'servers.local.json', 'config.json', 'config/private.json',
            '.env', '.env.local', '.credentials', '.auth/token.json', '.ssh/id_ed25519',
            'credentials.json', 'tokens.json', '.git', 'backup.zip', 'backup.tar.gz', 'backup.7z',
            'tests/.env', 'tests/private.ps1', 'tests/.auth/helper.ps1',
            'tests/fixtures/servers.private.json', 'tests/fixtures/.tokens.json',
            'dist/generated.ps1', '.test-tmp/private.txt'
        )

        try {
            foreach ($relativePath in $publicFiles) {
                $destination = Join-Path $sourceRoot $relativePath
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
                Copy-Item -LiteralPath (Join-Path $script:RepoRoot $relativePath) -Destination $destination
            }
            if ($SeedPrivateFiles) {
                foreach ($relativePath in $privateFiles) {
                    $destination = Join-Path $sourceRoot $relativePath
                    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
                    Set-Content -LiteralPath $destination -Value 'FAKE-PRIVATE-DATA-NOT-A-SECRET'
                }
                Set-Content -LiteralPath (Join-Path $sourceRoot '.git') -Value 'gitdir: /fake/private/worktree'
            }
            else {
                Test-Path (Join-Path $sourceRoot 'servers.json') | Should -BeFalse
            }

            $result = & $script:InvokeBuildTask Package @{ PackagePath = $packagePath } -BuildFile (Join-Path $sourceRoot 'psakefile.ps1')
            $result.ExitCode | Should -Be 0
            Test-Path $packagePath | Should -BeTrue

            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
            try {
                $entries = @($archive.Entries.FullName)
                $entries.Count | Should -Be $publicFiles.Count
                foreach ($relativePath in $publicFiles) {
                    $entries | Should -Contain $relativePath
                }
                foreach ($relativePath in $privateFiles) {
                    $entries | Should -Not -Contain $relativePath
                }
                @($entries | Where-Object { $_ -match '^(?:dist/|\.git(?:/|$)|\.test-tmp/)' }).Count | Should -Be 0
                @($entries | Where-Object { $_ -match '\.(?:zip|7z|tar(?:\.gz)?)$' }).Count | Should -Be 0
            }
            finally {
                $archive.Dispose()
            }
        }
        finally {
            & $script:RemoveTestRoot $tempRoot
        }
    }

    It 'adds missing Agency servers with unique ports and excludes utility commands' {
        $tempRoot = Join-Path $script:TempBase ("mcp-cli-pester-{0}" -f [Guid]::NewGuid().ToString('N'))
        $configPath = Join-Path $tempRoot 'servers.json'
        $helpText = @'
Mcp tools

Commands:
  local        MCP proxy to a local MCP server
  npx          Stdio MCP proxy via npx
  remote       MCP proxy to a remote server
    ado          Azure DevOps MCP server
  calendar     Microsoft Calendar MCP server
  new-server   A newly available MCP server
  other-one    Another available MCP server
  help         Print help

Options:
  -h, --help   Print help
'@
      $serverHelpJson = @{
        'new-server' = @'
Usage: agency.exe mcp new-server [OPTIONS]

Options:
    --tenant <TENANT>
    --transport <TRANSPORT>
    --port <PORT>
'@
        'other-one' = @'
Usage: agency.exe mcp other-one [OPTIONS]

Options:
    --connection-string <CONNECTION_STRING>
    --entra-client-id <ENTRA_CLIENT_ID>
'@
      } | ConvertTo-Json -Compress

        try {
            New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
            Copy-Item -Path $script:ServersConfig -Destination $configPath

            $result = & $script:InvokeBuildTask SyncAgencyServers @{
                SourceConfig = $configPath
                AgencyHelpText = $helpText
                AgencyServerHelpJson = $serverHelpJson
            }
            $result.ExitCode | Should -Be 0

            $config = Get-Content -Raw -Path $configPath | ConvertFrom-Json -Depth 100
            $config.servers.PSObject.Properties.Name | Should -Contain 'calendar'
            $config.servers.PSObject.Properties.Name | Should -Contain 'new-server'
            $config.servers.PSObject.Properties.Name | Should -Contain 'other-one'
            $config.servers.PSObject.Properties.Name | Should -Not -Contain 'local'
            $config.servers.PSObject.Properties.Name | Should -Not -Contain 'npx'
            $config.servers.PSObject.Properties.Name | Should -Not -Contain 'remote'
            $config.servers.PSObject.Properties.Name | Should -Not -Contain 'help'
            $config.servers.PSObject.Properties.Name | Should -Not -Contain 'ado'
            @($config.servers.calendar.command) | Should -Be @('agency', 'mcp', 'calendar')
            @($config.servers.'new-server'.command) | Should -Be @('agency', 'mcp', 'new-server', '--port', '{port}', '--transport', 'http')
            $config.servers.calendar.default_port | Should -Be 3456
            $config.servers.graph.default_port | Should -Be 3457
            $config.servers.'new-server'.default_port | Should -Be 3458
            $config.servers.'other-one'.default_port | Should -Be 3459
            $config.servers.'new-server'.enabled | Should -BeFalse
            $config.servers.'new-server'.description | Should -Be 'A newly available MCP server'
            $config.servers.'new-server'.added_by | Should -Be 'SyncAgencyServers'
            $config.servers.'new-server'.added_at_utc | Should -Not -BeNullOrEmpty
            @($config.servers.'new-server'.configuration_options) | Should -Be @('--tenant <TENANT>')
            @($config.servers.'other-one'.configuration_options) | Should -Be @('--connection-string <CONNECTION_STRING>')
            $allPorts = @($config.servers.PSObject.Properties.Value | Where-Object { $null -ne $_.default_port } | ForEach-Object { [int]$_.default_port })
            @($allPorts | Group-Object | Where-Object Count -gt 1).Count | Should -Be 0

            $firstSyncText = Get-Content -Raw -Path $configPath
            $secondResult = & $script:InvokeBuildTask SyncAgencyServers @{
                SourceConfig = $configPath
                AgencyHelpText = $helpText
                AgencyServerHelpJson = $serverHelpJson
            }
            $secondResult.ExitCode | Should -Be 0
            (Get-Content -Raw -Path $configPath) | Should -BeExactly $firstSyncText
        }
        finally {
            & $script:RemoveTestRoot $tempRoot
        }
    }

    It 'generates mcp-cli config entries for enabled selected servers' {
        $tempRoot = Join-Path $script:TempBase ("mcp-cli-pester-{0}" -f [Guid]::NewGuid().ToString('N'))
        $configPath = Join-Path $tempRoot 'mcp_servers.json'

        try {
            New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null

            $result = & $script:InvokeBuildTask Config @{
                SourceConfig = $script:ServersConfig
                McpConfigPath = $configPath
            }
            $result.ExitCode | Should -Be 0

            Test-Path $configPath | Should -BeTrue
            $config = Get-Content -Raw -Path $configPath | ConvertFrom-Json -Depth 100
            $config.mcpServers.calendar.command | Should -Be 'agency'
            @($config.mcpServers.calendar.args) | Should -Be @('--verbosity', 'error', 'mcp', 'calendar')
            $config.mcpServers.'remote-demo'.command | Should -Be 'agency'
            @($config.mcpServers.'remote-demo'.args)[0] | Should -Be '--verbosity'
            @($config.mcpServers.'remote-demo'.args) | Should -Be @('--verbosity', 'error', 'mcp', 'remote', 'remote-demo')
            $config.mcpServers.'web-search'.command | Should -Be 'npx'
            @($config.mcpServers.'web-search'.args) | Should -Contain '@zhafron/mcp-web-search'
            $config.mcpServers.PSObject.Properties.Name | Should -Not -Contain 'graph'
        }
        finally {
            & $script:RemoveTestRoot $tempRoot
        }
    }

    It 'builds selected skill packages with skipped discovery' {
        $tempRoot = Join-Path $script:TempBase ("mcp-cli-pester-{0}" -f [Guid]::NewGuid().ToString('N'))
        $outputDir = Join-Path $tempRoot 'dist'
        $configPath = Join-Path $tempRoot 'mcp_servers.json'

        try {
            New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null

            $result = & $script:InvokeBuildTask Build @{
                SourceConfig = $script:ServersConfig
                OutputDir = $outputDir
                McpConfigPath = $configPath
                SelectedServerNames = 'calendar|web-search'
                SkipDiscovery = $true
            }
            $result.ExitCode | Should -Be 0

            Test-Path (Join-Path $outputDir 'build-summary.json') | Should -BeTrue
            Test-Path (Join-Path $outputDir 'skills/calendar/SKILL.md') | Should -BeTrue
            Test-Path (Join-Path $outputDir 'skills/calendar/references/tool-info.md') | Should -BeTrue
            Test-Path (Join-Path $outputDir 'skills/calendar/scripts/Invoke-Tool.ps1') | Should -BeTrue
            Test-Path (Join-Path $outputDir 'skills/calendar/scripts/Invoke-McpTool.ps1') | Should -BeFalse
            Test-Path (Join-Path $outputDir 'skills/web-search/SKILL.md') | Should -BeTrue
            Test-Path (Join-Path $outputDir 'skills/web-search/scripts/Invoke-Tool.ps1') | Should -BeTrue
            Test-Path (Join-Path $outputDir 'skills/mail/SKILL.md') | Should -BeFalse
            Test-Path (Join-Path $outputDir 'launch-mcp-servers.ps1') | Should -BeFalse
            Test-Path (Join-Path $outputDir 'mcp-servers.yaml') | Should -BeFalse

            $skillText = Get-Content -Raw -Path (Join-Path $outputDir 'skills/calendar/SKILL.md')
            $skillText | Should -Match '^---\r?\n'
            $skillText | Should -Match '(?m)^name:\s*''calendar''\s*$'
            $skillText | Should -Match 'references/custom-instructions.md'
            $skillText | Should -Match 'custom instructions may override guidance in this generated skill'
            $skillText | Should -Match 'scripts/Invoke-Tool.ps1'
            $skillText | Should -Not -Match 'Invoke-McpTool'
            $skillText | Should -Not -Match '(?i)\bmcp\b|mcp-cli'
            $skillText | Should -Not -Match 'references/custom-guidance.md'
            $skillText | Should -Not -Match 'shim'

            $runnerText = Get-Content -Raw -Path (Join-Path $outputDir 'skills/calendar/scripts/Invoke-Tool.ps1')
            $runnerText | Should -Match 'generated by the .*build pipeline'
            $runnerText | Should -Match 'overwritten when the skill package is regenerated'
            $runnerText | Should -Match 'Do not edit this file directly'
            $runnerText | Should -Match '\[string\]\$CliPath'
            $runnerText | Should -Not -Match '(?i)(?:[a-z]:[\\/]Users[\\/]|/(?:home|Users)/)[^\\/\s]+'
            $runnerText | Should -Not -Match '\[string\]\$CliPath\s*=\s*''~[\\/]'

            $toolScripts = @(Get-ChildItem -Path (Join-Path $outputDir 'skills/calendar/scripts') -Filter '*.ps1' -File | Where-Object { $_.Name -ne 'Invoke-Tool.ps1' })
            if ($toolScripts.Count -gt 0) {
                $toolScriptText = Get-Content -Raw -Path $toolScripts[0].FullName
                $toolScriptText | Should -Match 'generated by the .*build pipeline'
                $toolScriptText | Should -Match 'overwritten when the skill package is regenerated'
                $toolScriptText | Should -Match 'Do not edit this file directly'
            }

            $summary = Get-Content -Raw -Path (Join-Path $outputDir 'build-summary.json') | ConvertFrom-Json -Depth 20
            @($summary).Count | Should -Be 2
            @($summary.server) | Should -Contain 'calendar'
            @($summary.server) | Should -Contain 'web-search'
        }
        finally {
            & $script:RemoveTestRoot $tempRoot
        }
    }

    It 'removes stale legacy artifacts from skill packages' {
        $tempRoot = Join-Path $script:TempBase ("mcp-cli-pester-{0}" -f [Guid]::NewGuid().ToString('N'))
        $outputDir = Join-Path $tempRoot 'dist'
        $configPath = Join-Path $tempRoot 'mcp_servers.json'
        $staleSkillDir = Join-Path $outputDir 'skills/calendar'

        try {
            New-Item -ItemType Directory -Force -Path (Join-Path $staleSkillDir 'scripts') | Out-Null
            Set-Content -Encoding UTF8 -Path (Join-Path $staleSkillDir 'tool_policy.json') -Value '{}'
            Set-Content -Encoding UTF8 -Path (Join-Path $staleSkillDir 'scripts/stale.ps1') -Value '# stale'

            $result = & $script:InvokeBuildTask Build @{
                SourceConfig = $script:ServersConfig
                OutputDir = $outputDir
                McpConfigPath = $configPath
                SelectedServerNames = 'calendar'
                SkipDiscovery = $true
            }
            $result.ExitCode | Should -Be 0

            Test-Path (Join-Path $staleSkillDir 'tool_policy.json') | Should -BeFalse
            Test-Path (Join-Path $staleSkillDir 'scripts') | Should -BeTrue
            Test-Path (Join-Path $staleSkillDir 'scripts/Invoke-Tool.ps1') | Should -BeTrue
            Test-Path (Join-Path $staleSkillDir 'scripts/Invoke-McpTool.ps1') | Should -BeFalse
            Test-Path (Join-Path $staleSkillDir 'scripts/stale.ps1') | Should -BeFalse
        }
        finally {
            & $script:RemoveTestRoot $tempRoot
        }
    }

    It 'deploys skills while preserving custom instructions only' {
        $tempRoot = Join-Path $script:TempBase ("mcp-cli-pester-{0}" -f [Guid]::NewGuid().ToString('N'))
        $outputDir = Join-Path $tempRoot 'dist'
        $configPath = Join-Path $tempRoot 'mcp_servers.json'
        $targetDir = Join-Path $tempRoot 'botnexus-skills'
        $customInstructionsPath = Join-Path $targetDir 'calendar/references/custom-instructions.md'
        $otherSkillPath = Join-Path $targetDir 'unrelated-skill/SKILL.md'

        try {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $customInstructionsPath) | Out-Null
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $otherSkillPath) | Out-Null
            Set-Content -Encoding UTF8 -Path $customInstructionsPath -Value 'keep me'
            Set-Content -Encoding UTF8 -Path (Join-Path $targetDir 'calendar/stale.txt') -Value 'remove me'
            Set-Content -Encoding UTF8 -Path $otherSkillPath -Value '# unrelated skill'
            Set-Content -Encoding UTF8 -Path (Join-Path $targetDir 'unrelated-skill/keep.txt') -Value 'do not touch'

            $buildResult = & $script:InvokeBuildTask Build @{
                SourceConfig = $script:ServersConfig
                OutputDir = $outputDir
                McpConfigPath = $configPath
                SelectedServerNames = 'calendar'
                SkipDiscovery = $true
            }
            $buildResult.ExitCode | Should -Be 0

            $deployResult = & $script:InvokeBuildTask Deploy @{
                SourceSkillsDir = (Join-Path $outputDir 'skills')
                TargetSkillsDir = $targetDir
            }
            $deployResult.ExitCode | Should -Be 0

            Test-Path (Join-Path $targetDir 'calendar/SKILL.md') | Should -BeTrue
            Test-Path (Join-Path $targetDir 'calendar/scripts/Invoke-Tool.ps1') | Should -BeTrue
            Test-Path (Join-Path $targetDir 'calendar/scripts/Invoke-McpTool.ps1') | Should -BeFalse
            Test-Path (Join-Path $targetDir 'calendar/references/tool-info.md') | Should -BeTrue
            Test-Path $customInstructionsPath | Should -BeTrue
            (Get-Content -Raw -Path $customInstructionsPath) | Should -Match 'keep me'
            Test-Path (Join-Path $targetDir 'calendar/stale.txt') | Should -BeFalse
            Test-Path $otherSkillPath | Should -BeTrue
            (Get-Content -Raw -Path $otherSkillPath) | Should -Match '# unrelated skill'
            Test-Path (Join-Path $targetDir 'unrelated-skill/keep.txt') | Should -BeTrue
        }
        finally {
            & $script:RemoveTestRoot $tempRoot
        }
    }

    It 'fails fast on unknown server names' {
        $tempRoot = Join-Path $script:TempBase ("mcp-cli-pester-{0}" -f [Guid]::NewGuid().ToString('N'))

        try {
            New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
            $result = & $script:InvokeBuildTask Build @{
                SourceConfig = $script:ServersConfig
                OutputDir = (Join-Path $tempRoot 'dist')
                McpConfigPath = (Join-Path $tempRoot 'mcp_servers.json')
                SelectedServerNames = 'this-does-not-exist'
                SkipDiscovery = $true
            }
            $result.ExitCode | Should -Not -Be 0
            ($result.Output | Out-String) | Should -Match 'Unknown server'
        }
        finally {
            & $script:RemoveTestRoot $tempRoot
        }
    }
}

Describe 'MCP CLI relative path resolution' {
    BeforeAll {
        $script:PathRepoRoot = Split-Path -Parent $PSScriptRoot
        $tokens = $null
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $script:PathRepoRoot 'psakefile.ps1'), [ref]$tokens, [ref]$parseErrors
        )
        if ($parseErrors.Count -gt 0) { throw ($parseErrors | Out-String) }
        $definitions = $ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -in @('Resolve-UserPath', 'Resolve-BuildPath')
        }, $false)
        . ([scriptblock]::Create(($definitions.Extent.Text -join [Environment]::NewLine)))
    }

    It 'uses the PowerShell filesystem location rather than the process directory' {
        $testRoot = Join-Path $script:PathRepoRoot ('.test-tmp/paths-' + [Guid]::NewGuid().ToString('N'))
        $shellRoot = Join-Path $testRoot 'shell'
        $processRoot = Join-Path $testRoot 'process'
        New-Item -ItemType Directory -Force -Path $shellRoot, $processRoot | Out-Null
        $originalDirectory = [Environment]::CurrentDirectory
        Push-Location -LiteralPath $shellRoot
        try {
            [Environment]::CurrentDirectory = $processRoot
            $expected = Join-Path $shellRoot 'dist/mcp_servers.json'
            Resolve-BuildPath -Path './dist/mcp_servers.json' | Should -Be $expected
            Resolve-BuildPath -Path $expected | Should -Be $expected
            Resolve-BuildPath -Path '~/mcp-example.json' | Should -Be (Join-Path $HOME 'mcp-example.json')
            Test-Path -LiteralPath $expected | Should -BeFalse
        }
        finally {
            [Environment]::CurrentDirectory = $originalDirectory
            Pop-Location
            Remove-Item -Recurse -Force -LiteralPath $testRoot
        }
    }
}

Describe 'MCP CLI offline build regressions' -Tag 'OfflineRegression' {
    BeforeAll {
        $script:OfflineRepoRoot = Split-Path -Parent $PSScriptRoot
        $tokens = $null
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $script:OfflineRepoRoot 'psakefile.ps1'), [ref]$tokens, [ref]$parseErrors
        )
        if ($parseErrors.Count -gt 0) { throw ($parseErrors | Out-String) }

        # Load only selected definitions, never the build file's top-level setup or tasks.
        $functionNames = @(
            'Write-BuildLog', 'Repair-TextEncodingArtifacts', 'Resolve-UserPath',
            'Get-SourceServerMap', 'Get-ConfiguredMcpCliPath', 'Get-ServerTransport',
            'Get-ServerEndpoint', 'Add-ToolPolicy', 'Test-IsAgencyMcpCommand',
            'ConvertTo-McpCliCommandEntry', 'ConvertTo-AgencyMcpCliCommandEntry',
            'ConvertTo-McpCliConfig', 'Get-ToolMetadataFromInfoText',
            'ConvertTo-PowerShellSingleQuoted', 'ConvertTo-PowerShellPathExpression',
            'Get-AutoGeneratedToolScriptHeader', 'Write-McpToolScripts'
        )
        $definitions = @($ast.EndBlock.Statements | Where-Object {
            $_ -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $_.Name -in $functionNames
        })
        if ($definitions.Count -ne $functionNames.Count) {
            throw 'Expected offline build function definitions were not found.'
        }
        . ([scriptblock]::Create(($definitions.Extent.Text -join [Environment]::NewLine)))

        $tasks = @($ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.CommandAst] -and
                $node.GetCommandName() -eq 'Task' -and $node.CommandElements.Count -gt 1 -and
                $node.CommandElements[1].Extent.Text -eq 'Discover'
        }, $false))
        if ($tasks.Count -ne 1) { throw 'Expected exactly one Discover task.' }
        $actions = @($tasks[0].CommandElements | Where-Object {
            $_ -is [System.Management.Automation.Language.ScriptBlockExpressionAst]
        })
        if ($actions.Count -ne 1) { throw 'Expected exactly one Discover action.' }
        $script:OfflineDiscover = $actions[0].ScriptBlock.GetScriptBlock()

        # Do not load the real CLI/process functions: an unmocked call must fail offline.
        function Resolve-AgencyPath {
            param([string]$RequestedPath)
            throw 'Agency path resolution must be mocked in offline tests.'
        }
        function Resolve-McpCliPath {
            param([string]$RequestedPath)
            throw 'CLI path resolution must be mocked in offline tests.'
        }
        function Invoke-McpCliInfo {
            param([string]$McpCli, [string]$ConfigPath, [string]$ServerName, [int]$TimeoutSeconds)
            throw 'Live discovery must not run in offline tests.'
        }
    }

    BeforeEach {
        $script:OfflineRoot = Join-Path $script:OfflineRepoRoot ('.test-tmp/offline-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $script:OfflineRoot | Out-Null
        $script:OfflineStaleAgencyPath = Join-Path $script:OfflineRoot 'missing/agency.exe'
        $script:OfflineResolvedAgencyPath = Join-Path $script:OfflineRoot 'resolved/agency.exe'
        Mock Resolve-AgencyPath { throw 'Unexpected Agency path resolution.' }
        Mock Resolve-McpCliPath { throw 'Unexpected CLI path resolution.' }
        Mock Invoke-McpCliInfo { throw 'Unexpected live discovery.' }
    }

    AfterEach {
        if (Test-Path -LiteralPath $script:OfflineRoot) {
            Remove-Item -Recurse -Force -LiteralPath $script:OfflineRoot
        }
    }

    It 'preserves Agency options but removes stdio-incompatible flags: <Case>' -ForEach @(
        @{ Case = 'separate values'; TransportArgs = @('--port', '3456', '--transport', 'http') }
        @{ Case = 'equals values'; TransportArgs = @('--port=3456', '--transport=http') }
    ) {
        $command = @('agency', 'mcp', 'ado', '--organization', 'example-org') +
            $TransportArgs + @('--read-only', '--tenant=example-tenant')

        $entry = ConvertTo-AgencyMcpCliCommandEntry -Command $command -AgencyCommand $script:OfflineResolvedAgencyPath

        $entry.command | Should -Be $script:OfflineResolvedAgencyPath
        @($entry.args) | Should -Be @(
            '--verbosity', 'error', 'mcp', 'ado', '--organization', 'example-org',
            '--read-only', '--tenant=example-tenant'
        )
    }

    It 'preserves all remote Agency arguments including port and transport options' {
        $command = @(
            'agency', 'mcp', 'remote', 'remote-demo', '--organization', 'example-org',
            '--port', '3456', '--transport', 'http', '--port=4567', '--transport=sse',
            '--header', 'X-Offline: yes'
        )

        $entry = ConvertTo-AgencyMcpCliCommandEntry -Command $command -AgencyCommand $script:OfflineResolvedAgencyPath

        $entry.command | Should -Be $script:OfflineResolvedAgencyPath
        @($entry.args) | Should -Be (@('--verbosity', 'error') + @($command | Select-Object -Skip 1))
    }

    It 'does not resolve a stale Agency path when only generic servers are enabled' {
        $config = [pscustomobject]@{
            agency_path = $script:OfflineStaleAgencyPath
            servers = [pscustomobject]@{
                demo = [pscustomobject]@{
                    command = @('npx', '-y', '@example/offline-server')
                    transport = 'stdio'
                    enabled = $true
                }
                disabled = [pscustomobject]@{ command = @('agency', 'mcp', 'calendar'); enabled = $false }
            }
        }

        $result = ConvertTo-McpCliConfig -ConfigObject $config

        Should -Invoke Resolve-AgencyPath -Times 0 -Exactly -Scope It
        $result.mcpServers.Count | Should -Be 1
        $result.mcpServers.demo.command | Should -Be 'npx'
        @($result.mcpServers.demo.args) | Should -Be @('-y', '@example/offline-server')
        $result.mcpServers.Contains('disabled') | Should -BeFalse
    }

    It 'resolves the configured Agency path once for enabled Agency commands' {
        Mock Resolve-AgencyPath { return $script:OfflineResolvedAgencyPath }
        $config = [pscustomobject]@{
            agency_path = $script:OfflineStaleAgencyPath
            servers = [pscustomobject]@{
                calendar = [pscustomobject]@{ command = @('agency', 'mcp', 'calendar'); enabled = $true }
                mail = [pscustomobject]@{ command = @('agency', 'mcp', 'mail'); enabled = $true }
            }
        }

        $result = ConvertTo-McpCliConfig -ConfigObject $config

        Should -Invoke Resolve-AgencyPath -Times 1 -Exactly -Scope It
        Should -Invoke Resolve-AgencyPath -Times 1 -Exactly -Scope It -ParameterFilter {
            $RequestedPath -eq $script:OfflineStaleAgencyPath
        }
        $result.mcpServers.Count | Should -Be 2
        $result.mcpServers.calendar.command | Should -Be $script:OfflineResolvedAgencyPath
        $result.mcpServers.mail.command | Should -Be $script:OfflineResolvedAgencyPath
    }

    It 'uses the resolved fallback CLI in Discover and the generated runner instead of the stale configured path' {
        $script:OfflineStaleMcpCliPath = Join-Path $script:OfflineRoot 'missing/mcp-cli'
        $script:OfflineFallbackMcpCliPath = Join-Path $script:OfflineRoot 'fallback/mcp-cli'
        $script:SourceConfigObject = [pscustomobject]@{
            mcp_cli_path = $script:OfflineStaleMcpCliPath
            servers = [pscustomobject]@{
                demo = [pscustomobject]@{ command = @('node', 'offline-server.js'); enabled = $true }
            }
        }
        $script:ConfigPath = Join-Path $script:OfflineRoot 'mcp_servers.json'
        $script:DiscoveryRoot = Join-Path $script:OfflineRoot 'discovery'
        New-Item -ItemType Directory -Force -Path $script:DiscoveryRoot | Out-Null
        $McpCliPath = $null
        $SelectedServerNames = 'demo'
        $SkipDiscovery = $false
        $DiscoveryTimeoutSeconds = 1
        Mock Resolve-McpCliPath { return $script:OfflineFallbackMcpCliPath }
        Mock Invoke-McpCliInfo {
            return [pscustomobject]@{ ExitCode = 0; Output = @('Tools (1):', '  offline_tool') }
        }

        & $script:OfflineDiscover

        Should -Invoke Resolve-McpCliPath -Times 1 -Exactly -Scope It -ParameterFilter {
            $RequestedPath -eq $script:OfflineStaleMcpCliPath
        }
        Should -Invoke Invoke-McpCliInfo -Times 1 -Exactly -Scope It -ParameterFilter {
            $McpCli -eq $script:OfflineFallbackMcpCliPath -and $ConfigPath -eq $script:ConfigPath -and
                $ServerName -eq 'demo' -and $TimeoutSeconds -eq 1
        }
        $script:ScriptMcpCliPath | Should -Be $script:OfflineFallbackMcpCliPath
        $script:DiscoveryResults.demo.ExitCode | Should -Be 0
        @($script:DiscoveryResults.demo.ToolMetadata.Name) | Should -Be @('offline_tool')

        $scriptsDir = Join-Path $script:OfflineRoot 'scripts'
        Write-McpToolScripts -ScriptsDir $scriptsDir -ServerName 'demo' -ConfigReference $script:ConfigPath -McpCli $script:ScriptMcpCliPath -ToolMetadata $script:DiscoveryResults.demo.ToolMetadata
        $tokens = $null
        $parseErrors = $null
        $runnerAst = [System.Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $scriptsDir 'Invoke-Tool.ps1'), [ref]$tokens, [ref]$parseErrors
        )
        $parseErrors.Count | Should -Be 0
        $cliParameter = @($runnerAst.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'CliPath' })
        $cliParameter.Count | Should -Be 1
        $cliParameter[0].DefaultValue.Extent.Text |
            Should -Be (ConvertTo-PowerShellPathExpression -Path $script:OfflineFallbackMcpCliPath)
    }
}
