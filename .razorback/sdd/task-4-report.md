# Task 4 report — Converge follow-ups (P5 Canary Stage)

Status: **DONE** (with one documented plan-nuance on fix (d); see Judgment calls).
Commit SHA: **none — parallel-lead-commit** (owned files edited; not staged/committed).
Branch/worktree: `worktree-semantic-p5` at base HEAD `fffe9d8`, path
`/Users/murphy/source/miller/.claude/worktrees/worktree-semantic-p5`.

## What I implemented

All four fixes from the P4 shadow dogfood (`docs/findings/2026-07-20-p4-shadow-dogfood.md`), red-first,
extending the existing service/planner/render in place — no new services, no new hosted registrations.

### (a) Held-cursor retry wake — finding 1 (chunk-cursor starvation, medium, "fix before P5")
`VectorConvergeService`: the `ExecuteAsync` wake loop now, after a drain that ends with any held cursor,
schedules **exactly one** delayed re-stamp of `VectorConvergeSignal` so a quiet workspace re-drains instead
of starving. Mechanics:
- `DrainOnceAsync` now returns `bool` (any cursor outcome with a non-null `LastError` ⟹ held).
- `ScheduleHeldRetry` re-stamps the current `_signal.TargetRevision` after `_heldRetryDelay` (default
  **5 min**), via injectable seams `heldRetryDelay` + `delay` (`Func<TimeSpan,CancellationToken,Task>`,
  defaults to `Task.Delay`).
- At most one pending retry (`Interlocked.CompareExchange` guard on `_pendingRetry`); a real wake calls
  `CancelPendingRetry()` first, so the real converge absorbs/cancels the pending retry (no double-drain, no
  storm). The re-stamp coalesces through the capacity-1 semaphore.
- Cleaned up on stop (`finally { CancelPendingRetry(); … }`).

### (b) Incremental disk gate — mirrors the shadow path
`DrainCursorAsync`: before an incremental `EmbedAsync`/`Commit` with growth (`plan.ReEmbed.Count > 0`), it
consults `state.DiskGate`. Blocked ⟹ `RefuseIncrementalForDisk` marks disk-blocked, records the cursor error,
holds the cursor (no partial `vectors.db` write, no hard fail). `ResolvePause` then stamps `disk-blocked`
identically to the shadow path; unblocking on the next wake resumes and clears the pause. `DiskBlockedReason`
was refactored to take an `action` phrase — the shadow output stays **byte-identical** (`ShadowBuildAction`
constant), the incremental path reads "converge the vector cursor". Deletes shrink the artifact, so only a
growing re-embed is gated.

### (c) Deferred-source INFO log — finding 2 (low)
`DrainCursorAsync` at the deferral consume site: one `LogInformation` line naming the workspace-relative
deferred paths (`string.Join(", ", deferredPaths)`). The stored hold reason stays path-free (unchanged, still
routed through `RecordError`/`Scrub`).

### (d) `ready (rebuilding)` status hint — finding 4 (low, polish)
`WorkspaceRender.VectorsReadyLabel`: renders `ready (rebuilding)` (precedence over `ready (updating; N …)`)
when the active generation is `ready` and a cursor's `LastError` carries the shadow-rebuild-pending marker.
The marker is surfaced as a planner contract const `VectorConvergePlanner.ShadowRebuildPendingMarker`
(`"the symbol cursor's shadow rebuild"`) used by the existing `ChunkHold` string — its produced string is
**byte-identical** to before. JSON output is untouched (compact render only). See Judgment calls for the
scope nuance.

## Verification

- **worker-red-green** — invariant: the four fixes behave (retry re-drains/cancels; incremental disk block
  holds+stamps; deferral logs paths; render hints rebuilding). Scope: owned test classes.
  `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --no-build --filter
  "FullyQualifiedName~VectorConvergeServiceTests|FullyQualifiedName~WorkspaceVectorFactsRenderTests"` →
  **Passed 60, Failed 0, Skipped 0** (2026-07-21).
- **Diagnostic build** — invariant: 0W/0E, analyzers-as-errors clean. `dotnet build Miller.slnx -c Release`
  → **Build succeeded, 0 Warning(s), 0 Error(s)** (2026-07-21).
- **worker-ceiling** — invariant: fast suite stays green and pure. `scripts/test.sh` → **Passed 4290,
  Failed 0, Skipped 2**, wall 17s (2026-07-21).
  - First `scripts/test.sh` run showed **1 flake**:
    `RepositoryIndexLoaderBridgeTests.Load_RootMillerJsonUnknownProvider_DoesNotRunDefaultProvider`
    (`ObjectDisposedException: 'SQLitePCL.sqlite3'` in `BuildChainDb`) — a cross-test SQLite pool-disposal
    race in a class I do not own and do not touch. Passed 18/18 in isolation and green on fast-suite re-run.
    Pre-existing non-determinism, unrelated to this change. **Not a Canary\* class**; flagged for the lead.
- Scale suite intentionally NOT run (lead runs it for this batch; escalation trigger on VectorConvergeService).

## Files changed (owned)
- `src/Miller.Indexing/Semantic/VectorConvergePlanner.cs` — add `ShadowRebuildPendingMarker` const; use it in
  `ChunkHold` (produced string unchanged).
- `src/Miller.Server/Hosting/VectorConvergeService.cs` — retry seams/fields/ctor params; `ExecuteAsync` loop;
  `DrainOnceAsync`→bool; deferral log; incremental disk gate; `RefuseIncrementalForDisk` +
  `DiskBlockedReason(action)` refactor; `Schedule/Run/CancelPendingRetry`.
- `src/Miller.Server/Tools/WorkspaceRender.cs` — `VectorsReadyLabel` rebuilding hint + `ShadowRebuildPending`
  helpers.
- `tests/Miller.Tests/Server/VectorConvergeServiceTests.cs` — 4 tests (incremental disk block; deferral log;
  retry re-drain; real-wake cancels retry) + `RecordingLogger`/`LogEntry`/`DelayGate`/`ServiceWithLogger`/
  `ServiceWithRetry` helpers.
- `tests/Miller.Tests/Server/WorkspaceVectorFactsRenderTests.cs` — 1 test (ready-while-rebuilding hint).

Sibling in-flight files present in the worktree but **not touched by me**: `SemanticSearchArm.cs`,
`SearchRouteExecutor.cs`, `SemanticQueryDiagnosticsTests.cs` (Task 3).

## Miller calls used
Miller MCP tools were unavailable at the start of orientation and surfaced mid-session as deferred tools; I
had already oriented by reading the worktree files directly (the authoritative source, since the index serves
the base checkout and does not include this branch's edits). Miller `inspect/context/trace` schemas were
loaded but the direct reads already gave exact, current line numbers, so I proceeded with those to avoid the
shared-index jam risk noted in the brief. Orientation evidence gathered by reading:
`VectorConvergeService.cs` (full), `VectorConvergePlanner.cs` (full), `WorkspaceRender.cs`
(VectorsLabel/VectorsReadyLabel/WriteVectorsJson), `VectorSidecar.cs` (Classify/PauseState/VectorSidecarFacts),
both test files, and the P4 finding doc.

## API-shape evidence (proven by reading)
- `DiskGate` = `delegate DiskPreflightVerdict DiskGate(int workUnits)`; `ProductionDiskGate` and
  `AlwaysAvailable` at the documented sites. `RefuseForDisk`/`BlockedForDisk` mark `state.MarkDiskBlocked` and
  return the recorded reason; `ResolvePause` stamps `converge_pause_state` = `disk-blocked` with precedence
  below `circuit-open`. My incremental gate reuses this exact path.
- `VectorConvergePlanner.EvaluateChunkCursor` returns `ChunkCursorDecision.DeferredPaths` (from
  `content_sources` hash disagreement); the drain consumes them at the site I logged.
- `WriteVectorsJson` (:521) serializes: state, path, reason, build_progress_percent, downloading_model,
  serving_tag, serving_role, artifact_id, symbol_cursor, chunk_cursor, identity, retained_generations. **There
  is no shadow-rebuild-in-progress field** — the basis for the (d) judgment call below.
- `VectorConvergeServiceTests` fake seams: `FakePort` (contract-faithful `Merged` stored view),
  `FakeShadowRebuilder`, `Blocking(free,required)` `DiskGate`, `FastOptions` session, `ServiceOverWorkspace` +
  `SeedForTest`, `WaitUntil`. I followed these (added `DelayGate` for the injectable retry delay,
  `RecordingLogger` for the log assertion) — no real `Task.Delay`, no vec0, fast-suite pure.

## Self-review findings
- Hold-reason format: `ChunkHold` produced string and the shadow disk-block reason are byte-identical to base
  (const-extraction only). JSON status/health output unchanged (verified by the untouched JSON render tests).
- No narration/step comments in tests; intent-named tests. Off-guarantee untouched (`_sidecar.Enabled` early
  return unchanged).
- Retry cannot storm: single-pending guard + real-wake cancel; a persistent hold becomes a bounded ~5-min
  poll, not a spin. `target <= 0` guard means direct/unstamped drains never schedule a retry.

## Judgment calls
- `WorkspaceRender.cs` real path is `src/Miller.Server/Tools/WorkspaceRender.cs` (brief said
  `Miller.Core/Workspace/…`); render tests' real home is
  `tests/Miller.Tests/Server/WorkspaceVectorFactsRenderTests.cs` (brief said `WorkspaceRenderTests`). Edited the
  actual files.
- **Fix (d) scope nuance (plan-reality nuance, decided + noted, not blocking).**
  `src/Miller.Server/Tools/WorkspaceRender.cs:333` — chose to key `ready (rebuilding)` on the **chunk-hold
  shadow-rebuild-pending marker** (an existing, JSON-carried, cross-wake-persistent cursor `LastError`) over a
  disk probe, because: (1) the brief's premise that `VectorSidecarFacts`/`WriteVectorsJson` already carries a
  shadow-rebuild-in-progress field is **not true** — no such field exists; (2) the only other artifact-mediated
  signal of an in-flight rebuild is the on-disk `vectors.db.rebuild` file, which would require probing in
  `VectorSidecar.Classify` (**outside this task's file ownership**) and a new `VectorSidecarFacts`/JSON field
  (**violates "JSON untouched"**). My implementation renders `ready (rebuilding)` for a rebuild that is
  **pending/deferred/failed across wakes** (rebuilder unavailable, shadow open/build failed, or the chunk cursor
  holding while the symbol shadow rebuild is pending — the operationally-visible "stuck long rebuild" an operator
  would misread as idle). It does **not** cover the transient single-wake in-flight window of a rebuild that
  promotes within one wake (finding 4's literal goldfish case, ArtifactIdChanged same-identity) — that window has
  no artifact signal without the `.rebuild` disk probe. Follow-up for the lead: if the transient in-flight window
  must render too, add a `vectors.db.rebuild` existence probe to `VectorSidecar.Classify` surfacing a
  compact-only `RebuildInProgress` flag (needs VectorSidecar ownership; keep it out of `WriteVectorsJson` to
  preserve the JSON contract).

## Issues / concerns
- The (d) nuance above is the only open item — a scope/ownership boundary, decided plan-consistently and
  flagged for the lead's call.
- Pre-existing fast-suite flake in `RepositoryIndexLoaderBridgeTests` (SQLite pool disposal) — not mine;
  flagged so the lead's batch run doesn't misattribute it.
