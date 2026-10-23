# Offline MCP server. Tool calls require a complete tools/list on this process.
param(
    [string]$Mode = 'normal',
    [string]$CapturePath,
    [string]$Text,
    [string]$Empty
)
$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = $utf8
[Console]::OutputEncoding = $utf8
$listed = $false
$callCount = 0
$pendingListId = $null
$inventoryChanged = $false
while ($null -ne ($line = [Console]::ReadLine())) {
    $message = ConvertFrom-Json -InputObject $line -AsHashtable -Depth 100
    $record = @{ Method = $message.method; Id = $message.id; ProcessId = $PID; Params = $message.params }
    [IO.File]::AppendAllText($CapturePath, (($record | ConvertTo-Json -Depth 100 -Compress) + "`n"), $utf8)
    $response = @{ jsonrpc = '2.0'; id = $message.id }
    if ($message.id -ceq 'server-ping' -and $message.Contains('result')) {
        $listed = $true
        $response.id = $pendingListId
        $response.result = @{ tools = @() }
        [Console]::Out.WriteLine(($response | ConvertTo-Json -Depth 100 -Compress))
        continue
    }
    switch ($message.method) {
        'initialize' {
            $response.result = @{ protocolVersion = '2024-11-05'; capabilities = @{ tools = @{} }; serverInfo = @{ name = 'offline'; version = '1' } }
        }
        'tools/list' {
            [Console]::Error.Write('offline discovery diagnostic')
            if ($Mode -eq 'list-changed-during-discovery' -and -not $inventoryChanged) {
                $inventoryChanged = $true
                [Console]::Out.WriteLine('{"jsonrpc":"2.0","method":"notifications/tools/list_changed"}')
            }
            if ($Mode -eq 'failure') {
                $response.error = @{ code = -32000; message = 'offline inventory unavailable' }
            }
            elseif ($Mode -eq 'malformed') {
                $response.result = @{ tools = 'not an array' }
            }
            elseif ($Mode -eq 'timeout') {
                Start-Sleep -Seconds 60
            }
            elseif ($Mode -eq 'reverse-request') {
                $pendingListId = $message.id
                [Console]::Out.WriteLine('{"jsonrpc":"2.0","id":"server-ping","method":"ping"}')
            }
            elseif ($Mode -in @('paged', 'repeated-cursor') -and -not $message.params.cursor) {
                $response.result = @{ tools = @(); nextCursor = 'second-page' }
            }
            elseif ($Mode -eq 'repeated-cursor') {
                $response.result = @{ tools = @(); nextCursor = 'second-page' }
            }
            else {
                $listed = $true
                $response.result = @{ tools = @(@{ name = 'echo'; inputSchema = @{ type = 'object' } }) }
            }
            # Exercise full-duplex forwarding while the bridge awaits discovery.
            [Console]::Out.WriteLine('{"jsonrpc":"2.0","method":"notifications/message","params":{"level":"info","data":"offline"}}')
        }
        'tools/call' {
            if (-not $listed) {
                $response.error = @{ code = -32602; message = "Unknown tool; call tools/list before invoking tools" }
            }
            elseif ($Mode -eq 'call-failure') {
                $response.error = @{ code = -32001; message = 'offline tool failure' }
            }
            else {
                $callCount++
                $value = @{
                    Arguments = $message.params.arguments
                    ProcessId = $PID
                    CallCount = $callCount
                    Environment = $env:MCP_BRIDGE_TEST_VALUE
                    WorkingDirectory = [Environment]::CurrentDirectory
                    Text = $Text
                    Empty = $Empty
                } | ConvertTo-Json -Depth 100 -Compress
                $response.result = @{ content = @(@{ type = 'text'; text = $value }) }
                if ($Mode -eq 'list-changed') {
                    $listed = $false
                    [Console]::Out.WriteLine('{"jsonrpc":"2.0","method":"notifications/tools/list_changed"}')
                }
            }
        }
        'ping' { $response.result = @{} }
        default { continue }
    }
    if (-not $message.Contains('id') -or ($Mode -eq 'reverse-request' -and $message.method -eq 'tools/list')) { continue }
    [Console]::Out.WriteLine(($response | ConvertTo-Json -Depth 100 -Compress))
    [Console]::Out.Flush()
}
