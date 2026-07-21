# Encoder Comparison + fusion-v2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Score all encoder candidates under production-faithful fused arms, select fusion-v2 constants from pre-registered profiles, decide the default encoder pin, and freeze the configuration for the user-run sealed acceptance event.

**Architecture:** Offline C# adapter (`eval/fusion-arm/`) calls `Miller.Core` fusion/policy directly over frozen-corpus arm dumps; `eval/model-bench` supplies semantic lanes; `eval/retrieval-eval` scores everything. Product change is confined to `RrfFusion` constants + profile id iff the pre-registered winner bar is met.

**Tech Stack:** .NET 10 console (eval-only, outside Miller.slnx), existing python bench (llama-server embedding), sqlite, bash.

**Architecture Quality:** Approved shape: new `eval/fusion-arm/` console referencing `Miller.Core` only (mirrors `eval/retrieval-eval`'s isolation); no new product modules; product diff limited to `src/Miller.Core/Search/RrfFusion.cs` constants + `src/Miller.Server/Tools/SearchTool.cs` chunk-mirror constants. Main risk: arm dumps diverging from production serving path — mitigated by the Task 3 parity smoke.

**Spec:** `docs/plans/2026-07-21-encoder-comparison-fusion-v2-design.md` (rev 2, codex-reviewed). The spec's R1–R5 ground rules and T5 pre-registered gates bind every task.

## Global Constraints

- **No pushes.** Everything stays local until the user calls the semantic plan complete.
- **Frozen corpus (R1):** all arms run against corpora built from clean worktrees at the SHAs recorded in the findings doc BEFORE any scoring; benchmark-derived docs excluded via `build_corpus.py`.
- **Sealed set untouched (spec §Non-goals):** nothing reads, references, or guesses sealed queries. Dev set only.
- **One fusion profile for all shippable encoders (R5).** No per-encoder weight tuning.
- **Pre-registered gates only (T5):** winner bar = beats fusion-v1 overall cluster-unit nDCG@10 with paired-bootstrap 95% CI excluding zero AND no regression > 0.02 nDCG on language macro-average, worst-language, docs_like view, or identifier diagnostic, for BOTH qwen3 and bge-small. Pin rule = bge-small takes the pin iff its fused overall nDCG is within 3% relative of qwen3 AND worst-language loss ≤ 0.02 absolute.
- **Profiles under sweep (T3):** global k ∈ {20, 60, 120} × Conceptual semantic:lexical ratio ∈ {1:1, 2:1, 3:1, 4:1} = 12, plus fusion-v1 control = 13. SymbolLookup and Mixed constants stay fusion-v1 everywhere.
- **Canary transition (T6):** fusion-v2 ships as a distinct commit; measurement window starts next UTC day; transition day excluded.
- Miller MCP caveat: this worktree and the main checkout are both indexed — always pass `scope=` or worktree-relative paths to disambiguate symbols.

## Verification Strategy

**Project source of truth:** CLAUDE.md §Testing (fast suite via `scripts/test.sh`, Scale opt-in via `scripts/test.sh scale`, Release build 0W/0E).

**Worker red/green scope:** per-task — eval tooling tasks verify by running the tool on a small fixture and asserting output (plus `dotnet test eval/retrieval-eval/tests/RetrievalEval.Tests.csproj` when scoring is touched); product task (Task 7) uses `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~RrfFusionTests"` and the named canary/contract test classes.

**Worker ceiling:** fast suite (`scripts/test.sh`). No worker runs the scale suite.

**Worker gate invariant:** each eval task proves its output artifact validates against its consumer's contract (retrieval-eval `validate`/`score` exit 0); Task 7 proves fused ranking constants changed without touching lexical goldens.

**Lead affected-change scope:** `scripts/test.sh` after each batch that touches product or test code.

**Branch gate:** `scripts/test.sh` + `dotnet build Miller.slnx -c Release` (0W/0E) + retrieval-eval tests. Scale suite NOT required — no indexing/extract path changes in this plan; escalation trigger below.

**Replay/metric evidence:** scorer reports are evidence artifacts. Hard gates = the T5 pre-registered numbers. Report-only = per-class per-query views, research-arm numbers, content-route smoke, negatives.

**Escalation triggers:** any change under `src/Miller.Indexing/` or to serving-path files beyond the two named in Task 7 → stop, report plan mismatch. Scale suite required only if that happens.

**Assigned verification failure:** workers stop and report; no gate edits without a plan revision.

**Verification ledger:** lead records invariant, command, scope, SHA, result, timestamp per task; embed-lane runs record corpus SHAs + artifact ids per R1.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: CodeRankEmbed spike | Batch A | Create: `eval/model-bench/bench-pins.local.json` (gitignored), scratch under bench `.cache/`; Modify: `eval/model-bench/.gitignore` | No | None - safe parallel batch. |
| Task 2: Corpus freeze + findings skeleton | Batch A | Modify: `eval/model-bench/build_corpus.py`; Create: `docs/findings/2026-07-21-fused-arm-encoder-benchmark.md`, frozen worktrees outside repo (scratch) | No | None - safe parallel batch. |
| Task 3: fusion-arm adapter | Batch A | Create: `eval/fusion-arm/**` (csproj, Program.cs, tests); Modify: none | No | None - safe parallel batch. |
| Task 4: Arm generation + scoring | None - serial | Create: bench `.cache/` artifacts, `eval/fusion-arm/out/` results (gitignored); Modify: none | Yes | Needs Task 2 frozen corpora + Task 3 adapter; Task 1's lane appended only if spike passed. |
| Task 5: Selection analysis | None - serial | Modify: `docs/findings/2026-07-21-fused-arm-encoder-benchmark.md` (sweep/selection sections) | Yes | Consumes Task 4 score reports. |
| Task 6: Real-artifact cost | Batch B (with Task 4) | Owns frozen-worktree `.miller/` state + `docs/findings/...` cost-table section (distinct section from Task 5) | No | Needs only Task 2's frozen worktree; independent of adapter/scoring. |
| Task 7: fusion-v2 product change | None - serial | Modify: `src/Miller.Core/Search/RrfFusion.cs`, `src/Miller.Server/Tools/SearchTool.cs:2374-2440`, `tests/Miller.Tests/Core/RrfFusionTests.cs`, canary test expectations, new contract tests in `tests/Miller.Tests/Indexing/` | Yes | Runs iff Task 5 meets the winner bar; consumes its constants. |
| Task 8: Freeze + sealed request | None - serial | Modify: findings doc (freeze record); Create: none | Yes | Consumes Tasks 5–7 outcomes. |

Commit mode: Batch A and Batch B tasks are `parallel-lead-commit`; serial tasks are `serial-worker-commit`.

---

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
- [x] Either an overlay pin with recorded revision/converter/sha256/pooling/prefix AND min-cosine ≥ 0.99 evidence, or a written drop reason naming the failed stage.
- [x] Nothing machine-specific committed; `bench-pins.local.json` gitignored.

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
- [x] Frozen worktrees exist, SHAs + index artifact ids recorded in the findings skeleton.
- [x] `validate` exits 0 against both frozen corpora.
- [x] Findings skeleton contains the pre-registered T5 gates verbatim and the R1 within-run-only comparability note.
- [x] `build_corpus.py` excludes the benchmark-derived docs; a one-line proof (corpus row count without/with exclusions) recorded.

### Task 3: `eval/fusion-arm` adapter

**Files:**
- Create: `eval/fusion-arm/FusionArm.csproj` (net10.0, references `src/Miller.Core/Miller.Core.csproj`, outside Miller.slnx), `eval/fusion-arm/Program.cs`, `eval/fusion-arm/README.md`, `eval/fusion-arm/tests/FusionArm.Tests.csproj` + one test file
- Test: `eval/fusion-arm/tests/`

**Interfaces:**
- Consumes: `Miller.Core.Search.RrfFusion.Fuse(IReadOnlyList<SymbolCandidate>, IReadOnlyList<SemanticRankedCandidate>, FusionWeights, int rankConstant)`, `SemanticQueryPolicy.Route(string?, LexicalEvidence?)` → `SemanticQueryRoute(IsHybrid, HybridClass, Reason)`, `LexicalEvidence(int HitCount, double TopScore, double RunnerUpScore)`.
- Produces: CLI `fusion-arm fuse --queries <dev queries.jsonl> --lexical <dir> --semantic <dir> --k-const <int> --conceptual-ratio <r> --out results.jsonl [--forced-hybrid]`. Input formats: lexical per-query file `<query_id>.json` = array of `{symbol_id, doc_id, score}` (rank = array order); semantic per-query file same shape plus `rank`. Output: retrieval-eval `results.jsonl` (`{"query_id", "ranked": [doc_id...]}`), post-fusion top-10, doc_id collapse AFTER fusion, dedupe doc_ids preserving best rank. `--forced-hybrid` bypasses `Route` (identifier diagnostic arm); default honors Route — LexicalOnly queries emit lexical order untouched.
- LexicalEvidence built from the lexical file itself: `HitCount = rows.Length, TopScore = rows[0].score, RunnerUpScore = rows[1]?.score ?? 0`.

**Contract inputs:** spec R2/R3; Conceptual ratio r maps to `new FusionWeights(1.0, r)` (RRF is scale-invariant — verified by RrfFusionTests determinism suite); SymbolLookup/Mixed always `RrfFusion.WeightsFor(class)` v1 values.

**File ownership:** Create: `eval/fusion-arm/**` (csproj, Program.cs, tests); Modify: none

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** The offline fused-arm runner that IS production fusion — same assemblies, no reimplementation.

**Approach:** TDD on the tests project: routing honored (identifier query → lexical passthrough), fusion parity with a hand-computed RRF fixture, doc-collapse-after-fusion, deterministic output. Keep Program.cs thin over a testable `FusionRunner` class.

**Acceptance criteria:**
- [x] Adapter builds and tests green (`dotnet test eval/fusion-arm/tests/FusionArm.Tests.csproj`).
- [x] Fixture run produces contract-valid results.jsonl (`retrieval-eval score` accepts it, exit 0).
- [x] Parity smoke DEFERRED to Task 4 start (needs live vectors): 5 dev prose queries, adapter at fusion-v1 vs `miller search --arm hybrid --json` — ranks must match.

### Task 4: Arm generation + scoring (serial; long-running)

**Files:**
- Create: bench `.cache/` embeddings + per-query semantic dumps; lexical dumps under `eval/fusion-arm/out/` (gitignore `out/`); score reports per profile per candidate.

**Interfaces:**
- Consumes: Task 2 frozen corpora + exclusion list; Task 3 adapter; Task 1 overlay (only if pass).
- Produces: for each candidate lane × 13 profiles + lexical control + semantic-only + forced-hybrid diagnostic: `report.json` from `retrieval-eval score --k 10`, laid out `eval/fusion-arm/out/<candidate>/<profile>/report.json`; consumed by Task 5.

**Contract inputs:** Lexical dumps via worktree-built CLI against the frozen-miller/julie workspaces: `miller search "<query>" --arm lexical --json --limit 50` (limit = production recall depth ceiling; record actual). Semantic candidates from `bench.py rank` at production depth (k*2 clamp per `SemanticSearchArm.MaxCandidates` — read the constant, record it). Embeds via `run-bench.sh` stages against frozen corpora (override `MILLER_REPO`/`JULIE_REPO` to the frozen worktrees).

**File ownership:** Create: bench `.cache/` artifacts, `eval/fusion-arm/out/` results (gitignored); Modify: none

**Serialization required:** Yes

**Dependency reason:** Needs Task 2 frozen corpora + Task 3 adapter; Task 1's lane appended only if spike passed.

**What to build:** All the evidence. Run embed lanes in background (~1h each cold); `RANK_ONLY=1` re-ranks thereafter.

**Approach:** Lead-driven with a background monitor per lane; parity smoke (Task 3 AC) runs first at fusion-v1 before any sweep scoring. Record corpus SHAs/artifact ids into each report directory (`meta.json`).

**Acceptance criteria:**
- [x] Parity smoke passed and recorded.
- [x] Score reports exist for every candidate × {13 profiles, lexical, semantic-only, forced-hybrid}; all `score` exits 0. (43 reports; v1 control = k60-r2 within the 12-profile grid.)
- [x] Every report dir carries `meta.json` with frozen SHAs + artifact ids.

### Task 5: Selection analysis

**Files:**
- Modify: `docs/findings/2026-07-21-fused-arm-encoder-benchmark.md` (results tables, stability, selection)
- Create: `eval/fusion-arm/analyze.py` (LOUO + paired bootstrap over per-unit scores; reads score reports' per-unit rows)

**Interfaces:**
- Consumes: Task 4 reports (per-unit scores are in the report's `units` block — verify field name via one report before coding).
- Produces: selected profile (k, Conceptual ratio) + evidence tables; verdict against the pre-registered winner bar and pin rule; consumed by Tasks 7–8.

**Contract inputs:** Global Constraints gates verbatim; bootstrap = 10,000 resamples over evaluation units, paired winner-vs-v1, seed recorded.

**File ownership:** Modify: `docs/findings/2026-07-21-fused-arm-encoder-benchmark.md` (sweep/selection sections)

**Serialization required:** Yes

**Dependency reason:** Consumes Task 4 score reports.

**What to build:** The analysis that turns 60+ reports into one defensible selection.

**Approach:** Winner must be modal under leave-one-unit-out re-selection; report per-class/per-language secondaries as the scorer defines them (per-query). If no profile passes the bar for BOTH shippable encoders, fusion-v1 stands — write that outcome explicitly.

**Acceptance criteria:**
- [x] Selection (or v1-stands) recorded with CI + LOUO evidence against the pre-registered gates, nothing post-hoc. (Winner bar NOT met — qwen3 CI includes zero; **fusion-v1 stands**.)
- [x] Pin rule applied to fused numbers + footprint facts; decision recorded. (**Pin → bge-small-en-v1.5-f32** per the pre-registered rule.)

### Task 6: Real-artifact cost (parallel with Task 4)

**Files:**
- Modify: findings doc cost-table section only.

**Interfaces:**
- Consumes: Task 2 frozen-miller worktree (its own `.miller/`, own leader — no contention with the live session).
- Produces: cost table: end-to-end clean initial vector build through BOTH cursors (symbol + chunk), download size, cold session load, warm embed latency, peak RSS, `vectors.db` size; ≥2 runs, median/range; qwen3 + bge-small.

**Contract inputs:** `MILLER_SEMANTIC=shadow MILLER_SEMANTIC_MODEL=<id>` on a serve process rooted at the frozen worktree; converge throughput lines (commit 59c2c79) + wall-clock around the whole build; `/usr/bin/time -l` for peak RSS. Between runs delete `<frozen>/.miller/vectors.db*` and retained generations. Models already cached — record download sizes from `~/.cache/julie-semantic` file sizes, do not re-download.

**File ownership:** Owns frozen-worktree `.miller/` state + findings cost-table section (distinct section from Task 5)

**Serialization required:** No

**Dependency reason:** None - safe parallel batch (Batch B, alongside Task 4; distinct findings sections prevent write conflicts — lead merges).

**What to build:** The adopter-cost evidence for the pin decision.

**Acceptance criteria:**
- [ ] Cost table with both cursors end-to-end, ≥2 runs each model, median/range, peak RSS, artifact sizes.
- [ ] Bench-lane wall-clock for research arms labeled harness-not-engine.

### Task 7: fusion-v2 product change (iff Task 5 bar met)

> **SKIPPED — precondition failed.** Task 5's winner bar was not met (qwen3 paired CI includes
> zero); fusion-v1 stands and no constants change ships. The pin change to bge-small (Task 5's
> pin-rule outcome) is a separate scoped change per the design's acceptance criteria
> ("registry/docs/conformance goldens updated iff the pin changed").

**Files:**
- Modify: `src/Miller.Core/Search/RrfFusion.cs` (constants + `FusionProfile = "fusion-v2"`), `src/Miller.Server/Tools/SearchTool.cs:2374-2440` (chunk mirror constants), `tests/Miller.Tests/Core/RrfFusionTests.cs`, canary fusion-profile expectations (`CanarySearchTests`, `CanaryContentSearchTests`, `SemanticQueryDiagnosticsTests:227-250`)
- Create: contract tests in `tests/Miller.Tests/Indexing/` — `ClassifyChange` fusion-profile change → `QueryTimeOnly` (no rebuild/re-embed), fusion-v1-stamped artifact opens under fusion-v2 reader, telemetry stamps `fusion-v2` while serving the old artifact (extend `VectorStoreTests`/`VectorGenerationManagerTests` patterns, fixture at `VectorStoreTests.cs:122` shows the identity-fields shape).

**Interfaces:**
- Consumes: Task 5 selected constants.
- Produces: `RrfFusion.FusionProfile == "fusion-v2"`; everything downstream reads it via the constant (verified: `MillerSemanticContract.FusionProfile` is a separate const at `MillerSemanticContract.cs:86` — check whether it must change too; the artifact-side profile is build-stamped and stays `fusion-v1` on old artifacts by design).

**Contract inputs:** spec T6; lexical goldens untouched; `MILLER_SEMANTIC=off` zero-work tests untouched.

**File ownership:** as listed; serial task.

**Serialization required:** Yes

**Dependency reason:** Runs iff Task 5 meets the winner bar; consumes its constants.

**What to build:** The frozen constants change plus the compatibility proof.

**Approach:** TDD: update `WeightsFor_MatchesTheFrozenFusionV1Profile`-style pins to v2 values first (red), change constants (green); new contract tests red→green. If `MillerSemanticContract.FusionProfile` (build-stamp side) also needs bumping, verify `ClassifyChange` still classifies the delta `QueryTimeOnly` — if code reality contradicts the spec's query-time-only claim, STOP and report plan mismatch.

**Acceptance criteria:**
- [ ] Constants + profile id shipped; fast suite green; Release 0W/0E.
- [ ] Compatibility tests green (QueryTimeOnly, old-artifact-new-reader, telemetry stamp).
- [ ] Canary transition note added to the findings doc (distinct commit, next-UTC-day window).

### Task 8: Freeze + sealed acceptance request

**Files:**
- Modify: findings doc (freeze record + sealed threshold pre-registration).

**Interfaces:**
- Consumes: Tasks 5–7 outcomes.
- Produces: freeze record (model id, dims 512, quant int8, fusion profile + constants, thresholds, miller/julie SHAs, bench-pins + overlay SHAs) and the sealed request text for the user (protocol steps 1–2 of `SEALED-SET-PROTOCOL.md` §Handoff).

**Contract inputs:** sealed thresholds pre-registered NOW (spec T7): overall nDCG@10 ≥ dev × 0.85 (15% relative floor), language macro ≥ dev × 0.85, worst-language ≥ dev worst − 0.05, identifier non-inferiority per dev diagnostic margin, negatives report-only, production declared top-k/no-abstention.

**File ownership:** findings doc; serial task.

**Serialization required:** Yes

**Dependency reason:** Consumes Tasks 5–7.

**What to build:** The freeze that makes the sealed event decidable in advance.

**Acceptance criteria:**
- [ ] Freeze record complete with exact identifiers and pre-registered sealed thresholds.
- [ ] Sealed request text handed to the user; no sealed content anywhere in the repo.
