# Parsing, search & storage findings (2026-05-28)

> Historical origin research. This predates the `julie-extract` migration, symbol/content sidecars, and current
> release packaging. Use [`../README.md`](../README.md) and [`../../README.md`](../../README.md) for current
> behavior.

Machine: Apple M2 Ultra. Runtime: .NET 10 (SDK 10.0.300). Microsoft.Data.Sqlite 10.0.8,
System.Numerics.Tensors 10.0.8, LLamaSharp 0.25.0.

## 1. Extract → SQLite contract (parsing without FFI)

**Question:** can we get julie's tree-sitter extraction without re-importing Rust build pain (the old design
statically linked a 107 MB UniFFI cdylib)?

**Method:** shell out to julie's prebuilt `julie-server extract scan --root R --db D --standalone --json`,
which writes a caller-owned canonical SQLite DB, then read it from C# with `Microsoft.Data.Sqlite` (ReadOnly).

**Result: works, zero in-process Rust.** Throughput ~15k symbols/sec, ~3.2 KB/symbol on disk, consistent across scales:

| repo | files | symbols | extract time | db size |
|---|---|---|---|---|
| codesearch | — | 5,284 | 0.43 s | 21 MB |
| MyraNext | 641 | 29,663 | 1.1 s | 56 MB |
| LabHandbookV2 | 436 | 8,357 | <1 s | 53 MB |
| Tycho | 591 | 10,455 | 1.1 s | 67 MB |
| hermes-agent | — | 237,298 | 10.6 s | 668 MB |
| openclaw | 13,493 | 565,025 | 37.5 s | 1.8 GB |

Canonical schema (relevant tables): `symbols` (id, name, kind, language, file_path, signature, doc_comment,
visibility, code_context, parent_id, body spans, `semantic_group`), `identifiers` (name, kind ∈
{type_usage, member_access, call}, language, file_path, containing_symbol_id, **target_symbol_id**, code_context),
`relationships` (sparse), `symbol_annotations`.

**Gotchas to remember:**
- `extract scan` does **no identifier resolution** — `target_symbol_id` is **always NULL**. Identifiers are raw
  name occurrences (+ `code_context`). Any resolution/linking is the host's job.
- `relationships` is sparse and within-language (MyraNext: 499 rows). Don't rely on it for cross-language links;
  mine `identifiers` + signatures instead.
- `semantic_group` is **never populated** by extract — the cross-language grouping column is greenfield.

Extraction is batch/index-time, so subprocess spawn cost is irrelevant. This replaces the cdylib entirely.

## 2. Search-index benchmark — confronting the "huge index / poor perf" abandonment reason

**Question:** an earlier julie attempt used "sqlite + sqlite-vec + FTS with pretokenization," abandoned because
it made a *huge* index with *poor* query latency vs tantivy/lancedb. Is pure-.NET lexical search actually viable?

**Method:** `spike/Codesearch.Spike/SearchBench.cs`, three indexers over the same corpus, measuring **build time,
index size, ranked top-50 query latency**:
1. **FTS5 + pretokenized** — the span tokenizer (`CodeTokenizer.cs`, a port of julie's camelCase/acronym/snake
   splitter) feeds component tokens, so `http` matches `getHTTPResponseCode`. FTS5 `ORDER BY rank` (BM25).
2. **FTS5 + trigram** — substring matching over raw names.
3. **In-memory inverted index** — `FrozenDictionary<string,int[]>` postings + **full BM25 top-50** (honest: not
   a fake `min(50,len)`; iterates all postings, length-normalized scoring, bounded top-50 selection).

**Result (openclaw, 565,025 symbols):**

| approach | build | index size | per-symbol | ranked top-50 query |
|---|---|---|---|---|
| FTS5 pretokenized | 1.79 s | **78.0 MB** | 145 B | 3.53 ms |
| FTS5 trigram | 1.66 s | 47.7 MB | 88 B | 5.56 ms |
| in-memory (FrozenDict + BM25) | 0.91 s | **~35 MB RAM** | — | **25.2 µs** |

In-memory holds: 115,748 terms / 3,628,553 postings. Second corpus (hermes, 237k): FTS5 pretok 27.3 MB / 1.45 ms;
trigram 20.2 MB / 1.25 ms; in-memory 30.7 MB / 15.3 µs. Scales linearly.

**Conclusions:**
- The lexical index is **tiny** (78 MB = 4% of the 1.8 GB extract DB) and **fast** (3.5 ms FTS5, 25 µs in-memory).
  Neither "huge" nor "poor." No tantivy needed at single/multi-repo scale.
- **The abandonment reason was misattributed.** What was actually huge: the **vectors**. 565k × 768 dims × 4 B =
  **1.62 GB of f32 — 21× the entire lexical index.** The "poor perf" was vector KNN, not lexical FTS. The pain
  lived in the embedding layer, not search. (This is what re-opened the embeddings question → [embeddings.md](embeddings.md).)
- Recommended host design: in-memory inverted index for hot queries (rebuilt from SQLite at startup in <1 s),
  with SQLite/FTS5 as the durable store. Either is more than adequate.

## 3. .NET 10 raw performance (supporting measurements)

From `spike/Codesearch.Spike/Bench.cs`:
- **SIMD cosine** (`TensorPrimitives.CosineSimilarity`, System.Numerics.Tensors): 100,000 × 768-dim vectors
  scored in **19 ms** = 3.4× a scalar loop. Brute-force cosine over a single repo's vectors is trivial → makes
  a dedicated vector DB (LanceDB) deletable at this scale.
- **Span tokenizer** vs Regex/Split baseline on real identifiers: **3.5× faster, 4.7× less allocation**. The
  zero-alloc `ReadOnlySpan<char>` scan is the ~300 LOC the host owns if it splits in C#.

Takeaway: .NET 10 (TensorPrimitives, Span/SearchValues, FrozenDictionary, AVX10.2/ARM NEON) is more than enough
for the search/ranking/vector-math the tool needs — CPU SIMD, no GPU.
