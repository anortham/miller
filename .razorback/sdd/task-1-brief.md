### Task 1: Encoder pin registry + `MILLER_SEMANTIC_MODEL` swap seam

**Files:**
- Modify: `src/Miller.Indexing/Semantic/MillerSemanticContract.cs` (`DefaultEncoder`/`FallbackEncoder` at :94/:105)
- Modify: `src/Miller.Indexing/VectorSidecar.cs` (:207 direct `DefaultEncoder` ref; `FromEnvironment` at :162 area)
- Modify: `src/Miller.Server/Hosting/VectorConvergeService.cs` (:591, :1139 direct `DefaultEncoder` refs)
- Modify: `src/Miller.Indexing/Semantic/SemanticEmbeddingSession.cs` (`FindPin` :685)
- Test: `tests/Miller.Tests/Indexing/SemanticEncoderSelectionTests.cs` (new)

**Interfaces:**
- Consumes: existing `SemanticEncoderPin`, `MillerSemanticContract.PinnedIdentity`, `ClassifyChange` (fingerprint change ⟹ `ShadowRebuild` — already tested).
- Produces: `MillerSemanticContract.KnownEncoders` — `IReadOnlyList<SemanticEncoderPin>` containing the qwen3 and bge-small pins keyed by `ModelId`; `MillerSemanticContract.FindEncoder(string modelId) : SemanticEncoderPin?`; `SemanticEncoderSelection.FromEnvironment() : SemanticEncoderPin` reading env var `MILLER_SEMANTIC_MODEL` (exact `ModelId` match against `KnownEncoders`; unset/empty → `DefaultEncoder`; unknown value → `DefaultEncoder` + one warning log at first resolution); `VectorSidecar.Encoder : SemanticEncoderPin` (the resolved active pin, set in `FromEnvironment`, injectable in tests via existing construction seams).

**Contract inputs:** `MILLER_SEMANTIC_MODEL` is the env var name. The active pin flows to every site that today hard-codes `DefaultEncoder`: `VectorSidecar` :207 fingerprint, `VectorConvergeService` :591/:1139 pinned identity. `FindPin` in `SemanticEmbeddingSession` generalizes to `MillerSemanticContract.FindEncoder`. Do NOT change `DefaultEncoder`'s pin values or `CanonicalEncoderString` — fingerprints of existing artifacts must not move.

**File ownership:** `src/Miller.Indexing/Semantic/MillerSemanticContract.cs`, `src/Miller.Indexing/VectorSidecar.cs`, `src/Miller.Server/Hosting/VectorConvergeService.cs` (encoder refs only), `src/Miller.Indexing/Semantic/SemanticEmbeddingSession.cs` (FindPin only), `tests/Miller.Tests/Indexing/SemanticEncoderSelectionTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** A registry + selection seam so swapping the embedding model is one env var. Selecting the fallback pin must produce its `PinnedIdentity`, which the existing generation-identity machinery classifies as `ShadowRebuild` — the swap then converges via the normal shadow-generation path with the old generation retained for rollback. No new download logic: `miller semantic prepare` and the sidecar `prepare` contract already key off the pin handed to them (verify the prepare path receives the active pin, not `DefaultEncoder`, and fix if hard-coded — trace `semantic prepare` in `CliDispatch`).

**Approach:** Keep `DefaultEncoder`/`FallbackEncoder` properties (tests reference them); add the registry on top. Resolution is process-wide and read once (matching `VectorSidecar.FromEnvironment`'s pattern); tests construct `VectorSidecar` with an explicit pin rather than mutating the environment.

**Acceptance criteria:**
- [ ] `MILLER_SEMANTIC_MODEL=bge-small-en-v1.5-f32` resolves the bge pin; its `PinnedIdentity` differs from qwen3's and `ClassifyChange` yields `ShadowRebuild`.
- [ ] Unset/unknown env values resolve `DefaultEncoder`; unknown logs one warning.
- [ ] `VectorConvergeService`, `VectorSidecar`, and the `semantic prepare` path all consume the resolved pin (no remaining direct `DefaultEncoder` reads outside `MillerSemanticContract` and tests — guard with a source-scan or reference test).
- [ ] Worker-scope verification passes and the change is handed to the lead per commit mode.

