# Creating a skill from an MCP server

## 1. Define the server locally

Copy `servers.example.json` to `servers.json`. The latter is machine-local and
must stay out of Git.

Each entry needs a name, command array, description, transport, and enabled flag:

```json
{
  "mcp_cli_backend": "dotnet",
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

The default `mcp_cli_backend` is `dotnet`, which builds and runs the local C#
CLI under `src/`. Set it to `bun` to use the legacy Bun-based CLI. The optional
`mcp_cli_path` points to an executable for the selected backend. These fields
configure the toolkit and are not passed to MCP servers.

For stdio servers, the command and arguments are emitted into the MCP server
configuration. Live discovery starts that command through the selected CLI.
The toolkit does not install each server or supply its credentials.

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

### Elicitation and approval defaults (.NET backend)

The optional per-server `elicitation` object enables MCP `elicitation/create`
form requests. It is not enabled globally or inferred from tool descriptions.
For servers such as Computer Use, prefer this default:

```json
"elicitation": { "action": "ask" }
```

In `ask` mode, the script returns exit code **4** and a JSON object such as:

```json
{
  "status": "approval_required",
  "approvalId": "<single-use-id>",
  "server": "my-tools",
  "tool": "my-tool",
  "request": {
    "message": "Approve this operation?",
    "requestedSchema": {
      "type": "object",
      "properties": { "approved": { "type": "boolean" } },
      "required": ["approved"]
    }
  }
}
```

The agent must present the request to the user, obtain their answer, and resume
the **same pending call** through the same script:

```powershell
pwsh -NoProfile -File './dist/skills/my-tools/scripts/my-tool.ps1' `
    -ApprovalId '<returned-id>' -ApprovalAction accept `
    -ApprovalJson '{"approved":true}'
# Or refuse/cancel without form content:
pwsh -NoProfile -File './dist/skills/my-tools/scripts/my-tool.ps1' `
    -ApprovalId '<returned-id>' -ApprovalAction decline
```

These are illustrative form fields, not a claim about a particular server's
approval schema. Supply the actual answers required by `request.requestedSchema`;
do not invent an `approved` property or infer permission from a tool result.
The generic runner accepts the same approval parameters plus `-Tool`.
`-ApprovalAction` defaults to `accept` when an ID is supplied; `-ApprovalJson`
defaults to `{}`. Invalid/mismatched content sends no approval and leaves the ID
available for a corrected response until expiry.

The cached worker retains the MCP connection and original tool execution;
approving does **not** call the tool again. Each approval ID is valid once, for
that server configuration, tool, CLI installation, and working directory.
Further requests can return new IDs. `MCP_TIMEOUT` bounds the original retained
call, including time spent waiting for the user (default 1800 seconds). Pending
calls prevent idle shutdown but not that deadline. Expired, lost, or already-used
IDs fail explicitly and never start a replacement worker or replay a call.
Only one tool call may be active on an `ask` server at a time; a new call is
rejected while the existing call runs or awaits approval. Tool retries are
disabled in this mode. `MCP_NO_DAEMON=1` is incompatible with `ask`.

For explicitly authorized unattended use, a server can instead set a default
response:

```json
"elicitation": {
  "action": "accept",
  "content": { "approved": true }
}
```

**This opts that server into automatically answering every compatible form
request, including approval requests.** Use it only for a trusted server and a
deliberately authorized scope. `content` is a flat object of strings, numbers,
booleans, or string arrays. Accepted answers must satisfy the server's schema;
unknown answer fields are rejected, and required fields are never fabricated or
filled from defaults. An omitted `content` means `{}`, not blanket agreement
with every possible form. `decline` and `cancel` defaults are also supported,
without `content`. `ask` cannot have default content.

With no `elicitation` field, the client retains its old behavior: no elicitation
capability and method-not-found responses. Enabled policies advertise form
support only; URL-mode requests are rejected and no browser is opened.
The legacy Bun backend does not implement these policies, so the generator
rejects enabled-server elicitation configuration when Bun is selected.
Custom CLI overrides must point to a compatible build.

Approval needs are normally known only at runtime. MCP tool discovery lists
tool schemas and annotations, not whether a particular call will elicit
consent. Discovery never probes tools to trigger approvals. This supports the
standard protocol path but is not a guarantee of live Computer Use compatibility
or a replacement for its native permissions, interruption, or action policies.
Regenerate previously generated scripts before using approval parameters.

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

The offline build still builds/resolves the selected CLI so generated runners
can contain its executable path. With the default backend this requires the
.NET 10 SDK but does not start MCP servers or contact MCP services. The build
creates a generic runner but no per-tool scripts because there is no captured
tool inventory.

## 3. Discover the tools

Initialize the selected CLI backend and install the selected server's own
dependencies. The .NET backend is the default and requires the .NET 10 SDK;
select `"mcp_cli_backend": "bun"` if you need the legacy Bun CLI. Authenticate
using the server's supported process, then repeat the build without
`SkipDiscovery`:

```powershell
Invoke-psake -TaskList Build -Properties @{
    McpConfigPath = './dist/mcp_servers.json'
    SelectedServerNames = 'my-tools'
}
if (-not $psake.build_success) { throw 'Build failed.' }
```

Existing configs with a `mcp_cli_path` that points to Bun can continue to use
that executable. To make the backend choice explicit, set
`mcp_cli_backend` to `bun`; to migrate to the local .NET CLI, select `dotnet`
and run `init.ps1`, which builds the local executable and records its path.
An invocation-level `McpCliBackend` psake property takes precedence over the
config selector, and `McpCliPath` can provide an executable-path override.

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

### Servers that require tools/list before calls

Generated stdio runners automatically use `scripts/Invoke-McpStdioBridge.ps1`.
It completes discovery on the actual server connection before forwarding tool
calls; agents should keep using their normal per-tool or generic wrapper.
Do not initialize a session by running a separate `mcp-cli info` command, and do
not invoke the bridge directly.

Discovery follows pagination and fails closed on errors, invalid responses,
repeated cursors, or timeout. Set `MCP_TIMEOUT` in seconds to bound discovery
(default 1800). The bridge does not replay tool calls. HTTP entries keep their
existing MCP CLI behavior and are not covered by the stdio workaround.

The runtime runner creates and then removes a temporary MCP CLI configuration,
leaving the original configuration and unselected server entries unchanged.
This copy may contain the same sensitive configuration as the original; it is
local runtime data, not a distributable skill artifact.

To update previously generated skills, regenerate them with the updated toolkit,
inspect the output, and stage deployment using an explicit `TargetSkillsDir`.
Source changes alone do not update installed skills. Offline checks establish
protocol behavior, not live server authentication or compatibility.

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
