# Task 5 — Symbol-route canary go-live (P5)

## Worktree state (verified)
- pwd: `/Users/murphy/source/miller/.claude/worktrees/worktree-semantic-p5`
- branch: `worktree-semantic-p5`
- HEAD at start: `5b7b946`

## Status: COMPLETE

The randomized-holdout canary is live on the symbol route (ops `auto`/`text`/`symbol`, plus `file` -> off-surface).
With `MILLER_SEMANTIC_CANARY` off the symbol path is the untouched pre-program code path (no keys, byte-identical).
With it on, every symbol-route call records the frozen contract row: eligible units split 50/50, treatment serves the
production hybrid path, control and every ineligible call serve today's lexical bytes.

## Implementation

### CanaryTelemetry.cs (owned)
- Assignment flip (`CanaryAssignment.ResolveArm`): placeholder control-always -> frozen `bucket < 50 ? control : treatment`.
- Eligibility ladder (`CanaryEligibility.Resolve`): first-match-wins over the frozen order -- off-surface -> semantic-disabled ->
  query-class -> vectors-unavailable (absent/building/downloading/disk-blocked) -> vectors-incompatible -> circuit-open ->
  cross-workspace-no-generation -> eligible. Pure, primitive params (no Indexing dependency); reusable by Tasks 6/7.
- `CanaryEligibility.RequiresVectorProbe` lets the caller skip the filesystem probe on a call already ineligible by a cheaper rung.

### CanaryQueryClassifier.cs (new, Miller.Core, I/O-free)
- `Classify(op, query, route) : string` -> one of the six frozen classes. Reason map: Empty/Short->short_token,
  IdentifierLike/CodeSyntax->identifier, PathLike->path, Prose->docs_like when op==content or the query carries a docs-vocabulary
  word (readme, docs, documentation, config, configuration, guide, install, setup, changelog, license, tutorial, faq) else prose,
  Ambiguous*->mixed. Whole-word, case-insensitive vocabulary match.

### SearchTool.cs (owned)
- Retains the injected VectorSidecar + Lazy<SemanticEmbeddingSession?> (previously consumed and discarded by the greediest public
  ctor that DI selects) so the tool can build a treatment arm and probe vector state.
- `RunSymbolsWithCanary(...)` -- the single orchestration seam (Tasks 6/7 reuse for content): classify (route computed with
  LexicalEvidence.None), walk the ladder, assign, pick the arm, execute, assemble facts. Off / empty-workspace-id ->
  request.FusionArm untouched, no facts.
- Treatment arm = production SemanticSymbolFusionArm forced to SemanticMode.On (mode gate bypassed) so treatment behaves exactly
  like MILLER_SEMANTIC=on even under shadow. Control and every ineligible call pass a null arm -> pure lexical.
- ExecuteSymbols mirrors SearchRouteExecutor.RunSymbols byte-for-byte (collect -> optional fuse -> render) plus the served page
  slice and pre-fusion lexical count.
- RenderSymbolCandidates gained an overload returning the served page slice (out IReadOnlyList<SymbolCandidate>); the old
  signature delegates. Served-result hashes cover exactly the rendered page; parent names resolved at stamp time via
  FindBySymbolId(SymbolId).ParentId -> parent Name (one-level Parent.Member only).
- Facts assembly maps Task 3's SemanticQueryDiagnostics -> contract fields: fallback (SemanticFallbackKind -> 13 snake_case
  strings), backend, warmth (cold/warm/none), embed+KNN latency, identity (fingerprint/schema/generation when vectors opened),
  fusion profile; counters (lexical always; semantic/fused/contribution only when fusion ran).
- Symbol branch of Search now calls the orchestrator, then the existing auto-rescue block, then stamps (so canary_rescue_kind
  reflects the rescue outcome for op=auto).

## Judgment calls (contract -> plan -> code evidence)
1. UtcDate: the row ts is a SQLite now DEFAULT set at insert (Dispose); the scope holds no timestamp. UtcDate is DateTime.UtcNow
   date at stamp time -- identical to the persisted ts date except a sub-millisecond midnight straddle. canary_bucket is
   persisted so a rendered row stays self-auditing regardless.
2. Cross-workspace rung: the symbol route passes crossWorkspaceNoGeneration: false. Per the frozen ladder order, a foreign read
   with unavailable vectors is already caught by ineligible_vectors_unavailable (rung 4, before rung 8); a foreign read whose own
   vectors.db probes ready genuinely has a ready generation. The distinct rung is retained, ordered, and unit-tested for when a
   future corpus-generation compat signal exists (design 5.3). No behavioral gap for v1's same-/foreign-workspace paths.
3. canary_rescue_kind: copies the auto-rescue outcome into the frozen 7-value enum; the lexical docs_config rung (absent from the
   canary enum) folds into source -- the nearest frozen lexical text rescue (file is a filename rescue, wrong shape).
   null -> none; semantic rungs pass through.
4. Backend normalization: diagnostics.Backend passes through when in the frozen 5-value set, else none. The sidecar resolves to
   metal/vulkan/cuda/cpu so this is defensive.
5. Scope: only the symbol route is wired (Task 5's stated scope). Non-symbol non-content surfaces
   (markers/regions/source/external/web) do not yet stamp ineligible_surface; that follows the plan's per-route rollout. op=file
   reaches the symbol branch and exercises the ineligible_surface rung in production and tests.

## Necessary edit outside the strict ownership list
tests/Miller.Tests/Server/CanaryTelemetryTests.cs::Assignment_ArmStaysControlForEveryBucketUntilP5 asserted control for every
bucket -- the exact P2 "until P5" behavior this task flips. Rewrote it to Assignment_SplitsFiftyFiftyOnBucketFiftyAtP5 (control
<50, treatment >=50). This is the explicit criteria revision the flip requires. The other two control-asserting tests still pass
unchanged: their fixture (ws-hex/prose) lands in bucket 23 -> control post-flip.

## Verification
- worker-red-green: CanaryQueryClassifierTests + CanarySearchTests -> 46 passed, 0 failed.
- Regression suites: CanaryTelemetryTests, HybridSearchTests, SearchGoldenParityTests, SearchDeterminismTests, CanaryExportTests,
  CanaryGateReportTests, SemanticQueryDiagnosticsTests -> 112 passed, 0 failed.
- worker-ceiling: scripts/test.sh -> 4351 passed, 2 skipped, 0 failed, 17s wall (< 30s ceiling).
- Diagnostic: dotnet build Miller.slnx -c Release -> 0 warnings / 0 errors.
- Known pre-existing flake (RepositoryIndexLoaderBridgeTests) not triggered.

## Acceptance criteria
- [x] Canary off => no canary_* keys, byte-identical output (CanaryOff_..., off path = pre-program code path).
- [x] Eligible: arm from the frozen derivation (bucket 23->control, 94->treatment pinned); control lexical byte-identical;
      treatment fused identical to MILLER_SEMANTIC=on over the same fixture.
- [x] Facts per the field table: counters, fallback/backend/warmth/latency, identity, three hash arrays + shared truncation flag
      (11-result fixture).
- [x] CanaryQueryClassifier table test pins all six classes incl. docs vocabulary and op=content promotion.
- [x] Ineligible rows record exactly arm/eligibility/query_class (+ contract/experiment/assignment/policy version) -- Stamp
      enforces; OffSurfaceCall_... verifies no bucket/counters/hashes.
- [x] Worker-scope verification passes; committed per serial-worker-commit.

## Concerns
None blocking. The two documented limitations (UtcDate midnight straddle; cross-workspace rung awaiting a corpus-generation compat
signal) are noted judgment calls, not gaps for v1.

## Fix round 1 — unify the duplicated serving pipeline

Lead finding: SearchTool.ExecuteSymbols mirrored SearchRouteExecutor.RunSymbols line-for-line (two copies of the same
load-bearing pipeline that would drift). Now own SearchRouteExecutor.cs for this round.

Change (no behavior change anywhere):
- Extracted the symbol-route pipeline into one `SearchRouteExecutor.RunSymbolsCore(index, route, request, armOverride)`
  returning `SymbolExecution` (result, served page slice, pre-fusion lexical count, fusion map). `SymbolExecution` now lives
  next to it in SearchRouteExecutor.cs (moved from SearchTool).
- `SearchRouteExecutor.RunSymbols` is now a thin wrapper: `RunSymbolsCore(index, route, request, request.FusionArm).Result`
  (public signature + behavior unchanged for all existing callers).
- `SearchTool.RunSymbolsWithCanary` calls `RunSymbolsCore` directly (treatment arm, or request.FusionArm on the off path);
  `SearchTool.ExecuteSymbols` and its private record are deleted.

Verification:
- red-green + regression (golden parity, determinism, hybrid, canary telemetry/export/gate, diagnostics, SearchRouteExecutor)
  -> 165 passed, 0 failed.
- scripts/test.sh -> 4351 passed, 2 skipped, 0 failed, 16s.
- dotnet build Miller.slnx -c Release -> 0W/0E.

Owned files touched this round: SearchTool.cs, SearchRouteExecutor.cs (report is documentation).
