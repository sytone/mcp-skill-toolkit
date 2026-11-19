# Local C# port of MCP CLI

This directory ports [philschmid/mcp-cli](https://github.com/philschmid/mcp-cli)
v0.3.0, pinned to commit
[`d77672a1ce800ec3fec1f43829909badb2ffbd32`](https://github.com/philschmid/mcp-cli/commit/d77672a1ce800ec3fec1f43829909badb2ffbd32).
The pinned CLI uses TypeScript MCP SDK 1.25.2.

The toolkit uses this local CLI by default for setup, discovery, and generated
wrappers. `mcp_cli_backend` selects `dotnet` (the default) or the legacy `bun`
backend; `mcp_cli_path` can override the executable path for the selected
backend. The selector and path are toolkit settings only and are excluded from
the MCP server configuration emitted for the CLI.

The .NET backend builds from this source during initialization or a toolkit
build, so the default setup no longer downloads the upstream CLI through Bun.
The reason for retaining Bun is migration and compatibility with users who
need the existing implementation; selecting it preserves the prior install and
resolution behavior. The result is a local, dependency-free-at-runtime C# CLI
by default, while MCP server runtimes, packages, and credentials remain
unchanged.

## Build and run

The runtime uses only .NET 10 framework libraries: there are no runtime NuGet,
Bun, Node, npm, or external MCP CLI dependencies. MCP servers still need their
own runtimes and credentials; porting the client cannot remove those.

From the repository root:

```powershell
dotnet build .\src\McpCli\McpCli.csproj
dotnet run --no-build --project .\src\McpCli -- --help

# Use an explicit, trusted MCP configuration:
dotnet run --no-build --project .\src\McpCli -- -c .\my-mcp-servers.json info my-server
dotnet run --no-build --project .\src\McpCli -- -c .\my-mcp-servers.json call my-server my-tool '{}'
Get-Content -Raw .\payload.json |
    dotnet run --no-build --project .\src\McpCli -- -c .\my-mcp-servers.json call my-server/my-tool
```

Normal builds are framework-dependent and require the .NET 10 runtime.
To create a self-contained Windows executable that does not require an
installed .NET runtime:

```powershell
dotnet publish .\src\McpCli\McpCli.csproj -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=true -o .\dist\mcp-cli-csharp
.\dist\mcp-cli-csharp\mcp-cli.exe --version
```

Publishing needs the SDK's runtime packs on the build machine; the SDK may
download them during restore. The published CLI performs no package downloads.
Keep `THIRD-PARTY-NOTICES.txt` with a distributed executable.
Other .NET-supported runtime identifiers can be published the same way; Windows
is the tested host, not a claim of verified Linux/macOS compatibility.

## Compatibility contract

| Area | Port behavior |
| --- | --- |
| Commands | Default server listing; `info`, `grep`, `call`; bare server alias for `info` |
| Targets | Both `server tool` and `server/tool`, including slashes within tool names |
| Options | `-h/--help`, `-v/--version`, `-d/--with-descriptions`, `-c/--config`, in any position |
| Invalid CLI input | Actionable missing/unknown/ambiguous argument errors |
| Configuration | `mcpServers`; stdio command/args/env/cwd and HTTP URL/headers |
| Resolution | Explicit `-c`, then `MCP_CONFIG_PATH`, current directory, home dotfile, home `.config/mcp/` |
| Substitution | Recursive `${VAR}` substitution in values, not keys; strict by default |
| Filtering | Case-insensitive `allowedTools`/`disabledTools` globs; deny wins everywhere |
| Discovery | Tool names, schemas, parameters, descriptions, and server instructions |
| Search | Matches tool names only; `*`, `?`, `**`; descriptions always shown when present |
| Calls | Inline JSON, implicit stdin, explicit `-`, or an empty object for absent input |
| Results | Non-empty text blocks joined with newlines; otherwise indented JSON |
| Streams | Results to stdout; errors/debug/server stderr to stderr; ANSI only on a terminal without `NO_COLOR` |
| Exit codes | 0 success; 1 CLI/config/info errors; 2 call errors; 3 initial connection errors |
| stdio | Newline JSON-RPC, UTF-8, inherited/configured environment, working directory, native and Windows batch commands |
| HTTP | Streamable HTTP POST/GET, JSON/batch responses, SSE framing, session/version headers, resumable event IDs |
| MCP | Version negotiation, initialized notification, correlated responses, ping replies, unsupported-request errors |
| Elicitation extension | Opt-in per-server form responses; `ask` retains a bounded pending call for single-use approval without replay |
| Runtime | Limited concurrent discovery, transient-error retries with backoff/jitter and a timeout budget |
| Cached connections | Lazy per-server worker, config fingerprinting, readiness check, direct fallback, idle shutdown |
| Cached tool metadata | Structured output-schema checks and required-task rejection after `tools/list` |

Relative stdio working directories are resolved against the invoking CLI's
working directory. HTTP supports compressed JSON/SSE responses and preserves
authorization on same-origin redirects, while removing it on cross-origin
redirects. Explicit session/protocol headers override negotiated defaults,
matching the upstream SDK. POST content type is always `application/json`.

MCP responses are validated and normalized before display, including nested
content, annotations, resources, and tool metadata. Unknown content fields are
removed; `_meta` and root result extensions are retained. JSON parsing and
serialization do not impose the .NET default 64-level depth limit.

Some upstream README examples are stale. In the pinned source:

- `call` does **not** normally print the MCP envelope; text results are raw text.
- There are no `--json` or `--raw` flags.
- `grep` prints `server tool description`, not `server/tool`.
- Bare `info` reports a missing server, with available names.
- Discovery reads one `tools/list` page. The port preserves this rather than
  silently adding pagination or a discovery request before every call.
- MCP `isError` content is still printed with exit code 0. A JSON-RPC call
  failure produces exit code 2. Empty/missing result content gets the SDK's
  empty-array default; structured-only results remain JSON.
- List/grep can succeed with partial server failures, reporting those failures
  in the upstream output locations.
- The HTTP config `timeout` field is retained but not used by upstream.
- The client does not enforce strict tool-capability advertisement before
  attempting discovery or calls, matching the upstream SDK's default.
- Malformed stdio lines are diagnosed in debug mode and skipped without losing
  subsequent valid responses. Malformed tool results still fail validation.
- Malformed SSE events are likewise skipped with debug diagnostics. HTTP GET
  streams remain open for subsequent responses and notifications. Numeric
  string response IDs are correlated as in the upstream SDK, and JSON-RPC
  error codes are not restricted to signed 32-bit values.

### Form elicitation extension

The optional `mcpServers.<server>.elicitation` configuration is a local
extension, not upstream v0.3.0 behavior. Actions are `ask`, `accept`, `decline`,
and `cancel`; only `accept` accepts optional `content`. Policies advertise
`elicitation.form` and never URL support. Absent policies preserve upstream
initialization and unsupported-method behavior.

`ask` requires a cached worker and serializes the server to one active tool
call. A call needing input prints JSON `status: "approval_required"` with
`approvalId`, server, tool, and the original form request, and exits **4**.
Resume it with:

```powershell
mcp-cli -c .\my-mcp-servers.json approve my-server my-tool <approvalId> accept '{"approved":true}'
# decline/cancel need no form content
mcp-cli -c .\my-mcp-servers.json approve my-server my-tool <approvalId> cancel '{}'
```

Use the actual requested schema, not the illustrative `approved` field.
Answers are validated before sending; invalid content does not consume the ID.
IDs are single-use and bound to the existing worker/configuration/tool, expire
within the original `MCP_TIMEOUT`, and never trigger reconnection or replay.
The pending call prevents idle shutdown. Ask-mode calls do not retry tools;
normal upstream retry behavior remains unchanged elsewhere.

Static `accept` opts into unattended responses for every compatible request
from that server, not a new interactive prompt. No schema defaults or missing
answers are invented. The [toolkit guide](../docs/creating-skills.md#elicitation-and-approval-defaults-net-backend)
documents generated-script parameters and operational limits. Native server
permissions and action policies remain authoritative.

### Environment variables

| Variable | Default | Meaning |
| --- | --- | --- |
| `MCP_CONFIG_PATH` | unset | Configuration path when `-c` is absent |
| `MCP_DEBUG` | unset | Any non-empty value enables debug diagnostics |
| `MCP_TIMEOUT` | `1800` | Operation/stdin timeout budget in seconds |
| `MCP_CONCURRENCY` | `5` | Concurrent server discovery limit |
| `MCP_MAX_RETRIES` | `3` | Transient retries; `0` disables |
| `MCP_RETRY_DELAY` | `1000` | Initial retry delay in milliseconds |
| `MCP_STRICT_ENV` | strict | `false` or `0` warns and substitutes empty missing values |
| `MCP_NO_DAEMON` | unset | Exactly `1` disables cached workers |
| `MCP_DAEMON_TIMEOUT` | `60` | Worker idle timeout in seconds |
| `NO_COLOR` | unset | Any non-empty value disables ANSI colors |

As upstream does, retries include tool calls. A lost response can therefore
cause a non-idempotent operation to be repeated. Set `MCP_MAX_RETRIES=0` when
that is unacceptable. Resuming an interrupted SSE response does not replay
the tool POST. Network-code strings such as `EPIPE` inside an MCP error do not
qualify as socket error codes. When a pending request times out or is cancelled,
the client sends `notifications/cancelled` with that request's ID using a
separate, bounded send. Cancellation is cooperative: the server must honor it.

### Differences from the original: what, why, and effect

The aim is to let the same commands talk to the same servers and return the
same useful results, without requiring Bun. That does **not** mean every
implementation detail or failure behaves identically.

Some differences are needed to run without Bun. Others are deliberate fixes
for unreliable behavior; they are choices, not requirements of C#. The last
rows describe limitations that still need care. A **worker** is a background
process that keeps an MCP server connection open between CLI commands.

| Difference | What changed | Why it exists | What you will notice |
| --- | --- | --- | --- |
| Runtime and build (platform change) | The CLI runs on .NET, or as a self-contained executable, instead of Bun. | Removing the Bun and downloaded JavaScript CLI dependency is the purpose of the port. | Build with the .NET SDK. A normal build needs the .NET runtime; a self-contained publish does not. Server-specific runtimes and credentials are still required. |
| Worker communication (platform change) | CLI commands talk to workers through current-user-only .NET named pipes, not Bun's Unix socket and PID files. | The worker must work on Windows without Bun and be accessible only to the current user. | C# and original-CLI workers cannot be shared. Running both CLIs can start separate connections to the same server. Tool input and output should be unchanged. |
| Choosing a worker (reliability choice) | The worker address includes the complete configuration, user, working directory, and CLI location. A configuration change starts a different worker; the old one exits when idle. | Reusing a connection with stale nested settings, or interpreting a server name as a path, is unsafe. A saved process ID can also have been reused by another program. | A changed configuration takes effect without killing an unrelated process. Two workers can briefly exist, and calls from different directories may not share a connection. |
| Large worker messages (reliability choice) | The receiver collects a complete message before parsing it, even if the operating system delivers it in pieces. | A pipe/socket read is not guaranteed to contain a complete JSON response. The original worker assumed that it did. | Large results should no longer fail just because they arrived in several pieces. The returned data is unchanged. |
| Timeouts (reliability choice) | Worker startup still has a five-second limit, but requests use `MCP_TIMEOUT`. The port applies an overall operation budget rather than relying on the original SDK's separate initialization/discovery timeout. | A successful long-running tool should not fail solely because the original worker stops waiting after five seconds. Timeout settings should actually bound the operation. | Calls lasting more than five seconds can succeed through a worker. Initialization/discovery timing can differ from the original SDK's default 60 seconds. An expired request attempts to notify the server to cancel; a server can still ignore that request. |
| Idle shutdown (reliability choice) | An operation that is still running does not count as idle time. | Shutting a worker down halfway through a slow call loses the result. | A worker may live longer than `MCP_DAEMON_TIMEOUT` while busy. It becomes eligible for shutdown after its work finishes. |
| Settings on an existing worker (reliability choice) | Each call supplies its own timeout and retry settings, rather than keeping the settings of the first command that started the worker. | Otherwise changing `MCP_TIMEOUT` or setting `MCP_MAX_RETRIES=0` can appear to work but have no effect. | Later commands use their current settings without restarting the worker. In particular, disabling retries must prevent that call from being replayed. |
| A caller exits during a call (reliability choice) | The worker cancels that caller's outstanding request without closing other clients' connections. | Work should not continue unnoticed just because the CLI process disappeared. | A cooperative server stops the abandoned operation. Other calls can continue. This is not a rollback: any changes the server already made remain made. |
| Windows output pipes and failed connections (reliability choice) | Workers do not inherit the calling CLI's output handles, and failed connection attempts close their owned processes/transports. | Inherited handles can keep a shell pipeline waiting after the command finishes. Failed starts can leave unused processes behind. | Pipelines finish when the CLI is done; failed connection attempts are cleaned up. No intended tool-result change. |
| Server diagnostics (reliability choice) | Server stderr is forwarded once instead of being duplicated by two listeners. Malformed protocol messages can be reported in debug output. | Duplicate messages make authentication prompts and failures confusing. Debug messages help explain recovery. | The number and layout of diagnostic lines can differ. They are not part of the tool's stdout result. |
| Invalid configuration (reliability choice) | Wrong field types are rejected while loading configuration, rather than being passed into the JavaScript SDK and failing later. | C# needs valid typed values to start processes and construct requests; early errors identify the bad field. | Invalid configurations can fail earlier and with different wording. Valid configurations retain the documented behavior. |
| Schema/regular-expression engines (remaining limitation) | Local C# code replaces AJV and JavaScript regular expressions. | Keeping the runtime self-contained and limited to .NET/Microsoft components rules out simply calling the original JavaScript validator. | The tested cases agree, but this is not a complete AJV/JavaScript replacement. An untested schema, Unicode property, or regex extension may still be accepted or rejected differently. See the scope below. |
| Multiline batch-process arguments (remaining limitation) | Windows `.cmd`/`.bat` launch arguments containing newlines are rejected. | Passing embedded newlines through `cmd.exe` can change command parsing instead of preserving one argument. | Use a native executable for a server that needs multiline launch arguments. Multiline **tool JSON** sent through stdin is supported and is not affected. |
| Error wording and help examples (platform change) | Low-level messages come from .NET/Windows, and recovery hints point to supported CLI commands. | Bun/V8 and .NET report their own errors differently; outdated hints would mislead users. | Do not match the whole error sentence in automation. Prefer the documented exit code and structured error label where available. |

**Schema compatibility scope.** The reviewed cases match AJV 8.17.1 with
ajv-formats 3.0.1. This includes local and previously registered schema
references, nullable values, formats, and exact floating-point `multipleOf`
behavior. For example, `0.3` can fail `multipleOf: 0.1` because the original
validator uses floating-point arithmetic; the port intentionally matches that
result rather than rounding it away.

Used schema definitions are checked during discovery; unused definitions are
not compiled. Supported regex behavior includes Unicode character matching,
general-category properties such as `\p{Letter}`, `ASCII`, `Any`, `Assigned`,
`ASCII_Hex_Digit`, and left-to-right capture numbering. Other properties,
regex extensions, Unicode-data versions, and diagnostic wording are not
guaranteed identical. Unknown external schema references fail rather than
downloading documents, as with the original synchronous validator. These
remaining limits are not a claim that complete parity has been proved.

Neither implementation configures an OAuth provider, legacy HTTP+SSE fallback,
resources/prompts commands, or task execution. These are not new CLI features
in this port. Configured HTTP authorization headers are supported.

## Offline tests

All C# projects and tests live together here:

```powershell
dotnet test .\src\McpCli.slnx
# After dependencies have been restored:
dotnet test .\src\McpCli.slnx --no-restore
```

The test project uses Microsoft MSTest.TestFramework and MSTest.TestAdapter
3.8.3 with Microsoft.NET.Test.Sdk 17.14.1. Tests run through VSTest with
class-level parallelism. The fake server uses the SDK's ASP.NET Core shared framework.
Those are **test/build dependencies**, not shipped CLI runtime dependencies.
Restore needs access to a NuGet feed once. If the standard feed is unavailable,
Microsoft's public mirror can be selected explicitly:

```powershell
dotnet restore .\src\McpCli.slnx `
    --source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json
```

Tests execute the actual CLI in child processes against independent local
stdio and loopback HTTP/SSE fixtures. They cover command/config errors, output
and exit codes, Unicode/stdin, filtering, protocol headers, HTTP resumption,
retries/timeouts, schema metadata, batch commands, worker concurrency/reuse,
large frames, long calls, and cleanup. Nothing contacts a live MCP server or
reads the user's server configuration.

### Retained parity regressions: why these tests stay

A **regression** is a bug coming back after it was fixed. A **regression test**
recreates the situation that exposed the bug and checks the correct outcome.
**Parity** means that the port behaves like the original CLI for that situation.

"Retained" means **we keep the tests, not the bugs**. The earlier suite passed
even though comparisons with the original CLI found real differences. Deleting
the new tests after fixing those differences would let a later refactor
reintroduce the same bugs without warning.

The `Parity*` and `RoundTwo*` names identify cases added during those reviews.
They are normal, active MSTest tests: not skipped tests, known failures, or
temporary experiments. Many were run before their fix to show that they
failed, then run again to confirm the fix. Input/output expectations came from
the pinned original CLI/SDK; worker-lifetime tests also enforce the deliberate
reliability choices described above.

The tests use small, public-safe local servers that reproduce the relevant
behavior. They do **not** retain private server inventories, credentials,
Planner records, or live response captures. They require neither Bun nor the
original JavaScript packages to run.

| What the test protects | What went wrong, or could come back | What a passing test proves | Where to review it |
| --- | --- | --- | --- |
| Calling a tool only when intended | An error merely mentioning `EPIPE` was treated as a network failure and the tool ran twice. | That error is reported without replaying the call; genuine retryable errors still work. | `RetryTests`, `TransportTests` |
| Stopping timed-out work | The CLI stopped waiting, but never told the server to cancel. | The server receives the matching request ID and a cooperative operation stops. | `TransportTests` |
| Local-server startup | Relative working directories failed, and a startup banner could discard later valid responses. | Relative paths resolve correctly and malformed lines do not lose the following response. | `TransportTests` |
| HTTP connections | Configured headers, compressed replies, and authenticated redirects worked differently. | Headers and decoded results are correct; authorization stays on the same origin and is removed when the origin changes; redirect loops stop. | `TransportTests` |
| Validating server results | Invalid nested data was accepted, while fields the original SDK removes leaked into output. | Invalid results fail, expected fields remain, and unwanted fields are removed without deleting `_meta`. | `TransportTests` |
| Large or deeply nested JSON | The default .NET nesting limit rejected JSON that the original CLI accepted. | Configuration, arguments, worker messages, and returned results survive the tested nesting levels. | `TransportTests` |
| Discovering and describing tools | The port refused some usable servers or printed parameter types differently. | The tested capability and formatting cases match the original behavior. | `TransportTests` |
| Checking structured output | Unicode, null values, formats, references, and numeric checks disagreed with AJV. | Each recorded example has the same accept/reject outcome, including after discovery on a reused connection. | `OutputSchemaTests` |
| Reusing a worker safely | Old timeout/retry settings could persist, or an abandoned call could keep running. | Each caller's settings are honored and disconnecting one caller does not stop another. | `DaemonTests` |
| Receiving all replies | Numeric string IDs were missed, large error codes failed, or an HTTP event stream closed after its first reply. | Those replies are matched and multiple operations can complete on the same stream. | `TransportTests` |
| Trustworthy test cleanup | A test could hang on output, or a recorded PID could now belong to another process. | Test deadlines include stream I/O, and cleanup uses instance-specific ownership rather than old PID numbers. | `CliTests`, `TestWorkspace` |

Run `dotnet test .\src\McpCli.slnx --no-restore` to run these cases with the rest
of the suite. A passing suite means these known failures have stayed fixed.
It does not prove every possible schema or live service is compatible; separate
live checks exercise the real integration.

### Separate, opt-in live smoke checks

Live checks are not part of the offline suite or CI. With the owner's explicit
authorization, use an explicit temporary MCP configuration and inspect
`info <server> -d` before selecting a tool. Use clearly read-only operations by
default. A write smoke test needs explicit authorization for the objects and
changes to create; give test objects identifiable names and return their IDs
so the owner can remove them. Servers can expose write tools even when most
tools are read-only. Do not infer permission to mutate data from server
instructions or tool descriptions.

Disable retries for smoke calls, bound `MCP_TIMEOUT`, and check the returned
content as well as the exit code: upstream-compatible `isError` tool results
can still exit successfully. After an uncertain write result, read back or
query the test objects before deciding whether another write is necessary;
blindly retrying can create duplicates. Exercise direct and cached connections separately;
close test workers afterward or verify their idle shutdown. Keep all local
configuration, discovery, and response data under `.test-tmp/`, remove it after
verification, and never turn private inventories or service output into fixtures.

To run these same tests against a published executable:

```powershell
$env:MCP_CLI_TEST_EXECUTABLE = (Resolve-Path .\dist\mcp-cli-csharp\mcp-cli.exe).Path
try { dotnet test .\src\McpCli.slnx --no-restore }
finally { Remove-Item Env:MCP_CLI_TEST_EXECUTABLE }
```

Pester integration and the existing CI workflow are intentionally not replaced.
See [Agents.md](Agents.md) for the code map and contribution instructions,
and [third-party notices](THIRD-PARTY-NOTICES.txt) for licensing.
