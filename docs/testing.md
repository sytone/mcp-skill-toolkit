# Testing

## Run the checks

Use a fresh PowerShell 7 process on Windows:

```powershell
pwsh -NoProfile -File ./init.ps1 -SkipMcpCli
pwsh -NoProfile -File ./scripts/Invoke-Tests.ps1 -Coverage
```

The initializer installs missing psake 5.0.4 and Pester 5.7.1 modules for the
current user. It does not build or check either MCP CLI backend or Agency with
`-SkipMcpCli`. The test runner runs both the Pester suite and the C# MSTest
solution; `dotnet test` restores the test-only NuGet dependencies as needed.
Use a fresh process because different Pester versions can load conflicting
assemblies.

The same command runs in [GitHub Actions](../.github/workflows/tests.yml) for
pushes, pull requests, and manual runs. CI uses a hosted Windows runner with
the .NET 10 SDK, read-only repository permissions, pinned action revisions, and
no service secrets. It does not start live MCP or Agency servers or deploy
installed skills.

## What the suite checks

- Source and test PowerShell syntax.
- Server selection, transport conversion, tool policy, quoting, and safe names.
- Agency inventory parsing and local configuration updates using fixtures.
- Tool/parameter extraction and generated skill contents.
- Generated wrapper execution against a local fake CLI, including JSON, UTF-8,
  input files/stdin, invalid input, named parameters, and failure propagation.
- The generated wrapper tool-call log contract: records, opt-out, overrides,
  absence of payload values, and resilience to unwritable log locations.
- Same-connection stdio discovery using a local JSON-RPC server and CLI double:
  a negative control without the bridge, discovery ordering and pagination,
  persistent/concurrent calls, server callbacks, inventory-change notifications,
  script/Windows command-shim entry points, failure/timeout blocking, no tool
  replay, child-process cleanup, and temporary-config cleanup.
- Offline builds, isolated deployment and custom-instruction preservation.
- Source-only packaging, including planted fake credentials that must stay out.
- The local C# CLI's MSTest coverage, including child-process execution and
  local stdio/loopback HTTP protocol fixtures.

Pester and protocol-fixture temporaries stay in `.test-tmp/`; .NET build output
is kept under ignored `src/**/bin` and `src/**/obj` directories and excluded
from source packages. Tests clean their own fixture folders. `servers.json` is
not needed. Mocks stand in for executable lookup, job APIs, and module
installation. NuGet may restore the pinned test-only dependencies, but no MCP
server, credential, or live-service access is required.

## Coverage and limits

Coverage is Pester command coverage across the complete `init.ps1` and
`psakefile.ps1`, not a hand-picked list of easy functions. The C# MSTest suite
runs in the same entry point but does not contribute to the PowerShell coverage
percentage. The test runner fails
below **75%**, on failing tests or setup/teardown, on an empty test run, or on
skipped/filtered tests.
Do not lower the CI threshold instead of fixing missing coverage.

Function tests load original AST scriptblocks so measurements retain the source
file and line. Child-process integration tests and generated wrapper execution
are separate behavioral evidence: their commands are not counted as source hits
in the parent Pester process, including the generated stdio bridge.
Installer entry-point and external executable
integration paths are intentionally not exercised against real services.
Passing offline tests does not prove authentication, upstream CLI formatting,
or compatibility with every MCP server. Those need authorized manual checks.

## Reports

`test-results/` contains `tests.xml` (NUnit), `coverage.xml` (JaCoCo),
`coverage-missed.json`, and `summary.json`. CI uploads this folder even when tests
fail. The folder is ignored by Git and excluded from source packages.

Use `-OutputDirectory` to choose another report location. Without `-Coverage`,
the runner executes all tests but does not measure or enforce coverage.


## Retained daemon endpoint collision regression (Windows)

`DaemonTests.HeldUnresponsiveEndpointFallbackDoesNotReportFatalDaemonFailure`
is an active regression for successful, quiet direct fallback. It starts the
real worktree CLI against the local stdio fixture, with debug off
and retries disabled. Before launch, the test holds a current-user-only,
asynchronous named pipe with `FirstPipeInstance` at the exact endpoint derived
from the server config, workspace working directory, and CLI application
directory. The holder never accepts or answers requests. There is no sleep or
random scheduling used to trigger the collision.

The initial readiness ping cannot succeed. The worker's first-instance pipe
creation fails, and direct fallback still completes the tool call. The test
first checks exit code 0, valid state JSON with `calls = 1`, the fixture working
directory and PID, exactly one `tools/call` in the shared request log, and only
one fixture start. It then requires no fatal `Daemon failed:` diagnostic and
clean stderr. Before the runtime fix, this final check failed with:

```text
Daemon failed: Access to the path is denied.
```

The child worker now reports its top-level failure only with `MCP_DEBUG`.
The parent owns the terminal error if direct fallback also fails. This does
not buffer or filter server stderr: authentication prompts and server errors
still stream through the existing diagnostic path. The whole operating system
error sentence is not asserted because its wording can vary. Held-endpoint
tests use MSTest's Windows OS condition and run normally on Windows.

Controls in the same file check that `MCP_NO_DAEMON=1` succeeds with clean
stderr while the same endpoint is held, and that a healthy debug-off daemon
serves `info` then a separate `state` CLI invocation on the same connection
with clean stderr. Further cases retain the child diagnosis with debug enabled,
check actionable connection failure (exit 3) and tool failure (exit 2) after
fallback, and verify no tool replay. Server stderr is forwarded exactly once
both during healthy worker startup and direct fallback, even when the server
message contains `Daemon failed:`. Healthy concurrent startup must also have
clean stderr and share one connection. All these CLI calls use
`TestWorkspace.RunAsync`, whose
30-second deadline bounds stdin, process exit, stdout, and stderr. Held pipe
handles close before workspace cleanup, even when an assertion fails. Worker
configurations are tracked for endpoint-based cleanup; no process-name kills
are used.

Run from the repository root in PowerShell 7 with the .NET 10 SDK. Leave
`MCP_CLI_TEST_EXECUTABLE` unset to test only the worktree build:

```powershell
dotnet build ./src/McpCli.slnx --configuration Release
$filter = 'FullyQualifiedName~HeldUnresponsiveEndpoint|FullyQualifiedName~HealthyDaemonWithDebugDisabled|FullyQualifiedName~ServerStderrIsForwardedOnce|FullyQualifiedName~ConcurrentStartupAndRequestsUseOneDaemon'
# Three independent test-host runs, including controls and diagnostic cases:
1..3 | ForEach-Object {
    dotnet test ./src/McpCli.slnx --configuration Release --no-build --no-restore --filter $filter --logger "trx;LogFileName=focused-$_.trx" --results-directory ./test-results/daemon-fallback-fix *> "./test-results/daemon-fallback-fix/focused-$_.log"
    Write-Host "Run $_ exit code: $LASTEXITCODE"
}
# Run the complete C# suite and the complete repository coverage entry point:
dotnet test ./src/McpCli.slnx --configuration Release --no-build --no-restore --logger 'trx;LogFileName=full-dotnet.trx' --results-directory ./test-results/daemon-fallback-fix *> ./test-results/daemon-fallback-fix/full-dotnet.log
pwsh -NoProfile -File ./scripts/Invoke-Tests.ps1 -Coverage -OutputDirectory ./test-results/daemon-fallback-fix *> ./test-results/daemon-fallback-fix/repository-coverage.log
```

Create `test-results/daemon-fallback-fix` before redirecting output if it
does not exist. Each focused run should report 9 passed, 0 failed, and 0 skipped
on Windows. Do not disable tests or lower the coverage threshold. Raw logs,
TRX files, and Pester coverage reports stay under ignored `test-results/`.

**Limits:** this is a deliberately held, unresponsive endpoint. It does not
model two healthy workers racing to start. That remains covered separately by
`ConcurrentStartupAndRequestsUseOneDaemon`. This reproduction shows one way to
produce the diagnostic; it does not prove the cause of any Teams incident or
establish live-service compatibility.
