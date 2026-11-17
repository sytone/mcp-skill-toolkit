[CmdletBinding()]
param(
    [switch]$SkipMcpCli,
    [ValidateSet('dotnet', 'bun')]
    [string]$McpCliBackend
)

$ErrorActionPreference = 'Stop'

function Write-InitLog {
    param([string]$Message, [string]$Label = 'INFO')
    Write-Host "[mcp-cli-init][$Label] $Message"
}

function Install-RequiredModule {
    param(
        [string]$Name,
        [version]$RequiredVersion,
        [version]$MinimumVersion
    )

    $available = @(Get-Module -ListAvailable -Name $Name)
    $installed = if ($null -ne $RequiredVersion) {
        $available | Where-Object Version -eq $RequiredVersion | Select-Object -First 1
    }
    else {
        $available | Where-Object Version -ge $MinimumVersion | Sort-Object Version -Descending | Select-Object -First 1
    }

    if ($null -eq $installed) {
        $installArguments = @{
            Name = $Name
            Scope = 'CurrentUser'
            Force = $true
            AllowClobber = $true
        }
        if ($null -ne $RequiredVersion) {
            $installArguments.RequiredVersion = $RequiredVersion
        }
        else {
            $installArguments.MinimumVersion = $MinimumVersion
        }

        Write-InitLog "Installing $Name..."
        Install-Module @installArguments
        $installed = Get-Module -ListAvailable -Name $Name |
            Where-Object {
                if ($null -ne $RequiredVersion) { $_.Version -eq $RequiredVersion }
                else { $_.Version -ge $MinimumVersion }
            } |
            Sort-Object Version -Descending |
            Select-Object -First 1
    }

    if ($null -eq $installed) {
        throw "Unable to install required module: $Name"
    }

    Write-InitLog "$Name $($installed.Version) is available." 'DONE'
}

function Find-McpCli {
    param([switch]$IgnoreConfiguredPath)

    $command = Get-Command mcp-cli -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    foreach ($candidate in @(
        (Join-Path $HOME '.bun\bin\mcp-cli.exe'),
        (Join-Path $HOME '.bun\bin\mcp-cli')
    )) {
        if (Test-Path $candidate) {
            return (Resolve-Path $candidate).Path
        }
    }

    if (-not $IgnoreConfiguredPath -and (Test-Path (Join-Path $PSScriptRoot 'servers.json'))) {
        $config = Get-Content -Raw -Path (Join-Path $PSScriptRoot 'servers.json') | ConvertFrom-Json
        $configuredPath = [string]$config.mcp_cli_path
        if (-not [string]::IsNullOrWhiteSpace($configuredPath) -and $configuredPath -ne 'mcp-cli') {
            if ($configuredPath -eq '~') {
                $configuredPath = $HOME
            }
            elseif ($configuredPath.StartsWith('~/') -or $configuredPath.StartsWith('~\')) {
                $configuredPath = Join-Path $HOME $configuredPath.Substring(2)
            }
            if (Test-Path $configuredPath) {
                return (Resolve-Path $configuredPath).Path
            }
        }
    }

    return $null
}

function Get-McpCliBackend {
    param(
        [string]$RequestedBackend,
        [string]$ConfigPath = (Join-Path $PSScriptRoot 'servers.json')
    )

    $backend = $RequestedBackend
    if ([string]::IsNullOrWhiteSpace($backend) -and (Test-Path -LiteralPath $ConfigPath)) {
        $config = Get-Content -Raw -LiteralPath $ConfigPath | ConvertFrom-Json
        $backend = [string]$config.mcp_cli_backend
    }
    if ([string]::IsNullOrWhiteSpace($backend)) {
        $backend = 'dotnet'
    }

    $backend = $backend.Trim().ToLowerInvariant()
    if ($backend -notin @('dotnet', 'bun')) {
        throw "Unsupported MCP CLI backend '$backend'. Choose 'dotnet' or 'bun'."
    }

    return $backend
}

function Find-DotNetMcpCli {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnet) {
        throw '.NET SDK 10 is required for the dotnet MCP CLI backend. Install the SDK or select -McpCliBackend bun.'
    }

    $projectPath = Join-Path $PSScriptRoot 'src\McpCli\McpCli.csproj'
    & $dotnet.Source build $projectPath --configuration Release --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Building the local MCP CLI failed (exit code $LASTEXITCODE)."
    }

    $cliPath = Join-Path $PSScriptRoot 'src\McpCli\bin\Release\net10.0\mcp-cli.exe'
    if (-not (Test-Path -LiteralPath $cliPath)) {
        throw "The local MCP CLI build succeeded but its executable was not found: $cliPath"
    }

    return (Resolve-Path -LiteralPath $cliPath).Path
}

function Get-McpCliExecutable {
    param(
        [string]$Backend,
        [switch]$IgnoreConfiguredPath
    )

    if ($Backend -eq 'dotnet') {
        return Find-DotNetMcpCli
    }

    $mcpCli = Find-McpCli -IgnoreConfiguredPath:$IgnoreConfiguredPath
    if ($null -ne $mcpCli) {
        return $mcpCli
    }

    $bunCommand = Get-Command bun -ErrorAction SilentlyContinue
    $bunPath = if ($null -ne $bunCommand) { $bunCommand.Source } else { $null }
    if ($null -eq $bunPath) {
        $installedBunPath = Join-Path $HOME '.bun\bin\bun.exe'
        if (Test-Path $installedBunPath) {
            $bunPath = $installedBunPath
        }
    }
    if ($null -eq $bunPath) {
        Write-InitLog 'Installing Bun...'
        Invoke-RestMethod https://bun.sh/install.ps1 | Invoke-Expression
        $installedBunPath = Join-Path $HOME '.bun\bin\bun.exe'
        if (Test-Path $installedBunPath) {
            $bunPath = $installedBunPath
        }
    }
    if ($null -eq $bunPath) {
        throw 'Bun could not be installed. Install Bun and rerun init.ps1.'
    }

    Write-InitLog 'Installing mcp-cli from GitHub...'
    & $bunPath install -g github:philschmid/mcp-cli
    if ($LASTEXITCODE -ne 0) {
        throw "Bun failed to install mcp-cli (exit code $LASTEXITCODE)."
    }

    return Find-McpCli
}

function Find-Agency {
    $command = Get-Command agency -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    foreach ($candidate in @(
        (Join-Path $env:APPDATA 'agency\CurrentVersion\agency.exe'),
        'C:\.Tools\agency\CurrentVersion\agency.exe'
    )) {
        if (Test-Path $candidate) {
            return (Resolve-Path $candidate).Path
        }
    }

    return $null
}

function Test-ConfigRequiresAgency {
    param([string]$ConfigPath = (Join-Path $PSScriptRoot 'servers.json'))

    if (-not (Test-Path -LiteralPath $ConfigPath)) {
        return $false
    }

    $config = Get-Content -Raw -LiteralPath $ConfigPath | ConvertFrom-Json -AsHashtable -Depth 100
    if (-not $config.Contains('servers') -or $null -eq $config.servers) {
        return $false
    }

    foreach ($server in $config.servers.Values) {
        if ($server.Contains('enabled') -and -not [bool]$server.enabled) {
            continue
        }

        $command = @($server.command)
        if ($command.Count -gt 0) {
            $executableName = ([string]$command[0] -split '[\\/]')[-1]
            if ($executableName -match '^agency(?:\.(?:exe|cmd|bat|ps1))?$') {
                return $true
            }
        }
    }

    return $false
}

function ConvertTo-LocalConfigPath {
    param([string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $homePath = [IO.Path]::GetFullPath($HOME)
    $homePrefix = $homePath.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($fullPath.StartsWith($homePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        return '~\' + $fullPath.Substring($homePrefix.Length)
    }
    return $fullPath
}

function Set-LocalToolPaths {
    param(
        [string]$McpCliPath,
        [string]$McpCliBackend,
        [string]$AgencyPath,
        [string]$ConfigPath = (Join-Path $PSScriptRoot 'servers.json')
    )

    if (-not (Test-Path $ConfigPath)) {
        Write-InitLog 'servers.json was not found; skipping local path update.' 'SKIP'
        return
    }

    $config = Get-Content -Raw -Path $ConfigPath | ConvertFrom-Json -AsHashtable -Depth 100
    $configuredMcpCliPath = ConvertTo-LocalConfigPath -Path $McpCliPath
    $changed = $config.mcp_cli_path -ne $configuredMcpCliPath -or
        $config.mcp_cli_backend -ne $McpCliBackend
    $config.mcp_cli_path = $configuredMcpCliPath
    $config.mcp_cli_backend = $McpCliBackend

    if (-not [string]::IsNullOrWhiteSpace($AgencyPath)) {
        $configuredAgencyPath = ConvertTo-LocalConfigPath -Path $AgencyPath
        $changed = $changed -or $config.agency_path -ne $configuredAgencyPath
        $config.agency_path = $configuredAgencyPath
    }

    if (-not $changed) {
        Write-InitLog 'Local tool paths are already current.' 'DONE'
        return
    }

    $temporaryPath = "$ConfigPath.tmp"
    $config | ConvertTo-Json -Depth 100 | Set-Content -Encoding UTF8 -Path $temporaryPath
    Move-Item -Force -Path $temporaryPath -Destination $ConfigPath
    Write-InitLog 'Updated local tool paths in servers.json.' 'DONE'
}

function Initialize-ConfiguredToolPaths {
    param(
        [string]$McpCliPath,
        [string]$McpCliBackend,
        [string]$ConfigPath = (Join-Path $PSScriptRoot 'servers.json')
    )

    $McpCliBackend = Get-McpCliBackend -RequestedBackend $McpCliBackend -ConfigPath $ConfigPath
    $agency = $null
    if (Test-ConfigRequiresAgency -ConfigPath $ConfigPath) {
        $agency = Find-Agency
        if ($null -eq $agency) {
            throw 'Agency is required by an enabled server command but was not found. Install Agency or disable that server and rerun init.ps1.'
        }
        Write-InitLog "Agency is available at $agency" 'DONE'
    }
    else {
        Write-InitLog 'No enabled server command requires Agency; skipping its path check.' 'SKIP'
    }

    Set-LocalToolPaths -McpCliPath $McpCliPath -McpCliBackend $McpCliBackend -AgencyPath $agency -ConfigPath $ConfigPath
}

Install-RequiredModule -Name psake -RequiredVersion ([version]'5.0.4')
Install-RequiredModule -Name Pester -RequiredVersion ([version]'5.7.1')

if ($SkipMcpCli.IsPresent) {
    Write-InitLog 'Skipping MCP CLI setup and checks.' 'SKIP'
    return
}

$configuredBackend = Get-McpCliBackend
$selectedBackend = Get-McpCliBackend -RequestedBackend $McpCliBackend
$ignoreConfiguredPath = -not [string]::IsNullOrWhiteSpace($McpCliBackend) -and $selectedBackend -ne $configuredBackend
$mcpCli = Get-McpCliExecutable -Backend $selectedBackend -IgnoreConfiguredPath:$ignoreConfiguredPath

if ($null -eq $mcpCli) {
    throw "The $selectedBackend MCP CLI was prepared but could not be resolved."
}

Write-InitLog "mcp-cli is available at $mcpCli" 'DONE'

Initialize-ConfiguredToolPaths -McpCliPath $mcpCli -McpCliBackend $selectedBackend
