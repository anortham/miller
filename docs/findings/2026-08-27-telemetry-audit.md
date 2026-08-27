# Telemetry audit — what the field data says to fix next

Date: 2026-08-27. Source: the full local telemetry store on the primary dev machine
(`miller telemetry export --jsonl --workspace-id all`, 36,130 rows, 2026-08-02 → 2026-08-27),
aggregated with duckdb. Workspaces observed: miller, julie-extractors, Tycho, gesso, claude, julie.
Clients observed include Claude Code and `grok-shell-miller` (a Grok harness that issues concurrent
tool calls). All rates below are for the last 7 days (2026-08-20 →, 20,376 calls) unless marked
"all-time".

## Priority 1 — live bug: `edit replace_text` fails 53% of calls, and the crash is unrecoverable after the fact

The numbers:

- 161 `replace_text` calls in the last 7 days; 85 errors (53%). 102 edit-tool errors total in the
  window counting `replace_symbol_body` and the insert operations.
- Every one of the 102 carries `edit_failure_reason=unhandled_InvalidOperationException` — the
  backstop bucket, meaning the exception ESCAPED the edit pipeline rather than exiting through a
  classified failure path.
- The failures span every server version from 1.20.1 through the current 1.24.0 and four different
  workspaces (miller 8/22–8/25, julie-extractors 8/20, Tycho 8/26, gesso 8/26–27). This is not one
  bad build or one bad repo.
- Failure shape: fast (4–24 ms, one 162 ms), both `apply=false` previews and `apply=true` commits,
  `match_mode=auto`, no `query`/`anchor`/`line` selectors. The Tycho failures happened against a
  stale index (`allow_stale=true` also failed); the gesso failures happened against a fresh one.

The diagnosability gap, which is why the root cause is still unknown:

- The `EditTool.Edit` catch backstop (`src/Miller.Server/Tools/EditTool.cs:166`) stamps telemetry
  with the exception TYPE only (`unhandled_` + type name — the privacy rule says never the message)
  and does NOT log the exception. The shared workspace log records only
  `tool edit completed with error in Nms` (verified in gesso and Tycho logs at the failure
  timestamps). The rendered message reached the calling agent once and is gone.
- `EditService` contains no explicit `throw new InvalidOperationException`, so the throw is
  BCL-raised: LINQ `First()`/`Single()` on an empty sequence, `Nullable.Value` on null (candidates
  at `EditService.cs:2002–2004`, `reference.StartByte!.Value`), or similar.

Fix path, in order:

1. Log the full exception with stack to the shared workspace log (`role:leader`, WRN) in the edit
   backstop. Local logs already carry full stacks for indexer exceptions
   (`IndexerCore: extract op scan failed…` prints the whole trace), so this adds no new exposure —
   telemetry keeps its type-only rule.
2. Consider a bounded, path-free stack SIGNATURE (topmost Miller frame: type + method) in telemetry
   metadata so recurrence is groupable without the message.
3. Reproduce or wait for the next occurrence with the log in place, then fix the actual bug.

## Priority 2 — the `context` tool is slow for everyone: p50 ~7 s, p95 20–37 s

Per-phase telemetry names the cost: `read_lookup_count` is 1,300–2,000 per call, and the lookup
backend decides everything.

| `read_lookup_backend` (context calls, last 7 days) | calls | total p50 | total p95 | lookup-time p50 |
|---|---:|---:|---:|---:|
| `search_sidecar` | 235 | 7.7 s | 18.5 s | 3.5 s |
| (null — older rows) | 15 | 14.2 s | 33.9 s | 10.9 s |
| `lagging_sidecar` | 12 | 26.7 s | 36.7 s | 24.0 s |
| `session_projection` | 8 | 8.0 s | 21.0 s | **0.054 s** |

The same ~1,400–2,000 lookups cost 3.5 s through the sidecar and 54 ms through the session
projection. The fast path exists and served 8 of ~270 calls. Worst observed all-time:
`read_lookup_ms` 32–35 s on `lagging_sidecar` rows.

**Superseded by the measured cost model in
[`../plans/2026-08-27-context-latency-diagnosis.md`](../plans/2026-08-27-context-latency-diagnosis.md):**
the dominant cold cost is the whole-generation `RevisionFactCache` load (~4.8 s here) that
term-rescue test-subject promotion pulls into `anchor_resolution` on every query call — even under
the default `reference_mode=off` — plus a ~1.3 s graph_reach rebuild after every revision advance.
The per-lookup sidecar cost is real but secondary (0.32 ms/lookup warm).

## Priority 3 — a file deleted mid-scan fails the whole store delta

Tycho, 2026-08-26, three occurrences during branch-switch churn (watcher buffer overflow forcing
rescans): the incremental delta died with
`StoreWorkspaceOperationException: source file could not be read: No such file or directory (os error 2)`
at `StoreWorkspaceCoordinator.RequireCommitted` (`StoreWorkspaceCoordinator.cs:846`), and each
failure started the scan-failure backoff. The index recovered on the next scan ~40 s later, but a
delta that dies because one file vanished between enumeration and read turns routine churn into
failed scans plus deferred retries. Direction: treat a vanished source file as a deletion for that
delta instead of failing the scan. The read happens inside julie-extract's import, so the fix may
belong there; both repos have the same owner.

## Lesser signals (not scheduled, recorded so they are not lost)

- `search file` returned empty 52% of the time (267 calls); `search source` 32% (3,849);
  `impact changed_paths` 36%; `impact target` 30%. Some of this is normal agent probing; the
  file-mode rate is high enough to warrant a later look at its matching or its cross-tool handoff.
- `content import` errors 16% (all `missing_file` — agents passing bad paths). Minor.
- `workspace open` averaged 58 s all-time (cold bootstrap of a new workspace; expected, not a bug).
- The 1,250+ all-time `search symbol` internal failures were the 1.18–1.19.x era (8/14–16 spike,
  745 on 8/16 alone) and do not occur on 1.22+; already fixed. The only errors recorded on 1.24.0
  are the Priority-1 edit failures.

## Reproduce the aggregation

```bash
miller telemetry export --jsonl --workspace-id all > /tmp/telemetry.jsonl
duckdb -c "
CREATE TABLE t AS SELECT * FROM read_json_auto('/tmp/telemetry.jsonl');
SELECT tool, op, count(*) n,
  round(100.0*sum(CASE WHEN outcome='empty' THEN 1 ELSE 0 END)/count(*)) pct_empty,
  round(100.0*sum(CASE WHEN outcome='error' THEN 1 ELSE 0 END)/count(*)) pct_err,
  round(median(duration_ms)) p50_ms, round(quantile_cont(duration_ms,0.95)) p95_ms
FROM t WHERE ts >= '2026-08-20' GROUP BY 1,2 HAVING n>=15 ORDER BY n DESC;"
```

Error categories live in `metadata_json` (`error_category`, `edit_failure_reason`,
`read_lookup_backend`, `read_lookup_count`, `read_lookup_ms`).
