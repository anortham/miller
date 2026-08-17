# Miller v1.19.4 Windows dogfood

- **Date:** 2026-08-17
- **Host:** native Windows, Grok Build TUI, Miller MCP plugin
- **Binary:** `1.19.4+10db3160e82b` from `~\.miller\plugin-cache\1.19.4\x86_64-pc-windows-msvc\package\miller.exe`
- **Producer:** pinned `julie-extract 2.33.5`
- **Workspace:** `C:\source\miller` (`miller-6662d0bd90fe`)
- **Semantic:** Vulkan owner, accelerator lease held, model `bge-small-en-v1.5-f32`

This is the evidence record for
[`plans/2026-08-17-windows-dogfood-read-availability-plan.md`](../plans/2026-08-17-windows-dogfood-read-availability-plan.md).
It does not change product behavior.

## Session shape

The published Windows plugin launched as indexer leader (pid 14212). The store view started
`unbound` at symbols level with a full-level upgrade owed. Search and inspect failed until sidecars
caught the current store log sequence. A later refresh plus drain-rescan made them current, then
stale again, then current. Steady state arrived about six minutes after process start.

## Failures

### 1. Startup incremental scan died on a 4000 ms coordinator quantum

Log at `2026-08-17T12:07:14Z`:

```
StoreWorkspaceOperationException: coordinator quantum took 4359 ms; maximum is 4000 ms
Startup delta scan failed; keeping the loaded index until a later scan converges.
indexer_phase_record import 111108.2344 failed
indexer_phase_record coordinator_total 111108.4781 failed
```

The miss is 359 ms. Miller wrote `scan-failure.json` as `IncrementalReconcile` and deferred the
extractor-upgrade rescan behind that backoff. Search and inspect then threw:

`Search sidecar for view '…' is missing or stale.`

### 2. Store resolve and sidecar converge blocked reads

First `workspace refresh` tool call: 121724 ms. Phases:

| Phase | ms | Notes |
| --- | ---: | --- |
| import | 23348 | completed |
| resolve | 96915 | completed |
| coordinator_total | 121570 | completed |
| sidecar_total | 56701 | after the tool returned "queued" |

During that window, status showed `resolution=unbound` then `converging`, `search_db` stale, and
`content_db` either stale or `SQLite Error 5: 'database is locked'`. MCP `search` and `inspect`
failed with `diagnostic_code=internal_failure`. CLI `search` failed the same way in 157 ms.

### 3. Bind phase runs on every 250 ms leader tick

`IndexerService.DebounceInterval` is 250 ms. After each `RunDrainTick`, the leadership loop
calls `EnsureBindingPointer` (`IndexerService.cs:530-532`) even when the pointer already
matches. That reads the store pointer file, then `LoggingIndexerPhaseSink` writes INF
`indexer_phase_record bind … completed null false` to both daily logs.

Follow-up sample on the same pid 14212 session (~07:35):

- 5735 bind lines, about 3.8 per second
- Last 200 log lines: 190 bind / 10 other
- Idle CPU over 5 s: ~0 extra CPU seconds
- Log + JSONL grew ~6 KB in 5 s
- `coord.db-wal` and sidecar writes at 07:33 were separate: incremental resolve after plan-doc saves, not the heartbeat

This is the designed watcher debounce loop, not a tight spin. The defect is per-tick file I/O
and Information logging on a no-work bind. Session-start bind at `:433` is the one that should
remain.

### 4. Live log import is locked on Windows

`content import` of `.miller/logs/miller-20260817.log` failed:

`The process cannot access the file ... because it is being used by another process.`

`ContentCorpusExternalStore.OpenRead` opens with `FileShare.Read`. Serilog holds the live log with
a write share. A copied file imported and searched.

### 5. `references candidates` returned empty after 278 s

CLI:

| Command | ms | Result |
| --- | ---: | --- |
| `metrics complexity --min-severity high --exclude-tests --limit 5` | 5404 | 5 rows |
| `metrics history --limit 3` | 29 | 3 snapshots, `marker_total=0` |
| `references candidates --limit 5` | 278043 | blank body |
| `context "how does store coordinator fail a Windows incremental scan"` | 9501 | sufficient bundle |

### 6. Edit no-op presented as an MCP error

Same-text `replace_text` preview matched exactly (`exact ×1 @ L81`, disk verified) and returned
`No change — the edit is a no-op.` The MCP channel classified it as
`diagnostic_code=unknown` / `diagnostic_class=internal_failure`. `EditService.Preview` already
sets `Outcome: "empty"`. A real one-line preview then succeeded and was not applied.

### 7. Eligibility text disagrees with the extractor version field

After settle, `workspace status --json` reported:

- `own_extractor_version`: `2.33.5`
- `artifact_extractor_version`: `2.33.5`
- `own_eligibility.reason`: `extractor 2.33.5 is newer than the index artifact 2.33.2`

`LeadershipEligibility.Evaluate` compared a stale `binary_version` (`2.33.2`) while status showed
the current extractor field. Startup also logged "Extractor upgrade detected" and then deferred it.

### 8. Scan governor refused this process's own on-demand refresh

```
Refused machine-wide scan admission for leader-ondemand after 5s
The recorded scan-governor owner is miller pid 14212 ... reason leader-drain-rescan
```

The same pid held `leader-drain-rescan` and then waited 5 s for `leader-ondemand`.

### 9. `julie-extractors` has been stuck since 2026-08-14

`workspace list` showed `state: error` / `locking protocol`. Status with `ensure_fresh=false`:

- `freshness: scan_failing`
- `scan_failure: IncrementalReconcile x1` at `2026-08-14T00:04:05Z`
- `next_attempt_at` already in the past
- `resolution=unbound`, search/content stale, vectors unavailable
- `index_level: symbols` with a full-level upgrade owed

### 10. Registry pollution

`workspace list` returned 30 rows, 27 with missing Temp scan-governor / CLI e2e roots.
`workspace prune dry_run=true` would remove 27 and keep 3 (`miller`, `goldfish`, `julie-extractors`).

## What worked after settle

| Surface | Result | Timing |
| --- | --- | --- |
| search symbol (CLI) | OK | 1343 ms |
| search source (CLI) | OK | 3571 ms |
| search lexical NL (CLI) | OK | 2258 ms |
| search semantic (CLI) | OK, cosine 0.73 on `Converge` | 666 ms |
| inspect overview (CLI) | OK | 426 ms |
| impact symbol (CLI) | OK, truncated | 7234 ms |
| trace refs (CLI) | OK | 1897 ms |
| context (CLI, after candidates) | OK | 9501 ms |
| patterns list/search | OK even while search was stale | 398 ms list during churn |
| goldfish cross-workspace search | OK, `freshness: unchanged` | not separately timed |
| metrics history | OK | 29 ms |
| edit preview with a real change | OK | not applied |
| vectors | ready after converge | serving tag present |

`search mode=markers` / `miller todos` returned no markers. Metric history `marker_total=0` and a
source search for `// TODO` also returned none, so that empty result is correct for this tree.

## Product rule taken from this session

Reference / store-view resolution may run in the background. It must not make `search` or a named
`inspect` throw. Status and health may say `resolving` or `sidecar_stale`. The last current sidecar
and the last readable generation must still serve.

Resolution wall time (97 s resolve, 121 s coordinator, 56 s sidecar) is a separate investigation.
This finding does not authorize raising the 4000 ms producer quantum as the primary Miller fix.
The August 13 recovery plan already rejected "raise the timeout" as a shortcut.

## Out of scope for the dogfood list

- Launching the dashboard UI
- Applying any `edit`
- Running `workspace prune` (dry-run only)
- Running `workspace full` after the drain-rescan had already moved the view to `full` / `exact`
