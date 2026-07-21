# Task 1 report — CodeRankEmbed feasibility spike

**Status: DROP — drop reason `converter` (Stage 2).**
**One-line:** Pinned llama.cpp `b10068` `convert_hf_to_gguf.py` recognizes `NomicBertModel` but crashes with `KeyError: ['num_local_experts','num_experts']` at `conversion/bert.py:372` for this non-MoE model — no f16 GGUF produced, so the sanity and parity gates never run. No overlay written.

> Note to lead: this file previously held a stale report titled "Encoder pin registry + `MILLER_SEMANTIC_MODEL` swap seam" from an earlier plan iteration (timestamped with the 12:07 worktree reset). It did not match `task-1-brief.md` (the CodeRankEmbed spike). Overwritten with the correct Task 1 report per your instruction to write here.

Worktree: `/Users/murphy/source/miller/.claude/worktrees/fusion-v2-eval`, branch `worktree-fusion-v2-eval`, HEAD `067c1f7`.

---

## Stages executed

### Stage 1 — download + license — PASS
- `hf download nomic-ai/CodeRankEmbed --revision 3c4b60807d71f79b43f3c4363786d9493691f8b1 --local-dir .cache/hf/CodeRankEmbed`
- **HF revision pinned:** `3c4b60807d71f79b43f3c4363786d9493691f8b1`
- **License: MIT** — `cardData.license = mit` AND repo tag `license:mit` (from `https://huggingface.co/api/models/nomic-ai/CodeRankEmbed`). This clears the stale `bench-pins.json` `rejected_candidates` note ("community GGUF with unclear license"): the **base model** is unambiguously MIT. The objection only ever applied to third-party GGUF repackagings, not to a first-party conversion of the MIT weights.
- Config confirms the T1 contract: `architectures=["NomicBertModel"]`, `model_type=nomic_bert`, `n_embd=768`, `1_Pooling/config.json → pooling_mode_cls_token=true` (CLS pooling), `n_positions=8192`, `max_trained_positions=2048`, `activation_function=swiglu`, non-MoE (no `moe_every_n_layers`).
- Pinned llama.cpp `b10068` **binary** archive downloaded and **sha256 verified** against `bench-pins.json` (`13aa2d40c76ad1dcb8ebeec5f0d2814bf3b2f84a66935c7d4dc6f7cca8e38d68` — exact match). Source tarball for tag `b10068` downloaded to obtain `convert_hf_to_gguf.py` (not shipped in the prebuilt binary archive).
- Converter venv built from the pin's own `requirements/requirements-convert_hf_to_gguf.txt` (torch 2.11.0, transformers 4.57.6, gguf, numpy 1.26.4, sentencepiece) via `uv pip install --index-strategy unsafe-best-match`.

### Stage 2 — convert (`convert_hf_to_gguf.py --outtype f16`) — **FAIL → HARD STOP**
Command:
```
python .cache/src/llama.cpp-b10068/convert_hf_to_gguf.py \
  .cache/hf/CodeRankEmbed --outtype f16 \
  --outfile .cache/gguf/coderankembed-f16.gguf
```
Output tail (evidence — `.cache/gguf/convert.log`):
```
INFO:hf-to-gguf:Model architecture: NomicBertModel      <- architecture IS registered/recognized
INFO:hf-to-gguf:Exporting model...
  File ".../conversion/bert.py", line 372, in modify_tensors
    n_experts = self.find_hparam(["num_local_experts", "num_experts"])
  File ".../conversion/base.py", line 198, in find_hparam
    raise KeyError(f"could not find any of: {keys}")
KeyError: "could not find any of: ['num_local_experts', 'num_experts']"
```
No `.cache/gguf/*.gguf` produced. Exit code 1.

**Root cause (pinned-source-verified, `conversion/bert.py:371-388`):** `NomicBertModel.modify_tensors` calls `find_hparam(["num_local_experts","num_experts"])` **unconditionally** at the top of the method. That value is only consumed inside the MoE tensor branches (`"mlp.experts.mlp.w1/w2" in name`). CodeRankEmbed is non-MoE (`self.is_moe = bool(hparams.get("moe_every_n_layers"))` → `False`), so no experts hparam exists and the lookup raises `KeyError` on the **first** tensor. The lookup should be `optional=True` or moved inside the MoE branch. This is a converter bug in the pinned `b10068` build that trips **every** non-MoE `NomicBertModel` (nomic-embed-text-v1 class), not a CodeRankEmbed-specific gap.

### Stage 3 — sanity gate — NOT RUN (blocked by Stage 2, no GGUF).
### Stage 4 — parity gate — NOT RUN (blocked by Stage 2, no GGUF).

The sanity/parity harness was fully staged before the stop and is ready if the converter is unblocked: `.cache/parity/` holds `texts.json` (20 non-empty conformance rows: 12 documents, 8 queries), `parity_llama.py` (reuses `bench.py`'s `LlamaServer`, CLS pooling, `--embd-normalize 2`), `parity_ref.py` (`SentenceTransformer(..., trust_remote_code=True)`, `normalize_embeddings=True`, query prefix applied to `role=query` rows only), and `compare.py` (per-row cosine, min/mean, 0.99 gate). A reference venv (`sentence-transformers` + `einops`) is also built. None of this executed because there is no f16 GGUF to embed with.

## What each gate would have proven (unreached)
- **sanity** ⟹ CLS pooling is wired correctly in the GGUF (anchor↔similar minus anchor↔dissimilar ≥ 0.10 margin, dims == 768).
- **parity** ⟹ conversion preserved the reference model's geometry (min cosine ≥ 0.99 between llama-server f16 and sentence-transformers fp32 over the 20 conformance texts).

## Decision
Per the T1 contract, converter failure is an enumerated HARD STOP and the budget is one session with "any ambiguity → drop with reason, do not extend." Patching the pinned converter to get past line 372 would (a) violate "do not extend" and (b) make any resulting overlay dishonest — it would claim conversion by the pinned `b10068` release when the model in fact required a source patch. So: **no `bench-pins.local.json` overlay written; `eval/model-bench/.gitignore` left unchanged** (nothing machine-specific was produced to ignore; `.cache/` is already gitignored).

## Remediation options for the plan (not acted on — needs a pin/approval decision)
- Bump the benchmark's llama.cpp pin to a `b#####` build where `bert.py`'s MoE-hparam lookup is guarded (`optional=True` / moved into the MoE branch), then re-run this spike end to end. This is the clean path — CodeRankEmbed is a standard non-MoE NomicBert, so once the converter bug is gone conversion should be uneventful.
- Or track the one-line upstream fix explicitly as a pinned patch (documented, sha-recorded) if a converter bump is undesirable — heavier process, same result.
- License is **not** a blocker (clean MIT, verified above); the only blocker is the converter build.

## Miller MCP calls used
None. The brief pre-named every essential file (`bench-pins.json`, `bench.py`, `README.md`, `run-bench.sh`, corpus) and they are small, so direct reads located them without a search. Given CAVEAT 2 (shared MCP connection; abandon on >60s hang) and that no discovery was needed, direct file reads were the lower-risk path. Reported honestly rather than manufacturing a search call.

## API-shape evidence (`bench-pins.json` `candidates[]` shape, copied exactly)
Candidate objects use these keys: `id`, `tier`, `model`, `hf_repo`, `hf_repo_owner`, `license`, `license_verified_from`, `file`, `url`, `sha256`, `size_bytes`, `native_dims`, `pooling`, `mrl`, `mrl_lanes`, `instruction_aware`, `query_instruction`, `document_instruction`, `context_length`. The pooling gate reads `pooling`, `native_dims`, `context_length`; the query-prefix field consumed by `bench.py` (`prep_query`) is **`query_instruction`**, not `query_prefix`. The T1 brief names the overlay field `query_prefix`; had the gates passed, the overlay would have carried **both** (`query_prefix` for the T1 contract and `query_instruction` = same value for `bench.py` compatibility), plus the recorded `hf_revision` and `converter_command`. Recorded here so Task 4 inherits the exact shape decision even though no overlay was emitted.

## Judgment calls
- `conversion/bert.py:372` (pinned b10068): classified the failure as drop reason **`converter`** — the architecture is registered (`Model architecture: NomicBertModel` logged) but the pinned converter cannot emit a GGUF for this non-MoE config. Same terminal stage the brief labels "converter"; the specific mechanism (MoE-hparam lookup bug) is narrower than "unsupported architecture" and is reported as such.
- Did **not** patch the pinned converter to continue to Stages 3–4 — see Decision above (pin fidelity + "do not extend").
- Parity text selection (`.cache/parity/texts.json`): first 20 non-empty `text` rows of `eval/sidecar-conformance/corpus.jsonl` (12 documents / 8 queries), query prefix applied to `role=query` only, matching CodeRankEmbed's queries-only prefix rule.

---

# Retry round (lead-sanctioned pin-bump attempt) — FINAL DROP stands

**Status: DROP — drop reason `converter` (both reasons: MIT-clean license, converter unfixed in all releases). No pin bump, no overlay, no converter patch.**

Lead sanctioned one bounded round under remediation option 1: bump the bench `llama_cpp` pin to the earliest released tag > b10068 where `conversion/bert.py`'s MoE-hparam lookup is guarded, then re-run Stages 2–4. Term 2 was explicit: if NO released build contains the fix, STOP with final DROP.

## Finding: no released build fixes the lookup — the bug is live on master today

- **The offending line is byte-identical from b10068 through the current newest release.** `conversion/bert.py:372` reads `n_experts = self.find_hparam(["num_local_experts", "num_experts"])` — unconditional, only consumed by the MoE tensor branches — on the pinned b10068 source AND on `master` HEAD (fetched raw, cache-busted). Newest release at check time: **`b10076`** (published `2026-07-21T15:52:50Z`), i.e. *after* b10068; it snapshots the same unfixed master.
- **The file has not been touched since before b10068.** GitHub API (`/repos/ggml-org/llama.cpp/commits?path=conversion/bert.py`) shows the last three commits to that file are `bfb4308b` (2026-06-02, granite embeddings R2), `d4c8e2c2` (2026-05-31, jina-v2-zh tokenizer), `cc7200bf` (2026-05-15, "Refactor: convert_hf_to_gguf.py"). None post-dates b10068; none guards the experts lookup. Releases are build-numbered snapshots of master, so every tag in `(b10068, b10076]` carries the identical unguarded line.
- **The crash is architecture-wide, not CodeRankEmbed-specific.** `nomic-ai/nomic-embed-text-v1`'s `config.json` (the canonical non-MoE NomicBert) also has **no** `num_experts`/`num_local_experts` (verified live); only `nomic-embed-text-v2-moe` carries them (`num_experts: 8`, `moe_every_n_layers: 2`). Probing CodeRankEmbed's hparams with the **pinned loader itself** (`ModelBase.load_hparams`, read-only, no patch) returns zero moe/expert keys and `is_moe=False`. So `modify_tensors`'s first line KeyErrors on any non-MoE NomicBert. The fix is a one-liner upstream (`optional=True` / move into the MoE branch) but it has not shipped in any release.

## Decision
Term 2's STOP condition is met: no released build > b10068 contains the fix, and patching converter source is out of bounds (would produce a dishonest overlay claiming conversion by a pinned release). **Final DROP stands.** Stages 3 (sanity) and 4 (parity) remain unreached; the `.cache/parity/` harness is still staged for the day a fixed llama.cpp release exists — at which point this spike re-runs unchanged with only the `llama_cpp` pin swapped.

## File changes this round
- **`eval/model-bench/bench-pins.json`** — `llama_cpp`/`runtime` entry **left unchanged** (no fixed release to point at). Updated the `rejected_candidates` CodeRankEmbed note (lead-sanctioned): corrected the stale "unclear license" reason to record MIT-clean license at revision `3c4b6080…` and the true converter blocker with the `bert.py:372` mechanism and the b10068→b10076 release span. JSON re-validated.
- **No `bench-pins.local.json` overlay** (fail path). **`.gitignore` unchanged.** No `git add`/`commit`.

## Retry judgment calls
- Did not bump the `llama_cpp` pin: the earliest-fixed-release search terminates with "no fixed release exists" because master itself is unfixed — an existence proof, no per-tag enumeration needed.
- Applied the `rejected_candidates` correction even though the round STOPped before a pin bump: the lead sanctioned it independently and it is evidence-backed. Kept CodeRankEmbed in `rejected_candidates` (still rejected) but replaced the false reason with the accurate one rather than deleting the entry.
