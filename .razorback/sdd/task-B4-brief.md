### Task B4: Corpus builder + dual-cursor convergence

**Files:**
- Create: `src/Miller.Indexing/Semantic/SymbolCardBuilder.cs`, `src/Miller.Indexing/Semantic/VectorConvergePlanner.cs`, `src/Miller.Server/Hosting/VectorConvergeService.cs`, `tests/Miller.Tests/Indexing/SymbolCardBuilderTests.cs`, `tests/Miller.Tests/Indexing/VectorConvergePlannerTests.cs`, `tests/Miller.Tests/Server/VectorConvergeServiceTests.cs`
- Modify: `src/Miller.Server/Hosting/IndexerSidecarConverger.cs` (stamp target revisions + wake), `src/Miller.Server/Hosting/MillerServiceRegistration.cs` (register service; bootstrap-getter discipline)

**Interfaces:**
- Consumes: B2 VectorStore, B3 session; `FreshnessReader.ChangedSince`; `ContentFileClassifier.IsDocsLike` + content_chunks per `content-corpus-v1.md`; vectors-v1 §Cursors (all four chunk-cursor preconditions), §Corpus contract.
- Produces: `SymbolCardBuilder.Build(symbol) -> string` per card text v1 (Global Constraints); `VectorConvergePlanner` (pure): given changed paths + hashes ⟹ re-embed work units gated by `embed_text_hash`; `VectorConvergeService` (hosted, leader-side, lazy bootstrap getters): coalescing capacity-1 wake, snapshot-under-gate/embed-outside-gate/revalidate-and-commit, per-revision staged batches, cursor advanced atomically with its batch; escalation-to-shadow triggers surfaced as a decision enum (execution of shadow build lands in B5).
- Note: `VectorConvergeService` construction must not read bootstrap getters (host lifecycle gotcha in CLAUDE.md).

**Contract inputs:** vectors-v1 §Cursors verbatim — chunk cursor requires content.db artifact-identity + per-source hash agreement, never bare revision comparison. Card eligibility is kind-driven; test symbols get cards with `is_test` set.

**File ownership:** Create: `src/Miller.Indexing/Semantic/SymbolCardBuilder.cs`, `src/Miller.Indexing/Semantic/VectorConvergePlanner.cs`, `src/Miller.Server/Hosting/VectorConvergeService.cs`, tests; Modify: `src/Miller.Server/Hosting/IndexerSidecarConverger.cs`, `src/Miller.Server/Hosting/MillerServiceRegistration.cs`

**Serialization required:** Yes

**Dependency reason:** Consumes B2 store + B3 session.

**What to build:** The write path: card/chunk text construction, hash-gated planning (pure, fast tests), and the drain-loop service (Scale tests with fake sidecar + real store).

**Acceptance criteria:**
- [ ] Card text v1 format/truncation proven by table-driven tests incl. word-boundary + comment-stripping cases; eligibility kind-driven with `is_test` marking
- [ ] Planner: unchanged `embed_text_hash` ⟹ no work; changed ⟹ exactly the affected units; idempotent replay
- [ ] Chunk cursor refuses to advance when content.db lags/identity mismatches (all four precondition rules covered by tests); each cursor carries independent last-error
- [ ] Crash between staged batch and cursor advance leaves a re-runnable state, never a cursor ahead of content (test simulates kill between stages)
- [ ] `HostStartupRegistrationTests` green (no bootstrap getter reads at construction)
- [ ] Worker-scope verification passes; worker commits per serial-worker-commit

