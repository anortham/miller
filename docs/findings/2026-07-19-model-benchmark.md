# Embedding model benchmark — P0 pin recommendation

**Date:** 2026-07-19
**Gate:** P0 model gate for the semantic integration program
([design §2.4, §4.1, §8](../plans/2026-07-19-miller-semantic-integration-design.md))
**Harness:** [`eval/model-bench/`](../../eval/model-bench/README.md), scored by
[`eval/retrieval-eval/`](../../eval/retrieval-eval/README.md)
**Machine:** macos-arm64 (Apple Silicon, Metal), llama.cpp `b10068` prebuilt

## Pin recommendation

**Default pin — `Qwen3-Embedding-0.6B`, f16 GGUF weights, `1024` dims, `int8` vector storage.**

**Fallback pin — `bge-small-en-v1.5`, f32 GGUF weights, `384` dims, `int8` vector storage.**
Both fallback lanes completed end to end, so this pin is evidence-backed rather than inferred, as the
decision rule requires.

Every lane below ran to completion — all six Qwen3 lanes (256/512/1024 × f32/int8), both fallback
candidates at f32/int8, and two BM25 baseline modes, each under both threshold policies. **No lane is
incomplete and no pin is recorded as OPEN.**

Three findings drive the recommendation, and one of them contradicts the design's prior:

1. **Semantic retrieval clears the lexical baseline decisively**, and precisely where it was supposed
   to. Prose recall@10 more than doubles (0.479 vs 0.223) and prose nDCG@10 more than doubles
   (0.378 vs 0.149) against `bm25-symbol`. Intent-cluster coverage goes 10/14 → **14/14**: every
   paraphrase cluster in the dev set is reachable.
2. **int8 vector storage is free.** At every dims lane, int8 scores within 0.0002 nDCG of f32 — at
   1024d the two are identical to four decimal places. There is no measured reason to spend 4× the
   storage on f32 vectors.
3. **256d does not hold up, and the design's favored lane should change.** Design §2.4 named "256d
   int8 + higher-precision rescore" as the favored lane. On this evidence 256d costs 9.6% relative
   recall and — the part that matters under Miller's language-parity rule — **22% relative
   worst-language nDCG** (0.4077 vs 0.5227). 1024d int8 costs 1KB per vector against 256B, which is
   ~35MB for this 34,481-unit corpus. That is a cheap price for the quality, so the recommendation is
   1024d. If storage pressure later makes 1024d untenable, **512d int8 is the compromise** (−3.2%
   recall, best macro nDCG of any arm at 0.5718); 256d should not be the default.

Qwen3 is the default pin on quality: it wins overall recall@10, overall nDCG@10, worst-language nDCG,
prose, and cluster coverage against both fallbacks. bge-small is the fallback over arctic on nDCG
(0.4895 vs 0.4483), cluster coverage (13/14 vs 12/14), identifier nDCG (0.9976 vs 0.9516), and a
cleaner license story (MIT declared against an MIT base).

Two caveats P1 must carry, both detailed below: Qwen3 is **weaker than every alternative on
docs-like/markdown queries**, and **no arm can currently reject a negative query** at the default
threshold.

## Results

All tables are **`topk` policy** (raw top-10, no threshold) unless the row name says `thr`. `topk` is
the only policy comparable to the BM25 baseline; see *Threshold policy* under Method. The `thr`
arms appear in the negatives section, which is the question they exist to answer.

### Headline comparison (`topk`)

| arm | recall@10 | nDCG@10 | macro recall | macro nDCG | worst lang | worst nDCG | prose recall | ident recall | ident nDCG | clusters |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `bm25-symbol` *(baseline)* | 0.4474 | 0.3820 | 0.6111 | 0.4722 | csharp | 0.3183 | 0.2234 | 1.0000 | 1.0000 | 10/14 |
| `bm25-auto` | 0.4836 | 0.4017 | 0.3403 | 0.2827 | markdown | 0.0000 | 0.3298 | 1.0000 | 1.0000 | 13/14 |
| **`qwen3-1024d-int8`** | **0.6184** | **0.5454** | 0.6574 | 0.5417 | csharp | **0.5227** | **0.4787** | 1.0000 | 0.9980 | **14/14** |
| `qwen3-1024d-f32` | 0.6184 | 0.5454 | 0.6574 | 0.5417 | csharp | 0.5227 | 0.4787 | 1.0000 | 0.9980 | 14/14 |
| `qwen3-512d-int8` | 0.5987 | 0.5356 | 0.6435 | **0.5718** | csharp | 0.4976 | 0.4255 | 1.0000 | 0.9759 | 14/14 |
| `qwen3-512d-f32` | 0.5987 | 0.5356 | 0.6435 | 0.5718 | csharp | 0.4976 | 0.4255 | 1.0000 | 0.9759 | 14/14 |
| `qwen3-256d-int8` | 0.5592 | 0.5161 | 0.5417 | 0.4840 | markdown | 0.4077 | 0.4149 | 1.0000 | 0.9519 | 14/14 |
| `qwen3-256d-f32` | 0.5592 | 0.5162 | 0.5417 | 0.4841 | markdown | 0.4077 | 0.4149 | 1.0000 | 0.9519 | 14/14 |
| `bge-384d-int8` *(fallback)* | 0.5724 | 0.4879 | **0.6991** | 0.5629 | rust | 0.4595 | 0.3617 | 1.0000 | 0.9977 | 13/14 |
| `bge-384d-f32` | 0.5724 | 0.4895 | 0.6991 | 0.5640 | rust | 0.4624 | 0.3617 | 1.0000 | 0.9976 | 13/14 |
| `arctic-384d-int8` | 0.5658 | 0.4482 | 0.6944 | 0.5367 | csharp | 0.4186 | 0.4043 | 1.0000 | 0.9516 | 12/14 |
| `arctic-384d-f32` | 0.5658 | 0.4483 | 0.6944 | 0.5367 | csharp | 0.4241 | 0.4043 | 1.0000 | 0.9516 | 12/14 |

### Identifier non-inferiority (`topk`)

The `identifier` class is the non-inferiority set: semantic retrieval must not degrade what lexical
already does perfectly.

| arm | identifier recall@10 | Δ vs baseline | identifier nDCG@10 | Δ vs baseline |
| --- | --- | --- | --- | --- |
| `bm25-symbol` *(baseline)* | 1.0000 | — | 1.0000 | — |
| `qwen3-1024d-int8` | 1.0000 | **0.0000** | 0.9980 | −0.0020 |
| `qwen3-512d-int8` | 1.0000 | 0.0000 | 0.9759 | −0.0241 |
| `qwen3-256d-int8` | 1.0000 | 0.0000 | 0.9519 | −0.0481 |
| `bge-384d-int8` | 1.0000 | 0.0000 | 0.9977 | −0.0023 |
| `arctic-384d-int8` | 1.0000 | 0.0000 | 0.9516 | −0.0484 |

**Every arm retrieves all 16 identifier queries' relevant docs inside k.** The only movement is
ordering: the recommended lane gives up 0.0020 nDCG, meaning it occasionally places the right file
one rank lower. Note that identifier nDCG degrades monotonically as dims shrink (0.9980 → 0.9759 →
0.9519), which is a second, independent argument against the 256d lane.

This measures the *semantic arm alone*, which is the strict reading. In production the bar is easier
to hold: the lexical path stays byte-identical (ADR-0003) and the semantic arm is fused on top by
weighted RRF, so hybrid retrieval cannot fall below lexical on identifiers by construction. The
number to watch in P1 is fusion weighting, not the encoder.

### Per-language (`topk`) — the language-parity view

| arm | csharp r/nDCG | rust r/nDCG | markdown r/nDCG |
| --- | --- | --- | --- |
| `bm25-symbol` *(baseline)* | 0.361 / 0.318 | 0.472 / 0.412 | 1.000 / 0.687 |
| `bm25-auto` | 0.486 / 0.398 | 0.535 / 0.450 | 0.000 / 0.000 |
| `qwen3-1024d-f32` | **0.528 / 0.523** | **0.694 / 0.570** | 0.750 / 0.533 |
| `qwen3-512d-f32` | 0.514 / 0.498 | 0.667 / 0.560 | 0.750 / 0.658 |
| `qwen3-256d-f32` | 0.486 / 0.487 | 0.639 / 0.557 | 0.500 / 0.408 |
| `bge-384d-f32` | 0.514 / 0.488 | 0.583 / 0.462 | **1.000 / 0.741** |
| `arctic-384d-f32` | 0.528 / 0.424 | 0.556 / 0.439 | 1.000 / 0.747 |

The recommended lane wins csharp and rust — the two code languages, and 72 of the 76 positive
queries — by a wide margin over every alternative. It **loses markdown** to both fallbacks and to
`bm25-symbol`.

### Per-query-class, recommended lane vs baseline (`topk`)

| class | n | qwen3-1024d r/nDCG | bm25-symbol r/nDCG | verdict |
| --- | --- | --- | --- | --- |
| prose | 47 | **0.479 / 0.378** | 0.223 / 0.149 | semantic wins decisively — the program's core thesis |
| identifier | 16 | 1.000 / 0.998 | 1.000 / 1.000 | non-inferior |
| short_token | 6 | **0.750 / 0.721** | 0.583 / 0.544 | semantic wins |
| docs_like | 4 | 0.750 / 0.533 | **1.000 / 0.687** | **lexical wins** |
| path | 2 | 0.250 / 0.166 | 0.000 / 0.000 | semantic wins (both weak) |
| mixed | 1 | 0.500 / 0.917 | 0.000 / 0.000 | semantic wins |

**The docs-like regression is real and should not be smoothed over.** Qwen3 is beaten on docs-like
and markdown by BM25 *and* by both fallbacks, consistently across the two views. The most likely
cause is corpus shape rather than the model: docs enter as ~1,200-char windows of ~7KB files, so a
markdown answer is spread across many partial chunks, while BM25 scores the whole file. `n=4`
docs_like and `n=4` markdown queries is far too small to pin a cause on, so this is flagged for P1 to
investigate with a larger docs slice — not treated as settled.

It is also a direct argument for the design's hybrid-fusion plan rather than a semantic-only path:
lexical wins docs_like and ties identifier, semantic wins prose by 2×, and fusion should keep both.

### Negatives, and the threshold sweep

**No arm passes a single negative query at the default policy** — false-positive rate is 1.0000
(6/6) for all 24 arms, semantic and lexical alike. The shipped default floor of 0.35 is simply too
low to reject anything; it is dominated and should not carry into P1.

Sweeping the absolute cosine floor on `qwen3-1024d-f32` (relative band disabled, k=10):

| floor | recall@10 | nDCG@10 | negative FP rate | note |
| --- | --- | --- | --- | --- |
| 0.35 *(shipped default)* | 0.6184 | 0.5454 | 1.0000 (6/6) | dominated |
| 0.40 | 0.6184 | 0.5454 | 1.0000 (6/6) | dominated |
| **0.45** | **0.6184** | **0.5454** | 0.6667 (4/6) | **strictly free** — identical quality, 33% fewer FPs |
| 0.50 | 0.6118 | 0.5441 | 0.5000 (3/6) | −1.1% recall, half the FPs |
| 0.55 | 0.5658 | 0.5243 | 0.3333 (2/6) | still well above BM25's 0.4474 |
| 0.60 | 0.5197 | 0.5022 | 0.1667 (1/6) | still above BM25 |
| 0.65 | 0.3947 | 0.3998 | **0.0000 (0/6)** | full abstention, but now below BM25 |

Two conclusions. Abstention **is** achievable — but only at 0.65, where recall falls below the
lexical baseline and the feature stops paying for itself. And **0.45 is a free win**: identical
recall and nDCG to the shipped default while rejecting a third of the negatives. P1 should start
threshold tuning in the **0.45–0.55** band, where the arm still beats BM25 by a comfortable margin
while rejecting half the negatives.

This does not change the model/dims/quantization pin — the sweep moves all lanes together — but it is
a P1 blocker for any "semantic can decline to answer" claim, and it is why the negatives column is
uniform across the headline table rather than discriminating between arms.

### Throughput and load (report-only)

| candidate | model load | corpus embed | units/sec | warm query embed |
| --- | --- | --- | --- | --- |
| Qwen3-0.6B f16 (1024d) | 13.2s cold / 1.0s warm | 659.5s for 34,481 units | 52.3 | 11.0 ms |
| bge-small-en-v1.5 f32 (384d) | 10.6s | 153.9s | 224.0 | 3.9 ms |
| snowflake-arctic-embed-s f16 (384d) | 0.5s | 136.5s | 252.7 | 3.5 ms |

Report-only, but two observations P1 should carry:

- **Warm query embed is comfortably inside budget.** 11.0 ms against the design's 10–150 ms target
  (§4.2), with ~4 ms of headroom to spare on the fallbacks.
- **Initial corpus build is the risk, not query latency.** Qwen3 runs at 52 units/sec, so these two
  workspaces take ~11 minutes. A 500k-unit workspace extrapolates to roughly **2.7 hours**, against
  the design's "initial build minutes, not hours" bar (§5.2). The fallbacks are ~4.5× faster and
  would land the same workspace near 35 minutes. This is a convergence/batching problem for P1
  (§5.3), not a reason to change the model pin — but the "minutes not hours" claim does not survive
  contact with a large workspace on Qwen3 at this batch shape, and should be re-measured with the
  real sidecar's batching before it is repeated.

Measured with `-b 8192 -ub 8192`, batch 16 (Qwen3) / 32 (fallbacks), Metal, single server slot. The
harness drives HTTP round-trips per batch, so these are floors — the resident sidecar of §4.2 should
beat them.

### Fallback truncation caveat

Both fallbacks have 512-token contexts against Qwen3's 32K, so **22.0% of corpus units (7,603 of
34,481) were truncated** to fit them, at an 819-char budget versus the ~1,200-char cards Qwen3 saw
whole. Their numbers are therefore measured under a real handicap. This is a genuine capability
difference rather than a harness artifact — a 512-token model cannot see a full card — but it means
the fallback gap would narrow somewhat under a card template tuned for short-context models. Recorded
in each candidate's `.perf.json`.

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

### Weight precision vs storage precision

These are different axes and the design's "quantization" means the second one:

- **Weight precision** is fixed per candidate at the highest each repo publishes. The official Qwen
  GGUF repo publishes only f16 and Q8_0 — **there is no f32 Qwen3 GGUF**. f16 is therefore the
  highest-precision weight lane that exists, and is what ran.
- **Storage precision** is the benchmark variable: each dims lane is ranked at both `f32` and `int8`
  vector storage, `int8` being symmetric per-vector quantize→dequantize.

Qwen3 is MRL, so **one** embedding pass serves all three dims lanes: slice to 256/512/1024 **then**
renormalize, in that order (design §4.1).

### Corpus

34,481 units built from both pinned workspaces' own Miller artifacts — symbol cards from
`symbols.db` on the design's §5.2 v1 card template, plus docs/config chunks from `content.db`
windowed to the same ~1,200-char budget.

`doc_id` is the repo-relative file path, matching the golden set's vocabulary. The dev set carries
zero `#Symbol` suffixes, so ranking is file-granular: a file's score is its best-scoring unit.

Card eligibility is **derived, not declared**: a language earns symbol cards only where a real
extract shows it emits at least one code kind. On these workspaces that excludes exactly `json`,
`markdown`, `yaml`, and `toml` — matching the design's predicted list, but as evidence rather than
assumption. The full per-language matrix is emitted into the corpus manifest.

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
```

Every artifact is re-verified against its pinned sha256 on each run; a mismatch aborts. Embeddings
are cached, so re-ranking a lane is seconds.

## What P1 inherits

Direct consumers of this document are `scripts/semantic-pins.json` and the sidecar's model manifest.

**Carry forward as decided:**

- Default: Qwen3-Embedding-0.6B, f16 GGUF, **1024 dims, int8 vector storage**, pooling `last`,
  `<|endoftext|>` append, instruction-prefixed queries, MRL slice-then-renormalize.
- Fallback: bge-small-en-v1.5, f32 GGUF, **384 dims, int8**, pooling `cls`. Evidence-backed.
- int8 vector storage everywhere — measured free.

**Amend in the design:**

- §2.4's favored "256d int8" lane should become **1024d int8**, with 512d int8 named as the
  storage-pressure compromise. 256d fails the language-parity bar it would be judged by.

**Open, with evidence attached:**

1. **Negative-query abstention.** No threshold both rejects negatives and beats BM25 today. Start
   tuning at floor 0.45–0.55; treat the shipped 0.35 default as dead. Any claim that semantic
   retrieval can decline to answer needs this closed first.
2. **Docs-like retrieval.** Qwen3 loses docs_like and markdown to lexical *and* to both fallbacks.
   Suspected cause is the docs chunk-window shape, not the encoder. Needs a larger docs slice than
   `n=4` before anyone concludes anything.
3. **Initial-build throughput.** 52 units/sec puts a large workspace at hours, not minutes. Re-measure
   against the real sidecar's batching before repeating the §5.2 "minutes not hours" claim.
4. **Fallback supply chain.** Neither fallback has an official first-party GGUF; both pinned
   artifacts are community conversions. P1 owns whether that is acceptable for a shipped escape
   hatch, or whether the sidecar should convert from first-party weights itself.

**Do not re-derive from the dev set.** This benchmark tunes against the dev set, which is what it is
for. The sealed acceptance set (`eval/retrieval-eval/sets/SEALED-SET-PROTOCOL.md`) is user-owned and
must not be touched during P1 tuning.
