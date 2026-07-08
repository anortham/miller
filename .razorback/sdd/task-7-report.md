# Task 7 — Boundary docs sync — Report

Status: COMPLETE. Fast suite green; AGENTS.md byte-identical to CLAUDE.md.

(Note: this file previously held an unrelated "guidance-delivery" task-7 report committed in b1a9d72;
overwritten here with the P4 metric-history Task 7 report, its actual owner on this branch.)

## What changed per file

### CLAUDE.md ("1.0 replacement boundary" — surgical edit of the trailing clause only)
Replaced the stale `metric history/trends are a designed-not-built P4` clause and the blanket
report/dashboard/MCP approval gate. Key new sentences:

> "...count-level report/dashboard surfacing of candidates (trend counts `dead_code_candidate_count`
> and suppressed totals) was APPROVED by the user 2026-07-07, while per-symbol candidate detail stays
> CLI-only and any new MCP tool still requires explicit approval per the MCP-stinginess rule. Metric
> history/trends SHIPPED as the P4 slice: an append-only `history.db` sidecar (hybrid converge/heavy-arm
> snapshots), the `miller metrics history` CLI verb (`docs/contracts/metrics-history-v1.md`), dashboard
> trend sparklines, and `workspace health` history status.)"

The MCP-stinginess paragraph itself was left untouched (new MCP tools still require approval — the rule is
preserved, and the boundary sentence now restates it explicitly for the dead-code count approval).

### AGENTS.md
Regenerated via `scripts/sync-agents.sh`; `cmp -s CLAUDE.md AGENTS.md` → IDENTICAL.

### README.md (three spots, same style)
1. "Replacing Julie" Miller bullet: added "...plus per-workspace metric-history trends (`miller metrics
   history` and dashboard trend sparklines) over those recorded facts."
2. CLI metrics section: new paragraph describing `miller metrics history` (symbol count, complexity p90,
   clone groups, markers, dead-code candidate counts; converge + heavy-arm recording; append-only
   `.miller/history.db`; dashboard sparklines; `workspace health` sidecar status; `--json` contract link)
   plus two example commands.
3. Dashboard detail-view paragraph: added metric-history trend sparklines (read from
   `.miller/history.db`) to the list of surfaced facts.

### docs/README.md (documentation map — Current docs list)
Added three entries after the `metrics-json-v1.md` contract line, following existing conventions:
- `contracts/metrics-history-v1.md` (active contract)
- `plans/2026-07-07-metric-history-design.md` (design record)
- `plans/2026-07-07-metric-history-implementation-plan.md` (implementation plan)

## Verification
- `scripts/test.sh` (fast suite): Passed — 3027 passed, 0 failed, 0 skipped (26s wall).
  AgentInstructionsTests + convention guards green.
- `cmp -s CLAUDE.md AGENTS.md && echo IDENTICAL` → IDENTICAL.
- Stale-language grep: no live `designed-not-built` claims remain. The only remaining hits are inside the
  two P4 plan docs (`2026-07-07-metric-history-{design,implementation-plan}.md`), which describe the
  transition itself ("P4 changes from designed-not-built to shipped") — intentional historical record,
  not a stale product claim.

## Judgment calls
- Kept every claim to the shipped surfaces named in the design doc + `metrics-history-v1.md` contract:
  `history.db` sidecar, `miller metrics history` CLI, dashboard sparklines, `workspace health` status.
  Did not describe any not-shipped surface (no MCP tool, no `metrics snapshot` verb).
- Added the README dashboard-detail sparkline sentence (beyond the minimum single mention) because Task 6
  shipped exactly that surface and the detail-view paragraph already enumerates surfaced facts — keeping it
  accurate rather than partial.
- Left release-version facts and the MCP-stinginess paragraph untouched, per spec.

## Concerns
None. Docs-only change; no code touched.
