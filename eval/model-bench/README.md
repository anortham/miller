# model-bench

The P0 model gate for Miller's semantic integration
([design §2.4, §4.1, §8](../../docs/plans/2026-07-19-miller-semantic-integration-design.md)): a
reproducible benchmark that embeds a real symbol/docs corpus with each candidate model, ranks the dev
golden set, scores every arm through the [retrieval-eval](../retrieval-eval/README.md) harness, and
produces the model/dims/quantization pin recommendation.

Result: [`docs/findings/2026-07-19-model-benchmark.md`](../../docs/findings/2026-07-19-model-benchmark.md).

**The model-bench scripts are throwaway tooling, not product code.** They remain outside `Miller.slnx`, product
builds never see them, and their llama.cpp pin does **not** define the `julie-semantic-sidecar` runtime. The
separate CodeRank evaluator host described below is in the solution so its bounded adapter and lifecycle seams
stay build- and test-covered.

## CodeRank final-behavior evaluator

`eval/semantic-model-eval` is a separate evaluator host for comparing an injected protocol-v1 encoder against
Miller's production BGE arm. It exposes the same nine MCP tools and embedded instructions as Miller, while
disabling the indexer and blocking tool calls that can mutate or refresh an index. The host takes Miller's
exclusive workspace writer lease before vector convergence, so it refuses to overlap a live Miller writer or a
second evaluator. Run it only against a copied frozen snapshot: vector generations are intentionally built in
that snapshot's `.miller/vectors.db`, never in a live development checkout.

The checked-in example config freezes current Julie's CodeRank identity:

- `nomic-ai/CodeRankEmbed` at revision `3c4b60807d71f79b43f3c4363786d9493691f8b1`;
- weights SHA-256 `827529bcd58aef0d9082e66eeff7e7d53a02f62bd005f841a26b3d3e2fb17ebe`;
- 768 dimensions, CLS pooling, L2 normalization, and empty query/document instructions;
- `vec0-int8-768-cosine-v1` storage.

Run BGE with `miller-semantic-model-eval --production`. Run CodeRank with
`miller-semantic-model-eval --config <runtime.json>`. The config names a protocol-v1 launcher plus its
environment overrides; evidence records configured variable names, never their values. The launcher inherits the
host environment and applies these overrides, so the benchmark runner must supply its normal isolated process
environment too. Unknown fields, duplicate fields,
or any pin mismatch fail before serving. `MILLER_SEMANTIC=off` returns before reading the config or touching a
vector path.

This host is evaluation-only. CodeRank is not added to `MillerSemanticContract.KnownEncoders`, ordinary
`MILLER_SEMANTIC_MODEL` selection, the production `miller` host, or the MCP tool surface.

## Run

```bash
eval/model-bench/run-bench.sh
```

Everything is cached under `.cache/` (gitignored). A re-run from a clean cache re-downloads and
re-verifies every sha256 in `bench-pins.json`; a mismatch aborts. Re-runs with a warm cache reuse
embeddings, so re-ranking a lane is seconds.

Useful overrides: `CANDIDATES="qwen3-0.6b-f16"`, `BATCH=16`, `K=10`, `FLOOR`, `RATIO`,
`MILLER_REPO`, `JULIE_REPO`, `MILLER_BIN`.

Stages: download+verify → build corpus → BM25 baseline → **pooling sanity gate** → embed → rank each
lane → score → `summarize.py` comparison table.

**`RANK_ONLY=1 eval/model-bench/run-bench.sh`** re-derives every arm from the cached vectors: no
download, no corpus rebuild, no BM25 re-run, no `llama-server`. Use it when the ranking population,
threshold policy, or scorer changes but the embeddings did not — those stages are deterministic given
the cached `.npy` files, so re-embedding would spend an hour reproducing identical vectors.

## Pieces

| file | role |
| --- | --- |
| `bench-pins.json` | llama.cpp release + every candidate GGUF: URL, sha256, size, license, pooling, dims, instruction prefixes. Also records rejected candidates and why. |
| `build_corpus.py` | Builds the retrieval corpus from both workspaces' `.miller/symbols.db` + `.miller/content.db`. |
| `bench.py` | `sanity` (pooling gate), `embed` (llama-server HTTP), `rank` (MRL slice → quantize → cosine → threshold). |
| `bm25_baseline.py` | The lexical baseline arm, from Miller's real `search --json` CLI. |
| `summarize.py` | Collects all scorer reports into one comparison table. |
| `run-bench.sh` | Orchestrates the above end to end. |

## Runtime: why `llama-server`, not `llama-embedding`

The upstream macos-arm64 prebuilt archive **does not contain `llama-embedding`**. That target lives
under `examples/`, and upstream `release.yml` builds with `-DLLAMA_BUILD_EXAMPLES=OFF`. Verified by
listing the extracted `b10068` archive. The harness therefore drives `llama-server`'s
OpenAI-compatible `POST /v1/embeddings` endpoint, which is the only prebuilt path. Getting the CLI
tool would require building llama.cpp from source — unnecessary, and it would weaken the pin.

## Corpus contract

Built from Miller's own artifacts, following the design's §5.2 **card text v1** template:

```
{kind} {qualified name} {signature first line} {doc excerpt ≤300} in: {container} {path}
```

~1,200-char budget, word-boundary truncation, comment-marker and XML-doc-tag stripping. Docs and
config chunks come from `content.db` (`workspace_docs` / `workspace_config`), windowed to the same
budget with 200-char overlap — whole-file chunks average ~7KB here and would blow every candidate's
context.

`doc_id` is the repo-relative file path, matching the golden set's vocabulary. The dev set carries
**zero** `#Symbol` suffixes, so ranking is file-granular: many units map onto one `doc_id` and a
file's score is its best-scoring unit.

### Ranking population: test units are excluded (production parity)

`rank` scores a query against its own repo's corpus units **excluding every `is_test` unit** (6,104 of
34,481 here, leaving 28,377). This is not a quality filter — it is what the shipped surface does.

Design §5.2 gives test symbols cards but excludes them "from default search recall via the metadata
filter", and Miller's lexical baseline already behaves that way: `SearchTool.ResolveExcludeTests`
auto-hides test code for natural-language queries. Before this filter the semantic arms ranked 178
test-path entries against BM25's 58, so the two arms were competing over **different doc populations** —
the same class of error as indexing the golden set, just quieter.

Two facts worth stating rather than assuming:

- **It cannot manufacture a win.** No graded doc in the dev set resolves to a test-only path (verified
  against the corpus), so no relevant document is removed from any query's candidate pool. The one golden
  doc carrying any test units, `crates/julie-core/src/string_similarity.rs`, keeps its non-test units.
- **A residual asymmetry remains, and it is production-faithful.** BM25 still returns 11 test-path
  entries, all on `identifier`/`path` queries, because Miller's auto-hide applies to natural-language
  phrasings only. Since no golden doc is test-only, this changes neither arm's recall.

`--include-tests` restores the unfiltered population for ablation. Default arms never use it.

### Card eligibility is data-driven, not a language blocklist

Per design §5.2, a language earns symbol cards only if a **real extract** shows it emits at least one
code kind (function/method/class/struct/interface/enum/…). Languages emitting only data-structure
kinds produce cards that merely restate a path, and their text is already covered by the doc/config
chunk corpus. The full per-language matrix is published in `.cache/corpus/corpus-manifest.json` as
the evidence for this cut. On these two workspaces it excludes exactly `json`, `markdown`, `yaml`,
and `toml` — matching the design's predicted outcome, derived rather than assumed.

### The golden-set self-retrieval trap

`eval/retrieval-eval/sets/**` lives inside the miller workspace and contains both the query text and
the answer paths. Any corpus containing it retrieves its own answer key.

`eval/`, `.razorback/`, and `.claude/` are excluded from **every** arm, and `build_corpus.py` emits a
`golden_set_leak_check` block in the corpus manifest that fails the build if any leak.

This bites the lexical arm too, and harder: the live miller index covers `.claude/worktrees/**`, so
an unfiltered BM25 run returns this harness's own source. Observed during bring-up — the top hit for
a promotion query was `bench.py`'s own `SANITY_PAIRS` literal. `bm25_baseline.py` applies the
identical exclusions; on the recorded run that filtered **1,516 of 4,842** raw hits. Both arms must
compete over the same doc space or the comparison means nothing.

## Pooling sanity gate

Wrong pooling degrades silently: vectors still look plausible and cosines stay high, but
discrimination is gone. So no candidate is scored until it proves it can separate a known-similar
pair from a known-dissimilar one by a **≥0.10 cosine margin**, with its output dimensionality
matching what `bench-pins.json` declares. Failure drops the candidate from the run (exit 3); it is
recorded as a result, never silently skipped.

The gate is verified against a deliberate negative control — Qwen3 forced to `--pooling cls` instead
of its required `last`:

| run | similar | dissimilar | margin | verdict |
| --- | --- | --- | --- | --- |
| Qwen3 correct (`last`) | 0.6005 | 0.1518 | **0.4487** | PASS |
| Qwen3 wrong (`cls`) | 0.8979 | 0.8419 | **0.0560** | FAIL |

Note the failure mode: the wrong-pooling cosines are *higher*, not lower. Only the margin exposes it.
Pooling per model is not optional — Qwen3 needs `last`, BGE and Arctic need `cls`.

## Lanes and the threshold policy

Qwen3 is MRL, so one embedding pass serves every dims lane: slice to 256/512/1024 **then**
renormalize (design §4.1 order). Each dims lane is ranked at both `f32` and `int8` vector-storage
precision — that is what "quantization" means in design §2.4. Model *weight* quantization is fixed at
the highest precision each repo publishes (Qwen ships only f16 and Q8_0; there is no f32 GGUF).

### Per-model input truncation

Both fallbacks have 512-token contexts, and `llama-server` **rejects** an over-length embedding
request with HTTP 400 rather than truncating it — so the caller must fit the text. `text_budget()`
caps input at `context_length × 1.6` chars (code is far more token-dense than prose; the factor
leaves headroom for the instruction prefix). Qwen3's 32K context needs no cap.

This is a genuine capability difference, not harness bookkeeping: a 512-token model sees roughly
820 chars of each ~1,200-char card, where Qwen3 sees all of it. `units_truncated` and
`text_budget_chars` are recorded in each candidate's `.perf.json`, and the findings doc reports the
fallback numbers as measured under that constraint.

### Threshold policy

Results are emitted **post-threshold**, as the scorer's negative-query rule requires. A doc is shown
only if it clears an absolute cosine floor (`--floor`, default 0.35) **and** sits within a relative
band of the query's best hit (`--ratio`, default 0.85), capped at `k`.

The BM25 baseline runs unthresholded by default (`--ratio 0`): Miller's CLI already returns exactly
what it would show a user. This makes the baseline's negative-query false-positive rate 1.00 by
construction — an honest property of ranked lexical search, not a harness artifact, and the reason
the findings doc compares negatives at matched threshold policy rather than head-to-head.
