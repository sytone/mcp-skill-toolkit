# MCP Skill Toolkit

Generate agent skills and PowerShell tool wrappers from MCP servers using
[philschmid/mcp-cli](https://github.com/philschmid/mcp-cli).

A local `servers.json` defines the servers. The build creates the MCP CLI
configuration, optionally discovers tools, and writes a skill package for each
enabled server:

```text
servers.json -> config -> tool discovery -> SKILL.md + scripts + references

agent -> generated PowerShell wrapper -> mcp-cli -> MCP server
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
Import-Module psake -RequiredVersion 5.0.4
Import-Module Pester -RequiredVersion 5.7.1
Invoke-psake -TaskList Test
if (-not $psake.build_success) { throw 'Tests failed.' }
```

The suite uses fixtures and task-local directories. It does not require live
MCP servers, Agency access, or your local `servers.json`. Initialization and the
`Test` task use Pester 5.7.1; Pester 6 is not part of the supported test setup.

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
`McpCliPath`, `SelectedServerNames`, `DiscoveryTimeoutSeconds`, and
`SkipDiscovery`. Deployment accepts `SourceSkillsDir` and `TargetSkillsDir`.

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
tests/                     Offline Pester tests and fixtures
```
