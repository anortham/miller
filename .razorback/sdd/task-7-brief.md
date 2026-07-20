### Task 7: Model benchmark harness + benchmark run → pin recommendation

**Files:**
- Create: `eval/model-bench/` — `bench-pins.json` (llama.cpp prebuilt release + candidate GGUF URLs, all sha256-pinned), `run-bench.sh` (download → verify → embed corpus+queries per candidate → emit results JSONL per arm → invoke retrieval-eval scorer), `eval/model-bench/README.md`, `docs/findings/2026-07-19-model-benchmark.md`
- Modify: `eval/retrieval-eval/README.md` (integration note)

**Interfaces:**
- Consumes: Task 6 harness CLI + JSONL schemas; dev golden set.
- Produces: the design's P0 model gate: `docs/findings/2026-07-19-model-benchmark.md` with the pin recommendation (model + dims + quantization lane) and its evidence. P1's `semantic-pins.json` and the sidecar's model manifest consume this.

**Contract inputs:** Design §2.4 + §4.1: candidates = Qwen3-Embedding-0.6B (pooling `last`, `<|endoftext|>` append, instruction prefixes, MRL 256/512/1024 slice→renormalize) vs Apache/MIT fallback tier (bge-small-en-v1.5 384d pooling `cls`; snowflake-arctic-embed-s if GGUF availability confirms — verify availability, do not assume). Correctness gotchas from the runtime research: wrong pooling silently degrades — the harness must include a sanity check (self-similarity of a known-similar pair must beat a known-dissimilar pair by margin) per candidate before scoring.

**File ownership:** Create: `eval/model-bench/**`, `docs/findings/2026-07-19-model-benchmark.md`; Modify: `eval/retrieval-eval/README.md` (integration note)

**Serialization required:** Yes

**Dependency reason:** Consumes the Task 6 harness CLI contract and dev golden set to score candidates.

**What to build:** A scripted, reproducible benchmark: download pinned upstream llama.cpp prebuilt (macos-arm64 for the local run) + pinned candidate GGUFs; build the corpus (symbol cards generated from the dev-set repos using the design's v1 card template — a small generator script reading Miller's `symbols.db` via sqlite; this generator is throwaway bench tooling, not product code); embed corpus + queries per candidate/dims-lane with correct per-model flags; produce ranked results per query (cosine over the embedded corpus — brute force in the script); score via Task 6 harness; emit the comparison table. Then RUN it locally (macos-arm64) for all candidate/lane combinations and write the findings doc: metrics per candidate/lane (macro + worst-language + identifier non-inferiority vs a BM25 baseline arm produced from Miller's actual `search mode=symbol` output for the same queries), model load + embed throughput observations (report-only), and the pin recommendation with rationale.

**Approach:** Downloads total ~1–2GB (models + llama.cpp) — proceed; they are cached under `eval/model-bench/.cache/` (gitignored). If a candidate's GGUF or license claim fails verification at download time, record it in the findings and drop the candidate rather than substituting an unpinned source. If the local hardware cannot complete all lanes in reasonable time, complete Qwen3 lanes + one fallback fully and record which lanes remain, with the exact command to run them. **Pin decision rule (evidence-gated):** the default pin may name Qwen3 only from completed Qwen3 lanes, and a fallback pin may only be named from a completed fallback lane — the fallback tier is the license-safe escape hatch, so it must be evidence-backed in P0, never inferred. If no fallback lane completes, the findings doc says so explicitly and the fallback pin is recorded as OPEN, not defaulted.

**Acceptance criteria:**
- [ ] All artifacts sha256-pinned; cache gitignored; re-run reproducible from clean cache
- [ ] Per-candidate pooling sanity check passes before scoring (guards the silent-garbage failure mode)
- [ ] Benchmark run completed locally; findings doc contains per-candidate/lane metrics vs BM25 baseline, identifier non-inferiority table, and an explicit pin recommendation (model + dims + quantization)
- [ ] Worker-scope verification passes (harness unit checks + successful end-to-end run); worker commits (serial-worker-commit)

---

## Post-plan notes for the lead

- Batch A merges in task order 1→6. The `docs/README.md` line for the canary contract is applied by the lead exactly once, during **Task 1's** commit (Task 1 owns `docs/README.md`; Task 5 only hands the line text to the lead — Task 5's commit must not touch that file).
- After Batch A + Task 7: run branch gate (`dotnet build Miller.slnx -c Release`, `scripts/test.sh all`), then goldfish checkpoint, then report P0 complete with the two gate verdicts (spike, model pins) and CI-pending evidence. P1 planning is a new writing-plans invocation against the design doc.
- User-owned follow-up (not a task): sealing the acceptance set per `SEALED-SET-PROTOCOL.md`, and reviewing the pin recommendation before P1 freezes it.
