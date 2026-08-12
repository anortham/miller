# TODO

## Active

- Semantic activation after `miller semantic prepare` requires a session restart (found 2026-08-02 fresh-machine
  dogfood; evidence `.memories/2026-08-02/224155_d317.md`). Two latches: the broker stats the model cache only at
  spawn, and Miller's embedding session opens its circuit permanently on `model_not_prepared`. Fix:
  1. julie-extractors: broker re-stats the cache on `health` while unready, loads and flips ready.
  2. Miller: park (don't latch) on `model_not_prepared`; re-probe health on the converge tick. Never respawn the
     sidecar — the no-restart-loop invariant stays.
  3. `semantic prepare`: after download, send one health probe to a live broker and print the outcome; surface the
     reason + prepare hint in compact `workspace status`; fix the misleading health recommended-action.
  Miller side ships first and is safe with old sidecars; the broker fix rides the next sidecar pin bump.

- JSON diagnostics during family-store resolution convergence: `inspect`, `trace`, and `impact` with
  `format=json` pass an empty result into `ToolDiagnosticRenderer.AttachJson` and return
  `invalid_json_output`; compact correctly returns `resolution_converging`. Add JSON variants to
  `ResolutionLayerGuardTests` and render a standalone diagnostic when the attached output is empty
  (found 2026-08-11 dogfood; evidence `.memories/2026-08-11/125539_bf6d.md`).

## Product Backlog

- Cross-tool discoverability: keep improving high-traffic empty states so `search`, `trace`, `impact`, and `inspect` hand agents to `content`, `patterns`, source-region search, or complexity when those are the better next tool.

- MCP SDK / stateless MCP: with new stateless MCP support available now, evaluate and plan the upgrade to the new MCP SDK. Goal: drop long-lived reader process assumptions where they hurt, improve multi-client behavior under Hermes gateway + CLI, and reduce cold/warm path surprises. Capture current stdio multi-process shape (gateway child + per-session reader) before changing it.

- Workspace blacklist / `.julieignore` sufficiency (station incident 2026-08-10):
  Hermes CLI opened `workspace_id=hermes-agent` against `~/.hermes/hermes-agent` and built a full index there (~7.8k files, ~543k symbols, ~4.3G under `.miller/`). Query path then sat at ~3.5s search / ~6.5s inspect; one `ensure_fresh` cold open took ~254s. Decide whether `.julieignore` (or a Miller-global ignore/deny list) is enough to keep install/home paths like `~/.hermes/hermes-agent` from being indexed, or whether path-class policy is required (e.g. deny `~/.hermes/**`, allow `~/source/**` by default). Note: `.julieignore` only helps once a root is chosen — it does not stop a bad root from being registered.

- Explicit workspace registration gate: consider requiring a user/agent interactive confirmation (or an explicit `workspace open --register` / allowlist step) before Miller builds or attaches a new workspace index. Intent: stop unintended indexes, enforce intentional working dirs, and make “current workspace” a conscious choice rather than a side effect of the first `search`/`inspect` with a path/`workspace_id`. Pair with clearer agent guidance when a root looks like an install tree, home config tree, or `/tmp`.

## Conditional Backlog

- Eros-first complexity workflows: keep `complexity export --jsonl` as the Miller fact feed. Do not add a Miller MCP/interactive complexity tool unless Eros dashboard usage proves a repeated agent workflow that cannot be served by the export.
- Dead-code workflow split: Miller owns deterministic local facts (`references export`, CLI-only `references candidates`, and metric-history candidate counts). Eros owns suppression persistence, cleanup tasks, richer ranking, and multi-workspace/fleet reporting.
- Eros CLI/export contracts: add or harden Miller CLI/export surfaces only when a concrete Eros workflow needs stable code facts or operations that the documented contracts do not cover. Current public surfaces are documented in `docs/contracts/cli-eros-v1.md`.
- Miller-native query/ranking surfaces: design only after a concrete agent or Eros workflow needs them. Likely future slices are structural-fact search/filtering, complexity report/ranking with Miller-owned thresholds, and body-hash duplicate/clone discovery.
