# Task 7 report — Model benchmark harness + benchmark run → pin recommendation

**Status:** COMPLETE
**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/semantic-integration`, branch
`worktree-semantic-integration`

> This file previously held a stale report from an older plan (dashboard UX fixes); overwritten per
> the task brief, matching what tasks 1–6 did with their own report files.

## Pin recommendation

- **Default pin:** `Qwen3-Embedding-0.6B`, f16 GGUF weights, **1024 dims, int8 vector storage**
  (pooling `last`, `<|endoftext|>` append, instruction-prefixed queries, MRL slice-then-renormalize).
- **Fallback pin:** `bge-small-en-v1.5`, f32 GGUF weights, **384 dims, int8 vector storage**
  (pooling `cls`). **Evidence-backed** — both fallback lanes completed end to end, satisfying the
  decision rule's "fallback pin only from a completed fallback lane". No pin is recorded as OPEN.

Every lane completed: 6 Qwen3 lanes (256/512/1024 × f32/int8), 2 fallback candidates × f32/int8, and
2 BM25 baseline modes — each under both threshold policies, **24 scored arms**. **No incomplete
lanes, so no resume commands are required.**

## Implementation

Created `eval/model-bench/`:

| file | role |
| --- | --- |
| `bench-pins.json` | llama.cpp `b10068` + 3 candidate GGUFs: URLs, sha256, sizes, licenses (with provenance), pooling, dims, instruction prefixes. Records 4 rejected candidates and why. |
| `build_corpus.py` | Corpus from both pinned workspaces' `symbols.db` + `content.db`, design §5.2 v1 card template. Emits `golden_set_leak_check`. |
| `bench.py` | `sanity` (pooling gate), `embed` (llama-server HTTP), `rank` (MRL slice → quantize → cosine → threshold). |
| `bm25_baseline.py` | Lexical baseline arm from Miller's real `search --json` CLI. |
| `summarize.py` | Collects scorer reports into one comparison table. |
| `run-bench.sh` | Orchestrates download→verify→corpus→BM25→sanity→embed→rank→score→summarize. |
| `README.md` | Harness contract, corpus contract, trap documentation, threshold and truncation policy. |
| `.gitignore` | `.cache/` + `runs/` — placed **inside** `eval/model-bench/`; repo-root `.gitignore` untouched. |

Also created `docs/findings/2026-07-19-model-benchmark.md`; modified `eval/retrieval-eval/README.md`
(integration note only, per ownership).

Corpus: **34,481 units** (25,815 symbol cards + 8,666 docs/config chunks) across both pinned
workspaces. `doc_id` = repo-relative file path, matching the dev set — verified the golden set
carries zero `#Symbol` suffixes, so ranking is file-granular with best-unit-per-file collapse.

## Verification

| invariant | scope | command | result |
| --- | --- | --- | --- |
| Golden-set exclusion (hard gate) | corpus + every arm | manifest `golden_set_leak_check`; direct scan of corpus + all 24 results files | **PASS** — 0 corpus units and 0 ranked entries under `eval/`/`.razorback/`/`.claude/` across all arms |
| Pooling sanity per candidate (hard gate) | 3 candidates | `bench.py sanity` | **PASS** ×3 — Qwen3 0.4487, bge 0.1677, arctic 0.1973 (threshold 0.10); dims match declared |
| Pooling gate actually bites (negative control) | Qwen3 forced `cls` | `bench.py sanity`, wrong pooling | **PASS** — margin 0.0560 = FAIL, **exit code 3** |
| Scorer runs green (hard gate) | 24 arms | `retrieval-eval score` | **PASS** — 24/24 scored, 0 failures, 0 `missing_results` |
| Pin supported by completed lanes (hard gate) | all lanes | see metrics | **PASS** — 12 Qwen3 + 8 fallback + 4 BM25 arms all complete |
| sha256 integrity re-verify | 4 artifacts | warm-cache re-verification | **PASS** ×4 |
| sha256 mismatch aborts (negative control) | 1 artifact | appended bytes, re-hashed, restored | **PASS** — mismatch detected; artifact restored to pinned sha |
| Miller build unaffected | repo | `dotnet build Miller.slnx -c Release` | **PASS** — 0 warnings, 0 errors |
| Miller tests unaffected | repo | `scripts/test.sh` | **PASS** — 3617 passed, 0 failed, 1 skipped, 29s |
| Script lint | 5 scripts | `py_compile` ×4, `bash -n` | **PASS** |

Timestamp: 2026-07-19, macos-arm64 (Apple Silicon, Metal), llama.cpp `b10068`.

### Hard-gate metrics (`topk` policy — the BM25-comparable one)

| arm | recall@10 | nDCG@10 | worst-lang nDCG | prose recall | ident recall | ident nDCG | clusters |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `bm25-symbol` (baseline) | 0.4474 | 0.3820 | csharp 0.3183 | 0.2234 | 1.0000 | 1.0000 | 10/14 |
| **`qwen3-1024d-int8`** (pin) | **0.6184** | **0.5454** | csharp **0.5227** | **0.4787** | 1.0000 | 0.9980 | **14/14** |
| `qwen3-512d-int8` | 0.5987 | 0.5356 | csharp 0.4976 | 0.4255 | 1.0000 | 0.9759 | 14/14 |
| `qwen3-256d-int8` | 0.5592 | 0.5161 | markdown 0.4077 | 0.4149 | 1.0000 | 0.9519 | 14/14 |
| `bge-384d-int8` (fallback pin) | 0.5724 | 0.4879 | rust 0.4595 | 0.3617 | 1.0000 | 0.9977 | 13/14 |
| `arctic-384d-int8` | 0.5658 | 0.4482 | csharp 0.4186 | 0.4043 | 1.0000 | 0.9516 | 12/14 |

**Identifier non-inferiority:** every arm holds recall@10 = 1.0000, matching the baseline exactly.
The pin gives up 0.0020 nDCG (ordering only). Identifier nDCG degrades monotonically as dims shrink
(0.9980 → 0.9759 → 0.9519) — an independent argument against 256d. In production the bar is easier
still: lexical output stays byte-identical and semantic fuses on top, so hybrid cannot fall below
lexical here.

**int8 is free:** within 0.0002 nDCG of f32 at every dims lane; identical to 4dp at 1024d.

### Report-only metrics

| candidate | model load | corpus embed (34,481 units) | units/sec | warm query embed |
| --- | --- | --- | --- | --- |
| Qwen3-0.6B f16 | 13.2s cold / 1.0s warm | 659.5s | 52.3 | 11.0 ms |
| bge-small-en-v1.5 f32 | 10.6s | 153.9s | 224.0 | 3.9 ms |
| arctic-embed-s f16 | 0.5s | 136.5s | 252.7 | 3.5 ms |

Warm query embed (11.0 ms) sits well inside the design's 10–150 ms target. Initial build is the risk:
52 units/sec extrapolates a 500k-unit workspace to ~2.7 hours against §5.2's "minutes not hours".

## Files changed

Created: `eval/model-bench/{bench-pins.json,build_corpus.py,bench.py,bm25_baseline.py,summarize.py,run-bench.sh,README.md,.gitignore}`,
`docs/findings/2026-07-19-model-benchmark.md`.
Modified: `eval/retrieval-eval/README.md` (integration note only), `.razorback/sdd/task-7-report.md`.

Nothing from `.cache/` is staged — `git check-ignore -v` confirms `eval/model-bench/.gitignore:2`
ignores `.cache/`. The 6 other modified `.razorback/sdd/task-*-report.md` files were already dirty at
session start, are not mine, and are excluded from the commit.

## Miller calls used

`search --mode symbol|auto --json` against both registered workspaces (82 queries × 2 modes × 2
policies) as the BM25 baseline arm — this doubles as the CLI API-shape evidence. `sqlite3` read-only
against `.miller/symbols.db` and `.miller/content.db` for the corpus and the card-eligibility matrix.

## API-shape evidence

- **`miller search --json`** returns a bare JSON array of `{name, kind, file, line, signature, score,
  symbol_id}`. `file` is the repo-relative path, so the mapping into the golden set's `doc_id` space
  is identity after exclusion filtering. Verified by direct invocation.
- **`symbols.db`** — verified schema for `symbols` (incl. `parent_symbol_id`, `doc_comment`,
  `is_test`) and `files`. **`content.db`** — `content_chunks` with `content_kind` values
  `workspace_docs|workspace_config|workspace_source|external_file`.
- **llama.cpp `b10068`** (`https://github.com/ggml-org/llama.cpp/releases/download/b10068/llama-b10068-bin-macos-arm64.tar.gz`,
  sha256 `13aa2d40…`): archive listed — contains `llama-server`, **not** `llama-embedding`.
- **`Qwen/Qwen3-Embedding-0.6B-GGUF`** (Apache-2.0, official Qwen org): only `f16` and `Q8_0`
  published — **no f32 GGUF exists**. f16 sha256 `421a27e5…`.
- **`CompendiumLabs/bge-small-en-v1.5-gguf`** (MIT declared, MIT base): f32 sha256 `bf40c42a…`.
- **`ChristianAzinn/snowflake-arctic-embed-s-gguf`** (Apache-2.0 declared, Apache-2.0 base): f16
  sha256 `6e3d14df…`. Snowflake publishes **no official GGUF for the -s size** — the org ships
  safetensors + ONNX, and its only gguf-tagged repo is arctic-embed-**m-v1.5**, a different model.
- **`llama-server` flags/endpoints** verified from `tools/server/README.md`:
  `--pooling {none,mean,cls,last,rank}`, `--embedding`, `--embd-normalize`, and three endpoints
  (`/v1/embeddings`, `/embeddings`, `/embedding`).

## Judgment calls

1. **`llama-server` HTTP instead of `llama-embedding`.** The prebuilt archive has no
   `llama-embedding` — it lives under `examples/` and upstream `release.yml` sets
   `-DLLAMA_BUILD_EXAMPLES=OFF`. Building from source would have weakened the pin. Approved by lead.
2. **Two threshold policies per lane.** A thresholded semantic arm emits ~1.5 docs/query, capping
   recall@10 by construction, while the BM25 baseline is unthresholded top-10 (what Miller's CLI
   actually shows a user). Scoring those against each other would understate every model and risk a
   wrong pin. `topk` carries recall/nDCG; `thr` carries negatives/precision. Approved by lead; every
   table in the findings doc labels its policy.
3. **Card eligibility derived, not hardcoded.** Design §5.2 requires kind/data-driven eligibility. A
   language earns cards only where a real extract shows ≥1 code kind; the full matrix is published in
   the corpus manifest. Outcome excludes exactly json/markdown/yaml/toml — the design's predicted
   list, reached as evidence rather than assumption. Cut the corpus 75,799 → 34,481 units.
4. **Per-model input truncation.** Both fallbacks are 512-token and `llama-server` rejects
   over-length input with HTTP 400 rather than truncating (hit as a real failure mid-run). Capped at
   `context_length × 1.6` chars; 22.0% of units (7,603) truncated for the fallbacks, 0% for Qwen3.
   Recorded in `.perf.json` and reported as a genuine capability handicap on the fallback numbers.
5. **Recommending 1024d against the design's favored 256d.** §2.4 named "256d int8" as favored. 256d
   measured −9.6% relative recall and −22% relative worst-language nDCG, which the language-parity
   rule treats as a regression. Recommending 1024d int8, with 512d int8 as the storage-pressure
   compromise, and flagging the design amendment explicitly.
6. **Ran a threshold sweep beyond the brief.** Negatives failed 6/6 on every arm at the default, so a
   bare "negatives fail" would have been useless to P1. The sweep localizes the tradeoff and shows
   floor 0.45 is strictly free.

## Concerns

1. **No arm rejects a single negative query** at the default policy (FP 1.0000, 6/6, all 24 arms).
   The shipped 0.35 floor is dominated. Sweep on the pin lane: 0.45 is free (FP → 0.667 at identical
   recall/nDCG); 0.55 gives FP 0.333 at recall 0.5658, still well above BM25's 0.4474; full
   abstention needs 0.65, where recall (0.3947) falls **below** the lexical baseline. **P1 blocker
   for any "semantic can decline to answer" claim.** Does not affect the model/dims/quant pin — the
   sweep moves all lanes together.
2. **Qwen3 loses docs-like and markdown** to BM25 *and* to both fallbacks (docs_like 0.750/0.533 vs
   BM25 1.000/0.687; markdown 0.750/0.533 vs bge 1.000/0.741). Suspected corpus-shape cause — docs
   enter as ~1,200-char windows of ~7KB files while BM25 scores whole files — not the encoder. But
   `n=4` docs_like and `n=4` markdown is far too small to conclude; needs a larger docs slice in P1.
3. **Initial-build throughput.** 52 units/sec puts a 500k-unit workspace near 2.7 hours against
   §5.2's "minutes not hours". Re-measure against the real sidecar's batching before repeating that
   claim; the harness's per-batch HTTP round-trips make these numbers a floor.
4. **Neither fallback has a first-party GGUF.** Both pinned artifacts are community conversions whose
   declared licenses match their base models. P1 owns whether that is acceptable for a shipped escape
   hatch, or whether the sidecar should convert from first-party weights itself.
5. **Dev-set tuning only.** These numbers come from the visible dev set, which is what it is for. The
   sealed acceptance set was not touched and must not be during P1 tuning.
