### Task 3: Semantic query diagnostics (typed reasons, warmth, backend, latency, identity)

**Files:**
- Modify: `src/Miller.Indexing/Semantic/SemanticSearchArm.cs` (`SemanticQueryResult` :16-23, `QueryAsync` :154-204, `Retrieve` :206-243, abstention sites :165-225)
- Modify: `src/Miller.Indexing/Semantic/SemanticEmbeddingSession.cs` (expose warmth + backend from handshake; embed timing)
- Modify: `src/Miller.Server/Tools/SearchRouteExecutor.cs` (`SemanticSymbolFusionArm` :252-283 — surface diagnostics; `IVectorSearchPort` identity exposure near :26-36/:305)
- Modify: `src/Miller.Server/Tools/SearchTool.cs` (`SemanticTextArm` :48-53 — same diagnostics for the content arm; no orchestration changes yet)
- Test: `tests/Miller.Tests/Indexing/SemanticQueryDiagnosticsTests.cs` (new)

**Interfaces:**
- Consumes: Task 1's `VectorSidecar.Encoder` (only incidentally — no behavior coupling); existing `SemanticSidecarHealth.ResolvedBackend`, `SemanticSessionState`, `VectorStore.Identity`.
- Produces: `SemanticQueryDiagnostics` record in `Miller.Indexing`: `(SemanticFallbackKind Fallback, string Backend, bool ColdEmbed, long? EmbedMs, long? KnnMs, SemanticGenerationIdentity? Identity, string? FusionProfile)`. `SemanticFallbackKind` enum in `Miller.Indexing` mirroring the contract's 13 `fallback_reason` values exactly (`None, VectorsMissing, VectorsStale, VectorsIncompatible, VectorsBuilding, ModelNotPrepared, CircuitOpen, EmbedTimeout, EmbedError, KnnError, DiskBlocked, Disabled, Unknown`). `SemanticQueryResult` gains `Diagnostics` (non-null whenever the arm was consulted). `SemanticSymbolFusionArm` exposes the last-call diagnostics to its caller via an out-of-band accessor on the arm instance (transient per call — recon: DI registers it transient, safe to hold per-call state).

**Contract inputs:** Each existing free-text abstention site maps to exactly one `SemanticFallbackKind`; the free-text `UnavailableReason` strings stay (status/CLI use them). Warmth: `ColdEmbed=true` when this call paid sidecar start and/or model load (session state was not `Ready` with a completed handshake before the embed was issued). Timing: `Stopwatch` around the embed RPC and around the KNN query separately; integer ms floor.

**File ownership:** `src/Miller.Indexing/Semantic/SemanticSearchArm.cs`, `src/Miller.Indexing/Semantic/SemanticEmbeddingSession.cs` (warmth/backend exposure), `src/Miller.Server/Tools/SearchRouteExecutor.cs`, `src/Miller.Server/Tools/SearchTool.cs` (SemanticTextArm diagnostics only), `tests/Miller.Tests/Indexing/SemanticQueryDiagnosticsTests.cs`

**Serialization required:** Yes (after Task 1)

**Dependency reason:** Task 1 also edits `SemanticEmbeddingSession.cs`; serialized to avoid same-file conflict.

**What to build:** The measurement layer the canary facts need. Today the arm reports only free-text reasons; backend/warmth are session-scoped; embed/KNN latency is entirely unmeasured on the query path; generation identity never reaches the caller. After this task, every arm consultation (fused, abstained, or failed) yields one `SemanticQueryDiagnostics`.

**Approach:** Thread `VectorStore.Identity` through `VectorStoreSearchPort` (it already exposes `Lane`/`Tag`). Existing callers ignore the new fields — zero behavior change; the P3 determinism tests must stay green untouched.

**Acceptance criteria:**
- [ ] Every abstention path yields the mapped `SemanticFallbackKind` (table-driven test over the fake sidecar/store fixtures).
- [ ] A served arm call yields `Fallback=None`, non-null `EmbedMs`/`KnnMs`, backend, warmth, identity, fusion profile.
- [ ] No change to any rendered search output (fast suite green, P3 determinism tests untouched).
- [ ] Worker-scope verification passes and the change is handed to the lead per commit mode.

