# Task 2 report — disk preflight + `disk-blocked` producer

## Status
COMPLETE. All acceptance criteria met. Not committed (parallel-lead-commit).

## What was built
Second convergence-pause producer, following Task 1's transition-stamp pattern, plus a pure
injectable disk preflight reused by Task 3/4's `semantic prepare` seam.

### Created `src/Miller.Indexing/Semantic/DiskPreflight.cs`
- `DiskPreflightVerdict(bool Ok, long FreeBytes, long RequiredBytes)` — pure record; `.Reason`
  formats both facts human-readably (exact bytes < 1 MiB, else MiB/GiB) so the numbers survive into
  the stored pause reason.
- `DiskPreflight` — verdict logic is pure; the only I/O is an injected `Func<string,long>` free-space
  probe (default walks to the nearest existing ancestor and reads `DriveInfo.AvailableFreeSpace`,
  returning negative on fault). A negative (unknown) probe never blocks a consented build.
- `EstimateRequiredBytes(workUnits, currentArtifactBytes, currentStoredUnits)` — the stated heuristic:
  work-list size × observed bytes-per-unit of the current artifact, floored at `MinimumRequiredBytes`
  (256 MiB); falls back to a conservative per-unit footprint when there is no artifact to observe.

### Modified `src/Miller.Server/Hosting/VectorConvergeService.cs`
- `internal delegate DiskPreflightVerdict DiskGate(int workUnits)` — per-wake seam; `AlwaysAvailable`
  is the neutral default used by every existing drain entry point.
- Injected `Func<WorkspaceContext, IVectorConvergePort, DiskGate> diskGateFactory` (defaults to
  `ProductionDiskGate`, which closes over `.miller` free space and the live artifact's observed
  bytes-per-unit). `DrainOnceAsync` builds and passes the gate.
- **Single pause-resolution point.** Replaced `ApplyCircuitPause` with `ResolvePause(port, session,
  state)`, run on every drain exit path (including the promote path). Explicit precedence: an open
  circuit outranks a disk block, so a wake that is both stamps `circuit-open`. Transition-edge writes
  only (no-op when the stamped value already matches), keeping the hot loop off `vectors_meta`.
- **Wire points.** `RunShadowRebuildAsync` preflights BEFORE `OpenShadow` (so a disk refusal never
  creates a `.rebuild` file), and `BuildShadowAsync` re-checks at each ≤2000-unit slice boundary with
  the remaining count as the shadow grows. A refusal marks `state.DiskBlocked`, records the free/required
  reason as the cursor error, and holds — never throws, never promotes.
- Recovery: the promoted artifact is a fresh generation with no pause stamp, so resolving the pause on
  the reopened port clears a stale `disk-blocked`; the incremental path clears it via the transition edge.

## Miller-first orientation findings
- Task 1's `ApplyCircuitPause` (VectorConvergeService.cs:773) writes `converge_pause_state` on
  transition edges using empty-string clears — extended into `ResolvePause` as the single producer.
- Consumer `VectorSidecar.PauseState` (VectorSidecar.cs:399) maps the exact value `disk-blocked`;
  empty ⟶ absent. Value string matched exactly.
- `VectorConvergePlanner.RebuildWorkList` / `MaxUnitsPerTransaction=2000` drive the shadow slice loop.
- `FakePort` records `MetaWrites` and carries a committed-symbol/chunk overlay; `FakePort.Snapshot`
  returns the same snapshot to BOTH cursors — a delta-history-missing snapshot escalates the symbol
  cursor AND holds the chunk cursor (shaped the precedence test: circuit is tripped via healthy
  escalation drains first, then the snapshot is flipped to disk-block escalation).
- `SemanticPrepareCli.cs` (Task 3, not touched) already carries a local `ISemanticPreparePreflight`
  seam with a note to rewire to this shared `DiskPreflight` in Task 4 — the API here matches that intent
  (`Check(path, requiredBytes)` + injected probe).

## Tests (TDD, red→green; fast suite only, no real disk probing)
- `DiskPreflightTests` (10): boundary at the floor (free == required is Ok, one byte below is blocked),
  unknown-space never blocks, injected probe path passthrough, heuristic math (floor clamp,
  scale-by-observed-per-unit, no-artifact fallback), reason names both facts.
- `VectorConvergeServiceTests` (+4):
  - blocked shadow build stamps `disk-blocked` with free+required in the reason, `OpenShadowCalls==0`
    (no `.rebuild` debris), cursor holds at 0;
  - disk recovers → build promotes → served (reopened) artifact carries no `disk-blocked` pause;
  - stale `disk-blocked` pause cleared on the next incremental wake;
  - both circuit-open and disk-blocked apply → `circuit-open` wins.

### Gate invariants
- DiskPreflight boundary tests: verdict `Ok` iff `free < 0 || free >= required` — pure over the probe.
- disk-blocked stamp test: refusal is a HOLD with the pause stamped and no shadow created.
- recovery tests: a passing preflight (promote or incremental) returns the pause to absent.
- precedence test: `converge_pause_state == circuit-open` when both conditions hold.

## Verification
- worker-red-green: `dotnet test --filter DiskPreflightTests|VectorConvergeServiceTests` → 47 passed,
  0 failed.
- worker-ceiling: `scripts/test.sh` → 4209 passed, 2 skipped, 0 failed. The 30s wall-ceiling tripwire
  fired (104s) — that is the pre-existing Task 8 concern (#28 fast-suite wall-ceiling fix), NOT a test
  failure and unrelated to this change (the added tests run in ~1 ms each). Release build is clean
  under warnings-as-errors. The flaky `IndexerServiceScanTests` did not appear.

## Concerns
- None blocking. The wall-ceiling tripwire is owned by Task 8; every actual test passes.
- Mid-slice disk refusal disposes the partially-built shadow port but relies on the next wake's
  `PrepareShadow` reclamation to remove the `.rebuild` file — identical to the existing embed-failure
  path, not a regression. The pre-`OpenShadow` entry check guarantees the common (entry) refusal leaves
  zero debris.
