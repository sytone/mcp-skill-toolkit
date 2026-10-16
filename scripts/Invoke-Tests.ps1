[CmdletBinding()]
param(
    [switch]$Coverage,
    [ValidateRange(0, 100)]
    [double]$MinimumCoverage = 75,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'test-results'
}
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

Import-Module psake -RequiredVersion 5.0.4 -Force
Import-Module Pester -RequiredVersion 5.7.1 -Force

# Fail on syntax errors in source and tests, even when a branch is not executed.
foreach ($file in Get-ChildItem -LiteralPath $repoRoot -Recurse -File -Filter '*.ps1' | Where-Object {
    $_.FullName -notmatch '[\\/](?:\.git|\.test-tmp|dist|test-results)[\\/]'
}) {
    $tokens = $null
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) { throw ($parseErrors | Out-String) }
}

$configuration = [PesterConfiguration]::Default
$configuration.Run.Path = Join-Path $repoRoot 'tests'
$configuration.Run.PassThru = $true
$configuration.Output.Verbosity = 'Detailed'
$configuration.TestResult.Enabled = $true
$configuration.TestResult.OutputFormat = 'NUnitXml'
$configuration.TestResult.OutputPath = Join-Path $OutputDirectory 'tests.xml'
$configuration.CodeCoverage.Enabled = $Coverage.IsPresent
$configuration.CodeCoverage.Path = @((Join-Path $repoRoot 'psakefile.ps1'), (Join-Path $repoRoot 'init.ps1'))
$configuration.CodeCoverage.OutputFormat = 'JaCoCo'
$configuration.CodeCoverage.OutputPath = Join-Path $OutputDirectory 'coverage.xml'
$configuration.CodeCoverage.CoveragePercentTarget = $MinimumCoverage

function Assert-TestRunSucceeded {
    param([Parameter(Mandatory)]$Result)

    if ($Result.Result -ne 'Passed' -or $Result.FailedCount -gt 0 -or
        $Result.TotalCount -eq 0 -or $Result.PassedCount -eq 0 -or
        $Result.NotRunCount -gt 0 -or $Result.SkippedCount -gt 0) {
        throw "Tests failed or did not run: $($Result.Result); $($Result.FailedCount) failed; $($Result.NotRunCount) not run; $($Result.SkippedCount) skipped; $($Result.TotalCount) total."
    }
}

$result = Invoke-Pester -Configuration $configuration
$summary = [ordered]@{
    result = [string]$result.Result
    passed = $result.PassedCount
    failed = $result.FailedCount
    skipped = $result.SkippedCount
    notRun = $result.NotRunCount
    total = $result.TotalCount
    durationSeconds = $result.Duration.TotalSeconds
}
if ($Coverage) {
    $summary.coveragePercent = $result.CodeCoverage.CoveragePercent
    $summary.minimumCoverage = $MinimumCoverage
    $summary.commandsAnalyzed = $result.CodeCoverage.CommandsAnalyzedCount
    $summary.commandsExecuted = $result.CodeCoverage.CommandsExecutedCount
    $result.CodeCoverage.CommandsMissed |
        Select-Object File, Function, Line, Command |
        ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $OutputDirectory 'coverage-missed.json') -Encoding utf8
}
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Encoding utf8

Assert-TestRunSucceeded -Result $result
if ($Coverage -and ($result.CodeCoverage.CommandsAnalyzedCount -eq 0 -or $result.CodeCoverage.CoveragePercent -lt $MinimumCoverage)) {
    throw "Coverage $($result.CodeCoverage.CoveragePercent.ToString('F2'))% is below $MinimumCoverage%."
}
