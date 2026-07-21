# Fused-arm encoder benchmark

**Date:** 2026-07-21
**Program:** Encoder comparison + fusion-v2 selection
**Spec:** [`docs/plans/2026-07-21-encoder-comparison-fusion-v2-design.md`](../plans/2026-07-21-encoder-comparison-fusion-v2-design.md) (rev 2, codex-reviewed)
**Plan:** [`docs/plans/2026-07-21-encoder-comparison-fusion-v2-plan.md`](../plans/2026-07-21-encoder-comparison-fusion-v2-plan.md)
**Status:** SKELETON — pre-registered before any scoring. Result sections (Tasks 4/5/6) are empty stubs.

This document pre-registers every decision gate before a single number exists. Nothing below the
"Pre-registered gates" line may be edited after scoring begins except to fill the result stubs; the
gates themselves are frozen.

---

## Pre-registered gates

Copied verbatim from the plan §Global Constraints (`docs/plans/2026-07-21-encoder-comparison-fusion-v2-plan.md`).
Numbers, not prose.

**Pre-registered gates only (T5):** winner bar = beats fusion-v1 overall cluster-unit nDCG@10 with
paired-bootstrap 95% CI excluding zero AND no regression > 0.02 nDCG on language macro-average,
worst-language, docs_like view, or identifier diagnostic, for BOTH qwen3 and bge-small. Pin rule =
bge-small takes the pin iff its fused overall nDCG is within 3% relative of qwen3 AND worst-language
loss ≤ 0.02 absolute.

**Profiles under sweep (T3):** global k ∈ {20, 60, 120} × Conceptual semantic:lexical ratio ∈ {1:1,
2:1, 3:1, 4:1} = 12, plus fusion-v1 control = 13. SymbolLookup and Mixed constants stay fusion-v1
everywhere.

**Canary transition (T6):** fusion-v2 ships as a distinct commit; measurement window starts next UTC
day; transition day excluded.

### R1 — within-run comparability

Per spec R1: all arms run against corpora built from clean worktrees at SHAs frozen before any
results are seen; `validate` confirms every graded doc exists at them; benchmark-derived documents
are excluded from the corpus by an explicit exclusion list in `build_corpus.py` because several
graded answers are named verbatim in them. Index artifact ids and revisions are recorded.
**Numbers are within-run comparable only** — a score from this run may be compared to another arm
scored in the same run against the same frozen corpora, never to a number from any other run.

---

## Frozen substrate

Corpora were frozen at the current local `main` HEAD of each repo (detached worktrees under scratch),
then indexed with the worktree-built `miller` (`workspace open --path <root> --full`, no semantic env
vars). julie-extract binary version **2.16.0** on both.

| Repo | Frozen SHA | Frozen worktree | Workspace id | Artifact id | Symbols |
|---|---|---|---|---|---|
| miller | `59c2c79e8633940de5d394f73235f10acbe2c2b8` | `<scratch>/frozen-miller` | `6772d4640d5de25305f25317098cc2cf62539ea3bc588bc5969bf375532fe894` | `artifact-1784654234183324000` | 49,276 |
| julie | `9d1d22c5dcca8509e412db96b6dbb5ff19d4311a` | `<scratch>/frozen-julie` | `b3282901372258f13a2038b121f7f708a208797f350e5f5d0a89cd86888257bc` | `artifact-1784654260643605000` | 34,429 |

`<scratch>` = `/private/tmp/claude-501/-Users-murphy-source-miller/df49671d-ef55-48b5-b537-7efdb9e2bce8/scratchpad`.
Both frozen roots are registered in `~/.miller/workspaces.db` (throwaway benchmark registrations —
prune after the program completes).

**`validate` gate (proves every graded doc exists at the frozen SHAs):**

```
$ dotnet run --project eval/retrieval-eval -- validate \
    --queries eval/retrieval-eval/sets/dev/queries.jsonl \
    --corpus miller=<scratch>/frozen-miller --corpus julie=<scratch>/frozen-julie
corpus: 38 distinct doc references checked, 0 missing
queries: 82
OK: schema valid and composition minimums met      # exit 0
```

---

## Corpus exclusions (`build_corpus.py`)

`BENCHMARK_DOC_EXCLUSIONS` is applied unconditionally to the **miller** corpus alongside the existing
`GOLDEN_SET_EXCLUSIONS`. It names the five benchmark docs the plan lists **plus every other `docs/`
file at the frozen miller SHA whose text contains a graded `doc_id` from the dev set** — a plan or
findings doc that enumerates answer paths is a leaked cheat sheet. Derived by grepping the 38 distinct
graded doc_ids against `docs/` at SHA `59c2c79`; **56 files** (5 named ∪ 53 grep-derived; the named
`dead-code-candidates-dogfood` and `model-benchmark` docs are inside the 53). None of the 56 is itself
a graded answer doc, so no ground truth is removed (verified: the two graded miller docs
`docs/adr/ADR-0001-guidance-delivery-channels.md` and `docs/release-process.md` are not in the list).

**Row-count proof (miller corpus):**

| | units | symbol cards | doc chunks |
|---|---|---|---|
| without benchmark exclusions | 19,465 | 13,905 | 5,560 |
| with benchmark exclusions | 17,032 | 13,905 | 3,127 |
| **excluded** | **2,433** | 0 | 2,433 |

All 2,433 excluded units are doc chunks (0 cards — the excluded files are markdown/csv/json with no
code symbol cards). Full corpus (miller + julie, exclusions applied): 35,392 units; golden-set leak
check PASS.

<details><summary>The 53 grep-derived docs (files under <code>docs/</code> naming ≥1 graded doc_id at SHA 59c2c79)</summary>

```
docs/contracts/canary-telemetry-v1.md
docs/contracts/semantic-sidecar-protocol-v1.md
docs/contracts/vectors-v1.md
docs/findings/2026-06-05-julie-side-by-side-audit.md
docs/findings/2026-06-05-tool-output-token-savings.md
docs/findings/2026-06-23-1.0-readiness-review.md
docs/findings/2026-07-07-dead-code-candidates-dogfood.md
docs/findings/2026-07-19-model-benchmark.md
docs/findings/benchmarks/2026-06-27-foundation-matrix/final-baseline/results.csv
docs/findings/benchmarks/2026-06-27-foundation-matrix/final-baseline/results.json
docs/findings/benchmarks/2026-06-27-foundation-matrix/search-inspect-recovery-hardening/results.csv
docs/findings/benchmarks/2026-06-27-foundation-matrix/search-inspect-recovery-hardening/results.json
docs/findings/benchmarks/2026-06-27-foundation-matrix/task3-retrieval-inspect-ambiguity/results.csv
docs/findings/benchmarks/2026-06-27-foundation-matrix/task3-retrieval-inspect-ambiguity/results.json
docs/plans/2026-05-31-workspace-registry-freshness-plan.md
docs/plans/2026-06-01-julie-extractors-migration-plan.md
docs/plans/2026-06-04-cli-workspace-open-remove-design.md
docs/plans/2026-06-04-symbol-search-collapsed-trigram-design.md
docs/plans/2026-06-05-source-regions-pillar3-implementation-plan.md
docs/plans/2026-06-07-content-corpus-fts5-search-plan.md
docs/plans/2026-06-07-incremental-search-sidecar.md
docs/plans/2026-06-09-miller-data-opportunities-plan.md
docs/plans/2026-06-09-miller-quality-review-goal-implementation-plan.md
docs/plans/2026-06-09-patterns-tool-implementation-plan.md
docs/plans/2026-06-09-reference-aware-context-design.md
docs/plans/2026-06-10-review-findings-fixes.md
docs/plans/2026-06-11-version-aware-leadership-design.md
docs/plans/2026-06-11-version-aware-leadership.md
docs/plans/2026-06-23-telemetry-workspace-onboarding-implementation-plan.md
docs/plans/2026-06-27-search-inspect-effectiveness-implementation-plan.md
docs/plans/2026-06-27-search-no-results-recall-plan.md
docs/plans/2026-07-02-guidance-delivery-design.md
docs/plans/2026-07-02-guidance-delivery-implementation.md
docs/plans/2026-07-02-tool-output-compaction.md
docs/plans/2026-07-05-rust-ct-impact-single-release.md
docs/plans/2026-07-06-background-bootstrap-design.md
docs/plans/2026-07-06-background-bootstrap-implementation-plan.md
docs/plans/2026-07-07-dead-code-candidates-implementation-plan.md
docs/plans/2026-07-07-metric-history-implementation-plan.md
docs/plans/2026-07-08-dashboard-registry-hygiene.md
docs/plans/2026-07-09-impact-traversal-evidence-implementation-plan.md
docs/plans/2026-07-12-telemetry-diagnosis-hardening.md
docs/plans/2026-07-16-agent-interaction-improvements.md
docs/plans/2026-07-17-julie-extract-2.15.0-adoption.md
docs/plans/2026-07-19-miller-semantic-integration-design.md
docs/plans/2026-07-19-p0-governance-and-gates-plan.md
docs/plans/2026-07-19-p1-freeze-and-conformance-plan.md
docs/plans/2026-07-20-p2-miller-lanes-plan.md
docs/plans/2026-07-20-p3-integration-plan.md
docs/plans/2026-07-20-p3-track1-sidecar-pins-plan.md
docs/plans/2026-07-20-semantic-p4-shadow-rollout.md
docs/plans/2026-07-21-semantic-p5-canary-plan.md
docs/release-notes/v0.1.0-beta.1.md
docs/release-notes/v1.4.0.md
```

Named-but-not-in-grep (excluded unconditionally; do not exist or carry no graded id at SHA 59c2c79):
`docs/plans/2026-07-21-encoder-comparison-fusion-v2-design.md`,
`docs/findings/2026-07-21-fused-arm-encoder-benchmark.md`.
</details>

---

## Results — Task 4: Arm generation + scoring

**Candidates run:** qwen3-0.6b-f16, bge-small-en-v1.5-f32, arctic-embed-s-f16 (research arm).
**CodeRankEmbed: FINAL DROP** — `drop-reason=converter`: the llama.cpp GGUF converter
(`conversion/bert.py:372`) crashes on all non-MoE NomicBert models through b10076 and master; no
released pin can convert it. The model itself is MIT-clean (rev `3c4b608`); a retry harness is staged
in `eval/model-bench/.cache/parity/` for a future fixed pin.

**Parity smoke (R3): PASS 5/5 exact.** 5 prose dev queries through live `miller search --arm hybrid`
vs the `eval/fusion-arm` adapter at fusion-v1 constants (k=60, Conceptual ratio 2:1) produced
identical top-10 doc rankings. Initial 3/5 FAIL exposed that the production lexical candidate pool is
**limit-dependent** (`overFetch = min(limit*4+10, 500)`), so arm dumps must capture the pool at
closure depth (limit 500) for exact parity; at closure the match is exact. Artifacts:
`eval/fusion-arm/out/parity-smoke/`.

**Arm inputs.** Lexical dumps: 82/82 dev queries against the frozen workspaces
(`--arm lexical --json --limit 50`, the plan's sweep depth). Semantic symbol rankings: `bench.py rank
--symbol-dump` (lead ownership extension) emits per-query top-20 symbol-level cosine rankings per
candidate at production serving shape (qwen3 512-dim / bge 384-dim / arctic 384-dim, all int8),
embedded under the pinned llama.cpp b10068 lanes against the frozen corpora.

**Scoring.** 43 arms scored by `eval/retrieval-eval` (k=10, cluster units): 1 lexical control +
3 candidates × {12 fused profiles, semantic-only, forced-hybrid}. Every report dir carries a
`meta.json` with the frozen SHAs and artifact ids from §Frozen substrate. To support per-unit paired
analysis the scorer was extended with a `units` block (all 48 evaluation units:
`cluster:<intent_cluster>` / `query:<query_id>`); all 43 arms were re-scored with the extended scorer
and headline metrics were verified unchanged (e.g. qwen3 k60-r2 overall nDCG 0.579363 before and
after). Scorer suite 32/32 green including the new reconciliation test.

**Route caveats (structural, apply to every table below):**
- Fused/lexical arms are **symbol-route only** (plan R4): doc-chunk answers are unreachable, so
  `docs_like` and the markdown language score **0 structurally** on those arms. The semantic-only
  arm is the bench **doc-level** arm (symbol cards + doc chunks), so its overall numbers are not
  route-comparable to the fused rows.
- Offline arms are unthresholded top-k: negatives false-positive count is 6/6 on every arm
  including lexical control — structural, report-only, not comparable to production abstention.

**Context arms (overall nDCG@10 / recall@10, cluster units):**

| arm | qwen3 | bge-small | arctic | lexical control |
|---|---|---|---|---|
| lexical control (candidate-independent) | — | — | — | 0.4830 / 0.5330 |
| semantic-only (doc-level, incl. chunks) | 0.6332 / 0.6806 | 0.5983 / 0.6979 | 0.5682 / 0.6771 | — |
| fused fusion-v1 (k60-r2, symbol route) | 0.5794 / 0.6094 | 0.5727 / 0.6163 | 0.5741 / 0.6476 | — |
| forced-hybrid diagnostic (v1 constants) | 0.5905 / 0.6458 | 0.5834 / 0.6632 | 0.5832 / 0.7049 | — |

Fusion-v1 lifts every encoder well above the lexical control (+0.09..0.10 nDCG). The identifier
routing guard held: identifier nDCG is byte-identical between lexical control and every fused arm
(0.9989) because `SemanticQueryPolicy` routes identifiers LexicalOnly; only the forced-hybrid
diagnostic moves it.

## Results — Task 5: Selection analysis

**Method.** `eval/fusion-arm/analyze.py --seed 20260721 --resamples 10000`, output
`eval/fusion-arm/out/analysis.json`. Selection statistic = mean of the two shippable encoders'
overall cluster-unit nDCG@10 (R5: one profile for both). Stability = leave-one-unit-out re-selection
over the 48 units + paired bootstrap (10,000 resamples, seed 20260721) winner-vs-v1 per encoder.

**Design erratum (recorded, does not alter any gate):** the design's profile grid line labels ratio
"1:1" as v1. Production fusion-v1 Conceptual weights are `FusionWeights(Lexical: 0.5, Semantic: 1.0)`
≡ ratio **2:1**, so the v1 control is **k60-r2** — confirmed empirically by the exact parity smoke at
those constants. The gates reference "fusion-v1 control", which is unambiguous; only the design's
ratio label was wrong.

**Sweep (overall cluster-unit nDCG@10):**

| profile | qwen3 | bge-small | selection stat |
|---|---|---|---|
| **k20-r2 (sweep winner)** | 0.5761 | 0.5786 | **0.5774** |
| k20-r3 | 0.5772 | 0.5771 | 0.5771 |
| k60-r4 | 0.5803 | 0.5734 | 0.5769 |
| k20-r4 | 0.5775 | 0.5761 | 0.5768 |
| **k60-r2 (= fusion-v1)** | 0.5794 | 0.5727 | 0.5760 |
| k120-r2 | 0.5794 | 0.5727 | 0.5760 |
| k60-r3 | 0.5805 | 0.5715 | 0.5760 |
| k120-r4 | 0.5781 | 0.5709 | 0.5745 |
| k120-r3 | 0.5781 | 0.5706 | 0.5744 |
| k20-r1 | 0.5655 | 0.5554 | 0.5604 |
| k120-r1 | 0.5653 | 0.5540 | 0.5596 |
| k60-r1 | 0.5652 | 0.5539 | 0.5596 |

The surface is flat outside r1: every r≥2 profile sits within 0.003 of v1. Only under-weighting
semantic (1:1) clearly loses.

**Stability + CI (winner k20-r2 vs v1 k60-r2):**

- LOUO: winner modal — k20-r2 wins 44/48 leave-one-out folds (k20-r3: 2, k60-r4: 1, k60-r2: 1).
- qwen3: ΔnDCG **−0.0033**, bootstrap CI95 **[−0.0201, +0.0080]** — CI includes zero, direction negative.
- bge-small: ΔnDCG **+0.0060**, bootstrap CI95 **[+0.0007, +0.0139]** — CI excludes zero.
- Regression gates (macro / worst-language / docs_like / identifier): all pass for both encoders
  (worst-language, docs_like, identifier deltas are exactly 0; see route caveats).

**Verdict — winner bar NOT met: fusion-v1 stands.** The bar requires the paired CI to exclude zero
for BOTH shippable encoders; qwen3's does not (and its point estimate is negative). No fusion-v2
ships; `RrfFusion` constants and `FusionProfile = "fusion-v1"` are unchanged; Task 7 is skipped by
its own precondition. This also resolves the program's motivating anecdote: the dev set shows
fusion-v1 is already at the flat top of the profile surface for qwen3 — the earlier WelchInterval
observation was a per-query anecdote, not a systematic under-weighting.

**Pin rule (pre-registered, applied at the shipping profile k60-r2):**

- Quality leg: bge fused overall 0.5727 ≥ qwen3 0.5794 × 0.97 = 0.5620 → **within 3% relative** (98.8%).
- Worst-language leg: both worst languages are markdown at 0 (structural symbol-route zero) →
  loss 0 ≤ 0.02 → **pass**, but note the leg was non-discriminative on this route.
- **Pin decision: bge-small-en-v1.5-f32 takes the default pin** (384-dim int8), per the
  pre-registered rule. Footprint rationale the rule encodes: 133.6 MB vs 1.198 GB download (9×),
  384 vs 512 dims, and Task 6's cost table (build wall-clock, memory) completes the record.
- Report-only context, both directions: on the doc-level semantic-only arm qwen3 leads overall
  (0.6332 vs 0.5983) while bge leads language-macro (0.6372 vs 0.6058) and markdown (bge's worst
  language is csharp 0.5797; qwen3's is markdown 0.5327). On the shipping fused route the two are
  within 1.2% relative, and bge is the encoder that actually benefits from fusion tuning
  (its winner-vs-v1 CI excludes zero).
- Implementation of the pin change (registry default, docs, conformance goldens) is recorded
  separately; per the design it happens iff the pin changed — it did.

## Results — Task 6: Real-artifact cost

> **STATUS (skeleton + protocol; medians pending machine-quiet).** Structure, methodology, download
> sizes, and the two contaminated qwen3 shakedown runs are recorded. Clean medians land after the
> concurrent model-bench embed lanes finish (they contend CPU/GPU and contaminate any timed run). A run
> counts only if 1-min loadavg < 4 at start; contaminated runs are kept raw and never averaged.

**What this measures.** The adopter cost of enabling Miller semantic retrieval for the first time on the
frozen-miller workspace, per encoder. Each run is a *clean initial vector build*: `MILLER_SEMANTIC=shadow`
(builds vectors without changing served bytes) on a serve process rooted at the frozen worktree, timed from
serve start until **both** vector cursors reach target — `symbol_completed_revision` **and**
`chunk_completed_revision` (in `vectors.db` `vectors_meta`) equal `*_target_revision` with
`build_state=ready`. `build_state` flips to `ready` when the *symbol* generation becomes queryable while the
*chunk* cursor is still building, so completion requires **both** cursors, not `ready` alone. Engine = the
production `julie-semantic-sidecar` 0.1.0-rc.2 driven by worktree `miller` 1.13.0 — **production-engine**
numbers, distinct from the offline eval harness (see the harness-not-engine row).

**Memory — read first.** `/usr/bin/time -l` wraps only the `miller` .NET host; the model lives in the
`julie-semantic-sidecar` **child**, which `time -l` never sees. Two figures therefore matter: the host's
`time -l` **peak memory footprint** (`phys_footprint`) and the **separately-sampled peak RSS of the sidecar
child**. macOS `time -l` "maximum resident set size" over-counts mapped pages and is *not* used.

**Corpus (frozen-miller, artifact `artifact-1784654234183324000`):** 49,276 symbols → **10,063 embeddable
symbol cards** + **792 doc chunks**. Default identity with `MILLER_SEMANTIC_MODEL` unset resolves to
**qwen3-0.6b-f16** (512-dim, storage `vec0-int8-512-cosine-v1`) — verified from `encoder_fingerprint`.

### Model download cost (already cached; sizes recorded, nothing re-downloaded)

| model | file (`~/.cache/julie-semantic/`) | bytes | size |
|---|---|---|---|
| qwen3-0.6b-f16 (default) | `Qwen3-Embedding-0.6B-f16.gguf` | 1,197,629,632 | 1.198 GB |
| bge-small-en-v1.5-f32 | `bge-small-en-v1.5-f32.gguf` | 133,609,568 | 133.6 MB |

### Cost table — medians of clean runs (loadavg < 4)

| Metric | qwen3-0.6b-f16 (default) | bge-small-en-v1.5-f32 |
|---|---|---|
| Model download | 1.198 GB | 133.6 MB |
| E2E build (both cursors), median [range] | _pending clean runs_ | _pending clean runs_ |
| Symbol converge s, median | _pending_ | _pending_ |
| Symbol throughput (cards/s) | _pending_ | _pending_ |
| Chunk converge s, median | _pending_ | _pending_ |
| `vectors.db` size | ~10.27 MiB (512-dim; confirm clean) | _pending (384-dim → smaller)_ |
| Host peak footprint (`time -l`) | _pending (~339 MiB indicative)_ | _pending_ |
| Sidecar child peak RSS (sampled) | _pending (~10.9 GiB indicative, contended)_ | _pending_ |
| Warm query embed — cold-load (first CLI) ms | _pending_ | _pending_ |
| Warm query embed — warm (median of 2–3) ms | _pending_ | _pending_ |

### Raw runs (all runs, contaminated included; medians drawn only from clean rows)

| run | model | loadavg@start | status | E2E s | sym converge s | cards/s | chunk converge s | vectors.db bytes | host peak footprint | sidecar peak RSS |
|---|---|---|---|---|---|---|---|---|---|---|
| qwen3-run1 | qwen3-0.6b-f16 | 6.68 (window) | **contaminated — discarded** | 318 (log-derived; harness poll pre-fix bogus 688.8) | ~183 | 56 | 134.6 | 10,768,384 | 355,238,776 B (339 MiB) | ~10.9 GiB (sampled) |
| qwen3-run2 | qwen3-0.6b-f16 | 7.30 (window) | **contaminated — discarded** | 332.0 | 203.6 | 50 | 128 (phase) | 10,768,384 | 353,928,128 B (337 MiB) | 12.2 GiB (sampled) |
| qwen3-clean-1 | qwen3-0.6b-f16 | _<4_ | _pending_ | | | | | | | |
| qwen3-clean-2 | qwen3-0.6b-f16 | _<4_ | _pending_ | | | | | | | |
| bge-clean-1 | bge-small-en-v1.5-f32 | _<4_ | _pending_ | | | | | | | |
| bge-clean-2 | bge-small-en-v1.5-f32 | _<4_ | _pending_ | | | | | | | |

### Warm query embed latency (3 CLI `search --arm semantic` per model, after build)

Each CLI call is a fresh process that spawns its own sidecar and loads the model, so call 1 = cold-load
(page-cache warm from the just-finished build), calls 2–3 = warm. Per-query wall ms:

| model | q1 (cold-load) ms | q2 (warm) ms | q3 (warm) ms |
|---|---|---|---|
| qwen3-0.6b-f16 | _pending_ | _pending_ | _pending_ |
| bge-small-en-v1.5-f32 | _pending_ | _pending_ | _pending_ |

### Research-arm bench wall-clock — harness-not-engine

The retrieval-eval **research arms** (Task 4's `eval/fusion-arm`) embed queries via the **model-bench**
lane — `llama.cpp` `llama-server` HTTP `/v1/embeddings` (pins `eval/model-bench/bench-pins.json`, native
dims, offline RRF replay over frozen arm dumps). That is a **different engine and code path** from Miller's
production `julie-semantic-sidecar` (512-dim int8, live serve). Its wall-clock therefore measures the eval
harness, **not** production embed throughput — do not compare it to the cost table above.

| lane | wall-clock | label |
|---|---|---|
| fusion-arm bench (qwen3 / bge / research) | _pending (task 4 lead)_ | **harness-not-engine** |

### What the evidence proves

First-time semantic enablement cost per encoder, on a real 49k-symbol workspace, as an initial clean vector
build through both cursors: model download, wall-clock to full convergence, per-cursor throughput,
`vectors.db` artifact size, host + sidecar peak memory, and warm query-embed latency. Clean medians are
gated on a quiet machine (loadavg < 4) so the numbers reflect the engine, not bench contention.
