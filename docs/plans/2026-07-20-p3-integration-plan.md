# P3 Integration Implementation Plan (Miller semantic program)

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Ship the query-time half of local semantic retrieval — SemanticQueryPolicy, the KNN arm, weighted-RRF hybrid fusion at the C1 seam, semantic rescue, content-mode arm, and the CLI `--arm` debug flag — all inert unless `MILLER_SEMANTIC=on` with a ready, fingerprint-matched artifact.

**Architecture:** Design §6.2–§6.3 of [2026-07-19-miller-semantic-integration-design.md](2026-07-19-miller-semantic-integration-design.md). A separate semantic arm composed at the executor level (never a decorator over `ISymbolLookupIndex`); fusion interposes on `SearchRouteExecutor.CollectSymbolCandidates` (the C1 seam) and reorders/extends `SymbolCandidateSet.Candidates`; rendering is untouched. Lexical-only output stays byte-identical in every mode (golden parity is the hard gate).

**Tech Stack:** existing P2 surfaces only — `VectorSidecar.TryOpen` → `VectorStore.Search` (KNN), `SemanticEmbeddingSession.EmbedQueryAsync` (fake sidecar in tests), `MillerSemanticContract.FusionProfile`.

**Architecture Quality:** Pure decision logic (policy, RRF math, profile constants) in Miller.Core; artifact/session I/O composition in Miller.Indexing; route wiring in Miller.Server. The main risk is fusion leaking behavior into the lexical path — guarded by the 18-case golden parity suite plus new byte-identity tests for off/shadow/unready/mismatched states. Workers report plan mismatches; no local redesigns.

## Global Constraints

- **`MILLER_SEMANTIC=off` is a permanent zero-work guarantee** (B1 tests must stay green). `shadow` = artifacts build, retrieval NEVER changes — output byte-identical to lexical-only. Hybrid participates only under `on` AND artifact `ready` AND encoder-fingerprint match (`VectorSidecar.TryOpen` is the only gate; a null store ⟹ lexical-only with the reason left to status/telemetry, never the result payload).
- **Lexical-only output byte-identical in every mode** — `SearchGoldenParityTests` (18 cases) must pass unchanged; semantic failure never breaks a lexical success (fail-open, per-call).
- Fusion: weighted RRF, rank-based, never score-based. Profile `fusion-v1` starting weights per design §6.2: symbol-lookup 1.0 lexical / 0.3 semantic, conceptual 0.5/1.0, mixed 0.8/0.8; k and weights versioned constants in Miller.Core. Dedupe by SymbolId before RRF; tie-breaks: fused score, then lexical score, then symbol id.
- Output contract: `score` keeps meaning lexical score; hybrid rows may carry additive `rrf_score` + per-arm ranks in JSON; JSON stays a bare array; compact layout unchanged except ordering.
- Filter-aware recall: `ToolSearchFilters` never silently drops semantic hits — deterministic adaptive refill mirroring the lexical 500-candidate escalation.
- Rescue rung: compact mode=auto only, ≤2 rows total, single-affordance rule, labels `semantic symbol` / `semantic docs`, telemetry `auto_rescue_kind=semantic_symbol|semantic_docs|semantic_mixed`.
- `mode=source` stays lexical-only under the default corpus, with a `source_chunks_not_indexed` note when the arm would have been consulted; symbol cards are never presented as source hits.
- `--arm lexical|semantic|hybrid` is CLI-ONLY. **No new MCP tool or MCP parameter** (MCP-stinginess rule; adding one needs explicit user approval).
- Persisted telemetry stays enum/counter-only (no query text) — reuse the forbidden-text test pattern.
- ADR-0001 guidance budgets unchanged; `AgentInstructionsTests` green.
- **No pushes** (standing 2026-07-20 directive). Serial lanes commit per task; parallel batch is lead-commit.
- Fast-suite headroom: quiet baseline ~23s of a 30s wall ceiling — fast additions well under 1s per task; sqlite-vec/process tests are `[Trait("Category","Scale")]`, skip-not-fail.

## Track 1 — BLOCKED pending user approval (not tasks in this plan)

Real pins (`scripts/semantic-pins.json`), restore scripts, csproj `Verify` guard, sqlite-vec release packaging, real-sidecar Scale tests, packaged-AOT smoke ×4 RIDs, Metal/Vulkan CI lanes, pinned-Julie drop-in test, swarm RAM + idle-unload gate. All require a **published `julie-semantic-sidecar` RC** — RC publication needs explicit user approval (release-discipline rule) and spends GH Actions minutes (4 legs, 2 macOS at 10×). Planned as a follow-up plan once the RC exists.

## Verification Strategy

**Project source of truth:** CLAUDE.md (fast/Scale split, `scripts/test.sh`, 0-warning Release build).

**Worker red/green scope:** `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~<task classes>"` — each task names its filter.

**Worker ceiling:** `scripts/test.sh` + `dotnet build Miller.slnx -c Release`. Workers do not run broader scopes; a failing assigned gate stops the worker (never weaken a gate).

**Worker gate invariant:** stated per task; golden parity + off/shadow byte-identity are hard gates everywhere.

**Lead affected-change scope:** `scripts/test.sh` after each landed batch.

**Branch gate:** `scripts/test.sh all` (with `SPIKE_CACHE_DIR` so Scale runs, not skips) + Release build, quiet machine.

**Escalation triggers:** any golden-parity diff ⟹ stop, it is a defect by definition; any off-guarantee test red ⟹ stop.

**Assigned verification failure:** stop and report.

**Verification ledger:** `docs/plans/2026-07-20-p3-verification-ledger.md`, same format as P2.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task F1: SemanticQueryPolicy | Batch A | Create: `src/Miller.Core/Search/SemanticQueryPolicy.cs`, `tests/Miller.Tests/Core/SemanticQueryPolicyTests.cs` | No | None - safe parallel batch. |
| Task F2: Semantic retrieval arm | Batch A | Create: `src/Miller.Indexing/Semantic/SemanticSearchArm.cs`, `tests/Miller.Tests/Indexing/SemanticSearchArmTests.cs` | No | None - safe parallel batch. |
| Task F3: RRF fusion at the executor seam | None - serial | Create: `src/Miller.Core/Search/RrfFusion.cs`, `tests/Miller.Tests/Core/RrfFusionTests.cs`, `tests/Miller.Tests/Server/HybridSearchTests.cs`; Modify: `src/Miller.Server/Tools/SearchRouteExecutor.cs`, `src/Miller.Server/Tools/SearchTool.cs` (candidate-set plumb + JSON rrf fields), `src/Miller.Server/Hosting/MillerServiceRegistration.cs` (arm wiring) | Yes | Consumes F1 routing + F2 arm; touches the shared SearchTool.cs. |
| Task F4: Semantic rescue + content/source modes | None - serial | Modify: `src/Miller.Server/Tools/SearchTool.cs` (rescue ladder ~:317/:1477, content/source routes), telemetry metadata keys; Test: `tests/Miller.Tests/Server/SearchToolRescueTests.cs` (new), existing content-route tests | Yes (after F3) | Same SearchTool.cs file; rescue reuses F2 arm + F1 policy. |
| Task F5: CLI --arm + determinism contract | None - serial | Modify: `src/Miller.Server/Cli/CliDispatch.cs` (search verb); Create: `tests/Miller.Tests/Server/SearchDeterminismTests.cs`; Test: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` | Yes (after F4) | Exercises the full hybrid path end to end. |

Commit mode: Batch A is `parallel-lead-commit`; F3–F5 are `serial-worker-commit`.

---

### Task F1: SemanticQueryPolicy

**Files:**
- Create: `src/Miller.Core/Search/SemanticQueryPolicy.cs`
- Test: `tests/Miller.Tests/Core/SemanticQueryPolicyTests.cs`

**Interfaces:**
- Consumes: nothing but the query string and (for the ambiguous case) a caller-supplied weak-lexical-evidence signal (top lexical score / hit count — shape chosen here, consumed by F3).
- Produces: `SemanticQueryPolicy.Route(query, LexicalEvidence?) -> SemanticQueryRoute { LexicalOnly, Hybrid, HybridClass }` where `HybridClass` is the fusion-profile class (`symbol_lookup | conceptual | mixed`) F3 keys weights on; a `PolicyVersion` constant. Pure, deterministic, versioned.

**Contract inputs:** design §6.2 policy row: identifier-like/path-like/short ⟹ lexical-only; clear prose/docs queries ⟹ hybrid (conceptual); ambiguous ⟹ decided by weak lexical evidence, NOT the empty-diagnosis classifier. Reuse existing query-shape helpers where they exist (`IsPathLikeQuery` etc. — discover via Miller, do not duplicate logic Miller.Core already has; lift shared helpers rather than copying).

**File ownership:** Create: `src/Miller.Core/Search/SemanticQueryPolicy.cs`, `tests/Miller.Tests/Core/SemanticQueryPolicyTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** The routing brain: cheap, pure classification of query shape into lexical-only vs hybrid (+ class). It must never require an index or artifact.

**Acceptance criteria:**
- [x] Table-driven tests: identifiers (`FooBar`, `foo_bar`, `foo.Bar`), paths (`src/x/y.cs`), short tokens ⟹ LexicalOnly; prose ("how does indexing converge") ⟹ Hybrid/conceptual; ambiguous two-word queries flip on lexical evidence
- [x] Deterministic and pure (no index, no I/O, no clock); PolicyVersion constant present
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode

### Task F2: Semantic retrieval arm

**Files:**
- Create: `src/Miller.Indexing/Semantic/SemanticSearchArm.cs`
- Test: `tests/Miller.Tests/Indexing/SemanticSearchArmTests.cs`

**Interfaces:**
- Consumes: `VectorSidecar.TryOpen(workspaceRoot, out reason)` (the ONLY artifact gate), `VectorStore.Search` (KNN over symbol_vectors / chunk_vectors), `SemanticEmbeddingSession.EmbedQueryAsync` (+ handshake fingerprint vs store identity — TryOpen already revalidates), the int8 quantizer (currently `QuantizeToInt8` in `VectorConvergeService` — B4 judgment call 4 said the reader side should move it to a shared helper: move it into Miller.Indexing/Semantic and point the service at it; that file touch is sanctioned for exactly that move).
- Produces: `SemanticSearchArm.QuerySymbols(query, k, ToolSearchFilters-shaped allow predicate) -> IReadOnlyList<SemanticHit(SymbolId, DocId?, FilePath, Rank, Cosine)>` and `QueryChunks(...)` for docs/config; fail-open: any failure (no artifact, circuit-open, embed fail) returns an empty result WITH a reason string for telemetry, never throws to the caller.

**Contract inputs:** design §6.2 filter-aware recall — when a filter predicate rejects hits, deterministically refill (fetch deeper, bounded, mirroring the lexical 500-candidate escalation) so filtered hybrid never silently loses results. Session acquisition: reuse the launcher/locator pattern from `VectorConvergeService`; a missing sidecar binary is an empty-result reason. Fast tests use `FakeSemanticSidecar` in-process + a recorded store seam; real-extension tests are Scale.

**File ownership:** Create: `src/Miller.Indexing/Semantic/SemanticSearchArm.cs`, `tests/Miller.Tests/Indexing/SemanticSearchArmTests.cs`; sanctioned narrow touch of `src/Miller.Server/Hosting/VectorConvergeService.cs` ONLY to relocate the shared quantizer (no behavior change there).

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** The read half of the vector artifact: embed the query, KNN, honor filters with deterministic refill, fail open with reasons.

**Acceptance criteria:**
- [ ] KNN returns rank+cosine hits mapped to SymbolId/path via the mapping tables; deterministic ordering (distance, then rowid)
- [ ] Filtered query with rejecting predicate refills deterministically and never silently returns fewer than available allowed hits (bounded escalation test)
- [ ] Every failure mode (off, no artifact, incompatible, circuit-open, embed failure) yields empty + reason, never an exception; off performs zero work
- [ ] Worker-scope verification passes and the change is handed to the lead per commit mode

### Task F3: RRF fusion at the executor seam

**Files:**
- Create: `src/Miller.Core/Search/RrfFusion.cs`, `tests/Miller.Tests/Core/RrfFusionTests.cs`, `tests/Miller.Tests/Server/HybridSearchTests.cs`
- Modify: `src/Miller.Server/Tools/SearchRouteExecutor.cs` (fusion interposes in `CollectSymbolCandidates` / between collect and render in `RunSymbols`), `src/Miller.Server/Tools/SearchTool.cs` (carry optional rrf_score/per-arm ranks on `SymbolCandidate` rows into `RenderJson` additively), `src/Miller.Server/Hosting/MillerServiceRegistration.cs` (compose arm + policy for the server path)

**Interfaces:**
- Consumes: F1 `SemanticQueryPolicy` (route + class), F2 `SemanticSearchArm`, C1's `SymbolCandidateSet` (`SearchTool.cs:55`) and `SearchRouteExecutor.CollectSymbolCandidates` (`SearchRouteExecutor.cs:25`), `MillerSemanticContract.FusionProfile`.
- Produces: `RrfFusion.Fuse(lexical ranks, semantic ranks, FusionWeights, k) -> fused order` (pure, Miller.Core, versioned constants `fusion-v1`); hybrid-aware executor path: policy says Hybrid AND sidecar opens ⟹ fuse (dedupe by SymbolId first, stable tie-breaks), else the EXACT pre-existing path. JSON rows that participated carry `rrf_score` + `lexical_rank` + `semantic_rank` (additive; absent on lexical-only rows and lexical-only runs).

**Contract inputs:** Global Constraints fusion row (weights/k versioned); `score` keeps meaning lexical score; blast radius is the search tool route ONLY — the ~480 other `SearchTool.Run` callers (context/impact/trace/CLI read paths) stay lexical (C1 P3 hand-off note, lead-confirmed). Golden parity (18 cases) must pass byte-identical on the lexical path; add byte-identity tests for shadow/unready/fingerprint-mismatch states.

**File ownership:** as Files above — this task owns `SearchTool.cs` and `SearchRouteExecutor.cs` for its duration.

**Serialization required:** Yes

**Dependency reason:** Consumes F1 routing + F2 arm; touches the shared SearchTool.cs.

**What to build:** The fusion itself, wired only at the executor seam, provably inert in every non-hybrid state.

**Acceptance criteria:**
- [ ] Pure RRF tests: rank math, per-class weights, dedupe-before-fuse, stable tie-breaks (fused, lexical score, symbol id)
- [ ] Hybrid end-to-end (fake sidecar + real or faked store): a conceptual query reorders/extends candidates; rows carry additive rrf fields in JSON; compact layout unchanged except order
- [ ] Byte-identity: off, shadow, unready artifact, fingerprint mismatch, policy=LexicalOnly — all render byte-identical to pre-fusion output; SearchGoldenParityTests green unchanged
- [ ] Semantic arm failure mid-query ⟹ lexical result unchanged (fail-open test)
- [ ] Worker-scope verification passes; worker commits per serial-worker-commit

### Task F4: Semantic rescue + content/source modes

**Files:**
- Modify: `src/Miller.Server/Tools/SearchTool.cs` (auto-rescue ladder — `TryRunAutoTextRescue` `SearchTool.cs:1477`, rescue stamping `:317-333`; content route hybrid arm; source-mode note)
- Create: `tests/Miller.Tests/Server/SearchToolRescueTests.cs`
- Test: extend existing content-route test files

**Interfaces:**
- Consumes: F1 policy (rescue trigger = rescue-eligible AND semantically-shaped), F2 arm (`QuerySymbols` + `QueryChunks`), existing rescue plumbing (`AutoTextRescueResult`, `auto_rescue_kind` metadata).
- Produces: final rescue rung emitting ≤2 rows labeled `semantic symbol` / `semantic docs` (compact, mode=auto only); `auto_rescue_kind` gains `semantic_symbol|semantic_docs|semantic_mixed`; `mode=content` gains the chunk-vector hybrid arm under the same gating as F3; `mode=source` stays lexical-only and appends the `source_chunks_not_indexed` note ONLY when the arm would have been consulted (policy says hybrid + artifact ready).

**Contract inputs:** design §6.3 verbatim; single-affordance rule (the rescue block keeps one next-step affordance); JSON untouched by rescue (compact-only, as today's rescue is); all gating identical to F3 (off/shadow/unready ⟹ byte-identical).

**File ownership:** as Files above — owns `SearchTool.cs` after F3 lands.

**Serialization required:** Yes (after F3)

**Dependency reason:** Same SearchTool.cs file; rescue reuses F2 arm + F1 policy.

**What to build:** The last-resort semantic affordance and the mode contracts, without touching symbol-route ranking (that is F3's).

**Acceptance criteria:**
- [ ] Rescue fires only when rescue-eligible AND policy says semantically-shaped AND artifact ready; emits ≤2 labeled rows; telemetry kind stamped; JSON unaffected
- [ ] `mode=content` hybrid reorders chunk hits under gating; `mode=source` never returns a card and appends the note only when the arm was consultable
- [ ] All off/shadow/unready states byte-identical on every touched route
- [ ] Worker-scope verification passes; worker commits per serial-worker-commit

### Task F5: CLI `--arm` + determinism contract

**Files:**
- Modify: `src/Miller.Server/Cli/CliDispatch.cs` (search verb: `--arm lexical|semantic|hybrid`, default absent = normal policy routing)
- Create: `tests/Miller.Tests/Server/SearchDeterminismTests.cs`
- Test: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`

**Interfaces:**
- Consumes: F3's hybrid executor path; F2 arm.
- Produces: CLI-only `--arm` forcing a single arm (`semantic` renders the semantic hits with cosine for evaluation; `hybrid` forces fusion regardless of policy; `lexical` forces today's path); determinism contract tests: identical inputs (fixed fake-sidecar vectors + fixed store) produce byte-identical output across repeated runs for each arm.

**Contract inputs:** `--arm` is NOT an MCP parameter (Global Constraints). Usage string additive. `--arm semantic|hybrid` without a ready artifact fails with a stated reason (evaluation flag — loud beats silent).

**File ownership:** as Files above.

**Serialization required:** Yes (after F4)

**Dependency reason:** Exercises the full hybrid path end to end.

**What to build:** The evaluation lever and the determinism proof for the whole query-time stack.

**Acceptance criteria:**
- [ ] `--arm` accepted on the search verb only; forces the named arm; absent flag = policy routing; unready artifact + forced semantic/hybrid ⟹ loud stated failure
- [ ] Determinism: repeated identical runs byte-identical per arm (fake vectors)
- [ ] Worker-scope verification passes; worker commits per serial-worker-commit
