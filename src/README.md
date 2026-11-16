# Local C# port of MCP CLI

This directory ports [philschmid/mcp-cli](https://github.com/philschmid/mcp-cli)
v0.3.0, pinned to commit
[`d77672a1ce800ec3fec1f43829909badb2ffbd32`](https://github.com/philschmid/mcp-cli/commit/d77672a1ce800ec3fec1f43829909badb2ffbd32).
The pinned CLI uses TypeScript MCP SDK 1.25.2.

**This is the port phase only.** The toolkit's initializer, generated wrappers,
discovery, and deployment have not been switched over. They still use their
existing MCP CLI/Bun setup. Source packaging includes this directory, but does
not build or install it.

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

### Deliberate platform and reliability differences

- C# workers use current-user-only .NET named pipes, not Bun's Unix socket/PID
  files. The two implementations do not share worker processes or IPC.
- Endpoint names hash the server name, complete nested configuration, user,
  working directory, and application directory. Arbitrary server names never
  become paths. Changed configurations get a new worker; the old worker idles
  out. No PID-file-based termination of unrelated processes is necessary.
- IPC uses complete newline-delimited frames, including large responses.
  Five seconds bounds startup, but tool IPC uses `MCP_TIMEOUT`, not upstream's
  hard-coded five-second daemon request timeout. Active calls do not count as
  idle. Windows workers do not inherit the foreground CLI's output handles.
- Time budgets cancel outstanding work, failed initialization closes its
  process/transport, and stderr is forwarded once rather than duplicated.
- Invalid config field types fail with explicit diagnostics instead of
  failing later in a JavaScript SDK. Recovery hints refer to supported flags.
- Structured-output checks implement the upstream default draft-07 vocabulary,
  local references/IDs, previously registered schema IDs, and formats without
  fetching schemas. The reviewed nullable, local-reference, format and
  floating-point cases match AJV 8.17.1 with ajv-formats 3.0.1, including AJV's
  exact floating-point `multipleOf` behavior (for example, `0.3 / 0.1`).
  Schema keywords are checked during discovery, not delayed until a call.
  Schema regex character atoms operate on Unicode code points, including
  general-category property escapes such as `\p{Letter}`. This remains a local
  implementation, not a complete replacement for JavaScript RegExp/AJV:
  Unicode data versions, properties outside general categories, other regex
  extensions, and diagnostics are not guaranteed identical. Unregistered
  external schema references fail instead of accessing the network.
- Windows batch argument escaping follows the upstream SDK's cross-spawn
  convention. Multiline process arguments must use a native executable.
  Multiline **tool JSON** uses stdin and has no such restriction.
- OS/.NET exception details and help examples are native to this port; those
  strings are not promised byte-for-byte matches with Bun/V8.

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

### Retained parity regressions

The `Parity*` tests retain the concrete failures found by differential execution
against the pinned upstream source and SDK. They run with MSTest and local C#
fixtures; Bun and the original JavaScript packages are not test dependencies.

| Regression area | Permanent tests |
| --- | --- |
| Non-transient error text must not replay calls; actual socket errors remain retryable | `RetryTests`, `TransportTests` |
| Timeout cancellation observed by stdio, HTTP, and cached servers | `TransportTests` |
| Relative working directories and malformed-line recovery | `TransportTests` |
| Configured HTTP headers, compressed responses, same/cross-origin redirects, redirect limits | `TransportTests` |
| Invalid tool metadata/content and preservation/removal of appropriate fields | `TransportTests` |
| Deep configuration, arguments, IPC, and structured result output | `TransportTests` |
| Optional capability enforcement and parameter-type formatting | `TransportTests` |
| Unicode patterns, nullable unions, formats, `multipleOf`, references, and invalid schema compilation | `OutputSchemaTests` |
| Successful discovery followed by structured calls on cached connections | `OutputSchemaTests` |

These regressions establish the reviewed cases, not certification of every
possible JSON Schema or live MCP server. The deliberate worker/platform
differences above remain unchanged.

To run these same tests against a published executable:

```powershell
$env:MCP_CLI_TEST_EXECUTABLE = (Resolve-Path .\dist\mcp-cli-csharp\mcp-cli.exe).Path
try { dotnet test .\src\McpCli.slnx --no-restore }
finally { Remove-Item Env:MCP_CLI_TEST_EXECUTABLE }
```

Pester integration and the existing CI workflow are intentionally not replaced.
See [Agents.md](Agents.md) for the code map and contribution instructions,
and [third-party notices](THIRD-PARTY-NOTICES.txt) for licensing.
