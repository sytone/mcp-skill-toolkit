# Working on the C# MCP CLI

Read the repository's [contributor instructions](../AGENTS.md), then
[the port guide](README.md). This directory contains the CLI, its C# tests,
and its local protocol test server. Do not edit generated skills.

## Scope and compatibility

- The baseline is philschmid/mcp-cli v0.3.0 at
  `d77672a1ce800ec3fec1f43829909badb2ffbd32`.
- Preserve observable command parsing, config resolution, filtering, output,
  diagnostics, exit codes, retries, and cached/direct connections.
- Upstream source takes precedence over its outdated README examples:
  `call` extracts text content; `grep` always includes available descriptions;
  `-c` takes precedence over `MCP_CONFIG_PATH`; bare `info` is an error.
- Keep the runtime dependency-free: no Bun, Node, downloaded CLI, or runtime
  NuGet packages. The test projects may use pinned test-only dependencies.
- Use MSTest for all C# tests, with Microsoft/.NET test frameworks and tooling.
  Keep the MSTest framework and adapter versions aligned.
- Toolkit initialization, discovery, wrapper generation, and deployment still
  use the existing CLI. Changing that integration is a separate phase.
- Preserve [third-party notices](THIRD-PARTY-NOTICES.txt), including when publishing.

## Code map

| File/project | Responsibility |
| --- | --- |
| `McpCli/Program.cs` | Entry point, signals, worker entry, Windows handle ownership |
| `McpCli/Cli.cs`, `CliArguments.cs`, `CliError.cs` | Commands and user-facing contracts |
| `McpCli/Configuration.cs` | Config validation/substitution, runtime settings, globs, fingerprints |
| `McpCli/Output.cs` | Text, schema, ANSI, and result formatting |
| `McpCli/McpConnection.cs`, `Retry.cs` | Filtered connections, metadata, retry budgets |
| `McpCli/RpcConnection.cs` | MCP initialization, JSON-RPC correlation, server requests |
| `McpCli/StdioTransport.cs`, `HttpTransport.cs` | Child processes and Streamable HTTP/SSE |
| `McpCli/Daemon.cs` | Per-user connection caching over framed named pipes |
| `McpCli/OutputSchema.cs` | Cached structured-output validation |
| `McpCli.Tests/` | Unit and child-process compatibility tests |
| `McpCli.TestServer/` | Independent offline stdio/loopback HTTP fixture |

## Build and test

From the repository root, with the .NET 10 SDK:

```powershell
dotnet build .\src\McpCli.slnx
dotnet test .\src\McpCli.slnx --no-restore
```

Use a focused `dotnet test --filter` while developing, then run the whole C#
suite. Warnings are errors. Keep source UTF-8 with LF endings.

Test the actual executable, not just its parser. Assertions should cover
stdout, stderr, exit codes, transmitted JSON, and child-process cleanup.
Use `TestWorkspace` for explicit temporary config, process ownership, and
cleanup under the repository's `.test-tmp/`. Never use a user's configuration,
installed skills, credentials, or a live MCP endpoint. HTTP fixtures bind only
to loopback on an OS-assigned port.

Daemon regressions must test separate CLI processes, output pipe closure,
large messages, concurrent startup, config changes, long calls, and idle
shutdown. Track every worker configuration so cleanup can close its endpoint.
Never terminate processes by executable name.

Transport changes must preserve request IDs, notifications, UTF-8 framing,
HTTP session/protocol headers, and cancellation/timeout ownership. Do not
replay a POST to resume an SSE response. Tool retries are a separate upstream
behavior and can be disabled with `MCP_MAX_RETRIES=0`.

When adding source or tests, extend the explicit allowlists in
`../psakefile.ps1` and `../tests/Build.Tests.ps1`. Never package `bin/`, `obj/`,
test output, configuration, secrets, or recursive directory exports.
Run the relevant Pester packaging tests and `git diff --check` as well.
