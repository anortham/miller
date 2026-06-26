# Cursor Plugin Launcher Root Cause

## Summary

Miller's v0.3.2/v0.3.3 Cursor manifest assumed Cursor would run MCP commands from the plugin root. Local Cursor
logs showed two separate issues:

- Cursor resolves relative MCP args against the open workspace, not the plugin root.
- Cursor rejects `~/.cursor/plugins/local` symlinks whose targets are outside that directory, then falls back to
  Claude-imported plugin copies when those are installed.

## Evidence

- In an empty Cursor window, Node tried to load `/bin/miller-plugin-launcher.cjs`.
- In `/Users/murphy/source/julie`, Node tried to load
  `/Users/murphy/source/julie/bin/miller-plugin-launcher.cjs`.
- In `/Users/murphy/source/miller`, the same relative path happened to work because the Miller checkout contains
  `bin/miller-plugin-launcher.cjs`.
- Cursor structured logs contained:
  `loadUserLocalPlugin miller rejected: symlink target /Users/murphy/source/miller is outside /Users/murphy/.cursor/plugins/local`.
- After rejecting the symlink, Cursor loaded `miller@miller` from Claude plugin metadata. The installed Claude copy
  was still v0.3.2 and still used the relative launcher.
- After copying the patched manifest and launcher into the Claude-imported plugin cache and reloading Cursor,
  Cursor logs showed `plugin-miller-miller` transition from `initializing` to `connected`.
- Cursor's agent then invoked Miller successfully from `/Users/murphy/source/razorback`; the `workspace` tool
  returned normally and the agent reported Miller tools available.
- A second reload after copying the updated plugin metadata launched the cached Miller package from
  `~/.miller/plugin-cache/0.3.3/.../miller serve`, confirming the Claude-imported Cursor path also follows the
  patched package version.

## Decision

The Cursor manifest now targets the plugin install/cache root through Cursor's plugin-root placeholder:

```json
"args": [
  "${CURSOR_PLUGIN_ROOT}/bin/miller-plugin-launcher.cjs"
]
```

Cursor's packaged `cursor-agent-exec` code expands `${CURSOR_PLUGIN_ROOT}` and `${CLAUDE_PLUGIN_ROOT}` to the
actual plugin install path before creating MCP stdio servers. This works for both real local Cursor plugin
directories and Claude-imported plugin cache directories.

The manifest intentionally does not set `MILLER_WORKSPACE_ROOT="${workspaceFolder}"`. Cursor's empty/global
Settings window tries to resolve manifest env vars before launching the process; with no folder open, that fails
with `Variable workspaceFolder can not be resolved`. The launcher instead uses Cursor's cwd when it is a normal
project path and falls back to the plugin root when Cursor starts from `/`, the home directory, or another
sensitive root.

## Remaining Limitation

Cursor may still show a stale imported Miller MCP entry as errored if it is launching an old Claude-plugin, an old
relative-path config, or an old manifest with `${workspaceFolder}` in `env`. Users should update/reinstall the
Miller plugin, then reload Cursor. For local Cursor plugin testing, `~/.cursor/plugins/local/miller` must be a real
directory copy, not a symlink to a checkout outside `~/.cursor/plugins/local`.

Cursor also shows two Miller rows when both a real Cursor-local copy and a Claude-imported Miller plugin are
present. Cursor logs show both `loadUserLocalPlugin miller` and `loadClaudePlugin miller@miller`; it does not dedupe
those plugin sources in Settings. In the Home tab those duplicate rows can remain at `Loading tools`, while the
workspace tab shows both rows with `8 tools enabled`. The backend still uses the same MCP server identifier,
`plugin-miller-miller`, and skips duplicate client creation after the first connected server.

## 2026-06-10 Follow-up

Cursor can start the shared MCP process from an empty/global window even while another Cursor workspace tab is open.
In that state the launcher receives no usable workspace environment (`WORKSPACE_FOLDER_PATHS` can be blank, `PWD=/`,
and `VSCODE_CWD=/`). Falling back to the plugin root then lets Miller index its own plugin/cache directory, so a
later agent in `/Users/murphy/source/julie-extractors` sees `workspace: miller...` or plugin-cache paths instead of
the editor workspace.

The launcher now treats the whole plugin install trees (`~/.claude/plugins`, `~/.codex/plugins`,
`~/.cursor/plugins`, and `~/.miller/plugin-cache`) as unsafe fallback cwd values — whole trees rather than only the
`cache`/`local` subdirectories, because marketplace clones such as `~/.claude/plugins/marketplaces/miller` are full
repo checkouts that Cursor's Claude-plugin import can launch from. If the client provides a real workspace root,
Miller still uses it. If no workspace root is available, Miller fails with guidance instead of silently indexing the
plugin directory.

## 2026-06-12 Follow-up (superseded by MCP roots binding, 2026-06-25)

Historical config churn (`${workspaceFolder}` in user-global MCP, interim project-local `.cursor/mcp.json`) is
retired. Miller now binds workspace from MCP `roots/list` on the first tool call. See
[`2026-06-25-mcp-roots-workspace-binding-design.md`](../plans/2026-06-25-mcp-roots-workspace-binding-design.md)
and the superseded interim note in
[`2026-06-25-cursor-project-local-mcp-config.md`](2026-06-25-cursor-project-local-mcp-config.md).

Historical notes from the superseded global-config attempt:

- Cursor's MCP docs list `env` as an interpolated field and support `${workspaceFolder}` for the active project root.
  Miller must still run through the launcher rather than directly through `miller.exe`, because `miller.exe serve`
  derives its workspace from `cwd`; the launcher reads `MILLER_WORKSPACE_ROOT` and starts Miller with the resolved
  workspace as `cwd`.
- A global `user-miller` entry that launched `miller.exe` directly failed from `C:\Users\CHS300372` with Miller's
  sensitive-root guard.
- A standalone launcher root under `~/.miller/plugin-cache/cursor-global-miller`, plus global `~/.cursor/mcp.json`
  env `MILLER_WORKSPACE_ROOT="${workspaceFolder}"`, resolved a real workspace to `C:\source\miller` on one probe and
  failed closed when `${workspaceFolder}` was unresolved on others.

**Current recommendation:** Cursor plugin marketplace install, or user-global `~/.cursor/mcp.json` with direct
`miller serve` (absolute path). The plugin launcher no longer guesses workspace cwd; Miller binds via MCP roots.
Optional `MILLER_WORKSPACE_ROOT` env for clients without roots support.

Treat `~/.cursor/plugins/local/miller` as a package/testing artifact, not the normal user install, because Cursor
can launch plugin MCP servers before a workspace exists and can auto-import Claude plugin copies as duplicate Miller
rows.
