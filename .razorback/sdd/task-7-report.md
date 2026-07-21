# Task 7 — Identifier shadow population (P5)

## Worktree state (verified)
- pwd: `/Users/murphy/source/miller/.claude/worktrees/worktree-semantic-p5`
- branch: `worktree-semantic-p5`
- HEAD at start: `44f3b36` (T6 commit)

## Status: COMPLETE

The identifier non-inferiority shadow population is live. For a canary-on, semantic shadow|on call whose query
classifies as `identifier` (ops auto/text/symbol), 10% of assignment units (`bucket < 10` under
`semantic_identifier_noninferiority_v1`) upgrade from the ordinary `arm=ineligible` row to a shadow row: the
lexical result is finalized first, then a forced-hybrid pass runs off to the side, is discarded, and its ranking
is compared against the served one. The row records `arm=shadow`, the bucket, the status, and — on the `ok` path
— the three comparison counters, plus the generation identity when vectors were opened. Nothing the shadow does
can change the served bytes, the result count, or the row's `outcome`. This closes the Field Reference: every
frozen field is now writable by a real code path (identity + shadow counters here; all others in T1–T6).

## API shape evidence (worktree reads)
- `RunSymbolsWithCanary` flow / ineligible identifier stamp: `SearchTool.cs:1235`; the ineligible early-return in
  `BuildCanaryFacts` at `:1293` (identifier ⇒ `eligibility=ineligible_query_class`, `arm=ineligible` via `Stamp`).
- `SymbolExecution.ServedPage`: `SearchRouteExecutor.cs:57` (`RunSymbolsCore` returns served page + fusion map).
- `BuildTreatmentArmFactory` forced-On pattern: `SearchTool.cs:1542`.
- `CanaryAssignment.IdentifierExperimentId` + `Bucket`: `CanaryTelemetry.cs:199`, `:201` (SHA-256 → uint32 %100).
- `CanaryShadowStatus` values: `CanaryTelemetry.cs:154` — `ok/timeout/error/skipped` (named constants added).
- `TelemetryScope.SetMetadata` overloads (string/bool/long/list): `TelemetryScope.cs:130-163`.
- `SemanticFallbackKind` values for status mapping: `SemanticSearchArm.cs:18`.
- Critical finding: `SemanticSymbolFusionArm.Fuse` abstains on `!route.IsHybrid` (`SearchRouteExecutor.cs:296`),
  and `SemanticQueryPolicy.Route` routes every identifier lexical-only (`SemanticQueryPolicy.cs:117-121`). The
  production treatment arm therefore CANNOT produce a hybrid ranking for an identifier — see judgment call 1.

## Implementation

### CanaryTelemetry.cs (owned)
- Added named constants `Ok/Timeout/Error/Skipped` to `CanaryShadowStatus` (was `All`-only).
- New `CanaryShadowFacts` record: the shadow facts of one sampled call — status, the three comparison counters
  (nullable, present only at `ok`), and the generation identity (nullable, present only when vectors opened).
- New `StampShadow(TelemetryScope, CanaryShadowFacts)`, mirroring `Stamp`'s style: writes the standard
  version/class keys under the fixed `IdentifierExperimentId`, `arm=shadow`, `canary_bucket`,
  `canary_shadow_status`; the identity trio when present; and — only when `status=ok` — the three shadow
  counters. Backend/warmth/latency and lexical/semantic counters are deliberately never written (shadow rows are
  not eligible rows).

### SearchTool.cs (owned)
- `SymbolCanaryOutcome` gained `CanaryShadowFacts? ShadowFacts`. New `ShadowExecution` record (served flag,
  fallback, ordered hybrid symbol ids, identity).
- `RunSymbolsWithCanary` gained an optional `shadowRunner` param and a post-finalize hook: after the lexical
  `execution`, `ShadowSampled(...)` gates on `eligibility==ineligible_query_class && class==identifier &&
  Bucket(IdentifierExperimentId,…) < 10`; a sample calls `RunIdentifierShadow` and returns a shadow outcome.
- `RunIdentifierShadow`: try/catch around the shadow thunk (any throw ⇒ `error`), identity lifted when vectors
  opened, `MapShadowStatus` on abstain, `CompareShadow` on serve.
- `CompareShadow`: top-10 overlap by symbol id, `top1_changed`, and the 1-based rank of the served lexical top-1
  within the hybrid top 50 (0 when absent).
- `ShadowSymbolArm` (private `ISymbolFusionArm`): forces the semantic consult + `RrfFusion` **regardless of the
  lexical-only route**, reusing the same recall clamp, allow predicate, and fusion the served path uses;
  captures the ordered fused ranking. `ShadowRunnerFor(openArm)` (internal, test seam) drives it through
  `RunSymbolsCore` and discards the render. `BuildShadowRunner` supplies the production opener or null.
- Dispatch: passes `BuildShadowRunner`, and stamps via `StampShadow` when `ShadowFacts` is present (else `Stamp`).

## Judgment calls (contract → plan → code)
1. **Forced fusion, not the production treatment arm.** The task note says the hybrid ranking comes from the
   T5 treatment arm forced On. But `SemanticSymbolFusionArm.Fuse` abstains on `!route.IsHybrid`, and every
   identifier routes lexical-only — so that arm returns lexical-unchanged for exactly this class, which would
   make every shadow row a degenerate `overlap=10 / top1_changed=false / rank=1`. Contract §Shadow step 3 is
   explicit ("Embed the query, run the semantic arm, fuse — producing a hybrid ranking"), which outranks the
   note. Resolved with a small owned `ShadowSymbolArm` that forces the consult+fusion past the policy gate,
   reusing the same `SemanticSearchArm` + `RrfFusion` primitives (not a serving-pipeline copy). The production
   arm is correctly left untouched (it must keep abstaining for served identifier queries).
2. **`canary_semantic_result_count` intentionally omitted from shadow rows.** The Field Reference row lists
   `arm ∈ {treatment,shadow}` for it, but §Shadow step 4 and this task both say the shadow row records ONLY the
   three comparison counters. Followed the §Shadow enumeration + task ("records ONLY"): shadow writes no
   lexical/semantic/fused/contribution counter. The field stays writable via the treatment path (T5), so the
   "every field writable" close still holds.
3. **`off` ⇒ zero shadow work, for free.** Under `MILLER_SEMANTIC=off`, `semanticDisabled` makes the eligibility
   ladder stop at `ineligible_semantic_disabled` before the query-class rung — so the shadow gate
   (`ineligible_query_class`) is never met and the runner is never invoked. No extra guard needed; proved by
   `SemanticDisabled_RunsNoShadowWorkAndRecordsPlainIneligible` (runner invocation count 0).
4. **Serve-first covers cancellation too.** The shadow runs inside `RunSymbolsWithCanary`, whose caller wraps
   everything in a `catch (Exception) ⇒ outcome=Error`. A propagating shadow exception would discard the already
   finalized served bytes and flip `outcome`. So `RunIdentifierShadow` catches **all** exceptions (including
   `OperationCanceledException`) → `status=error`; the served result and outcome are provably untouched. Pinned
   by `ShadowCancels_...` and `ShadowArmThrows_...`.

## Status mapping table (fallback → `canary_shadow_status`)
| `SemanticFallbackKind` | status | rationale |
|---|---|---|
| served, hits fused (`None`) | `ok` | hybrid executed, comparison valid |
| `EmbedTimeout` | `timeout` | embed deadline |
| `VectorsMissing / VectorsStale / VectorsIncompatible / VectorsBuilding / ModelNotPrepared / CircuitOpen / DiskBlocked / Disabled` | `skipped` | prerequisites (vectors/circuit/model) unavailable |
| `EmbedError / KnnError / Unknown` | `error` | execution failure |
| thrown exception (any) | `error` | serve-first catch-all |
| null runner (semantic graph absent) | `skipped` | cannot run |

## Verification (all from the worktree, branch `worktree-semantic-p5`)
- worker-red-green: `dotnet test … --filter FullyQualifiedName~CanaryShadowPopulationTests` → **31 passed, 0 failed**.
  The `ok` path drives the real `ShadowSymbolArm` + `RrfFusion` over a 3-symbol index with one semantic hit on
  the lowest-ranked symbol; hand-computed hybrid `[C,A,B]` yields `overlap=3, top1_changed=true, rank=2`, matched
  exactly. Fault taxonomy (throw/cancel/timeout/skipped×6/error×3), sampling (pinned buckets 0/1/3/5 in, 28/51
  out), off + semantic-disabled zero-work, and the exact key-set (15 present / 15 absent) are all pinned.
- Regression (Canary*/SearchGolden/SearchDeterminism/HybridSearch/SearchRouteExecutor/SearchToolTests/
  AgentInstructions/SemanticQueryDiagnostics): **476 passed, 0 failed**.
- worker-ceiling: `scripts/test.sh` → **4389 passed, 2 skipped, 0 failed** (warm run 15s wall, < 30s ceiling; a
  first cold-build run reported 34s purely from build + machine load — the second warm run was 15s).
- Diagnostic: `dotnet build Miller.slnx -c Release` → **0 warnings / 0 errors**.
- Known pre-existing flake (`RepositoryIndexLoaderBridgeTests`) not triggered.

## Acceptance criteria
- [x] Sampling honors `bucket < 10` under the noninferiority experiment id (pinned vectors: 0/1/3/5 sampled,
      28/51 not; `IdentifierBucket_MatchesTheFrozenNoninferiorityDerivation`, `BucketBelowTen_…`, `BucketAtOrAbove…`).
- [x] Ok path records exactly the shadow key set; overlap/top1/rank verified against a fixture with known lexical
      and hybrid rankings, hand-computed (`ShadowOk_RecordsHandComputed…`, `StampShadow_OkPath_WritesExactly…`).
- [x] Timeout/error/skipped paths record status only; served output and `outcome` provably untouched
      (`ShadowArmThrows_…`, `ShadowCancels_…`, `ShadowEmbedTimeout_…`, `ShadowPrerequisiteUnavailable_…`,
      `RealArm_KnnFailure_…` all assert `outcome.Result.Output == LexicalOutput`).
- [x] Worker-scope verification passes; committed per serial-worker-commit.

## Files
- `src/Miller.Server/Tools/SearchTool.cs` (post-finalize shadow hook + forced-hybrid arm — owned)
- `src/Miller.Server/Telemetry/CanaryTelemetry.cs` (StampShadow + CanaryShadowFacts + status constants — owned)
- `tests/Miller.Tests/Server/CanaryShadowPopulationTests.cs` (new — owned)
- `.razorback/sdd/task-7-report.md` (this report)

## Concerns
None blocking. Judgment call 1 (forced fusion in an owned arm rather than the production treatment arm) is the
one deviation from the task's approach note, and it is required by contract §Shadow step 3 — noted rather than
redesigned. Judgment call 2 (no `canary_semantic_result_count` on shadow rows) follows the §Shadow enumeration
over the permissive Field Reference "written when"; if the analysis layer wants that counter on shadow rows, it
is a one-line addition to `StampShadow` — but it would exceed the task's "records ONLY" spec.
