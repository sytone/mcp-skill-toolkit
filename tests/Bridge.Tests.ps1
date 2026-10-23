Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Describe 'Generated stdio session bridge (offline)' {
    BeforeAll {
        $script:BridgeRepo = Split-Path -Parent $PSScriptRoot
        $script:BridgeRoot = Join-Path $script:BridgeRepo ('.test-tmp/bridge-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $script:BridgeRoot -Force | Out-Null
        $script:BridgePwsh = Join-Path $PSHOME 'pwsh.exe'
        $script:BridgeUtf8 = [Text.UTF8Encoding]::new($false)
        $script:BridgeCli = Join-Path $PSScriptRoot 'fixtures/stdio-mcp-cli.ps1'
        $script:BridgeServer = Join-Path $PSScriptRoot 'fixtures/stdio-mcp-server.ps1'
        $tokens = $null
        $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $script:BridgeRepo 'psakefile.ps1'), [ref]$tokens, [ref]$errors)
        if ($errors.Count) { throw ($errors | Out-String) }
        foreach ($definition in $ast.EndBlock.Statements | Where-Object {
            $_ -is [Management.Automation.Language.FunctionDefinitionAst]
        }) {
            Set-Item -Path ('Function:' + $definition.Name) -Value $definition.Body.GetScriptBlock()
        }
        $script:BridgeHarness = Join-Path $script:BridgeRoot 'invoke.ps1'
        $harness = @'
param([string]$RequestPath)
$ErrorActionPreference = 'Stop'
$request = Get-Content -LiteralPath $RequestPath -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 100
if ($request.Direct) {
    $request.Json | & $request.Cli -c $request.Config call offline echo
}
else {
    $parameters = $request.Parameters
    & $request.Script @parameters
}
exit $LASTEXITCODE
'@
        [IO.File]::WriteAllText($script:BridgeHarness, $harness, $script:BridgeUtf8)

        function Invoke-BridgeCase {
            param(
                [string]$Mode = 'normal',
                [string]$InputKind = 'generic',
                [switch]$MultipleCalls,
                [switch]$ConcurrentCalls,
                [switch]$ScriptServer,
                [switch]$CommandShim,
                [switch]$Direct,
                [switch]$MissingServer,
                [switch]$MissingCli
            )
            $root = Join-Path $script:BridgeRoot ([Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $root -Force | Out-Null
            $configPath = Join-Path $root 'original config.json'
            $capturePath = Join-Path $root 'protocol.jsonl'
            $configCapture = Join-Path $root 'runtime config.json'
            $scripts = Join-Path $root "generated scripts' 雪"
            $text = "O'Brien `"quoted`" 雪; `$HOME"
            $config = @{
                mcpServers = @{
                    offline = @{
                        command = $script:BridgePwsh
                        args = @('-NoLogo', '-NoProfile', '-File', $script:BridgeServer, '-Mode', $Mode, '-CapturePath', $capturePath, '-Text', $text, '-Empty', '')
                        cwd = $root
                        env = @{ MCP_BRIDGE_TEST_VALUE = $text }
                        allowedTools = @('echo')
                        disabledTools = @('delete_*')
                    }
                    untouched = @{ url = 'http://127.0.0.1:1/not-contacted'; headers = @{ Example = 'not-a-secret' } }
                }
            }
            if ($ScriptServer) {
                $config.mcpServers.offline.command = $script:BridgeServer
                $config.mcpServers.offline.args = @($config.mcpServers.offline.args | Select-Object -Skip 4)
            }
            if ($CommandShim) {
                $shimPath = Join-Path $root 'offline server.cmd'
                @(
                    '@echo off'
                    ('"{0}" -NoLogo -NoProfile -File "{1}" %*' -f $script:BridgePwsh, $script:BridgeServer)
                    'exit /b %errorlevel%'
                ) -join "`r`n" | Set-Content -LiteralPath $shimPath -Encoding ascii
                $config.mcpServers.offline.command = $shimPath
                $config.mcpServers.offline.args = @('-CapturePath', $capturePath, '-Mode', $Mode)
            }
            if ($MissingServer) {
                $config.mcpServers.Remove('offline')
            }
            $originalJson = $config | ConvertTo-Json -Depth 100
            [IO.File]::WriteAllText($configPath, $originalJson, $script:BridgeUtf8)
            $cli = if ($MissingCli) { Join-Path $root 'missing.exe' } else { $script:BridgeCli }
            Write-McpToolScripts -ScriptsDir $scripts -ServerName offline -ConfigReference $configPath -McpCli $cli -ToolMetadata @(
                [pscustomobject]@{ Name = 'echo'; Parameters = @('Text') }
                [pscustomobject]@{ Name = 'Invoke-McpStdioBridge'; Parameters = @('Text') }
            )
            $payload = @{ Text = $text; nested = @{ count = 0; enabled = $false }; items = @('a', '雪') } | ConvertTo-Json -Depth 100 -Compress
            $parameters = @{ Tool = 'echo'; Json = $payload }
            $scriptName = 'Invoke-Tool.ps1'
            $stdin = ''
            switch ($InputKind) {
                'per-tool' { $scriptName = 'echo.ps1'; $parameters = @{ Text = $text } }
                'stdin' { $scriptName = 'echo.ps1'; $parameters = @{}; $stdin = $payload }
                'json-file' {
                    $scriptName = 'echo.ps1'
                    $jsonPath = Join-Path $root 'payload.json'
                    [IO.File]::WriteAllText($jsonPath, $payload, $script:BridgeUtf8)
                    $parameters = @{ JsonPath = $jsonPath }
                }
            }
            $requestPath = Join-Path $root 'request.json'
            @{
                Script = Join-Path $scripts $scriptName
                Parameters = $parameters
                Direct = [bool]$Direct
                Cli = $cli
                Config = $configPath
                Json = $payload
            } | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $requestPath -Encoding utf8
            $start = [Diagnostics.ProcessStartInfo]::new()
            $start.FileName = $script:BridgePwsh
            foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $script:BridgeHarness, $requestPath)) {
                $start.ArgumentList.Add($argument)
            }
            $start.UseShellExecute = $false
            $start.RedirectStandardInput = $true
            $start.RedirectStandardOutput = $true
            $start.RedirectStandardError = $true
            $start.StandardInputEncoding = $script:BridgeUtf8
            $start.StandardOutputEncoding = $script:BridgeUtf8
            $start.StandardErrorEncoding = $script:BridgeUtf8
            $start.Environment['MCP_SKILL_TOOLKIT_LOG_DIR'] = Join-Path $root 'logs'
            $start.Environment['MCP_SKILL_TOOLKIT_LOG'] = 'on'
            $start.Environment['MCP_BRIDGE_CONFIG_CAPTURE'] = $configCapture
            $start.Environment['MCP_BRIDGE_MULTIPLE_CALLS'] = if ($MultipleCalls) { '1' } else { '' }
            $start.Environment['MCP_BRIDGE_CONCURRENT_CALLS'] = if ($ConcurrentCalls) { '1' } else { '' }
            $start.Environment['MCP_TIMEOUT'] = if ($Mode -eq 'timeout') { '2' } else { '10' }
            $start.Environment['TMP'] = $root
            $start.Environment['TEMP'] = $root
            $child = [Diagnostics.Process]::new()
            $child.StartInfo = $start
            try {
                [void]$child.Start()
                $stdoutTask = $child.StandardOutput.ReadToEndAsync()
                $stderrTask = $child.StandardError.ReadToEndAsync()
                $child.StandardInput.Write($stdin)
                $child.StandardInput.Close()
                if (-not $child.WaitForExit(25000)) {
                    $child.Kill($true)
                    $child.WaitForExit()
                    throw 'Offline bridge test exceeded its 25-second timeout.'
                }
                $stdout = $stdoutTask.GetAwaiter().GetResult()
                $stderr = $stderrTask.GetAwaiter().GetResult()
                $protocol = if (Test-Path -LiteralPath $capturePath) {
                    @(Get-Content -LiteralPath $capturePath -Encoding utf8 | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable -Depth 100 })
                } else { @() }
                $responses = @($stdout -split '\r?\n' | Where-Object { $_.StartsWith('{') } | ForEach-Object {
                    $_ | ConvertFrom-Json -AsHashtable -Depth 100
                })
                $log = if (Test-Path -LiteralPath (Join-Path $root 'logs')) {
                    Get-ChildItem -LiteralPath (Join-Path $root 'logs') -File | Get-Content | ConvertFrom-Json -AsHashtable
                } else { $null }
                return [pscustomobject]@{
                    ExitCode = $child.ExitCode; Stdout = $stdout; Stderr = $stderr
                    Protocol = @($protocol); Responses = $responses; Log = $log
                    Root = $root; ConfigCapture = $configCapture
                    OriginalJson = $originalJson; ConfigPath = $configPath; Text = $text
                }
            }
            finally {
                if ($child.Id -and -not $child.HasExited) { $child.Kill($true); $child.WaitForExit() }
                $child.Dispose()
            }
        }
    }

    AfterAll {
        Remove-Item -LiteralPath $script:BridgeRoot -Recurse -Force
    }

    It 'reproduces the missing-session-discovery error without the bridge' {
        $result = Invoke-BridgeCase -Direct
        $result.ExitCode | Should -Be 7 -Because ($result.Stderr + $result.Stdout)
        $result.Responses[0].error.message | Should -Match 'call tools/list before invoking tools'
        @($result.Protocol | Where-Object Method -EQ 'tools/list').Count | Should -Be 0
    }

    It 'discovers before calling through <InputKind> without leaking discovery output' -ForEach @(
        @{ InputKind = 'generic' }, @{ InputKind = 'per-tool' }, @{ InputKind = 'stdin' }, @{ InputKind = 'json-file' }
    ) {
        $result = Invoke-BridgeCase -InputKind $InputKind
        $result.ExitCode | Should -Be 0 -Because ($result.Stderr + $result.Stdout)
        @($result.Protocol.Method) | Should -Be @('initialize', 'notifications/initialized', 'tools/list', 'tools/call')
        @($result.Protocol.ProcessId | Select-Object -Unique).Count | Should -Be 1
        $result.Responses.Count | Should -Be 1
        $value = $result.Responses[0].content[0].text | ConvertFrom-Json -AsHashtable
        $value.Arguments.Text | Should -BeExactly $result.Text
        $value.Text | Should -BeExactly $result.Text
        $value.Empty | Should -BeExactly ''
        $value.Environment | Should -BeExactly $result.Text
        $value.WorkingDirectory | Should -BeExactly $result.Root
        $result.Log.status | Should -Be 'succeeded'
        ($result.Stderr + $result.Stdout) | Should -Match 'offline discovery diagnostic'
        $result.Stdout | Should -Not -Match 'mcp-skill-discovery-|notifications/message'
        Get-Content -LiteralPath $result.ConfigPath -Raw | Should -BeExactly $result.OriginalJson
        $runtime = Get-Content -LiteralPath $result.ConfigCapture -Raw | ConvertFrom-Json -AsHashtable
        $runtime.mcpServers.offline.allowedTools | Should -Be @('echo')
        $runtime.mcpServers.offline.disabledTools | Should -Be @('delete_*')
        $runtime.mcpServers.untouched.url | Should -BeExactly 'http://127.0.0.1:1/not-contacted'
        $runtime.mcpServers.untouched.headers.Example | Should -BeExactly 'not-a-secret'
        Test-Path -LiteralPath (Get-Content -LiteralPath ($result.ConfigCapture + '.path') -Raw) | Should -BeFalse
    }

    It 'completes all discovery pages before executing a call' {
        $result = Invoke-BridgeCase -Mode paged
        $result.ExitCode | Should -Be 0 -Because ($result.Stderr + $result.Stdout)
        @($result.Protocol.Method) | Should -Be @('initialize', 'notifications/initialized', 'tools/list', 'tools/list', 'tools/call')
        $result.Protocol[3].Params.cursor | Should -Be 'second-page'
    }

    It 'discovers once for multiple calls on a persistent connection' {
        $result = Invoke-BridgeCase -MultipleCalls
        $result.ExitCode | Should -Be 0 -Because ($result.Stderr + $result.Stdout)
        @($result.Protocol | Where-Object Method -EQ 'tools/list').Count | Should -Be 1
        @($result.Protocol | Where-Object Method -EQ 'tools/call').Count | Should -Be 2
        @($result.Protocol.ProcessId | Select-Object -Unique).Count | Should -Be 1
    }

    It 'queues concurrent calls behind a single discovery request' {
        $result = Invoke-BridgeCase -MultipleCalls -ConcurrentCalls
        $result.ExitCode | Should -Be 0 -Because ($result.Stderr + $result.Stdout)
        @($result.Protocol | Where-Object Method -EQ 'tools/list').Count | Should -Be 1
        @($result.Protocol | Where-Object Method -EQ 'tools/call').Count | Should -Be 2
        $result.Responses.Count | Should -Be 2
    }

    It 'forwards server requests and client responses while discovery is pending' {
        $result = Invoke-BridgeCase -Mode reverse-request
        $result.ExitCode | Should -Be 0 -Because ($result.Stderr + $result.Stdout)
        @($result.Protocol | Where-Object Id -CEQ 'server-ping').Count | Should -Be 1
        @($result.Protocol | Where-Object Method -EQ 'tools/call').Count | Should -Be 1
    }

    It 'supports a server launched through a <Kind> entry point' -ForEach @(
        @{ Kind = 'PowerShell script'; ScriptServer = $true; CommandShim = $false }
        @{ Kind = 'Windows command shim'; ScriptServer = $false; CommandShim = $true }
    ) {
        $result = Invoke-BridgeCase -ScriptServer:$ScriptServer -CommandShim:$CommandShim
        $result.ExitCode | Should -Be 0 -Because ($result.Stderr + $result.Stdout)
        @($result.Protocol | Where-Object Method -EQ 'tools/list').Count | Should -Be 1
        @($result.Protocol | Where-Object Method -EQ 'tools/call').Count | Should -Be 1
    }

    It 'refreshes discovery after a tools/list_changed notification' {
        $result = Invoke-BridgeCase -Mode list-changed -MultipleCalls
        $result.ExitCode | Should -Be 0 -Because ($result.Stderr + $result.Stdout)
        @($result.Protocol | Where-Object Method -EQ 'tools/list').Count | Should -Be 2
        @($result.Protocol | Where-Object Method -EQ 'tools/call').Count | Should -Be 2
    }

    It 'restarts discovery when the inventory changes before discovery completes' {
        $result = Invoke-BridgeCase -Mode list-changed-during-discovery
        $result.ExitCode | Should -Be 0 -Because ($result.Stderr + $result.Stdout)
        @($result.Protocol.Method) | Should -Be @('initialize', 'notifications/initialized', 'tools/list', 'tools/list', 'tools/call')
    }

    It 'blocks calls on <Mode> discovery and removes the temporary configuration' -ForEach @(
        @{ Mode = 'failure'; ErrorText = 'offline inventory unavailable' }
        @{ Mode = 'malformed'; ErrorText = 'invalid tool list' }
        @{ Mode = 'repeated-cursor'; ErrorText = 'repeated cursor' }
        @{ Mode = 'timeout'; ErrorText = 'discovery timed out' }
    ) {
        $result = Invoke-BridgeCase -Mode $Mode
        $result.ExitCode | Should -Not -Be 0
        @($result.Protocol | Where-Object Method -EQ 'tools/call').Count | Should -Be 0
        ($result.Stdout + $result.Stderr) | Should -Match $ErrorText
        $result.Log.status | Should -Be 'failed'
        Test-Path -LiteralPath (Get-Content -LiteralPath ($result.ConfigCapture + '.path') -Raw) | Should -BeFalse
        $serverId = $result.Protocol[0].ProcessId
        Get-Process -Id $serverId -ErrorAction SilentlyContinue | Should -BeNullOrEmpty
    }

    It 'preserves tool failures without replaying the call' {
        $result = Invoke-BridgeCase -Mode call-failure
        $result.ExitCode | Should -Be 7
        $result.Responses[0].error.code | Should -Be -32001
        $result.Responses[0].error.message | Should -Be 'offline tool failure'
        @($result.Protocol | Where-Object Method -EQ 'tools/call').Count | Should -Be 1
    }

    It 'cleans the temporary configuration when the CLI cannot start' {
        $result = Invoke-BridgeCase -MissingCli
        $result.ExitCode | Should -Not -Be 0
        @(Get-ChildItem -LiteralPath $result.Root -Filter 'mcp-skill-toolkit-*.json').Count | Should -Be 0
        $result.Log.error | Should -Be 'cli-error'
    }

    It 'rejects a missing server without starting the CLI' {
        $result = Invoke-BridgeCase -MissingServer
        $result.ExitCode | Should -Not -Be 0
        ($result.Stdout + $result.Stderr) | Should -Match 'MCP configuration does not contain server: offline'
        $result.Log.error | Should -Be 'invalid-config'
        Test-Path -LiteralPath $result.ConfigCapture | Should -BeFalse
        $result.Protocol.Count | Should -Be 0
    }
}
