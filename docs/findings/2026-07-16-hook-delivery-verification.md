# Hook delivery verification — v1.10.0 (T5.5 gate)

**Date:** 2026-07-16 · **Release:** [v1.10.0](https://github.com/anortham/miller/releases/tag/v1.10.0) (full release, published 2026-07-16T15:59:51Z, tag on cf2bded)
**T7.2 adoption-measurement clock starts:** 2026-07-16 (re-measure on/after 2026-07-30).

## Release evidence

- Tag-push workflow run 29513220607 succeeded; all 8 assets live: 4 platform archives + 4 `.sha256` sidecars
  (`aarch64-apple-darwin`, `x86_64-apple-darwin`, `x86_64-unknown-linux-gnu`, `x86_64-pc-windows-msvc`).
- Archive smoke (aarch64-apple-darwin, downloaded from the live release): checksum OK; `miller version` →
  `1.10.0+cf2bded066ef`; `miller rules --harness cursor` emits the framed block on stdout with the path note on
  stderr; misspelled `--harnes` exits 2 with empty stdout (the post-merge review fix shipped).
- All six version spots aligned at 1.10.0 (`Directory.Build.props`, `miller-plugin.json`, plugin manifests ×3,
  `marketplace.json` ×2 fields); `plugin-manifest.test.cjs` alignment gate green.

## Live hook observations — Claude Code (marketplace install of v1.10.0)

Method: `claude plugin marketplace add anortham/miller` + `claude plugin install miller@miller` (installed
1.10.0), then headless `claude -p` sessions probing for a phrase present ONLY in the hook block.

| Check | Probe | Result |
|---|---|---|
| SessionStart injection | headless session, hook-unique phrase | **INJECTED** ✅ |
| SubagentStart injection | spawned subagent probes its own context | **INJECTED** ✅ |
| Opt-out (`MILLER_SESSION_HOOKS=0`) | same session probe | **ABSENT** ✅ |
| Opt-out at script level | direct run, both events | 0 bytes stdout, exit 0 ✅ |

**Probe-contamination lesson (method note for future smokes):** the first probe phrase ("One Miller call beats
shell greps") exists in BOTH the hook block and the MCP `ServerInstructions`, which any session with a Miller MCP
server also carries — it returned INJECTED even under the opt-out, falsifying nothing. All recorded results above
use "proved before it lands", which appears only in `hooks/miller-routing-block.md`. A hook-delivery probe must
use text unique to the hook payload.

The smoke install was temporary: the plugin and marketplace were removed afterward to restore this machine's
deliberate user-scope-MCP-without-plugin setup.

## Codex — documented host limitation

Codex plugin hooks are **inert** as of this verification: [openai/codex#16430](https://github.com/openai/codex/issues/16430)
(open, `bug`) — plugin docs/examples imply plugin-local hooks, but the runtime only executes the global
`~/.codex/hooks.json`. The manifest wiring ships anyway (validated shape; a 2026-07-16 external review confirmed
the docs side) so Codex delivery activates when the runtime catches up; re-test when #16430 closes. The Codex
trust flow is additionally interactive and cannot be exercised headlessly.

## Versions

| Component | Version |
|---|---|
| Miller release | v1.10.0 (cf2bded) |
| Hook payload | `hooks/miller-routing-block.md` @ v1.10.0 (2,359 chars, canary-gated) |
| Claude Code (smoke host) | local CLI, 2026-07-16 |
| Codex | not exercised (inert per #16430) |
