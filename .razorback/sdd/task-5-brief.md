### Task 5: GC scheduler + live-reader registry

**Files:**
- Create: `src/Miller.Indexing/Semantic/VectorLiveReaderRegistry.cs`
- Modify: `src/Miller.Server/Hosting/VectorConvergeService.cs`; `src/Miller.Indexing/Semantic/VectorGenerationManager.cs` only if an input seam is missing; reader open sites (`SemanticSearchArm`/`WorkspaceIndexProvider` vector open path) to register/unregister
- Test: `tests/Miller.Tests/Indexing/VectorGenerationManagerTests.cs` (registry), `tests/Miller.Tests/Server/VectorConvergeServiceTests.cs` (scheduler wiring)

**Interfaces:**
- Consumes: `VectorGcInputs { Retained, ActiveIsReady, Now, TagsWithLiveReaders, SoakWindow, RetentionCap }` and the GC plan logic in `VectorGenerationManager` (`src/Miller.Indexing/Semantic/VectorGenerationManager.cs:41-68`) — pure, tested, currently caller-less; `RetainedPathFor`/`TagFromRetainedPath`/`EnumerateRetained`.
- Produces: `VectorLiveReaderRegistry` — process-wide, thread-safe `Register(tag) : IDisposable` / `LiveTags` snapshot; GC execution after each successful shadow promote and on leader wakes (piggybacked on the existing drain timer, no new hosted service): build inputs, apply `plan.Deletions` (delete files + fold WAL via `IVectorGenerationFiles`), log one line per deletion with the outcome reason.

**Contract inputs:** P2 B6 decision (recorded in `.razorback/sdd/progress.md`): "P2 posture = soak-window-only GC protection, registration lands with the P4 GC scheduler." Cross-process readers stay protected by the soak window ONLY — the registry is in-process; do not attempt cross-process reader tracking.

**File ownership:** Create: `src/Miller.Indexing/Semantic/VectorLiveReaderRegistry.cs`; Modify: `src/Miller.Server/Hosting/VectorConvergeService.cs`, `src/Miller.Indexing/Semantic/VectorGenerationManager.cs` (if needed), reader open sites; Test: `tests/Miller.Tests/Server/VectorConvergeServiceTests.cs`, `tests/Miller.Tests/Indexing/VectorGenerationManagerTests.cs`

**Serialization required:** Yes

**Dependency reason:** Follows Task 2 in Lane 1 (same files).

**What to build:** Retained generations currently accumulate forever (`vectors.gen-*.db` are never deleted). Wire the existing pure GC plan to real execution so rollback generations disappear after the soak window unless a live in-process reader holds them, capped at `DefaultRetentionCap`.

**Approach:** Registry is a `ConcurrentDictionary<string,int>` refcount; readers register on open, dispose on close. Scheduler runs under the leader's converge lock only (readers never GC). Deletion failures (Windows held handles) log and retry next wake — never crash the drain. TDD with the fake files seam (`IVectorGenerationFiles`) already used by `VectorGenerationManager` tests.

**Acceptance criteria:**
- [ ] After promote, generations beyond the soak window with no live reader are deleted; `LiveReader`/`WithinSoakWindow`/`OnlyReadyGeneration` outcomes are respected (existing plan semantics unchanged).
- [ ] A registered live reader blocks deletion until disposed; disposal makes the next wake collect it.
- [ ] GC never runs on reader instances (non-leader), proved by test.
- [ ] Worker-scope verification passes and the change is committed per `serial-worker-commit`.

