### Task B2: Generation identity + vectors.db storage schema

**Files:**
- Create: `src/Miller.Indexing/Semantic/MillerSemanticContract.cs`, `src/Miller.Indexing/Semantic/VectorStore.cs`, `tests/Miller.Tests/Indexing/MillerSemanticContractTests.cs`, `tests/Miller.Tests/Indexing/VectorStoreTests.cs`
- Modify: `src/Miller.Indexing/VectorSidecar.cs` (open path validates meta + vec_version)

**Interfaces:**
- Consumes: B1's VectorSidecar; vectors-v1 §Generation identity, §Pinned initial values, §Invalidation matrix, §Storage schema (table/column names verbatim); the sqlite-vec load pattern from `spike/SqliteVec.AotSpike/`.
- Produces: `MillerSemanticContract` exposing the five identity fields with pinned initial values and `ClassifyChange(old, new) -> InvalidationAction` (None | ShadowRebuild | TargetedReEmbed | ReaderGate | QueryTimeOnly) implementing the invalidation matrix as pure logic; `VectorStore` creating/validating `vectors_meta`, `symbol_vectors`/`chunk_vectors` vec0 tables, mapping + filter tables per contract, `vec_version()` checked at open.

**Contract inputs:** vectors-v1 §§Generation identity/Pinned initial values/Invalidation matrix/Storage schema. sqlite-vec extension located via the spike's cache path or `MILLER_SQLITE_VEC_PATH` env for dev; tests needing the real extension are `[Trait("Category","Scale")]` and SKIP when absent.

**File ownership:** Create: `src/Miller.Indexing/Semantic/MillerSemanticContract.cs`, `src/Miller.Indexing/Semantic/VectorStore.cs`, `tests/Miller.Tests/Indexing/VectorStoreTests.cs`; Modify: `src/Miller.Indexing/VectorSidecar.cs`

**Serialization required:** Yes

**Dependency reason:** Builds on B1's VectorSidecar and activation.

**What to build:** The identity/invalidation core (fast, pure tests) and the physical store (Scale tests against real sqlite-vec).

**Acceptance criteria:**
- [ ] Invalidation matrix covered by a table-driven pure test — every field × change ⟹ exactly the contract's mechanism
- [ ] Scale test: create store, write/read vectors round-trip, `vec_version()` matches pin, schema matches contract shapes (column names asserted)
- [ ] Mismatched `reader_compatibility` minimum ⟹ open refused with reason; mismatched `encoder_fingerprint` ⟹ not queryable, no re-embed triggered
- [ ] Worker-scope verification passes; worker commits per serial-worker-commit

