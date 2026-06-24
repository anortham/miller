# TODO

No active implementation items.

## Recently Addressed In v0.5.8 Prep

- Trace/impact empty-state and CLI parity slice:
  - return actionable fallback context when `trace` resolves a target but finds no graph neighbours.
  - distinguish empty telemetry reasons for unresolved targets, no seed symbols, and no graph dependents.
  - expose `impact` CLI parity for existing MCP inputs: `--changed-paths` and `--diff`.
- Git-aware impact CLI slice:
  - add `impact --git` to read the current working-tree git diff and run existing diff-to-impact mapping.
  - support `--base REF` and `--staged` for branch or index blast-radius checks.
  - return a clean no-impact message for empty git diffs and an operational error for git failures.
- Git-aware impact MCP slice:
  - add `impact(git=true)` to read the selected workspace's git diff and reuse the existing diff-to-impact mapping.
  - support `base` and `staged` parameters, with both implying git mode.
  - keep the shared git diff reader injected so MCP tests stay subprocess-free.
- Region index default-on slice:
  - make source-region indexing default-on when the search sidecar is enabled.
  - preserve explicit opt-out with `MILLER_REGION_INDEX=0`.
  - update stale docs and guidance that described region indexing as opt-in.
- TODO/FIXME/HACK comment surface:
  - add marker audits over comment/doc-comment source-region data via `search --mode markers`.
  - preserve `todos` as a CLI compatibility alias for Eros/scripts, not as a standalone MCP tool.
  - return marker, file:line, snippet, and containing symbol when available.
  - support marker, file-pattern, language, test-exclusion, workspace, limit, and JSON options.

## Product Backlog

- Cross-tool discoverability: keep improving high-traffic empty states so `search`, `trace`, `impact`, and `inspect` hand agents to `content`, `patterns`, source-region search, or complexity when those are the better next tool.

## Conditional Backlog

- Eros-first complexity workflows: keep `complexity export --jsonl` as the Miller fact feed. Do not add a Miller MCP/interactive complexity tool unless Eros dashboard usage proves a repeated agent workflow that cannot be served by the export.
- Eros-first dead-code workflows: keep dead-code candidate ranking, suppressions, history, cleanup tasks, and multi-workspace reporting in Eros. Add a narrow Miller references/graph JSONL export only if Eros cannot compute candidates cleanly from existing public surfaces.
- Eros CLI/export contracts: add or harden Miller CLI/export surfaces only when a concrete Eros workflow needs stable code facts or operations that the documented contracts do not cover. Current public surfaces are documented in `docs/contracts/cli-eros-v1.md`.
- Miller-native query/ranking surfaces: design only after a concrete agent or Eros workflow needs them. Likely future slices are structural-fact search/filtering, complexity report/ranking with Miller-owned thresholds, and body-hash duplicate/clone discovery.
