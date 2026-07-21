# Fused-arm encoder benchmark

**Date:** 2026-07-21
**Program:** Encoder comparison + fusion-v2 selection
**Spec:** [`docs/plans/2026-07-21-encoder-comparison-fusion-v2-design.md`](../plans/2026-07-21-encoder-comparison-fusion-v2-design.md) (rev 2, codex-reviewed)
**Plan:** [`docs/plans/2026-07-21-encoder-comparison-fusion-v2-plan.md`](../plans/2026-07-21-encoder-comparison-fusion-v2-plan.md)
**Status:** SKELETON — pre-registered before any scoring. Result sections (Tasks 4/5/6) are empty stubs.

This document pre-registers every decision gate before a single number exists. Nothing below the
"Pre-registered gates" line may be edited after scoring begins except to fill the result stubs; the
gates themselves are frozen.

---

## Pre-registered gates

Copied verbatim from the plan §Global Constraints (`docs/plans/2026-07-21-encoder-comparison-fusion-v2-plan.md`).
Numbers, not prose.

**Pre-registered gates only (T5):** winner bar = beats fusion-v1 overall cluster-unit nDCG@10 with
paired-bootstrap 95% CI excluding zero AND no regression > 0.02 nDCG on language macro-average,
worst-language, docs_like view, or identifier diagnostic, for BOTH qwen3 and bge-small. Pin rule =
bge-small takes the pin iff its fused overall nDCG is within 3% relative of qwen3 AND worst-language
loss ≤ 0.02 absolute.

**Profiles under sweep (T3):** global k ∈ {20, 60, 120} × Conceptual semantic:lexical ratio ∈ {1:1,
2:1, 3:1, 4:1} = 12, plus fusion-v1 control = 13. SymbolLookup and Mixed constants stay fusion-v1
everywhere.

**Canary transition (T6):** fusion-v2 ships as a distinct commit; measurement window starts next UTC
day; transition day excluded.

### R1 — within-run comparability

Per spec R1: all arms run against corpora built from clean worktrees at SHAs frozen before any
results are seen; `validate` confirms every graded doc exists at them; benchmark-derived documents
are excluded from the corpus by an explicit exclusion list in `build_corpus.py` because several
graded answers are named verbatim in them. Index artifact ids and revisions are recorded.
**Numbers are within-run comparable only** — a score from this run may be compared to another arm
scored in the same run against the same frozen corpora, never to a number from any other run.

---

## Frozen substrate

Corpora were frozen at the current local `main` HEAD of each repo (detached worktrees under scratch),
then indexed with the worktree-built `miller` (`workspace open --path <root> --full`, no semantic env
vars). julie-extract binary version **2.16.0** on both.

| Repo | Frozen SHA | Frozen worktree | Workspace id | Artifact id | Symbols |
|---|---|---|---|---|---|
| miller | `59c2c79e8633940de5d394f73235f10acbe2c2b8` | `<scratch>/frozen-miller` | `6772d4640d5de25305f25317098cc2cf62539ea3bc588bc5969bf375532fe894` | `artifact-1784654234183324000` | 49,276 |
| julie | `9d1d22c5dcca8509e412db96b6dbb5ff19d4311a` | `<scratch>/frozen-julie` | `b3282901372258f13a2038b121f7f708a208797f350e5f5d0a89cd86888257bc` | `artifact-1784654260643605000` | 34,429 |

`<scratch>` = `/private/tmp/claude-501/-Users-murphy-source-miller/df49671d-ef55-48b5-b537-7efdb9e2bce8/scratchpad`.
Both frozen roots are registered in `~/.miller/workspaces.db` (throwaway benchmark registrations —
prune after the program completes).

**`validate` gate (proves every graded doc exists at the frozen SHAs):**

```
$ dotnet run --project eval/retrieval-eval -- validate \
    --queries eval/retrieval-eval/sets/dev/queries.jsonl \
    --corpus miller=<scratch>/frozen-miller --corpus julie=<scratch>/frozen-julie
corpus: 38 distinct doc references checked, 0 missing
queries: 82
OK: schema valid and composition minimums met      # exit 0
```

---

## Corpus exclusions (`build_corpus.py`)

`BENCHMARK_DOC_EXCLUSIONS` is applied unconditionally to the **miller** corpus alongside the existing
`GOLDEN_SET_EXCLUSIONS`. It names the five benchmark docs the plan lists **plus every other `docs/`
file at the frozen miller SHA whose text contains a graded `doc_id` from the dev set** — a plan or
findings doc that enumerates answer paths is a leaked cheat sheet. Derived by grepping the 38 distinct
graded doc_ids against `docs/` at SHA `59c2c79`; **56 files** (5 named ∪ 53 grep-derived; the named
`dead-code-candidates-dogfood` and `model-benchmark` docs are inside the 53). None of the 56 is itself
a graded answer doc, so no ground truth is removed (verified: the two graded miller docs
`docs/adr/ADR-0001-guidance-delivery-channels.md` and `docs/release-process.md` are not in the list).

**Row-count proof (miller corpus):**

| | units | symbol cards | doc chunks |
|---|---|---|---|
| without benchmark exclusions | 19,465 | 13,905 | 5,560 |
| with benchmark exclusions | 17,032 | 13,905 | 3,127 |
| **excluded** | **2,433** | 0 | 2,433 |

All 2,433 excluded units are doc chunks (0 cards — the excluded files are markdown/csv/json with no
code symbol cards). Full corpus (miller + julie, exclusions applied): 35,392 units; golden-set leak
check PASS.

<details><summary>The 53 grep-derived docs (files under <code>docs/</code> naming ≥1 graded doc_id at SHA 59c2c79)</summary>

```
docs/contracts/canary-telemetry-v1.md
docs/contracts/semantic-sidecar-protocol-v1.md
docs/contracts/vectors-v1.md
docs/findings/2026-06-05-julie-side-by-side-audit.md
docs/findings/2026-06-05-tool-output-token-savings.md
docs/findings/2026-06-23-1.0-readiness-review.md
docs/findings/2026-07-07-dead-code-candidates-dogfood.md
docs/findings/2026-07-19-model-benchmark.md
docs/findings/benchmarks/2026-06-27-foundation-matrix/final-baseline/results.csv
docs/findings/benchmarks/2026-06-27-foundation-matrix/final-baseline/results.json
docs/findings/benchmarks/2026-06-27-foundation-matrix/search-inspect-recovery-hardening/results.csv
docs/findings/benchmarks/2026-06-27-foundation-matrix/search-inspect-recovery-hardening/results.json
docs/findings/benchmarks/2026-06-27-foundation-matrix/task3-retrieval-inspect-ambiguity/results.csv
docs/findings/benchmarks/2026-06-27-foundation-matrix/task3-retrieval-inspect-ambiguity/results.json
docs/plans/2026-05-31-workspace-registry-freshness-plan.md
docs/plans/2026-06-01-julie-extractors-migration-plan.md
docs/plans/2026-06-04-cli-workspace-open-remove-design.md
docs/plans/2026-06-04-symbol-search-collapsed-trigram-design.md
docs/plans/2026-06-05-source-regions-pillar3-implementation-plan.md
docs/plans/2026-06-07-content-corpus-fts5-search-plan.md
docs/plans/2026-06-07-incremental-search-sidecar.md
docs/plans/2026-06-09-miller-data-opportunities-plan.md
docs/plans/2026-06-09-miller-quality-review-goal-implementation-plan.md
docs/plans/2026-06-09-patterns-tool-implementation-plan.md
docs/plans/2026-06-09-reference-aware-context-design.md
docs/plans/2026-06-10-review-findings-fixes.md
docs/plans/2026-06-11-version-aware-leadership-design.md
docs/plans/2026-06-11-version-aware-leadership.md
docs/plans/2026-06-23-telemetry-workspace-onboarding-implementation-plan.md
docs/plans/2026-06-27-search-inspect-effectiveness-implementation-plan.md
docs/plans/2026-06-27-search-no-results-recall-plan.md
docs/plans/2026-07-02-guidance-delivery-design.md
docs/plans/2026-07-02-guidance-delivery-implementation.md
docs/plans/2026-07-02-tool-output-compaction.md
docs/plans/2026-07-05-rust-ct-impact-single-release.md
docs/plans/2026-07-06-background-bootstrap-design.md
docs/plans/2026-07-06-background-bootstrap-implementation-plan.md
docs/plans/2026-07-07-dead-code-candidates-implementation-plan.md
docs/plans/2026-07-07-metric-history-implementation-plan.md
docs/plans/2026-07-08-dashboard-registry-hygiene.md
docs/plans/2026-07-09-impact-traversal-evidence-implementation-plan.md
docs/plans/2026-07-12-telemetry-diagnosis-hardening.md
docs/plans/2026-07-16-agent-interaction-improvements.md
docs/plans/2026-07-17-julie-extract-2.15.0-adoption.md
docs/plans/2026-07-19-miller-semantic-integration-design.md
docs/plans/2026-07-19-p0-governance-and-gates-plan.md
docs/plans/2026-07-19-p1-freeze-and-conformance-plan.md
docs/plans/2026-07-20-p2-miller-lanes-plan.md
docs/plans/2026-07-20-p3-integration-plan.md
docs/plans/2026-07-20-p3-track1-sidecar-pins-plan.md
docs/plans/2026-07-20-semantic-p4-shadow-rollout.md
docs/plans/2026-07-21-semantic-p5-canary-plan.md
docs/release-notes/v0.1.0-beta.1.md
docs/release-notes/v1.4.0.md
```

Named-but-not-in-grep (excluded unconditionally; do not exist or carry no graded id at SHA 59c2c79):
`docs/plans/2026-07-21-encoder-comparison-fusion-v2-design.md`,
`docs/findings/2026-07-21-fused-arm-encoder-benchmark.md`.
</details>

---

## Results — Task 4: Arm generation + scoring

_Empty stub. Task 4 fills: parity smoke result; per-candidate × {13 profiles, lexical, semantic-only,
forced-hybrid} score reports; each report dir's `meta.json` frozen SHAs + artifact ids._

## Results — Task 5: Selection analysis

_Empty stub. Task 5 fills: per-model semantic + fused tables; sweep results; LOUO stability; paired
bootstrap CI vs fusion-v1; selected profile (k, Conceptual ratio) or "fusion-v1 stands"; pin rule
applied to fused numbers + footprint; verdict against the pre-registered winner bar and pin rule
above._

## Results — Task 6: Real-artifact cost

_Empty stub. Task 6 fills: cost table (both cursors end-to-end, download size, cold load, warm embed
latency, peak RSS, `vectors.db` size; ≥2 runs, median/range; qwen3 + bge-small); bench-lane
wall-clock labeled harness-not-engine._
