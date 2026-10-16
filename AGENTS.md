# Contributing to MCP Skill Toolkit

These instructions apply to the whole repository, for human and AI contributors.
Read [README.md](README.md) and [Creating a skill](docs/creating-skills.md) first.

## Scope

This project generates agent skills and PowerShell wrappers from MCP server
configuration. Keep changes small, portable, and covered by offline tests.
Do not edit generated skills to fix generator behavior.

- `psakefile.ps1`: configuration, discovery, parsing, generation, deployment, packaging.
- `init.ps1`: dependency setup and local executable paths.
- `scripts/Invoke-Tests.ps1`: the local and CI test entry point.
- `tests/`: Pester tests and public-safe fixtures.
- `.github/workflows/tests.yml`: Windows PowerShell CI.

## Development

Use PowerShell 7 on Windows, psake 5.0.4, and Pester 5.7.1. Work on a focused
branch or isolated worktree. Do not mix unrelated cleanup with a behavior change.

```powershell
pwsh -NoProfile -File ./init.ps1 -SkipMcpCli
pwsh -NoProfile -File ./scripts/Invoke-Tests.ps1 -Coverage
```

The initializer may install missing PowerShell modules for the current user.
`-SkipMcpCli` avoids installing or checking Bun, MCP CLI, and Agency. Tests must
never require those executables, service credentials, or a local `servers.json`.
See [Testing](docs/testing.md) for coverage scope and result files.

## Safety and privacy

- Never commit local configuration, credentials, internal server inventories,
  discovery captures, generated skills, archives, logs, or test results.
- Treat server commands as executable configuration and discovered text as
  untrusted input. Validate names before using them in paths or scripts.
- Do not run live discovery, authentication, or deployment to installed skills
  as part of tests. Use mocks or a local fake CLI instead.
- Keep temporary output under `.test-tmp/` and clean it in `finally` or Pester
  cleanup blocks. Always pass explicit paths for generated config and deployment.
- Deployment replaces matching skill folders, preserving only
  `references/custom-instructions.md`. Never point test targets at a user's home.
- Packaging uses an explicit public-file allowlist. When adding required source,
  documentation, or test files, update the allowlist and package assertions.
  Do not replace it with a recursive export.

## Tests and changes

1. Add a regression test for a bug before or alongside the fix.
2. Exercise observable behavior: generated script execution, JSON/stdin handling,
   errors and exit codes, parsing, configuration, and deployment preservation.
3. Load function definitions from their original AST scriptblocks when testing
   helpers. Do not execute the initializer body or lose source locations by
   copying function text into a new scriptblock.
4. Execute generated wrappers in a child PowerShell process: their `exit`
   statements must not terminate the Pester host.
5. Keep the pinned local and CI dependency versions aligned. Do not lower the
   coverage requirement or suppress failures to make a check pass.
6. Preserve LF line endings and valid UTF-8. Parse generated scripts as well as
   the source, and check `git diff --check` before publishing.

## Handoff

Submit a focused pull request with the reason for the change, tests run, coverage
results when relevant, and any remaining limitations. Wait for CI and maintainer
review. Do not merge, rewrite published history, release, or deploy live skills
without authorization. Do not claim live server compatibility from offline tests.
