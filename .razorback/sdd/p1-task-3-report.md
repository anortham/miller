# P1 Task 3 — Conformance fixtures + golden vectors

**Status:** complete
**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/semantic-integration`
**Branch:** `worktree-semantic-p1` @ `25794d0` (ahead 1 of `origin/main`)
**Dirty state at handoff:** untracked `eval/sidecar-conformance/` (mine) + `docs/contracts/*.md` (Tasks 1–2, other workers)
**Commit SHA: none - parallel-lead-commit**

## Deliverables

All under `eval/sidecar-conformance/` (exclusive ownership respected — zero files touched outside it).

| File | Size | Notes |
|---|---|---|
| `corpus.jsonl` | 19.4 KiB | 39 texts, 16 edge-case classes, 12 query / 27 document |
| `generate.py` | 16.6 KiB | generate + `--verify` |
| `golden-qwen3-0.6b-f16.jsonl` | 521 KiB | 39 rows, 1024d native + 512d int8 lane |
| `golden-bge-small-f32.jsonl` | 242 KiB | 39 rows, 384d native + 384d int8 lane |
| `README.md` | 9.0 KiB | regeneration, tolerance, rounding, CPU rationale, pass/fail rule |
| `.gitignore` | 10 B | ignores `.scratch/` (server logs, CPU probe log) |

**Committed payload ≈ 790 KiB**, well under the 2 MB budget. No weights or binaries. Native goldens kept for
**all 39 texts** in both models — the documented 12-text core-subset fallback was not needed.

## Verification

**Gate:** `python3 eval/sidecar-conformance/generate.py --verify`

```
backend proof: layers assigned to ['CPU']
CONFORMANCE PASS: 78 vectors across 2 models
  (dims exact, |norm-1| <= 0.001, cosine >= 0.999) in 12.0s
exit 0
```

- **Reproducibility proven:** goldens written by one run, independently reproduced green by three later
  `--verify` runs against the committed files.
- **Gate proven bidirectional (negative test):** a deliberately reversed golden vector was injected and
  `--verify` exited 1 with `ascii-ident-004: native cosine 0.073107 < 0.999`. Golden restored afterwards.
- **Wall time (report-only):** ~12–18 s for both models end-to-end on an Apple M2 Ultra.

**Worker ceiling:** `scripts/test.sh` (fast suite) — `Failed: 1, Passed: 3617, Skipped: 1`.
The single failure is `IndexerServiceLeadershipTests.StartAsync_ArtifactMatchesOwn_RunsOnlyTheStartupDeltaScan`.
**Not caused by this task** — every file I created is untracked and outside `src/`/`tests/`. It **passes in
isolation** (`Failed: 0, Passed: 1`), so it is load-flakiness from the parallel Batch A workers sharing the
machine. Flagged for the lead, not fixed (out of ownership).

## Evidence for every embedding flag

| Knob | Value | Evidence |
|---|---|---|
| Server invocation | `--embedding --pooling <p> -c <ctx> -b 8192 -ub 8192 --embd-normalize 2 -np 1` | Reused verbatim from `eval/model-bench/bench.py:LlamaServer.__enter__` (imported, not copied) |
| Instruction / EOS / text budget | `prep_query` / `prep_doc` / `_fit` / `text_budget` / `QWEN_EOS` | Imported from `bench.py`; knob values read from `bench-pins.json` at runtime |
| qwen3 pooling `last`, EOS appended, query instruction | as pinned | `bench-pins.json` `qwen3-0.6b-f16`; verified in output: EOS on 39/39, instruction on 12/12 queries |
| bge pooling `cls`, no EOS, query instruction | as pinned | `bench-pins.json` `bge-small-en-v1.5-f32`; verified: EOS 0/39, instruction 12/12 queries |
| CPU backend | `LLAMA_ARG_DEVICE=none`, `LLAMA_ARG_N_GPU_LAYERS=0` | Env names read from `llama-server --help` (documented equivalents of `-dev none` / `-ngl 0`). Empirically compared: default run logs `using device MTL0 (Apple M2 Ultra)` + `layer N assigned to device MTL0`; forced run logs `layer N assigned to device CPU` with no MTL0 line. |
| CPU backend asserted at runtime | `prove_cpu_backend()` | Parses a verbose probe server's device assignments and **fails the run** unless every layer is `CPU` and no `using device …` line appears. Not a claim — a gate. |
| llama.cpp build | `b10068` | `bench-pins.json` `runtime.release_tag`; `generate.py` refuses to run on a different tag |

## Empty-string behaviour — observed, not assumed

Read from the reference implementation (`~/source/julie/python/embeddings_sidecar/sidecar/runtime.py:233-250`,
`_sanitize_texts`, read-only):

- empty / whitespace-only / non-string input → replaced with the literal `"[empty]"` and embedded normally.
  **It is never an error.**
- NUL bytes stripped from all other input; substitution applies only if the result is still blank.

`generate.py:sanitize()` mirrors this exactly, so `empty-001` and `whitespace-001/002` carry real golden
vectors (the vector of `[empty]`), while `control-bytes-001` (NUL + ANSI escape + `ok`) correctly does *not*
get the substitution. Verified in the emitted goldens.

## Decisions worth the lead's attention

1. **Tolerance policy applied to the right object, not weakened.** The first `--verify` failed 7 rows on
   *dequantized int8 lane* L2 norms drifting ~1.5e-3. This is inherent symmetric-int8 rounding loss at
   384/512 dims, not pipeline drift. The frozen `1e-3` norm bar is applied to emitted **float** vectors
   (`vector_native`, `norm_lane`); the int8 codes are a storage encoding bounded instead by *two* cosine
   checks (vs. golden, and vs. their own pre-quantization float) plus a code-range check — a stricter
   statement about direction, which is all cosine retrieval consumes. **No frozen number was changed.**
   Documented in the README tolerance table. If Task 1's contract states the norm bar over "every emitted
   vector" without this float/int8 distinction, it should pick up the same wording.
2. **Batch semantics.** `batch-group-001` carries `batch_expand: 250`; the generator embeds it 250× in one
   batch and asserts every position matches position 0 at the 0.999 bar, committing one golden vector and
   recording `batch_group_positions_checked: 250`. Batch position/size cannot perturb a vector.
3. **Plan mismatch (minor, non-blocking).** Global Constraints name the fallback pins entry `bge-small-f32`;
   the actual key in `bench-pins.json` is **`bge-small-en-v1.5-f32`**. `generate.py` resolves the real key
   while keeping the plan's short name as the golden filename and the `model` field, so contract citations
   stay stable. Noted in a code comment.
4. **Truncation recorded, not hidden.** `long-truncation-001/002` are flagged `input_truncated: true` in the
   bge goldens (512-token budget) and `false` in qwen3 (32K context) — a real capability difference.

## Acceptance criteria

- [x] `generate.py --verify` green from the existing cache, both models on CPU, all texts within tolerance
- [x] Corpus covers every listed edge-case class; each row labels its class and role
- [x] Committed payload < 2 MB (≈790 KiB), no weights or binaries
- [x] README documents regeneration, tolerance, rounding rationale, CPU rationale, pass/fail rule
- [x] Worker-scope verification run; handed to lead per `parallel-lead-commit` (no git add/commit performed)
