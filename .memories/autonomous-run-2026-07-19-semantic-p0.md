# Autonomous Execution Report - P0 Governance and Gates (Miller Semantic Integration)

**Status:** Complete
**Plan:** docs/plans/2026-07-19-p0-governance-and-gates-plan.md
**Branch:** worktree-semantic-integration
**PR:** https://github.com/anortham/miller/pull/6
**Duration:** single session, ~6h wall (design + doubt pass + plan + 7-task execution + codex pre-merge review + fix round)
**Phases:** 1/7 program phases complete (P0 of P0–P6)
**Tasks:** 7/7 plan tasks complete + 5/5 review findings fixed

## What shipped
- ADR-0003 + boundary docs: local semantic retrieval is now permitted in Miller (CLAUDE.md/AGENTS.md/README/docs map updated; Eros migration inventory recorded) — 700cc50
- Edit failure-class telemetry: every edit failure row carries a queryable `failure_reason`; unhandled exceptions bucket as `unhandled_<Type>`; request-derived facts now stamped before execution so unhandled rows keep operation/target diagnosis — 747d887, 3886ec5
- Frozen canary telemetry contract v1 (`docs/contracts/canary-telemetry-v1.md`): assignment, attribution (bare/qualified/path hashes), aggregate export, frozen analysis parameters, computable gates — 557bdd6, 9ec1ce3 amendments, f370885
- `miller_version` telemetry column with concurrent-adder-safe migration — 9ec1ce3
- sqlite-vec-on-AOT spike: **hard gate PASSES on osx-arm64** (10/10 stages, zero extra AOT flags); isolated CI job for the 3 remaining RIDs — 6a2bffc
- Retrieval-eval harness + frozen 82-query dev golden set (cluster-unit primary scoring, weight-invariance tested) — 132c911, 7703e5f
- Model benchmark (24 arms, sha256-pinned, cached-vector reproducible): **hard gate #2 CLOSED with pin decision** — 367a7f2, 7703e5f

## The pin (headline outcome)
**Default: Qwen3-Embedding-0.6B f16 GGUF @ 512 dims, int8 vector storage. Fallback: bge-small-en-v1.5 f32 @ 384 dims, int8.**
Cluster-unit metrics (topk): recall@10 0.6979, nDCG@10 0.6423, worst-language nDCG 0.5997, clusters 14/14 — vs BM25 baseline 0.5625 / 0.5073 / 0.4847 / 10/14. Storage 13.9MB for a 28,377-unit corpus. The pre-benchmark favored lane (256d) and the first-run pin (1024d) are both rejected on worst-language evidence; design §2.4 amended (26e7a19).

## Judgment calls (non-blocking decisions made)
- `eval/retrieval-eval/Scorer.cs` — cluster unit score = MEAN over members (expected quality of a random phrasing) as primary; cluster-max reported as secondary. Both orderings agree on the pin.
- `eval/retrieval-eval/tests/ScorerTests.cs` — pinned WEIGHT invariance (a cluster contributes one unit regardless of member count) instead of the review brief's literal "duplicate a member → unchanged" example, which is not a true property of a mean; deviation flagged by the worker and accepted.
- `eval/model-bench/bench.py` — BM25 residual asymmetry kept deliberately: semantic arms exclude tests for all classes, Miller BM25 excludes them only for NL phrases, because that is production behavior; cannot affect recall (no golden doc is test-only).
- `docs/contracts/canary-telemetry-v1.md` — amended in place at contract_version 1 with an explicit self-expiring "pre-ship amendment" exception (no rows exist yet); alternative was a v2 fork of an unshipped contract.
- `docs/contracts/canary-telemetry-v1.md` — control population for the warm-latency clause = all eligible control rows (control never embeds, so it has no warm/cold split).
- `docs/findings/2026-07-19-model-benchmark.md` — accepted 512d's identifier nDCG 0.9759 (vs 1024d's 0.9980): ordering-only cost, recall stays 1.0000, lexical output stays byte-identical per ADR-0003; P1 open item to confirm on the hybrid arm.
- Branch-gate wall-clock tripwire exceedances (31–59s vs 30s ceiling) attributed to sustained external machine load (rustc + second claude session, loadavg 22–25); suites green throughout, 27s wall on quiet machine same day.

## External review (codex, adversarial)
- **Findings:** 5 (verdict: needs-attention)
- **Verified real, fixed:** 5 (commits: 3886ec5, f370885, 7703e5f) — zero false positives
  - Scorer primary metrics averaged paraphrase queries independently, violating design §8; lead independently reproduced codex's recompute from recorded artifacts before dispatching — fixed, and the fix FLIPPED THE PIN 1024d → 512d
  - Semantic arms ranked 6,104 is_test units the production surface excludes (178 vs BM25's 58 test paths) — population parity restored, re-ranked from cached embeddings
  - Canary warm-latency gate was not computable from the sanctioned export and estimators were unfrozen — local raw-row definitions + bucketed export histogram + frozen parameters added
  - Follow-up attribution could not match qualified inspect targets — `canary_result_qualified_hashes` over the real `Parent.Member` resolution shape (verified against symbols schema: no qualified-name column exists)
  - EditTool exception path lost operation/target/request metadata — stamps moved before Execute, exception-path test added
- **Dismissed:** 0
- **Flagged for your review:** 2
  - Five frozen canary statistical values are judgment calls, not design-sourced: min 30 units/arm, Welch 95% t-interval, min 100 rows per latency population, identifier margins (top-1 change CI ≤ 0.05, overlap@10 CI ≥ 8.0, min 30 shadow units). The 8.0 overlap floor is the softest — if fusion is intentionally aggressive on mixed queries this may be tighter than intended.
  - The "pre-ship amendment" exception weakens "frozen means frozen" prose; if you'd rather the contract had become v2, that is a small edit you own.
- Cost note: codex does not surface per-request token counts in its JSON output.

## Tests
- Fast suite 3618 passing / 0 failing (includes new exception-path edit test); scale suite 54/54; eval harness 31/31; Release build 0 warnings / 0 errors. Branch gate recorded at 2e26dba; subsequent commits are metadata-only (.razorback/.memories), so the evidence carries.

## Blockers hit
- None. (Push and PR held for user approval per approval boundaries; approved and executed 2026-07-19.)

## Files changed
- 64 files, +8288/−939 over 18 commits (main..HEAD): docs/plans (design + plan), docs/adr/ADR-0003, docs/contracts/canary-telemetry-v1.md, docs/findings (spike, benchmark), spike/SqliteVec.AotSpike + CI job, eval/retrieval-eval + eval/model-bench, src/Miller.Server (EditTool, EditService, TelemetryLedger), tests, CLAUDE.md/AGENTS.md/README.md, .memories + .razorback evidence.

## Next steps
- Review PR: https://github.com/anortham/miller/pull/6
- Sanity-check the five frozen canary statistical values and the pre-ship amendment exception (flagged above).
- P1 kickoff (sidecar + vectors.db) consumes: 512d int8 pin, sqlite-vec AOT evidence (3 non-mac RIDs still pending CI on push), and five inherited concerns: negative-query FP 1.0 at default threshold (tuning band 0.45–0.55 mapped), Qwen3 markdown weakness (n=4, drove the dims flip), 52 units/sec observed embed throughput vs the design's "minutes" initial-build claim, community-GGUF provenance for fallback models, identifier nDCG ordering cost to confirm on the hybrid arm.
- User-owned follow-ups noted during execution: sealed acceptance set (third repo/language beyond csharp/rust), promotion of the spike CI job to a required check, 30-day telemetry retention vs 30-day canary window (export before age-out), sibling tools (search/inspect/trace) may have the same post-execute telemetry stamping pattern as EditTool — out of P0 scope, worth an audit.
