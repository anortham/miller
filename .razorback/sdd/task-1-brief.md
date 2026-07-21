### Task 1: CodeRankEmbed feasibility spike

**Files:**
- Create: `eval/model-bench/bench-pins.local.json` (local overlay, add to `eval/model-bench/.gitignore`)
- Test: n/a (spike; evidence is the parity report pasted into the task result)

**Interfaces:**
- Consumes: pinned llama.cpp from `eval/model-bench/bench-pins.json` (`llama_cpp` entry: release tag + sha256); HF model `nomic-ai/CodeRankEmbed` (pin the exact revision hash you fetch).
- Produces: on pass — overlay entry with the same candidate shape as `bench-pins.json` `candidates[]` (id `coderankembed-f16`, local file path, sha256, pooling `cls`, dims 768, `query_prefix: "Represent this query for searching relevant code: "`), consumed by Task 4. On fail — a written drop reason for the findings doc.

**Contract inputs:** spec T1 stop conditions; MIT license verification; sentence-transformers parity gate cosine ≥ 0.99 vs HF reference on the sidecar conformance texts (`eval/sidecar-conformance/corpus.jsonl` non-empty `text` rows).

**File ownership:** Create: `eval/model-bench/bench-pins.local.json` (gitignored), scratch under bench `.cache/`; Modify: `eval/model-bench/.gitignore`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Attempt GGUF conversion of CodeRankEmbed with the pinned llama.cpp converter; validate pooling + embedding fidelity; produce an overlay pin or a drop reason.

**Approach:** Stage order with hard stops: (1) `huggingface-cli download` at a pinned revision, record license file; (2) `convert_hf_to_gguf.py` — NomicBert-long architecture unsupported by the pinned converter ⟹ STOP (drop reason: converter); (3) f16 GGUF through `bench.py sanity`; (4) parity: embed ~20 conformance texts via llama-server AND via `sentence-transformers` (uv/pip ephemeral env, `trust_remote_code=True`), report min cosine — < 0.99 ⟹ STOP (drop reason: fidelity). Remember the query prefix applies to queries only, not documents. Budget: one session; any ambiguity → drop with reason, do not extend.

**Acceptance criteria:**
- [ ] Either an overlay pin with recorded revision/converter/sha256/pooling/prefix AND min-cosine ≥ 0.99 evidence, or a written drop reason naming the failed stage.
- [ ] Nothing machine-specific committed; `bench-pins.local.json` gitignored.

