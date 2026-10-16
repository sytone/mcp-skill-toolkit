Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Describe 'Test runner failure gate' {
    BeforeAll {
        $repoRoot = Split-Path -Parent $PSScriptRoot
        $tokens = $null
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $repoRoot 'scripts/Invoke-Tests.ps1'), [ref]$tokens, [ref]$parseErrors
        )
        if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
        $definition = @($ast.EndBlock.Statements | Where-Object {
            $_ -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -eq 'Assert-TestRunSucceeded'
        })
        if ($definition.Count -ne 1) { throw 'Test runner gate missing.' }
        Set-Item -Path 'Function:Assert-TestRunSucceeded' -Value $definition[0].Body.GetScriptBlock()
    }

    It 'rejects <Case> even when the failed-test count is zero' -ForEach @(
        @{ Case = 'a failing teardown block'; Result = 'Failed'; Passed = 1; Total = 1; NotRun = 0; Skipped = 0 }
        @{ Case = 'a failing container'; Result = 'Failed'; Passed = 1; Total = 1; NotRun = 0; Skipped = 0 }
        @{ Case = 'an empty run'; Result = 'Passed'; Passed = 0; Total = 0; NotRun = 0; Skipped = 0 }
        @{ Case = 'a filtered test'; Result = 'Passed'; Passed = 1; Total = 2; NotRun = 1; Skipped = 0 }
        @{ Case = 'a skipped test'; Result = 'Passed'; Passed = 1; Total = 2; NotRun = 0; Skipped = 1 }
    ) {
        $run = [pscustomobject]@{ Result = $Result; FailedCount = 0; PassedCount = $Passed; TotalCount = $Total; NotRunCount = $NotRun; SkippedCount = $Skipped }
        { Assert-TestRunSucceeded -Result $run } | Should -Throw '*Tests failed or did not run*'
    }

    It 'accepts a complete passing run' {
        $run = [pscustomobject]@{ Result = 'Passed'; FailedCount = 0; PassedCount = 2; TotalCount = 2; NotRunCount = 0; SkippedCount = 0 }
        { Assert-TestRunSucceeded -Result $run } | Should -Not -Throw
    }
}
