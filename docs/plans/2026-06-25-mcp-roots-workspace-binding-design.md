# MCP Roots Workspace Binding Design

> Historical/superseded for workspace target selection by the implemented
> [`2026-08-30-stateless-workspace-targeting-design.md`](2026-08-30-stateless-workspace-targeting-design.md).
> Retained to document the earlier Roots-based approach and its rationale.

## Problem

Miller's MCP server binds its primary workspace from `Environment.CurrentDirectory` at process
startup and runs a full julie bootstrap before accepting MCP tool calls. Cursor (and other VS
Code-fork clients) often spawn user-global or plugin MCP servers with cwd set to home, `/`, or a
plugin cache directory. Miller fails closed before `initialize` completes, or would index the wrong
tree if it did not refuse.

Goldfish and Julie solve this with **request-time** workspace binding: MCP `roots/list` from the
client, with deferred heavy work until the first tool call that needs a workspace.

## Resolution order

Primary workspace path is chosen in this order:

1. `MILLER_WORKSPACE_ROOT` environment variable (explicit override; unresolved `${...}` placeholders
   are ignored)
2. First valid `file://` URI from MCP `roots/list`
3. `Environment.CurrentDirectory` when not a sensitive root and not a known plugin/cache install root
4. Fail closed with actionable guidance

Sensitive-root refusal applies **only** when cwd is the last resort (source `cwd`). Env and roots
sources are trusted explicit client/operator intent.

Known plugin/cache install roots are not valid cwd fallbacks even though they are ordinary user directories:
`~/.claude/plugins`, `~/.codex/plugins`, `~/.cursor/plugins`, `~/.miller/plugin-cache`, and explicit
`*_PLUGIN_ROOT` environment paths. A Cursor plugin process may start there, but Miller must defer until MCP roots
or an explicit `MILLER_WORKSPACE_ROOT` supplies the project.

## Startup modes

### Eager bootstrap

When step 1 or a safe step 3 resolves at startup, run the existing bootstrap immediately in
`IndexBootstrapService.StartAsync` (Claude Code / correct cwd — no regression).

### Deferred bootstrap

When neither env nor safe cwd is available at startup:

- Skip sensitive-root throw in `Program.cs`
- Log to machine-global `~/.miller/logs/` until bound
- Start MCP transport without holder/workspace
- Complete bootstrap on the first `tools/call` after `roots/list`

## Request-time binding

A `WorkspaceBindingCallToolFilter` runs before tool handlers:

- Calls `IWorkspaceBindingService.EnsurePrimaryBoundAsync(McpServer, ct)`
- Caches roots per session; clears on `notifications/roots/list_changed`
- On root change after initial bind: re-bootstrap primary workspace (~400ms)

Background services (`FreshnessService`, `IndexerService`) wait on `IWorkspaceBindingService` before
reading bootstrap getters.

Current-workspace DI services (`WorkspaceContext`, `IndexHolder`, `SmartTargetResolver`, `TelemetryLedger`,
`WorkspaceIndexProvider`, `IndexFreshProbe`, and edit applier state) resolve from the latest bootstrap binding
rather than freezing the first root. The indexer exits its current reader/leader session when the binding generation
changes, releases old leadership state, then restarts against the new primary root.

## Semantics

- First valid MCP root is the **primary** workspace for default/`current` tool routing
- Existing cross-workspace `workspace_id` selectors unchanged
- No multi-root corpus merge

## Non-goals

- Replacing `workspace_id` cross-workspace reads
- Multi-root aggregation
- julie-extract ownership changes
- Codex Desktop env-per-project path (document separately; no MCP roots today)

## References

- Goldfish: `goldfish/src/server.ts`, `goldfish/src/workspace.ts`
- Julie: deferred auto-index + roots reconciliation
- [`docs/findings/2026-06-08-cursor-plugin-relative-launcher-root-cause.md`](../findings/2026-06-08-cursor-plugin-relative-launcher-root-cause.md)
- Supersedes interim project-local-only Cursor guidance once verified
