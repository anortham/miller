# Miller Plugin Distribution Design

## Decision

Miller should use the main repository as the plugin repository for the first public plugin path. Do not create a
separate `miller-plugin` repository yet.

The plugin layer is intentionally thin:

- Claude Code reads `.claude-plugin/plugin.json`.
- Cursor reads `.cursor-plugin/plugin.json`.
- Codex reads `.codex-plugin/plugin.json` plus the root `.mcp.json` companion.
- Both surfaces expose the repo's Miller skills through root `skills/`.
- A Node launcher downloads, verifies, caches, and runs the matching Miller release archive.

## Why

Miller already has a release workflow that builds the real product archives for each pinned `julie-extract`
target. Reusing those archives keeps plugin distribution tied to the tested binary artifact instead of adding a
second product build in another repo.

This differs from Julie because Julie's plugin repo carries a prebuilt plugin-local binary launcher shape. Miller's
release workflow can be the binary source of truth, so a split repo would mostly create synchronization work before
there is evidence that the separation is useful.

## Launcher Contract

`bin/miller-plugin-launcher.cjs`:

- maps the host platform to the release matrix target;
- downloads the matching archive and `.sha256` sidecar from GitHub releases;
- verifies the archive hash before extraction;
- caches packages under `~/.miller/plugin-cache/<version>/<target>/package`;
- runs the cached `miller serve` binary;
- respects `MILLER_BINARY` for local development and debugging.

The release version and repository live in `miller-plugin.json`. `MILLER_PLUGIN_VERSION`,
`MILLER_PLUGIN_REPOSITORY`, and `MILLER_PLUGIN_CACHE` are override hooks for development and tests.

## Skills

Root `skills/` is a generated mirror of `.agents/skills/`. Run:

```bash
scripts/sync-plugin-skills.sh
```

The plugin manifest tests verify the mirror is byte-for-byte identical.

## Cursor

Cursor needs a first-class `.cursor-plugin/plugin.json`; importing the Claude manifest leaves
`${CLAUDE_PLUGIN_ROOT}` unexpanded and Node tries to launch a literal non-existent path. The Cursor manifest uses a
relative `./bin/miller-plugin-launcher.cjs` command with `cwd: "."`, matching Cursor's plugin-root execution model
and avoiding Claude-specific environment variables.

For local Cursor testing, install this checkout or a packaged plugin under `~/.cursor/plugins/local/miller` and
reload Cursor. The package root must contain `.cursor-plugin/plugin.json`, `skills/`, `miller-plugin.json`, and
`bin/miller-plugin-launcher.cjs`.

## Verification

The plugin distribution surface is covered by:

```bash
scripts/test-plugin.sh
```

The normal Miller fast suite remains separate from the Node plugin checks so .NET source-checkout development stays
focused on the core server.
