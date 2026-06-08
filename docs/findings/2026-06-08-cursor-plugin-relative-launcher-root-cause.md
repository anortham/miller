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
