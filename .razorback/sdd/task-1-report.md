# Task 1 report — Page-spine resilience + endpoint parity

## Status: DONE

## Worktree state
- Path: `/Users/murphy/source/miller/.worktrees/dashboard-polish`
- Branch: `feat/dashboard-polish`
- HEAD at report time: `187ce3d` (base advanced from 6207978 while I worked — lead committed a parallel task; my edits sit on top cleanly)
- Dirty state: only my three owned files modified (no staging/commit per parallel-lead-commit mode):
  - `M src/Miller.Dashboard/DashboardData.cs`
  - `M src/Miller.Dashboard/Endpoints/DashboardEndpoints.cs`
  - `M tests/Miller.Tests/Server/DashboardRegistryReadTests.cs`

## What I built

### DashboardData.cs — spine reader resilience
1. **`DashboardWorkspaceIndex`** gained one additive nullable field: `[JsonPropertyName("error")] string? Error = null` (last positional param, default null — no existing call site breaks; Task 4 renders it).
2. **`ReadWorkspaces` split** into public non-throwing `ReadWorkspaces` (returns `.Rows`) + private `TryReadWorkspaces` returning `(IReadOnlyList<DashboardWorkspaceRow> Rows, string? Error)`. The registry open + query are now inside a precise catch filter; a corrupt/truncated `workspaces.db` degrades to an empty list. `ReadIndex` calls `TryReadWorkspaces` and carries the message into `DashboardWorkspaceIndex.Error`. Because `ReadWorkspaces` is now non-throwing, every downstream spine caller (`ReadSnapshot`, `ReadRecentActivity` display-id map, `RenderWorkspacesJson`, `ReadWorkspaceFacts`) stops throwing on a corrupt registry for free.
3. **`ReadTelemetrySummary`** — connection open + queries wrapped in the precise filter; added private `EmptyTelemetrySummary(workspaceId)` helper (dedupes the three empty-shape sites) returned on degrade.
4. **`ReadRecentActivity`** — body extracted to private `ReadRecentActivityCore(...)`; the public method wraps the call in the precise filter and returns an empty `DashboardActivityFeed` on degrade.
5. **`ReadContextSavings`** — the A1 bug fix: `OpenReadOnly` + `TableExists` moved INSIDE the `try` (they previously sat outside it), and the catch broadened from `SqliteException`-only to the precise filter.
6. **`SelectTelemetryWorkspace`** — also opens `telemetry.db` and sits on the `ReadSnapshot` path, so it was wrapped too; on a corrupt telemetry DB it returns null and `ReadSnapshot` falls back to registry-order `workspaces[0]`. (Not named in the brief's four readers, but required so `/workspace` does not 500 on corrupt telemetry — the selection runs before `ReadTelemetrySummary`.)
7. **`RenderSnapshotJson`** — added optional `string? preferredWorkspaceRoot = null` and forwarded it to `ReadSnapshot`. Additive overload; the existing 3-arg test still compiles.

All new catches use exactly `ex is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException` (mirrors the panel readers; no blanket `Exception`).

### DashboardEndpoints.cs — endpoint parity
- `/snapshot.json` now passes `launchDirectory` as `preferredWorkspaceRoot` (A5) → selects the same default workspace as `/workspace`.
- `/workspaces/{workspace_id}/refresh` now calls `TryRefreshWorkspace` instead of the throwing `RefreshWorkspace` (A4) → an unregistered id renders a Failed result body, not a 500. Matches the htmx `/fragments/refresh` route.

## Judgment calls
- **`SelectTelemetryWorkspace` wrapped (DashboardData.cs, `SelectTelemetryWorkspace`):** Not in the brief's explicit four-reader list, but it opens `telemetry.db` on the `ReadSnapshot` critical path before `ReadTelemetrySummary` runs. Leaving it unguarded would still 500 `/workspace` on a corrupt telemetry DB, defeating the task intent. It is inside my owned file and its degrade (return null → fall back to `workspaces[0]`) is the existing no-telemetry behavior.
- **`EmptyTelemetrySummary` + `ReadRecentActivityCore` helpers:** extracted only to keep the degrade shape single-sourced and the try/catch shallow. Pure refactor, same public surface.
- **JSON refresh property casing:** `WorkspaceRefreshResult` carries no `[JsonPropertyName]` and `DashboardJsonContext` sets no naming policy, so it serializes PascalCase (`WorkspaceId`, `StatusText`). Test asserts against that real shape (`StatusText == "failed"`) rather than an invented snake_case contract — I did not add attributes (out of scope + would be a contract change).
- **Endpoint A4 guard is a source-scan** (`JsonRefreshEndpoint_UsesNonThrowingRefreshPath`) mirroring the existing `DashboardHost_PreservesFragmentCompatibilityRoutes` pattern, because spinning a full Razor host for one route is disproportionate; the behavioral proof that the non-throwing path yields a Failed body is `TryRefreshWorkspace_UnregisteredIdReturnsFailedJsonNotThrow`.

## Miller calls used
- `workspace list filter=miller` — confirmed the worktree isn't indexed (rev 1) but the main checkout `miller-b275269b2d7c` reflects the same base; used it as `workspace_id` for all reads.
- `inspect src/Miller.Dashboard/DashboardData.cs` — full symbol map (line numbers for all spine readers/records) before reading regions.
- `trace RenderSnapshotJson mode=refs` — confirmed exactly 2 callers (`DashboardEndpoints.cs:188` + the contract test), so an additive optional param is safe.
- `inspect WorkspaceRefreshResult depth=overview` — confirmed record shape (`Status`, `WorkspaceId`, `Error`, `StatusText`) and that `TryRefreshWorkspace` already returns Failed on any exception.
- `inspect CrossWorkspaceRefreshService.Refresh depth=full` — confirmed an unregistered id fails at `GetRequiredRow` BEFORE any `_scan` spawn, so my refresh test does not spawn julie-extract and is correctly non-Scale.

## API-shape evidence
- `DashboardContextSavingsSummary.NotTracked(workspaceId)` (DashboardData.cs:339) — reused as the context-savings degrade shape.
- `DashboardActivityFeed(string? WorkspaceId, IReadOnlyList<DashboardActivityEntry> Entries)` (:73) — empty feed shape.
- `DashboardTelemetrySummary(workspaceId, tools, totalCalls, windowStart, windowEnd, recentErrors)` (:25) — empty summary shape.
- `ReadSnapshot(registryDbPath, telemetryDbPath, workspaceId, preferredWorkspaceRoot = null)` (:823) already had the 4th param — `RenderSnapshotJson` simply forwards it.
- `WorkspaceRegistry.UpsertSeen(...)` / `WorkspaceRegistryState` / `TelemetryLedger` / `InsertTelemetryRow` helper — reused from existing tests verbatim.

## Gates
- **worker-red-green:** `dotnet test … --filter "Category!=Scale&FullyQualifiedName~DashboardRegistryReadTests"` → **Passed 42 / Failed 0** (33 pre-existing + 9 new). Proves: corrupt `workspaces.db` → `ReadIndex` empty + `Error` set and `ReadSnapshot` no-throw; corrupt `telemetry.db` → telemetry summary + activity feed + context savings degrade with no throw; `/snapshot.json` (`RenderSnapshotJson` + preferred root) selects the same workspace as `/workspace`; unregistered refresh id → Failed JSON body; JSON refresh route rides `TryRefreshWorkspace`.
- **worker-ceiling:** `scripts/test.sh` → **Passed 3091 / Failed 0** (wall 19s). Proves no regression across the fast suite.
- **build guard:** `dotnet build Miller.slnx -c Release` → **0 Warning(s) / 0 Error(s)** (TreatWarningsAsErrors holds).

## New tests added
`ReadIndex_CorruptRegistryDbReturnsEmptyIndexWithError`, `ReadWorkspaces_CorruptRegistryDbDegradesToEmptyNotCrash`, `ReadSnapshot_CorruptRegistryDbReturnsSnapshotNotCrash`, `ReadTelemetrySummary_CorruptTelemetryDbDegradesToEmpty`, `ReadRecentActivity_CorruptTelemetryDbDegradesToEmpty`, `ReadSnapshot_CorruptTelemetryDbDegradesTelemetryPanelsNotCrash`, `TryRefreshWorkspace_UnregisteredIdReturnsFailedJsonNotThrow`, `JsonRefreshEndpoint_UsesNonThrowingRefreshPath`, `RenderSnapshotJson_PreferredRootMatchesWorkspacePageSelection`. All use per-test temp dirs (garbage-byte `.db` fixtures, mirroring the existing unreadable-workspace test); `OpenReadOnly` sets `Pooling=false` so no `ClearAllPools` is needed.

## Remaining / notes
- `WorkspaceIndex.razor` rendering of the new `Error` field is Task 4 (not touched). The field only needed to serialize + be asserted here.
- No JSON property renamed or removed; the only new field is `error` (snake_case) on `DashboardWorkspaceIndex`.
