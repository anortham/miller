# P1 Freeze & Conformance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Freeze the two contracts every P2 lane builds against — the sidecar RPC protocol (`docs/contracts/semantic-sidecar-protocol-v1.md`) and the vector artifact (`docs/contracts/vectors-v1.md`) — and publish conformance fixtures with golden vectors so the P2a sidecar and P2b Miller consumer can each prove correctness without the other existing.

**Architecture:** P1 of the program plan in
[docs/plans/2026-07-19-miller-semantic-integration-design.md](2026-07-19-miller-semantic-integration-design.md) §10.
Contract-first by design: §10 moves everything requiring a published sidecar binary to P3, so P1 produces documents and fixtures only — zero `src/` changes. The protocol contract is a hardening of the **existing, running** `julie.embedding.sidecar` v1 implementation (Julie's Python sidecar), not a green-field spec: where this document and Julie's reference implementation disagree, the reference implementation wins and the discrepancy is recorded.

**Tech Stack:** Markdown contracts; Python 3 fixture generator reusing the P0 model-bench cache (llama.cpp `b10068` llama-server, pinned GGUFs — no new downloads); JSONL fixtures.

**Architecture Quality:** The approved module/interface shape is design §4 (sidecar binary + RPC protocol + prepare subcommand) and §5 (vectors.db: five-field generation identity, dual cursors, shadow generations, sqlite-vec vec0 schema). These contracts ARE the architecture record for P2; the risk is contract-vs-reference drift, mitigated by requiring every protocol statement to cite the reference implementation and every fixture to be generated, not hand-written. Workers report plan mismatches instead of redesigning.

## Global Constraints

Exact values — copy verbatim, never re-derive:

- **Default model pin (P0 benchmark, corrected scoring):** `Qwen3-Embedding-0.6B` f16 GGUF, native 1024d MRL, **storage lane 512d int8**. sha256 + source URL in [`eval/model-bench/bench-pins.json`](../../eval/model-bench/bench-pins.json) (`qwen3-0.6b-f16` entry).
- **Fallback model pin:** `bge-small-en-v1.5` f32 GGUF, 384d, int8 storage. sha256 + source in the same pins file (`bge-small-f32` entry).
- **Qwen3 knobs (verbatim from bench-pins.json):** pooling `last`; append `<|endoftext|>` to every input before tokenization; `query_instruction` = `"Instruct: Given a code search query, retrieve the code or documentation that answers it\nQuery: "`; `document_instruction` = `""`; L2 normalization always; MRL order **slice → renormalize → quantize**.
- **bge-small knobs (verbatim):** pooling `cls`; no EOS append; `query_instruction` = `"Represent this sentence for searching relevant passages: "`; `document_instruction` = `""`.
- **Protocol literals (from the reference implementation):** schema `"julie.embedding.sidecar"`, `version: 1`, methods `health | embed_query | embed_batch | shutdown`, error codes `invalid_request | invalid_json | unknown_method | internal_error` (CORRECTED during Task 1: the design's six-code list was wrong — the reference emits these four; see the contract's Deviations D1), newline-delimited JSON envelopes with `schema/version/request_id/method/params`, invariants: dims echo, batch count match, exactly-one-of result/error, request-id echo.
- **Reference implementation paths (read, cite, do not modify):** `~/source/julie/python/embeddings_sidecar/sidecar/protocol.py`, `~/source/julie/python/embeddings_sidecar/sidecar/runtime.py`, Rust consumer `~/source/julie/src/bin/julie-embedding-host.rs`, consumer tests `~/source/julie/src/tests/core/embedding_sidecar_provider.rs`.
- **storage_schema lane string:** `vec0-int8-512-cosine-v1` (pinned default), fallback lane `vec0-int8-384-cosine-v1`. Format: `vec0-<element>-<dims>-<metric>-v<schema rev>`.
- **sqlite-vec pin:** the version + per-RID checksums in [`scripts/spike-pins.json`](../../scripts/spike-pins.json), proven by the P0 AOT spike on all four RIDs (CI on PR #6).
- **Conformance tolerance policy (frozen here, used by both contracts and fixtures):** output dims exactly equal the requested lane; L2 norm of every emitted vector within `1e-3` of 1.0; cosine similarity to the CPU-generated golden vector `≥ 0.999` per text. Bitwise equality is explicitly NOT the bar (backend numerics differ across Metal/Vulkan/CPU); goldens are generated with the CPU backend for reproducibility.
- **No new downloads:** fixture generation reuses `eval/model-bench/.cache/` (llama.cpp `b10068`, both GGUFs already verified). If the cache is missing, fail with the exact restore command (`eval/model-bench/run-bench.sh` stages `download`+`verify`), do not fetch ad hoc.
- Repo rules apply: 0-warning Release build, fast/Scale test split untouched, no comments narrating code, `docs/README.md` is the docs map.

## Verification Strategy

**Project source of truth:** CLAUDE.md (build + test tiers), `scripts/test.sh`.

**Worker red/green scope:** Task-specific. Contract tasks (1, 2): a documented consistency pass — every protocol/artifact statement resolves to a citation (reference file:line, design §, bench-pins key, or P0 findings doc) and the internal cross-checks listed in the task pass. Fixture task (3): `python3 eval/sidecar-conformance/generate.py --verify` runs green end-to-end (regenerate + tolerance self-check against committed goldens).

**Worker ceiling:** `scripts/test.sh` (fast suite) for confirmation that no code path changed. Workers do not run `scale`/`all`.

**Worker gate invariant:** stated per task below.

**Lead affected-change scope:** lead re-runs the fixture `--verify` and spot-checks contract citations after each task review.

**Branch gate:** `dotnet build Miller.slnx -c Release` (0 warnings) + `scripts/test.sh all` — cheap here (no `src/` changes) but proves the branch ships.

**Replay/metric evidence:** fixture tolerance checks are hard gates (dims exact, norm ≤1e-3, cosine ≥0.999). Fixture generation wall time and file sizes are report-only.

**Escalation triggers:** any change under `src/` or `tests/Miller.Tests/` is out of plan scope — stop and report a plan mismatch.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp in `.razorback/sdd/progress.md`. Reuse passing entries for unchanged HEAD.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Sidecar protocol contract | Batch A | Create: `docs/contracts/semantic-sidecar-protocol-v1.md` | No | None - safe parallel batch. |
| Task 2: Vector artifact contract | Batch A | Create: `docs/contracts/vectors-v1.md` | No | None - safe parallel batch. |
| Task 3: Conformance fixtures + golden vectors | Batch A | Create: `eval/sidecar-conformance/**` (corpus.jsonl, generate.py, golden-*.jsonl, README.md, .gitignore) | No | None - safe parallel batch. |
| Task 4: Docs map + cross-contract consistency pass | None - serial | Modify: `docs/README.md`; may add cross-reference lines (not content changes) to the three files owned by Tasks 1–3 | Yes | Reads all three prior outputs; owns the shared `docs/README.md` file no Batch A task may touch. |

## Task 1: Sidecar protocol contract — `semantic-sidecar-protocol-v1.md`

**Files:**
- Create: `docs/contracts/semantic-sidecar-protocol-v1.md`

**Interfaces:**
- Consumes: reference implementation paths + protocol literals + model knobs + tolerance policy from Global Constraints.
- Produces: the frozen v1 protocol document P2a implements and P2b's fake sidecar mimics. Section names other tasks may cite: `## Envelopes`, `## Methods`, `## Errors`, `## Health metadata (v1 additive)`, `## Prompt templates`, `## Model knob table`, `## prepare subcommand`, `## Conformance`.

**Contract inputs:** Global Constraints block verbatim; design §4.1, §4.2 (deadline/restart/circuit expectations a sidecar must tolerate), §4.4 (prepare); existing contract style exemplar `docs/contracts/canary-telemetry-v1.md` (frozen-contract framing, field tables, "Written when" discipline).

**File ownership:** Create: `docs/contracts/semantic-sidecar-protocol-v1.md`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** The frozen wire contract for `julie-semantic-sidecar`. Start from the RUNNING reference implementation — read `protocol.py` and `runtime.py` and transcribe the envelope/method/error shapes with exact field names and a citation (file:line) for each; where the design's §4.1 hardening adds to the reference (additive health metadata, ignore-unknown-fields rule, capability negotiation via health, truncation semantics, per-model prompt templates), mark those sections **"v1 additive — not yet in the reference implementation"** so P2a knows what is new. Then specify: the model knob table (both pinned models, all knobs from Global Constraints, keyed by model identity — callers never pass knobs); per-item failure isolation semantics (zero-vector + flagged item, binary-search poison isolation); stdout purity during model load; the `prepare` subcommand contract per §4.4 (manifest ownership, atomic download, cache lock, offline fail-loud, `ready:false, degraded_reason:model_not_prepared` health shape); backend selection contract (§4.1: CPU always available, first-start micro-benchmark, cached choice keyed by shim version + model hash + GPU/driver identity, "Vulkan slower than CPU" is a cached fallback not an error); and the `## Conformance` section binding implementations to the fixture set (`eval/sidecar-conformance/`) and the frozen tolerance policy.

**Approach:** Follow `canary-telemetry-v1.md`'s voice: frozen means frozen, every value is a decision, v2 rule for post-ship changes (this contract is pre-ship, so state the same pre-ship amendment convention). Read the Julie consumer tests to capture invariants the prose misses (request-id echo behavior on errors, batch-count mismatch handling). If the reference implementation contradicts a design §4.1 claim, the reference wins for v1 wire behavior — record the discrepancy in a `## Deviations from design` note rather than silently siding with either.

**Acceptance criteria:**
- [x] Every envelope/method/error statement carries a reference citation (file:line) or a "v1 additive" marker — no uncited wire claims
- [x] Both pinned models' prompt templates and knob rows match Global Constraints byte-for-byte
- [x] `prepare`, backend selection, failure isolation, stdout purity, and conformance tolerance sections present and internally consistent
- [x] Worker-scope verification passes (documented consistency pass listed in the report) and the change is handed to the lead per commit mode

## Task 2: Vector artifact contract — `vectors-v1.md`

**Files:**
- Create: `docs/contracts/vectors-v1.md`

**Interfaces:**
- Consumes: Global Constraints (storage_schema strings, sqlite-vec pin, model pins); design §5.1–§5.4 verbatim facts.
- Produces: the frozen artifact contract P2b's `VectorSidecar` writer implements. Section names citable by later phases: `## Generation identity`, `## Invalidation matrix`, `## Cursors`, `## Storage schema`, `## Shadow generations and rollback`, `## Writer discipline`, `## Status vocabulary`.

**Contract inputs:** Design §5.1 (VectorSidecar mirrors SymbolSearchSidecar — cite the real class: `src/Miller.Indexing/SymbolSearchSidecar.cs`), §5.1 five-field generation identity with per-field invalidation semantics, §5.3 dual cursors + convergence rules, §5.4 storage schema; the P0 findings doc for the pinned lane evidence; `docs/contracts/canary-telemetry-v1.md`'s `storage_schema` field (the canary example value must stay consistent — Task 4 checks).

**File ownership:** Create: `docs/contracts/vectors-v1.md`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** The frozen `<workspace>/.miller/vectors.db` artifact contract: file placement and env opt-outs (`MILLER_SEMANTIC=off` zero-work guarantee); the five generation-identity fields with an explicit **invalidation matrix** (field → what changing it invalidates → mechanism: shadow rebuild / targeted re-embed / reader gate / nothing); dual desired-state cursors with the chunk cursor's content.db-current precondition and per-cursor last-error; escalation-to-shadow triggers list; meta/mapping/vec0 table shapes (`symbol_vectors`, `chunk_vectors` as `int8[512]` cosine for the pinned lane, parameterized by `storage_schema`; integer rowids + mapping tables; `path`/`kind`/`is_test` metadata columns; the prefiltered manual-distance rule for glob scoping with oversampling documented as approximate); writer discipline (only the writer-lock holder mutates; any reader embeds queries against a compatible `encoder_fingerprint`); cross-workspace read/degrade rule; shadow generation promote/rollback/GC lifecycle; corruption recovery (per-generation delete + rebuild, never touching symbols.db); the compact status vocabulary from §5.1 verbatim (`ready | ready (updating; N files pending) | building 42% (not queryable) | downloading | unavailable (reason) | incompatible | circuit-open | disk-blocked | disabled`); and the initial pinned values: `encoder_fingerprint` composition for both pinned models, `storage_schema=vec0-int8-512-cosine-v1` (fallback `vec0-int8-384-cosine-v1`), `corpus_generation=cards-v1-chunks-v1` starting value, quantization order slice→renormalize→quantize.

**Approach:** DDL shapes are contract-level (column names, types, constraints, vec0 declarations) — exact enough that two independent implementations produce compatible artifacts, without freezing incidental SQLite details. Cite `FullRebuildPromotion` for the promote pattern and `SidecarCorruptionRecovery` for recovery registration (verify both symbol names with Miller before citing). Same frozen-contract voice and pre-ship amendment convention as Task 1.

**Acceptance criteria:**
- [x] Five-field generation identity with a complete invalidation matrix; `fusion_profile` explicitly never invalidates stored vectors
- [x] Dual-cursor rules include the chunk cursor's content.db precondition and atomic cursor-advance-with-batch rule
- [x] Storage schema section fully parameterized by `storage_schema` with both pinned lane strings recorded
- [x] Cited Miller symbols verified against the index (report the Miller calls)
- [x] Worker-scope verification passes (documented consistency pass listed in the report) and the change is handed to the lead per commit mode

## Task 3: Conformance fixtures + golden vectors — `eval/sidecar-conformance/`

**Files:**
- Create: `eval/sidecar-conformance/corpus.jsonl`
- Create: `eval/sidecar-conformance/generate.py`
- Create: `eval/sidecar-conformance/golden-qwen3-0.6b-f16.jsonl`
- Create: `eval/sidecar-conformance/golden-bge-small-f32.jsonl`
- Create: `eval/sidecar-conformance/README.md`
- Create: `eval/sidecar-conformance/.gitignore` (only if a scratch dir is needed)

**Interfaces:**
- Consumes: Global Constraints (model pins, knobs, tolerance policy, no-new-downloads rule); `eval/model-bench/.cache/` layout (llama.cpp binary under `.cache/llama/`, GGUFs under `.cache/dist/` — verify actual paths before coding); `eval/model-bench/bench.py`'s `LlamaServer` class + `_fit`/instruction plumbing as the reference for correct embedding invocation (import or adapt — do not reinvent the flags).
- Produces: the fixture set the protocol contract's `## Conformance` section binds to. Golden JSONL row shape: `{"text_id", "role": "query"|"document", "model", "native_dims", "vector_native": [...], "vector_512d_int8"| "vector_384d_int8": [...], "norm", "generator": {"llama_cpp": "b10068", "backend": "cpu", "pooling", "instruction_applied"}}` (exact field names are Task 3's to finalize — document them in the README; Tasks 1–2 cite the directory, not row internals).

**Contract inputs:** Global Constraints verbatim; `eval/model-bench/bench-pins.json` (single source for model identities); tolerance policy (dims exact, norm ±1e-3, cosine ≥0.999).

**File ownership:** Create: `eval/sidecar-conformance/**` (corpus.jsonl, generate.py, golden-*.jsonl, README.md, .gitignore)

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** A committed, regenerable conformance fixture set: (a) `corpus.jsonl` — 30–40 texts covering the edge cases a sidecar must survive: plain ASCII code identifiers, natural-language prose, markdown with fences, CJK text, emoji/astral-plane unicode, a text exceeding the 512-token bge budget (exercises truncation), single-character and whitespace-only strings, a 250-item batch-shaped group marker (for batch semantics), and both roles (query vs document — role determines which instruction template applies); (b) `generate.py` — embeds every text with both pinned models on the **CPU backend** via the cached llama.cpp server, applies the exact knobs from bench-pins.json (reuse model-bench's server/instruction code rather than duplicating flag knowledge), emits native-dims goldens plus the sliced+renormalized+int8 lane vectors in the frozen order, and a `--verify` mode that regenerates and asserts the tolerance policy against the committed goldens; (c) `README.md` — regeneration command, tolerance policy restated, why CPU goldens (backend variance), and the consumer contract (a sidecar implementation passes conformance iff every corpus text meets tolerance under its own runtime).

**Approach:** Keep committed goldens compact: round floats to 6 decimals (well inside the 0.999-cosine bar — state this in the README) and target < 2 MB total; if native-1024d goldens for 40 texts exceed that, keep native goldens for a documented 12-text core subset and lane-sliced goldens for all texts. Empty-string handling: record the reference implementation's actual behavior (embed or error) as the golden fact rather than assuming — check `runtime.py`. Fixture generation must be deterministic given the cache (fixed corpus order, fixed server settings, temperature-free embedding).

**Acceptance criteria:**
- [x] `generate.py --verify` green from the existing cache: regenerates both models on CPU and every text passes the tolerance policy against committed goldens
- [x] Corpus covers all listed edge-case classes; each row labels its class and role
- [x] Committed fixture payload < 2 MB and contains no model weights or binaries
- [x] README documents regeneration, tolerance, rounding, and the pass/fail rule for implementations
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode

## Task 4: Docs map + cross-contract consistency pass

**Files:**
- Modify: `docs/README.md` (contracts section — add both new contracts + the conformance fixture dir with one-line hooks)
- Modify (cross-references only): `docs/contracts/semantic-sidecar-protocol-v1.md`, `docs/contracts/vectors-v1.md`, `eval/sidecar-conformance/README.md`

**Interfaces:**
- Consumes: all three Batch A outputs.
- Produces: the P1 exit state — contracts discoverable from the docs map, mutually consistent, and consistent with the already-shipped canary contract.

**Contract inputs:** `docs/README.md` current map structure; `docs/contracts/canary-telemetry-v1.md` (its `storage_schema` example value `vec0-int8-256-cosine-v1` predates the pin — verify whether the canary text presents it as an example or a pin; if example, leave it and note nothing; if it reads as normative, flag to the lead — do NOT edit the canary contract in this task).

**File ownership:** Modify: `docs/README.md`; may add cross-reference lines (not content changes) to the three files owned by Tasks 1–3

**Serialization required:** Yes

**Dependency reason:** Reads all three prior outputs; owns the shared `docs/README.md` file no Batch A task may touch.

**What to build:** Add both contracts and the conformance directory to the docs map's active-contracts section. Then a recorded consistency pass across the four semantic documents (two new contracts, canary contract, design doc): every shared literal (model ids, sha sources, dims, lane strings, tolerance numbers, prompt templates, schema literals) appears with identical values everywhere it appears; each contract cross-links the other and the fixtures; discrepancies are fixed in the file that is wrong (within ownership) or reported to the lead (canary/design edits are lead-owned).

**Approach:** Grep-driven: enumerate the shared literals from Global Constraints, grep each across the four documents, reconcile. Keep docs map hooks one line each per the map's existing style.

**Acceptance criteria:**
- [x] `docs/README.md` lists both contracts and the fixture set in the active section
- [x] Recorded literal-by-literal consistency table in the report; zero unreconciled mismatches within owned files; lead-owned mismatches (if any) explicitly reported
- [x] Worker-scope verification passes (the consistency table) and the worker commits (serial-worker-commit)
