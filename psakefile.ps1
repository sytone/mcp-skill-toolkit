Properties {
    $SourceConfig = Join-Path $PSScriptRoot 'servers.json'
    $OutputDir = Join-Path $PSScriptRoot 'dist'
    $McpConfigPath = '~/.config/mcp/mcp_servers.json'
    $McpCliPath = $null
    $SelectedServerNames = ''
    $DiscoveryTimeoutSeconds = 180
    $SkipDiscovery = $false
    $SourceSkillsDir = Join-Path $PSScriptRoot 'dist\skills'
    $TargetSkillsDir = Join-Path $HOME '.botnexus\skills'
    $TestPath = Join-Path $PSScriptRoot 'tests'
    $AgencyPath = $null
    $AgencyHelpText = $null
    $AgencyServerHelpJson = $null
    $PackagePath = Join-Path $PSScriptRoot 'mcp-cli-setup.zip'
    $ToolCallLogDirectory = '~/.mcp-skill-toolkit/logs'
}

$ErrorActionPreference = 'Stop'

function Set-McpCliUtf8Encoding {
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [Console]::InputEncoding = $utf8NoBom
    [Console]::OutputEncoding = $utf8NoBom
    $script:OutputEncoding = $utf8NoBom
}

function Repair-TextEncodingArtifacts {
    param([string]$Text)

    if ($null -eq $Text) {
        return $Text
    }

    $repaired = $Text
    $replacements = @(
        @('ΓÇó', [string][char]0x2022),
        @('ΓÇö', [string][char]0x2014),
        @('ΓÇô', [string][char]0x2013),
        @('ΓÇÖ', [string][char]0x2019),
        @('ΓÇ£', [string][char]0x201C),
        @('ΓÇ¥', [string][char]0x201D),
        @('ΓÇª', [string][char]0x2026),
        @('┬á', ' ')
    )

    foreach ($entry in $replacements) {
        $repaired = $repaired.Replace($entry[0], $entry[1])
    }

    return $repaired
}

Set-McpCliUtf8Encoding

function Write-BuildLog {
    param([string]$Message, [string]$Label = 'INFO')
    Write-Host "[mcp-cli][$Label] $Message"
}

function New-SetupArchive {
    param(
        [string]$SourceDirectory,
        [string]$DestinationPath
    )

    $sourceRoot = Resolve-BuildPath -Path $SourceDirectory
    $archivePath = Resolve-BuildPath -Path $DestinationPath
    $archiveDirectory = Split-Path -Parent $archivePath
    New-Item -ItemType Directory -Force -Path $archiveDirectory | Out-Null
    Remove-Item -Force -Path $archivePath -ErrorAction SilentlyContinue

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # Keep this list explicit: a recursive scan can publish local config or credentials.
    # Recipients create their own servers.json from servers.example.json.
    $publicFiles = @(
        '.gitignore',
        '.gitattributes',
        'init.ps1',
        'psakefile.ps1',
        'README.md',
        'AGENTS.md',
        'docs/creating-skills.md',
        'docs/testing.md',
        'servers.example.json',
        'scripts/Invoke-Tests.ps1',
        'scripts/Invoke-McpStdioBridge.ps1',
        'tests/Build.Tests.ps1',
        'tests/Init.Tests.ps1',
        'tests/Unit.Tests.ps1',
        'tests/Wrappers.Tests.ps1',
        'tests/Integration.Tests.ps1',
        'tests/Runner.Tests.ps1',
        'tests/Bridge.Tests.ps1',
        'tests/fixtures/servers.json',
        'tests/fixtures/fake-mcp-cli.ps1',
        'tests/fixtures/stdio-mcp-server.ps1',
        'tests/fixtures/stdio-mcp-cli.ps1'
    )
    $archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($relativePath in $publicFiles) {
            $file = Get-Item -LiteralPath (Join-Path $sourceRoot $relativePath) -Force
            if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Public package input must be a regular file: $relativePath"
            }

            $entryName = $relativePath.Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                $file.FullName,
                $entryName,
                [IO.Compression.CompressionLevel]::Optimal
            ) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }

    return $archivePath
}

function Resolve-UserPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $Path
    }
    if ($Path -eq '~') {
        return $HOME
    }
    if ($Path.StartsWith('~/') -or $Path.StartsWith('~\')) {
        return (Join-Path $HOME $Path.Substring(2))
    }
    return $Path
}

function Resolve-BuildPath {
    param([string]$Path)

    $expanded = Resolve-UserPath -Path $Path
    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($expanded)
}

function Resolve-McpCliPath {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        if (-not [IO.Path]::IsPathRooted($RequestedPath) -and $RequestedPath -notmatch '[\\/]') {
            $requestedCommand = Get-Command $RequestedPath -ErrorAction SilentlyContinue
            if ($null -ne $requestedCommand) {
                return $requestedCommand.Source
            }
        }
        else {
            $expanded = Resolve-UserPath -Path $RequestedPath
            if (Test-Path $expanded) {
                return (Resolve-Path -Path $expanded).Path
            }
        }
        Write-BuildLog "Configured mcp-cli path was not found: $RequestedPath. Trying installed locations." 'WARN'
    }

    $command = Get-Command mcp-cli -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $candidates = @(
        (Join-Path $HOME 'scoop\persist\bun\bin\mcp-cli.exe'),
        (Join-Path $HOME 'scoop\persist\bun\bin\mcp-cli.bunx'),
        (Join-Path $HOME '.bun\bin\mcp-cli.exe'),
        (Join-Path $HOME '.bun\bin\mcp-cli')
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return (Resolve-Path -Path $candidate).Path
        }
    }

    throw 'mcp-cli was not found on PATH or in known Bun install locations.'
}

function Resolve-AgencyPath {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $expanded = Resolve-UserPath -Path $RequestedPath
        if (Test-Path $expanded) {
            return (Resolve-Path -Path $expanded).Path
        }
        Write-BuildLog "Configured Agency path was not found: $RequestedPath. Trying installed locations." 'WARN'
    }

    $command = Get-Command agency -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    foreach ($candidate in @(
        (Join-Path $env:APPDATA 'agency\CurrentVersion\agency.exe'),
        'C:\.Tools\agency\CurrentVersion\agency.exe'
    )) {
        if (Test-Path $candidate) {
            return (Resolve-Path -Path $candidate).Path
        }
    }

    throw 'Agency was not found on PATH or in its standard user installation directory.'
}

function Get-AgencyMcpServersFromHelp {
    param([string]$HelpText)

    $excludedNames = @('local', 'npx', 'remote', 'help')
    $inCommands = $false
    $servers = New-Object System.Collections.Generic.List[object]

    foreach ($line in $HelpText -split '\r?\n') {
        if ($line -match '^Commands:\s*$') {
            $inCommands = $true
            continue
        }
        if ($inCommands -and $line -match '^Options:\s*$') {
            break
        }
        if (-not $inCommands -or $line -notmatch '^\s{2}([a-z0-9][a-z0-9-]*)\s{2,}(.+?)\s*$') {
            continue
        }

        $name = $Matches[1]
        if ($name -in $excludedNames) {
            continue
        }
        [void]$servers.Add([pscustomobject]@{
            Name = $name
            Description = $Matches[2].Trim()
        })
    }

    if ($servers.Count -eq 0) {
        throw 'No Agency MCP servers were found in the help output.'
    }

    return $servers.ToArray()
}

function Get-AgencyMcpConfigurationOptions {
    param([string]$HelpText)

    $commonOptions = @('--no-config-cache', '--transport', '--port', '--entra-client-id', '--help')
    $options = New-Object System.Collections.Generic.List[string]
    foreach ($match in [regex]::Matches($HelpText, '(?m)^\s+(--[a-z0-9-]+)(?:\s+<([^>]+)>)?')) {
        $name = $match.Groups[1].Value
        if ($name -in $commonOptions) {
            continue
        }
        $displayName = if ($match.Groups[2].Success) { "$name <$($match.Groups[2].Value)>" } else { $name }
        if (-not $options.Contains($displayName)) {
            [void]$options.Add($displayName)
        }
    }

    return $options.ToArray()
}

function Sync-AgencyMcpServers {
    param(
        [string]$ConfigPath,
        [string]$HelpText,
        [hashtable]$ServerHelpByName = @{}
    )

    $resolvedConfigPath = (Resolve-Path -Path (Resolve-UserPath -Path $ConfigPath)).Path
    $config = Get-Content -Raw -Path $resolvedConfigPath | ConvertFrom-Json -AsHashtable -Depth 100
    if (-not $config.Contains('servers') -or $null -eq $config.servers) {
        throw 'Source config does not contain a servers object.'
    }

    $discoveredServers = @(Get-AgencyMcpServersFromHelp -HelpText $HelpText)
    $existingAgencyNames = @{}
    $usedPorts = @{}
    $portEntries = New-Object System.Collections.Generic.List[object]
    $highestPort = 3455
    foreach ($entry in $config.servers.GetEnumerator()) {
        $server = $entry.Value
        $existingAgencyNames[[string]$entry.Key] = $true
        if ($server.Contains('exe_name') -and -not [string]::IsNullOrWhiteSpace([string]$server.exe_name)) {
            $existingAgencyNames[[string]$server.exe_name] = $true
        }
        $command = @($server.command | ForEach-Object { [string]$_ })
        if ($command.Count -ge 3 -and $command[0] -eq 'agency' -and $command[1] -eq 'mcp') {
            $existingAgencyNames[$command[2]] = $true
        }
        if ($server.Contains('default_port')) {
            $port = [int]$server.default_port
            $usedPorts[$port] = $true
            [void]$portEntries.Add([pscustomobject]@{
                Name = [string]$entry.Key
                Server = $server
                Port = $port
                Enabled = $server.Contains('enabled') -and [bool]$server.enabled
            })
            if ($port -gt $highestPort) {
                $highestPort = $port
            }
        }
    }

    $changes = New-Object System.Collections.Generic.List[object]
    $nextPort = $highestPort + 1
    foreach ($group in @($portEntries | Group-Object Port | Where-Object Count -gt 1)) {
        $orderedEntries = @($group.Group | Sort-Object @{ Expression = 'Enabled'; Descending = $true })
        foreach ($duplicate in @($orderedEntries | Select-Object -Skip 1)) {
            while ($usedPorts.ContainsKey($nextPort)) {
                $nextPort++
            }
            $duplicate.Server.default_port = $nextPort
            $usedPorts[$nextPort] = $true
            [void]$changes.Add([pscustomobject]@{
                Action = 'Reassigned'
                Name = $duplicate.Name
                OldPort = $duplicate.Port
                Port = $nextPort
            })
            $nextPort++
        }
    }

    foreach ($server in $discoveredServers) {
        if ($existingAgencyNames.ContainsKey($server.Name)) {
            continue
        }

        while ($usedPorts.ContainsKey($nextPort)) {
            $nextPort++
        }

        $newServer = [ordered]@{
            exe_name = $server.Name
            command = @('agency', 'mcp', $server.Name, '--port', '{port}', '--transport', 'http')
            default_port = $nextPort
            description = $server.Description
            enabled = $false
            added_by = 'SyncAgencyServers'
            added_at_utc = [DateTime]::UtcNow.ToString('o')
        }
        if ($ServerHelpByName.ContainsKey($server.Name)) {
            $configurationOptions = @(Get-AgencyMcpConfigurationOptions -HelpText ([string]$ServerHelpByName[$server.Name]))
            if ($configurationOptions.Count -gt 0) {
                $newServer['configuration_options'] = $configurationOptions
            }
        }
        $config.servers[$server.Name] = $newServer
        $usedPorts[$nextPort] = $true
        [void]$changes.Add([pscustomobject]@{ Action = 'Added'; Name = $server.Name; Port = $nextPort })
        $nextPort++
    }

    if ($changes.Count -gt 0) {
        $temporaryPath = "$resolvedConfigPath.tmp"
        $config | ConvertTo-Json -Depth 100 | Set-Content -Encoding UTF8 -Path $temporaryPath
        Move-Item -Force -Path $temporaryPath -Destination $resolvedConfigPath
    }

    return $changes.ToArray()
}

function ConvertTo-YamlSingleQuoted {
    param([string]$Text)

    if ($null -eq $Text) {
        return "''"
    }
    return "'" + $Text.Replace("'", "''") + "'"
}

function ConvertTo-PowerShellSingleQuoted {
    param([string]$Text)

    if ($null -eq $Text) {
        return "''"
    }
    return "'" + $Text.Replace("'", "''") + "'"
}

function ConvertTo-PowerShellPathExpression {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return "''"
    }

    if (-not [IO.Path]::IsPathRooted($Path) -and $Path -notmatch '[\\/]') {
        return (ConvertTo-PowerShellSingleQuoted -Text $Path)
    }

    $expanded = Resolve-UserPath -Path $Path
    $fullPath = [IO.Path]::GetFullPath($expanded)
    $homePath = [IO.Path]::GetFullPath($HOME)
    $comparison = [StringComparison]::OrdinalIgnoreCase

    if ($fullPath.Equals($homePath, $comparison)) {
        return '$HOME'
    }

    $homePrefix = $homePath.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($fullPath.StartsWith($homePrefix, $comparison)) {
        $relativePath = $fullPath.Substring($homePrefix.Length)
        return "(Join-Path `$HOME $(ConvertTo-PowerShellSingleQuoted -Text $relativePath))"
    }

    return (ConvertTo-PowerShellSingleQuoted -Text $fullPath)
}

function Format-SkillDescription {
    param([string]$Description, [string]$ServerName)

    $clean = ([string]$Description) -replace '\bMCP\b\s*', ''
    $clean = ($clean -replace '\s{2,}', ' ').Trim()
    if ([string]::IsNullOrWhiteSpace($clean)) {
        $clean = "$ServerName tools through mcp-cli"
    }
    if ($clean.Length -gt 1024) {
        $clean = $clean.Substring(0, 1024)
    }
    return $clean
}

function Get-SourceServerMap {
    param([object]$ConfigObject, [string[]]$SelectedServers)

    $normalizedSelectedServers = @()
    foreach ($name in @($SelectedServers)) {
        if ([string]::IsNullOrWhiteSpace([string]$name)) {
            continue
        }
        $normalizedSelectedServers += @([string]$name -split '\s*,\s*' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }

    $serversProperty = $ConfigObject.PSObject.Properties['servers']
    if ($null -eq $serversProperty -or $null -eq $serversProperty.Value) {
        throw 'Source config does not contain a servers object.'
    }

    $allServers = @{}
    foreach ($property in $serversProperty.Value.PSObject.Properties) {
        if ($property.Name -notmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$' -or $property.Name.Length -gt 64) {
            throw "Invalid server name: $($property.Name). Use lowercase letters, digits, and single hyphens (max 64 characters)."
        }
        $allServers[$property.Name] = $property.Value
    }

    if ($normalizedSelectedServers.Count -gt 0) {
        $selected = @{}
        foreach ($name in $normalizedSelectedServers) {
            if (-not $allServers.ContainsKey($name)) {
                throw "Unknown server: $name. Available servers: $(@($allServers.Keys) -join ', ')"
            }
            $selected[$name] = $allServers[$name]
        }
        $allServers = $selected
    }

    $result = [ordered]@{}
    foreach ($entry in $allServers.GetEnumerator()) {
        $enabled = $true
        if ($null -ne $entry.Value.PSObject.Properties['enabled']) {
            $enabled = [bool]$entry.Value.enabled
        }
        if ($enabled) {
            $result[$entry.Key] = $entry.Value
        }
    }

    return $result
}

function Get-ConfiguredMcpCliPath {
    param([object]$ConfigObject)

    foreach ($propertyName in @('mcp_cli_path', 'mcpCliPath')) {
        $property = $ConfigObject.PSObject.Properties[$propertyName]
        if ($null -ne $property -and -not [string]::IsNullOrWhiteSpace([string]$property.Value)) {
            return [string]$property.Value
        }
    }

    return $null
}

function Get-ServerTransport {
    param([object]$ServerConfig)

    if ($null -ne $ServerConfig.PSObject.Properties['transport'] -and -not [string]::IsNullOrWhiteSpace([string]$ServerConfig.transport)) {
        return ([string]$ServerConfig.transport).Trim().ToLowerInvariant()
    }

    $command = @($ServerConfig.command | ForEach-Object { [string]$_ })
    for ($index = 0; $index -lt $command.Count - 1; $index++) {
        if ($command[$index] -eq '--transport') {
            return $command[$index + 1].Trim().ToLowerInvariant()
        }
    }

    return 'http'
}

function Get-ServerEndpoint {
    param([object]$ServerConfig)

    if ($null -ne $ServerConfig.PSObject.Properties['endpoint'] -and -not [string]::IsNullOrWhiteSpace([string]$ServerConfig.endpoint)) {
        $endpoint = [string]$ServerConfig.endpoint
    }
    else {
        $endpoint = '/'
    }

    if (-not $endpoint.StartsWith('/')) {
        $endpoint = '/' + $endpoint
    }

    return $endpoint
}

function Add-ToolPolicy {
    param(
        [System.Collections.Specialized.OrderedDictionary]$Target,
        [object]$ServerConfig
    )

    if ($null -eq $ServerConfig.PSObject.Properties['tool_policy']) {
        return
    }

    $policy = $ServerConfig.tool_policy
    if ($null -ne $policy.PSObject.Properties['allow']) {
        $Target['allowedTools'] = @($policy.allow | ForEach-Object { [string]$_ })
    }
    if ($null -ne $policy.PSObject.Properties['deny']) {
        $Target['disabledTools'] = @($policy.deny | ForEach-Object { [string]$_ })
    }
}

function Test-IsAgencyMcpCommand {
    param([string[]]$Command)

    return $Command.Count -ge 3 -and $Command[0] -eq 'agency' -and $Command[1] -eq 'mcp'
}

function ConvertTo-McpCliCommandEntry {
    param([string[]]$Command)

    $entry = [ordered]@{
        command = $Command[0]
    }

    if ($Command.Count -gt 1) {
        $entry['args'] = @($Command | Select-Object -Skip 1)
    }

    return $entry
}

function ConvertTo-AgencyMcpCliCommandEntry {
    param([string[]]$Command, [string]$AgencyCommand = 'agency')

    $arguments = @('--verbosity', 'error') + @($Command | Select-Object -Skip 1)
    if ($Command[2] -ne 'remote') {
        # Local Agency servers run over stdio here. Remove only their HTTP
        # launch options; keep server-specific filters and configuration.
        $arguments = @('--verbosity', 'error', 'mcp', $Command[2])
        for ($index = 3; $index -lt $Command.Count; $index++) {
            if ($Command[$index] -in @('--port', '--transport')) {
                $index++
                continue
            }
            if ($Command[$index] -match '^--(?:port|transport)=') {
                continue
            }
            $arguments += $Command[$index]
        }
    }

    return [ordered]@{
        command = $AgencyCommand
        args = $arguments
    }
}

function ConvertTo-McpCliConfig {
    param([object]$ConfigObject)

    if ($null -eq $ConfigObject.PSObject.Properties['servers'] -or $null -eq $ConfigObject.servers) {
        throw 'Source config does not contain a servers object.'
    }

    $agencyCommand = 'agency'
    $agencyPathResolved = $false

    $mcpServers = [ordered]@{}
    foreach ($property in $ConfigObject.servers.PSObject.Properties) {
        $serverName = $property.Name
        if ($serverName -notmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$' -or $serverName.Length -gt 64) {
            throw "Invalid server name: $serverName. Use lowercase letters, digits, and single hyphens (max 64 characters)."
        }
        $serverConfig = $property.Value
        $enabled = $true
        if ($null -ne $serverConfig.PSObject.Properties['enabled']) {
            $enabled = [bool]$serverConfig.enabled
        }
        if (-not $enabled) {
            continue
        }

        $command = @($serverConfig.command | ForEach-Object { [string]$_ })
        if ($command.Count -eq 0) {
            Write-Warning "Skipping '$serverName' because it has no command."
            continue
        }

        $port = 3500
        if ($null -ne $serverConfig.PSObject.Properties['default_port']) {
            $port = [int]$serverConfig.default_port
        }

        $transport = Get-ServerTransport -ServerConfig $serverConfig
        $entry = [ordered]@{}

        if (Test-IsAgencyMcpCommand -Command $command) {
            if (-not $agencyPathResolved) {
                foreach ($propertyName in @('agency_path', 'agencyPath')) {
                    $pathProperty = $ConfigObject.PSObject.Properties[$propertyName]
                    if ($null -ne $pathProperty -and -not [string]::IsNullOrWhiteSpace([string]$pathProperty.Value)) {
                        $agencyCommand = Resolve-AgencyPath -RequestedPath ([string]$pathProperty.Value)
                        break
                    }
                }
                $agencyPathResolved = $true
            }
            $entry = ConvertTo-AgencyMcpCliCommandEntry -Command $command -AgencyCommand $agencyCommand
        }
        elseif ($transport -eq 'stdio') {
            $expandedCommand = @($command | ForEach-Object { $_.Replace('{port}', [string]$port).Replace('${PORT}', [string]$port) })
            $entry = ConvertTo-McpCliCommandEntry -Command $expandedCommand
        }
        else {
            $endpoint = Get-ServerEndpoint -ServerConfig $serverConfig
            $entry['url'] = "http://127.0.0.1:$port$endpoint"
        }

        Add-ToolPolicy -Target $entry -ServerConfig $serverConfig
        $mcpServers[$serverName] = $entry
    }

    return [ordered]@{
        mcpServers = $mcpServers
    }
}

function Write-McpCliConfig {
    param(
        [object]$ConfigObject,
        [string]$OutputConfig
    )

    $resolvedOutputConfig = Resolve-BuildPath -Path $OutputConfig
    $outputDirectory = Split-Path -Parent $resolvedOutputConfig
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }

    $output = ConvertTo-McpCliConfig -ConfigObject $ConfigObject
    $output | ConvertTo-Json -Depth 64 | Set-Content -Path $resolvedOutputConfig -Encoding UTF8
    Write-BuildLog "Generated config: $resolvedOutputConfig" 'DONE'
    Write-BuildLog "Servers included: $($output.mcpServers.Count)" 'DONE'
}

function Invoke-McpCliInfo {
    param(
        [string]$McpCli,
        [string]$ConfigPath,
        [string]$ServerName,
        [int]$TimeoutSeconds
    )

    $job = Start-Job -ScriptBlock {
        param($McpCli, $ConfigPath, $ServerName)
        $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
        [Console]::InputEncoding = $utf8NoBom
        [Console]::OutputEncoding = $utf8NoBom
        $OutputEncoding = $utf8NoBom
        $env:MCP_NO_DAEMON = '1'
        $output = & $McpCli -c $ConfigPath -d info $ServerName 2>&1
        [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Output = @($output)
        }
    } -ArgumentList $McpCli, $ConfigPath, $ServerName

    $completed = $job | Wait-Job -Timeout $TimeoutSeconds
    if ($null -eq $completed) {
        Stop-Job -Job $job -ErrorAction SilentlyContinue
        Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
        return [pscustomobject]@{
            ExitCode = -1
            Output = "Timed out after $TimeoutSeconds second(s)."
        }
    }

    $result = Receive-Job -Job $job
    Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
    return $result
}

function Get-ToolMetadataFromInfoText {
    param([string]$InfoText)

    $tools = New-Object System.Collections.Generic.List[object]
    $insideTools = $false
    $current = $null
    foreach ($line in ($InfoText -split "`r?`n")) {
        if ($line -match '^Tools \(\d+\):') {
            $insideTools = $true
            continue
        }
        if (-not $insideTools) {
            continue
        }
        if ($line -match '^  (?<name>[A-Za-z_][A-Za-z0-9_-]*)\s*$') {
            $current = [ordered]@{
                Name = $Matches['name'].Trim()
                Parameters = New-Object System.Collections.Generic.List[string]
            }
            [void]$tools.Add([pscustomobject]$current)
            continue
        }
        if ($null -ne $current -and $line -match '^\s+\u2022\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s+\(') {
            $parameterName = $Matches['name'].Trim()
            if (-not $current.Parameters.Contains($parameterName)) {
                [void]$current.Parameters.Add($parameterName)
            }
        }
    }

    return $tools.ToArray()
}

function Get-ToolNamesFromInfoText {
    param([string]$InfoText)

    return @(Get-ToolMetadataFromInfoText -InfoText $InfoText | ForEach-Object { $_.Name })
}

function Get-AutoGeneratedToolScriptHeader {
    param(
        [string]$ServerName,
        [string]$ScriptName,
        [string]$Description
    )

    return @"
<#
.SYNOPSIS
Auto-generated MCP tool wrapper for '$ServerName'.

.DESCRIPTION
This file was generated by the mcp-cli build pipeline for the '$ServerName' MCP server.
It is a generated runtime wrapper and is considered read-only. Any direct edits to this file will be overwritten when the skill package is regenerated.
Do not edit this file directly. Use the supported generated scripts as the runtime interface and update the source generator instead.

$Description

.NOTES
    Generated by: mcp-cli build pipeline
    Server: $ServerName
    Script: $ScriptName
    Warning: This file is auto-generated and may be overwritten without notice.
#>

"@
}

function Write-McpToolScripts {
    param(
        [string]$ScriptsDir,
        [string]$ServerName,
        [string]$ConfigReference,
        [string]$McpCli,
        [object[]]$ToolMetadata,
        [string]$LogDirectory = ''
    )

    if (Test-Path $ScriptsDir) {
        Remove-Item -Recurse -Force -Path $ScriptsDir
    }
    New-Item -ItemType Directory -Force -Path $ScriptsDir | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'scripts\Invoke-McpStdioBridge.ps1') -Destination $ScriptsDir

    $serverLiteral = ConvertTo-PowerShellSingleQuoted -Text $ServerName
    $configExpression = ConvertTo-PowerShellPathExpression -Path $ConfigReference
    $mcpCliExpression = ConvertTo-PowerShellPathExpression -Path $McpCli
    $logDirectoryExpression = ConvertTo-PowerShellPathExpression -Path $LogDirectory

    $runnerTemplate = @'
{{HEADER}}param(
    [Parameter(Mandatory = $true)]
    [string]$Tool,
    [string]$Json,
    [string]$JsonPath,
    [string]$ConfigPath = {{CONFIG_REFERENCE}},
    [string]$CliPath = {{MCP_CLI_PATH}}
)

$ErrorActionPreference = 'Stop'
$mcpToolkitLogDirectory = {{LOG_DIRECTORY}}

function Set-McpCliUtf8Encoding {
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [Console]::InputEncoding = $utf8NoBom
    [Console]::OutputEncoding = $utf8NoBom
    $script:OutputEncoding = $utf8NoBom
}

function Resolve-UserPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $Path
    }
    if ($Path -eq '~') {
        return $HOME
    }
    if ($Path.StartsWith('~/') -or $Path.StartsWith('~\')) {
        return (Join-Path $HOME $Path.Substring(2))
    }
    return $Path
}

function Repair-TextEncodingArtifacts {
    param([string]$Text)

    if ($null -eq $Text) {
        return $Text
    }

    $repaired = $Text
    $replacements = @(
        @('ΓÇó', [string][char]0x2022),
        @('ΓÇö', [string][char]0x2014),
        @('ΓÇô', [string][char]0x2013),
        @('ΓÇÖ', [string][char]0x2019),
        @('ΓÇ£', [string][char]0x201C),
        @('ΓÇ¥', [string][char]0x201D),
        @('ΓÇª', [string][char]0x2026),
        @('┬á', ' ')
    )

    foreach ($entry in $replacements) {
        $repaired = $repaired.Replace($entry[0], $entry[1])
    }

    return $repaired
}

function Write-McpToolCallLog {
    param([System.Collections.IDictionary]$Record)

    # Logging must never change the tool result, exit code, or output streams.
    try {
        $logSetting = [string]$env:MCP_SKILL_TOOLKIT_LOG
        if ($logSetting.Trim() -in @('0', 'off', 'false', 'no', 'disabled')) {
            return
        }
        $logDirectory = if (-not [string]::IsNullOrWhiteSpace($env:MCP_SKILL_TOOLKIT_LOG_DIR)) { $env:MCP_SKILL_TOOLKIT_LOG_DIR } else { $mcpToolkitLogDirectory }
        if ([string]::IsNullOrWhiteSpace($logDirectory)) {
            return
        }
        $logDirectory = Resolve-UserPath -Path $logDirectory
        New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
        $logPath = Join-Path $logDirectory ('tool-calls-{0}.jsonl' -f [DateTime]::UtcNow.ToString('yyyy-MM-dd'))
        $line = (ConvertTo-Json -InputObject $Record -Depth 5 -Compress) + "`n"
        $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($line)
        for ($attempt = 1; $attempt -le 5; $attempt++) {
            try {
                $stream = [IO.FileStream]::new($logPath, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
                try {
                    $stream.Write($bytes, 0, $bytes.Length)
                }
                finally {
                    $stream.Dispose()
                }
                return
            }
            catch [IO.IOException] {
                Start-Sleep -Milliseconds (25 * $attempt)
            }
        }
    }
    catch {
    }
}

Set-McpCliUtf8Encoding

$mcpToolkitStopwatch = [Diagnostics.Stopwatch]::StartNew()
$mcpToolkitCallLog = [ordered]@{
    schemaVersion = 1
    timestamp = [DateTime]::UtcNow.ToString('o')
    callId = [Guid]::NewGuid().ToString()
    server = {{SERVER_NAME}}
    tool = $Tool
    agent = if ([string]::IsNullOrWhiteSpace($env:MCP_SKILL_TOOLKIT_AGENT)) { $null } else { $env:MCP_SKILL_TOOLKIT_AGENT }
    processId = $PID
    inputSource = 'none'
    argumentNames = @()
    payloadBytes = $null
    status = 'rejected'
    exitCode = $null
    error = 'invalid-input'
    durationMs = $null
}
$exitCode = 1
$runtimeConfigPath = $null

try {
    if ($PSBoundParameters.ContainsKey('Json') -and $PSBoundParameters.ContainsKey('JsonPath')) {
        $mcpToolkitCallLog.error = 'conflicting-input'
        throw 'Use only one of -Json or -JsonPath.'
    }

    if ($PSBoundParameters.ContainsKey('Json')) {
        $mcpToolkitCallLog.inputSource = 'json'
        $payload = $Json
    }
    elseif ($PSBoundParameters.ContainsKey('JsonPath')) {
        $mcpToolkitCallLog.inputSource = 'jsonPath'
        $mcpToolkitCallLog.error = 'unreadable-input'
        $resolvedJsonPath = Resolve-UserPath -Path $JsonPath
        $payload = Get-Content -Raw -Path $resolvedJsonPath
    }
    elseif ([Console]::IsInputRedirected) {
        $mcpToolkitCallLog.inputSource = 'stdin'
        $mcpToolkitCallLog.error = 'unreadable-input'
        $payload = [Console]::In.ReadToEnd()
    }
    else {
        $payload = '{}'
    }

    if ([string]::IsNullOrWhiteSpace($payload)) {
        $payload = '{}'
    }

    $mcpToolkitCallLog.error = 'invalid-json'
    try {
        $parsedPayload = ConvertFrom-Json -InputObject $payload -Depth 100 -NoEnumerate
        $payload = ConvertTo-Json -InputObject $parsedPayload -Depth 100 -Compress
    }
    catch {
        throw "Invalid JSON payload for ${Tool}: $($_.Exception.Message)"
    }
    # Record argument names and size only; payload values may contain sensitive data.
    if ($parsedPayload -is [System.Management.Automation.PSCustomObject]) {
        $mcpToolkitCallLog.argumentNames = @($parsedPayload.PSObject.Properties | ForEach-Object { $_.Name })
    }
    $mcpToolkitCallLog.payloadBytes = [System.Text.Encoding]::UTF8.GetByteCount($payload)

    $resolvedConfigPath = Resolve-UserPath -Path $ConfigPath
    $resolvedCliPath = Resolve-UserPath -Path $CliPath

    $mcpToolkitCallLog.error = 'invalid-config'
    $runtimeConfig = Get-Content -LiteralPath $resolvedConfigPath -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 100
    if (-not ($runtimeConfig.mcpServers -is [System.Collections.IDictionary]) -or
        -not $runtimeConfig.mcpServers.Contains({{SERVER_NAME}})) {
        throw ('MCP configuration does not contain server: ' + {{SERVER_NAME}})
    }
    $serverConfig = $runtimeConfig.mcpServers[{{SERVER_NAME}}]
    if (-not $serverConfig.Contains('url') -and $serverConfig.Contains('command')) {
        # Keep argv as separate strings so MCP CLI's env substitution and quoting
        # still apply. Only the selected stdio server gets the session bridge.
        $serverArguments = if ($serverConfig.Contains('args')) { @($serverConfig.args) } else { @() }
        $serverConfig.args = @(
            '-NoLogo', '-NoProfile', '-NonInteractive', '-File',
            (Join-Path $PSScriptRoot 'Invoke-McpStdioBridge.ps1'),
            [string]$serverConfig.command
        ) + $serverArguments
        $serverConfig.command = (Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' }))
        $runtimeConfigPath = Join-Path ([IO.Path]::GetTempPath()) ('mcp-skill-toolkit-' + [Guid]::NewGuid().ToString('N') + '.json')
        $runtimeConfig | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $runtimeConfigPath -Encoding utf8
        $resolvedConfigPath = $runtimeConfigPath
    }

    $mcpToolkitCallLog.status = 'failed'
    $mcpToolkitCallLog.error = 'cli-error'
    $output = $payload | & $resolvedCliPath -c $resolvedConfigPath call {{SERVER_NAME}} $Tool 2>&1
    $exitCode = $LASTEXITCODE
    $mcpToolkitCallLog.exitCode = $exitCode
    if ($exitCode -eq 0) {
        $mcpToolkitCallLog.status = 'succeeded'
        $mcpToolkitCallLog.error = $null
    }
    else {
        $mcpToolkitCallLog.error = 'nonzero-exit'
    }
    $text = ($output | Out-String).TrimEnd()
    if (-not [string]::IsNullOrEmpty($text)) {
        Write-Output (Repair-TextEncodingArtifacts -Text $text)
    }
}
finally {
    $mcpToolkitStopwatch.Stop()
    $mcpToolkitCallLog.durationMs = [long]$mcpToolkitStopwatch.ElapsedMilliseconds
    Write-McpToolCallLog -Record $mcpToolkitCallLog
    if ($runtimeConfigPath -and (Test-Path -LiteralPath $runtimeConfigPath)) {
        Remove-Item -LiteralPath $runtimeConfigPath -Force
    }
}

exit $exitCode
'@

    $runnerHeader = Get-AutoGeneratedToolScriptHeader -ServerName $ServerName -ScriptName 'Invoke-Tool.ps1' -Description "Generic runner for invoking MCP tools for the '$ServerName' server."
    $runner = $runnerTemplate.Replace('{{HEADER}}', $runnerHeader).Replace('{{SERVER_NAME}}', $serverLiteral).Replace('{{CONFIG_REFERENCE}}', $configExpression).Replace('{{MCP_CLI_PATH}}', $mcpCliExpression).Replace('{{LOG_DIRECTORY}}', $logDirectoryExpression)
    $runner | Set-Content -Encoding UTF8 -Path (Join-Path $ScriptsDir 'Invoke-Tool.ps1')

    $reservedParameterNames = @(
        'Json', 'JsonPath', 'PSBoundParameters', 'PSScriptRoot', 'PSCommandPath',
        'args', 'input', 'this', 'null', 'true', 'false', 'HOME', 'PID', 'Host',
        'Error', 'ExecutionContext', 'PSVersionTable'
    )
    $generatedToolNames = @('Invoke-Tool', 'Invoke-McpStdioBridge')
    foreach ($tool in @($ToolMetadata)) {
        $toolName = [string]$tool.Name
        if ($toolName -notmatch '^[A-Za-z_][A-Za-z0-9_-]*$' -or $generatedToolNames -contains $toolName) {
            continue
        }
        $generatedToolNames += $toolName

        $toolLiteral = ConvertTo-PowerShellSingleQuoted -Text $toolName
        $parameterDeclarations = New-Object System.Collections.Generic.List[string]
        [void]$parameterDeclarations.Add('    [string]$Json')
        [void]$parameterDeclarations.Add('    [string]$JsonPath')

        $declaredParameterNames = @()
        foreach ($parameterName in @($tool.Parameters)) {
            if ($parameterName -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
                continue
            }
            if ($reservedParameterNames -contains $parameterName -or $declaredParameterNames -contains $parameterName) {
                continue
            }
            $declaredParameterNames += $parameterName
            [void]$parameterDeclarations.Add("    [object]`$$parameterName")
        }

        $paramBlock = ($parameterDeclarations.ToArray() -join ",`r`n")
        $wrapperTemplate = @'
{{HEADER}}param(
{{PARAMETERS}}
)

$ErrorActionPreference = 'Stop'

$mcpToolkitRunner = Join-Path $PSScriptRoot 'Invoke-Tool.ps1'
$mcpToolkitRunnerArguments = @{
    Tool = {{TOOL_NAME}}
}

# The generic runner validates, rejects, and logs conflicting JSON inputs.
if ($PSBoundParameters.ContainsKey('Json') -or $PSBoundParameters.ContainsKey('JsonPath')) {
    if ($PSBoundParameters.ContainsKey('Json')) {
        $mcpToolkitRunnerArguments.Json = $Json
    }
    if ($PSBoundParameters.ContainsKey('JsonPath')) {
        $mcpToolkitRunnerArguments.JsonPath = $JsonPath
    }
}
else {
    if (@($PSBoundParameters.Keys).Length -gt 0) {
        $mcpToolkitRunnerArguments.Json = ($PSBoundParameters | ConvertTo-Json -Depth 100 -Compress)
    }
    # With no named payload, let the generic runner read stdin or default to {}.
}

& $mcpToolkitRunner @mcpToolkitRunnerArguments
exit $LASTEXITCODE
'@

        $wrapperHeader = Get-AutoGeneratedToolScriptHeader -ServerName $ServerName -ScriptName "$toolName.ps1" -Description "Per-tool wrapper for the '$toolName' MCP tool on the '$ServerName' server."
        $wrapper = $wrapperTemplate.Replace('{{HEADER}}', $wrapperHeader).Replace('{{PARAMETERS}}', $paramBlock).Replace('{{TOOL_NAME}}', $toolLiteral)
        $wrapper | Set-Content -Encoding UTF8 -Path (Join-Path $ScriptsDir "$toolName.ps1")
    }
}

function Write-McpCliSkillPackage {
    param(
        [string]$ServerDir,
        [string]$ServerName,
        [object]$ServerConfig,
        [string]$ConfigPath,
        [string]$ConfigReference,
        [string]$McpCli,
        [string]$InfoText,
        [object[]]$ToolMetadata,
        [string]$LogDirectory = ''
    )

    $referencesDir = Join-Path $ServerDir 'references'
    New-Item -ItemType Directory -Force -Path $referencesDir | Out-Null

    $scriptsDir = Join-Path $ServerDir 'scripts'
    Write-McpToolScripts -ScriptsDir $scriptsDir -ServerName $ServerName -ConfigReference $ConfigReference -McpCli $McpCli -ToolMetadata $ToolMetadata -LogDirectory $LogDirectory

    $description = Format-SkillDescription -Description ([string]$ServerConfig.description) -ServerName $ServerName
    $ToolNames = @($ToolMetadata | ForEach-Object { $_.Name })
    $toolCount = @($ToolNames).Count
    $toolList = if ($toolCount -gt 0) {
        (@($ToolNames) | ForEach-Object { "- ``$_``" }) -join "`r`n"
    }
    else {
        'No tools were discovered at build time. Use the discovery commands below at runtime.'
    }

    $skillMarkdown = @"
---
name: $(ConvertTo-YamlSingleQuoted $ServerName)
description: $(ConvertTo-YamlSingleQuoted $description)
---

# $ServerName

$description

## Skill-Specific Guidance

Before using this skill, check whether ``references/custom-instructions.md`` exists. If it exists, read it first and follow its guidance because it contains user-created instructions for the current environment. These custom instructions may override guidance in this generated skill; when they conflict, follow ``references/custom-instructions.md`` first.

## Generated Tool Scripts

STOP: use the generated PowerShell scripts only. Do not launch servers. Do not start background processes. Do not run direct command-line tool calls. Do not use discovery commands unless the generated scripts are missing or broken.

Every generated skill package includes ``scripts/Invoke-Tool.ps1``. When tools are discovered at build time, it also includes one wrapper per tool, for example ``scripts/<tool>.ps1``.

Prefer the per-tool wrapper first. These scripts handle UTF-8, JSON validation, config routing, and safe stdin piping.

For stdio servers, the scripts automatically list tools on the active server connection before the first tool call. Do not run a separate discovery command to initialize a session, and do not invoke ``scripts/Invoke-McpStdioBridge.ps1`` directly. HTTP servers keep their existing transport behavior.

Use one JSON blob:

``````powershell
pwsh -NoProfile -File scripts/<tool>.ps1 -Json '{"name":"value"}'
``````

Use a JSON file:

``````powershell
`$payloadPath = Join-Path `$env:TEMP 'tool-payload.json'
Set-Content -Encoding UTF8 -Path `$payloadPath -Value '{"name":"value"}'
pwsh -NoProfile -File scripts/<tool>.ps1 -JsonPath `$payloadPath
``````

When a per-tool wrapper has generated parameters, you can also pass them by name, for example ``pwsh -NoProfile -File scripts/<tool>.ps1 -name value``. If a wrapper is missing, call the generic runner with ``-Tool <tool>``.

## Workflow

1. Read ``references/custom-instructions.md`` if it exists.
2. Choose the tool from the discovered tool list. Use ``references/tool-info.md`` only to read parameter descriptions; do not run commands from that file.
3. Call the per-tool wrapper in ``scripts/<tool>.ps1`` when present.
4. If no per-tool wrapper exists, call ``scripts/Invoke-Tool.ps1`` with ``-Tool <tool>``.
5. Stop after the script returns the result.

## Discovered Tools

Discovered $toolCount tool(s):

$toolList

Full build-time tool information is stored in ``references/tool-info.md``. Treat that file as read-only reference material.
"@

    $infoMarkdown = @"
# $ServerName Tool Information

Generated from:

``````powershell
mcp-cli -c $ConfigPath -d info $ServerName
``````

``````text
$InfoText
``````
"@

    $skillMarkdown | Set-Content -Encoding UTF8 -Path (Join-Path $ServerDir 'SKILL.md')
    $infoMarkdown | Set-Content -Encoding UTF8 -Path (Join-Path $referencesDir 'tool-info.md')
}

function Invoke-SkillDeployment {
    param([string]$SourceDirectory, [string]$TargetDirectory)

    $sourceRoot = Resolve-BuildPath -Path $SourceDirectory
    $targetRoot = Resolve-BuildPath -Path $TargetDirectory
    if (-not (Test-Path $sourceRoot)) {
        throw "Source skills directory not found: $sourceRoot. Run the Build task first."
    }

    Write-Host "[mcp-cli-deploy][INFO] Source skills: $sourceRoot"
    Write-Host "[mcp-cli-deploy][INFO] Target skills: $targetRoot"
    New-Item -ItemType Directory -Force -Path $targetRoot | Out-Null

    $sourceSkills = @(Get-ChildItem -Path $sourceRoot -Directory -ErrorAction SilentlyContinue)
    $preservedCustomInstructionCount = 0
    foreach ($sourceSkill in $sourceSkills) {
        $targetSkillDir = Join-Path $targetRoot $sourceSkill.Name
        $customInstructionsPath = Join-Path $targetSkillDir 'references\custom-instructions.md'
        $preservedCustomInstructions = $null

        if (Test-Path $customInstructionsPath) {
            $preservedCustomInstructions = [IO.File]::ReadAllBytes($customInstructionsPath)
            $preservedCustomInstructionCount++
        }

        if (Test-Path $targetSkillDir) {
            $targetItem = Get-Item -LiteralPath $targetSkillDir -Force
            if (-not $targetItem.PSIsContainer) {
                Remove-Item -LiteralPath $targetSkillDir -Force
                New-Item -ItemType Directory -Force -Path $targetSkillDir | Out-Null
            }
            else {
                Get-ChildItem -Path $targetSkillDir -Force -ErrorAction SilentlyContinue |
                    Remove-Item -Recurse -Force
            }
        }
        else {
            New-Item -ItemType Directory -Force -Path $targetSkillDir | Out-Null
        }

        Get-ChildItem -Path $sourceSkill.FullName -Force |
            Copy-Item -Destination $targetSkillDir -Recurse -Force

        if ($null -ne $preservedCustomInstructions) {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $customInstructionsPath) | Out-Null
            [IO.File]::WriteAllBytes($customInstructionsPath, $preservedCustomInstructions)
        }
    }

    Write-Host "[mcp-cli-deploy][DONE] Deployed $($sourceSkills.Count) skill package(s)"
    Write-Host "[mcp-cli-deploy][DONE] Preserved $preservedCustomInstructionCount custom instruction file(s)"
}

Task Initialize -Description 'Load configuration and prepare output directories.' {
    Set-McpCliUtf8Encoding
    $script:SourceConfigPath = (Resolve-Path -Path (Resolve-UserPath -Path $SourceConfig)).Path
    $script:SourceConfigObject = Get-Content -Raw -Path $script:SourceConfigPath | ConvertFrom-Json -Depth 100
    $script:OutputRoot = Resolve-BuildPath -Path $OutputDir
    $script:ConfigReference = if ($McpConfigPath -eq '~' -or $McpConfigPath.StartsWith('~/') -or $McpConfigPath.StartsWith('~\')) { $McpConfigPath } else { Resolve-BuildPath -Path $McpConfigPath }
    $script:ConfigPath = Resolve-BuildPath -Path $McpConfigPath
    $script:ToolCallLogReference = if ([string]::IsNullOrWhiteSpace($ToolCallLogDirectory)) { '' } elseif ($ToolCallLogDirectory -eq '~' -or $ToolCallLogDirectory.StartsWith('~/') -or $ToolCallLogDirectory.StartsWith('~\')) { $ToolCallLogDirectory } else { Resolve-BuildPath -Path $ToolCallLogDirectory }
    $script:SkillsRoot = Join-Path $script:OutputRoot 'skills'
    $script:DiscoveryRoot = Join-Path $script:OutputRoot 'discovery'

    New-Item -ItemType Directory -Force -Path $script:OutputRoot, $script:SkillsRoot, $script:DiscoveryRoot | Out-Null
    Get-ChildItem -Path $script:SkillsRoot -Filter 'tool_policy.json' -Recurse -ErrorAction SilentlyContinue |
        Remove-Item -Force
    Get-ChildItem -Path $script:SkillsRoot -Directory -Filter 'scripts' -Recurse -ErrorAction SilentlyContinue |
        Where-Object { -not @(Get-ChildItem -Path $_.FullName -Force -ErrorAction SilentlyContinue).Count } |
        Remove-Item -Force
}

Task Config -Depends Initialize -Description 'Generate the mcp-cli configuration.' {
    Write-BuildLog "Generating mcp-cli config from $script:SourceConfigPath"
    Write-McpCliConfig -ConfigObject $script:SourceConfigObject -OutputConfig $script:ConfigPath
}

Task SyncAgencyServers -Description 'Add missing Agency MCP servers to servers.json with unique ports.' {
    $helpText = $AgencyHelpText
    $resolvedAgencyPath = $null
    if ([string]::IsNullOrWhiteSpace($helpText)) {
        $resolvedAgencyPath = Resolve-AgencyPath -RequestedPath $AgencyPath
        Write-BuildLog "Reading MCP servers from $resolvedAgencyPath"
        $helpOutput = & $resolvedAgencyPath mcp --help 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "agency mcp --help failed with exit code $LASTEXITCODE."
        }
        $helpText = ($helpOutput | Out-String)
    }

    $serverHelpByName = @{}
    if (-not [string]::IsNullOrWhiteSpace($AgencyServerHelpJson)) {
        $serverHelpByName = ConvertFrom-Json -InputObject $AgencyServerHelpJson -AsHashtable
    }
    elseif ($null -ne $resolvedAgencyPath) {
        foreach ($server in @(Get-AgencyMcpServersFromHelp -HelpText $helpText)) {
            $detailOutput = & $resolvedAgencyPath mcp $server.Name --help 2>&1
            if ($LASTEXITCODE -ne 0) {
                Write-BuildLog "Could not read configuration options for $($server.Name)." 'WARN'
                continue
            }
            $serverHelpByName[$server.Name] = ($detailOutput | Out-String)
        }
    }

    $changes = @(Sync-AgencyMcpServers -ConfigPath $SourceConfig -HelpText $helpText -ServerHelpByName $serverHelpByName)
    if ($changes.Count -eq 0) {
        Write-BuildLog 'All Agency MCP servers are already present.' 'DONE'
        return
    }

    foreach ($change in $changes) {
        if ($change.Action -eq 'Reassigned') {
            Write-BuildLog "Reassigned $($change.Name) from port $($change.OldPort) to $($change.Port)" 'DONE'
        }
        else {
            Write-BuildLog "Added $($change.Name) on port $($change.Port)" 'DONE'
        }
    }
}

Task Package -Description 'Package only public source, docs, example configuration and tests.' {
    $archivePath = New-SetupArchive -SourceDirectory $PSScriptRoot -DestinationPath $PackagePath
    Write-BuildLog "Created setup package: $archivePath" 'DONE'
}

Task Discover -Depends Config -Description 'Discover tools for selected servers unless discovery is skipped.' {
    $configuredMcpCliPath = Get-ConfiguredMcpCliPath -ConfigObject $script:SourceConfigObject
    $effectiveMcpCliPath = if (-not [string]::IsNullOrWhiteSpace($McpCliPath)) { $McpCliPath } else { $configuredMcpCliPath }
    $requestedServers = @($SelectedServerNames -split '\s*[,|]\s*' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $script:BuildServers = Get-SourceServerMap -ConfigObject $script:SourceConfigObject -SelectedServers $requestedServers
    $script:DiscoveryResults = @{}
    $script:ScriptMcpCliPath = if (-not [string]::IsNullOrWhiteSpace($effectiveMcpCliPath)) { $effectiveMcpCliPath } else { 'mcp-cli' }

    if ($script:BuildServers.Count -eq 0) {
        Write-BuildLog 'No selected servers to build.' 'SKIP'
        return
    }

    if ([bool]$SkipDiscovery) {
        Write-BuildLog 'Live discovery skipped.' 'SKIP'
    }
    else {
        $script:McpCli = Resolve-McpCliPath -RequestedPath $effectiveMcpCliPath
        $script:ScriptMcpCliPath = $script:McpCli
        Write-BuildLog "Using mcp-cli: $script:McpCli"
    }

    foreach ($entry in $script:BuildServers.GetEnumerator()) {
        $serverName = $entry.Key
        $infoText = 'Discovery was skipped.'
        $toolMetadata = @()
        $exitCode = 0

        if (-not [bool]$SkipDiscovery) {
            Write-BuildLog "[$serverName] Discovering tools with mcp-cli"
            $result = Invoke-McpCliInfo -McpCli $script:McpCli -ConfigPath $script:ConfigPath -ServerName $serverName -TimeoutSeconds $DiscoveryTimeoutSeconds
            $exitCode = [int]$result.ExitCode
            $infoText = Repair-TextEncodingArtifacts -Text (($result.Output | Out-String).Trim())
            if ([string]::IsNullOrWhiteSpace($infoText)) {
                $infoText = 'mcp-cli returned no output.'
            }
            $infoText | Set-Content -Encoding UTF8 -Path (Join-Path $script:DiscoveryRoot "$serverName.info.txt")
            if ($exitCode -eq 0) {
                $toolMetadata = @(Get-ToolMetadataFromInfoText -InfoText $infoText)
                Write-BuildLog "[$serverName] Discovered $($toolMetadata.Count) tool(s)" 'DONE'
            }
            else {
                Write-BuildLog "[$serverName] Discovery failed with exit code $exitCode; generating runtime-discovery skill" 'WARN'
            }
        }

        $script:DiscoveryResults[$serverName] = [pscustomobject]@{
            ExitCode = $exitCode
            InfoText = $infoText
            ToolMetadata = $toolMetadata
        }
    }
}

Task GenerateSkills -Depends Discover -Description 'Generate skill packages from configuration and discovery results.' {
    if ($script:BuildServers.Count -eq 0) {
        return
    }

    Write-BuildLog "Generating skill package(s): $($script:BuildServers.Keys -join ', ')"
    $summary = New-Object System.Collections.Generic.List[object]
    foreach ($entry in $script:BuildServers.GetEnumerator()) {
        $serverName = $entry.Key
        $discovery = $script:DiscoveryResults[$serverName]
        Write-Host ''
        Write-BuildLog "[$serverName] Building skill package"

        $serverDir = Join-Path $script:SkillsRoot $serverName
        Write-McpCliSkillPackage -ServerDir $serverDir -ServerName $serverName -ServerConfig $entry.Value -ConfigPath $script:ConfigPath -ConfigReference $script:ConfigReference -McpCli $script:ScriptMcpCliPath -InfoText $discovery.InfoText -ToolMetadata $discovery.ToolMetadata -LogDirectory $script:ToolCallLogReference
        [void]$summary.Add([pscustomobject]@{
            server = $serverName
            discoveryExitCode = $discovery.ExitCode
            toolCount = @($discovery.ToolMetadata).Count
            skillPath = Join-Path $serverDir 'SKILL.md'
        })
    }

    $summaryPath = Join-Path $script:OutputRoot 'build-summary.json'
    $summary | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -Path $summaryPath
    Write-Host ''
    Write-BuildLog "Generated config: $script:ConfigPath" 'DONE'
    Write-BuildLog "Generated skills: $script:SkillsRoot" 'DONE'
    Write-BuildLog "Discovery logs: $script:DiscoveryRoot" 'DONE'
    Write-BuildLog "Summary: $summaryPath" 'DONE'
}

Task Build -Depends GenerateSkills -Description 'Generate configuration and skill packages.'

Task Test -Description 'Run the Pester test suite.' {
    if (-not @(Get-Module -ListAvailable -Name Pester | Where-Object Version -eq ([version]'5.7.1')).Count) {
        throw 'Pester 5.7.1 not found. Run init.ps1 and retry.'
    }

    # Pester versions load conflicting assemblies. Use a fresh process so the
    # pinned runner also works when a different Pester is already imported.
    $previousTestPath = $env:MCP_SKILL_TOOLKIT_TEST_PATH
    $testCommand = '$ErrorActionPreference = ''Stop''; Import-Module psake -RequiredVersion 5.0.4; Import-Module Pester -RequiredVersion 5.7.1; $results = Invoke-Pester -Path $env:MCP_SKILL_TOOLKIT_TEST_PATH -Output Detailed -PassThru; if ($results.FailedCount -gt 0 -or $results.TotalCount -eq 0) { exit 1 }'
    try {
        $env:MCP_SKILL_TOOLKIT_TEST_PATH = Resolve-BuildPath -Path $TestPath
        $executableName = if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' }
        & (Join-Path $PSHOME $executableName) -NoLogo -NoProfile -NonInteractive -Command $testCommand
        if ($LASTEXITCODE -ne 0) {
            throw "Pester test process failed (exit code $LASTEXITCODE)."
        }
    }
    finally {
        $env:MCP_SKILL_TOOLKIT_TEST_PATH = $previousTestPath
    }
}

Task Deploy -Description 'Deploy generated skill packages while preserving custom instructions.' {
    Invoke-SkillDeployment -SourceDirectory $SourceSkillsDir -TargetDirectory $TargetSkillsDir
}

Task Default -Depends Test -Description 'Run the test suite.'