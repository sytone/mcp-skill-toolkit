# Offline wrapper-test double. It never resolves or contacts a real MCP server.
param(
    [string]$c,
    [string]$Action,
    [string]$Server,
    [string]$Tool
)

$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = $utf8
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8

if ([string]::IsNullOrWhiteSpace($env:MCP_WRAPPER_CAPTURE_PATH)) {
    throw 'The offline fake requires MCP_WRAPPER_CAPTURE_PATH.'
}

# A directly invoked .ps1 receives PowerShell pipeline objects. The native .cmd
# shim starts a separate pwsh process, so that version reads operating-system stdin.
if ($env:MCP_WRAPPER_INPUT_MODE -eq 'Console') {
    $payload = [Console]::In.ReadToEnd()
}
else {
    $payload = @($input) -join [Environment]::NewLine
}

$receipt = [ordered]@{
    ConfigPath = $c
    Command = $Action
    Server = $Server
    Tool = $Tool
    Payload = $payload
    ExtraArguments = @($args)
}
$receiptJson = ConvertTo-Json -InputObject $receipt -Depth 100 -Compress
[IO.File]::WriteAllText($env:MCP_WRAPPER_CAPTURE_PATH, $receiptJson, $utf8)
Write-Output $receiptJson
exit ([int]$env:MCP_WRAPPER_EXIT_CODE)
