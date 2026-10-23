# Offline MCP CLI double: deliberately calls a tool without first listing tools.
param([string]$c, [string]$Action, [string]$Server, [string]$Tool)
$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false)
$config = Get-Content -LiteralPath $c -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 100
$entry = $config.mcpServers[$Server]
if ($env:MCP_BRIDGE_CONFIG_CAPTURE) {
    Copy-Item -LiteralPath $c -Destination $env:MCP_BRIDGE_CONFIG_CAPTURE
    [IO.File]::WriteAllText($env:MCP_BRIDGE_CONFIG_CAPTURE + '.path', $c, $utf8)
}
$payload = @($input) -join "`n"
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $entry.command
foreach ($argument in $entry.args) { $start.ArgumentList.Add($argument) }
$start.UseShellExecute = $false
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.StandardInputEncoding = $utf8
$start.StandardOutputEncoding = $utf8
$start.StandardErrorEncoding = $utf8
foreach ($key in $entry.env.Keys) { $start.Environment[$key] = $entry.env[$key] }
$start.WorkingDirectory = $entry.cwd
$process = [Diagnostics.Process]::new()
$process.StartInfo = $start
$code = 1
try {
    [void]$process.Start()
    $stderr = $process.StandardError.ReadToEndAsync()
    $process.StandardInput.AutoFlush = $true
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}')
    $callCount = if ($env:MCP_BRIDGE_MULTIPLE_CALLS) { 2 } else { 1 }
    for ($id = 1; $id -le $callCount + 1; $id++) {
        $response = $null
        while ($null -eq $response) {
            $read = $process.StandardOutput.ReadLineAsync()
            if (-not $read.Wait(10000)) { throw 'Offline CLI timed out waiting for the server.' }
            $line = $read.GetAwaiter().GetResult()
            if ($null -eq $line) { throw 'Offline server disconnected.' }
            $message = $line | ConvertFrom-Json -AsHashtable -Depth 100
            if ($message.method -eq 'ping' -and $message.Contains('id')) {
                $reply = @{ jsonrpc = '2.0'; id = $message.id; result = @{} }
                $process.StandardInput.WriteLine(($reply | ConvertTo-Json -Compress))
                continue
            }
            if ($message.Contains('id')) {
                if ($message.id -ne $id) { throw "Unexpected response id for ${id}: $line" }
                $response = $message
            }
        }
        if ($response.Contains('error')) {
            Write-Output ($response | ConvertTo-Json -Depth 100 -Compress)
            $code = 7
            break
        }
        if ($id -gt 1) {
            Write-Output ($response.result | ConvertTo-Json -Depth 100 -Compress)
            $code = 0
        }
        if ($id -le $callCount -and -not ($id -gt 1 -and $env:MCP_BRIDGE_CONCURRENT_CALLS)) {
            if ($id -eq 1) { $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}') }
            $call = @{ jsonrpc = '2.0'; id = $id + 1; method = 'tools/call'; params = @{ name = $Tool; arguments = ($payload | ConvertFrom-Json -AsHashtable -Depth 100) } }
            $process.StandardInput.WriteLine(($call | ConvertTo-Json -Depth 100 -Compress))
            if ($env:MCP_BRIDGE_CONCURRENT_CALLS) {
                $call.id = 3
                $process.StandardInput.WriteLine(($call | ConvertTo-Json -Depth 100 -Compress))
            }
        }
    }
}
finally {
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(5000)) {
        $process.Kill($true)
        $process.WaitForExit()
    }
    $diagnostic = $stderr.GetAwaiter().GetResult()
    if ($diagnostic) { [Console]::Error.Write($diagnostic) }
    $process.Dispose()
}
exit $code
