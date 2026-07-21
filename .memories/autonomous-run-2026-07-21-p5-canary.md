# Autonomous Execution Report - P5 Canary Stage

**Status:** Complete
**Plan:** docs/plans/2026-07-21-semantic-p5-canary-plan.md
**Branch:** worktree-semantic-p5 (base d4a43ed)
**PR:** not created — no-push constraint active (no miller pushes until the semantic plan completes, user 2026-07-20); merged to local main per P4 precedent
**Duration:** ~3h 40m wall (single session, 8 tasks + codex review round)
**Phases:** 6/6 batches complete
**Tasks:** 8/8 complete (2 single-round fix iterations, 1 respawned worker)

## What shipped

- T1 `fffe9d8` — Encoder pin registry + `MILLER_SEMANTIC_MODEL` swap seam: `SemanticEncoderSelection` resolves the process-wide pin once; `VectorSidecar`/`VectorConvergeService`/embedding session consume it; source-scan guard bans new direct `DefaultEncoder` reads. Model swap converges via the normal shadow-rebuild path with rollback.
- T2 `f6bd105` — `miller telemetry canary` CLI: the frozen aggregate export envelope and the local-authoritative gate (`CanaryGateMath` Welch interval + Hill inverse-t + nearest-rank p95; `CanaryLedgerReader` attribution join; per-cohort report with pass/fail/underpowered/indeterminate verdicts).
- T3 `5b7b946` — `SemanticQueryDiagnostics`: every semantic-arm consultation yields typed fallback (13-value contract mirror), backend, warmth, embed/KNN ms, generation identity, fusion profile. Fix round 1 made `embed_timeout` producible by propagating the transport layer's typed `EndedByTimeout`.
- T4 `c3bd69e` — Converge reliability (P4 dogfood follow-ups): coalescing 5-min held-cursor retry wake, incremental-path disk gate with shadow-identical pause facts, deferral INFO log naming paths, `ready (rebuilding)` compact status hint.
- T5 `4f4797e` + `2c4ce8a` — Symbol-route canary go-live: assignment flip (bucket<50 control), eligibility ladder, pure `CanaryQueryClassifier`, treatment = production hybrid arm past the mode gate, contract-faithful facts + served-result hashes. Fix round 1 unified the briefly-duplicated serving pipeline into `SearchRouteExecutor.RunSymbolsCore`.
- T6 `44f3b36` — Content-route canary: fourth instrumented surface, path-only served hashes (name/qualified arrays absent per absent-vs-zero).
- T7 `39333db` — Identifier shadow population: 10% sampled serve-first shadow rows with overlap/top1/rank counters; `ShadowSymbolArm` forces consult+fusion past the policy gate (the production arm abstains on identifiers — without the bypass every shadow row would be degenerate and the noninferiority gate vacuous).
- T8 `4495cea` — Operator runbook (`docs/findings/2026-07-21-p5-canary-runbook.md`: enable → observe → export → gate → interpret, retention squeeze, model swap/rollback), docs map pointer, README Known-limits bullet updated ("not shipped yet" → "off by default", opt-in env vars documented).
- Codex-review fixes `f538962` + `6a8ea37` — see External review.

## Judgment calls (non-blocking decisions made)

- T1: no `CliDispatch` edit — `semantic prepare` passes `--model` through to the sidecar and never read `DefaultEncoder`; defaulting an unset `--model` would be new download-selection behavior the plan excluded.
- T2: `suppressed_unit_count` pools experiment + shadow suppressions (single frozen counter, same 5-call floor); `semantic_contribution_calls` = count of calls with contribution>0 (matches `_calls` naming + the frozen example's arithmetic); count-map key order = enum/ladder declaration order.
- T3: port-null → `VectorsMissing` (typed `VectorSidecarFacts.State` not exposed through the port factory); `VectorsStale`/`VectorsBuilding`/`DiskBlocked` are eligibility-layer facts with no query-arm producer.
- T4 plan mismatch accepted: no shadow-rebuild-in-progress JSON field exists; the `ready (rebuilding)` hint keys on the cross-wake `ShadowRebuildPendingMarker` cursor hold. Follow-up recorded: `vectors.db.rebuild` disk probe for the transient single-wake window.
- T5: `CanaryTelemetryTests` "control for every bucket until P5" test rewritten to assert the 50/50 split — the explicit criteria revision the assignment flip requires.
- T7: shadow-status mapping — arm diagnostics `EmbedTimeout` → `timeout`; prerequisite-unavailable fallbacks → `skipped`; execution failures → `error` (table pinned in tests).
- Lead, T4 nit (unfixed, benign): retry-wake CTS has a theoretical dispose race whose worst case is an unobserved exception and one skipped redundant retry immediately after a real wake.

## External review (codex, adversarial)

- **Findings:** 8 (2 high, 6 medium; verdict needs-attention)
- **Verified real, fixed:** 7 (commits: f538962, 6a8ea37)
  - Auto-rescue rows invisible to attribution (high) — rescue paths now extend `canary_result_path_hashes` under the shared ≤10 cap + truncation flag; arm-differential bias removed.
  - `MILLER_SEMANTIC=off` still stamped canary keys — both routes now fully inert per contract §Activation (the plan's `ineligible_semantic_disabled` ladder rung contradicted the contract; the value stays as unproduced frozen vocabulary).
  - Assignment date could disagree with persisted row `ts` — one captured instant (TimeProvider seam in `TelemetryScope`/`TelemetryLedger`) now backs both; midnight-straddle fake-clock test pins the frozen recompute guarantee.
  - Export forwarded unknown metadata labels off-box — count maps now emit only frozen vocabulary; histograms clamp to contract ranges (fail-closed at the privacy boundary).
  - Content `semantic_contribution_count` inflated by membership counting — now rank-aware (semantic must strictly outrank lexical); T6 fixture expectation revised 2→1 with the rank math documented.
  - Ok shadow rows omitted `canary_semantic_result_count` — now stamped including zero (absence no longer falsely reads as "arm didn't run").
  - `generated_at_utc` broke byte-identical re-export in real CLI runs — now derived from the window end (`to`+1d 00:00Z); repeated-export byte-identity test added.
- **Dismissed:** 1
  - "Identifier shadow work blocks the served response" (high) — working as planned: the approved plan explicitly chose synchronous post-finalize shadow bounded by the embed deadline, with shadow rows excluded from the latency gate by construction. Async shadow execution noted as a possible post-P5 refinement.
- **Flagged for your review:** 0
- Cost: not reported by codex-cli (no per-request token counts in its JSON output).
- Note: two fixes (contribution counting, shadow result count) reverse earlier lead inline-review rulings — codex's contract-fidelity arguments were stronger, and pre-ship was the moment to make v1 data self-consistent.

## Tests

- Branch gate @ 6a8ea37 (final HEAD): fast suite 4401 passed / 0 failed / 2 skipped; scale suite 86/86; Release build 0 warnings / 0 errors.
- Net new tests this branch: ~250 (canary serving/stamping/export/gate/shadow/diagnostics/converge/encoder-selection).
- Known pre-existing flake (not P5): `RepositoryIndexLoaderBridgeTests` SQLite pool-disposal race — failed once under parallel-worker load, green in isolation and on re-runs; same class as the P4 de-flakes, candidate for the same fixture treatment.

## Blockers hit

- None. One worker (first Task 5 attempt) wedged silently producing nothing — stopped and respawned; likely the known shared-Miller-MCP-connection jam (the respawn was told to abandon hung MCP calls and completed; T7's worker avoided MCP entirely and was fine).

## Files changed

- 55 files changed, ~7,800 insertions, ~1,000 deletions (d4a43ed..6a8ea37): 4 new `Miller.Core`/`Miller.Server` telemetry classes, canary orchestration in `SearchTool`/`SearchRouteExecutor`/`CanaryTelemetry`, encoder registry in `MillerSemanticContract`/`VectorSidecar`, converge fixes in `VectorConvergeService`, 10 new/extended test files, runbook + README/docs-map updates.

## Next steps

- Merged to local main; publish/push remains gated on the semantic-plan completion constraint.
- Turn the canary on for dogfood (`MILLER_SEMANTIC=shadow` + `MILLER_SEMANTIC_CANARY=on` per the runbook) and let rows accumulate; the 30-day retention squeeze means exports should run before rows age out.
- Recorded follow-ups: `vectors.db.rebuild` disk probe for the in-flight rebuild status window; `RepositoryIndexLoaderBridgeTests` de-flake; optional async shadow execution; reader-level vocabulary normalization in `CanaryLedgerReader` (export is now fail-closed, but other consumers would benefit).
- Later phase (per plan): per-tool integrations, default-on flip, and the model-comparison eval over the swap seam.
