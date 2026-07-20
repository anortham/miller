# P2 Miller Lanes (b, c, d, e) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Land the four Miller-side P2 lanes of the semantic integration program: vectors-v1 consumption against a deterministic fake sidecar (b), the typed candidate seam refactor (c), the edit reliability lane (d), and the MinHash near-duplicate analyzer (e).

**Architecture:** Lane b implements the frozen `docs/contracts/vectors-v1.md` artifact (five-field generation identity, dual cursors, shadow generations) with embeddings supplied ONLY by an in-repo deterministic fake speaking `docs/contracts/semantic-sidecar-protocol-v1.md` — no pins, no restore scripts, no build guards (those are P3). Lane c splits SearchRouteExecutor's symbol route into candidate generation → (future fusion seam) → rendering with byte-identical golden output. Lanes d and e are self-contained per design §7 and §6.4.

**Tech Stack:** .NET 10, Miller.Core (zero I/O) / Miller.Indexing / Miller.Server seams, Microsoft.Data.Sqlite + sqlite-vec (pinned v0.1.9 line, loaded per the P0 spike pattern in `spike/SqliteVec.AotSpike/`), xUnit with the fast/Scale split.

**Architecture Quality:** Shapes are fixed by the frozen contracts (`vectors-v1.md`, `semantic-sidecar-protocol-v1.md`, `canary-telemetry-v1.md`) and design §5–§7 (`docs/plans/2026-07-19-miller-semantic-integration-design.md`). Program-rated HIGH architecture risk lives in lane b; the mitigations are contract conformance tests plus the `MILLER_SEMANTIC=off` zero-work guarantee. Lane c risk is regression, mitigated by golden-output parity. Workers who find code contradicting the contracts report a plan mismatch — the contracts are frozen and do NOT get edited to fit code.

## Global Constraints

- `MILLER_SEMANTIC=off` (and alias `0`) is a permanent zero-work guarantee per vectors-v1 §File placement and activation: no vectors.db open/create/stat, no `vectors.gen-*.db` enumeration, no sqlite-vec load, no child process, no GPU probe, zero added latency. Test-enforced.
- Lexical-only output stays byte-identical in every mode. Lane c proves this with golden-output tests; lane b must not alter any existing tool output when semantic is off or absent.
- No new MCP tools, no MCP parameter additions, no ServerInstructions growth (`AgentInstructionsTests` stays green). CLI-only surfaces are allowed.
- Fast suite stays fast and pure: tests needing the real sqlite-vec native extension or spawning processes are `[Trait("Category","Scale")]` and SKIP (not fail) when the extension/tooling is absent. A test spawning `julie-extract` uses `ScaleTestSupport.RequireJulieServer()`.
- Build is 0 warnings / 0 errors (`dotnet build Miller.slnx -c Release`, TreatWarningsAsErrors).
- Five generation-identity fields and their pinned initial values come verbatim from vectors-v1 §Generation identity / §Pinned initial values. `fusion_profile` never invalidates stored vectors; `reader_compatibility` never triggers re-embedding.
- The chunk cursor never advances past what content.db proves under all four chunk-cursor precondition rules (vectors-v1 §Cursors) — never a bare revision comparison.
- Telemetry fields for canary plumbing come verbatim from `canary-telemetry-v1.md` (enum/counter-only; no query text, no paths in persisted telemetry).
- Card text v1 is local-only (no graph enrichment): `{kind} {qualified name} {signature first line} {doc excerpt ≤300} in: {container} {path}`, ~1,200-char budget, word-boundary truncation, comment-marker stripping. Eligibility is symbol-kind/data-driven, never a language blocklist.
- MinHash analyzer is deterministic (fixed normalization, seeds, LSH params) and separate from `CloneGroupReader`, which is untouched. Card vectors are never used for clone claims.
- Do NOT push miller commits to origin — all work stays local per the 2026-07-20 no-push directive (commits are fine; pushes are not).

## Verification Strategy

**Project source of truth:** `CLAUDE.md` §Testing + §Build; `scripts/test.sh`.

**Worker red/green scope:** `dotnet test --filter "FullyQualifiedName~<TestClassName>"` for the task's test classes (fast suite member), or `scripts/test.sh` when the task touches shared seams.

**Worker ceiling:** `scripts/test.sh` (fast suite). Workers do not run the scale suite on their own; the lead owns it.

**Worker gate invariant:** each task's acceptance criteria name the behavior its tests prove; the off-guarantee test (Task B1) and golden parity tests (Task C1) are hard gates that later tasks must keep green.

**Lead affected-change scope:** `scripts/test.sh` after each accepted batch.

**Branch gate:** `dotnet build Miller.slnx -c Release` (0 warnings) + `scripts/test.sh all` before declaring the plan complete. Scale tests that need sqlite-vec/julie-extract skip cleanly when tooling is absent; the lead runs them on the reference machine where tooling exists.

**Replay/metric evidence:** Lane d's fuzzy-policy evaluation (Task D2) produces a report from the historical failure corpus — report-only, not a hard gate; the <10% error-rate gate is a P4-cohort measurement, not a P2 exit gate.

**Escalation triggers:** touching `MillerServiceRegistration`/hosted services ⟹ run `HostStartupRegistrationTests`; touching search rendering ⟹ run golden parity tests; touching telemetry schema ⟹ run telemetry contract tests.

**Assigned verification failure:** workers stop and report; no gate weakening.

**Verification ledger:** record invariant, command, scope label, commit SHA, result, timestamp in `docs/plans/2026-07-20-p2-verification-ledger.md`.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task C1: Typed candidate seam | Batch A | Create: `src/Miller.Core/Search/SymbolCandidate.cs`, `tests/Miller.Tests/Server/SearchGoldenParityTests.cs`; Modify: `src/Miller.Server/Tools/SearchRouteExecutor.cs`, `src/Miller.Server/Tools/SearchTool.cs` | No | None - safe parallel batch. |
| Task D1: Edit instrumentation + guidance + whitespace | Batch A | Modify: `src/Miller.Server/Tools/EditTool.cs`, edit-related telemetry records, `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` edit description only if within budget; Test: `tests/Miller.Tests/Server/EditToolTests.cs` | No | None - safe parallel batch. |
| Task E1: MinHash near-duplicate analyzer | Batch A | Create: `src/Miller.Core/Analysis/NearDuplicateAnalyzer.cs`, `tests/Miller.Tests/Core/NearDuplicateAnalyzerTests.cs`; Modify: `src/Miller.Server/Tools/MetricsTool.cs`, `tests/Miller.Tests/Server/MetricsToolTests.cs` | No | None - safe parallel batch. |
| Task B1: Activation + VectorSidecar skeleton + off-guarantee | Batch A | Create: `src/Miller.Indexing/VectorSidecar.cs`, `src/Miller.Indexing/SemanticActivation.cs`, `tests/Miller.Tests/Indexing/VectorSidecarTests.cs`, `tests/Miller.Tests/Indexing/SemanticOffGuaranteeTests.cs`; Modify: `src/Miller.Server/Hosting/WorkspaceFactsAssembler.cs`, `src/Miller.Core/Workspace/WorkspaceRender.cs` (or actual render seam), their tests | No | None - safe parallel batch. |
| Task B2: Generation identity + storage schema | None - serial | Create: `src/Miller.Indexing/Semantic/MillerSemanticContract.cs`, `src/Miller.Indexing/Semantic/VectorStore.cs`, `tests/Miller.Tests/Indexing/VectorStoreTests.cs` (Scale where vec0 is real); Modify: `src/Miller.Indexing/VectorSidecar.cs` | Yes | Builds on B1's VectorSidecar and activation. |
| Task B3: Fake sidecar + SemanticEmbeddingSession | None - serial | Create: `src/Miller.Indexing/Semantic/SemanticEmbeddingSession.cs`, `tests/Miller.Tests/Support/FakeSemanticSidecar.cs`, `tests/Miller.Tests/Indexing/SemanticEmbeddingSessionTests.cs` | Yes | Session records encoder_fingerprint from B2's contract types. |
| Task B4: Corpus builder + dual-cursor converge | None - serial | Create: `src/Miller.Indexing/Semantic/SymbolCardBuilder.cs`, `src/Miller.Indexing/Semantic/VectorConvergePlanner.cs`, `src/Miller.Server/Hosting/VectorConvergeService.cs`, tests; Modify: `src/Miller.Server/Hosting/IndexerSidecarConverger.cs`, `src/Miller.Server/Hosting/MillerServiceRegistration.cs` | Yes | Consumes B2 store + B3 session. |
| Task B5: Shadow generations + corruption recovery | None - serial | Create: `src/Miller.Indexing/Semantic/VectorGenerationManager.cs`, tests; Modify: `src/Miller.Indexing/Semantic/VectorStore.cs`, `src/Miller.Server/Hosting/SidecarCorruptionRecovery.cs` | Yes | Promote/GC operate on B4's written artifact. |
| Task B6: Status/health/telemetry + canary plumbing | None - serial | Modify: `src/Miller.Server/Hosting/WorkspaceFactsAssembler.cs`, render seam, telemetry records + writer, their tests | Yes | Reports cursor/generation facts produced by B2–B5; canary fields ride the telemetry seam last to avoid churn. |
| Task D2: Fuzzy policy evaluation + stale-target wait | Batch B (with E2) | Modify: `src/Miller.Server/Tools/EditTool.cs` (plan-time stale wait), fuzzy matcher policy code; Create: `docs/findings/2026-07-20-edit-fuzzy-policy-replay.md`; Test: `tests/Miller.Tests/Server/EditToolTests.cs` | Yes (after D1) | Needs D1's failure-reason instrumentation to build the replay corpus. |
| Task E2: near_duplicate_group_count history metric + dashboard | Batch B (with D2) | Modify: history snapshot writer (per `docs/contracts/metrics-history-v1.md`), `src/Miller.Dashboard/DashboardData.cs`, report rollup; tests alongside each | Yes (after E1) | Consumes E1's analyzer output. |

Commit mode: Batch A and Batch B tasks are `parallel-lead-commit` (lead reviews and commits). Serial tasks B2–B6 are `serial-worker-commit`.

---

### Task C1: Typed candidate seam in SearchRouteExecutor

**Files:**
- Create: `src/Miller.Core/Search/SymbolCandidate.cs`, `tests/Miller.Tests/Server/SearchGoldenParityTests.cs`
- Modify: `src/Miller.Server/Tools/SearchRouteExecutor.cs` (RunSymbols, src/Miller.Server/Tools/SearchRouteExecutor.cs:20), `src/Miller.Server/Tools/SearchTool.cs` (Run seam at :144 caller side)
- Test: `tests/Miller.Tests/Server/SearchRouteExecutorTests.cs` (extend)

**Interfaces:**
- Consumes: `ISymbolLookupIndex`, `SearchRoute`, `SearchRouteExecutionRequest` as they exist today.
- Produces: a typed candidate stage: `IReadOnlyList<SymbolCandidate>` (symbol id, name, path, line, lexical score, enclosing metadata needed by the renderer) flowing candidate-generation → rendering inside RunSymbols, with a single seam method (e.g. `SearchRouteExecutor.CollectSymbolCandidates(...)`) that P3's fusion arm can interpose on. Rendering consumes ONLY the typed list.

**Contract inputs:** Design §6.1. Golden corpus: capture current compact AND json output for a fixed set of ≥12 representative queries (symbol/exact/phrase/filtered/limit-edge/empty-result) against a fixture index BEFORE refactoring; assert byte-identical after.

**File ownership:** Create: `src/Miller.Core/Search/SymbolCandidate.cs`, `tests/Miller.Tests/Server/SearchGoldenParityTests.cs`; Modify: `src/Miller.Server/Tools/SearchRouteExecutor.cs`, `src/Miller.Server/Tools/SearchTool.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Split the symbol search route so candidate generation returns typed candidates and rendering is a pure function of them, without changing a single output byte. Only the symbols route needs the seam in P2 (content/text/regions/markers routes are not fusion targets).

**Approach:** Write the golden tests FIRST against current behavior, commit-worthy on their own. Then refactor behind them. `SymbolCandidate` lives in Miller.Core (zero I/O). Keep `SearchRouteExecutionResult` shape unchanged for callers (CliDispatch.cs:320, SearchTool.cs:144).

**Acceptance criteria:**
- [x] Golden parity tests cover compact + json for ≥12 query shapes and pass byte-identical pre/post refactor
- [x] RunSymbols internally flows typed candidates; rendering reads only the typed list
- [x] All existing SearchRouteExecutorTests pass unchanged
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode

### Task D1: Edit failure instrumentation, guidance, Unicode whitespace

**Files:**
- Modify: `src/Miller.Server/Tools/EditTool.cs`, the edit telemetry record type (locate via `Edit_ReplaceText_NoMatch_StampsNoMatchBucket`, tests/Miller.Tests/Server/EditToolTests.cs:1683), normalized-matching code (locate via EditTool's match ladder)
- Test: `tests/Miller.Tests/Server/EditToolTests.cs`

**Interfaces:**
- Consumes: existing `match_mode=auto` ladder (exact→normalized→fuzzy), existing failure-bucket stamping.
- Produces: `edit_failure_reason` stamped on EVERY failure path (design §7.1 — audit all paths, not just no-match); Miller version stamped on edit telemetry records; error messages that carry the recovery action at the point of failure (scope disambiguation, mode suggestion); `Normalized` matching treating Unicode spaces (U+00A0 NBSP, U+2000–U+200A, U+202F, U+205F, U+3000) and form feed as whitespace.

**Contract inputs:** Design §7 items 1–3. Telemetry stays enum/counter-only — no query text. Existing partial instrumentation from `docs/plans/2026-07-12-telemetry-diagnosis-hardening.md` Task 1: audit which failure paths still stamp nothing before adding.

**File ownership:** Modify: `src/Miller.Server/Tools/EditTool.cs`, edit-related telemetry records, `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` edit description only if within budget; Test: `tests/Miller.Tests/Server/EditToolTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Close design §7 items 1–3: complete failure-reason coverage, version-stamped telemetry, recovery-action error messages, Unicode-aware normalized whitespace. Description/guidance edits must respect the ADR-0001 budgets (edit description ≤900 chars; run AgentInstructionsTests).

**Acceptance criteria:**
- [x] A test enumerates every replace_text failure path and asserts a non-empty `edit_failure_reason` on each
- [x] Edit telemetry records carry Miller version
- [x] NBSP/Unicode-space/form-feed variants match under `normalized` (tests per §7.3 list)
- [x] Failure messages name the concrete next action; AgentInstructionsTests green
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode

### Task E1: MinHash/LSH near-duplicate analyzer

**Files:**
- Create: `src/Miller.Core/Analysis/NearDuplicateAnalyzer.cs`, `tests/Miller.Tests/Core/NearDuplicateAnalyzerTests.cs`
- Modify: `src/Miller.Server/Tools/MetricsTool.cs` (RunClones, src/Miller.Server/Tools/MetricsTool.cs:120), `src/Miller.Server/Cli/CliDispatch.cs` metrics verb only if flag plumbing requires
- Test: `tests/Miller.Tests/Server/MetricsToolTests.cs`, `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`

**Interfaces:**
- Consumes: symbol bodies/body hashes as `CloneGroupReader` reads them today (same source columns; discover exact query via `CloneGroupReader`, src/Miller.Indexing/CloneGroupReader.cs:5).
- Produces: `NearDuplicateAnalyzer.FindGroups(inputs, options) -> IReadOnlyList<NearDuplicateGroup>` (group members + Jaccard-estimate similarity), pure and deterministic; `metrics clones` output gains `kind=near_duplicate` groups with similarity alongside existing exact groups (exact groups byte-stable when no near-duplicates exist); JSON per `metrics-json-v1.md` additive rules.

**Contract inputs:** Design §6.4 metrics-clones. Fixed normalization (identifier/whitespace canonicalization), fixed shingle size, fixed seed set, fixed LSH band/row params — all constants in Miller.Core, documented in the analyzer's doc comment. Exact `CloneGroupReader` is untouched.

**File ownership:** Create: `src/Miller.Core/Analysis/NearDuplicateAnalyzer.cs`, `tests/Miller.Tests/Core/NearDuplicateAnalyzerTests.cs`; Modify: `src/Miller.Server/Tools/MetricsTool.cs`, `tests/Miller.Tests/Server/MetricsToolTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Token-shingle MinHash/LSH Type-2 near-duplicate detection surfaced through `metrics clones` as a new group kind. Pure logic in Miller.Core; MetricsTool wires data in.

**Acceptance criteria:**
- [x] Analyzer is deterministic across runs and platforms (seeded, no Random/time), proven by a repeat-run test
- [x] Detects Type-2 clones (renamed identifiers/changed literals) in fixture bodies; exact duplicates stay in exact groups, not double-reported
- [x] `metrics clones` compact + JSON show near_duplicate groups with similarity; existing exact-group output unchanged when analyzer finds nothing
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode

### Task B1: MILLER_SEMANTIC activation, VectorSidecar skeleton, off-guarantee

**Files:**
- Create: `src/Miller.Indexing/SemanticActivation.cs`, `src/Miller.Indexing/VectorSidecar.cs`, `tests/Miller.Tests/Indexing/SemanticActivationTests.cs`, `tests/Miller.Tests/Indexing/VectorSidecarTests.cs`, `tests/Miller.Tests/Indexing/SemanticOffGuaranteeTests.cs`
- Modify: `src/Miller.Server/Hosting/WorkspaceFactsAssembler.cs` + the render seam that carries sidecar facts (mirror how search-sidecar facts flow; discover via WorkspaceFactsAssemblerTests)
- Test: `tests/Miller.Tests/Server/WorkspaceFactsAssemblerTests.cs`

**Interfaces:**
- Consumes: `SymbolSearchSidecar` (src/Miller.Indexing/SymbolSearchSidecar.cs:12) as the exact structural pattern; vectors-v1 §File placement and activation + §Status vocabulary.
- Produces: `SemanticActivation.FromEnvironment()` → `off | shadow | on` (`0` aliases `off`); `VectorSidecar` with `EnvVar`, `Disabled` singleton, `PathFor(workspaceRoot)`, `TryOpen`, `OpenRequired` (fail-visible message includes "run `miller workspace refresh`"), no real vec0 yet — B2 adds the store; `vectors:` status line in workspace status/health with vocabulary `disabled | unavailable (reason)` for now (later states land with B2–B6).

**Contract inputs:** vectors-v1 §File placement and activation (the off-guarantee definition, verbatim), §Status vocabulary. Off means: no open, no create, no stat, no `vectors.gen-*.db` enumeration, status `disabled` derived without filesystem access.

**File ownership:** Create: `src/Miller.Indexing/VectorSidecar.cs`, `src/Miller.Indexing/SemanticActivation.cs`, `tests/Miller.Tests/Indexing/VectorSidecarTests.cs`, `tests/Miller.Tests/Indexing/SemanticOffGuaranteeTests.cs`; Modify: `src/Miller.Server/Hosting/WorkspaceFactsAssembler.cs`, render seam, their tests

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** The activation switch, the sidecar shell mirroring SymbolSearchSidecar, and the test-enforced zero-work guarantee — the contract clause everything later builds under.

**Approach:** The off-guarantee test observes the filesystem (e.g. workspace dir with a sentinel `vectors.gen-x.db` whose access would be detectable, plus asserting no vectors.db created and no path enumerated via an injected filesystem probe or directory watcher — choose the strongest observable the codebase supports). Status rendering follows the search-sidecar fact pattern exactly.

**Acceptance criteria:**
- [x] `MILLER_SEMANTIC` unset/`off`/`0` ⟹ status `disabled`, zero filesystem touches under the vectors paths (test-enforced)
- [x] `shadow`/`on` with no artifact ⟹ `unavailable (reason)` fact via WorkspaceFactsAssembler in compact + JSON
- [x] VectorSidecar mirrors SymbolSearchSidecar's surface (EnvVar/Disabled/TryOpen/OpenRequired) with fail-visible messaging
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode

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
- [x] Invalidation matrix covered by a table-driven pure test — every field × change ⟹ exactly the contract's mechanism
- [x] Scale test: create store, write/read vectors round-trip, `vec_version()` matches pin, schema matches contract shapes (column names asserted)
- [x] Mismatched `reader_compatibility` minimum ⟹ open refused with reason; mismatched `encoder_fingerprint` ⟹ not queryable, no re-embed triggered
- [x] Worker-scope verification passes; worker commits per serial-worker-commit

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

### Task B5: Shadow generations, promote, rollback, corruption recovery

**Files:**
- Create: `src/Miller.Indexing/Semantic/VectorGenerationManager.cs`, `tests/Miller.Tests/Indexing/VectorGenerationManagerTests.cs`
- Modify: `src/Miller.Indexing/Semantic/VectorStore.cs`, `src/Miller.Server/Hosting/SidecarCorruptionRecovery.cs` (per-generation recovery registration)

**Interfaces:**
- Consumes: vectors-v1 §Shadow generations and rollback (generation tag, compatible vs incompatible promotes, lifecycle, GC rules), §Corruption recovery; `FullRebuildPromotion` as the promote-pattern reference.
- Produces: shadow build at `vectors.db.rebuild` → atomic promote; incompatible promote retains superseded generation as self-contained `vectors.gen-<tag>.db`; reader routing to a retained compatible generation across restarts; GC honoring the three never-delete rules (only ready generation, soak window, live compatible reader); corrupt vectors delete+rebuild without touching symbols.db.

**Contract inputs:** vectors-v1 conformance clause 6 verbatim.

**File ownership:** Create: `src/Miller.Indexing/Semantic/VectorGenerationManager.cs`, tests; Modify: `src/Miller.Indexing/Semantic/VectorStore.cs`, `src/Miller.Server/Hosting/SidecarCorruptionRecovery.cs`

**Serialization required:** Yes

**Dependency reason:** Promote/GC operate on B4's written artifact.

**What to build:** Generation lifecycle: shadow beside live, promote, retain, serve-from-retained, GC — plus corruption recovery wiring.

**Acceptance criteria:**
- [ ] Incompatible promote: old generation discoverable and queryable by an old-fingerprint reader across a process restart (Scale test)
- [ ] GC never deletes the only ready generation, an in-soak generation, or one with a registered live reader (each rule its own test)
- [ ] Corrupt vectors.db ⟹ deleted + rebuilt via recovery path; symbols.db untouched
- [ ] Worker-scope verification passes; worker commits per serial-worker-commit

### Task B6: Status/health facts, telemetry, canary-contract plumbing

**Files:**
- Modify: `src/Miller.Server/Hosting/WorkspaceFactsAssembler.cs`, the status/health render + JSON seams, telemetry record types + writer
- Test: `tests/Miller.Tests/Server/WorkspaceFactsAssemblerTests.cs`, telemetry contract tests

**Interfaces:**
- Consumes: cursor/generation/session facts from B2–B5; vectors-v1 §Status vocabulary (full compact vocabulary + JSON-only exact revisions/coverage/fingerprints); `canary-telemetry-v1.md` field set.
- Produces: full `vectors:` status vocabulary in compact (`ready | ready (updating; N files pending) | building N% (not queryable) | unavailable (reason) | incompatible | circuit-open | disk-blocked | disabled`) reporting the laggier cursor; exact fields in `workspace status --json` / `workspace health --json` (additive per `workspace-status-v1.md`/`workspace-health-v1.md`); telemetry: semantic participation/reason fields + canary plumbing (assignment unit, query-class enum, experiment/arm id, opaque result ids, success event) recorded but with NO experiment activation — fields exist and are exercised by tests, arm assignment is a constant `control` until P5.

**Contract inputs:** canary-telemetry-v1 verbatim field names; privacy rule — persisted telemetry is enum/counter-only, proven query-free by a test.

**File ownership:** Modify: `src/Miller.Server/Hosting/WorkspaceFactsAssembler.cs`, render seam, telemetry records + writer, their tests

**Serialization required:** Yes

**Dependency reason:** Reports cursor/generation facts produced by B2–B5; canary fields ride the telemetry seam last to avoid churn.

**What to build:** The observability half of lane b: everything a P4 shadow rollout needs to be diagnosable, and the canary schema ready so P5 flips a switch rather than migrating telemetry.

**Acceptance criteria:**
- [ ] Every status vocabulary state renderable and covered by a test; compact shows laggier cursor; JSON carries exact revisions/fingerprints
- [ ] Canary-contract fields present per canary-telemetry-v1 with constant control arm; telemetry proven query-text-free by test
- [ ] Existing status/health JSON consumers unaffected (additive only; contract tests green)
- [ ] Worker-scope verification passes; worker commits per serial-worker-commit

### Task D2: Fuzzy policy replay evaluation + stale-target convergence wait

**Files:**
- Modify: `src/Miller.Server/Tools/EditTool.cs` (plan-time stale_target bounded wait mirroring the 2.5s apply-path wait), fuzzy matcher policy constants/code (locate via the fuzzy rung of the match ladder)
- Create: `docs/findings/2026-07-20-edit-fuzzy-policy-replay.md`
- Test: `tests/Miller.Tests/Server/EditToolTests.cs`

**Interfaces:**
- Consumes: D1's `edit_failure_reason` instrumentation; historical edit-failure telemetry (telemetry.db export) as the replay corpus where retrievable, plus synthesized fixture failures for the policy tests.
- Produces: plan-time stale-target bounded wait + retry; a proposed fuzzy policy (snippet cap / distance ceiling) with before/after replay numbers in the findings doc; the policy change itself ONLY if replay shows strict improvement (otherwise document and keep current policy).

**Contract inputs:** Design §7 items 4–5. Current policy facts: 160-char snippet cap, distance ceiling 3, zero historical fuzzy successes.

**File ownership:** Modify: `src/Miller.Server/Tools/EditTool.cs`, fuzzy matcher policy code; Create: `docs/findings/2026-07-20-edit-fuzzy-policy-replay.md`; Test: `tests/Miller.Tests/Server/EditToolTests.cs`

**Serialization required:** Yes (after D1)

**Dependency reason:** Needs D1's failure-reason instrumentation to build the replay corpus.

**What to build:** The judgment half of the edit lane: measure, then change policy only on evidence.

**Acceptance criteria:**
- [x] Plan-time stale_target waits (bounded) and succeeds when index converges within budget; still fails cleanly after
- [x] Findings doc reports replay methodology + numbers; any policy change is gated on those numbers
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode

### Task E2: near_duplicate_group_count history metric + dashboard + report

**Files:**
- Modify: the history snapshot writer (per `docs/contracts/metrics-history-v1.md`; locate via `metrics history` CLI verb), `src/Miller.Dashboard/DashboardData.cs` (ReadLocalMetricsPanel, src/Miller.Dashboard/DashboardData.cs:982), report rollup (`miller report`) count surface
- Test: history writer tests, dashboard data tests, report tests alongside existing patterns

**Interfaces:**
- Consumes: E1's `NearDuplicateAnalyzer` output via the same data path MetricsTool uses.
- Produces: append-only metric name `near_duplicate_group_count` in history.db snapshots; dashboard trend sparkline (rides the existing trend mechanism — design says "dashboard sparkline free"); report rollup count. Count-level only per ADR-0002 — no per-symbol detail on the dashboard.

**Contract inputs:** `metrics-history-v1.md` (metric names are append-only); ADR-0002 dashboard boundary.

**File ownership:** Modify: history snapshot writer, `src/Miller.Dashboard/DashboardData.cs`, report rollup; tests alongside each

**Serialization required:** Yes (after E1)

**Dependency reason:** Consumes E1's analyzer output.

**What to build:** Trend surfacing for the new metric through the existing history/dashboard/report machinery.

**Acceptance criteria:**
- [ ] Snapshots record `near_duplicate_group_count`; `miller metrics history` surfaces it per contract
- [ ] Dashboard shows the trend at count level only; report rollup includes the count
- [ ] Worker-scope verification passes and the change is handed to the lead per commit mode
