### Task 1: circuit-open pause producer

**Files:**
- Modify: `src/Miller.Server/Hosting/VectorConvergeService.cs`
- Test: `tests/Miller.Tests/Server/VectorConvergeServiceTests.cs`

**Interfaces:**
- Consumes: `SemanticSessionState.CircuitOpen` (existing), `VectorStore.SetMeta(key, value)` (`src/Miller.Indexing/Semantic/VectorStore.cs:186`), existing consumer `VectorSidecar.PauseState` (`src/Miller.Indexing/VectorSidecar.cs:400`) with keys `converge_pause_state`/`converge_pause_reason`.
- Produces: `converge_pause_state=circuit-open` + human-readable `converge_pause_reason` stamped on the active artifact when the session circuit opens during a drain; both keys **cleared** (deleted or set empty — match `PauseState`'s null semantics) on the first successful drain wake after recovery.

**Contract inputs:** `VectorSidecarClassificationTests.CircuitOpenPause_OverridesReady` (`tests/Miller.Tests/Indexing/VectorSidecarClassificationTests.cs:73`) fixes the consumer's expected key/values — the producer must emit exactly those.

**File ownership:** Modify: `src/Miller.Server/Hosting/VectorConvergeService.cs`; Test: `tests/Miller.Tests/Server/VectorConvergeServiceTests.cs`

**Serialization required:** Yes

**Dependency reason:** Tasks 1, 2, 5 all modify `VectorConvergeService.cs` and its test file; ordered lane.

**What to build:** When a drain wake ends with the circuit open, stamp the pause on the artifact so `workspace status` from ANY process reports `circuit-open` instead of a stale `ready`. When a later wake completes a request cleanly, clear the pause. This closes the top concern from the P2 B6 report ("a paused convergence reports ready").

**Approach:** Stamp inside the drain path where the session's circuit state is already observed (after `DrainOnceAsync`/error recording), via the already-open converge port's store. Write only on state *transitions* (open→stamp, recovered→clear), not every wake — vectors_meta writes on a hot loop would churn WAL. Reuse the existing `RecordError` neighborhood; do not add a new hosted service. TDD against the existing `FakePort` (extend it to expose meta writes).

**Acceptance criteria:**
- [ ] Circuit opening during drain stamps `converge_pause_state=circuit-open` and a non-empty `converge_pause_reason` on the artifact.
- [ ] A subsequent successful wake clears both keys; `workspace status` classification returns to `ready`/`building` (proved via `VectorSidecar.Inspect` on the same store in-test).
- [ ] No meta write occurs on wakes with no state transition.
- [ ] Worker-scope verification passes and the change is committed per `serial-worker-commit`.

