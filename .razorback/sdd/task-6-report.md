# Task 6 — Content-route canary (P5)

## Worktree state (verified)
- pwd: `/Users/murphy/source/miller/.claude/worktrees/worktree-semantic-p5`
- branch: `worktree-semantic-p5`
- HEAD at start: `ceb8dd8`

## Status: COMPLETE

The randomized-holdout canary now covers the fourth instrumented surface, `op=content`. With
`MILLER_SEMANTIC_CANARY` off the content path is byte-identical to today (the production rerank untouched, no
keys). With it on, every content-route call records the frozen contract row: eligible units split 50/50,
treatment serves the P3 content-mode hybrid forced past the mode gate, control and every ineligible call serve
today's lexical content bytes. Served-result hashes are PATH ONLY — a content result is a path+line chunk with
no symbol name, so the name and qualified arrays are absent, not empty.

## Implementation (SearchTool.cs — owned)

- **Content dispatch** (`SearchRouteKind.Content` branch of `Search`): replaced the inline rerank/RunContent
  split with a single `RunContentWithCanary(...)` call, then stamps via `StampContentCanary` when the canary is
  on. Reads `CanaryActivation.FromEnvironment()` and `_semanticSidecar?.Mode` exactly as the symbol branch does.
- **`RunContentWithCanary`** (static, mirrors `RunSymbolsWithCanary`): off / empty-workspace-id → the production
  rerank untouched, no facts. Otherwise classify (`CanaryQueryClassifier.Classify("content", …)` → prose promotes
  to `docs_like`), walk `CanaryEligibility.Resolve` (probing vector state only when `RequiresVectorProbe`), assign
  via `CanaryAssignment`, and build the treatment rerank only for an eligible treatment unit. Control / ineligible
  pass a null rerank → byte-identical lexical content. Treatment reranks through a forced-`On` arm.
- **`BuildTreatmentContentArm`**: the content analogue of `BuildTreatmentArmFactory` — a `SemanticTextArm` forced
  to `SemanticMode.On` built from the injected `_semanticSidecar` + `_embeddingSession`, so treatment fuses even
  under `MILLER_SEMANTIC=shadow`, exactly like `=on`. Null when the semantic graph is absent (treatment then
  serves lexical bytes).
- **`BuildContentRerank`** (extracted static): the content-mode reordering over any `ISemanticTextArm`, gated on
  `SemanticQueryPolicy.Route(query).IsHybrid` (so treatment equals `=on`, which also serves lexical for a
  non-hybrid query). An optional `onConsult` observes each arm consultation so the canary reads its diagnostics
  without changing what is served. `SemanticContentRerank` now delegates here (byte-identical off behavior).
- **`RunContentCorpus` served-page overload**: added `out int lexicalResultCount` (ranked hit count before paging)
  and `out IReadOnlyList<ContentSearchHit> servedPage` (the exact rendered rows in served order). The pre-existing
  full overload delegates to it, discarding the two new outs. Same pattern as `RenderSymbolCandidates`' served-page
  overload — no serving-pipeline mirror.
- **`BuildContentCanaryFacts`**: maps `count`/`lexicalResultCount` and the captured `SemanticQueryDiagnostics` to
  the field table (fallback/backend/warmth/latency/identity, reusing the symbol route's `MapFallbackReason`,
  `NormalizeBackend`, `ResolveWarmth`). Semantic/fused/contribution counters + `fusion_profile` are written only
  when the content fusion actually ran (`consulted.Served && Hits.Count > 0`).
- **Path-only served hashes**: `ContentCanaryOutcome` carries the ≤10 path digests + shared truncation flag
  separately from `CanaryCallFacts.ServedResults` (kept empty, so the frozen `CanaryTelemetry.StampServedResults`
  writes no name/path/qualified array). `StampContentCanary` calls the frozen `Stamp` then writes
  `canary_result_path_hashes` + `canary_result_hash_truncated` only when path hashes exist (eligible, result_count
  > 0). `ContentPathDigest` is the same SHA-256 lower-hex derivation `TelemetryScope.SetTarget` uses, so a served
  path digest matches a later `inspect`/`content read` `target_hash`.

## Judgment calls (contract → plan → code evidence)

1. **Path-only stamping without touching CanaryTelemetry.cs.** The frozen `StampServedResults`
   (CanaryTelemetry.cs:344) *always* writes both name and path arrays whenever `ServedResults` is non-empty; it
   cannot express path-only, and CanaryTelemetry.cs is off-limits (T7 owns it). Resolved within SearchTool.cs
   ownership: keep `facts.ServedResults` empty (so `Stamp` emits no result-hash array) and write
   `canary_result_path_hashes` + the shared truncation flag directly in `StampContentCanary`. This honors the
   absent-vs-zero guarantee (name/qualified arrays truly absent, not empty) and reuses the identical digest
   mechanism. No CanaryTelemetry.cs change, so no report-a-mismatch trigger.
2. **`semantic_contribution_count` for content.** Content fusion joins on file path and only reorders (never
   changes membership), so there is no per-row "semantic rank beat lexical rank" the way symbols have. Defined it
   as the number of served (paged) results whose path appears in the semantic hit set — the closest faithful
   analogue of "results the semantic arm contributed." `fused_result_count` = the lexical count (membership
   unchanged); `semantic_result_count` = chunk hits returned. All three (plus `fusion_profile`) are written only
   when fusion actually ran.
3. **`fusion_profile` source.** The symbol arm stuffs the profile into its diagnostics; the content fusion runs in
   SearchTool, so the content diagnostics don't carry it. Set `FusionProfile = RrfFusion.FusionProfile` explicitly
   in the fused branch — same value, same "only when fusion ran" condition.
4. **`lexical_result_count` = total ranked hits, not the page.** Matches the symbol route
   (`candidates.Candidates.Count`, pre-fusion). Content fusion preserves membership, so the post-rerank total
   equals the lexical count.

## Verification (all from the worktree, branch `worktree-semantic-p5`)

- worker-red-green: `dotnet test … --filter FullyQualifiedName~CanaryContentSearchTests` → 8 passed, 0 failed.
- Regression: SearchGolden, SearchDeterminism, HybridSearch, SearchToolRescue, SearchRouteExecutor, CanarySearch,
  SearchToolTests, CanaryTelemetry → 331 passed, 0 failed.
- worker-ceiling: `scripts/test.sh` → 4359 passed, 2 skipped, 0 failed, 23s wall (< 30s ceiling).
- Diagnostic: `dotnet build Miller.slnx -c Release` → 0 warnings / 0 errors.
- Known pre-existing flake (`RepositoryIndexLoaderBridgeTests`) not triggered.

## Acceptance criteria

- [x] Content-op canary rows carry path hashes only; name/qualified arrays absent
      (`ContentRow_CarriesPathHashesOnly_NameAndQualifiedArraysAbsent`, `Eleven…` truncation fixture).
- [x] Control/off byte-identical to today's content output (`CanaryOff_…`, `EligibleControlUnit_…` compared to
      `RunContentCorpus`); treatment identical to the `MILLER_SEMANTIC=on` content hybrid
      (`EligibleTreatmentUnit_…` compared to `RunContentCorpus` with the same-arm `BuildContentRerank`).
- [x] Worker-scope verification passes; committed per serial-worker-commit.

## Files

- `src/Miller.Server/Tools/SearchTool.cs` (content route + dispatch — owned)
- `tests/Miller.Tests/Server/CanaryContentSearchTests.cs` (new — owned)
- `.razorback/sdd/task-6-report.md` (this report)

## Concerns

None blocking. Content `semantic_contribution_count` is a path-membership analogue (judgment call 2), not a
per-row rank comparison, because content fusion only reorders — noted for the analysis layer.
