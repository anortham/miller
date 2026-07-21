### Task 2: Corpus freeze, exclusions, findings skeleton

**Files:**
- Modify: `eval/model-bench/build_corpus.py` (extend `GOLDEN_SET_EXCLUSIONS` mechanism with a benchmark-derived-docs list)
- Create: `docs/findings/2026-07-21-fused-arm-encoder-benchmark.md` (skeleton with pre-registered gates)

**Interfaces:**
- Consumes: dev manifest repo list (`eval/retrieval-eval/sets/dev/manifest.json`); current local HEADs of miller + julie.
- Produces: frozen clean worktrees at `<scratchpad>/frozen-miller` and `<scratchpad>/frozen-julie` (SHAs recorded in the findings doc); `build_corpus.py` exclusion list `BENCHMARK_DOC_EXCLUSIONS` covering `docs/plans/2026-07-21-encoder-comparison-fusion-v2-design.md`, `docs/plans/2026-07-19-miller-semantic-integration-design.md`, `docs/findings/2026-07-19-model-benchmark.md`, `docs/findings/2026-07-21-fused-arm-encoder-benchmark.md`, `docs/findings/2026-07-07-dead-code-candidates-dogfood.md` plus any doc whose text names a graded `doc_id` (grep the dev set's doc_ids against docs/ at the frozen SHA and list what hits); Task 4 and Task 6 consume the worktrees.
- Findings skeleton contains the T5 gates verbatim from the spec (numbers, not prose) under "Pre-registered gates", dated before any scoring.

**Contract inputs:** spec R1; frozen SHAs = current local `main` HEAD of each repo at freeze time (miller includes this branch's base 59c2c79; record exact SHAs). `git worktree add --detach <path> <sha>` from each repo. Each frozen worktree needs `.miller/symbols.db` + `content.db` built: run the worktree-built `miller` with `workspace open` + full index against that root (no semantic env needed), record artifact ids.

**File ownership:** Modify: `eval/model-bench/build_corpus.py`; Create: `docs/findings/2026-07-21-fused-arm-encoder-benchmark.md`, frozen worktrees outside repo (scratch)

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** The frozen evaluation substrate and the findings doc that pre-registers every decision gate before numbers exist.

**Approach:** Run `dotnet run --project eval/retrieval-eval -- validate --queries eval/retrieval-eval/sets/dev/queries.jsonl --corpus miller=<frozen-miller> --corpus julie=<frozen-julie>` and require exit 0 — this proves every graded doc exists at the frozen SHAs. Exclusion list keyed on repo-relative path prefix, same mechanism as `GOLDEN_SET_EXCLUSIONS` (build_corpus.py:44-46).

**Acceptance criteria:**
- [ ] Frozen worktrees exist, SHAs + index artifact ids recorded in the findings skeleton.
- [ ] `validate` exits 0 against both frozen corpora.
- [ ] Findings skeleton contains the pre-registered T5 gates verbatim and the R1 within-run-only comparability note.
- [ ] `build_corpus.py` excludes the benchmark-derived docs; a one-line proof (corpus row count without/with exclusions) recorded.

