# Phase 9 BGE-small versus CodeRankEmbed model-decision report

**Date:** 2026-07-24 CDT  
**Miller worktree:** `/Users/murphy/source/miller/.worktrees/miller-julie-takeover`  
**Miller branch / evaluated base:** `codex/miller-julie-takeover` / `5ffa4782bf670a1552bccbe4a4e6af4ff2f9cf25`; the reviewed evaluator adapter and this decision were committed as `6f2234d8da47f6840b8b76a2af948aee053282d7`  
**RC3 source commit:** `24ce6257bee7f41865b10daf1457ed9b4fd71a8a`  
**Released artifact:** `julie-semantic-sidecar-0.1.0-rc.3-aarch64-apple-darwin-metal-portable.tar.gz`  
**Released artifact SHA-256:** `92a873438635f843d46e166c105bb122a30e86199ba1428cef13bb00f9ebf6e0`

## Decision

Keep **BGE-small as Miller's default now**.

- The evaluator-only adapter now drives the exact frozen CodeRank pin through Miller's real vector convergence,
  semantic search, fusion, `search`, and `context` paths. It does not add CodeRank to production selection.
- Both arms used the same evaluator host, nine MCP tools, instructions, fusion profile, five frozen source
  snapshots, and frozen index artifacts. Every paired readiness check had identical workspace and index identity;
  only model and vector identity differed.
- The visible five-task discovery/context-orientation subset was non-decisional by its frozen contract. CodeRank
  did not improve correctness or relevance: both arms were correct on 1 / 5 tasks, wrong-action rate was 0.2,
  and scored relevance was 0 for both.
- CodeRank was more efficient on the one jointly correct task: median calls 2 versus 3, median tool-output tokens
  1,655 versus 1,728, and p75 wall time 20,461 ms versus 31,627 ms. That isolated efficiency signal cannot promote
  a model when four tasks failed in both arms and the subset explicitly reports `decision_verdict=not_decisional`.
- The earlier protocol probe still establishes CodeRank's resource cost: about 6x BGE memory and 10x warm query
  latency, with no relevance win.

The sealed gate was not opened because visible evidence did not show a correctness or relevance advantage.
Do not add a production Rust/ONNX CodeRank engine on this evidence; BGE remains the supported default.

## Safety and isolation

- No sealed prompt, label, task row, mapping, answer, trajectory, or scorer row was inspected.
- Nothing under the active `/Users/murphy/source/julie` checkout was modified or launched.
- The existing `julie-embedding-host` process, PID 55579, was observed read-only and left running.
- The frozen Julie Python producer was copied from the existing visible snapshot at
  `/private/tmp/miller-agent-efficiency-visible.DpjG5t/julie-source/python/embeddings_sidecar`.
- The CodeRank Hugging Face cache was APFS-cloned into the isolated benchmark root and used with
  `HF_HUB_OFFLINE=1` and `TRANSFORMERS_OFFLINE=1`.
- Exact RC3 and all benchmark output lived under
  `/private/tmp/miller-phase9-model-decision.6wQU4Z`.
- No source checkout received model files, generated vectors, Python bytecode, or benchmark output.
- Frozen source snapshots received only their Miller sidecar artifacts under `.miller`; the evaluator disabled
  the indexer and rejected index-mutating tool calls so the compared extract revision could not drift during
  either arm.
- The evaluator held Miller's exclusive workspace writer lease for its lifetime. It refuses to overlap a live
  Miller writer or another evaluator, preventing concurrent vector promotion/GC.
- Evaluator runs are restricted to copied frozen snapshots. Their vector generations intentionally share the
  snapshot-local `.miller/vectors.db`; no live development checkout is an allowed target.

## Product boundary

CodeRank still has no production route through the current product, and the evaluator does not create one.

1. RC3's manifest contains only `qwen3-0.6b-f16` and `bge-small-en-v1.5-f32`
   (`julie-semantic-sidecar/src/manifest.rs:84-121`).
2. The exact released RC3 binary rejects CodeRank before serving:

   ```text
   exit=1
   julie-semantic-sidecar: serve failed: unknown model id 'nomic-ai/CodeRankEmbed'; run `prepare --model`
   ```

3. Miller's `MillerSemanticContract.KnownEncoders` contains only BGE and Qwen
   (`src/Miller.Indexing/Semantic/MillerSemanticContract.cs:99-123`).
4. `MILLER_SEMANTIC_MODEL=nomic-ai/CodeRankEmbed` does not select CodeRank. Unknown model ids fall back to BGE
   with a warning (`MillerSemanticContract.cs:335-355`).
5. Production `SemanticEmbeddingSession.MatchEncoder` still accepts only `KnownEncoders`. The evaluator uses an
   explicit expected-pin overload and never mutates that list.
6. The evaluator truthfully stamps the CodeRank fingerprint and 768-d storage schema only when its strict
   evaluation config matches every frozen pin field.
7. The pinned llama.cpp conversion route remains blocked: the current NomicBert converter crashes before a
   valid GGUF can be produced. This is a serving blocker, not negative quality evidence.
8. The separate `miller-semantic-model-eval` host supplies the missing comparison arm without changing the
   production `miller` host or adding an MCP tool.

Consequently, the final-behavior comparison is now technically available, while production CodeRank remains
deliberately unavailable.

## Producer identity and an unresolved prompt-policy choice

The frozen visible Julie producer loaded:

- model id: `nomic-ai/CodeRankEmbed`
- revision: `3c4b60807d71f79b43f3c4363786d9493691f8b1`
- weights SHA-256: `827529bcd58aef0d9082e66eeff7e7d53a02f62bd005f841a26b3d3e2fb17ebe`
- weights size: 546,938,168 bytes
- dimensions: 768
- pooling: CLS
- normalization: L2

The model configuration contains the query prompt
`Represent this query for searching relevant code: `, but `default_prompt_name` is null. Julie's
`embed_query` delegates directly to `embed_batch`, and `SentenceTransformer.encode` is called without
`prompt` or `prompt_name` (`runtime.py:355-363`, `runtime.py:381-397`). The current Julie producer therefore
uses no distinct query instruction.

The controlled comparison must freeze this choice explicitly:

- To compare against Julie's actual current producer, pin an empty CodeRank query instruction.
- To evaluate the model author's intended query prompt, create a separately named experimental pin and compare
  it as a different encoder identity.

Mixing these behaviors under one fingerprint would invalidate stored vectors and the comparison.

## Existing visible evidence

The committed diagnostic at
`docs/findings/agent-efficiency/2026-07-22-visible/semantic-model-diagnostic.json` used 22 equal-boundary,
equal-candidate excerpts on two visible concept tasks:

| Metric | BGE-small | CodeRankEmbed |
|---|---:|---:|
| Target rank, `dev-003` | 1 | 1 |
| Target rank, `dev-004` | 1 | 1 |
| Startup | 498 ms | 10,335 ms |
| Cold batch | 174 ms | 711 ms |
| Warm batch | 109 ms | 635 ms |
| Query latency | 8 / 7 ms | 106 / 100 ms |

Its disposition was correct: retain BGE; the visible concept losses were not embedding-rank failures.

## Independent common-probe method

Both producers received exactly the same protocol-v1 requests:

1. `health`
2. one `embed_batch` containing all 27 documents
3. ten `embed_query` requests
4. the same ten queries again for determinism
5. `shutdown`

Cosine similarity ranked each query against all 27 document vectors. Ties were stable by document index.
Startup was process launch through completed `health`. RSS came from `ps -o rss= -p <pid>`.

### Document identities

| # | Document identity |
|---:|---|
| 0 | `AuthController.cs` — bearer-token validation |
| 1 | `CacheService.cs` — cache population and expiration |
| 2 | `UserRepository.cs` — relational user lookup by email |
| 3 | `PaymentGateway.cs` — payment capture/provider failures |
| 4 | `SearchIndex.cs` — full-text rebuild after source changes |
| 5 | `HealthEndpoint.cs` — readiness/dependency health |
| 6 | `RetryPolicy.cs` — exponential backoff |
| 7 | `RouteTable.cs` — HTTP route registration |
| 8 | `JsonConfig.cs` — JSON application settings |
| 9 | `TelemetrySink.cs` — latency/token/success recording |
| 10 | `FileWatcher.cs` — filesystem changes/incremental refresh |
| 11 | `SqlMigration.cs` — database tables/indexes |
| 12 | `MarkdownRenderer.cs` — sanitized HTML rendering |
| 13 | `WebSocketHub.cs` — realtime browser broadcast |
| 14 | `FeatureFlags.cs` — tenant/user switches |
| 15 | `SecretStore.cs` — operating-system keychain |
| 16 | `BackgroundQueue.cs` — bounded cancellable jobs |
| 17 | `RateLimiter.cs` — per-client request budget |
| 18 | `CsvExporter.cs` — comma-separated report output |
| 19 | `ImageResizer.cs` — aspect-ratio-preserving resize |
| 20 | `GraphTraversal.cs` — shortest dependency path |
| 21 | `SymbolReferences.cs` — parser reference-edge resolution |
| 22 | `HybridRanker.cs` — lexical/semantic reciprocal-rank fusion |
| 23 | `ContextAssembler.cs` — source snippets under token budget |
| 24 | `WorkspaceRegistry.cs` — selector/canonical-root resolution |
| 25 | `VectorStore.cs` — normalized embeddings/cosine KNN |
| 26 | `FallbackPolicy.cs` — unavailable GPU to CPU degradation |

### Query identities and intended targets

| Query | Target |
|---|---:|
| `where are authentication tokens validated` | 0 |
| `how is cache expiration implemented` | 1 |
| `find user lookup by email` | 2 |
| `what handles transient network retries` | 6 |
| `where are http endpoints registered` | 7 |
| `measure request latency and token use` | 9 |
| `trace shortest symbol dependency path` | 20 |
| `resolve tree sitter reference edges` | 21 |
| `combine lexical and vector search ranking` | 22 |
| `assemble relevant source under token limit` | 23 |

This probe is intentionally transparent and diagnostic, not a substitute for the frozen final search/context
evaluator. Its easy 10 / 10 tie says only that CodeRank did not demonstrate a quality advantage on these
concepts.

## Independent common-probe results

### Single-process

| Metric | RC3 BGE Metal, warm selection | CodeRank MPS, first process | CodeRank MPS, second process |
|---|---:|---:|---:|
| Startup through health | 488.745 ms | 5,338.225 ms | 4,512.744 ms |
| RSS at health | 181,440 KiB | 1,096,304 KiB | 1,095,088 KiB |
| 27-document batch | 29.494 ms | 321.892 ms | 116.830 ms |
| Batch throughput | 915.440 texts/s | 83.879 texts/s | 231.105 texts/s |
| RSS after batch | 193,280 KiB | 1,105,856 KiB | 1,105,840 KiB |
| Median query | 6.004 ms | 62.804 ms | 60.060 ms |
| Rank 1 | 10 / 10 | 10 / 10 | 10 / 10 |
| Mean reciprocal rank | 1.0 | 1.0 | 1.0 |
| Repeat max absolute difference | 0.0 | 0.0 | 0.0 |
| Repeat minimum cosine | ~1.0 | ~1.0 | ~1.0 |

The BGE common-probe row used the already-warm RC3 backend-selection cache. A separate exact-RC3 clean-selection
run measured 9,318.575 ms startup, 203,152 KiB ready RSS, 49.776 ms for 27 documents, and 6.238 ms median query.
Its second process measured 452.150 ms startup, 181,296 KiB ready RSS, 60.466 ms for 27 documents, and
5.495 ms median query. Both were bit-deterministic.

### Two concurrent producer processes

| Metric | RC3 BGE process A / B | CodeRank process A / B |
|---|---:|---:|
| Total wall time | 9,435 ms | 7,986 ms |
| Startup | 9,055.619 / 480.027 ms | 4,969.106 / 5,241.839 ms |
| RSS at health | 192,768 / 182,304 KiB | 1,095,264 / 1,097,344 KiB |
| Median query | 6.559 / 6.506 ms | 81.779 / 80.075 ms |
| Rank 1 | 10 / 10 each | 10 / 10 each |
| Repeat max absolute difference | 0.0 each | 0.0 each |
| Exit | 0 / 0 | 0 / 0 |

One BGE process performed the selection benchmark under the shared selection-cache lock while the other reused
the resolved selection. Both served correctly and remained isolated. CodeRank loaded one approximately
1.1-GiB runtime per process; simultaneous query latency rose to about 80 ms.

### Fallback

Exact RC3 was launched on Apple with `JULIE_SIDECAR_FORCE_BACKEND=cuda`, using a fresh isolated cache:

| Field | Result |
|---|---|
| requested backend | `cuda` |
| resolved backend | `cpu` |
| accelerated | `false` |
| degraded reason | `requested backend is unavailable` |
| startup | 477.232 ms |
| RSS at health | 181,376 KiB |
| median query | 5.825 ms |
| relevance/determinism | 10 / 10 rank 1; max absolute repeat difference 0.0 |

The fallback is truthful and usable. It does not silently claim Metal or CUDA.

### Zero-work

No sidecar process is constructed from Miller's semantic-off path. Current dedicated contract tests prove:

- unset, `off`, and `0` never ask the vector filesystem any question;
- the workspace directory remains byte-for-byte unchanged;
- search semantic mode off never consults an injected semantic arm;
- context semantic mode off performs zero semantic work.

Those tests were inspected but not rerun in this lane because the shared worktree contained live edits from
parallel Phase 7 lanes. The final branch gate must rerun them. The isolated benchmark itself did not attempt to
reinterpret “off” by launching a process.

## Exact benchmark commands

The benchmark driver was `/tmp/phase9_protocol_benchmark.py`. Raw JSON and stderr are under
`/tmp/miller-phase9-model-decision.6wQU4Z`.

### BGE

```bash
JULIE_EMBEDDING_CACHE_DIR=/tmp/miller-phase9-model-decision.6wQU4Z/cache \
  python /tmp/phase9_protocol_benchmark.py \
  --output /tmp/miller-phase9-model-decision.6wQU4Z/bge-rc3-common-probe.json \
  --stderr /tmp/miller-phase9-model-decision.6wQU4Z/bge-common-probe.stderr.log \
  --label bge-small-rc3-metal -- \
  /tmp/miller-phase9-model-decision.6wQU4Z/rc3/unpacked/julie-semantic-sidecar \
  serve --model bge-small-en-v1.5-f32
```

### CodeRank

```bash
PYTHONDONTWRITEBYTECODE=1 \
HF_HOME=/tmp/miller-phase9-model-decision.6wQU4Z/coderank-hf \
TRANSFORMERS_OFFLINE=1 \
HF_HUB_OFFLINE=1 \
PYTHONPATH=/tmp/miller-phase9-model-decision.6wQU4Z/coderank-source:<cached-einops-root> \
JULIE_EMBEDDING_SIDECAR_MODEL_ID=nomic-ai/CodeRankEmbed \
  <isolated-python> /tmp/phase9_protocol_benchmark.py \
  --output /tmp/miller-phase9-model-decision.6wQU4Z/coderank-python-common-probe.json \
  --stderr /tmp/miller-phase9-model-decision.6wQU4Z/coderank-common-probe.stderr.log \
  --label coderankembed-julie-python-mps -- \
  <isolated-python> -m sidecar.main
```

The concrete interpreter was the existing read-only uv environment
`/Users/murphy/.cache/uv/environments-v2/pytorch-baseline-e73453e65c91b552/bin/python`.
Its relevant runtime versions were Python 3.12, torch 2.12.0, sentence-transformers 5.5.1,
transformers 5.11.0, and numpy 2.4.6.

### Raw result locations

- `bge-rc3-benchmark.json`
- `bge-rc3-common-probe.json`
- `bge-rc3-forced-cuda-fallback.json`
- `bge-concurrent-a.json`
- `bge-concurrent-b.json`
- `coderank-python-common-probe.json`
- `coderank-python-common-probe-warm.json`
- `coderank-concurrent-a.json`
- `coderank-concurrent-b.json`
- `rc3-coderank.stderr.log`
- `rc3-coderank.exit-code`

These `/tmp` artifacts are supporting diagnostics, not durable release evidence.

## Final-behavior comparison

The bounded **evaluation adapter** is implemented without a new MCP tool or production model option:

1. Freeze a CodeRank evaluation pin:
   - model id `nomic-ai/CodeRankEmbed`;
   - revision `3c4b60807d71f79b43f3c4363786d9493691f8b1`;
   - weights SHA-256 `827529bcd58aef0d9082e66eeff7e7d53a02f62bd005f841a26b3d3e2fb17ebe`;
   - 768 dimensions, CLS pooling, L2 normalization;
   - explicit empty query instruction for current-Julie behavior;
   - `vec0-int8-768-cosine-v1` storage schema.
2. Keep that pin out of ordinary environment selection unless the evaluation adapter is explicitly enabled.
   An unavailable production option is worse than an honest bounded evaluator.
3. Add a launcher seam that can run a protocol-v1-compatible executable with a supplied environment and
   expected pin. The copied Python producer already implements `health`, `embed_query`, `embed_batch`, and
   `shutdown`; it does not need a wire-protocol change.
4. Make expected-pin handshake validation compare health directly against the injected evaluation pin rather
   than requiring membership in the production `KnownEncoders` list. Preserve exact model id, dimensions,
   hash/revision/pooling when the adapter reports them.
5. Build a shadow 768-d generation through Miller's existing corpus-card/chunk and converge path. Do not import
   Julie's vector database or change corpus boundaries.
6. Point the unchanged production `SemanticSearchArm`, fusion, `search`, and `context` at that isolated
   generation and the same live CodeRank query producer.
7. Freeze BGE and CodeRank arm identities, corpus generation, snapshot, task prompts, fusion profile, output
   budgets, model runtime, and hardware before the first evaluator task.
8. The visible action-efficiency subset ran first. Its frozen decision scope was `subset`, so it could not
   promote either arm and did not justify spending sealed tasks.
9. The adapter remains evaluator-only. No production model selector or MCP tool was added.

This path is smaller and more honest than adding ONNX or a second inference runtime to the RC3 Rust sidecar.
It exercises the exact final Miller behavior while changing only model identity and producer.

### Adapter verification

- Exact CodeRank fingerprint and storage-schema tests pass.
- Wrong model, dimensions, hash, revision, pooling, prompt identity, and unknown config fields refuse loudly.
- A real 768-d sqlite-vec shadow generation promotes and serves actual `search` and `context`.
- BGE-to-CodeRank shadow promotion and failed-shadow rollback retain a queryable BGE generation.
- Two protocol clients remain isolated.
- Evaluator evidence records model identity without recording secret environment values.
- The actual current-Julie Python producer passed health, embedding, convergence, search, and context on MPS.
- `MILLER_SEMANTIC=off` returns before config reads, process launch, or vector-path access.

### Frozen visible action-efficiency result

| Metric | BGE-small | CodeRankEmbed |
|---|---:|---:|
| Correct tasks | 1 / 5 | 1 / 5 |
| Wrong-action rate | 0.2 | 0.2 |
| Median tool calls | 3 | 2 |
| Median tool-output tokens | 1,728 | 1,655 |
| p75 duration | 31,627 ms | 20,461 ms |
| Relevance MRR / recall@6 | 0 / 0 | 0 / 0 |

The scorer returned relevance, correctness, efficiency, and action verdicts of `pass`, but the only allowed
model-decision result was `not_decisional` because this was a visible subset. Four tasks failed in both arms,
so CodeRank's one-task efficiency improvement is not a credible production-promotion signal.

## Exact evidence CodeRank must provide to displace BGE

CodeRank does not displace BGE by tying an embedding rank probe. It must:

1. Run through the unchanged final Phase 4 search and Phase 5 context behavior on the exact same frozen tasks,
   corpus, snapshot, fusion, and output budgets.
2. Meet the evaluator's correctness floor with no additional wrong actions, P0/P1 failures, or recovery
   regressions.
3. **Win**, not merely tie, the predeclared agent action-efficiency decision: fewer tool calls/context tokens
   and/or lower end-to-end task wall time on the frozen evaluator. No post-hoc embedding-only metric may replace
   that rule.
4. Demonstrate that the action-efficiency gain is large enough to justify its measured approximately 6x memory,
   9x warm-start, and 10x query-latency costs. If final action efficiency ties, BGE wins the resource tie-break.
5. Pass the applicable released-package protocol, platform acceleration, concurrency, deterministic-output,
   automatic-fallback, and zero-work checks on Apple, Windows, and Linux.
6. Provide a pinned, redistributable, maintainable production serving path. An evaluation-only Python adapter
   can decide quality but cannot by itself qualify CodeRank as Miller's packaged default.

Until all six are true, BGE-small remains the default and CodeRank remains an unproven alternative.

## Gate disposition

| Phase 9 model item | Result |
|---|---|
| Same visible model-only corpus | Pass; two committed tasks and ten independent probes tie at rank 1 |
| Cold/warm latency and memory | Measured locally for exact RC3 BGE and frozen Python CodeRank |
| Concurrent processes | Measured locally; both deterministic, CodeRank about 1.1 GiB per process |
| Automatic fallback | Exact RC3 BGE pass, forced unavailable CUDA truthfully resolves to CPU |
| Zero-work off | Contract/tests present; final branch rerun still required |
| Same final Miller search/context behavior | Pass through evaluator-only pin/producer/storage lane |
| Frozen workspace/index identity parity | Pass on all five snapshots; model/vector identity differs |
| Visible calls/tokens/task wall | Measured; CodeRank lower on the one jointly correct task |
| Visible correctness/relevance | Tie; 1 / 5 correct, zero relevance for both |
| Sealed decision tasks | Not opened; visible evidence did not justify the spend |
| Current default | **BGE-small** |
| Final Phase 9 model gate | **Closed: retain BGE-small** |
