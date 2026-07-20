### Task B3: Deterministic fake sidecar + SemanticEmbeddingSession

**Files:**
- Create: `src/Miller.Indexing/Semantic/SemanticEmbeddingSession.cs`, `tests/Miller.Tests/Support/FakeSemanticSidecar.cs`, `tests/Miller.Tests/Indexing/SemanticEmbeddingSessionTests.cs`

**Interfaces:**
- Consumes: `docs/contracts/semantic-sidecar-protocol-v1.md` (handshake, `health`, `embed_batch`, error envelope, per-item failure semantics, stdout purity); B2's `MillerSemanticContract` encoder_fingerprint fields.
- Produces: `SemanticEmbeddingSession` managing a resident child process (start-on-demand, handshake capture of encoder identity, request/response over stdio, restart-with-backoff, circuit-open after repeated failures, clean dispose); `FakeSemanticSidecar` — an in-repo deterministic process (test-support console entry or script) speaking protocol v1, emitting hash-derived unit-norm vectors of the pinned dims so embeddings are reproducible cross-platform; fault modes switchable by env (stall, garbage line on stdout, per-item error, crash mid-batch).

**Contract inputs:** Protocol contract frozen — the fake must pass the same wire-shape assertions the conformance suite applies to the real sidecar (subset: request/response framing, error envelope, `ready`/`degraded_reason` health shape). No real model, no download, no pins.

**File ownership:** Create: `src/Miller.Indexing/Semantic/SemanticEmbeddingSession.cs`, `tests/Miller.Tests/Support/FakeSemanticSidecar.cs`, `tests/Miller.Tests/Indexing/SemanticEmbeddingSessionTests.cs`

**Serialization required:** Yes

**Dependency reason:** Session records encoder_fingerprint from B2's contract types.

**What to build:** Miller's client half of the sidecar relationship, tested end-to-end against a fake it fully controls. Process-spawning tests are Scale-tagged.

**Acceptance criteria:**
- [ ] Session round-trips embed_batch with deterministic vectors; dims/norm validated per protocol tolerances
- [ ] Stall ⟹ bounded timeout ⟹ fail-open error (no hang); crash ⟹ restart-with-backoff; repeated failure ⟹ circuit-open state surfaced as a reason
- [ ] Garbage on stdout ⟹ session fails that request loudly, never misparses
- [ ] Worker-scope verification passes; worker commits per serial-worker-commit

