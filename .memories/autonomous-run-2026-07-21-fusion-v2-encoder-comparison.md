# Autonomous Execution Report - Encoder Comparison + Fusion-v2 Selection

**Status:** Complete
**Plan:** docs/plans/2026-07-21-encoder-comparison-fusion-v2-plan.md
**Branch:** worktree-fusion-v2-eval
**PR:** not created — standing constraint: no pushes to origin until the semantic plan is declared complete; branch merged to LOCAL main only
**Duration:** ~2 sessions (overnight autonomous run, 2026-07-21)
**Phases:** 8/8 complete
**Tasks:** 8/8 complete

## What shipped
- Frozen benchmark substrate: miller@59c2c79 + julie@9d1d22c corpora with anti-leak doc exclusions (56 files) and pre-registered gates (`566da2d`)
- Offline fusion adapter running production Route+RrfFusion over frozen arm dumps; parity proved 5/5 exact vs the live arm at pool closure (`c3f7e58`)
- CodeRankEmbed spike: FINAL DROP on an unfixed upstream llama.cpp converter bug; filed upstream as ggml-org/llama.cpp#25970 (user-approved)
- Full 43-arm sweep + selection analysis: sweep winner k20-r2 fails the pre-registered winner bar (qwen3 CI [−0.0201, +0.0080] includes zero) → **fusion-v1 stands**; selection-adjusted max-statistic bootstrap (qwen3 p=1.000, bge p=0.475) strengthens the verdict (`9a16bdb`, `db5a3c6`)
- Pre-registered pin rule fired: bge-small fused nDCG is 98.8% relative of qwen3 (within the 3% leg), worst-language leg holds → **default encoder pin moved to bge-small-en-v1.5-f32** (384-dim int8, lane vec0-int8-384-cosine-v1); qwen3 becomes the fallback (`0153adc`)
- Task 6 real-artifact cost table sealed the pin: bge builds 7.7× faster (40.4s vs 312.7s E2E), 27× smaller sidecar RSS (470 MiB vs 12.34 GiB), 5× faster warm query (802ms vs 4048ms), 9× smaller download (`b6b81e6`)
- Real product bug found and fixed: both ProcessSession sites launched the sidecar with no `--model`, so it always loaded its embedded qwen3 default; bge lanes wedged on a 512-vs-384 dims error. Fix: `ProcessSemanticSidecarLauncher.ForServe` always forwards the active encoder + strict `MatchEncoder` handshake refusal (`bf58afd`, user-approved mid-run)
- Task 8 freeze record + pre-registered sealed-set thresholds; findings doc COMPLETE (`0f07ef1`, freeze pointer updated to db5a3c6 in `cbb1fe7`)

## Judgment calls (non-blocking decisions made)
- `eval/retrieval-eval/Scorer.cs` — Extended the scorer to emit per-unit rows instead of recomputing metrics in python, keeping one source of truth for nDCG (contract-faithful-fixtures lesson).
- `docs/findings/2026-07-21-fused-arm-encoder-benchmark.md` §Task 6 — Replaced the loadavg<4 clean-run gate (never clears on a 24-core M2 Ultra with ambient loadavg 6–10 at 77% idle) with an intent-faithful gate: no bench workloads via ps + CPU idle ≥60%; amendment recorded verbatim in the findings.
- Design erratum recorded: the design's "1:1 (v1)" label was wrong — RRF is scale-invariant so k60-r2 IS the v1 control; documented rather than re-run.
- `docs/findings/...` freeze record — Frozen-arm commit updated 0153adc → db5a3c6 so the sealed run includes the F2 prepare fix; serving-path behavior is identical between the two.

## External review (codex, adversarial)
- **Findings:** 4
- **Verified real, fixed:** 3 (commits: db5a3c6)
  - F2: `semantic prepare` without `--model` silently prepared the sidecar's embedded qwen3 default instead of Miller's active encoder — now resolves `SemanticEncoderSelection.Active` and always passes explicit `--model`
  - F3: vectors-v1 contract DDL still said `int8[512]` after the pin flip — parameterized (default 384 / fallback 512)
  - F4: winner-vs-v1 CI ignored post-selection inference — added max-statistic selection-adjusted bootstrap to `eval/fusion-arm/analyze.py`
- **Dismissed:** 0
- **Flagged for your review:** 1
  - F1: sweep arms were generated at a request shape that differs slightly from the production serving path — regenerating all 43 arms at exact production shape is a major re-run; arm-internal validity holds (parity 5/5) and the sealed acceptance event runs the real production arm. Recommendation: the sealed event suffices.
- Cost note: codex does not report per-request token counts.

## Tests
- Branch gate @ db5a3c6: fast suite 4407 passing / 0 failing (2 skip), scale suite 86/86 (live sidecar `serve --model bge` under strict handshake), `dotnet build Miller.slnx -c Release` 0 warnings / 0 errors; retrieval-eval tests 32/32 @ 0f07ef1 (eval scorer unchanged since). Commits after db5a3c6 are markdown-only — evidence carries.

## Blockers hit
- None.

## Files changed
- 60+ files vs main@59c2c79: eval harness (`eval/retrieval-eval`, `eval/fusion-arm`), semantic pin + launcher (`src/Miller.Indexing/Semantic/*`, `src/Miller.Server/Cli/SemanticPrepareCli.cs`, `src/Miller.Server/Hosting/VectorConvergeService.cs`), tests across 8 suites, contracts (`docs/contracts/vectors-v1.md`, `semantic-sidecar-protocol-v1.md`), findings + plan docs, README encoder line.

## Next steps
- **Sealed acceptance run (user-owned):** per findings §Task 8 — run the frozen arm at merged commit (branch HEAD db5a3c6 + docs) against the sealed set; return aggregates only. Thresholds pre-registered at freeze; no one on the dev side reads the sealed set.
- **Decide F1:** regenerate all 43 arms at exact production request shape, or accept the sealed event as the production-shape check (recommended).
- Push to origin only after you declare the semantic plan complete (standing constraint honored — everything is local).
- Canary telemetry export before the 30-day retention squeeze (carried from P5) — not part of this plan; flagging so it isn't lost.
