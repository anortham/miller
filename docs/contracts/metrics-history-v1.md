# Metrics History Contract v1

Status: stable. `miller metrics history` is a **read-only** view over a workspace's metric-history
sidecar (`<workspace>/.miller/history.db`). It renders a recorded trend — it never computes or records
a snapshot. Recording is owned by the write arms (below); this surface only reads. The `--json` envelope
is a stable contract Eros may consume without .NET coupling.

`miller metrics history [--metric a,b,…] [--limit N] [--json] [--workspace-id SELECTOR] [--workspace DIR]`
returns the most recent `N` snapshots' values for the selected metrics, one point per snapshot, ordered
so a trend reads naturally (oldest first, newest last).

**Posture: recorded facts, not a live recompute.** Each point is a value that some earlier `miller`
command already recorded against a specific artifact revision. History is append-only and is **never**
rebuilt from the current artifact (it *is* the history), so a point reflects the workspace as it was when
that snapshot was taken, not as it is now. A metric with no recorded points is **absent**, never a
fabricated `0`.

## Invocation And Selectors

`history` is an operation on the existing `metrics` CLI verb (no new MCP tool). It accepts the normal
read-command selectors:

- `--workspace-id SELECTOR` — display ID, unique prefix, full workspace ID, registered root path,
  `current`, or `primary`.
- `--workspace DIR` — path alias, normalized before selection.
- `--metric a,b,…` — restrict to the named metrics. Accepts a comma-separated list and/or a repeated
  flag (`--metric symbol_count --metric marker_total`), de-duplicated in first-seen order. Omitted ⟹ the
  default set below. Column/series order follows the requested order.
- `--limit N` — bound the snapshot window to the most recent `N` snapshots (by `snapshot_id`). Default
  `20`. Clamped to `[1, 500]`.
- `--json` — emit the JSON envelope below instead of the compact table.

A selector flag supplied without a value is a usage error (exit `2`).

### Default metric set

When `--metric` is omitted, the default set is one rollup per signal family:

| Metric | Recording arm (`source`) | Meaning |
|---|---|---|
| `symbol_count` | `converge` | Named symbols in the artifact. |
| `complexity_p90` | `converge` | 90th-percentile `decision_count` over `complexity_metrics`. |
| `clone_group_count` | `converge` | Body-hash groups with ≥ 2 members. |
| `marker_total` | `converge` | Distinct comment regions containing a TODO/FIXME/HACK/XXX marker. |

Any metric name recorded by any write arm may be requested via `--metric`, including heavy-arm names not
in the default set (`churn_files_changed`, `risk_top_score`, `risk_rows`,
`near_duplicate_group_count`). Historical `dead_code_candidate_count` and
`dead_code_suppressed_total` rows remain readable via `--metric`; new snapshots do not record them.
Canonical names are single-sourced on `MetricSnapshotAggregates` (cheap arm) and `MetricHistoryHeavyArm`
(heavy arm) so producer and this reader never drift.

## Exit Codes

Same process-level contract as the other `metrics` operations:

- `0` — success, **including an empty/missing history** (a workspace with no recorded snapshots is a normal
  state, not an error).
- `2` — usage or selector error.
- `3` — operational failure reading the sidecar (unreadable/locked file surfaced as `metrics failed: …`).

## Ordering (load-bearing)

Points are ordered by **`snapshot_id`** (the append order), never by `recorded_at_utc`. `recorded_at_utc`
is writer-clock display metadata and can move backwards across processes/machines; `snapshot_id` is the
monotonic insertion order and is the only stable trend axis. Newest is **last** in both output forms, so a
metric column reads top-to-bottom as time moves forward.

## Compact Output

A header line, a tab-separated column header, then one line per snapshot (oldest first, newest last):

```
# metric history
recorded_at_utc	revision	source	<metric_1>	<metric_2>	…
2026-07-01T10:00:00.0000000Z	40	converge	1000	7	…
2026-07-02T10:00:00.0000000Z	41	report	-	-	…
```

- Columns after `source` are the selected metrics in requested order (or the default set).
- A metric a given snapshot did not record renders `-` — an **absent** value, never `0`.
- Integral values render without a decimal tail (`1200`); a fractional metric (e.g. an interpolated
  `complexity_p90`) keeps up to three fractional digits.
- Because most snapshots come from a single `source`, most cells outside that source's metrics are `-`;
  read a metric down its column, not a snapshot across its row.

### Empty / missing history

An **absent** or empty `history.db` is a normal state — exit `0` with a single nudge line, never an error:

```
no trend data yet — run `miller report`.
```

A **present-but-unreadable** `history.db` (corrupt/locked/not-a-db) is distinct: it is an operational failure
surfaced as `metrics failed: …` with exit `3`, never the friendly exit-`0` nudge above. A broken sidecar must
fail visibly rather than read as an empty trend.

## `--json` Envelope

A single JSON object.

| Field | Type | Description |
|---|---|---|
| `schema_version` | number | Envelope schema version. Currently `1`. |
| `workspace_id` | string | The resolved stable workspace ID (bootstrap-set, else derived from the canonical root). Always present, even when `metrics` is empty. |
| `metrics` | array | One entry per requested metric that has **at least one** recorded point, in requested order. A metric with no points is omitted (so an empty/missing history yields `[]`). |
| `metrics[].metric` | string | The metric name. |
| `metrics[].points` | array | Recorded points for this metric, `snapshot_id`-ordered (newest last). |
| `metrics[].points[].recorded_at_utc` | string | ISO-8601 UTC writer-clock timestamp (display metadata; not the sort axis). |
| `metrics[].points[].artifact_id` | string | The artifact identity the snapshot was recorded against. |
| `metrics[].points[].revision` | number | Workspace revision for that artifact. |
| `metrics[].points[].source` | string | The recording arm: `converge`, `report`, `churn`, `risk`, or `clones`. Historical rows may also carry `candidates`. |
| `metrics[].points[].value` | number | The recorded metric value. |

### Example

```json
{
  "schema_version": 1,
  "workspace_id": "3f2a…",
  "metrics": [
    {
      "metric": "symbol_count",
      "points": [
        { "recorded_at_utc": "2026-07-01T10:00:00.0000000Z", "artifact_id": "artifact-a", "revision": 40, "source": "converge", "value": 1000 },
        { "recorded_at_utc": "2026-07-02T10:00:00.0000000Z", "artifact_id": "artifact-a", "revision": 41, "source": "converge", "value": 1200 }
      ]
    },
    {
      "metric": "near_duplicate_group_count",
      "points": [
        { "recorded_at_utc": "2026-07-02T10:05:00.0000000Z", "artifact_id": "artifact-a", "revision": 41, "source": "clones", "value": 5 }
      ]
    }
  ]
}
```

An empty/missing history:

```json
{ "schema_version": 1, "workspace_id": "3f2a…", "metrics": [] }
```

## Write Arms (how points get recorded — summary)

`history.db` is written by two families of arm; this read surface consumes both. Full design:
[`plans/2026-07-07-metric-history-design.md`](../plans/2026-07-07-metric-history-design.md).

- **Cheap arm (`source='converge'`).** The indexer leader's converge path records one snapshot per
  `(artifact_id, revision)` — `symbol_count`, `file_count`, `language_count`, `complexity_p50/p90/max`,
  `clone_group_count`, and (when the region index is available) `marker_total`. First writer wins
  (`INSERT OR IGNORE`); non-blocking and skip-on-busy so it never stalls indexing.
- **Heavy arms (`source='report'|'churn'|'risk'|'clones'`).** The CLI commands `miller report`,
  `miller metrics churn|risk`, and `miller metrics clones --near-duplicates`
  record a per-source upsert (a re-run at
  the same revision replaces only its own snapshot). Historical `source='candidates'` rows remain
  readable. **Only canonical (default-params) runs record** — a
  run with a custom `--range`/`--limit`/test filter renders normally but does not record, because a trend
  that mixes parameters is incomparable. Recording is best-effort telemetry: a failed write warns on
  stderr and never changes the command's output or exit code.

Because the reader flattens points by metric name across ALL sources, **every value recorded under one
name must be exactly comparable across its producers** ("exact or absent, never misleading"):

- `clone_group_count` and `marker_total` are recorded **only** by the converge arm. The report renders its
  own bounded clone/marker sections but does not record them — its bounds (top-SectionLimit clones; a
  final 500-region marker cap vs converge's per-marker cap) would mix non-comparable values into the
  converge-owned series.
- `churn_files_changed` is the **exact** distinct changed-path count for the range, computed before any
  row truncation, so `report` (section limit 10) and `metrics churn` (limit 50) record identical values
  for the same range. `risk_top_score` is the global maximum and is likewise limit-insensitive.
- `near_duplicate_group_count` is recorded by the two commands that actually run the opt-in Type-2
  (MinHash/LSH) arm: `miller metrics clones --near-duplicates` (`source='clones'`) and
  `miller report --near-duplicates` (`source='report'`). It is the number of near-duplicate groups the scan
  found, computed **before** any display limit and under **fixed** analyzer bounds (a 2000-symbol candidate
  cap and the analyzer's default similarity threshold), so both producers record the identical value for the
  same artifact and no CLI flag can move it. `detail_json` stamps those bounds.
  **Truncation ⟹ absence.** The candidate scan is bounded; when it hits the cap, later files (by path order)
  are never examined and the group count is a floor, so **no point is recorded** and both commands print
  `near-duplicate scan truncated at 2000 candidate symbols — the group count is a floor and is not recorded.`
  A complete scan that found nothing records `0` (the absent-vs-zero rule, both directions).

The **absent-vs-zero rule** is load-bearing on both sides: a metric whose source was unavailable is an
absent row, never `0`; a count that genuinely evaluated to `0` with its source present is recorded as `0`.

## Schema

`history.db` is a workspace-local, append-only SQLite sidecar (WAL). It is **never** atomically replaced
by a rebuild (unlike `search.db`/`content.db`) — it is not derivable from the current artifact.

```sql
CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);   -- schema_version = 1

CREATE TABLE snapshots(
    snapshot_id       INTEGER PRIMARY KEY AUTOINCREMENT,        -- the trend sort axis
    recorded_at_utc   TEXT NOT NULL,                            -- writer-clock; display only
    workspace_id      TEXT NOT NULL,
    artifact_id       TEXT NOT NULL,
    revision          INTEGER NOT NULL,
    extractor_version TEXT NOT NULL,
    miller_version    TEXT NOT NULL,
    source            TEXT NOT NULL,
    UNIQUE(artifact_id, revision, source)                       -- one snapshot per (artifact, revision, source)
);

CREATE TABLE snapshot_metrics(
    snapshot_id INTEGER NOT NULL REFERENCES snapshots(snapshot_id) ON DELETE CASCADE,
    metric      TEXT NOT NULL,
    value       REAL NOT NULL,
    detail_json TEXT NULL,                                      -- optional bounded breakdown (e.g. per-marker counts)
    PRIMARY KEY(snapshot_id, metric)
);

CREATE INDEX idx_snapshot_metrics_metric ON snapshot_metrics(metric, snapshot_id);
```

A `schema_version` newer than the running binary is skip-never-destroy (append-only history has no
rebuild escape hatch). `detail_json` is intentionally **not** surfaced in the `metrics history` envelope;
it is producer-side provenance kept for future breakdown views.

## Stability Rules

- `schema_version` bumps on any breaking change to the envelope (renamed/removed field, changed ordering
  guarantee). Additive, backward-compatible fields may appear without a bump.
- Field names are stable: `workspace_id`, `metrics`, `metric`, `points`, `recorded_at_utc`, `artifact_id`,
  `revision`, `source`, `value`.
- Ordering (`snapshot_id`, newest last) is a guarantee, not an implementation detail.
- The absent-vs-zero rule is a guarantee: a metric with no data is omitted (JSON) / `-` (compact), never
  `0`.

## Capabilities

`miller capabilities --json` advertises the surface (verify with `capabilities --json`):

- `json_commands` includes `metrics history --json`.
- `json_contracts` includes `metrics_history` at schema version `1`, pointing at this doc
  (`docs/contracts/metrics-history-v1.md`).

## Boundary

Miller owns this deterministic, read-only trend over locally-recorded snapshots. It does **not** own:
cross-workspace/fleet trend aggregation, semantic ranking of trends, suppression persistence, or any
confidence/evidence view — those require semantics or fleet state and stay out of Miller. Read-time
downsampling (uniform stride to a fixed point budget) is a **dashboard** concern; the CLI returns every
recorded point in the window (no downsampling).
