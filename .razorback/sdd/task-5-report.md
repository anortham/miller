# Task 5 report — Telemetry query efficiency + display-id fix

**Status: DONE**

## Summary of changes
- `src/Miller.Dashboard/DashboardData.cs`
  - Removed the N+1 P95 read: deleted `ComputeP95` (per-tool `ORDER BY duration_ms LIMIT 1 OFFSET n`
    query, one per tool row) and added `ComputeP95ByTool`, which does ONE grouped pass over the window's
    rows and computes every tool's p95 in C#. `ReadToolStats` now calls it once and looks p95 up by tool.
  - `ReadRecentErrors` now resolves each errored row's registered display id and populates
    `WorkspaceDisplayId` (was hardcoded `null`). Added private helper `ResolveWorkspaceDisplayIds`.
- `tests/Miller.Tests/Server/TelemetrySummaryTests.cs`
  - Added a new class `DashboardTelemetrySummaryTests` (10 P95 pins + 3 display-id tests). The existing
    `TelemetrySummaryTests` class (server `TelemetryLedger.Summarize`) is untouched.

## Pinned P95 semantics (one sentence)
P95 = the ascending-sorted `duration_ms` value at 0-based index `floor((count-1)*0.95)` per tool over the
window (NULL durations sort first; a NULL/absent value at that index degrades to `0`; `long` result) — the
exact behavior of the old per-tool `ORDER BY duration_ms ASC LIMIT 1 OFFSET floor((count-1)*0.95)`.

## Before/after query pattern (N+1 → bounded)
- Before: 1 grouped stats query + **N** per-tool P95 queries (one `ORDER BY duration_ms LIMIT 1 OFFSET n`
  per tool row) → `O(tools × rows·log rows)`.
- After: 1 grouped stats query + **1** duration-scan query
  (`SELECT tool, duration_ms ... ORDER BY tool, duration_ms ASC`), accumulate per-tool ordered lists, pick
  the offset value in C#. Total telemetry queries for the summary are now **bounded (2)**, independent of
  tool count. The scan is index-friendly (`idx_tool_telemetry_tool_duration` /
  `idx_tool_telemetry_ws_tool_duration` already exist on `(…, tool, duration_ms)`).

### Why byte-identical
- Per-tool list length == `COUNT(*)` for that tool (same WHERE), so the offset index matches.
- `ORDER BY tool, duration_ms ASC` yields, within each tool, NULL-first + ascending — identical to the old
  single-tool `ORDER BY duration_ms ASC`.
- Ordering is value-based, so ties need no secondary key: the duration at a given offset is deterministic
  across equal-duration runs.
- NULL-at-offset → 0 preserved (`value ?? 0`); `duration_ms` is `NOT NULL CHECK(>=0)` in schema so this is
  defensive-but-faithful. A real `0ms` row is preserved (pinned by `P95_ZeroDuration_...`).

## Display-id resolution (B5)
- `ReadRecentErrors` reads the registry once (not per row) and maps `workspace_id → display_id`, then sets
  `WorkspaceDisplayId` per error. **Registered → display id; unregistered or NULL workspace_id → null**
  (per acceptance criterion #3). This is a deliberate, documented difference from the activity feed, which
  falls back to the raw id for unregistered ids; acceptance #3 overrides "same as the activity feed".

## Judgment calls (file:line)
1. **Registry path is derived, not threaded** — `DashboardData.cs` `ResolveWorkspaceDisplayIds`.
   `ReadTelemetrySummary(telemetryDbPath, workspaceId)` carries **no** registry path, and its callers
   include `Endpoints/DashboardEndpoints.cs` (the machine-wide `"all"` case at :27) — both **out of my
   ownership** (endpoints are explicitly off-limits; `ReadTelemetrySummary` is a Task-1 spine reader I must
   not touch). Threading a `registryDbPath` param would ripple into those files. To make the fix REAL within
   my region for the default layout, I derive the registry as the telemetry DB's **sibling**
   `workspaces.db` via `connection.DataSource` — exactly the co-location `DashboardPaths` produces (both
   default under `~/.miller`) and the same sibling-dir pattern `BuildRuntimeInfo` already uses. It degrades
   gracefully: split `MILLER_REGISTRY_DB`/`MILLER_TELEMETRY_DB` overrides, or a missing/corrupt registry,
   yield an empty map → null display ids (== today's behavior, no regression). **If the lead prefers the
   explicit threaded approach, it requires editing `DashboardEndpoints.cs` + `ReadTelemetrySummary` (both
   lead-owned).**
2. **Tests added as a second class in the assigned file** — `TelemetrySummaryTests.cs`. The pre-existing
   class tests the *server* `TelemetryLedger`, not the dashboard path; my class is
   `DashboardTelemetrySummaryTests`. The gate filter `FullyQualifiedName~TelemetrySummaryTests` matches it
   by substring, so both classes run.
3. **Query-count assertion omitted** — observing the SQL command count cheaply would require injecting a
   counting `SqliteConnection` wrapper into production (connections are created internally with
   `Pooling=false`); that is disproportionate. I rely on the semantics pins + the structural fact that the
   per-tool loop is gone (one `ComputeP95ByTool` call, one query inside it). Stated per the brief's escape
   clause.
4. **Correlated subqueries left intact** — the grouped stats query keeps its `last_outcome`/`last_error_kind`
   correlated subqueries (audit B1 also flagged these). Rewriting them risks changing those exact values and
   is beyond "no per-tool query loop"; out of scope for a byte-identical P95 rewrite.

## TelemetryPanel note
`Components/TelemetryPanel.razor` does **not** currently render `WorkspaceDisplayId` for recent errors (only
`ActivityFeedPanel` renders it, for activity entries). So the observable surface for this fix today is the
JSON contract (`workspace_display_id` on `recent_errors`) and any future/other razor consumer — I did not
touch razor. (Task 6 "id chips" may surface it in the UI.)

## Miller calls used + confirmations
- `inspect src/Miller.Dashboard/DashboardData.cs` — symbol map (index still on pre-Batch-A layout; used only
  as a map, then Read the real worktree regions).
- `trace ComputeP95 mode=refs scope=…/DashboardData.cs` — confirmed the dashboard `ComputeP95` had exactly
  **one** caller (`ReadToolStats`); the other `ComputeP95` lives in `Server/Telemetry/TelemetryLedger.cs`
  (separate, untouched). Safe to fold in and delete.

## API-shape evidence
- `DashboardToolStat` (record, :33): `(Tool, Calls, AvgMs, P95Ms, MaxMs, ErrorCount, SumEstTokens,
  LastCallTs?, LastOutcome?, LastErrorTs?, LastErrorKind?)`.
- `DashboardRecentError` (record, :46): `(Ts, Tool, Op?, ErrorKind?, DurationMs, Id?=null, WorkspaceId?=null,
  WorkspaceDisplayId?=null, ErrorMessage?=null, ErrorDetail?=null)` — `WorkspaceDisplayId` already existed
  (JSON `workspace_display_id`); populating it is additive, not a contract change.
- Registry row `DashboardWorkspaceRow` (:14): `WorkspaceId` + `DisplayId` (both non-null `string` from
  `TryReadWorkspaces` `SELECT workspace_id, display_id, …`), the join source for display ids. Resolution
  reuses `ReadWorkspaces` (graceful empty-on-missing/corrupt).

## Gate invariants + results
- **worker-red-green** — `dotnet test … --filter "Category!=Scale&FullyQualifiedName~TelemetrySummaryTests"`
  → **Passed 22/22**. Proves: P95 semantics identical pre/post rewrite (10 pins written first, green against
  OLD code; still green after), recent errors carry workspace display ids (2 tests red against OLD, green
  after), empty-window and unregistered-id/no-registry edges hold.
- **worker-ceiling** — `scripts/test.sh` (fast suite) → **Passed 3108/3108**, 20s wall (ceiling 30s). Proves
  no regression; Task 1's `DashboardRegistryReadTests` (which exercise this file, incl. the dashboard
  `p95_ms=25` JSON pin and the WorkspaceShell render) stay green.
- **build** — `dotnet build Miller.slnx -c Release` → **0 warnings / 0 errors**.

TDD order followed: (1) 10 P95 pins green on current code; (2) 2 display-id tests red; (3) rewrite
single-pass + populate display id; (4) all green.

## Fix round (inline review) — explicit path threading

Lead review approved the P95 single-pass as-is and flagged the sibling-derivation as hidden coupling
(a `MILLER_TELEMETRY_DB` override splits the pair and silently nulls display ids — relevant to the Task 7
corruption drill). Switched to explicit path threading, ownership extended to the wrapper + endpoints:

- `DashboardData.cs`
  - `ReadTelemetrySummary` gained an optional trailing `string? registryDbPath = null`, threaded into
    `ReadRecentErrors(connection, scope, allWorkspaces, registryDbPath)`.
  - `ReadRecentErrors` now takes `string? registryDbPath`; builds the `workspace_id → display_id` map via
    `ReadWorkspaces(registryDbPath)` when the path is non-blank (still degrades safely to null on
    missing/corrupt/absent registry). **Deleted `ResolveWorkspaceDisplayIds` (the `connection.DataSource`
    sibling-guess) entirely** — no more path derivation.
  - `ReadSnapshot` passes its `registryDbPath` to `ReadTelemetrySummary`.
  - `RenderTelemetryJson` gained the same optional `registryDbPath` and threads it (so `/telemetry.json`
    carries display ids too — parity with `/snapshot.json` via `ReadSnapshot`).
- `Endpoints/DashboardEndpoints.cs` — pass `paths.RegistryDbPath` at all three telemetry call sites:
  `/` (`"all"`, :27), `/fragments/telemetry` (:99), `/telemetry.json` (:184).
- `tests/…/TelemetrySummaryTests.cs` — display-id tests now pass the explicit `_registryDb`; replaced the
  old "no registry file" test with two sharper ones: `NullRegistryDbPath_DisplayIdStaysNull_EvenWhenRegistryExists`
  (proves null path ⟹ null even though a resolvable registry sits beside the telemetry DB — no sibling
  guessing) and `MissingRegistryFile_DisplayIdStaysNull` (safe degradation).

All signatures are additive (optional trailing params). Build 0W/0E maintained.

### Fix-round gate results
- **worker-red-green** — `--filter "Category!=Scale&FullyQualifiedName~TelemetrySummaryTests"` →
  **Passed 23/23** (10 P95 pins still identical; display-id resolution + null/missing-registry edges hold).
- **worker-ceiling** — `scripts/test.sh` → **all 3109 tests Passed** (test-run duration reported 42s).
  Earlier in this session the identical suite ran 15–20s; the re-run tripped the 30s wall-clock tripwire at
  ~47s purely due to machine contention (load avg 7.75, 19 concurrent dotnet/testhost processes from the
  other parallel agents), NOT a slow test I introduced — the whole `DashboardTelemetrySummaryTests` class
  (13 tests) runs in **376ms** in isolation, and the `TelemetrySummaryTests` filter (23 tests) in ~370ms.
  No slow test leaked into the fast suite; re-run when the machine is quiet to see wall time back under 30s.
- **build** — `dotnet build Miller.slnx -c Release` → **0 warnings / 0 errors**.

## Worktree state
- Path: `/Users/murphy/source/miller/.worktrees/dashboard-polish`
- Branch: `feat/dashboard-polish`
- HEAD: `4c28d90 feat(dashboard): workspace list auto-refresh, client sort, registry error notice`
- Dirty (uncommitted, per parallel-lead-commit mode — I did NOT stage/commit):
  - `M src/Miller.Dashboard/DashboardData.cs`
  - `M src/Miller.Dashboard/Endpoints/DashboardEndpoints.cs`
  - `M tests/Miller.Tests/Server/TelemetrySummaryTests.cs`
- No other source/test files touched.
