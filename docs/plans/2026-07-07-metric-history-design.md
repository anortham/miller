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
    source            TEXT NOT NULL,          -- computing operation: 'converge' | 'report' | 'churn' | 'risk' | 'candidates'
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
the revision counter — **trend ordering is by `snapshot_id`** (monotonically assigned within
the single history file; immune to clock skew between writer processes), with
`recorded_at_utc` as display metadata and the range-filter axis. Comparisons across an
`artifact_id` change are still valid (same workspace, insertion-ordered).

Write semantics:

- **`source` is the computing operation**, not the process kind: one command run = one
  snapshot with one coherent timestamp. `metrics churn` and `metrics risk` at the same
  revision are two snapshots (`source='churn'` / `source='risk'`), so neither can clobber or
  time-mislabel the other — which a shared `source='metrics'` bucket would (a later risk run
  would inherit or move churn's `recorded_at_utc`).
- `source='converge'`: `INSERT OR IGNORE` on the unique key — exactly one converge snapshot
  per `(artifact_id, revision)`, first writer wins.
- Heavy sources: upsert scoped to the snapshot's own rows — a re-run of the same operation
  at the same revision replaces its own snapshot (row + metrics) in one transaction and
  touches nothing else.
- Heavy arms record **only canonical-parameter runs** (default range/limit/filters). A
  `metrics churn --range 90d` run is rendered as usual but skips history recording — mixing
  ranges in one trend line would make the points incomparable. The canonical params are
  stamped in `detail_json` so the contract is self-describing.
- **A missing metric is an absent row, never 0.** (Amendment #1's null-vs-absence rule.) If
  the marker/region index is unavailable at converge time, no `marker_total` row is written.
- **Artifact-identity guard (heavy arms):** report/metrics composition opens several
  independent read connections, and a full-rebuild promotion can atomically replace
  `symbols.db` mid-command. The writer captures `(artifact_id, revision)` before computing
  and re-reads it **inside the append transaction**; on mismatch it skips recording (logged)
  rather than stamping fresh identity onto stale numbers. **Accepted residual:** promotion
  takes no history lock, so it can still land in the instant between that re-check and the
  commit — the worst case is one old-artifact point appended after a newer converge
  snapshot, a display-order blip that self-heals as new snapshots land. Eliminating it would
  couple promotion to history locking, which is not worth it for best-effort telemetry.

## Writers

### Cheap arm — leader, after converge

Hook: immediately after the leader's sidecar-converge step (`IndexerSidecarConverger` /
`CrossWorkspaceRefreshService.TryConvergeSidecar`), guarded by the existing
`SingleWriterLock` ownership. History recording is an **independent best-effort step, not
conditioned on sidecar success** — `IndexerSidecarConverger.Converge` returns void and
swallows sidecar failures by design, and the cheap-arm metrics read `symbols.db` directly,
not the sidecars. The only sidecar-dependent metrics are the marker counts, and the recorder
checks region-index availability itself at read time (unavailable ⟹ rows absent). The leader
records one `source='converge'` snapshot per converged revision:

| metric | source of truth | detail_json |
|---|---|---|
| `symbol_count`, `file_count`, `language_count` | `symbols.db` aggregates | — |
| `marker_total` | region index (only if available) | per-marker counts (TODO/FIXME/…) |
| `complexity_p50`, `complexity_p90`, `complexity_max` | complexity facts in artifact | — |
| `clone_group_count` | clone facts in artifact | — |

Budget: one bounded pass of aggregate queries; failures are logged and **never fail or delay
indexing** — history is best-effort telemetry, not a freshness invariant. Concretely: the
converge hook runs under `_opsGate` (the lock that serializes extract subprocesses), so the
leader's history write must be **non-blocking — `busy_timeout` ≈ 0, skip-on-busy, no retry
loop**. A skipped converge snapshot is an absent point in the trend, which the read side
already tolerates; a 5-second busy wait inside `_opsGate` would stall indexing, which is
never acceptable.

### Heavy arm — recorded by the command that computed the fact

When run against a registered workspace, these commands append what they just computed:

- `miller report` → `source='report'`: everything the report composed, including churn/risk
  scalars (range recorded in `detail_json`) and, if it composes candidates in future, counts.
- `miller metrics churn` → `source='churn'` / `miller metrics risk` → `source='risk'`:
  `churn_files_changed`, `risk_top_score` etc., with the range/params in `detail_json`.
- `miller references candidates` → `source='candidates'`: `dead_code_candidate_count`,
  `dead_code_suppressed_total`, suppressed breakdown in `detail_json`.

Heavy-arm recording is also best-effort: a failed history write warns on stderr/log but never
fails the command that computed the metrics.

### Multi-process append (the one new pattern)

The leader (server process) and CLI one-shots may write concurrently. This is the first
multi-writer sidecar, handled by construction:

- WAL mode; transactions are single-append scale. Leader writes are skip-on-busy (above);
  CLI heavy-arm writes may use a short `busy_timeout` (their latency is the user's command,
  not the indexing path).
- **`history.lock`** — a sibling short-lived file lock using the `SingleWriterLock` flock
  mechanics. Every history writer (leader and CLI) holds it only for the duration of the
  append transaction, and **`workspace remove` acquires it (short timeout) before deleting
  `.miller` contents**. Without this, remove — which deletes while holding only the indexer
  `SingleWriterLock` — could hit a CLI history writer mid-append: a Windows sharing-violation
  crash of the remove, or silent unlinked-inode writes on POSIX. Lock order is fixed
  (indexer `SingleWriterLock` first where held, then `history.lock`) so leader and remove
  can't deadlock; CLI writers take only `history.lock`. **Remove-path consequence:**
  `workspace remove` generalizes to coordinate with **all workspace-local write locks**, in
  fixed order: indexer `SingleWriterLock` → `content.lock` → `history.lock`. This fixes a
  **pre-existing latent defect**, not just a new-lock need: CLI content imports already hold
  `content.lock` (`ContentCorpusWriteLock`) without the indexer lock, so today's remove can
  delete `content.db` mid-import (Windows sharing-violation crash / POSIX unlinked-inode
  writes). `SingleWriterLock.DeleteContentsExceptLock` — which currently skips only the
  indexer lock file — generalizes to skip **every held lock file**, and lock-file debris is
  deleted best-effort only after all remove-held leases are released (the pattern the
  indexer lock already uses).
- **Schema policy:** `meta.schema_version = 1`. Only the leader migrates, transactionally.
  Any writer (including an old binary) that finds a **newer** version than it knows skips
  writes and logs — append-only history cannot use the rebuild-on-mismatch escape hatch the
  derived sidecars use, so forward-compatibility is skip, never destroy. A CLI writer that
  finds **no** `history.db` may create it at the current version (idempotent DDL; racing
  creators converge) — creation is not migration.
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
| History DB busy during leader converge | Skip this revision's snapshot (no busy wait inside `_opsGate`) |
| `history.db` corrupt | Rename aside, recreate, warn, surface in `workspace health` |
| Region index unavailable at converge | Omit marker metrics (absent, not 0) |
| Unregistered workspace / no `.miller` | Heavy arms skip recording silently (nothing to attach history to) |
| Artifact replaced mid-command (heavy arm) | Identity re-check fails ⟹ skip recording, log |
| Non-canonical params (heavy arm) | Render as usual, skip recording |
| `schema_version` newer than the writer knows | Skip writes, log (never rebuild/destroy history) |
| `workspace remove` vs in-flight history write | `history.lock` serializes them; remove waits its short timeout then refuses-in-use |
| `workspace remove` vs in-flight content import | `content.lock` now also acquired by remove (fixes pre-existing race) |
| Concurrent writers | WAL + `history.lock`-scoped appends; converge dedup via INSERT OR IGNORE |

## Testing

Fast suite (pure, temp-dir SQLite — same style as `SearchIndexWriterTests`):

- `MetricHistoryStoreTests`: schema creation, converge dedup (second identical converge is a
  no-op), per-source upsert (a churn re-run replaces the churn snapshot and leaves the risk
  snapshot at the same revision intact, timestamps independent), absent-vs-zero semantics,
  trend read ordering by `snapshot_id` across an `artifact_id` change and across
  out-of-order `recorded_at_utc`, downsample stride, corruption rename-aside recovery,
  skip-on-busy under a held write lock, newer-`schema_version` skip-not-destroy,
  artifact-identity-mismatch skip, `history.lock` mutual exclusion with a simulated remove,
  and remove-path deletion skipping a held `history.lock` file.
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

## Doubt pass (Codex, 2026-07-07)

Cycle 1 produced seven refutations; all survived verification against the code and are folded
in above:

1. `busy_timeout(5s)` inside `_opsGate` could stall indexing → leader writes are skip-on-busy.
2. `workspace remove` deletes `.miller` under `SingleWriterLock`, which CLI history writers
   don't hold (verified: `CliDispatch` remove + `SingleWriterLock.DeleteContentsExceptLock`)
   → `history.lock` honored by all writers and by remove.
3. `UNIQUE(artifact_id, revision, source)` let a churn re-run erase risk metrics → per-metric
   upsert; canonical-params-only recording.
4. Heavy arms could stamp fresh artifact identity onto metrics read from a just-replaced
   artifact (promote-over-live) → capture-and-recheck identity, skip on mismatch.
5. "After sidecar convergence succeeds" hook doesn't exist (`Converge` returns void, swallows
   failures) → history recording is independent of sidecar success; reads `symbols.db`.
6. `CREATE TABLE IF NOT EXISTS` is not a migration plan and leader-owns vs CLI-creates
   contradicted each other → explicit policy: leader-only transactional migration, newer
   version ⟹ skip writes, CLI may create-at-current only.
7. Wall-clock trend ordering breaks on clock skew between writer processes → order by
   `snapshot_id`, `recorded_at_utc` is display/filter metadata.

Cycle 2 confirmed the seven amendments and surfaced three new material findings, all folded
in: (a) `DeleteContentsExceptLock` skips only the indexer lock file — generalized to skip
every held lock file, debris cleaned after release; (b) a shared `source='metrics'` bucket
gave churn and risk one timestamp — `source` is now the computing operation
(`converge|report|churn|risk|candidates`), one command = one coherent snapshot; (c) a
promotion landing between identity re-check and append can insert one old-artifact point
after a newer one — window shrunk (re-check inside the append transaction) and the residual
explicitly accepted as a self-healing display blip.

Cycle 3 confirmed all cycle-2 amendments (and judged the accepted residual in (c)
defensible), with one final material finding, folded in: `workspace remove` must coordinate
with **all** workspace-local write locks — Miller already has an uncoordinated
`content.lock` today (a pre-existing remove-vs-import race this slice fixes), and
`history.lock` would have been the third. Doubt pass closed at the 3-cycle cap with that
finding resolved in the design.

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
- [ ] `workspace remove` coordinates with `content.lock` and `history.lock` (fixed lock
      order), and held-lock files survive `DeleteContentsExceptLock`; regression tests for
      the pre-existing remove-vs-content-import race.
- [ ] CLAUDE.md/AGENTS.md boundary text updated; README/site copy updated.
- [ ] Fast suite green (<30s budget); scale suite green; build 0 warnings/0 errors.
