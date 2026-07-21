# Task 8 Report — Docs, runbook, closeout (P5 Canary Stage)

## Working-directory guard
- pwd: `/Users/murphy/source/miller/.claude/worktrees/worktree-semantic-p5`
- branch: `worktree-semantic-p5`
- HEAD at start: `c3fb3aa`; HEAD after commit: `4495cea`
- All work done inside the worktree; nothing touched under `/Users/murphy` or the primary checkout.

## Status
DONE. Commit `4495cea` (serial-worker-commit, not pushed).

## What I wrote

- **`docs/findings/2026-07-21-p5-canary-runbook.md`** (new). Operator runbook, operational-only, links the
  frozen contract instead of restating field definitions. Covers:
  - Enable — the two switches (`MILLER_SEMANTIC`, `MILLER_SEMANTIC_CANARY`), the `off` outranks rule, the two
    typical setups (shadow-measure vs on-serve), what/where gets recorded (`~/.miller/telemetry.db`,
    instrumented surfaces, assignment unit, identifier shadow 10% sampling).
  - Observe/export — `miller telemetry canary [--json] [--from] [--to]`, default 30-day window, suppression,
    determinism.
  - Retention squeeze — 30-day prune, no slack, export weekly and archive off-box.
  - Gate — `miller telemetry canary --gate [--json]`, per exact `miller_version` cohort, three clauses,
    `gate_passes` = success AND warm-latency, shadow reported separately, exit codes.
  - Interpret — pass/fail/underpowered/indeterminate meanings; underpowered/indeterminate = need more
    traffic, not a threshold change.
  - Model swap — `MILLER_SEMANTIC_MODEL` registry, `miller semantic prepare --model <id>`, shadow-rebuild on
    swap, rollback via retained generation (revert env + restart), and the placeholder note that the model
    comparison list/eval is a later phase.
  - Quick-reference command block.
- **`docs/README.md`** — added the runbook pointer under Current docs, immediately after the
  `canary-telemetry-v1` contract line, matching the existing map format.
- **`README.md`** — extended the semantic bullet in Known limits (the closest thing to an env/config section;
  there is no dedicated env table) to document the opt-in experimental env vars and link the runbook. Release
  facts and quickstart untouched.

## Spelling verification (every documented token checked against shipped code)

| Documented spelling | Verifying file:line |
| --- | --- |
| `MILLER_SEMANTIC_MODEL` | `src/Miller.Indexing/Semantic/MillerSemanticContract.cs:315` |
| `qwen3-0.6b-f16` (default model id) | `MillerSemanticContract.cs:95` |
| `bge-small-en-v1.5-f32` (fallback model id) | `MillerSemanticContract.cs:106` |
| unset/empty/unknown → default + one stderr warning | `MillerSemanticContract.cs:318,349` |
| encoder change → `ShadowRebuild` (invalidation matrix) | `MillerSemanticContract.cs:202-206` |
| `MILLER_SEMANTIC_CANARY` = `off`/`on`, `0`/`1` aliases, unknown→off | `src/Miller.Server/Telemetry/CanaryTelemetry.cs:21,25-29` |
| `MILLER_SEMANTIC` = `off`/`shadow`/`on`, default off | `src/Miller.Indexing/SemanticActivation.cs:25,37-42` |
| `miller telemetry canary [--json] [--from YYYY-MM-DD] [--to YYYY-MM-DD]` | `src/Miller.Server/Cli/CliDispatch.cs:1093,1119-1140` |
| `miller telemetry canary --gate [--json]` | `CliDispatch.cs:1094,1125-1129` |
| default export window last 30 days | `CliDispatch.cs:1131-1132` |
| bad date → usage error exit 2 | `CliDispatch.cs:1133-1136,1144-1145` |
| gate/export exit 0 when computed | `CliDispatch.cs:1128,1141` |
| `miller semantic prepare [--model <id>] [--json]` | `CliDispatch.cs:329` |
| verdicts `pass`/`fail`/`underpowered`/`indeterminate` | `src/Miller.Server/Telemetry/CanaryGateReport.cs:301-307` |
| `gate_passes` = success-rate pass AND warm-latency pass | `CanaryGateReport.cs:46-47` |
| clause minimums 5/30/100/30, thresholds 1.20/0.05/8.0 | `CanaryGateReport.cs:60-66` |
| instrumented surfaces `search` op auto/text/symbol/content | contract §Activation (frozen) + `CanaryTelemetry.cs` op stamping |
| assignment unit `(workspace_id, utc_date, query_class)`, 50/50 | `CanaryTelemetry.cs:206,219` |
| identifier shadow: canary-on + semantic∈{shadow,on}, bucket<10 (10%) | `src/Miller.Server/Tools/SearchTool.cs:1313-1316` |
| `semantic_identifier_noninferiority_v1` / `semantic_hybrid_search_v1` | `CanaryTelemetry.cs:202,204` |
| store `~/.miller/telemetry.db`, machine-global | `src/Miller.Server/Hosting/WorkspaceContext.cs:18,47` |
| retention 30 days | `src/Miller.Server/Hosting/IndexBootstrapService.cs:472` |
| shadow serve-first, failure never changes served result | `SearchTool.cs:1307-1322` (and contract §Shadow Population) |

## Verification
- **worker-ceiling (fast suite):** `scripts/test.sh` → **Passed 4390, Skipped 2, Failed 0**, wall 20s. All six
  projects compiled 0-warning during the test build (warnings are errors), so the diagnostic build is
  effectively covered; `AgentInstructionsTests` green and untouched (no guidance-channel edits).
- Known pre-existing flake `RepositoryIndexLoaderBridgeTests` did not surface this run.

## Judgment calls
- **No dedicated env table exists in README.** Searched the whole file — env vars are documented inline per
  feature. Chose the semantic bullet under `## Known limits` as the env/config home, grouping with the
  adjacent `MILLER_REGION_INDEX`/`MILLER_REGION_MAX_BYTES` config bullets. Reworded "not shipped yet" to "off
  by default" since the opt-in canary now ships, keeping the default-lexical guarantee accurate. Release
  facts and quickstart untouched.
- **Runbook in `docs/findings/`** per the plan's stated path (not `docs/contracts/`), matching an existing
  findings header style (title, **Date:**, **Scope:**).
- **Operational-only.** The runbook never restates a normative field/enum/window definition; it links
  `canary-telemetry-v1.md` and states the contract wins on any conflict.
- **Model comparison placeholder.** Documented the two-entry registry as the shipped surface and explicitly
  marked the comparison list/eval as a later phase, so the fallback entry is not read as a switch
  recommendation.

## Concerns
- None blocking. Docs-only change; no code, no guidance channels, no release facts touched. Commit not pushed
  per instructions.
