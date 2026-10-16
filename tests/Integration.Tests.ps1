Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Describe 'Offline source integration and safety boundaries' {
    BeforeAll {
        $script:IntegrationRepo = Split-Path -Parent $PSScriptRoot
        $tokens = $null
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $script:IntegrationRepo 'psakefile.ps1'), [ref]$tokens, [ref]$parseErrors
        )
        if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
        foreach ($definition in @($ast.EndBlock.Statements | Where-Object {
            $_ -is [System.Management.Automation.Language.FunctionDefinitionAst]
        })) {
            Set-Item -Path ('Function:' + $definition.Name) -Value $definition.Body.GetScriptBlock()
        }
        $script:TaskActions = @{}
        foreach ($task in @($ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Task'
        }, $false))) {
            $actions = @($task.CommandElements | Where-Object {
                $_ -is [System.Management.Automation.Language.ScriptBlockExpressionAst]
            })
            if ($actions.Count -eq 1) {
                $script:TaskActions[$task.CommandElements[1].Extent.Text] = $actions[0].ScriptBlock.GetScriptBlock()
            }
        }
        # Local job-boundary stubs accept fixture objects, never real jobs.
        function Start-Job { param($ScriptBlock, $ArgumentList); throw 'Unmocked job launch.' }
        function Wait-Job { param([Parameter(ValueFromPipeline)]$Job, $Timeout); throw 'Unmocked job wait.' }
        function Stop-Job { param($Job); throw 'Unmocked job stop.' }
        function Remove-Job { param($Job, [switch]$Force); throw 'Unmocked job removal.' }
        function Receive-Job { param($Job); throw 'Unmocked job receive.' }
    }

    BeforeEach {
        $script:IntegrationRoot = Join-Path $script:IntegrationRepo ('.test-tmp/integration-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $script:IntegrationRoot | Out-Null
        $SourceConfig = Join-Path $script:IntegrationRoot 'servers.json'
        Copy-Item -LiteralPath (Join-Path $script:IntegrationRepo 'servers.example.json') -Destination $SourceConfig
        $OutputDir = Join-Path $script:IntegrationRoot 'dist'
        $McpConfigPath = Join-Path $script:IntegrationRoot 'mcp_servers.json'
        $McpCliPath = $null
        $SelectedServerNames = 'everything'
        $SkipDiscovery = $true
        $DiscoveryTimeoutSeconds = 1
        $AgencyHelpText = $null
        $AgencyServerHelpJson = $null
        $AgencyPath = $null
        $PackagePath = Join-Path $script:IntegrationRoot 'setup.zip'
        $SourceSkillsDir = Join-Path $OutputDir 'skills'
        $TargetSkillsDir = Join-Path $script:IntegrationRoot 'staged-skills'
        Mock Resolve-McpCliPath { throw 'Unexpected executable lookup.' }
        Mock Resolve-AgencyPath { throw 'Unexpected Agency lookup.' }
        Mock Start-Job { throw 'Unexpected live discovery.' }
    }

    AfterEach {
        Remove-Item -LiteralPath $script:IntegrationRoot -Recurse -Force
    }

    It 'runs offline task actions, removes stale artifacts, and stages the example skill' {
        $stale = Join-Path $OutputDir 'skills/everything/scripts'
        New-Item -ItemType Directory -Force -Path $stale | Out-Null
        Set-Content -LiteralPath (Join-Path (Split-Path -Parent $stale) 'tool_policy.json') -Value '{}'
        & $script:TaskActions.Initialize
        & $script:TaskActions.Config
        & $script:TaskActions.Discover
        & $script:TaskActions.GenerateSkills
        & $script:TaskActions.Deploy
        Test-Path -LiteralPath (Join-Path $stale 'Invoke-Tool.ps1') | Should -BeTrue
        Test-Path -LiteralPath (Join-Path (Split-Path -Parent $stale) 'tool_policy.json') | Should -BeFalse
        Test-Path -LiteralPath (Join-Path $TargetSkillsDir 'everything/SKILL.md') | Should -BeTrue
        $summary = @(Get-Content -Raw -LiteralPath (Join-Path $OutputDir 'build-summary.json') | ConvertFrom-Json)
        $summary.Count | Should -Be 1
        $summary[0].server | Should -Be 'everything'
        Should -Invoke Start-Job -Times 0 -Exactly
    }

    It 'captures failed or empty discovery without inventing tool wrappers: <Case>' -ForEach @(
        @{ Case = 'failed'; FixtureExit = 7; FixtureText = 'fixture discovery failure'; ExpectedText = 'fixture discovery failure' }
        @{ Case = 'empty'; FixtureExit = 0; FixtureText = ''; ExpectedText = 'mcp-cli returned no output.' }
    ) {
        $SkipDiscovery = $false
        $McpCliPath = 'fixture-cli'
        $script:FixtureExit = $FixtureExit
        $script:FixtureText = $FixtureText
        Mock Resolve-McpCliPath { return 'fixture-cli' }
        Mock Invoke-McpCliInfo { return [pscustomobject]@{ ExitCode = $script:FixtureExit; Output = $script:FixtureText } }
        & $script:TaskActions.Initialize
        & $script:TaskActions.Config
        & $script:TaskActions.Discover
        & $script:TaskActions.GenerateSkills
        $summary = @(Get-Content -Raw -LiteralPath (Join-Path $OutputDir 'build-summary.json') | ConvertFrom-Json)
        $summary[0].discoveryExitCode | Should -Be $FixtureExit
        $summary[0].toolCount | Should -Be 0
        Get-Content -Raw -LiteralPath (Join-Path $OutputDir 'discovery/everything.info.txt') | Should -Match ([regex]::Escape($ExpectedText))
        @(Get-ChildItem -LiteralPath (Join-Path $OutputDir 'skills/everything/scripts') -File).Count | Should -Be 1
    }

    It 'does not resolve a CLI or create a summary when every selected server is disabled' {
        $SelectedServerNames = 'local-http-example'
        & $script:TaskActions.Initialize
        & $script:TaskActions.Config
        & $script:TaskActions.Discover
        & $script:TaskActions.GenerateSkills
        Test-Path -LiteralPath (Join-Path $OutputDir 'build-summary.json') | Should -BeFalse
        Should -Invoke Resolve-McpCliPath -Times 0 -Exactly
    }

    It 'syncs an Agency inventory fixture with disabled defaults and supports a no-op repeat' {
        @{ servers = @{} } | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $SourceConfig
        $AgencyHelpText = @('Commands:', '  demo-one     Demonstration server', 'Options:') -join "`n"
        $AgencyServerHelpJson = @{ 'demo-one' = '    --organization <NAME>' } | ConvertTo-Json -Compress
        & $script:TaskActions.SyncAgencyServers
        & $script:TaskActions.SyncAgencyServers
        $config = Get-Content -Raw -LiteralPath $SourceConfig | ConvertFrom-Json -AsHashtable
        $config.servers.'demo-one'.enabled | Should -BeFalse
        $config.servers.Count | Should -Be 1
        Should -Invoke Resolve-AgencyPath -Times 0 -Exactly
    }

    It 'packages all supported source inputs and closes the archive on repeat' {
        & $script:TaskActions.Package
        & $script:TaskActions.Package
        $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
        try {
            $archive.Entries.FullName | Should -Contain 'AGENTS.md'
            $archive.Entries.FullName | Should -Contain 'scripts/Invoke-Tests.ps1'
            $archive.Entries.FullName | Should -Not -Contain 'servers.json'
        }
        finally { $archive.Dispose() }
    }

    It 'refuses non-regular package input instead of following a directory' {
        $source = Join-Path $script:IntegrationRoot 'package-source'
        New-Item -ItemType Directory -Force -Path (Join-Path $source '.gitignore') | Out-Null
        { New-SetupArchive -SourceDirectory $source -DestinationPath $PackagePath } | Should -Throw '*regular file*'
        Remove-Item -LiteralPath $PackagePath -Force
    }

    It 'rejects a missing deployment source before creating a target' {
        { Invoke-SkillDeployment -SourceDirectory (Join-Path $script:IntegrationRoot 'missing') -TargetDirectory $TargetSkillsDir } | Should -Throw '*Run the Build task first*'
        Test-Path -LiteralPath $TargetSkillsDir | Should -BeFalse
    }

    It 'replaces a conflicting target file and preserves custom guidance byte-for-byte on repeat' {
        $source = Join-Path $SourceSkillsDir 'demo'
        New-Item -ItemType Directory -Force -Path $source, $TargetSkillsDir | Out-Null
        Set-Content -LiteralPath (Join-Path $source 'SKILL.md') -Value '# demo'
        Set-Content -LiteralPath (Join-Path $TargetSkillsDir 'demo') -Value 'conflicting file'
        Invoke-SkillDeployment -SourceDirectory $SourceSkillsDir -TargetDirectory $TargetSkillsDir
        $custom = Join-Path $TargetSkillsDir 'demo/references/custom-instructions.md'
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $custom) | Out-Null
        $bytes = [byte[]]@(239, 187, 191, 65, 13, 10, 66)
        [IO.File]::WriteAllBytes($custom, $bytes)
        Set-Content -LiteralPath (Join-Path $TargetSkillsDir 'demo/stale.txt') -Value 'stale'
        Invoke-SkillDeployment -SourceDirectory $SourceSkillsDir -TargetDirectory $TargetSkillsDir
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($custom)) | Should -Be ([Convert]::ToBase64String($bytes))
        Test-Path -LiteralPath (Join-Path $TargetSkillsDir 'demo/stale.txt') | Should -BeFalse
    }

    It 'returns a timeout result and cleans a mocked discovery job' {
        Mock Start-Job { return [pscustomobject]@{ Id = 123 } }
        Mock Wait-Job { return $null }
        Mock Stop-Job { }
        Mock Remove-Job { }
        $result = Invoke-McpCliInfo -McpCli 'never-run' -ConfigPath $McpConfigPath -ServerName 'demo' -TimeoutSeconds 1
        $result.ExitCode | Should -Be -1
        Should -Invoke Stop-Job -Times 1 -Exactly
        Should -Invoke Remove-Job -Times 1 -Exactly
    }

    It 'receives and cleans a completed mocked discovery job' {
        Mock Start-Job { return [pscustomobject]@{ Id = 123 } }
        Mock Wait-Job { return [pscustomobject]@{ Id = 123 } }
        Mock Receive-Job { return [pscustomobject]@{ ExitCode = 0; Output = 'fixture' } }
        Mock Remove-Job { }
        $result = Invoke-McpCliInfo -McpCli 'never-run' -ConfigPath $McpConfigPath -ServerName 'demo' -TimeoutSeconds 1
        $result.Output | Should -Be 'fixture'
        Should -Invoke Remove-Job -Times 1 -Exactly
    }
}
