# Miller Semantic Integration — Program Design

**Date:** 2026-07-19
**Status:** Draft for user review (brainstorming output; not yet committed to a feature branch)
**Scope:** Multi-phase program executed by implementation teams. This document is the authoritative
design; per-phase implementation plans derive from it via razorback:writing-plans.
**Architecture risk:** HIGH (recorded via architecture-quality gate; four adversarial Codex reviews
folded in per section, plus a whole-document doubt pass before user sign-off).

---

## 1. Context and problem

Miller's lexical-only bet succeeded where it was tested and failed where it wasn't:

- `search symbol`: 0.7–2% empty across ~1,100 fleet calls (June–July 2026). `search auto`: 1.3%.
  `context`: ~0%. The FTS5/BM25 symbol path matched Julie's tantivy quality.
- `search source`: 42–60% empty by week. `search content`: 26–46%. `search file`: 50%.
  These are natural-language paraphrase queries that literal FTS cannot rescue.
- Julie's felt quality edge was its **symbol-level semantic (embedding) search** — confirmed by the
  user and by extraction of Julie's implementation (symbol-card embeddings only; Julie never
  embedded source chunks).
- The semantic layer was assigned to Eros by the 1.0 boundary. Eros's direction has shifted and it
  may never ship. The user has decided (2026-07-19) to revise the boundary and fully integrate
  semantic capability into Miller — not as a bolt-on search boost, but strengthening every tool.

Separately, telemetry shows `edit replace_text` at a 21% error rate in the instrumented July cohort
(31% raw historical, but pre-instrumentation rows lack failure reasons and telemetry lacks version
stamping — see §9.4). This reliability work rides the program as an independent lane.

## 2. Decision summary

1. **Boundary revision (ADR-0003, phase 0):** Miller owns *optional local semantic retrieval*.
   Eros (if/when it ships) owns fleet-level semantics: cross-workspace ranking, guidance/confidence
   views, embeddings-as-a-service orchestration. CLAUDE.md, README.md (which still assigns
   semantic/vector retrieval to Eros), and AGENTS.md are updated in the same change. ADR-0003
   includes an Eros migration/deprecation inventory and names a Julie-compatibility owner.
2. **New shared repo `julie-semantic-sidecar`:** a self-contained, hardware-accelerated embedding
   binary consumed by both Miller and Julie. Replaces Julie's Python/pytorch sidecar over time.
3. **Runtime: llama.cpp**, vendored. Metal (macOS, default-on), Vulkan (Windows/Linux — zero
   end-user downloads; loader ships with GPU drivers), CPU compiled into every build. CUDA is an
   optional opt-in tier, never the default (~373MB redistributables). Chosen as the lowest-risk
   runtime with the required Metal+Vulkan pair (ONNX Runtime has no MPS; candle/mistral.rs/TEI
   have no Vulkan; MLC is LLM-serving oriented with weak embedding support). CoreML is explicitly
   rejected on Apple Silicon (slower than CPU for this class of model).
4. **Model policy:** default **Qwen3-Embedding-0.6B GGUF** (Apache 2.0, 1024d MRL). Storage dims
   and quantization are **benchmark outputs, not constants** (256d int8 + higher-precision rescore
   is the favored lane; 256/512/1024 × quantization lanes gated on recall@10 / nDCG@10 per
   language). Fallback tier must be Apache/MIT-licensed (bge-small, arctic-embed candidates);
   EmbeddingGemma is license-gated on HF and cannot be a silent fallback; jina-code (CC-BY-NC) and
   CodeRankEmbed (community GGUF, unclear license) are rejected. Model weights are **not** in
   release archives: explicit prefetch (`miller semantic prepare`-style CLI verb) or consented
   first-use download with pinned sha256 into a shared cache; atomic partial-download handling,
   concurrent-download lock, offline fail-loud with actionable message, disk-space preflight.
5. **Hybrid retrieval, lexical parity preserved:** the lexical-only path remains byte-identical to
   today (the Bm25/backends parity invariant is untouched). Hybrid is a separate semantic arm fused
   by weighted RRF, active only when the vector artifact is ready and fingerprint-matched.
6. **No new MCP tools.** Surface grows only as improved behavior of existing tools, new field/mode
   values, CLI verbs, dashboard count-level panels, skills, and docs. (MCP-stinginess rule.)

## 3. Constraints and invariants (load-bearing)

- **Local-first.** No query or source-content egress, ever. The single permitted network
  operation is consented, sha256-pinned model/binary acquisition (see §4.4); the dashboard-font-CDN
  rule extends to everything else.
- **Zero-config acceleration.** Metal/Vulkan out of the box; no multi-GB vendor downloads for the
  default path; CPU always works.
- **Fail-visible, never fail-silent.** Missing/stale/corrupt vector artifacts degrade to lexical
  with the reason in `workspace status/health` + telemetry — mirroring the search-sidecar pattern.
- **`MILLER_SEMANTIC` is a three-state contract:** `off | shadow | on` (default `on` after GA;
  `shadow` during rollout). `off` is a **permanent guarantee**: no model download, no child
  process, no vectors.db writes, no GPU probe, zero added latency. `0` aliases `off`.
- **Determinism as an artifact contract.** Exact KNN ordering (distance, then integer rowid);
  dedupe before fusion; versioned fusion profiles; a vector generation is queryable only by an
  encoder with the identical fingerprint.
- **Language parity.** Chunk corpus inherits all-language coverage from content.db. Symbol-card
  corpus coverage is verified per language on a real extract before any feature depends on it
  (`SELECT language, kind, COUNT(*) … GROUP BY 1,2`).
- **Test split.** Fast suite stays pure (deterministic fake sidecar; contract-faithful fixtures
  carrying the sidecar's real health/metadata shapes). Anything spawning the real sidecar is
  `[Trait("Category","Scale")]`, obtained via a `ScaleTestSupport.RequireSemanticSidecar()`
  sibling of the julie-extract signal, and the convention guard extends to it.
- **Hosted-service rule.** No new hosted service reads bootstrap getters in its constructor; the
  startup contract test (`HostStartupRegistrationTests`) extends to the semantic services.
- **Privacy.** Telemetry stores no query text (existing rule). New gate: sidecar error paths are
  proven not to echo query payloads into `SetError`'s persisted exception text (truncation +
  scrubbing at the RPC-client boundary; test-enforced).
- **Guidance channels.** ServerInstructions gains **no** semantic line (ADR-0001: the core is a
  discovery contract; semantic ranking is automatic behavior, not a discoverable action).
  Capability surfacing rides tool descriptions (pool at 5,899/9,000), status/health output,
  onboarding, skills, and NextStepHints only where a real user action exists.

## 4. System architecture — `julie-semantic-sidecar`

### 4.1 Binary and process model

- Thin Rust shim vendoring llama.cpp; one pinned binary per platform (aarch64-apple-darwin,
  x86_64-apple-darwin, x86_64-unknown-linux-gnu, x86_64-pc-windows-msvc — same matrix as
  julie-extract; keep in step with pins).
- Speaks the existing **`julie.embedding.sidecar` v1** protocol verbatim: newline-delimited JSON
  envelopes (`schema`, `version:1`, `request_id`, `method`, `params`), methods
  `health | embed_query | embed_batch | shutdown`, error envelope with
  `parse_error | invalid_params | embed_error | internal_error | unknown_method |
  serialize_error`. `embed_query` applies query instruction policy; `embed_batch` applies document
  policy — no new `kind` field. Existing validation invariants preserved (dims echo, batch count
  match, exactly-one-of result/error, request-id echo).
- **v1 additive health metadata (backward compatible):** model sha256 + revision, sidecar build
  identity (llama.cpp commit + shim version), pooling, normalization, output dims,
  instruction-policy version, max text tokens / batch items / request bytes, backend/device
  identity, `accelerated`, `degraded_reason`. Protocol spec hardened in phase 1: unknown-field
  rules (ignore-unknown), capability negotiation via health, truncation semantics, and the exact
  query/document prompt templates per supported model.
- Model-specific correctness knobs (pooling=`last` for Qwen3, `<|endoftext|>` append, instruction
  prefixes, L2 normalization, MRL slice-then-renormalize order) live **inside the shim**, keyed by
  model identity; callers never pass them. Output is always L2-normalized.
- Per-item failure isolation (binary-search a failing batch to the poison text; zero-vector +
  flagged item, mirroring Julie's proven behavior). Stdout purity during model load (llama.cpp
  writes to fd1; the shim redirects during load — the Julie sidecar's dup2 lesson).
- Backend selection: CPU in every build; first-start micro-benchmark (batch-1 + indexing-batch
  shapes) selects Metal/Vulkan/CPU per machine and caches the choice keyed by shim version + model
  hash + GPU/driver identity. "Vulkan slower than CPU" is a normal cached fallback, not an error.

### 4.2 Miller's process lifecycle (resident child, not a daemon)

- `SemanticEmbeddingSession` (Miller.Server): one sidecar child per Miller server process, stdio
  transport, owned for the process lifetime. Lazy single-flight start on first semantic use
  (query or converge). Child exits on stdin EOF when Miller dies — no sockets, no named pipes, no
  singleton locks, no PID files, no detached processes.
- Per-request deadlines; one automatic restart after transport failure; then a circuit breaker
  (`FATAL_THRESHOLD`-style cap) that opens → all semantic features degrade to lexical with reason
  `circuit_open`. Application-level embed errors do not trip the breaker (Julie's distinction).
- Query-priority scheduling: converge work runs in bounded batches (250 texts/RPC) and yields to
  query embeds; a minimum background quota prevents queries starving convergence forever.
- Warm query embeds target 10–150ms. CLI one-shot invocations pay cold start (0.3–3s) when they
  need semantic; acceptable and noted in CLI output. Multi-process swarms: lazy start bounds
  resident copies; an idle-unload policy (or a proven concurrency envelope) is validated by
  multi-process RAM tests in phase 3 before default-on.
- Julie adoption path: same binary via `JULIE_EMBEDDING_SIDECAR_PROGRAM` (+ raw-program flag),
  drop-in for its `SidecarEmbeddingProvider`; Julie's own host/daemon remains Julie's business.
  CI pins a Julie version for compatibility tests (not "current branch").

### 4.3 Packaging and pinning (mirrors julie-extract exactly)

- `scripts/semantic-pins.json` (separate file — the MSBuild version-guard regex takes the first
  `"version"` key, so a second key in `julie-pins.json` is a trap), `restore-julie-semantic.sh/.ps1`
  (or the existing scripts parameterized by binary name).
- `Miller.Server.csproj`: Content/Link copy pair, chmod target, `VerifyPinnedSemanticSidecarVersion`
  target (offline `--version` check against pins; stale ⟹ build fails, missing ⟹ builds + runtime
  fails loud with restore instructions).
- `MillerSemanticContract` beside `MillerExtractContract`: runtime gates are protocol/fingerprint
  versions, not product versions (the D7 split).
- CI: restore steps per platform; release.yml gains the archive assertions, smoke (`--version` +
  one embed round-trip), sha256 sidecars. **Real-hardware Metal and Vulkan CI lanes** are added in
  phase 3 — CPU-only smoke cannot back GPU claims.
- Leadership: the semantic binary version does **not** participate in indexer leadership.
  `LeadershipEligibility` stays single-dimensional (julie-extract only). A semantic version/
  fingerprint mismatch invalidates the *sidecar artifact* (rebuild/shadow), never an instance's
  eligibility.

### 4.4 Model acquisition contract (single owner: the sidecar binary)

Doubt-pass addition — acquisition previously had no owner and no interface.

- The **sidecar binary owns all model acquisition** via a `prepare` subcommand
  (`julie-semantic-sidecar prepare [--model <id>]`): resolves the pinned manifest (model id →
  sha256 + size + source URL), downloads atomically (temp + rename), verifies sha256, handles
  concurrent invocations with a cache lock, honors offline mode with a fail-loud actionable
  message, and prints machine-readable progress. The RPC protocol gains **no** download method —
  a `health` on a missing model reports `ready:false, degraded_reason:model_not_prepared`.
- Cache path: `JULIE_EMBEDDING_CACHE_DIR` → platform cache dir (`~/.cache/julie-semantic`,
  `%LOCALAPPDATA%`…), shared between Miller and Julie by construction. The manifest (model ids,
  shas, dims, prompt templates) is versioned inside the sidecar binary; Miller never parses model
  URLs.
- Miller's `miller semantic prepare` CLI verb and any dashboard/status affordance shell out to the
  sidecar's `prepare`; consent semantics (§10 P4) live in Miller, mechanics in the sidecar.
- Disk preflight before download; partial-download cleanup on startup.

## 5. The vector artifact — `<workspace>/.miller/vectors.db`

### 5.1 Sidecar class and freshness

- `VectorSidecar` (Miller.Indexing) mirrors `SymbolSearchSidecar`: path derivation, env handling,
  `TryOpen` / `OpenRequired` (fail-visible with "run `miller workspace refresh`" messaging),
  atomic temp→move full builds, `SidecarCorruptionRecovery` registration (per-generation: corrupt
  vectors delete + rebuild without touching symbols.db).
- **Status splits availability from convergence** (the 5-state enum is not overloaded):
  compact `vectors: ready | ready (updating; N files pending) | building 42% (not queryable) |
  downloading | unavailable (reason) | incompatible | circuit-open | disk-blocked | disabled`;
  exact revisions/coverage/fingerprints in JSON `workspace status/health` only. Facts flow through
  `WorkspaceFactsAssembler` → `WorkspaceRender` like search-sidecar facts.
- **Generation identity is FIVE separate fields, not one monolithic fingerprint** (doubt-pass
  correction — a single fingerprint would force re-embedding on every revision or fusion tweak):
  1. `encoder_fingerprint` — model sha256 + revision, dims, tokenizer/prompt/pooling identity,
     normalization. Governs query compatibility: **a generation is queryable by any encoder whose
     `encoder_fingerprint` matches** — nothing else about the reader must match.
  2. `storage_schema` — vector schema version, quantization lane, distance metric. Change ⟹
     shadow rebuild. Quantization records `none` until the benchmark decides (field exists from
     day one; retrofitting identity fields is the invalidation bug class this prevents).
  3. `corpus_generation` — card-schema version, chunker + truncation-policy version, corpus scope
     flags. Change ⟹ re-embed affected corpus.
  4. `reader_compatibility` — writer (Miller) version + **minimum reader version**. Governs which
     Miller binaries may open the artifact; never triggers re-embedding.
  5. `fusion_profile` — RRF k/weights/policy version. Query-time only; lives with the reader, is
     recorded in telemetry, and **never** invalidates stored vectors.
  Only changes to 1–3 require re-embedding work; 1–2 via shadow rebuild, 3 via targeted re-embed.
- **Rollback:** the last compatible generation is preserved through incompatible upgrades (shadow
  generation built beside, atomic promote per FullRebuildPromotion lessons); GC after a soak
  window. An older Miller binary can keep reading its compatible generation.

### 5.2 Corpus contract

Default corpus (matches Julie's proven quality bar; initial build minutes, not hours):

1. **Symbol cards** — one vector per eligible symbol. Card text v1 is **local-only**:
   `{kind} {qualified name} {signature first line} {doc excerpt ≤300} in: {container} {path}`,
   ~1,200-char budget, word-boundary truncation, comment-marker stripping. **No graph enrichment
   in v1** (no callees/members/implementors — editing A must not invalidate callers' cards; graph
   facts remain structured signals at fusion time). Eligibility is **symbol-kind/data-driven, not
   a language blocklist** (doubt-pass correction): a language is excluded only where a real
   extract shows it emits no eligible symbol kinds; the per-language coverage matrix is published
   with the feature, and a supported language with eligible kinds but missing card coverage is a
   `julie-extractors` blocker, per the language-parity rule. (Expected outcome matches Julie's
   practical list — markdown/json/yaml/toml/css/html/regex/sql carry no eligible kinds — but the
   evidence, not the list, is the contract.)
   Test symbols DO get cards (unlike Julie), marked `is_test` in the filter columns: excluded
   from default search recall via the metadata filter, but available to impact's likely-test
   ranking and to explicit test-scoped queries.
2. **Docs/config prose chunks** — one vector per existing `content_chunks` row where the source
   classifies docs-like (`ContentFileClassifier.IsDocsLike`), with an explicit token limit and
   versioned truncation policy (measured: chunks average 836 tokens; a third exceed 1,024).
   External/web imports excluded unless separately enabled.

Explicitly designed, activation eval-gated (fully specified in §10 P6, not deferred):
all-source chunk corpus (opt-in), enriched symbol cards (reconstruct-all-and-hash mechanism),
Type-3 clone body-vector set.

Re-embed gating: `embed_text_hash` per unit — a unit re-embeds only when its constructed text
changed. Idempotent replay.

### 5.3 Convergence (the deliberate break from the synchronous sidecar pattern)

- **No durable job queue.** julie's `revision_file_changes` is the durable work log. vectors.db
  meta holds **two independent desired-state cursors** (doubt-pass correction — symbol cards and
  chunks have different sources with different failure/lag behavior): a **symbol cursor**
  (`symbol_completed_revision` / `symbol_target_revision`, sourced from `symbols.db`) and a
  **chunk cursor** (`chunk_completed_revision` / `chunk_target_revision` + content.db schema
  identity, advancing only after `content.db` itself proves current at the target revision —
  content converge failures are caught-and-continued today, so chunk vectors must never claim a
  revision content.db hasn't reached). Each cursor carries its own last-error. Status reports the
  laggier of the two. A bounded, coalescing in-memory wake signal (capacity 1) connects
  `IndexerSidecarConverger` (which only stamps target revisions + wakes — cheap, stays under the
  ops gate) to the drain loop.
- `VectorConvergeService` (leader-side hosted service, lazy bootstrap-getter discipline): on wake,
  recompute changed paths from `completed_revision` via `FreshnessReader.ChangedSince`, coalesce
  per path, rebuild card/chunk texts, hash-gate, embed in bounded batches **outside any gate**
  (snapshot inputs under the gate, release, embed, reacquire briefly, re-validate identity +
  artifact_id, commit), stage per-revision and **advance the cursor atomically with the staged
  batch** — vectors.db never claims a revision it only partially contains.
- Escalation to **shadow full rebuild** when: delta history missing, changed-vector ratio above
  threshold (bulk refactors), identity/fingerprint change, `artifact_id` change (promotion), or
  the per-revision transaction would be too large. Shadow generation builds beside the live one
  (old generation stays queryable), atomic promote, abandoned-generation cleanup.
- Model swap mid-flight: in-flight inference is cancelled; responses produced for a previous
  generation are never committed.
- Initial build: background with progress in status; semantic arm not offered until built;
  lexical behavior untouched throughout. Backpressure: retry budget, poison-input isolation,
  disk-space preflight (2–3× expected artifact size incl. WAL/temp), WAL autocheckpoint policy +
  short reader snapshots (long readers block checkpoints), fragmentation-triggered rebuild.
- **Cross-workspace rule:** foreign workspaces get vector *convergence* only from their own
  leader. The cross-workspace refresh write-lease ends when `Refresh` returns, so async foreign
  embedding is illegal; the service performs no vector convergence. Cross-workspace **reads use
  an already-ready compatible generation when one exists** and degrade lexically only when none
  does (reason `vectors: not built — open workspace to build`).
- **Writer discipline (doubt-pass precision):** only the writer-lock holder embeds *corpus units*
  or mutates `vectors.db`. **Any reader process may embed queries** through its own
  `SemanticEmbeddingSession` against a compatible-`encoder_fingerprint` generation — query
  embedding writes nothing.

### 5.4 Storage schema (sqlite-vec)

- Pinned sqlite-vec release (v0.1.9 line; no alpha APIs), loaded from an absolute packaged path
  via `Microsoft.Data.Sqlite` `LoadExtension`, `vec_version()` verified at open. The
  **sqlite-vec-on-Native-AOT spike across all four RIDs is a phase-0 HARD GATE.** sqlite-vec gets
  the full second-native-artifact treatment (doubt-pass addition): per-RID pins + checksums in
  `semantic-pins.json`, restore-script section, csproj copy layout next to the binary, release
  archive assertions, and packaged runtime smoke — same ownership pattern as the sidecar binary.
- Integer vec0 rowids + ordinary mapping tables (symbol_id/chunk_id ↔ rowid); text-PK vec0 is
  alpha-line, not used. Two vec0 tables: `symbol_vectors`, `chunk_vectors`; element type and
  dims are **parameterized by the benchmark-selected `storage_schema` lane**
  (`float[{dims}]` or `int8[{dims}]`, `distance_metric=cosine`), + meta + mapping + filter tables.
- Filters: `path`, `kind`, `is_test` as vec0 **metadata columns** (filterable); `language` as a
  partition key only if benchmarks justify; `path` is never a partition key (high cardinality).
  `LIKE/GLOB` unsupported in vec0 metadata ⟹ glob-style `file_pattern` scoping uses the
  **prefiltered manual-distance path** (resolve matching rowids first, brute-force distance over
  the subset). Oversampling is documented as approximate and used only where prefiltering is
  impractical.
- Quantization lane (post-benchmark): int8 candidates + higher-precision rescore copy, or plain
  float32 if the benchmark says the complexity isn't paid for. Slice → renormalize → quantize
  order pinned.
- Concurrency: inference before the write transaction; delete+insert+mapping+cursor in one short
  transaction; WAL; release-gated crash tests at every boundary (post-inference, mid-vec0-mutation,
  pre-cursor-advance, post-model-swap, post-promotion) plus vec0 DELETE/checkpoint/corruption
  tests on the exact pinned extension on all four RIDs.

## 6. Retrieval integration

### 6.1 Typed candidate seam (prerequisite refactor)

`SearchRouteExecutor` currently returns rendered output + count; fusion cannot operate on
rendered strings. Phase-2 refactor splits candidate generation → fusion → rendering with typed
candidate lists, preserving byte-identical output on the lexical-only path (golden-output tests).

### 6.2 Hybrid symbol search

- A separate semantic retrieval arm composed at the executor level — **no decorator over
  `ISymbolLookupIndex`** (it also serves exact lookup/resolution for inspect; wrapping risks
  non-search behavior changes). No-semantic path calls today's code unchanged.
- Fusion: weighted RRF (k and per-profile weights are versioned, eval-tuned; Julie's profile
  shape as the starting point: symbol-lookup 1.0/0.3, conceptual 0.5/1.0, mixed 0.8/0.8).
  Deeper per-arm retrieval; dedupe before RRF; stable tie-breaks (score, then symbol id).
- Filter-aware vector recall: filters pushed into KNN (metadata/prefilter paths per §5.4) or
  deterministic adaptive refill — mirroring the lexical arm's 500-candidate escalation so
  filtered hybrid never silently loses results.
- Output contract: `score` keeps meaning lexical score; participating hybrid rows carry optional
  `rrf_score` + per-arm ranks; absence/staleness reasons ride telemetry + status, not the result
  payload; JSON stays a bare array (no envelope change).
- `SemanticQueryPolicy` (new, small, versioned): routes identifier-like/path-like/short queries
  lexical-only; clear prose/docs queries hybrid; ambiguous queries decided by weak lexical
  evidence (not by the empty-diagnosis classifier, which was built for post-hoc labeling).

### 6.3 Semantic rescue and modes

- New final rung in the existing auto-rescue ladder (compact, mode=auto, ≤2 rows total,
  single-affordance rule): symbol-card KNN + docs/config chunk KNN, rows labeled
  `semantic symbol` / `semantic docs`; telemetry `auto_rescue_kind=semantic_symbol|semantic_docs|
  semantic_mixed`. Trigger: rescue-eligible AND SemanticQueryPolicy says semantically-shaped.
- `mode=content`: chunk-vector hybrid arm (docs/config embedded by default).
- `mode=source`: **lexical-only under the default corpus** with a `source_chunks_not_indexed`
  note when the semantic arm would have been consulted — symbol cards are never presented as
  source hits (mode contract honesty). True source hybrid activates with the all-source opt-in.
- CLI-only `--arm lexical|semantic|hybrid` debug flag for evaluation; not an MCP parameter.

### 6.4 Per-tool integration

- **context:** vector-recall seeds fused **within** the existing 10-seed budget (never additive);
  explicit `entry_symbols` and failing-test/stack-trace anchors always preserved; query embedded
  concurrently with lexical seeding under a hard deadline (fallback byte-identical on miss);
  cosine as a stable tie-break within `(hop, existing relevance)` — not a weighted sum (the
  current relevance scale grows with query token count). Promotion beyond tie-break is eval-gated.
- **impact:** ordering becomes `hop ASC → cosine(changed card, candidate card) → stable id` for
  impacted symbols and likely-tests (test symbols embedded with `is_test`); cosine never removes
  graph members; the diff→whole-file degradation keeps membership and only orders by similarity.
  Note the true truncation is the `limit` parameter (default 100); 40/20 are compact display cuts.
  Eval: likely-test hit-rate@20 before any role stronger than ordering.
- **inspect:** `related:` block at depth=overview/full — symbol-card KNN neighbors excluding
  already-shown graph relations, bounded (≤5), compact + JSON. Ambiguous-candidate lists ordered
  by query similarity.
- **trace:** no new bridge signal. Cosine may attach to the existing `FieldSetSignal` as
  non-anchoring diagnostic evidence only — it never creates an edge or raises a confidence band
  (it derives from the same evidence as the Jaccard signal; independence is what BridgeScorer
  counts). refs-mode empty results may carry a NextStepHint toward semantic neighbors (hint only).
- **metrics clones:** Type-2 detection via a deterministic **token-shingle MinHash/LSH
  near-duplicate analyzer** (fixed normalization, seeds, LSH params; separate from the exact
  `CloneGroupReader`, which is untouched), `kind=near_duplicate` with similarity, new history
  metric `near_duplicate_group_count` (append-only metric name; dashboard sparkline free).
  Card vectors are NOT used for clone claims (cards omit bodies). Type-3: opt-in body-vector set
  reranking MinHash candidates (never all-pairs KNN), precision-gated (§10 P6).
- **references candidates (dead-code):** semantic similarity is **display-only** — clustering and
  review ordering. It never changes candidate counts and never becomes a suppression rule
  (a dead duplicate is maximally similar to its live original; similarity transfers topic, not
  liveness). The one-directional evidence invariant stands.
- **workspace onboarding:** capability note in `InstructionNotes` when semantic is available/
  degraded; no new fact families (Eros-boundary caution).
- **dashboard:** count-level only per ADR-0002 — vector coverage/status per workspace,
  `near_duplicate_group_count` trend, semantic participation counters from shared telemetry.
  Per-symbol semantic detail stays CLI-only (dead-code precedent).

### 6.5 Cross-cutting behavior

Every semantic feature: degrades to exactly-current behavior when vectors are absent / stale /
incompatible / circuit-open, with machine-readable reason in telemetry metadata (and status);
fail-open on RPC timeout (lexical result returns; participation recorded); semantic failure never
converts a lexical success into a tool error (release-gated invariant test).

## 7. Edit reliability lane (independent workstream)

Verified current state: `match_mode=auto` is already the request default with an
exact→normalized→fuzzy ladder, and apply-time stale convergence waits up to 2.5s. Telemetry shows
149/167 calls explicitly passing `exact` ⟹ substantially a guidance/ergonomics problem.

Work items:
1. **Instrumentation first:** stamp `edit_failure_reason` on every failure path (41/52 historical
   errors carry none); add Miller version to telemetry records (gates become cohort-relative).
2. Find and fix what steers agents to `exact` (tool description, skills, error-message guidance);
   error messages must carry the recovery action at the point of failure (scope disambiguation,
   mode suggestion).
3. Widen `Normalized` whitespace handling to Unicode spaces/NBSP/form feed.
4. Evaluate fuzzy policy: the 160-char snippet cap and distance ceiling 3 yield zero fuzzy
   successes; propose and gate a revised policy on a replay corpus of historical failures.
5. Stale-target UX: plan-time `stale_target` failures get bounded convergence wait + retry
   (mirroring the apply path) instead of immediate failure.

Gate: replace_text error rate <10% measured on the instrumented, version-stamped cohort.

## 8. Evaluation protocol

- **Dev set (visible) + sealed acceptance set** (owned outside the implementation lane), frozen
  before tuning. Leave-one-repo-out; ≥1 repo never used for selection. Multi-repo, multi-language
  (language-parity: macro-average AND worst-language reported).
- Content: paraphrase intent clusters (scored as clusters, not independent samples), identifier
  queries (non-inferiority required), short-token, negation, ambiguous-concept, generated-code,
  and irrelevant-query negatives.
- Top-level measure: **agent-task completion** on scripted tasks; recall@10 / nDCG@10 diagnostic.
- Embedding parity (shim vs sentence-transformers reference, cosine ≥0.99 on a fixed corpus)
  proves protocol/model equivalence only — it is a phase-1 gate, separate from retrieval quality.
- Benchmarks feeding pins: model choice, dims (256/512/1024), quantization lanes, KNN latency at
  50k/500k (warm/cold), RRF k + weights, backend micro-benchmark thresholds.

## 9. Success gates and measurement

1. **Primary (causal): randomized holdout canary** among SemanticQueryPolicy-eligible calls —
   control (lexical) vs treatment (hybrid). The **canary telemetry contract is frozen in P0**
   (doubt-pass addition): stable assignment unit, query-class enum, experiment/arm id, opaque
   result identifiers with a follow-up attribution window (so "rescue conversion = downstream
   inspect/read of a rescued result" is actually measurable — today every call gets an unrelated
   UUID), and an explicit success event. Enum/counter-only fields (arm, eligibility,
   fingerprints, per-arm result counts, rescue/fallback reason, backend, cold/warm, latency
   buckets). Hard gate: positive treatment effect with confidence interval on eligible-query
   success. **Identifier-query non-inferiority runs as its own shadow population** — identifier
   queries are not canary-eligible (the policy routes them lexical-only), so non-inferiority is
   measured by shadow-executing the hybrid arm on a sample and comparing offline, never affecting
   served results.
2. Secondary absolute objectives (health indicators, not causal gates): content-mode empty rate
   <12% (from 26–46%); auto-mode+rescue empty rate materially down. **Source-mode empty rate
   (<15%) moves to the P6 all-source opt-in cohort** (doubt-pass correction: default `mode=source`
   stays lexical-only under the default corpus, so a GA source-mode KPI was structurally
   unreachable — it now gates the opt-in's value instead of GA).
3. Rescue conversion defined as downstream acceptance (follow-up inspect/read of a rescued
   result), not "returned something".
4. Reliability gates: semantic failure never breaks a lexical success; bounded fallback rate;
   vector-lag SLO; restart/circuit-open rate; peak RAM/VRAM envelope; disk growth; model-download
   failure rate. Latency reported warm / cold-start / circuit-open separately (p95 overall hides
   mix shifts).
5. Population: single-workspace numbers are dogfood evidence. Acceptance requires aggregate-only
   exports from several operators/repos/language families.
6. Edit lane gate per §7.

## 10. Program plan (phases, dependencies, lanes)

Teams parallelize independent lanes; each phase gets its own implementation plan via
razorback:writing-plans. "Gate" = must pass before dependents ship defaults.

- **P0 — Governance & hard gates** (no dependencies):
  ADR-0003 + CLAUDE.md/README/AGENTS.md boundary reversal (with Eros migration inventory,
  Julie-compat owner). sqlite-vec-on-AOT spike, 4 RIDs (**gate**). Eval protocol + golden/sealed
  sets built (**gate** for P3+ defaults). Model/dims/quantization benchmark → pins (**gate**).
  **Canary telemetry contract frozen** (§9.1 — assignment unit, arm id, attribution window,
  success event). Telemetry version-stamping + edit failure-reason completion (feeds §7 and §9
  cohorts).
- **P1 — Freeze & conformance** (after P0 pins):
  Protocol v1 spec hardening + published conformance fixtures (round-trip corpus with golden
  vectors from the reference implementation). Generation-fingerprint contract doc
  (`docs/contracts/vectors-v1.md`, `docs/contracts/semantic-sidecar-protocol-v1.md`).
- **P2 — Parallel build lanes** (after P1 freeze; lanes genuinely independent — doubt-pass
  correction: everything requiring a *published* sidecar binary moved out of P2b):
  *(a)* Sidecar implementation against conformance fixtures (shim, backends, `prepare`
  subcommand + acquisition contract, parity gate, per-platform CI, release pipeline).
  *(b)* Miller consumption against a **deterministic fake sidecar** only:
  `MillerSemanticContract`, `SemanticEmbeddingSession`, `VectorSidecar` + writer + dual-cursor
  convergence + shadow generations + corruption recovery + status/health facts + telemetry +
  canary-contract plumbing. No pins, no restore scripts, no build guards yet.
  *(c)* Typed candidate seam refactor in SearchRouteExecutor (golden-output parity tests).
  *(d)* Edit reliability lane (§7).
  *(e)* MinHash near-duplicate analyzer + history metric + report/dashboard.
- **P3 — Integration** (after P2a produces a release candidate; **RC publication itself requires
  explicit user approval per the release-discipline rule**):
  Real pins/restore scripts/csproj `Verify` guards/sqlite-vec packaging (moved here from P2b —
  they need a published artifact to verify against); real-sidecar Scale tests; packaged-AOT
  smoke, 4 RIDs; real-hardware Metal + Vulkan CI lanes; pinned-Julie drop-in compatibility test;
  multi-process swarm RAM tests + idle-unload policy (**gate**). Hybrid retrieval +
  SemanticQueryPolicy + rescue + content-mode arm + CLI `--arm` + determinism contract tests,
  behind `shadow`.
- **P4 — Shadow rollout** (after P3): existing users build vectors in `shadow` (no behavior
  change), model download explicit/consented; observe converge health, disk, RAM across the fleet.
- **P5 — Canary → default-on** (after P4 healthy): randomized search canary (**gate** per §9.1);
  then context/impact/inspect/trace/dead-code-display integrations; then default `on`, guidance
  updates (descriptions, skills, onboarding, NextStepHints, dashboard panels), release notes,
  30-day measurement. Julie repo switches its sidecar via pins (its own release cadence).
- **P6 — Specified extensions, activation eval-gated:** all-source chunk corpus opt-in (token
  budget + truncation policy versioned; enables true source-mode hybrid); enriched symbol cards
  (reconstruct-all-and-hash, cost measured, retrieval lift gated); Type-3 clone body-vector set +
  rerank (precision gated). These are designed here and planned like any phase — the gate is
  evidence, not appetite.

## 11. Acceptance criteria (program-level checklist)

- [ ] ADR-0003 merged; CLAUDE.md/README/AGENTS.md no longer assign local semantic retrieval to Eros.
- [ ] sqlite-vec loads under Native AOT on all four RIDs (packaged smoke), `vec_version()` verified.
- [ ] `julie-semantic-sidecar` v1: parity ≥0.99 vs reference; conformance fixtures pass; 4-platform
      archives + shasums; Metal + Vulkan real-hardware lanes green; pinned-Julie drop-in test green.
- [ ] Miller: `MILLER_SEMANTIC=off` guarantees zero semantic side effects (test-enforced);
      `shadow` builds artifacts with zero retrieval change; fingerprint mismatch ⟹ shadow rebuild,
      old generation preserved for rollback.
- [ ] Lexical-only path byte-identical to pre-program behavior (golden-output tests) in every mode.
- [ ] Hybrid: filtered KNN correct (no silent filter loss); determinism contract tests green;
      semantic failure never breaks a lexical success.
- [ ] Canary gate: positive treatment effect with CI; identifier non-inferiority via shadow
      population; no >20% p95 warm-latency regression on eligible queries.
- [ ] Content/auto empty rates at secondary objectives on 30-day post-GA fleet telemetry
      (source-mode KPI gates the P6 all-source opt-in cohort, not GA).
- [ ] Per-language symbol-card coverage verified on a real extract before per-tool features ship.
- [ ] context/impact/inspect/trace/clones/dead-code integrations shipped per §6.4 with per-feature
      participation telemetry; near-duplicate metric trending on dashboard.
- [ ] Edit lane: <10% replace_text error rate on version-stamped cohort.
- [ ] Docs: contracts (vectors-v1, sidecar-protocol-v1), release notes each release, docs/README
      map updated; skills updated; no ServerInstructions growth (AgentInstructionsTests green).
- [ ] Privacy: sidecar error text proven query-free in persisted telemetry (test-enforced).

## 12. Rejected alternatives (with reasons)

- In-process .NET inference (ONNX/TorchSharp): no MPS / no DirectML respectively; AOT packaging
  risk; kills the shared-with-Julie sidecar.
- Reusing the Python/pytorch sidecar: venv bootstrap + model download + Windows process pain is
  the bug class this program exists to retire.
- Resident daemon / shared host for Miller: socket discovery, singleton locks, named-pipe
  Windows risk — rejected in favor of per-process child; revisit only on measured swarm RAM data.
- Score-based fusion: BM25 scores unnormalized across arms; RRF is rank-based.
- Decorator over `ISymbolLookupIndex`: would entangle exact-lookup/resolution behavior.
- Embedding-based clone claims from symbol cards: cards omit bodies; dishonest labels.
- `semantic_sibling_live` dead-code suppression: similarity transfers topic, not liveness.
- Field-set cosine as an independent bridge signal: double-counts evidence; unpriced per-edge RPC.
- Spawn-per-query embedding: 0.3–3s+ cold start per search is not viable.
- jina-code (CC-BY-NC), CodeRankEmbed (license/GGUF provenance), EmbeddingGemma-as-silent-fallback
  (license-gated download): all rejected on license/provenance grounds.

## 13. Research provenance

Five research inputs, 2026-07-19: Miller search-pipeline map; Miller indexing/sidecar machinery
map; Miller per-tool core map (incl. edit telemetry); Julie embedding-stack extraction (protocol,
pipeline, sqlite-vec, hybrid math); runtime/model grounding with citations (llama.cpp backend
matrix + correctness gotchas, sqlite-vec state, model licensing). Four adversarial Codex reviews
(one per design section) folded in, plus a whole-document doubt pass whose nine surviving
objections (split generation identity, dual cursors, canary telemetry contract, source-KPI
relocation, writer/reader embedding split, model-acquisition ownership, P2/P3 lane dependency
fix, storage-lane parameterization + sqlite-vec packaging ownership, data-driven card
eligibility) are folded in and marked "doubt-pass" throughout. Telemetry baselines from
`~/.miller/telemetry.db` fleet data, June–July 2026.
