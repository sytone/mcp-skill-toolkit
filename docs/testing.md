# Testing

## Run the checks

Use a fresh PowerShell 7 process on Windows:

```powershell
pwsh -NoProfile -File ./init.ps1 -SkipMcpCli
pwsh -NoProfile -File ./scripts/Invoke-Tests.ps1 -Coverage
```

The initializer installs missing psake 5.0.4 and Pester 5.7.1 modules for the
current user. It does not install or check external CLIs with `-SkipMcpCli`.
The test runner itself never installs dependencies. Use a fresh process because
different Pester versions can load conflicting assemblies.

The same command runs in [GitHub Actions](../.github/workflows/tests.yml) for
pushes, pull requests, and manual runs. CI uses a hosted Windows runner,
read-only repository permissions, pinned action revisions, and no service
secrets. It does not start live MCP or Agency servers or deploy installed skills.

## What the suite checks

- Source and test PowerShell syntax.
- Server selection, transport conversion, tool policy, quoting, and safe names.
- Agency inventory parsing and local configuration updates using fixtures.
- Tool/parameter extraction and generated skill contents.
- Generated wrapper execution against a local fake CLI, including JSON, UTF-8,
  input files/stdin, invalid input, named parameters, and failure propagation.
- The generated wrapper tool-call log contract: records, opt-out, overrides,
  absence of payload values, and resilience to unwritable log locations.
- Offline builds, isolated deployment and custom-instruction preservation.
- Source-only packaging, including planted fake credentials that must stay out.

All generated test files stay in `.test-tmp/` and tests clean their own folders.
`servers.json` is not needed. Mocks stand in for executable lookup, job APIs, and
module installation; no credential or network access is needed for the suite.

## Coverage and limits

Coverage is Pester command coverage across the complete `init.ps1` and
`psakefile.ps1`, not a hand-picked list of easy functions. The test runner fails
below **75%**, on failing tests or setup/teardown, on an empty test run, or on
skipped/filtered tests.
Do not lower the CI threshold instead of fixing missing coverage.

Function tests load original AST scriptblocks so measurements retain the source
file and line. Child-process integration tests and generated wrapper execution
are separate behavioral evidence: their commands are not counted as source hits
in the parent Pester process. Installer entry-point and external executable
integration paths are intentionally not exercised against real services.
Passing offline tests does not prove authentication, upstream CLI formatting,
or compatibility with every MCP server. Those need authorized manual checks.

## Reports

`test-results/` contains `tests.xml` (NUnit), `coverage.xml` (JaCoCo),
`coverage-missed.json`, and `summary.json`. CI uploads this folder even when tests
fail. The folder is ignored by Git and excluded from source packages.

Use `-OutputDirectory` to choose another report location. Without `-Coverage`,
the runner executes all tests but does not measure or enforce coverage.
