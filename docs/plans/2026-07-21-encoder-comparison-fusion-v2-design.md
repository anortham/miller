# Encoder comparison under fused arms + fusion-v2 — design

**Date:** 2026-07-21 (rev 2 — codex design review folded in, 12 findings, see §Review log)
**Status:** revised design, pre-plan
**Prereqs:** P5 canary machinery live (shadow dogfood since 2026-07-21); P0 model benchmark
([findings](../findings/2026-07-19-model-benchmark.md)) chose the qwen3 512d/int8 pin on
semantic-only arms; build-time embedding throughput logging shipped (`VectorConvergeService`,
commit 59c2c79).

## Why

Two evidence gaps block the opt-in semantic release:

1. **P0 compared models semantic-only.** Production serves a *fused* ranking, and dogfood shows
   fusion-v1 diluting clear semantic wins (`CanaryGateMath.WelchInterval`: semantic rank 1, fused
   rank ~8; `WorkspaceRootSafety` cluster: semantic ranks 2/4/5, fused top-4 miss). A model choice
   made on semantic-only arms can be wrong for the fused path, and hand-chosen fusion-v1 constants
   (`RankConstant=60`, per-class weights) were never swept.
2. **The default pin decides what every adopter downloads** (qwen3 1.1 GB vs bge-small 127 MB —
   design §2.4 left the footprint question open, to be decided on evidence).

## Decisions already made (user, 2026-07-21)

- **Candidates:** qwen3-0.6b-f16, bge-small-en-v1.5-f32, arctic-embed-s-f16 (pinned in
  `eval/model-bench/bench-pins.json`), plus a bounded attempt at CodeRankEmbed. Arctic and
  CodeRankEmbed are **research arms**: they inform the roadmap but cannot take the pin this round
  (no registry entry, no sidecar conformance).
- **Pin criterion:** quality first on the fused arm; a smaller shippable model within the
  pre-registered margin (§T5) takes the pin on footprint.
- **Fusion outcome ships:** clear pre-registered winner → `fusion-v2` in product code, then freeze
  for the user-run sealed acceptance event.
- Standing constraint: **no pushes** until the user calls the semantic plan complete.

## Ground rules adopted from review

- **R1 — Frozen corpus.** All arms run against corpora built from **clean worktrees at SHAs
  frozen before any results are seen** (new SHAs recorded in the findings doc; `validate` confirms
  every graded doc exists at them). Benchmark-derived documents (this design, the P0/fused findings
  docs, bench result files) are excluded from the corpus by an explicit exclusion list in
  `build_corpus.py` — several graded answers are named verbatim in them. Index artifact ids and
  revisions are recorded. Numbers are within-run comparable only; the findings doc says so.
- **R2 — Production policy, not label mapping.** The fused arm routes each query through the real
  `Miller.Core.Search.SemanticQueryPolicy.Route` (text + lexical evidence), which decides
  fusion-vs-lexical-only and the fusion class. The dev set's `query_class` labels are reporting
  metadata only. Identifier/path/short-token queries route LexicalOnly in production; a forced
  hybrid over them exists solely as the identifier non-inferiority diagnostic, clearly labeled.
- **R3 — C# fusion, no python mirror.** Fusion and policy execute in a small offline C# adapter
  (`eval/fusion-arm/`, outside `Miller.slnx` like `retrieval-eval`) that references `Miller.Core`
  and calls `RrfFusion`/`SemanticQueryPolicy` directly. No reimplementation, no mirror-drift risk.
  A 5-query live parity check against `miller search --arm hybrid` remains as a smoke test only.
- **R4 — Production-comparable arms, symbol route only.** The lexical arm is
  `miller search --arm lexical --json` output carrying symbol identity; the semantic arm ranks the
  symbol-card population at production recall depth (`k*2` clamp per `SemanticSearchArm`
  candidates). Fusion joins on symbol id, collapsing to `doc_id` only after the fused top-k.
  **The content/chunk route is out of sweep scope**: it keeps mirroring the symbol profile as
  today, gets a smoke comparison in the findings doc, and its own sweep is future work.
- **R5 — One profile for all shippable encoders.** No per-encoder weight tuning. The selected
  profile must beat fusion-v1 for the pinned default AND not regress the shippable fallback
  (bge-small) beyond the pre-registered margin. Research arms get the same profile, reported as-is.

## Work breakdown

### T1 — CodeRankEmbed feasibility spike (numeric budget, fail-forward)

Facts from the model card: MIT license, 547 MB f32, `NomicBertModel` custom architecture
(`trust_remote_code`), CLS pooling, **required query prefix**
(`Represent this query for searching relevant code:`), base `snowflake-arctic-embed-m-long`.

- Stages with stop conditions: (1) GGUF conversion via the pinned llama.cpp
  `convert_hf_to_gguf.py` — if NomicBert-long isn't supported by the pinned converter, stop;
  (2) pooling sanity gate (`bench.py sanity`); (3) **sentence-transformers parity gate: cosine
  ≥ 0.99 vs the HF reference on the conformance texts** — the three-text sanity check alone cannot
  prove conversion fidelity. Budget: one session total; any stage failure → written drop reason.
- Pass → candidate entry in a **local pins overlay** (`bench-pins.local.json`, gitignored) pinning
  HF revision, converter tag + command, output sha256, query prefix, pooling. No machine-specific
  URL lands in the committed pins.

### T2 — Offline fused-arm adapter (`eval/fusion-arm/`)

- C# console, outside `Miller.slnx`, referencing `Miller.Core` only. Inputs: per-query raw lexical
  candidates JSONL (from `--arm lexical --json`, symbol ids preserved — extend the CLI JSON with
  symbol id if it lacks one), per-query semantic candidates JSONL (from bench embeddings over the
  symbol-card population, unit ids = symbol ids), fusion parameters. Output: `results.jsonl` per
  the retrieval-eval arm contract.
- Routing per R2: the adapter calls `SemanticQueryPolicy.Route`; LexicalOnly queries emit the
  lexical ranking untouched. The forced-hybrid identifier diagnostic is a separate labeled arm.
- Parity smoke (R3): 5 dev queries through live `miller search --arm hybrid` vs the adapter at
  fusion-v1 constants; ranks must match; mismatch stops the sweep until explained.

### T3 — Sweep (pre-registered profiles, not free grid search)

- RRF ranking is invariant to scaling both weights, so only the semantic:lexical **ratio** matters
  per class; `RankConstant` is global in product code and sweeps globally.
- **Pre-registered candidate profiles** (complete, small): global k ∈ {20, 60, 120} × Conceptual
  ratio ∈ {1:1 (v1), 2:1, 3:1, 4:1} — 12 profiles, plus fusion-v1 as control. Classes without
  support keep fusion-v1 constants: Mixed has **1 dev query** and SymbolLookup fusion is
  production-unreachable (LexicalOnly routing), so neither is tuned; SymbolLookup constants are
  carried unchanged for the forced-arm diagnostic only.
- Dev-set support after policy routing: 82 queries / 48 evaluation units, dominated by prose
  (53 queries) — effectively the Conceptual class is what the sweep can decide.
- Selection: primary metric = overall nDCG@10 on **cluster units** (the corrected P0 math);
  stability check = leave-one-unit-out re-selection (winner must be modal) + paired bootstrap CI
  over units for winner-vs-v1; per-class and per-language views are secondary and use the scorer's
  existing definitions (per-class metrics are per-query in the scorer — reported as such, not
  relabeled).

### T4 — Embed lanes + real-artifact cost

- `run-bench.sh` for all candidates against the R1 frozen corpora (cold cache ≈ 1h/lane, then
  cached; `RANK_ONLY=1` re-ranks in seconds).
- For each **registry** candidate (qwen3, bge-small): measured on this workspace via
  `MILLER_SEMANTIC_MODEL` swap — end-to-end clean initial build through **both cursors** (symbol
  cards AND chunk convergence, not just the shadow-rebuild timer), model download size, cold
  session load, warm query embed latency, peak RSS during build, disk footprint of `vectors.db`.
  Repeated runs (≥2) on the same snapshot, median/range reported. Research arms get bench embed
  wall-clock only, labeled harness-not-engine.
- Rollback per runbook after each swap.

### T5 — Pre-registered decision gates (written into the findings doc BEFORE lanes are scored)

- **Fusion winner bar:** winning profile beats fusion-v1 on overall cluster-unit nDCG@10 with a
  paired bootstrap 95% CI excluding zero, AND no regression beyond −0.02 nDCG on
  language macro-average, worst-language, `docs_like`, or the identifier non-inferiority
  diagnostic, for BOTH shippable encoders. Otherwise fusion-v1 stands and T6 is a no-op.
- **Pin rule:** best fused-arm overall nDCG wins; bge-small takes the pin if its fused overall
  nDCG is within **3% relative** of qwen3's AND it does not lose worst-language by more than
  0.02 absolute — the 8.7× download and faster build then decide. (Precise restatement of the
  user's "quality first, footprint tiebreak".)
- **Agent-task completion** (the program's top-level measure) is explicitly NOT re-measured here:
  the live canary success-rate gate measures it on real agent traffic and remains the program
  gate. This package optimizes the retrieval metrics that feed it. (Scoped disagreement with
  review finding 7, reason recorded.)

### T6 — fusion-v2 in product code (iff T5's winner bar is met)

- `RrfFusion`: new constants, `FusionProfile = "fusion-v2"`; `SearchTool` chunk fusion mirror
  updated to the same constants; `RrfFusionTests` + canary stamp expectations updated; lexical
  goldens untouched.
- **Compatibility proof (replaces the StillValid check):** tests that
  `MillerSemanticContract.ClassifyChange` treats a fusion-profile change as query-time-only (no
  rebuild, no re-embed planned), that a fusion-v1-stamped artifact opens and serves under a
  fusion-v2 reader, and that telemetry stamps `fusion-v2` while serving that older artifact.
- **Canary transition (corrects the rev-1 risk note):** export units are keyed
  `(experiment, workspace, utc_date, query_class)` with no version — a same-day flip creates
  mixed-profile units. fusion-v2 therefore ships as a distinct commit (distinct
  `miller_version`), its measurement window starts at the next UTC day boundary, and the
  transition day is excluded from any gate read.

### T7 — Freeze + sealed acceptance + findings

- Findings doc `docs/findings/2026-07-21-fused-arm-encoder-benchmark.md`: frozen SHAs/artifacts
  (R1), per-model semantic + fused tables, profile selection with stability evidence, cost table,
  pin decision against T5, CodeRankEmbed verdict, content-route smoke.
- **Sealed thresholds pre-registered at freeze** (protocol requires deciding against gates):
  overall nDCG@10 floor relative to dev (e.g. within 15% relative of the dev number), language
  macro/worst floors, identifier non-inferiority margin, negatives **report-only** (production is
  top-k/no-abstention by declaration — a fused arm always returns results; an abstention design is
  out of scope). Exact numbers fixed in the freeze record before the user runs the sealed set.
- Freeze record: model id, dims, quantization, fusion profile + constants, thresholds, repo SHAs,
  bench pins + overlay SHAs. User runs the sealed event, returns aggregates only; we log it.

## Acceptance criteria

- [ ] Corpus SHAs frozen and recorded before any scoring; benchmark-derived docs excluded from the
      corpus; `validate` passes at the frozen SHAs.
- [ ] CodeRankEmbed spike concluded within budget: pass (overlay pin + ≥0.99 ST parity) or written
      drop reason.
- [ ] `eval/fusion-arm` adapter runs `RrfFusion`/`SemanticQueryPolicy` from `Miller.Core` (no
      reimplementation); 5-query live parity smoke passes at fusion-v1.
- [ ] Lexical arm carries symbol identity from `--arm lexical --json`; fusion joins on symbol id
      at production recall depth; content route explicitly out of sweep scope with a smoke note.
- [ ] The 13 pre-registered profiles scored on the frozen dev corpora for all candidates; winner
      selected by the T5 bar with LOUO stability + bootstrap CI evidence.
- [ ] Real-artifact cost table for qwen3 + bge-small (both cursors end-to-end, ≥2 runs,
      median/range); research arms labeled harness-not-engine.
- [ ] fusion-v2 shipped iff the bar is met: constants + profile id + chunk mirror + tests;
      compatibility proven via ClassifyChange/old-artifact-new-reader tests; lexical goldens
      untouched; fast suite green; Release 0W/0E.
- [ ] Canary transition rule honored: distinct commit, next-UTC-day window, transition day
      excluded.
- [ ] Pin decision recorded against the pre-registered T5 rule; registry/docs/conformance goldens
      updated iff the pin changed.
- [ ] Sealed thresholds pre-registered in the freeze record; sealed request handed to the user;
      aggregates logged. No pushes.

## Risks

- **CodeRankEmbed conversion unsupported by pinned llama.cpp** → bounded by T1 stop conditions.
- **Dev-set support is thin outside prose** → accepted: the sweep only claims to tune Conceptual;
  everything else keeps v1 constants and is reported, not tuned.
- **Frozen-SHA corpora diverge from what dogfood serves** → inherent to frozen evaluation;
  the canary measures the live path continuously and remains the program gate.
- **Sealed-set burn** → thresholds pre-registered; one event; failure diagnosis on dev + new
  material only.

## Review log

Codex design review 2026-07-21 (read-only, 12 findings: 9 high, 3 medium). Accepted: frozen-corpus
protocol (F1→R1), production policy routing (F2→R2), production-comparable arms/symbol-route scope
(F3→R4), C# adapter over python mirror (F4→R3), sweep restructure to pre-registered profiles with
ratio sweep + stability checks (F5→T3), single cross-encoder profile (F6→R5), pre-registered
decision gates (F7→T5, agent-task portion dismissed with reason recorded in T5), pre-registered
sealed thresholds + negatives report-only (F8→T7), canary mixed-unit correction (F9→T6),
ClassifyChange compatibility tests (F10→T6), CodeRankEmbed spike hardening (F11→T1), end-to-end
cost methodology (F12→T4). Dismissed in part: F7's scripted agent-task eval (duplicates the live
canary gate; recorded in T5).
