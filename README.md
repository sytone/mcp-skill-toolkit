# MCP Skill Toolkit

This toolkit is the recommended way to work with MCP tools in
[BotNexus](https://github.com/sytone/botnexus). Generate skills and PowerShell
wrappers, then have agents call those wrappers through MCP CLI.

See the [BotNexus skills guide](https://sytone.dev/botnexus/skills.html) and
[Creating a skill](docs/creating-skills.md) for setup and usage.

Generate agent skills and PowerShell tool wrappers from MCP servers using
[philschmid/mcp-cli](https://github.com/philschmid/mcp-cli).

A local `servers.json` defines the servers. The build creates the MCP CLI
configuration, optionally discovers tools, and writes a skill package for each
enabled server:

```text
servers.json -> config -> tool discovery -> SKILL.md + scripts + references

agent -> generated PowerShell wrapper -> mcp-cli -> stdio bridge -> MCP server
```

The toolkit generates the tool interface. Add task-specific guidance separately
in each skill's `references/custom-instructions.md`.

## Requirements

- PowerShell 7. Windows is the tested host.
- psake 5.0.4 and Pester 5.7.1 for builds and tests.
- MCP CLI for live discovery and tool calls. The initializer can install it
  using Bun.
- The runtime and credentials required by the MCP servers you choose.
- Agency only if your enabled server configuration uses it.

The example uses the public
[Everything test server](https://github.com/modelcontextprotocol/servers/tree/main/src/everything),
not a production service. Its Windows command requires Node.js and `npx`.
No server runtime is required for an offline build.

## Quick start

Clone the repository and create your machine-local configuration:

```powershell
git clone https://github.com/sytone/mcp-skill-toolkit.git
Set-Location mcp-skill-toolkit
Copy-Item ./servers.example.json ./servers.json
```

Review `servers.json` before running anything. Server commands are executable
configuration. An `npx -y` entry downloads and runs a package during live
discovery; use only packages and servers you trust.

Initialize the build dependencies without installing or checking external CLIs:

```powershell
pwsh -NoProfile -File ./init.ps1 -SkipMcpCli
Import-Module psake -RequiredVersion 5.0.4
```

Build offline, keeping generated configuration inside `dist/`:

```powershell
Invoke-psake -TaskList Build -Properties @{
    SkipDiscovery = $true
    McpConfigPath = './dist/mcp_servers.json'
}
if (-not $psake.build_success) { throw 'Build failed.' }
```

This creates `SKILL.md`, tool references, and a generic runner. Offline builds
do not discover tool names and therefore do not create per-tool wrappers.

For live discovery, install or verify the external CLIs, then build:

```powershell
pwsh -NoProfile -File ./init.ps1
Invoke-psake -TaskList Build
if (-not $psake.build_success) { throw 'Build failed.' }
```

The default build writes MCP CLI configuration to
`$HOME/.config/mcp/mcp_servers.json`. It can replace an existing file there.
Use `McpConfigPath` to choose a separate file if you already have a configuration.

## Generated files

```text
dist/
  build-summary.json
  discovery/<server>.info.txt       # live discovery only
  skills/<server>/
    SKILL.md
    scripts/Invoke-Tool.ps1
    scripts/Invoke-McpStdioBridge.ps1 # internal stdio transport helper
    scripts/<tool>.ps1              # discovered tools only
    references/tool-info.md
```

Read `build-summary.json` after a live build. Discovery failures are recorded
in `discoveryExitCode`; the build can still emit a generic skill when a server
could not be reached. Generation success alone does not prove tool discovery
or authentication succeeded.

See [Creating a skill](docs/creating-skills.md) for the workflow, configuration
format, and customization guidance.

## Call generated tools

Inspect the captured tool information first. Use the generated wrapper for the
actual tool name and pass JSON as one value or a file:

```powershell
# Replace <server> and <tool> with discovered names.
pwsh -NoProfile -File './dist/skills/<server>/scripts/<tool>.ps1' -Json '{}'
pwsh -NoProfile -File './dist/skills/<server>/scripts/<tool>.ps1' -JsonPath './payload.json'

# Generic runner when a per-tool wrapper is unavailable:
pwsh -NoProfile -File './dist/skills/<server>/scripts/Invoke-Tool.ps1' -Tool '<tool>' -Json '{}'
```

Discovered parameters can also be supplied by name. The wrappers handle UTF-8,
JSON validation, and stdin payload delivery. They are not an authorization or
sandbox boundary. Review the server's permissions and any returned content.

Do not edit generated scripts. Change `psakefile.ps1` and regenerate them.
The current runner combines CLI output streams; callers should not assume its
stdout is always a single JSON document, especially on errors.

### Session-local discovery

For stdio servers, the runner automatically inserts a generated bridge between
MCP CLI and the server. Before forwarding the first tool call on each connection,
the bridge completes `tools/list`, including pagination, on that same connection.
This supports proxies that reject calls until their session's tools are loaded.
It also refreshes discovery after a `notifications/tools/list_changed` message.

No MCP CLI changes or separate `info` command are needed. An earlier build-time
discovery or a separate `info` process cannot guarantee runtime session state.
The bridge does not retry tool calls. Failed, malformed, or timed-out discovery
blocks queued calls instead of attempting them without an inventory.
`MCP_TIMEOUT` bounds discovery in seconds (default 1800, matching MCP CLI).

The runner reads the configured MCP CLI file without modifying it. For stdio
calls it creates a temporary copy that wraps only the selected server, retaining
its arguments, environment, working directory, and tool policy. It deletes that
copy when the CLI returns, including failure paths. The bridge is resolved from
the skill's current directory, so staged or deployed skills do not depend on a
build-machine bridge path. PowerShell 7, native executables, and Windows
`.cmd`/`.bat` server entry points are supported.

HTTP entries are passed to MCP CLI unchanged; this workaround does not add
session-local discovery to HTTP transports. Do not call the internal bridge
directly or edit generated files. Regenerate affected skills and stage deployment
to pick up generator changes; existing installed skills are not updated by
changing the source alone.

## Tool call logs

Generated wrappers record every call, so you can see which tools agents ran,
when, and with what outcome. `Invoke-Tool.ps1` appends one JSON object per line
to `tool-calls-<yyyy-MM-dd>.jsonl` (UTC date) in the log directory. Per-tool
wrappers delegate to it, so both entry points log the same way.

| Field | Meaning |
|-------|---------|
| `schemaVersion` | Log contract version, currently `1` |
| `timestamp` | UTC start time, ISO 8601 |
| `callId` | Unique identifier for the call |
| `server`, `tool` | MCP server and tool requested |
| `agent` | Value of `MCP_SKILL_TOOLKIT_AGENT`, or `null` |
| `processId` | Wrapper process ID |
| `inputSource` | `json`, `jsonPath`, `stdin`, or `none` |
| `argumentNames` | Top-level JSON object property names |
| `payloadBytes` | UTF-8 size of the validated payload, or `null` |
| `status` | `succeeded`, `failed` (CLI ran and failed), or `rejected` (input refused) |
| `exitCode` | MCP CLI exit code, or `null` if it did not run |
| `error` | `null`, `conflicting-input`, `unreadable-input`, `invalid-json`, `invalid-config`, `cli-error`, or `nonzero-exit` |
| `durationMs` | Elapsed wrapper time in milliseconds |

Argument values, payloads, and tool output are never logged, but tool and
argument names are. Logging failures are ignored and never change a tool's
output or exit code. The log is local observability, not an audit boundary.

The build property `ToolCallLogDirectory` sets the default directory baked into
the wrappers (`~/.mcp-skill-toolkit/logs`; an empty value disables it). At run
time, `MCP_SKILL_TOOLKIT_LOG_DIR` overrides the directory and
`MCP_SKILL_TOOLKIT_LOG=off` (or `0`, `false`, `no`, `disabled`) turns logging off.
Have the agent host set `MCP_SKILL_TOOLKIT_AGENT` to identify the caller.
Logs are not rotated or deleted automatically.

## Deploy to an agent

Stage deployment in a separate directory first:

```powershell
Invoke-psake -TaskList Deploy -Properties @{
    TargetSkillsDir = './dist/deployed-skills'
}
```

The default `Deploy` target is `$HOME/.botnexus/skills`. It replaces the contents
of matching skill folders, preserves existing
`references/custom-instructions.md`, and leaves unrelated skills alone.
Back up any other hand-edited files before deploying. Set `TargetSkillsDir`
explicitly for another agent runtime's skills directory.

## Tests

```powershell
pwsh -NoProfile -File ./init.ps1 -SkipMcpCli
pwsh -NoProfile -File ./scripts/Invoke-Tests.ps1 -Coverage
```

The suite uses fixtures, a local fake CLI, and task-local directories. It does
not require live MCP servers, Agency access, or your local `servers.json`.
Pester 5.7.1 is pinned; run the command in a fresh process to avoid conflicts
with other Pester versions. `Invoke-psake -TaskList Test` remains available.

[GitHub Actions](https://github.com/sytone/mcp-skill-toolkit/actions/workflows/tests.yml)
runs the offline suite and enforces at least 75% command coverage on every push
and pull request. Test and coverage reports are uploaded as workflow artifacts.
See [Testing](docs/testing.md) for scope, limitations, and report locations.

## Contributing

Read [AGENTS.md](AGENTS.md) for contributor and agent guidance. Keep changes
focused, add regression tests, and submit a pull request with passing checks.
Never include local configuration, internal inventories, credentials, or
generated skills in a contribution.

## Tasks and properties

| Task | Purpose |
|------|---------|
| `Config` | Convert enabled server entries to MCP CLI configuration |
| `Discover` | Capture live tool information, unless discovery is skipped |
| `GenerateSkills` | Write skill packages and tool wrappers |
| `Build` | Run configuration, discovery, and generation |
| `Test` | Run the Pester suite |
| `Deploy` | Copy generated skills to the selected target |
| `SyncAgencyServers` | Optional Agency integration for server configuration |
| `Package` | Create a source-only setup ZIP, excluding local configuration |

Use `Invoke-psake -Docs` to list task descriptions.

Build properties include `SourceConfig`, `OutputDir`, `McpConfigPath`,
`McpCliPath`, `SelectedServerNames`, `DiscoveryTimeoutSeconds`,
`SkipDiscovery`, and `ToolCallLogDirectory`. Deployment accepts `SourceSkillsDir` and `TargetSkillsDir`.

For example, build one enabled server:

```powershell
Invoke-psake -TaskList Build -Properties @{
    SelectedServerNames = 'everything'
    SkipDiscovery = $true
    McpConfigPath = './dist/mcp_servers.json'
}
```

## Sharing

Push source files and example configuration, not your local setup:

- `servers.json`, generated files, logs, and ZIPs are ignored.
- `Package` includes source scripts, documentation, tests, and
  `servers.example.json`, not the local `servers.json`.
- Recipients copy `servers.example.json` to `servers.json` and configure their
  own paths, servers, and credentials.
- Review discovery output and generated skills before sharing them. Server
  descriptions, local paths, and internal tool schemas can contain private data.

## Source layout

```text
init.ps1                   Dependency initialization and local CLI paths
psakefile.ps1              Build, discovery, generation, deployment, packaging
servers.example.json       Portable sample configuration
docs/creating-skills.md    Skill creation and customization workflow
docs/testing.md            Test commands, coverage scope, and limitations
AGENTS.md                  Contributor and agent instructions
scripts/Invoke-Tests.ps1    Shared local/CI test runner
scripts/Invoke-McpStdioBridge.ps1  Source for the generated stdio transport helper
.github/workflows/         GitHub Actions workflow
tests/                     Offline Pester tests and fixtures
```
