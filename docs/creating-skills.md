# Creating a skill from an MCP server

## 1. Define the server locally

Copy `servers.example.json` to `servers.json`. The latter is machine-local and
must stay out of Git.

Each entry needs a name, command array, description, transport, and enabled flag:

```json
{
  "mcp_cli_path": "mcp-cli",
  "servers": {
    "my-tools": {
      "command": ["node", "C:/tools/my-server/server.js"],
      "description": "Project lookup tools. Use when finding projects or reading their status.",
      "transport": "stdio",
      "enabled": true
    }
  }
}
```

Use a stable, lowercase, hyphenated server name. It becomes the skill directory
and the skill's frontmatter `name`. A useful description says what the tools do
and when an agent should use them.

For stdio servers, the command and arguments are emitted into the MCP CLI
configuration. Live discovery starts that command through MCP CLI. The toolkit
does not install each server or supply its credentials.

For non-stdio entries, `default_port` and `endpoint` become a loopback URL such
as `http://127.0.0.1:3500/mcp`. The toolkit does not start the HTTP server. An
arbitrary remote URL, headers, environment variables, and working directory
are not part of this generator's current server-entry format.

Do not put credentials in a command array. Keep authentication in the server's
supported local credential mechanism. Use an absolute path when a server
script needs a predictable location.

The optional `tool_policy` object accepts `allow` and `deny` arrays, which map
to MCP CLI's `allowedTools` and `disabledTools` configuration. Tool filtering
is not a replacement for service-side permissions.

## 2. Generate offline first

```powershell
Import-Module psake -RequiredVersion 5.0.4
Invoke-psake -TaskList Build -Properties @{
    SourceConfig = './servers.json'
    OutputDir = './dist'
    McpConfigPath = './dist/mcp_servers.json'
    SelectedServerNames = 'my-tools'
    SkipDiscovery = $true
}
if (-not $psake.build_success) { throw 'Build failed.' }
```

Inspect `dist/mcp_servers.json` and `dist/skills/my-tools/SKILL.md`.
Only enabled servers are included. `SelectedServerNames` accepts a
comma-separated list and limits generated skills; it does not narrow the
shared MCP CLI configuration, which includes all enabled entries.

The offline build creates a generic runner but no per-tool scripts because
there is no captured tool inventory.

## 3. Discover the tools

Install MCP CLI and the selected server's dependencies, authenticate using that
server's supported process, then repeat the build without `SkipDiscovery`:

```powershell
Invoke-psake -TaskList Build -Properties @{
    McpConfigPath = './dist/mcp_servers.json'
    SelectedServerNames = 'my-tools'
}
if (-not $psake.build_success) { throw 'Build failed.' }
```

Read the result in three places:

- `dist/build-summary.json`: `discoveryExitCode` and `toolCount` for each server.
- `dist/discovery/my-tools.info.txt`: captured discovery output.
- `dist/skills/my-tools/references/tool-info.md`: the same information for the agent.

The parser reads MCP CLI's human-readable `info` format. A CLI formatting
change can affect tool or parameter extraction. Review the inventory rather
than assuming every available tool was captured.

## 4. Add domain guidance

Create `dist/skills/my-tools/references/custom-instructions.md` with the parts
that discovery cannot infer:

- Which user requests should trigger this skill.
- Which tools are read-only and which require approval.
- Identity lookup and permission requirements.
- Required filters, output limits, and validation checks.
- How to report partial results and failures.

Generated guidance asks the agent to read this file first. It can refine the
skill's instructions but cannot override the agent's higher-priority safety
rules or the user's authorization boundaries.

This file survives deployment when it already exists in the target skill.
Keep a separate maintained copy or backup. The deployment process does not
preserve other hand-edited files in matching skill folders.

## 5. Validate and stage deployment

Run the test suite and inspect the generated scripts. Test a read-only tool
only after confirming its schema, scope, and authorization.

```powershell
Invoke-psake -TaskList Test
if (-not $psake.build_success) { throw 'Tests failed.' }

Invoke-psake -TaskList Deploy -Properties @{
    TargetSkillsDir = './dist/deployed-skills'
}
if (-not $psake.build_success) { throw 'Staging failed.' }
```

Review the staged skill, then deploy to the intended runtime with an explicit
`TargetSkillsDir`. Without that property, deployment targets
`$HOME/.botnexus/skills`.

Use the generated `scripts/<tool>.ps1` wrappers for calls. Their JSON-file input
is useful for payloads with quotes, newlines, or large values. The generic
`scripts/Invoke-Tool.ps1` is the fallback for a known tool without a dedicated
wrapper. Do not invent tool names or parameter names.

## Optional Agency integration

The existing build also supports Agency-backed server commands. Agency is a
separate dependency and is not required for generic stdio or HTTP servers.
`SyncAgencyServers` reads Agency help to add missing entries to local
configuration. New entries are disabled by default, and duplicate ports are
reassigned. Review every addition before enabling it.

Local `agency mcp <server>` entries run over stdio. The generator removes
`--port` and `--transport` launch options but keeps server-specific arguments,
such as an organization filter. `agency mcp remote` arguments are kept unchanged.

The repository does not include an Agency installation package, enterprise
configuration, internal server inventory, or credentials.

## Before publishing a skill

Review both the source and generated output:

1. Remove secrets, personal paths, tenant identifiers, and internal endpoints.
2. Check captured discovery text for private schemas or service instructions.
3. Check generated descriptions and trigger wording.
4. Verify all documented script names exist and their parameters match.
5. Confirm what the agent runtime will load and what the tools can modify.

`Package` shares the toolkit source and example config. It is not an export of
your configured skills or local server configuration.

## References

- [MCP CLI source and usage](https://github.com/philschmid/mcp-cli)
- [Everything example server](https://github.com/modelcontextprotocol/servers/tree/main/src/everything)
- [Agent Skills format](https://agentskills.io/specification)
