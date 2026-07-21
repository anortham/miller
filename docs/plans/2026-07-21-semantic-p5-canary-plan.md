# P5 Canary Stage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Make the randomized-holdout canary of `docs/contracts/canary-telemetry-v1.md` fully runnable (assignment, arm serving, attribution hashes, identifier shadow population, aggregate export, local gate calculator), add a simple encoder-model swap seam for later model comparison, and close the Miller-side pre-P5 follow-ups.

**Architecture:** Canary orchestration lives entirely in `SearchTool` + the existing `CanaryTelemetry` stamping layer; the per-call arm decision rides the existing `SearchRouteExecutionRequest.FusionArm` seam, so control stays byte-identical lexical and treatment is exactly the production hybrid path with only the `MILLER_SEMANTIC` mode gate bypassed. The Indexing layer gains typed query diagnostics (enum fallback reason, backend, warmth, latencies, generation identity) that the Server layer maps onto the frozen contract strings. Analysis is CLI-only: a `telemetry canary` verb for the frozen export envelope and the local-authoritative gate.

**Tech Stack:** .NET 10, SQLite (`~/.miller/telemetry.db`), existing sidecar/vec0 stack. No new dependencies.

**Architecture Quality:** Approved shape: no decorator over `ISymbolLookupIndex`; canary logic confined to `SearchTool`, `CanaryTelemetry`, and a new pure classifier + gate math in `Miller.Core`. Main risk: accidentally perturbing the lexical/control path — every task that touches the search route must keep the P3 golden-output/determinism tests green. Layering rule: `Miller.Indexing` must not reference `Miller.Server.Telemetry`; diagnostics use an Indexing-local enum, mapped in Server.

**Out of scope (gated on canary evidence, per design §10 P5):** the §6.4 per-tool integrations (context/impact/inspect/trace/dead-code display), the default-on flip, guidance/skill/dashboard updates, and release notes. Also out of scope: the sidecar RSS peak ceiling check (sidecar-repo work) and the model comparison itself — this plan only builds the swap seam; the "available models and specialties" comparison list is a later, separately-run evaluation.

## Global Constraints

- `docs/contracts/canary-telemetry-v1.md` is **frozen**: no field additions/renames/repurposing; enum values exhaustive; follow the Field Reference "Written when" conditions literally; absent-vs-zero is a guarantee (a field whose condition does not hold is omitted, never zeroed).
- `MILLER_SEMANTIC=off` is a permanent zero-side-effect guarantee and outranks `MILLER_SEMANTIC_CANARY` (contract §Activation). `MILLER_SEMANTIC_CANARY`: `off|on`, `0`/`1` aliases, unknown → off + one startup log (already in `CanaryActivation`).
- Instrumented surfaces (frozen): `search` with `op` in `auto`, `text`, `symbol`, `content`. Nothing else.
- Control arm and every lexical path stay **byte-identical** to pre-program behavior; the P3 golden-output/determinism contract tests must stay green untouched.
- Semantic failure never converts a lexical success into a tool error (design §6.5); shadow-population failures are silent-to-user, loud-to-telemetry.
- Privacy: persisted telemetry never carries query text, source text, absolute paths, or workspace roots. Log lines (not stored reasons) may name workspace-relative paths.
- No new MCP tools and no new MCP tool parameters; new surfaces are CLI-only (`miller telemetry canary`). No ServerInstructions or tool-description growth (`AgentInstructionsTests` budgets unchanged).
- Build: `dotnet build Miller.slnx -c Release` 0 warnings / 0 errors. Fast suite stays pure and fast; any test loading vec0 or spawning a real sidecar follows the Scale trait + `SqliteVecEnvironment` collection rules (P4 lesson: three classes raced on the shared `.tools/vec0` file).
- Test fixtures that fake extractor/sidecar data must carry contract-faithful metadata (see `feedback_contract_faithful_fixtures`).
- Tests get zero comments; no narration comments in code.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` (Testing section) + `scripts/test.sh`.

**Worker red/green scope:** `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~<TestClassName>"` for the classes the task adds/changes (seconds).

**Worker ceiling:** `scripts/test.sh` (fast suite, ~15s wall). Workers do not run the scale suite.

**Worker gate invariant:** each task's acceptance criteria list the behavior its tests prove; the fast suite proves no regression in the pure/contract layer.

**Lead affected-change scope:** `scripts/test.sh` + `dotnet build Miller.slnx -c Release` after each accepted batch.

**Branch gate:** `scripts/test.sh all` (fast + scale) + Release build 0W/0E before merge. Scale suite skips (not fails) if `.tools/julie-extract` is missing — it is present in this worktree.

**Escalation triggers:** any change under `src/Miller.Server/Hosting/VectorConvergeService*`, `src/Miller.Indexing/Semantic/`, or `VectorStore`/vec0 handling ⟹ run the scale suite for that batch, not just at branch gate.

**Assigned verification failure:** workers stop and report when assigned verification fails, unless the task explicitly says to update that gate.

**Verification ledger:** record invariant, command, scope label, commit SHA, result, timestamp in the run ledger (`.memories/` per goldfish convention). Reuse a passing entry for the same HEAD rather than rerunning an expensive gate.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Encoder registry + swap seam | Batch 1 | `src/Miller.Indexing/Semantic/MillerSemanticContract.cs`, `src/Miller.Indexing/VectorSidecar.cs`, `src/Miller.Server/Hosting/VectorConvergeService.cs` (encoder refs only), `src/Miller.Indexing/Semantic/SemanticEmbeddingSession.cs` (FindPin only), `tests/Miller.Tests/Indexing/SemanticEncoderSelectionTests.cs` | No | None - safe parallel batch. |
| Task 2: CLI `telemetry canary` verb (export + gate) | Batch 1 | `src/Miller.Server/Cli/CliDispatch.cs` (Telemetry method + help), `src/Miller.Core/Telemetry/CanaryGateMath.cs` (new), `src/Miller.Server/Telemetry/CanaryLedgerReader.cs` (new), `src/Miller.Server/Telemetry/CanaryExport.cs` (new), `src/Miller.Server/Telemetry/CanaryGateReport.cs` (new), `tests/Miller.Tests/Core/CanaryGateMathTests.cs`, `tests/Miller.Tests/Telemetry/CanaryExportTests.cs`, `tests/Miller.Tests/Telemetry/CanaryGateReportTests.cs` | No | None - safe parallel batch. |
| Task 3: Semantic query diagnostics | Batch 2 | `src/Miller.Indexing/Semantic/SemanticSearchArm.cs`, `src/Miller.Indexing/Semantic/SemanticEmbeddingSession.cs` (warmth/backend exposure), `src/Miller.Server/Tools/SearchRouteExecutor.cs` (SemanticSymbolFusionArm + port identity), `src/Miller.Server/Tools/SearchTool.cs` (SemanticTextArm diagnostics only), `tests/Miller.Tests/Indexing/SemanticQueryDiagnosticsTests.cs` | Yes (after Task 1) | Task 1 also edits `SemanticEmbeddingSession.cs`; serialized to avoid same-file conflict. |
| Task 4: Converge follow-ups + status hint | Batch 2 | `src/Miller.Server/Hosting/VectorConvergeService.cs` (post-Task-1), `src/Miller.Indexing/Semantic/VectorConvergePlanner.cs`, `src/Miller.Core/Workspace/WorkspaceRender.cs`, `tests/Miller.Tests/Server/VectorConvergeServiceTests.cs`, `tests/Miller.Tests/Indexing/VectorConvergePlannerTests.cs`, `tests/Miller.Tests/Core/WorkspaceRenderTests.cs` | Yes (after Task 1) | Task 1 edits two encoder-ref lines in `VectorConvergeService.cs`; serialized to avoid same-file conflict. Parallel-safe with Task 3 (disjoint files). |
| Task 5: Symbol-route canary serving + stamping | Batch 3 | `src/Miller.Server/Tools/SearchTool.cs`, `src/Miller.Server/Telemetry/CanaryTelemetry.cs` (ResolveArm flip), `src/Miller.Core/Search/CanaryQueryClassifier.cs` (new), `tests/Miller.Tests/Core/CanaryQueryClassifierTests.cs`, `tests/Miller.Tests/Server/CanarySearchTests.cs` | Yes | Consumes Task 3 diagnostics; edits files owned by Tasks 2/3 in earlier batches. |
| Task 6: Content-route canary | Batch 4 | `src/Miller.Server/Tools/SearchTool.cs` (content route), `tests/Miller.Tests/Server/CanaryContentSearchTests.cs` | Yes | Same file as Task 5 (`SearchTool.cs`); builds on its orchestration helper. |
| Task 7: Identifier shadow population | Batch 5 | `src/Miller.Server/Tools/SearchTool.cs` (post-serve hook), `src/Miller.Server/Telemetry/CanaryTelemetry.cs` (StampShadow), `tests/Miller.Tests/Server/CanaryShadowPopulationTests.cs` | Yes | Same files as Tasks 5/6; needs the served lexical list from Task 5's finalize seam. |
| Task 8: Docs, runbook, closeout | Batch 6 | `docs/findings/2026-07-21-p5-canary-runbook.md` (new), `docs/README.md`, `README.md` (env/config section) | Yes | Documents behavior shipped by Tasks 1–7. |

Commit mode: Batch 1 and Batch 2 run **parallel-lead-commit** (two workers per batch); Batches 3–6 run **serial-worker-commit**.

---

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
- [x] `MILLER_SEMANTIC_MODEL=bge-small-en-v1.5-f32` resolves the bge pin; its `PinnedIdentity` differs from qwen3's and `ClassifyChange` yields `ShadowRebuild`.
- [x] Unset/unknown env values resolve `DefaultEncoder`; unknown logs one warning.
- [x] `VectorConvergeService`, `VectorSidecar`, and the `semantic prepare` path all consume the resolved pin (no remaining direct `DefaultEncoder` reads outside `MillerSemanticContract` and tests — guard with a source-scan or reference test).
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode.

### Task 2: `miller telemetry canary` — frozen export + local gate calculator

**Files:**
- Create: `src/Miller.Core/Telemetry/CanaryGateMath.cs`
- Create: `src/Miller.Server/Telemetry/CanaryLedgerReader.cs`
- Create: `src/Miller.Server/Telemetry/CanaryExport.cs`
- Create: `src/Miller.Server/Telemetry/CanaryGateReport.cs`
- Modify: `src/Miller.Server/Cli/CliDispatch.cs` (`Telemetry` :1091 — relax the `!= "export"` guard :1098, add `canary` op; help line near :2733)
- Test: `tests/Miller.Tests/Core/CanaryGateMathTests.cs`, `tests/Miller.Tests/Telemetry/CanaryExportTests.cs`, `tests/Miller.Tests/Telemetry/CanaryGateReportTests.cs`

**Interfaces:**
- Consumes: `CanaryTelemetry` constants/enums (`CanaryArm`, `CanaryEligibility`, `CanaryLatencyBucket`, `CanaryAssignment.Bucket`), read-only DB open pattern from `TelemetryExportReader.OpenReadOnly` (:55), row-iteration pattern from `TelemetryOnboardingReader.ReadEvents` (:159) + `MetadataString` (:331), `ctx.TelemetryDbPath`.
- Produces: CLI verbs `miller telemetry canary --json [--from YYYY-MM-DD --to YYYY-MM-DD]` (frozen export envelope, default window last 30 days) and `miller telemetry canary --gate [--json]` (local gate report). `CanaryGateMath` (pure, `Miller.Core`): `WelchInterval(IReadOnlyList<double> a, IReadOnlyList<double> b) : (double Lower, double Upper, double Effect)` (95% two-sided, Welch–Satterthwaite df); `NearestRankP95(IReadOnlyList<long> ascending) : long` (index `ceil(0.95×n)`, 1-based, no interpolation); `BucketedP95(IReadOnlyDictionary<string,int> bucketCounts, int calls) : string`.

**Contract inputs:** Contract §Aggregate Export (envelope shape verbatim, `unit_id` = first 12 hex of the assignment digest, <5-call units suppressed with `suppressed_unit_count`, count maps omit zeros, `units` ordered by `(utc_date, query_class, unit_id)` for byte-identical re-export, `total_latency_bucket_counts` from raw `duration_ms` per the `latency_bucket` ladder, **no hashes / workspace ids / raw ms in the export**); §Frozen analysis parameters (per-unit rates, min 5 calls/unit, min 30 units/arm else "underpowered — not a pass", Welch 95% lower bound > 0, warm-treatment vs all-control nearest-rank p95 ≤ 1.20×, 100-row minimums else indeterminate, shadow margins: per-unit `top1_changed` 95% upper ≤ 0.05 AND mean `overlap_at_10` 95% lower ≥ 8.0, min 30 shadow units); §Matching rule (attribution: `F.tool='inspect'` or `content`+`op='read'`, `outcome='ok'`, hash in any of the three arrays, `0 < F.ts−C.ts ≤ 600s`, latest-C rule, one follow-up max per row); cohort = exact `miller_version` set (never string ≥) — `--gate` groups by exact version strings present and reports per-set. Rows lacking `miller_version` or with `canary_contract_version != 1` are excluded.

**File ownership:** `src/Miller.Server/Cli/CliDispatch.cs` (Telemetry method + help), `src/Miller.Core/Telemetry/CanaryGateMath.cs`, `src/Miller.Server/Telemetry/CanaryLedgerReader.cs`, `src/Miller.Server/Telemetry/CanaryExport.cs`, `src/Miller.Server/Telemetry/CanaryGateReport.cs`, the three test files.

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** The only sanctioned off-box surface (export) and the local-authoritative gate. Both read `tool_telemetry` rows and parse `canary_*` metadata; neither needs the serving path to exist — tests seed a temp telemetry DB via `TelemetryLedger` with hand-built rows (contract-faithful metadata: real key names, real enum values, real digests via the same SHA-256 derivation).

**Approach:** `CanaryLedgerReader` yields typed rows (columns + parsed canary metadata incl. the three hash arrays); `CanaryExport` aggregates units; `CanaryGateReport` computes attribution then per-unit rates then the three clauses, and renders a human summary (per clause: value, threshold, pass/fail/underpowered/indeterminate) plus `--json`. Welch t-quantile: implement the inverse-t via a small deterministic approximation (e.g. Hill's algorithm) in `CanaryGateMath` with unit tests against known values (df=10 t=2.228, df=30 t=2.042, df→∞ 1.960 within 1e-3). Gate exit code: 0 = computed (regardless of pass/fail; the verdict is in the output), nonzero only for I/O/usage errors.

**Acceptance criteria:**
- [x] Export envelope matches the contract example shape byte-for-byte on ordering/field names; suppression and truncation-free counters verified; re-export of an unchanged window is byte-identical.
- [x] Gate report reproduces the contract's six conformance attribution cases (bare/qualified/path/top-level/deeper-spelling/double-count) from seeded rows.
- [x] Underpowered (<30 units) and indeterminate (<100 latency rows) paths report as such and never as a pass; Welch interval + nearest-rank p95 unit-tested against hand-computed values.
- [x] `miller telemetry export` behavior unchanged.
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode.

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
- [x] Every abstention path yields the mapped `SemanticFallbackKind` (table-driven test over the fake sidecar/store fixtures).
- [x] A served arm call yields `Fallback=None`, non-null `EmbedMs`/`KnnMs`, backend, warmth, identity, fusion profile.
- [x] No change to any rendered search output (fast suite green, P3 determinism tests untouched).
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode. (Fix round 1: typed `TimedOut` propagation makes `EmbedTimeout` producible; VectorsStale/VectorsBuilding/DiskBlocked stay eligibility-layer facts for Task 5.)

### Task 4: Converge follow-ups — starvation retry wake, incremental disk gate, deferred-source log, status hint

**Files:**
- Modify: `src/Miller.Server/Hosting/VectorConvergeService.cs` (wake loop `ExecuteAsync` :284/:296, `DrainCursorAsync` incremental branch :596-631, deferral consume :563-576)
- Modify: `src/Miller.Indexing/Semantic/VectorConvergePlanner.cs` (hold plan :201-210 — only if a hold flag needs surfacing; prefer no change)
- Modify: `src/Miller.Core/Workspace/WorkspaceRender.cs` (`VectorsLabel` :321, `VectorsReadyLabel` :333)
- Test: extend `tests/Miller.Tests/Server/VectorConvergeServiceTests.cs`, `tests/Miller.Tests/Core/WorkspaceRenderTests.cs` (or the render tests' actual home — locate with Miller before editing)

**Interfaces:**
- Consumes: existing `VectorConvergeSignal` (capacity-1 coalescing semaphore), `DiskGate` delegate (:181) + `ProductionDiskGate` (:361) + `RefuseForDisk`/`BlockedForDisk` (:848/:860), P4's `converge_pause_state` disk-blocked facts, `VectorSidecarFacts` (shadow-rebuild-in-progress indicator — verify exact field via `WriteVectorsJson` :521 before rendering).
- Produces: (a) a bounded held-cursor retry: when a drain ends with a held cursor (`AdvanceTo=0`/hold reason), schedule exactly one delayed signal re-stamp (default 5 minutes, test-injectable delay/scheduler; coalesces — no stacking retries; canceled by a real wake). (b) The incremental branch consults `state.DiskGate` before `EmbedAsync`/`Commit`, mirroring shadow-path semantics: blocked ⟹ record disk-blocked pause state, hold the cursor, no partial write, no hard fail. (c) An INFO log naming the deferred workspace-relative paths at the deferral consume site (stored hold reason stays path-free). (d) Compact status renders `ready (rebuilding)` when state is ready and a shadow rebuild is in flight (JSON untouched — it already carries the state).

**Contract inputs:** The stored hold reason string format must not change (status surfaces show it). Disk-blocked semantics must match the shadow path's pause facts so `workspace status`/health render it identically.

**File ownership:** `src/Miller.Server/Hosting/VectorConvergeService.cs` (post-Task-1), `src/Miller.Indexing/Semantic/VectorConvergePlanner.cs`, `src/Miller.Core/Workspace/WorkspaceRender.cs`, the listed test files.

**Serialization required:** Yes (after Task 1)

**Dependency reason:** Task 1 edits two encoder-ref lines in `VectorConvergeService.cs`; serialized to avoid same-file conflict. Parallel-safe with Task 3 (disjoint files).

**What to build:** The three Miller-side pre-P5 reliability fixes plus the status polish, red-first (each fix starts from a failing test reproducing the P4 dogfood finding: quiet-workspace starvation, ungated incremental write under a blocked disk, plain `ready` during rebuild).

**Approach:** For the retry wake, follow the existing fake/injectable patterns in `VectorConvergeServiceTests` (no real `Task.Delay` in tests). Escalation trigger applies: run the scale suite for this batch.

**Acceptance criteria:**
- [x] A held cursor on a quiet workspace re-drains after the retry delay without an index-convergence stamp; a real wake cancels/absorbs the pending retry; no retry storm (at most one pending).
- [x] Incremental drain under a blocked disk gate: no vectors.db write, cursor held, disk-blocked pause state recorded; unblocking resumes.
- [x] Deferral logs one INFO line naming the deferred paths; stored reason unchanged.
- [x] Compact status shows `ready (rebuilding)` during a shadow rebuild; plain `ready` otherwise; JSON output unchanged. (Lead ruling: hint keys on the cross-wake shadow-pending cursor marker; the plan's assumed in-flight JSON field does not exist. Transient single-wake in-flight window recorded as a follow-up — `vectors.db.rebuild` disk probe.)
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode.

### Task 5: Symbol-route canary — assignment flip, arm serving, facts, result hashes

**Files:**
- Modify: `src/Miller.Server/Telemetry/CanaryTelemetry.cs` (`CanaryAssignment.ResolveArm` :166 — flip to `bucket < 50 ? Control : Treatment` per the doc comment)
- Modify: `src/Miller.Server/Tools/SearchTool.cs` (orchestration in `Search` around :400-467; `FusionArm` injection point :430; `RenderSymbolCandidates` :1130-1183 — expose the served page slice; rescue-kind copy at the :450-454 site)
- Create: `src/Miller.Core/Search/CanaryQueryClassifier.cs`
- Test: `tests/Miller.Tests/Core/CanaryQueryClassifierTests.cs`, `tests/Miller.Tests/Server/CanarySearchTests.cs` (new)

**Interfaces:**
- Consumes: `CanaryActivation.FromEnvironment()`, `CanaryTelemetry.Stamp` + `CanaryCallFacts`/`CanaryServedResult` (all exist), Task 3's `SemanticQueryDiagnostics`, `SemanticQueryPolicy.Route`, `RrfFusion`/`FusedCandidate` ranks, `VectorSidecar` state probe (reuse the CLI probe approach at CliDispatch :559-566 / `VectorSidecar.TryOpen`), `TelemetryContext.Current` scope + its row timestamp (align `CanaryCallFacts.UtcDate` with the scope's persisted `ts`).
- Produces: `CanaryQueryClassifier.Classify(string op, string? query, SemanticQueryRoute route) : string` returning exactly one of the six frozen `query_class` values. Mapping (deterministic, fully test-pinned): reason `Empty`/`Short` → `short_token`; `IdentifierLike`/`CodeSyntax` → `identifier`; `PathLike` → `path`; `Prose` → `docs_like` when `op == "content"` or the query contains a word from a small fixed docs-vocabulary set (`readme, docs, documentation, config, configuration, guide, install, setup, changelog, license, tutorial, faq`), else `prose`; `AmbiguousWeakLexical`/`AmbiguousStrongLexical` → `mixed`. Also produces the per-call orchestration helper in `SearchTool` that Tasks 6/7 reuse: computes eligibility ladder → assignment → picks `FusionArm` (treatment ⟹ production `SemanticSymbolFusionArm` with the mode gate bypassed — treatment must behave exactly like `MILLER_SEMANTIC=on`; control ⟹ null) → assembles `CanaryCallFacts` → `Stamp`s on the ambient scope. And the finalize seam: `RenderSymbolCandidates` (or an overload) additionally returns the served page slice so served-result hashes cover exactly the rendered page; parent names for the ≤10 served rows resolved at stamp time via `index.FindBySymbolId(SymbolId).ParentId` → parent's `Name` (one-level `Parent.Member` only).
- Eligibility ladder order (first match wins): canary off ⟹ no keys at all; op outside {auto,text,symbol,content} ⟹ `ineligible_surface`; `MILLER_SEMANTIC=off` ⟹ `ineligible_semantic_disabled`; query class ∉ {prose,docs_like,mixed} ⟹ `ineligible_query_class`; no artifact / building / downloading / disk-blocked ⟹ `ineligible_vectors_unavailable`; fingerprint mismatch ⟹ `ineligible_vectors_incompatible`; circuit open ⟹ `ineligible_circuit_open`; foreign-workspace read with no ready generation ⟹ `ineligible_cross_workspace_no_generation`; else `eligible`.

**Contract inputs:** Contract §Assignment (unit = workspace_id × utc_date × query_class; bucket<50 = control), §Field Reference write conditions (literal), §Ineligible calls (ineligible rows record arm/eligibility/query_class and nothing else semantic; served behavior byte-identical lexical). `query_class` note: the classifier input route must be computed with `LexicalEvidence.None` for classification purposes (class must be recomputable offline from the query alone; evidence only affects the *treatment arm's* internal hybrid/lexical decision, never the class or the assignment). Treatment rows where the policy's evidence check kept the call lexical: `fallback_reason=none`, semantic counters absent (the arm didn't run) — representable per the field table.

**File ownership:** `src/Miller.Server/Tools/SearchTool.cs`, `src/Miller.Server/Telemetry/CanaryTelemetry.cs` (ResolveArm flip), `src/Miller.Core/Search/CanaryQueryClassifier.cs`, the two test files.

**Serialization required:** Yes

**Dependency reason:** Consumes Task 3 diagnostics; edits files owned by Tasks 2/3 in earlier batches.

**What to build:** The experiment goes live on the symbol route (ops auto/text/symbol). With canary off: zero canary keys, zero added work, byte-identical behavior (test-enforced). With canary on: every instrumented call records the contract row; eligible units split 50/50; treatment serves the production hybrid path; control and all ineligible calls serve today's lexical path byte-identically.

**Approach:** TDD with the fake sidecar/store fixtures from P2/P3 tests (contract-faithful). Include the contract's six attribution conformance cases at the stamping level (served-result arrays produce the exact digests the matching rule expects — shared fixture with Task 2's gate tests if convenient). Auto-op rescue: copy the existing `auto_rescue_kind` value into `canary_rescue_kind` (map `rescue==null` to `none`; keep `unavailable` as-is).

**Acceptance criteria:**
- [x] Canary off ⟹ no `canary_*` keys and byte-identical output (golden test).
- [x] Eligible call: arm from the frozen derivation (test vectors pin bucket values for fixed inputs); control serves lexical byte-identical; treatment serves fused output identical to `MILLER_SEMANTIC=on` for the same fixture.
- [x] Facts written per the field table: counters, fallback/backend/warmth/latency buckets, identity fields, the three hash arrays + shared truncation flag (11-result fixture proves the cap and the flag).
- [x] `CanaryQueryClassifier` table-driven test pins all six classes incl. the docs-vocabulary set and `op=content` promotion.
- [x] Ineligible rows record exactly arm/eligibility/query_class (+ contract/experiment/assignment/policy version keys) and nothing else.
- [x] Worker-scope verification passes and the change is committed per commit mode. (Fix round 1: mirrored pipeline unified into `SearchRouteExecutor.RunSymbolsCore`; `ExecuteSymbols` deleted.)

### Task 6: Content-route canary

**Files:**
- Modify: `src/Miller.Server/Tools/SearchTool.cs` (content route — `RunContentCorpus` :1252 and its dispatch site)
- Test: `tests/Miller.Tests/Server/CanaryContentSearchTests.cs` (new)

**Interfaces:**
- Consumes: Task 5's orchestration helper and classifier; the content-route semantic arm (`SemanticTextArm`) with Task 3 diagnostics.
- Produces: `op=content` canary rows. Served-result hashes: path array only (content rows are path+line chunks, not symbols — name/qualified arrays are absent per the absent-vs-zero rule). Treatment = the P3 content-mode hybrid arm forced past the mode gate; control = lexical content search byte-identical.

**Contract inputs:** Same field table and eligibility ladder as Task 5. `query_class` for content queries still comes from `CanaryQueryClassifier` (op=content promotes prose → docs_like).

**File ownership:** `src/Miller.Server/Tools/SearchTool.cs` (content route), `tests/Miller.Tests/Server/CanaryContentSearchTests.cs`

**Serialization required:** Yes

**Dependency reason:** Same file as Task 5 (`SearchTool.cs`); builds on its orchestration helper.

**What to build:** Extend the canary to the fourth instrumented surface. The content route renders path+snippet rows outside the symbol-candidate seam, so the arm split and stamping hook into the content dispatch instead.

**Acceptance criteria:**
- [ ] Content-op canary rows carry path hashes only; name/qualified arrays absent.
- [ ] Control/off byte-identical to today's content output; treatment identical to `MILLER_SEMANTIC=on` content hybrid.
- [ ] Worker-scope verification passes and the change is committed per commit mode.

### Task 7: Identifier shadow population

**Files:**
- Modify: `src/Miller.Server/Tools/SearchTool.cs` (post-finalize shadow hook on the symbol route)
- Modify: `src/Miller.Server/Telemetry/CanaryTelemetry.cs` (add `StampShadow(TelemetryScope, CanaryShadowFacts)` + `CanaryShadowFacts` record)
- Test: `tests/Miller.Tests/Server/CanaryShadowPopulationTests.cs` (new)

**Interfaces:**
- Consumes: Task 5's orchestration (query class, eligibility probe), the production semantic arm + `RrfFusion`, the served lexical page slice from the finalize seam, `CanaryAssignment.Bucket` with `IdentifierExperimentId`.
- Produces: shadow rows per contract §Shadow Population: for `query_class=identifier` calls (ops auto/text/symbol) with canary on and semantic shadow|on, bucket under the noninferiority experiment id; sampled in when `bucket < 10`. Serve-first: lexical output is fully finalized before shadow work runs; shadow executes the hybrid arm under the same per-request embed deadline, compares against the served ranking, records ONLY: `canary_arm=shadow`, `canary_experiment_id=semantic_identifier_noninferiority_v1`, standard version/class keys, `canary_bucket`, `canary_shadow_status`, and when status=ok: `canary_shadow_overlap_at_10`, `canary_shadow_top1_changed`, `canary_shadow_lexical_top1_rank` (1–50, 0 = absent from hybrid top 50), plus `canary_encoder_fingerprint`/`canary_storage_schema`/`canary_corpus_generation` (vectors opened). Backend/warmth/latency-bucket keys are NOT written (field table: "every eligible row" — shadow rows are not eligible). Any failure ⟹ `canary_shadow_status` timeout/error/skipped and no counters; never affects the served result or the row's `outcome`.

**Contract inputs:** Contract §Shadow Population steps 1–5 verbatim; the served comparison uses the hybrid top 50 for `lexical_top1_rank` and top 10 for overlap. Neither ranking is persisted.

**File ownership:** `src/Miller.Server/Tools/SearchTool.cs` (post-serve hook), `src/Miller.Server/Telemetry/CanaryTelemetry.cs` (StampShadow), `tests/Miller.Tests/Server/CanaryShadowPopulationTests.cs`

**Serialization required:** Yes

**Dependency reason:** Same files as Tasks 5/6; needs the served lexical list from Task 5's finalize seam.

**What to build:** The non-inferiority measurement for the highest-volume query class, which can never be canary-eligible. This is the last serving-path piece; after it, every field in the contract's Field Reference is writable by some real code path.

**Approach:** Shadow work runs synchronously after finalization, bounded by the embed deadline (the row's own `duration_ms` includes it — acceptable: shadow rows are excluded from the latency gate by construction). Unsampled identifier calls record the ordinary `arm=ineligible` row (Task 5 behavior, `eligibility=ineligible_query_class`); sampled calls upgrade to the shadow row (same eligibility value, arm=shadow, hybrid-experiment keys replaced by the noninferiority experiment id).

**Acceptance criteria:**
- [ ] Sampling honors `bucket < 10` under the noninferiority experiment id (pinned test vectors).
- [ ] Ok path records exactly the shadow key set above; overlap/top1/rank values verified against a fixture with known lexical and hybrid rankings.
- [ ] Timeout/error/skipped paths record status only; served output and `outcome` provably untouched (fault-injection tests on the fake arm).
- [ ] Worker-scope verification passes and the change is committed per commit mode.

### Task 8: Docs, runbook, closeout

**Files:**
- Create: `docs/findings/2026-07-21-p5-canary-runbook.md`
- Modify: `docs/README.md` (map pointer)
- Modify: `README.md` (environment/configuration section — locate the existing env table with Miller search before editing)
- Test: none (docs); `AgentInstructionsTests` must stay green untouched (no guidance-channel changes)

**Interfaces:**
- Consumes: everything shipped in Tasks 1–7.
- Produces: the operator runbook: enabling the canary (`MILLER_SEMANTIC_CANARY=on` + `MILLER_SEMANTIC=shadow|on`), what gets recorded and where, running `miller telemetry canary --json` / `--gate`, reading underpowered/indeterminate verdicts, the 30-day retention squeeze (export before rows age out), and the model-swap how-to (`MILLER_SEMANTIC_MODEL`, the shadow-rebuild it triggers, rollback via retained generations) with the current registry (qwen3-0.6b-f16 default, bge-small-en-v1.5-f32) and a placeholder note that the model comparison list/eval is a later phase.

**Contract inputs:** Documented env vars and CLI flags must match the shipped spellings exactly. README release facts stay untouched (no release in this plan).

**File ownership:** `docs/findings/2026-07-21-p5-canary-runbook.md`, `docs/README.md`, `README.md` (env/config section)

**Serialization required:** Yes

**Dependency reason:** Documents behavior shipped by Tasks 1–7.

**What to build:** The operating documentation that makes the canary runnable by the user (and future sessions) without re-reading the frozen contract.

**Acceptance criteria:**
- [ ] Runbook covers enable → observe → export → gate → interpret, plus model swap; docs/README map updated.
- [ ] Every documented command/env var spelling verified against the shipped code.
- [ ] Worker-scope verification passes (fast suite green) and the change is committed per commit mode.
