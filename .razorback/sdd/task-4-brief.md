### Task 4: `downloading` status state (consumer + producer)

**Files:**
- Modify: `src/Miller.Indexing/VectorSidecar.cs`, `src/Miller.Server/Cli/SemanticPrepareCli.cs` (marker read helper if it lives there)
- Test: `tests/Miller.Tests/Indexing/VectorSidecarClassificationTests.cs` (+ the render/facts tests that cover status strings)

**Interfaces:**
- Consumes: Task 3's marker contract (`<workspace>/.miller/semantic-prepare.marker`, content model id + pid + timestamp).
- Produces: `DownloadingState = "downloading"` constant and classification in `VectorSidecar` — reported when the marker exists AND its pid is alive; a dead-pid marker is stale and ignored (classification falls through, and the next `semantic prepare` run replaces it). Precedence: `downloading` ranks below `circuit-open`/`disk-blocked` (a pause is more actionable) and above `unavailable(model_not_prepared)`.

**Contract inputs:** Design §5.1 compact vocabulary (exact string `downloading`); existing classification precedence tests in `VectorSidecarClassificationTests`.

**File ownership:** Modify: `src/Miller.Indexing/VectorSidecar.cs`, `src/Miller.Server/Cli/SemanticPrepareCli.cs`; Test: `tests/Miller.Tests/Indexing/VectorSidecarClassificationTests.cs`, render tests

**Serialization required:** Yes

**Dependency reason:** Follows Task 3 in Lane 2 (marker produced there).

**What to build:** `workspace status`/`health` say `downloading` while a consented prepare is in flight, so a user watching a fresh setup sees progress instead of `unavailable`. Wire the string through the same facts flow the other states use (`WorkspaceFactsAssembler` → `WorkspaceRender` — the P2 consumer work means only the new state constant and classification arm should be needed; report a plan mismatch if render needs more).

**Approach:** Marker probing goes through the existing `IVectorFileProbe` seam (extend it rather than raw `File` calls) so classification stays unit-testable. Pid-alive check: `Process.GetProcessById` try/catch behind the probe seam.

**Acceptance criteria:**
- [ ] Live marker (pid alive) → compact status `downloading`; JSON carries the model id from the marker.
- [ ] Stale marker (pid dead) → classification unchanged from today; no error.
- [ ] Precedence: pause states beat `downloading`; `downloading` beats `unavailable (model_not_prepared)`.
- [ ] Worker-scope verification passes and the change is committed per `serial-worker-commit`.

