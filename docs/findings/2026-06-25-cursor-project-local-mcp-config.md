# Cursor Project-Local MCP Config (historical — superseded)

## Status

**Historical/superseded (2026-08-30)** by the implemented stateless workspace-targeting design
([`docs/plans/2026-08-30-stateless-workspace-targeting-design.md`](../plans/2026-08-30-stateless-workspace-targeting-design.md)).
The current user-level GUI flow is `workspace operation=list`, then `workspace operation=open
path=/absolute/project` when absent, followed by the returned `workspace_id` on every workspace-bound call.

## Historical summary (2026-06-25 interim)

Before roots binding shipped, Miller bound workspace from process cwd at startup. Cursor user-global MCP often
started with an unresolved `${workspaceFolder}` or plugin-cache cwd, so the interim fix was a **project-local**
`.cursor/mcp.json` with a direct `miller serve` path (Cursor sets project MCP cwd correctly).

That interim guidance is retired. The user-global `~/.cursor/mcp.json` registration remains valid with
`"command": "/absolute/path/to/miller", "args": ["serve"]`; target selection belongs in each tool call.
Do not rely on launch cwd, `MILLER_WORKSPACE_ROOT`, `GOLDFISH_WORKSPACE`, MCP Roots, `current`, `primary`,
or session binding.

## Evidence (still valid context)

- User-global `~/.cursor/mcp.json` with `MILLER_WORKSPACE_ROOT="${workspaceFolder}"` failed on macOS Cursor 2026-06-25:
  unresolved placeholders and plugin-cache cwd.
- Project `.cursor/mcp.json` with direct `miller serve` worked because Cursor set cwd to the open repo.
- Stateless targeting removes the need for that workaround.
