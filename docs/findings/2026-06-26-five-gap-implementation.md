# Five-gap implementation note

Status: historical implementation evidence for the 2026-06-26 Miller five-gap plan.

Current note: the metrics MCP tool implemented during this slice was later removed. Metrics are now CLI-only via
`miller metrics <churn|clones|complexity> --json`; see `docs/contracts/metrics-json-v1.md` for the active
contract. Keep the older MCP-oriented names below as implementation evidence, not current agent guidance.

## Completed surfaces

- Added `metrics` as one deterministic local metrics surface with `operation=churn`, `operation=clones`, and
  `operation=complexity`.
- Added empty-state candidate recovery for high-traffic symbol misses.
- Added `workspace leader` diagnostics plus explicit graceful handoff requests through the local request queue.
- Added dashboard health and onboarding projections/panels.
- Added clone-group and complexity-ranking readers over existing artifact facts.

## Boundary decisions

- Churn maps historical git hunks to current-index symbols and labels the mapping basis. It does not reconstruct
  historical ASTs.
- Clone discovery groups identical normalized `body_hash` values. It does not emit source bodies or cleanup advice.
- Complexity ranking uses transparent local thresholds. Fleet ranking, suppressions, history, and cleanup workflows
  remain Eros-owned.
- Leader handoff uses request files and graceful abdication. Miller does not kill stale processes.
- Dashboard panels consume bounded read-only projections; they do not hydrate full indexes for detail rendering.

## Verification

Focused verification while implementing:

- `SmartTargetResolverTests`
- `SearchToolTests`
- `MetricsReaderTests`
- `MetricsToolTests`
- `LeaderScanRequestQueueTests`
- `IndexerLeadershipCoordinatorTests`
- `IndexerServiceLeadershipTests`
- `WorkspaceToolTests`
- `CliDispatchTests`
- `Dashboard` test filter

Final branch-gate results are expected to be recorded in the session closeout after `scripts/test.sh` and
`dotnet build Miller.slnx -c Release` complete.
