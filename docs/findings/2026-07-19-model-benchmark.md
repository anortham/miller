# Embedding model benchmark — P0 pin recommendation

**Date:** 2026-07-19
**Gate:** P0 model gate for the semantic integration program
([design §2.4, §4.1, §8](../plans/2026-07-19-miller-semantic-integration-design.md))
**Harness:** [`eval/model-bench/`](../../eval/model-bench/README.md), scored by
[`eval/retrieval-eval/`](../../eval/retrieval-eval/README.md)
**Machine:** macos-arm64 (Apple Silicon, Metal), llama.cpp `b10068` prebuilt

## Revision — this supersedes the first version of this document

Pre-merge review found two defects in how the first run was measured. Both are fixed; every arm was
re-ranked from the same cached embeddings and re-scored, and **the default pin changed as a result**.

1. **Scoring corrected to cluster units (design §8).** The scorer averaged every positive query
   independently, so a 3-paraphrase intent cluster carried three times the weight of a distinct intent
   with one phrasing. §8 requires paraphrase clusters to be "scored as clusters, not independent
   samples". The primary headline, per-language, macro-average, and worst-language metrics now average
   over **48 evaluation units** (14 intent clusters + 34 standalone queries), where a cluster's score is
   the mean over its members. Per-query and cluster-max views are still reported, as secondary.
2. **Test units excluded from the ranking population (production parity).** The semantic arms ranked all
   34,481 corpus units, including 6,104 `is_test` units, while Miller's BM25 baseline auto-hides test
   code for natural-language queries and design §5.2 excludes test symbols "from default search recall
   via the metadata filter". The arms were competing over different doc populations. Semantic arms now
   rank the same 28,377-unit non-test population. No graded doc in the dev set is a test-only path, so
   this removed no relevant document from any query.

**What changed in the conclusion:** the default pin moves from **1024d int8 to 512d int8**. Under
cluster-unit scoring 512d takes the best overall recall, the best nDCG, the best macro nDCG, and — the
criterion that decided against 256d in the first version — **the best worst-language nDCG of any arm
measured** (0.5997 vs 1024d's 0.5327). The 1024d lane's weakness is markdown, which the per-query
weighting had diluted behind 72 code-language queries. The fallback pin is unchanged. Both fixes also
narrowed the semantic-vs-lexical gap, which remains decisive but is smaller than first reported.

Arm file names changed with the re-run (`qwen3-1024d-int8-topk` → `qwen3-0.6b-f16-1024d-int8-topk`);
the superseded short-named result files were deleted so no stale, unfiltered arm survives in the cache.

## Pin recommendation

**Default pin — `Qwen3-Embedding-0.6B`, f16 GGUF weights, `512` dims, `int8` vector storage.**

**Fallback pin — `bge-small-en-v1.5`, f32 GGUF weights, `384` dims, `int8` vector storage.**
Both fallback lanes completed end to end, so this pin is evidence-backed rather than inferred, as the
decision rule requires.

Every lane ran to completion — all six Qwen3 lanes (256/512/1024 × f32/int8), both fallback candidates
at f32/int8, and two BM25 baseline modes, each under both threshold policies. **No lane is incomplete
and no pin is recorded as OPEN.** The default pin is drawn only from completed Qwen3 lanes and the
fallback pin only from a completed fallback lane.

Four findings drive the recommendation:

1. **Semantic retrieval clears the lexical baseline decisively**, and precisely where it was supposed
   to. Cluster-unit recall@10 goes 0.5625 → **0.6979** (+24% relative) and nDCG@10 0.5073 → **0.6423**
   (+27% relative) against `bm25-symbol`. Per-query prose recall doubles (0.447 vs 0.223). Intent-cluster
   coverage goes 10/14 → **14/14**: every paraphrase cluster in the dev set is reachable.
2. **512d is the best Qwen3 lane, not 1024d.** It wins overall recall (0.6979 vs 0.6910), ties nDCG
   (0.6423 vs 0.6421), and wins macro nDCG decisively (0.6465 vs 0.6123) — but the deciding margin is
   **worst-language nDCG: 0.5997 against 0.5327**. It also costs half the storage. See the language view
   below for why 1024d loses: it is markedly worse on markdown.
3. **256d still does not hold up.** Design §2.4 named "256d int8 + higher-precision rescore" as the
   favored lane. It costs 8.5% relative recall against 512d and **32% relative worst-language nDCG**
   (0.4077 vs 0.5997). That verdict is unchanged from the first version and survives the corrected
   scoring.
4. **int8 vector storage is essentially free, and exactly free at the recommended lane.** At 512d, int8
   and f32 are identical to four decimal places on every primary metric. The claim is weaker than first
   reported at other lanes — see the re-verification below — but it holds where the pin lands.

Qwen3 is the default pin on quality, though the margin over the fallback is narrower than first
reported: it **ties** `bge-small` on cluster-unit recall (0.6979) and wins on nDCG (0.6423 vs 0.6157),
cluster coverage (14/14 vs 13/14), and prose. bge-small is the fallback over arctic on nDCG (0.6157 vs
0.5738), cluster coverage (13/14 vs 12/14), identifier nDCG (0.9977 vs 0.9747), and a cleaner license
story (MIT declared against an MIT base).

Two caveats P1 must carry, both detailed below: Qwen3 is **weaker than every alternative on
docs-like/markdown queries**, and **no arm can currently reject a negative query** at the default
threshold.

## Results

**Unit policy for every table below is stated in its heading.** *Cluster units* means the primary
design-§8 policy: 48 units (14 intent clusters scored as the mean over their paraphrases + 34 standalone
queries), covering 76 positive queries. *Per-query* means all 76 positives weighted equally.

**Threshold policy** is likewise stated per table. All headline tables are **`topk`** (raw top-10, no
threshold), the only policy comparable to the unthresholded BM25 baseline; see *Threshold policy* under
Method. The `thr` arms appear in the negatives section, which is the question they exist to answer.

### Headline comparison — cluster units, `topk` policy

| arm | recall@10 | nDCG@10 | macro recall | macro nDCG | worst lang | worst nDCG | clusters |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `bm25-symbol` *(baseline)* | 0.5625 | 0.5073 | 0.6818 | 0.5562 | csharp | 0.4847 | 10/14 |
| `bm25-auto` | 0.5573 | 0.5025 | 0.4053 | 0.3655 | markdown | 0.0000 | 13/14 |
| **`qwen3-512d-int8`** | **0.6979** | **0.6423** | 0.7121 | **0.6465** | csharp | **0.5997** | **14/14** |
| `qwen3-512d-f32` | 0.6979 | 0.6423 | 0.7121 | 0.6465 | csharp | 0.5997 | 14/14 |
| `qwen3-1024d-int8` | 0.6910 | 0.6421 | 0.7071 | 0.6123 | markdown | 0.5327 | 14/14 |
| `qwen3-1024d-f32` | 0.6944 | 0.6428 | 0.7096 | 0.6128 | markdown | 0.5327 | 14/14 |
| `qwen3-256d-int8` | 0.6389 | 0.6020 | 0.6010 | 0.5490 | markdown | 0.4077 | 14/14 |
| `qwen3-256d-f32` | 0.6389 | 0.6021 | 0.6010 | 0.5491 | markdown | 0.4077 | 14/14 |
| `bge-384d-int8` *(fallback)* | 0.6979 | 0.6157 | **0.7803** | 0.6499 | rust | 0.5908 | 13/14 |
| `bge-384d-f32` | 0.6979 | 0.6170 | 0.7803 | 0.6508 | rust | 0.5935 | 13/14 |
| `arctic-384d-int8` | 0.6667 | 0.5738 | 0.7576 | 0.6210 | csharp | 0.5527 | 12/14 |
| `arctic-384d-f32` | 0.6701 | 0.5735 | 0.7601 | 0.6207 | csharp | 0.5557 | 12/14 |

Arm names are shortened for readability; the cache files carry the full candidate id
(`qwen3-0.6b-f16-512d-int8-topk`, `bge-small-en-v1.5-f32-384d-int8-topk`,
`arctic-embed-s-f16-384d-int8-topk`).

### Secondary unit views — `topk` policy

The unit policy is a real decision, so the alternatives are published rather than asserted away.
`per-query` weights all 76 positives equally; `cluster-max` gives each cluster its **best** member's
score, answering "is this intent reachable by *some* phrasing?".

| arm | cluster recall *(primary)* | per-query recall | cluster-max recall |
| --- | --- | --- | --- |
| `bm25-symbol` *(baseline)* | 0.5625 | 0.4474 | 0.6250 |
| **`qwen3-512d-int8`** | **0.6979** | 0.6118 | 0.7708 |
| `qwen3-1024d-int8` | 0.6910 | 0.6250 | 0.7500 |
| `qwen3-256d-int8` | 0.6389 | 0.5658 | 0.7292 |
| `bge-384d-int8` | 0.6979 | 0.5855 | 0.7812 |
| `arctic-384d-int8` | 0.6667 | 0.5789 | 0.7292 |

Note that 512d and 1024d **swap** between the primary and per-query views (0.6979/0.6910 cluster vs
0.6118/0.6250 per-query). That inversion is exactly the defect the review caught: the pin depends on
which unit is the unit, and design §8 settles it in favor of clusters.

### Identifier non-inferiority — per-query, standalone semantic arm

The `identifier` class is the non-inferiority set: semantic retrieval must not degrade what lexical
already does perfectly. This block is **per-query** by construction — query classes cut across intent
clusters — and measures the **semantic arm alone**, without fusion.

| arm | identifier recall@10 | Δ vs baseline | identifier nDCG@10 | Δ vs baseline |
| --- | --- | --- | --- | --- |
| `bm25-symbol` *(baseline)* | 1.0000 | — | 1.0000 | — |
| **`qwen3-512d-int8`** | 1.0000 | **0.0000** | 0.9759 | −0.0241 |
| `qwen3-1024d-int8` | 1.0000 | 0.0000 | 0.9980 | −0.0020 |
| `qwen3-256d-int8` | 1.0000 | 0.0000 | 0.9519 | −0.0481 |
| `bge-384d-int8` | 1.0000 | 0.0000 | 0.9977 | −0.0023 |
| `arctic-384d-int8` | 1.0000 | 0.0000 | 0.9747 | −0.0253 |

**Non-inferiority holds on recall exactly: every arm retrieves all 16 identifier queries' relevant docs
inside k, unchanged by the corrected scoring.** The only movement is ordering.

This is the one axis where the recommended 512d lane is worse than 1024d: −0.0241 nDCG against −0.0020,
meaning it occasionally places the right file one rank lower. Identifier nDCG still degrades
monotonically as dims shrink (0.9980 → 0.9759 → 0.9519), so this is a genuine cost of the 512d choice,
not noise. It is accepted because recall — whether the answer is retrievable at all — is unaffected,
because the deficit is an ordering effect inside a set the arm already fully recalls, and because in
production the bar is easier still: the lexical path stays byte-identical (ADR-0003) and the semantic arm
is fused on top by weighted RRF, so hybrid retrieval cannot fall below lexical on identifiers by
construction. The number to watch in P1 is fusion weighting, not the encoder.

### Per-language — cluster units, `topk` policy — the language-parity view

Each cell is recall / nDCG. Unit counts: csharp 22 units (36 queries), rust 22 units (36 queries),
markdown 4 units (4 queries — markdown carries no intent clusters, so its units are its queries).

| arm | csharp | rust | markdown |
| --- | --- | --- | --- |
| `bm25-symbol` *(baseline)* | 0.515 / 0.485 | 0.530 / 0.497 | 1.000 / 0.687 |
| `bm25-auto` | 0.629 / 0.565 | 0.587 / 0.531 | 0.000 / 0.000 |
| **`qwen3-512d-int8`** | 0.614 / 0.600 | **0.773 / 0.682** | 0.750 / 0.658 |
| `qwen3-1024d-int8` | **0.621 / 0.625** | 0.750 / 0.679 | 0.750 / 0.533 |
| `qwen3-256d-int8` | 0.591 / 0.577 | 0.712 / 0.663 | 0.500 / 0.408 |
| `bge-384d-int8` | 0.621 / 0.618 | 0.720 / 0.591 | **1.000 / 0.741** |
| `arctic-384d-int8` | 0.598 / 0.553 | 0.674 / 0.563 | 1.000 / 0.747 |

**This table is why the pin moved.** 1024d edges 512d on csharp (0.625 vs 0.600 nDCG) but loses markdown
badly (0.533 vs 0.658) and rust narrowly. Under per-query weighting the 72 code-language queries buried
the markdown gap; under the language-parity rule the worst language *is* the number, and 512d's floor
(csharp 0.5997) sits well above 1024d's (markdown 0.5327).

Every Qwen3 lane still **loses markdown** to both fallbacks and to `bm25-symbol`.

### Per-query-class, recommended lane vs baseline — per-query, `topk` policy

Per-query by construction: query classes cut across intent clusters.

| class | n | qwen3-512d-int8 r/nDCG | bm25-symbol r/nDCG | verdict |
| --- | --- | --- | --- | --- |
| prose | 47 | **0.447 / 0.368** | 0.223 / 0.149 | semantic wins decisively — the program's core thesis |
| identifier | 16 | 1.000 / 0.976 | 1.000 / 1.000 | non-inferior on recall; −0.024 nDCG ordering |
| short_token | 6 | **0.917 / 0.768** | 0.583 / 0.544 | semantic wins |
| docs_like | 4 | 0.750 / 0.658 | **1.000 / 0.687** | **lexical wins** |
| path | 2 | 0.250 / 0.183 | 0.000 / 0.000 | semantic wins (both weak) |
| mixed | 1 | 0.500 / 0.917 | 0.000 / 0.000 | semantic wins |

**The docs-like regression is real and should not be smoothed over.** Qwen3 is beaten on docs-like and
markdown by BM25 *and* by both fallbacks, consistently across the two views. The most likely cause is
corpus shape rather than the model: docs enter as ~1,200-char windows of ~7KB files, so a markdown
answer is spread across many partial chunks, while BM25 scores the whole file. `n=4` docs_like and `n=4`
markdown queries is far too small to pin a cause on, so this is flagged for P1 to investigate with a
larger docs slice — not treated as settled.

It is also a direct argument for the design's hybrid-fusion plan rather than a semantic-only path:
lexical wins docs_like and ties identifier on recall, semantic wins prose by 2×, and fusion should keep
both.

### int8 vs f32 — re-verification of the "storage is free" claim

Cluster units, `topk`. The first version claimed int8 was within 0.0002 nDCG at every lane and identical
at 1024d. Under the corrected pipeline that is **not** uniformly true, so the claim is restated:

| lane | f32 recall / nDCG | int8 recall / nDCG | Δ recall | Δ nDCG |
| --- | --- | --- | --- | --- |
| **qwen3 512d** *(recommended)* | 0.6979 / 0.6423 | 0.6979 / 0.6423 | **+0.0000** | **+0.0000** |
| qwen3 256d | 0.6389 / 0.6021 | 0.6389 / 0.6020 | +0.0000 | −0.0001 |
| qwen3 1024d | 0.6944 / 0.6428 | 0.6910 / 0.6421 | −0.0035 | −0.0007 |
| bge 384d | 0.6979 / 0.6170 | 0.6979 / 0.6157 | +0.0000 | −0.0012 |
| arctic 384d | 0.6701 / 0.5735 | 0.6667 / 0.5738 | −0.0035 | +0.0004 |

**Verdict: int8 is free at the recommended 512d lane — exactly, to four decimal places on both metrics —
and cheap everywhere else.** The worst observed cost is −0.0035 recall (one cluster unit shifting by a
fraction, at 1024d and on arctic), against a 4× storage saving. Note the arctic row moves in opposite
directions on the two metrics, which is the signature of quantization noise rather than degradation. The
recommendation stands, but "identical at every lane" was an artifact of the earlier measurement and is
withdrawn.

Storage for the 28,377-unit ranked population: 512d int8 is **13.9 MB** against 55.4 MB at f32, and
against 27.7 MB for 1024d int8. The recommended lane is half the storage of the previously recommended
one and a quarter of 1024d f32.

### Negatives, and the threshold sweep

**No arm passes a single negative query at the default policy** — false-positive rate is 1.0000 (6/6)
for all 24 arms, semantic and lexical alike. The shipped default floor of 0.35 is simply too low to
reject anything; it is dominated and should not carry into P1. This is unchanged by both fixes.

Sweeping the absolute cosine floor on `qwen3-1024d-f32` (relative band disabled, k=10; cluster units for
recall/nDCG, per-query shown for reference):

| floor | cluster recall@10 | cluster nDCG@10 | per-query recall | negative FP rate | note |
| --- | --- | --- | --- | --- | --- |
| 0.35 *(shipped default)* | 0.6944 | 0.6428 | 0.6316 | 1.0000 (6/6) | dominated |
| 0.40 | 0.6944 | 0.6428 | 0.6316 | 1.0000 (6/6) | dominated |
| **0.45** | **0.6944** | **0.6428** | 0.6316 | 0.6667 (4/6) | **strictly free** — identical quality, 33% fewer FPs |
| 0.50 | 0.6910 | 0.6420 | 0.6250 | 0.5000 (3/6) | −0.5% recall, half the FPs |
| 0.55 | 0.6528 | 0.6246 | 0.5789 | 0.3333 (2/6) | still above BM25's 0.5625 |
| 0.60 | 0.6146 | 0.6055 | 0.5197 | 0.1667 (1/6) | still above BM25 |
| 0.65 | 0.5278 | 0.5335 | 0.3947 | **0.0000 (0/6)** | full abstention, but now below BM25 |

Two conclusions, both surviving the corrected scoring. Abstention **is** achievable — but only at 0.65,
where cluster recall (0.5278) falls below the lexical baseline (0.5625) and the feature stops paying for
itself. And **0.45 is a free win**: identical recall and nDCG to the shipped default while rejecting a
third of the negatives. P1 should start threshold tuning in the **0.45–0.55** band, where the arm still
beats BM25 by a comfortable margin while rejecting half the negatives.

This does not change the model/dims/quantization pin — the sweep moves all lanes together — but it is a
P1 blocker for any "semantic can decline to answer" claim, and it is why the negatives column is uniform
across the headline table rather than discriminating between arms.

### Throughput and load (report-only)

Unchanged by this revision: both fixes are downstream of embedding, and the cached vectors were reused
rather than regenerated.

| candidate | model load | corpus embed | units/sec | warm query embed |
| --- | --- | --- | --- | --- |
| Qwen3-0.6B f16 (1024d) | 13.2s cold / 1.0s warm | 659.5s for 34,481 units | 52.3 | 11.0 ms |
| bge-small-en-v1.5 f32 (384d) | 10.6s | 153.9s | 224.0 | 3.9 ms |
| snowflake-arctic-embed-s f16 (384d) | 0.5s | 136.5s | 252.7 | 3.5 ms |

Report-only, but two observations P1 should carry:

- **Warm query embed is comfortably inside budget.** 11.0 ms against the design's 10–150 ms target
  (§4.2), with ~4 ms of headroom to spare on the fallbacks.
- **Initial corpus build is the risk, not query latency.** Qwen3 runs at 52 units/sec, so these two
  workspaces take ~11 minutes. A 500k-unit workspace extrapolates to roughly **2.7 hours**, against the
  design's "initial build minutes, not hours" bar (§5.2). The fallbacks are ~4.5× faster and would land
  the same workspace near 35 minutes. This is a convergence/batching problem for P1 (§5.3), not a reason
  to change the model pin — but the "minutes not hours" claim does not survive contact with a large
  workspace on Qwen3 at this batch shape, and should be re-measured with the real sidecar's batching
  before it is repeated.

The embedding corpus is all 34,481 units; only *ranking* excludes the 6,104 test units. Embedding cost
therefore does not fall with the filter — but note MRL means the 512d pin costs nothing extra to embed,
since one 1024d pass serves every dims lane.

Measured with `-b 8192 -ub 8192`, batch 16 (Qwen3) / 32 (fallbacks), Metal, single server slot. The
harness drives HTTP round-trips per batch, so these are floors — the resident sidecar of §4.2 should
beat them.

### Fallback truncation caveat

Both fallbacks have 512-token contexts against Qwen3's 32K, so **22.0% of corpus units (7,603 of
34,481) were truncated** to fit them, at an 819-char budget versus the ~1,200-char cards Qwen3 saw
whole. Their numbers are therefore measured under a real handicap. This is a genuine capability
difference rather than a harness artifact — a 512-token model cannot see a full card — but it means the
fallback gap would narrow somewhat under a card template tuned for short-context models. It is worth
noting that bge-small **ties the recommended Qwen3 lane on cluster-unit recall despite this handicap**,
which strengthens rather than weakens it as a fallback. Recorded in each candidate's `.perf.json`.

## Method

### What was benchmarked

Three candidates, all sha256-pinned in
[`bench-pins.json`](../../eval/model-bench/bench-pins.json) with URLs, sizes, licenses, and pooling:

| candidate | tier | dims | pooling | license | source |
| --- | --- | --- | --- | --- | --- |
| Qwen3-Embedding-0.6B (f16 GGUF) | default | 1024 (MRL 256/512/1024) | `last` | Apache-2.0 | `Qwen/Qwen3-Embedding-0.6B-GGUF` (official Qwen org) |
| bge-small-en-v1.5 (f32 GGUF) | fallback | 384 | `cls` | MIT | `CompendiumLabs/bge-small-en-v1.5-gguf` |
| snowflake-arctic-embed-s (f16 GGUF) | fallback | 384 | `cls` | Apache-2.0 | `ChristianAzinn/snowflake-arctic-embed-s-gguf` |

Rejected without benchmarking, per the design's license rule: EmbeddingGemma (HF license-gated),
jina-code (CC-BY-NC), CodeRankEmbed (unclear license), and
`yixuan-chia/snowflake-arctic-embed-s-GGUF` (declares no license at all).

Two license facts are worth stating plainly rather than burying:

- **Snowflake publishes no official GGUF for arctic-embed-s.** The Snowflake org ships safetensors and
  ONNX only; the one Snowflake repo carrying a `gguf` tag is arctic-embed-**m-v1.5**, a different model.
  The pinned artifact is therefore a community conversion whose declared Apache-2.0 matches the
  Apache-2.0 base model.
- **bge-small-en-v1.5 has no official BAAI GGUF either.** The pinned conversion declares MIT in its own
  card frontmatter, matching the MIT base model.

Both fallbacks are community conversions. That is a supply-chain fact P1 must weigh when it decides
whether a fallback ships, not something the benchmark can resolve.

### The evaluation unit — clusters, not queries

Design §8 requires paraphrase intent clusters to be "scored as clusters, not independent samples". The
scorer therefore averages the primary metrics over **evaluation units**:

- each non-empty `intent_cluster` is **one unit**, scored as the **mean over its member paraphrases** —
  the expected retrieval quality over a random phrasing of that intent;
- each positive query with no cluster is **one unit**.

The dev set yields **48 units** from 76 positive queries: 14 clusters × 3 paraphrases, plus 34
standalone. Without this, the 14 mined clusters would carry 42 of 76 votes (55%) while representing 14
of 48 intents (29%), over-weighting whichever subsystems happened to be mined for paraphrases.

The harness also reports `overall_per_query`, `overall_cluster_max`, and `per_language_per_query` as
secondary views, plus the existing `intent_cluster_summary` hit coverage. `per_query_class` remains
per-query because query classes cut across clusters — every table above is labeled with the unit policy
it uses. Invariance tests in `ScorerTests` pin the property that matters: adding a paraphrase to a
cluster, or duplicating one verbatim, cannot change that cluster's weight in the primary metric, while
the per-query view visibly drifts.

### Corpus, and the ranking population

34,481 units built from both pinned workspaces' own Miller artifacts — symbol cards from `symbols.db` on
the design's §5.2 v1 card template, plus docs/config chunks from `content.db` windowed to the same
~1,200-char budget.

`doc_id` is the repo-relative file path, matching the golden set's vocabulary. The dev set carries zero
`#Symbol` suffixes, so ranking is file-granular: a file's score is its best-scoring unit.

**Ranking excludes `is_test` units** — 6,104 of 34,481, leaving a 28,377-unit population. Design §5.2
gives test symbols cards but excludes them "from default search recall via the metadata filter", and
Miller's BM25 baseline already auto-hides test code for natural-language queries
(`SearchTool.ResolveExcludeTests`). Before this filter the semantic arms ranked 178 test-path entries
against BM25's 58 — competing over a doc population the shipped surface never returns. Two facts keep
this honest:

- **It cannot manufacture a win.** No graded doc in the dev set resolves to a test-only path, verified
  against the corpus, so no relevant document was removed from any query's candidate pool. The single
  golden doc with any test units (`crates/julie-core/src/string_similarity.rs`) retains its non-test
  units.
- **A residual asymmetry remains, and it is production-faithful.** BM25 still returns 11 test-path
  entries, all on `identifier`/`path` queries, because Miller's auto-hide applies to natural-language
  phrasings only. Since no golden doc is test-only, this changes neither arm's recall.

Card eligibility is **derived, not declared**: a language earns symbol cards only where a real extract
shows it emits at least one code kind. On these workspaces that excludes exactly `json`, `markdown`,
`yaml`, and `toml` — matching the design's predicted list, but as evidence rather than assumption. The
full per-language matrix is emitted into the corpus manifest.

### The golden-set self-retrieval trap

`eval/retrieval-eval/sets/**` lives inside the miller workspace and contains both the query text and
the answer paths, so any arm indexing it retrieves its own answer key. `eval/`, `.razorback/`, and
`.claude/` are excluded from **every** arm, and the corpus builder emits a `golden_set_leak_check`
block that fails the build on any leak.

It bit the lexical arm harder than the semantic one. The live miller index covers
`.claude/worktrees/**`, so an unfiltered BM25 run retrieves this harness's own source — during
bring-up the top hit for a promotion query was `bench.py`'s own `SANITY_PAIRS` literal, which
contains the answer text verbatim. With identical exclusions applied, **1,519 of 4,843** raw BM25
hits were filtered. Both arms compete over the same doc space; otherwise the comparison is
meaningless.

### Pooling sanity gate

Wrong pooling degrades *silently* — vectors stay plausible and cosines stay high while
discrimination disappears. No candidate is scored until it separates a known-similar pair from a
known-dissimilar one by a ≥0.10 cosine margin, with output dims matching the pin file.

| run | similar | dissimilar | margin | verdict |
| --- | --- | --- | --- | --- |
| Qwen3, correct pooling (`last`) | 0.6005 | 0.1518 | **0.4487** | PASS |
| bge-small-en-v1.5 (`cls`) | 0.6679 | 0.5003 | **0.1677** | PASS |
| snowflake-arctic-embed-s (`cls`) | 0.6202 | 0.4229 | **0.1973** | PASS |
| *negative control:* Qwen3 forced to `cls` | 0.8979 | 0.8419 | **0.0560** | **FAIL** (exit 3) |

The negative control is the point: under wrong pooling both cosines go **up**, and only the margin
exposes the damage. A benchmark without this gate can produce a confident, entirely wrong pin.

### Weight precision vs storage precision

These are different axes and the design's "quantization" means the second one:

- **Weight precision** is fixed per candidate at the highest each repo publishes. The official Qwen
  GGUF repo publishes only f16 and Q8_0 — **there is no f32 Qwen3 GGUF**. f16 is therefore the
  highest-precision weight lane that exists, and is what ran.
- **Storage precision** is the benchmark variable: each dims lane is ranked at both `f32` and `int8`
  vector storage, `int8` being symmetric per-vector quantize→dequantize.

Qwen3 is MRL, so **one** embedding pass serves all three dims lanes: slice to 256/512/1024 **then**
renormalize, in that order (design §4.1).

### Threshold policy — why every lane is reported twice

The scorer requires post-threshold results, and negative-query scoring depends on it. But threshold
strength trades directly against recall@10, so a single policy cannot answer both questions:

- **`topk`** — raw top-10, no threshold. Matched to the BM25 baseline, which is unthresholded
  because Miller's CLI already returns exactly what it would show a user. This is the apples-to-apples
  arm for **recall@10 / nDCG@10**.
- **`thr`** — absolute cosine floor 0.35 plus a relative band at 0.85 of the query's best hit. This
  arm emits ~1.5 docs per query, so its recall is capped by construction; it exists to show
  **negative-query and precision behavior**.

Scoring a thresholded semantic arm against an unthresholded top-10 baseline would understate every
model and could produce a wrong pin. Both policies are reported for every arm, semantic and lexical.

## Reproduction

```bash
eval/model-bench/run-bench.sh                       # full run, clean cache
CANDIDATES="qwen3-0.6b-f16" eval/model-bench/run-bench.sh   # one candidate
RANK_ONLY=1 eval/model-bench/run-bench.sh           # re-rank + re-score from cached vectors
```

Every artifact is re-verified against its pinned sha256 on each run; a mismatch aborts. Embeddings are
cached, so re-ranking a lane is seconds. **This revision was produced with `RANK_ONLY=1`** — the
embeddings are byte-identical to the first run, and only the ranking population and the scorer changed.

## What P1 inherits

Direct consumers of this document are `scripts/semantic-pins.json` and the sidecar's model manifest.

**Carry forward as decided:**

- Default: Qwen3-Embedding-0.6B, f16 GGUF, **512 dims, int8 vector storage**, pooling `last`,
  `<|endoftext|>` append, instruction-prefixed queries, MRL slice-then-renormalize.
- Fallback: bge-small-en-v1.5, f32 GGUF, **384 dims, int8**, pooling `cls`. Evidence-backed.
- int8 vector storage everywhere — measured exactly free at the pinned 512d lane, ≤0.0035 recall
  elsewhere.
- Cluster-unit scoring is the decision metric for every future retrieval comparison (design §8). Any
  arm compared against these numbers must be scored the same way and over the same non-test ranking
  population, or the comparison is void.

**Amend in the design:**

- §2.4's favored "256d int8" lane should become **512d int8**. 256d fails the language-parity bar it
  would be judged by (worst-language nDCG 0.4077 vs 0.5997). 1024d is available if a later workload
  shows csharp-heavy identifier ordering matters more than markdown parity, but it costs 2× storage and
  loses worst-language today.

**Open, with evidence attached:**

1. **Negative-query abstention.** No threshold both rejects negatives and beats BM25 today. Start
   tuning at floor 0.45–0.55; treat the shipped 0.35 default as dead. Any claim that semantic
   retrieval can decline to answer needs this closed first.
2. **Docs-like retrieval.** Qwen3 loses docs_like and markdown to lexical *and* to both fallbacks —
   and under cluster-unit scoring this is what determines the dims pin, so it is no longer a footnote.
   Suspected cause is the docs chunk-window shape, not the encoder. Needs a larger docs slice than
   `n=4` before anyone concludes anything.
3. **Identifier ordering at 512d.** The pinned lane gives up 0.0241 identifier nDCG against 1024d at
   equal recall. Fusion weighting is expected to absorb it; P1 should confirm that on the hybrid arm
   rather than assume it.
4. **Initial-build throughput.** 52 units/sec puts a large workspace at hours, not minutes. Re-measure
   against the real sidecar's batching before repeating the §5.2 "minutes not hours" claim.
5. **Fallback supply chain.** Neither fallback has an official first-party GGUF; both pinned
   artifacts are community conversions. P1 owns whether that is acceptable for a shipped escape
   hatch, or whether the sidecar should convert from first-party weights itself.

**Do not re-derive from the dev set.** This benchmark tunes against the dev set, which is what it is
for. The sealed acceptance set (`eval/retrieval-eval/sets/SEALED-SET-PROTOCOL.md`) is user-owned and
must not be touched during P1 tuning.
