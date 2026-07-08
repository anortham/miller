# Autonomous Execution Report - Metric History & Trends (P4)

**Status:** Complete (push + PR awaiting user approval per approval boundaries)
**Plan:** docs/plans/2026-07-07-metric-history-implementation-plan.md (design: docs/plans/2026-07-07-metric-history-design.md)
**Branch:** feat/metric-history (base main@88efb27, HEAD 8c2ea36 + this report commit)
**PR:** not created yet (push is approval-gated)
**Duration:** ~4h same-session (2026-07-07 evening)
**Phases:** 2/2 batches + 2 serial lanes complete
**Tasks:** 7/7 complete + 1 external-review fix round

## What shipped
- Task 1: `MetricHistoryStore` + `history.lock` — append-only `history.db` sidecar (schema v1: snapshots + snapshot_metrics, WAL, UNIQUE(artifact_id, revision, source) dedup), non-blocking file-lock writes, reactive corruption recovery (commits 26db5f9, 6bb1857)
- Task 2: leader converge arm — cheap SQL aggregates (symbol_count, complexity_p90, clone_group_count, marker_total, …) recorded after sidecar converge in `IndexerSidecarConverger` + `CrossWorkspaceRefreshService`, best-effort/never-blocks, FTS region index wired for marker counts (5d1d865)
- Task 3: heavy arms — `miller metrics churn|risk`, `miller report`, `miller references candidates` record snapshots on canonical/default-param runs with identity recheck inside the append txn; report arm deliberately omits its truncated clone count (51b9222)
- Task 4: `miller metrics history [--metric] [--limit] [--json]` read verb + stable contract `docs/contracts/metrics-history-v1.md` + capabilities advertisement (9394f42)
- Task 5: `workspace remove` now co-holds content.lock + history.lock via `WorkspaceWriteLeases` before gutting `.miller/` — also fixes a PRE-EXISTING race where removes could delete content.db mid-import (46a5190)
- Task 6: dashboard per-workspace trend sparklines (pure-SVG, read-only sidecar probe, no index hydration) + `workspace health` `history_db:` line and JSON block (5a8c7f7)
- Task 7: boundary docs truth-up — CLAUDE.md/AGENTS.md replacement boundary (P4 shipped; dead-code count-surfacing approval 2026-07-07 recorded), README, docs/README.md map (ed11791)

## Judgment calls (non-blocking decisions made)
- `MetricHistoryStore.cs` — reactive corruption recovery (catch corruption → rename aside → retry once) instead of per-write `PRAGMA quick_check`, which would scan the whole DB on every write and grow forever under keep-all retention (review round, 6bb1857).
- `ReportTool.BuildSnapshotMetrics` — report arm records NO clone_group_count: its SectionLimit-truncated count sharing a metric name with converge's exact count would corrupt the flattened trend (sawtooth). Absent row over misleading value.
- `MetricsTool.cs` (Task 4) — added `schema_version: 1` to the history JSON envelope beyond the plan sketch; every sibling json_contract carries one; strictly additive.
- `MetricSnapshotAggregates.cs` — marker vocabulary duplicated from Server-internal MarkerSearch with a keep-in-step comment (Miller.Indexing cannot reference Miller.Server).
- Dashboard trends read `maxPoints=50` downsampling; CLI reads `maxPoints=0` (no downsampling) — dashboard is a sparkline, CLI is the data surface; documented in the contract.

## External review (codex, adversarial)
- **Findings:** 2 (verdict: needs-attention)
- **Verified real, fixed:** 2 (commit: 8c2ea36)
  - HIGH — corruption recovery deleted `history.db-wal`/`-shm`, discarding committed WAL-resident snapshots from the one non-derivable sidecar; fixed: whole bundle renamed aside under one corrupt-stamp with SQLite-replayable sibling naming; empirical Microsoft.Data.Sqlite WAL-close probe informed the test fixture.
  - MEDIUM — present-but-unreadable history read as empty everywhere (CLI exit-0 "no trend data yet", empty dashboard panel, healthy-looking health line); fixed: three-state reads — `MetricHistoryUnreadableException`, CLI exit 3, dashboard "history unreadable" panel, `workspace health` unreadable surfacing.
- **Dismissed:** 0
- **Flagged for your review:** 0
- Cost note: codex does not report per-request token counts.

## Tests
- Branch gate @ 8c2ea36: `dotnet build Miller.slnx -c Release` 0 warnings/0 errors; `scripts/test.sh` 3037 passed/0 failed (fast suite); `scripts/test.sh scale` 48 passed/0 failed.
- Known pre-existing flake (untouched, unrelated): `IndexerServiceLeadershipTests` timing tests fail intermittently under parallel load, pass isolated — observed twice by workers, never in lead gate runs.

## Blockers hit
- None. Push/PR intentionally held at the approval boundary (user instructions: never push without explicit approval).

## Files changed
- 43 files, +5,788 / −865 (`git diff --stat 88efb27..HEAD`): new `MetricHistoryStore`/`MetricHistoryWriteLock`/`MetricSnapshotAggregates` in Miller.Indexing; converge/heavy-arm wiring in Miller.Server; dashboard trends in Miller.Dashboard; `metrics history` CLI + contract doc; `WorkspaceWriteLeases` remove coordination; boundary docs; ~1,300 lines of new tests.

## Next steps
- Push branch + open PR to main (approval-gated; note local main holds 5 unpushed commits incl. the 2.11.0 pin bump).
- After merge: Codex release-payload review (user directive) over merged main + pending 4 dead-symbol deletions BEFORE any release prep.
- Release prep + publish: separate explicit approvals.
