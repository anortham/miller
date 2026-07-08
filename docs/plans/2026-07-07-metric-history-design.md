# Metric History & Trends (P4) — Design

Status: DRAFT — pending user review
Date: 2026-07-07
Predecessor: [2026-07-06-miller-standalone-bolstering-assessment.md](2026-07-06-miller-standalone-bolstering-assessment.md) (P4, green-lit after P1–P3 proved out; dead-code gate passed 2026-07-07)

## Purpose

Give Miller a per-workspace record of how deterministic quality metrics change over time, and
surface those trends on the dashboard and CLI. This is the last item of the standalone
bolstering plan: P1 `metrics risk`, P2 `miller report`, and P3 `references candidates` compute
point-in-time facts; P4 makes them comparable across revisions ("complexity p90 is down 12%
since last month", "dead-code candidates: 392 → 5").

Everything here is composition over already-computed facts. No new extraction, no embeddings,
no new MCP tool.

## Decisions locked with the user (2026-07-07)

1. **Snapshot trigger: hybrid.** The leader records cheap SQL-only aggregates automatically
   after each converge; heavy metrics (git churn/risk, dead-code candidates) are recorded only
   when the command that computes them actually runs. No git subprocess and no literal file
   scan ever enters the background convergence path.
2. **Retention: keep everything in v1.** Snapshot rows are a few hundred bytes; no pruning,
   no compaction pass. Read paths downsample. Revisit only if `workspace health` shows real
   size pressure.
3. **Dead-code candidate counts are included** in history and dashboard trends. This is the
   explicit user approval CLAUDE.md required for report/dashboard surfacing of candidates
   (granted for *counts + suppressed breakdown*; per-symbol candidate detail remains CLI-only).
   The CLAUDE.md boundary sentence is updated in this slice.

## Storage — `<workspace>/.miller/history.db`

A new workspace-local sidecar, sibling to `search.db`/`content.db`, owned by a single new
class `MetricHistoryStore` in `Miller.Indexing`. Unlike `search.db` it is **append-only and
never atomically replaced** — it is not derivable from the current artifact (it IS the
history), so the rebuild-by-replace pattern would erase it.

```sql
CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
-- meta: schema_version = 1

CREATE TABLE snapshots(
    snapshot_id       INTEGER PRIMARY KEY AUTOINCREMENT,
    recorded_at_utc   TEXT NOT NULL,          -- ISO-8601 UTC, writer's clock
    workspace_id      TEXT NOT NULL,
    artifact_id       TEXT NOT NULL,
    revision          INTEGER NOT NULL,
    extractor_version TEXT NOT NULL,          -- artifact_metadata.binary_version
    miller_version    TEXT NOT NULL,
    source            TEXT NOT NULL,          -- 'converge' | 'report' | 'metrics' | 'references'
    UNIQUE(artifact_id, revision, source)
);

CREATE TABLE snapshot_metrics(
    snapshot_id INTEGER NOT NULL REFERENCES snapshots(snapshot_id) ON DELETE CASCADE,
    metric      TEXT NOT NULL,                -- e.g. 'symbol_count', 'complexity_p90'
    value       REAL NOT NULL,
    detail_json TEXT NULL,                    -- bounded breakdown (per-marker counts, params)
    PRIMARY KEY(snapshot_id, metric)
);
CREATE INDEX idx_snapshot_metrics_metric ON snapshot_metrics(metric, snapshot_id);
```

Key-value metrics instead of wide columns: adding a metric later is a data change, not a
schema migration. Keying follows the assessment's Codex amendment #6: every snapshot carries
`(workspace_id, artifact_id, revision)` plus extractor version, because full rebuilds restart
the revision counter — **trend ordering is by `recorded_at_utc`, never by revision alone**,
and comparisons across an `artifact_id` change are still valid (same workspace, wall-clock
ordered).

Write semantics:

- `source='converge'`: `INSERT OR IGNORE` on the unique key — exactly one converge snapshot
  per `(artifact_id, revision)`, first writer wins.
- Heavy sources: upsert — delete + reinsert that snapshot's metrics in one transaction, so
  re-running `miller report` at the same revision refreshes rather than duplicates.
- **A missing metric is an absent row, never 0.** (Amendment #1's null-vs-absence rule.) If
  the marker/region index is unavailable at converge time, no `marker_total` row is written.

## Writers

### Cheap arm — leader, after converge

Hook: the same leader-owned path that converges `search.db`/`content.db` after a revision
lands (IndexerService leader / CrossWorkspaceRefreshService), guarded by the existing
`SingleWriterLock` ownership. After sidecar convergence succeeds, the leader records one
`source='converge'` snapshot with SQL-only aggregates read from the just-served artifact and
sidecars:

| metric | source of truth | detail_json |
|---|---|---|
| `symbol_count`, `file_count`, `language_count` | `symbols.db` aggregates | — |
| `marker_total` | region index (only if available) | per-marker counts (TODO/FIXME/…) |
| `complexity_p50`, `complexity_p90`, `complexity_max` | complexity facts in artifact | — |
| `clone_group_count` | clone facts in artifact | — |

Budget: one bounded pass of aggregate queries; failures are logged and **never fail or delay
indexing** — history is best-effort telemetry, not a freshness invariant.

### Heavy arm — recorded by the command that computed the fact

When run against a registered workspace, these commands append what they just computed:

- `miller report` → `source='report'`: everything the report composed, including churn/risk
  scalars (range recorded in `detail_json`) and, if it composes candidates in future, counts.
- `miller metrics churn|risk` → `source='metrics'`: `churn_files_changed`,
  `risk_top_score` etc., with the range/params in `detail_json`.
- `miller references candidates` → `source='references'`: `dead_code_candidate_count`,
  `dead_code_suppressed_total`, suppressed breakdown in `detail_json`.

Heavy-arm recording is also best-effort: a failed history write warns on stderr/log but never
fails the command that computed the metrics.

### Multi-process append (the one new pattern)

The leader (server process) and CLI one-shots may write concurrently. This is the first
multi-writer sidecar, handled by construction:

- WAL mode, `busy_timeout` (5s), transactions are single-statement-scale appends.
- The **leader owns schema creation/migration**; a CLI writer that finds no `history.db` (or
  an older `schema_version`) creates/migrates it with the same idempotent DDL under
  `busy_timeout` — DDL is `CREATE TABLE IF NOT EXISTS`, so racing creators converge.
- Contention is inherently low: converge writes are one row-set per revision; heavy writes
  happen at human/CI command frequency.

### Corruption / recovery

`history.db` cannot be rebuilt. On open failure or integrity error the writer renames the
file aside to `history.db.corrupt-<utc-stamp>` and starts a fresh one, logging a warning;
`workspace health` surfaces the history sidecar status (present/fresh/corrupt-recovered) and
size. Losing history on corruption is acceptable; silently blocking writes is not.

## Read surfaces

### CLI — `miller metrics history`

```
miller metrics history [--metric complexity_p90[,symbol_count,…]] [--limit N] [--json]
```

- Default: last `N=20` snapshots, one compact line per snapshot (timestamp, revision, source,
  selected metric values), newest last so trend direction reads naturally.
- `--metric` filters to named metrics; omitted = a default set (symbol_count, complexity_p90,
  clone_group_count, marker_total, dead_code_candidate_count).
- `--json`: typed envelope `{ workspace_id, metrics: [{ metric, points: [{ recorded_at_utc,
  artifact_id, revision, source, value }] }] }` — documented as a stable contract in
  `docs/contracts/metrics-history-v1.md` so Eros can consume it without .NET coupling.
- Read-only open; works in any process (reader role included).

### Dashboard — trend sparklines

Workspace detail page gains a "Trends" section: sparklines for symbol count, complexity p90,
clone groups, markers, and dead-code candidates. Reads `history.db` read-only via the
established `DashboardIndexFactsReader` pattern (aggregate facts only — **no index
hydration**), downsampled at read time to ≤50 points per sparkline (uniform stride over the
wall-clock range). Metrics with <2 points render as "no trend data yet" with the command that
would produce data (e.g. run `miller report`).

No new MCP tool. Agents reach trends through the CLI, per the assessment's boundary rule.

## Boundary housekeeping (same slice)

- CLAUDE.md "1.0 replacement boundary": P4 changes from "designed-not-built" to shipped;
  the dead-code sentence changes to record that count-level report/dashboard surfacing was
  approved 2026-07-07 (per-symbol detail remains CLI-only). Run `scripts/sync-agents.sh`.
- README/site replacement-story copy: mention metric trends where metrics/report are listed.
- `docs/contracts/metrics-history-v1.md`: new contract doc for the sidecar schema + CLI JSON.

## Error handling summary

| Failure | Behavior |
|---|---|
| History write fails (any arm) | Log/warn; indexing and the computing command still succeed |
| `history.db` corrupt | Rename aside, recreate, warn, surface in `workspace health` |
| Region index unavailable at converge | Omit marker metrics (absent, not 0) |
| Unregistered workspace / no `.miller` | Heavy arms skip recording silently (nothing to attach history to) |
| Concurrent writers | WAL + busy_timeout; converge dedup via INSERT OR IGNORE |

## Testing

Fast suite (pure, temp-dir SQLite — same style as `SearchIndexWriterTests`):

- `MetricHistoryStoreTests`: schema creation, converge dedup (second identical converge is a
  no-op), heavy-arm upsert (re-run replaces), absent-vs-zero semantics, trend read ordering by
  `recorded_at_utc` across an `artifact_id` change, downsample stride, corruption
  rename-aside recovery, concurrent-writer smoke (two connections, busy_timeout).
- `MetricsToolTests`/`ReportToolTests` additions: command run against a seeded workspace
  records the expected snapshot rows; history-write failure does not fail the command.
- `CliDispatchTests`: `metrics history` routing, compact + `--json` output shapes.
- Dashboard reader test: trend read + downsampling from a seeded `history.db`.

Scale suite: one end-to-end test — real converge on a small fixture writes a converge
snapshot (rides the existing julie-spawning workspace test pattern, tagged Scale).

Language parity note: not extractor-schema-dependent — aggregates are language-agnostic
rollups of already-shipped facts, so the parity gate is inherited from P1–P3, not re-opened.

## Explicitly out (v1)

- Pruning/compaction/retention config (keep-all decision above).
- Cross-workspace/fleet trend aggregation (Eros).
- Snapshot-on-demand verb (`metrics snapshot`) — converge + report already cover the need;
  add only on demonstrated demand.
- Trend deltas inside `miller report` output (candidate for a later polish slice).

## Acceptance criteria

- [ ] `MetricHistoryStore` writes/reads `history.db` with the schema above; fast contract
      tests green.
- [ ] Leader records a converge snapshot after sidecar convergence; failure is non-fatal.
- [ ] `miller report`, `metrics churn|risk`, `references candidates` record heavy-arm
      snapshots on registered workspaces.
- [ ] `miller metrics history` compact + `--json` output; contract doc committed.
- [ ] Dashboard workspace detail shows downsampled sparklines incl. dead-code candidate
      count; no index hydration.
- [ ] `workspace health` reports history sidecar status/size.
- [ ] CLAUDE.md/AGENTS.md boundary text updated; README/site copy updated.
- [ ] Fast suite green (<30s budget); scale suite green; build 0 warnings/0 errors.
