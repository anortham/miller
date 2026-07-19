# Pre-merge fix report — eval harness (Findings A + B)

**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/semantic-integration`
**Branch:** `worktree-semantic-integration`
**Commit at start:** `338e665`
**Commit SHA:** none - parallel-lead-commit
**Dirty state at finish:** 10 modified files, all owned. Frozen dev-set files untouched.

## Status: COMPLETE

Both findings fixed, all arms re-ranked and re-scored from cached embeddings, findings doc rewritten
from real output. **The default pin changed: 1024d int8 → 512d int8.**

## Finding A — primary metrics now use cluster units

`eval/retrieval-eval/Scorer.cs` built a `ScoredQuery` per positive query and averaged those directly
into `Overall`, `PerLanguage`, `LanguageMacroAverage`, and `WorstLanguage`. Design §8 requires
paraphrase intent clusters to be "scored as clusters, not independent samples".

**Implemented:**

- New `EvalUnit` abstraction + `BuildUnits`: each non-empty `intent_cluster` becomes **one unit** whose
  recall/nDCG is the **mean over its members**; each cluster-less positive is one unit. Dev set: 76
  positive queries → **48 units** (14 clusters × 3 + 34 standalone).
- `Overall`, `PerLanguage`, `LanguageMacroAverage`, `WorstLanguage` are now cluster-unit (PRIMARY).
- Added secondary blocks: `overall_per_query`, `overall_cluster_max` (cluster takes its best member —
  the recall/nDCG analogue of the existing `ClusterRollup` hit coverage, which is retained unchanged),
  `per_language_per_query`.
- `per_query_class` deliberately stays per-query (classes cut across clusters); documented in the
  `EvalReport` XML doc, the README, and every report table label.
- Report shape additions: `unit_policy` (`UnitPolicies.Cluster`), `evaluation_unit_count`, and
  `unit_count` on `MetricBlock`/`WorstLanguage` alongside `query_count`.
- Cluster language attribution: dominant member language, ties broken by name. (Dev set clusters are
  all single-language, so this is defensive only.)
- `summarize.py` gained `pq recall` / `cmax recall` columns and a unit-policy header note.

**Tests added** (`eval/retrieval-eval/tests/ScorerTests.cs`):

- `An_extra_paraphrase_scoring_like_its_cluster_does_not_shift_the_primary_metric`
- `Duplicating_a_cluster_member_verbatim_leaves_the_primary_metric_unchanged`
- `A_cluster_weighs_the_same_as_one_standalone_query`
- `Cluster_max_overall_credits_a_cluster_for_its_best_phrasing`
- `Per_language_and_worst_language_use_cluster_units`

Each asserts the primary metric is unchanged **and** that `overall_per_query` visibly drifts, so the
test fails if the two are ever collapsed back together.

**Note on the invariant as briefed.** The brief's example ("duplicate a member with identical results →
overall metric unchanged") does not hold literally for a mean: duplicating a *hit* inside a 50/50
cluster shifts that cluster's mean. The property that actually holds — and the one §8 is about — is
**weight invariance**: a cluster contributes exactly one unit regardless of member count. The tests pin
weight invariance, plus the exact-duplication case in the form where it is true (members scoring alike).
Flagging because it is a deliberate deviation from the literal wording.

`EndToEndTests` was updated: its hand-computed `Overall.NdcgAtK` was the per-query value, now asserted
on `OverallPerQuery` with the cluster value asserted on `Overall`.

## Finding B — test units excluded from the ranking population

`eval/model-bench/bench.py` `cmd_rank` masked only on repo. Corpus field is `is_test` (int 0/1, emitted
by `build_corpus.py`; 6,104 of 34,481 units truthy).

**Implemented:** `eligible = ~is_test` folded into the per-query mask, with `--include-tests` to restore
the unfiltered population for ablation. Rank output now logs the excluded count. `README.md` documents
the rule and the production-parity reason (design §5.2 metadata filter +
`SearchTool.ResolveExcludeTests`).

**Verified:** ranked test-path entries went 31 → **0** for `qwen3-1024d-f32-topk`; BM25 unchanged at 11
(all on `identifier`/`path` queries, where Miller's auto-hide does not apply — production-faithful, and
documented as a residual asymmetry). Independently re-confirmed the safety precondition: **0 of 38
graded doc references resolve to a test-only path**, so no relevant doc left any candidate pool.

## Re-run

Added `RANK_ONLY=1` to `run-bench.sh` — skips download/verify, corpus build, BM25 re-run, sanity gate,
and embedding; re-ranks every lane from the cached `.npy` vectors and re-scores every arm. Nothing was
re-embedded or re-downloaded; BM25 result files are byte-identical (mtime 17:14, untouched).

Re-ran all 6 Qwen3 lanes + 2 fallback candidates × f32/int8 × topk/thr, rescored all 24 arms including
the 4 unchanged BM25 arms, re-ran the 7-point threshold sweep, regenerated `summary.md`.

**Cache hygiene:** the prior run's arm files used short names (`qwen3-1024d-int8-topk`) while the
current script emits full candidate ids (`qwen3-0.6b-f16-1024d-int8-topk`). The re-run produced the
long-named arms while the stale short-named files were rescored in place, briefly appearing as a
duplicate arm set with un-test-filtered rankings. **The 12 stale files were deleted**, so no superseded
arm survives. Called out in the findings doc's Revision note.

## New pin recommendation

**Default: `Qwen3-Embedding-0.6B`, f16 GGUF, `512` dims, `int8`** — changed from 1024d int8.
**Fallback: `bge-small-en-v1.5`, f32 GGUF, `384` dims, `int8`** — unchanged.

Cluster-unit, `topk` policy:

| arm | recall@10 | nDCG@10 | macro nDCG | worst lang | clusters |
| --- | --- | --- | --- | --- | --- |
| **qwen3-512d-int8** | **0.6979** | **0.6423** | **0.6465** | csharp **0.5997** | 14/14 |
| qwen3-1024d-int8 | 0.6910 | 0.6421 | 0.6123 | markdown 0.5327 | 14/14 |
| bge-384d-int8 *(fallback)* | 0.6979 | 0.6157 | 0.6499 | rust 0.5908 | 13/14 |
| qwen3-256d-int8 | 0.6389 | 0.6020 | 0.5490 | markdown 0.4077 | 14/14 |
| bm25-symbol *(baseline)* | 0.5625 | 0.5073 | 0.5562 | csharp 0.4847 | 10/14 |

512d wins recall, nDCG, macro nDCG, and — decisively — worst-language nDCG (0.5997 vs 0.5327), at half
the storage (13.9 MB vs 27.7 MB for the 28,377-unit population). Root cause of the flip: 1024d is
markedly worse on markdown (0.533 vs 0.658 nDCG); per-query weighting buried that behind 72
code-language queries. Evidence-gated rule honored: default pin from completed Qwen3 lanes only,
fallback pin from a completed fallback lane only.

512d and 1024d **invert** between the primary (0.6979/0.6910) and per-query (0.6118/0.6250) views — the
concrete demonstration that Finding A was pin-relevant, published in the doc as a secondary table.

## Re-verified claims

- **"int8 storage is free" — partially withdrawn.** Exactly free at the pinned 512d lane (0.0000 delta
  on both metrics to 4dp). Elsewhere the worst cost is −0.0035 recall (1024d, arctic); bge −0.0012
  nDCG; arctic moves opposite ways on the two metrics (quantization noise). The first version's
  "within 0.0002 at every lane, identical at 1024d" was an artifact of the old measurement and is
  withdrawn in the doc.
- **Identifier non-inferiority (per-query, standalone) — holds on recall, costs ordering.** All arms
  retrieve 16/16 identifier queries' relevant docs: recall 1.0000, Δ 0.0000 vs baseline. nDCG: 512d
  0.9759 (−0.0241) vs 1024d 0.9980 (−0.0020). This is the one axis where the pinned lane is worse than
  1024d; the doc states it plainly, accepts it (recall unaffected, ordering-only, RRF fusion keeps
  lexical byte-identical per ADR-0003), and files it as open item #3 for P1 to confirm on the hybrid arm.
- **Negatives + sweep — conclusions survive.** Still 1.0000 FP at the default floor for all 24 arms.
  0.45 still strictly free (identical 0.6944/0.6428, FP 6/6 → 4/6). Full abstention still only at 0.65,
  where cluster recall 0.5278 falls below BM25's 0.5625. Tuning band 0.45–0.55 unchanged.
- **Throughput — unchanged** (embeddings reused). Doc adds that MRL means the 512d pin costs nothing
  extra to embed, and that the test filter reduces the *ranked* population but not embedding cost.

## Verification gates and the invariant each proves

1. `dotnet test eval/retrieval-eval/tests/RetrievalEval.Tests.csproj -c Release` → **31/31 passed**.
   Proves: cluster units carry weight-1 per cluster regardless of member count; primary metrics are
   invariant to paraphrase addition/duplication while the per-query view is not; per-language and
   worst-language read from cluster units; cluster-max is the best-member view; existing negative,
   missing/unknown-row, and duplicate-id contracts are unbroken.
   *Red/green evidence:* first run was 28/31 — my two invariance tests failed on miscomputed
   expectations and `EndToEndTests` failed because it asserted the per-query nDCG on `Overall`. All
   three failures were real signal about the changed semantics, corrected, then green.
2. `RANK_ONLY=1 eval/model-bench/run-bench.sh` → all 24 arms ranked and scored, no missing/unknown
   result rows. Proves the pipeline runs end to end from cached vectors and every arm is re-derived
   under the same scorer and population.
3. Test-path entry count 31 → 0 (semantic), 11 (BM25, unchanged). Proves the filter changed the
   population it was meant to and nothing else.
4. 0 of 38 graded docs are test-only. Proves the filter cannot remove a relevant document.
5. Programmatic diff of every headline-table cell against the report JSON → **0 mismatches** across 12
   arms × 7 columns. Proves the doc is transcribed from real output, not recalled.
6. `validate --corpus` on the frozen dev set → 82 queries, 38 doc refs, 0 missing, composition minimums
   met. Proves the frozen set was not disturbed.
7. `bash -n run-bench.sh` → clean. Proves the `RANK_ONLY` conditional nesting is well-formed.

## Files changed (all owned)

```
docs/findings/2026-07-19-model-benchmark.md   rewritten
eval/model-bench/README.md                    population rule + RANK_ONLY
eval/model-bench/bench.py                     is_test exclusion + --include-tests
eval/model-bench/run-bench.sh                 RANK_ONLY re-run path
eval/model-bench/summarize.py                 secondary-unit columns + policy note
eval/retrieval-eval/README.md                 evaluation-unit section, report shape
eval/retrieval-eval/Report.cs                 unit_policy, unit_count, secondary blocks
eval/retrieval-eval/Scorer.cs                 EvalUnit + cluster-unit primaries
eval/retrieval-eval/tests/EndToEndTests.cs    unit-policy assertions
eval/retrieval-eval/tests/ScorerTests.cs      5 invariance tests
```

Untouched as required: `sets/dev/queries.jsonl`, `sets/dev/manifest.json`, `src/`,
`tests/Miller.Tests/`, `docs/contracts/`.

## Miller MCP calls used

- `inspect eval/retrieval-eval/Scorer.cs depth=overview` — symbol map before editing (`Score`,
  `Aggregate`, `Mean`, `ToGradeMap`).
- `inspect eval/retrieval-eval/tests/ScorerTests.cs` — located the 13 existing tests and their factory
  helpers before adding to them, which is how I found that `Overall_averages_per_query_metrics_over_positive_queries_only`
  would still pass (its queries carry no clusters).

## Concerns for the lead

1. **The design's §2.4 amendment target changed.** The doc now recommends amending §2.4's favored lane
   to **512d int8** (the superseded version said 1024d). `docs/plans/…-design.md` is not mine to edit —
   someone must land that amendment.
2. **Invariant wording deviation** — see the note under Finding A. The literal briefed example is not a
   true property of a cluster mean; I pinned weight invariance instead. Worth a look.
3. **BM25 residual asymmetry is deliberate.** Semantic arms now exclude tests for every query class;
   BM25 excludes them only for natural-language queries, because that is what production does. It
   cannot affect recall (no golden doc is test-only) but it is not perfectly symmetric. Documented in
   both the README and the findings doc rather than silently normalized.
4. **Arm file names changed** (`qwen3-1024d-int8-topk` → `qwen3-0.6b-f16-1024d-int8-topk`). Anything
   downstream keyed on the old names needs updating. `.cache` is gitignored so nothing is committed,
   but the summary table in the doc uses shortened display names with the mapping stated.
5. **The Qwen-vs-fallback margin narrowed materially.** bge-small now *ties* the pinned lane on
   cluster-unit recall (0.6979) despite a 22% input-truncation handicap. Qwen3 still wins nDCG, cluster
   coverage, and prose, so the pin holds — but "Qwen3 wins decisively over the fallbacks" is no longer
   an accurate summary, and the doc no longer says it.
