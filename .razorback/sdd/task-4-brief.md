### Task 4: Converge follow-ups — starvation retry wake, incremental disk gate, deferred-source log, status hint

**Files:**
- Modify: `src/Miller.Server/Hosting/VectorConvergeService.cs` (wake loop `ExecuteAsync` :284/:296, `DrainCursorAsync` incremental branch :596-631, deferral consume :563-576)
- Modify: `src/Miller.Indexing/Semantic/VectorConvergePlanner.cs` (hold plan :201-210 — only if a hold flag needs surfacing; prefer no change)
- Modify: `src/Miller.Core/Workspace/WorkspaceRender.cs` (`VectorsLabel` :321, `VectorsReadyLabel` :333)
- Test: extend `tests/Miller.Tests/Server/VectorConvergeServiceTests.cs`, `tests/Miller.Tests/Core/WorkspaceRenderTests.cs` (or the render tests' actual home — locate with Miller before editing)

**Interfaces:**
- Consumes: existing `VectorConvergeSignal` (capacity-1 coalescing semaphore), `DiskGate` delegate (:181) + `ProductionDiskGate` (:361) + `RefuseForDisk`/`BlockedForDisk` (:848/:860), P4's `converge_pause_state` disk-blocked facts, `VectorSidecarFacts` (shadow-rebuild-in-progress indicator — verify exact field via `WriteVectorsJson` :521 before rendering).
- Produces: (a) a bounded held-cursor retry: when a drain ends with a held cursor (`AdvanceTo=0`/hold reason), schedule exactly one delayed signal re-stamp (default 5 minutes, test-injectable delay/scheduler; coalesces — no stacking retries; canceled by a real wake). (b) The incremental branch consults `state.DiskGate` before `EmbedAsync`/`Commit`, mirroring shadow-path semantics: blocked ⟹ record disk-blocked pause state, hold the cursor, no partial write, no hard fail. (c) An INFO log naming the deferred workspace-relative paths at the deferral consume site (stored hold reason stays path-free). (d) Compact status renders `ready (rebuilding)` when state is ready and a shadow rebuild is in flight (JSON untouched — it already carries the state).

**Contract inputs:** The stored hold reason string format must not change (status surfaces show it). Disk-blocked semantics must match the shadow path's pause facts so `workspace status`/health render it identically.

**File ownership:** `src/Miller.Server/Hosting/VectorConvergeService.cs` (post-Task-1), `src/Miller.Indexing/Semantic/VectorConvergePlanner.cs`, `src/Miller.Core/Workspace/WorkspaceRender.cs`, the listed test files.

**Serialization required:** Yes (after Task 1)

**Dependency reason:** Task 1 edits two encoder-ref lines in `VectorConvergeService.cs`; serialized to avoid same-file conflict. Parallel-safe with Task 3 (disjoint files).

**What to build:** The three Miller-side pre-P5 reliability fixes plus the status polish, red-first (each fix starts from a failing test reproducing the P4 dogfood finding: quiet-workspace starvation, ungated incremental write under a blocked disk, plain `ready` during rebuild).

**Approach:** For the retry wake, follow the existing fake/injectable patterns in `VectorConvergeServiceTests` (no real `Task.Delay` in tests). Escalation trigger applies: run the scale suite for this batch.

**Acceptance criteria:**
- [ ] A held cursor on a quiet workspace re-drains after the retry delay without an index-convergence stamp; a real wake cancels/absorbs the pending retry; no retry storm (at most one pending).
- [ ] Incremental drain under a blocked disk gate: no vectors.db write, cursor held, disk-blocked pause state recorded; unblocking resumes.
- [ ] Deferral logs one INFO line naming the deferred paths; stored reason unchanged.
- [ ] Compact status shows `ready (rebuilding)` during a shadow rebuild; plain `ready` otherwise; JSON output unchanged.
- [ ] Worker-scope verification passes and the change is handed to the lead per commit mode.

