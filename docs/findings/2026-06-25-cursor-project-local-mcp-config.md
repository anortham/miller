# Cursor Project-Local MCP Config (interim — superseded)

## Status

**Superseded** by MCP roots workspace binding
([`docs/plans/2026-06-25-mcp-roots-workspace-binding-design.md`](../plans/2026-06-25-mcp-roots-workspace-binding-design.md)).
Miller now binds its primary workspace from MCP `roots/list` on the first tool call, so user-global Cursor MCP and
the plugin launcher work per editor window without `${workspaceFolder}` or per-repo `.cursor/mcp.json` hacks.

## Historical summary (2026-06-25 interim)

Before roots binding shipped, Miller bound workspace from process cwd at startup. Cursor user-global MCP often
started with an unresolved `${workspaceFolder}` or plugin-cache cwd, so the interim fix was a **project-local**
`.cursor/mcp.json` with a direct `miller serve` path (Cursor sets project MCP cwd correctly).

That interim guidance is retired. Prefer:

- Cursor plugin marketplace install, or
- User-global `~/.cursor/mcp.json` with `"command": "/absolute/path/to/miller", "args": ["serve"]`

Optional `MILLER_WORKSPACE_ROOT` in the MCP env block remains for clients without MCP roots (Codex per-project).

## Evidence (still valid context)

- User-global `~/.cursor/mcp.json` with `MILLER_WORKSPACE_ROOT="${workspaceFolder}"` failed on macOS Cursor 2026-06-25:
  unresolved placeholders and plugin-cache cwd.
- Project `.cursor/mcp.json` with direct `miller serve` worked because Cursor set cwd to the open repo.
- Roots binding removes the need for that workaround.
