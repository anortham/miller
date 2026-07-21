## Task 2: Enforce vector freshness and share one process-local sidecar session

**Owns:**

- `src/Miller.Indexing/VectorSidecar.cs`
- `src/Miller.Indexing/Semantic/SemanticSearchArm.cs`
- a new session broker under `src/Miller.Indexing/Semantic/` or `src/Miller.Server/Hosting/`
- `src/Miller.Server/Hosting/VectorConvergeService.cs`
- `src/Miller.Server/Hosting/MillerServiceRegistration.cs`
- focused vector/session/registration tests

**Red tests:**

1. Ready generation with a different live artifact ID returns `VectorsStale` before embedding.
2. Cursor lag outside the accepted freshness rule returns `VectorsStale` before embedding.
3. Artifact promotion or cursor change during embedding returns `VectorsStale` before KNN can serve.
4. Missing, stale, building, incompatible, disk-blocked, timeout, and circuit-open states remain distinguishable.
5. Concurrent query and convergence demand creates one session/child and shares restart/circuit state.
6. Semantic off never calls the broker factory.
7. Rebinding workspace A to B recreates root-bound generation cleanup state.

**Implementation:**

- Add a typed vector open/classification result rather than using `TryOpen` null as every failure.
- Pass the live workspace artifact/cursor expectation into query execution and revalidate after embedding.
- Introduce a singleton lazy broker used by `SemanticSearchArm` and `VectorConvergeService`; preserve query priority and bounded cancellation.
- Reset root-bound cleanup objects when the bound workspace identity changes.

**Worker verification:** focused `VectorSidecar*`, `SemanticSearchArm*`, `SemanticEmbeddingSession*`, `VectorConvergeServiceTests`, and `HostStartupRegistrationTests`.

