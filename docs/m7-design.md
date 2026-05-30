# M7 design — `workspace` + soft budgets (dashboard deferred)

Status: **design, ready to build**. Decision-driven, grounded against the live code + pinned `julie-server`
v7.12.2. House style matches [m3](m3-design.md)/[m5](m5-design.md)/[m6](m6-design.md). Confidence ~85.

## Goal

Ship the 7th and final MVP tool — `workspace` (admin / index lifecycle) — plus the operational-hygiene polish:
**soft budgets** (warn-on-overage for latency + tokens, the telemetry feedback loop the ledger was built for).
This **completes the 7-tool surface** and makes Miller telemetry-driven and self-monitoring.

**Dashboard: DEFERRED** (user decision 2026-05-29). The plan flags it optional ("defer if it threatens focus");
it is a standalone read-only add-on requestable anytime. M7 ships without it.

## What this builds on (verified against the code)

- **Single-writer / leader topology (M3):** `IndexerService` is the leader (holds `SingleWriterLock` on
  `<.miller>/indexer.lock`, runs the watcher + `extract` writes); `FreshnessService` polls
  `canonical_revisions` every 2s and atomically swaps a rebuilt `MillerRepositoryIndex` into `IndexHolder`.
  Every instance reads; only the leader writes.
- **Telemetry ledger (M2):** `TelemetryLedger` is a write-only singleton over `<.miller>/telemetry.db` (STRICT
  `tool_telemetry` table; `Record`/`Prune` only — **no read/aggregate path exists yet**). The ONE central
  `TelemetryCallToolFilter` wraps every `tools/call` and is where duration + est_tokens are known per call.
- **WorkspaceContext:** one source of truth for `WorkspaceRoot`, `ExtractDbPath`, `TelemetryDbPath`,
  `CanonicalRoot`, `CanonicalExtractDbPath`, `WorkspaceId`.
- **Index facts:** `MillerRepositoryIndex` exposes `DocumentCount` + `KnownExtensions`; `IndexHolder` exposes
  `Current` + `BuiltRevision`; `IndexFreshProbe.Compute()` = built==latest AND queue empty.

## Architecture reality that shapes the operations (verified)

[architecture-decision](findings/architecture-decision.md) + [julie-eros-audit](findings/julie-eros-audit.md):
julie keys **workspace identity = SHA256(canonical root) → one SQLite per root**. Miller serves **exactly one
workspace per process** (CWD-rooted, stdio). A multi-workspace *registry* with a tenancy key is an **eros /
commercial-tier** concern, explicitly out of Miller's local scope. So `list/open/remove` are mapped **honestly**
to the single-process model rather than faking a registry that does not fit (decision-1) — no silent pretense.

---

## Decisions

### D1 — Surface = toolbox §7, single-workspace-honest semantics

| param | default | notes |
|---|---|---|
| `operation` | `status` | status\|refresh\|full\|list\|open\|remove |
| `path` | null | required for open/remove; ignored by status/list; optional for refresh/full |
| `format` | compact | compact\|json |

**80% call:** `workspace()` → status. Operation semantics (decision-1):

- **`status`** — the health view (D2). The default, the common call.
- **`refresh`** — reconcile NOW (don't wait for the 2s poll). Leader: delta `extract scan` + immediate
  poll+swap. Non-leader: immediate poll+swap to pick up the leader's writes (no scan — it does not hold the
  writer lock). Either way the in-memory index ends current. (D3)
- **`full`** — force a from-scratch rebuild. Leader: `extract scan --force` + immediate poll+swap. Non-leader:
  cannot force a global rescan (another instance owns the writer) → poll+swap + a clear note. (D3)
- **`list`** — Miller is single-workspace-per-process: lists the **current** workspace (root, id, db, symbol
  count, fresh, leader). Honestly labeled, not a faked multi-entry registry.
- **`open(path)`** — **prime** a workspace: run an `extract scan` at `path` so a Miller later launched there has
  a warm index. NOT a live switch (the index/watcher/telemetry are bound to CWD at bootstrap — documented).
- **`remove(path)`** — delete the `.miller` index dir at `path` (cleanup). **Guard:** refuse to remove the
  CURRENTLY-served workspace's live `.miller` (it is in use) — clear refusal, not a corrupt half-delete.

### D2 — `status` = index + freshness + telemetry tool-breakdown
Assemble (compact + json):
- **workspace:** root, workspace_id, db path, leader (this instance?).
- **index:** `DocumentCount`, language/extension count (`KnownExtensions`), built revision, latest observed
  revision, `index_fresh` (probe), watcher queue empty.
- **telemetry breakdown** (the day-1 KPI, literally julie's tool-breakdown screen): per tool — calls, avg ms,
  p95 ms, total est_tokens, error rate; plus overall calls, dropped-writes rate, and the window covered. This
  is the feedback loop the whole telemetry design exists for; surfacing it is M7's payoff.

### D3 — `refresh`/`full` route writes through the leader; never write from a non-leader
Two `miller` instances must NOT both `extract scan` (the M3 corruption guard). So:
- Add `IndexerService.TryScanAsLeader(bool force) → ScanOutcome` — under `_opsGate` (same serialization as the
  M6 write-through and the debounce drain), if this instance is the leader run the scan via its `IExtractOps`;
  else return `NotLeader`. Best-effort + typed outcome; never throws into the tool.
- `IExtractOps.Scan` gains a `bool force` (JulieExtractOps threads it to `runner.Scan(..., force)`). The M3
  `extract scan` (delta) is `force:false`; `full` is `force:true`.
- Add `FreshnessService.PollNow() → (bool swapped, long revision)` — runs the existing private poll+swap once,
  on demand (reuse `PollAndSwap` logic; expose a public trigger). `refresh`/`full` call it after the scan so the
  result is immediate, not up-to-2s-later. A non-leader `refresh`/`full` calls only `PollNow()`.

### D4 — Soft budgets = warn-only, in the ONE central filter
The plan: eros has hard gates; Miller starts **warn-only**. Implement a pure `Miller.Core` (or Server) component
`SoftBudgets` holding per-tool latency-ms + est-tokens thresholds (sensible defaults per tool; the slow/fat
tools — context/impact/edit — get higher latency budgets than search/inspect). The central
`TelemetryCallToolFilter`, which already knows the tool name + final duration + est_tokens per call, evaluates
the budget after the inner handler and logs a **WARN** (Serilog) on overage — `"tool X exceeded latency budget:
820ms > 500ms"`. No behavior change to the call (warn only, never blocks/errors). The check itself is pure +
unit-tested (given tool, duration, tokens, budgets → list of breaches); the filter just logs the result.

- **Where duration is known:** the filter currently lets the `TelemetryScope` compute duration on dispose. For
  the budget check, the filter measures its own elapsed (a local `Stopwatch.GetTimestamp` at entry) so it can
  evaluate before the scope disposes — no change to the scope's own timing. (Keep the two independent; the
  budget WARN is diagnostic, the ledger row is the record of truth.)

### D5 — Telemetry aggregation lives on the ledger (same connection, under its lock)
Add `TelemetryLedger.Summarize() → TelemetrySummary` (a GROUP BY over `tool_telemetry` on the ledger's existing
connection, under `_gate`): per-tool count/avg/max/error_count/sum_est_tokens, plus p95 (per-tool ordered
`LIMIT 1 OFFSET floor((count-1)*0.95)` — the nearest-rank method on a 0-based ordering, so a single row yields
its own value and the max is never skipped; SQLite has no PERCENTILE). Also total calls, window
(min/max ts), and `DroppedWrites`. Reusing the singleton's connection avoids a second connection + WAL-visibility
question and is correct for a rare admin call (negligible lock contention). The **rendering** of the summary into
compact/json is a pure, unit-tested formatter (given a `TelemetrySummary` → text), keeping the SQL thin.

### D6 — Pure ↔ infra seam held
- **Pure (Core or Server, unit-tested):** `SoftBudgets` evaluation; the `status`/`list` renderers (given the
  assembled facts → compact/json); `TelemetrySummary` rendering; the `remove` safety predicate (is this path the
  live workspace?).
- **Infra (thin):** `TryScanAsLeader`, `PollNow`, `Summarize` (SQLite), the actual `.miller` dir delete, the
  prime scan.
- The tool orchestrates; the renderers + budget + safety logic are pure.

### D7 — Telemetry on `workspace` itself
The central filter already records it (op = operation). The tool sets `TelemetryContext.Current` outcome/result
count like the others. `workspace` is itself in the tool-breakdown.

### D8 — Safety + honesty
- `remove` of the live workspace → refused (D1). `remove` of a non-existent `.miller` → clear not-found, not an
  error. `open`/`remove` require `path`; missing → usage message.
- A non-leader `full` → does NOT silently no-op; it reports it cannot force a global rescan here and that the
  leader's watcher keeps the index fresh (honest, per the cross-language/honesty clause).
- A scan failure (`extract` exit 1) surfaces as a clear status, never a silent success.

---

## Components

**Miller.Server/Tools**
- `WorkspaceTool.cs` — `[McpServerTool(Name="workspace")]` + pure-ish `Run(...)` orchestrator dispatching the 6
  operations; injects `IndexHolder`, `WorkspaceContext`, `IndexerService`, `FreshnessService`, `IndexFreshProbe`,
  `TelemetryLedger`, `JulieExtractRunner`/ops for prime, `ILogger`.
- `WorkspaceStatus.cs` (or in-tool) — the pure renderers (status/list/summary → compact/json).

**Miller.Server/Telemetry**
- `TelemetryLedger.Summarize()` + `TelemetrySummary`/`ToolStat` records.
- `SoftBudgets.cs` (pure evaluation) + per-tool defaults; `TelemetryCallToolFilter` evaluates + WARN-logs.

**Miller.Server/Hosting**
- `IndexerService.TryScanAsLeader(bool force)` + `ScanOutcome`.
- `FreshnessService.PollNow()`.
- `IExtractOps.Scan(bool force)` + `JulieExtractOps` threading.

**Miller.Server/Program.cs** — `WorkspaceTool` auto-discovered (WithToolsFromAssembly); register any new singleton
deps (a `JulieExtractRunner` for the prime scan if not already resolvable).

## Test strategy
- **Pure unit (default suite):** SoftBudgets breach detection (under/at/over each threshold; per-tool defaults);
  status/list/summary renderers (compact+json, empty telemetry, single tool, multi tool); remove-safety predicate
  (live vs other path); the p95/aggregate shaping given synthetic rows.
- **Contract (synth telemetry.db):** `Summarize()` aggregates a known row set correctly (counts, avg, error
  rate, p95 offset, window, dropped).
- **Server:** `WorkspaceTool.Run` dispatch — status assembles the facts; list shows current; open/remove arg
  guards + remove-live refusal + remove not-found; refresh/full non-leader path (PollNow only) via injected
  fakes (no live process).
- **Scale (excluded by default, live binary):** `workspace full` as leader force-scans + swaps on a real
  extract; `open(path)` primes a second temp repo's `.miller`; `refresh` converges after an external edit.

## Implementation order (TDD by layer)
1. Core/Server `SoftBudgets` + tests → wire into the filter (WARN). 2. `TelemetryLedger.Summarize` + summary
records + tests. 3. Pure renderers (status/list/summary) + tests. 4. `IExtractOps.Scan(force)` +
`TryScanAsLeader` + `PollNow` + tests. 5. `WorkspaceTool` dispatch + arg/safety guards + tests. 6. Register +
Scale tests.

## Verify / exit
- Build 0/0; default suite green and < 10s; Scale green on the live binary.
- `workspace()` → a readable status with the tool-breakdown. `workspace("full")` (leader) rebuilds + swaps.
  `workspace("open", path=...)` primes a repo. `workspace("remove", path=<live>)` is refused.
- A deliberately slow/fat call emits a budget WARN to the log; no behavior change.
- **Exit:** the **7-tool surface is complete** (search, inspect, context, impact, edit, trace*, workspace —
  *trace is M4, still blocked on julie); Miller is telemetry-driven + self-monitoring.

## Explicitly NOT in M7
- The Kestrel dashboard (deferred, user decision — standalone add-on later).
- Hard budget gates (eros-tier; Miller stays warn-only).
- A multi-workspace registry / tenancy (eros/commercial-tier — decision-1).
- M4's `trace` + resolver (blocked on julie enrichment).
