# Autonomous Execution Report - Rebind P3 Miller wiring

**Status:** Complete
**Plan:** docs/plans/2026-08-05-rebind-p3-miller-wiring-plan.md
**Branch:** rebind-p3-miller-wiring (worktree, base b0d96b75 on local main)
**PR:** not created — the user holds all miller pushes; integration decision pending
**Duration:** ~1 agent session (plan approval → branch gate → pre-merge review → closeout)
**Phases:** 3/3 complete (Batch A → Task 2 → Batch C)
**Tasks:** 7/7 complete

## What shipped
- Task 1: registry lineage columns (`git_common_dir`, `git_is_linked`, `git_dir`, `git_dir_created_at`) + `FindMainCheckoutByCommonDir` sibling lookup (504b1a2f)
- Task 2: bootstrap lineage capture + restart-proof persisted-identity replacement rule (0d8fb3e7)
- Task 3: `RebindEligibility` pure two-stage decisions — prefilter + snapshot validation (db6c9d7b)
- Task 4: `SqliteOnlineBackup` page-stepped online backup with a wall-clock budget (030f6a77)
- Task 5: `JulieExtractRunner.Rebind` verb seams over julie-extract 2.27.0 (4d15f108)
- Task 6: `RebindBootstrap.TryRebind` orchestration wired into the bootstrap `!dbExists` arm under the governor admission (ad6c1f88)
- Task 7: `rebound_from` provenance in status/health JSON, compact output, dashboard, and the Eros contract doc (c10e0254)
- Pre-merge fixes: OOM-clamp fallback re-evaluation, partial-scan warning carry, source-invariant precision (3a467108)
- Closeout: acceptance boxes ticked in the program plan and design doc §9; verification ledger; checkpoints (48d66b3f)

## Judgment calls (non-blocking decisions made)
- `src/Miller.Indexing/RebindBootstrap.cs` — `FallbackAttemptAfterRebind` lives in `Miller.Indexing`, not `IndexBootstrapService`, because its parameter types live there and `Miller.Tests` sees those internals; `Miller.Server` internals it does not.
- `src/Miller.Indexing/RebindBootstrap.cs` — `DescribeScanWarning` seam is `required`, not defaulted to null: a null default would silently reproduce the partial-scan bug for future callers.
- `tests/Miller.Tests/Server/RebindBootstrapScaleTests.cs` — the source fingerprint covers `symbols.db` AND `symbols.db-wal` (length:SHA-256, `(absent)` sentinel); `-shm` deliberately excluded per the house WAL-reader protocol.
- Worktree created manually with `git worktree add … HEAD` because the native tool bases on origin/main, which would drop the three unpushed main commits.

## External review (codex, adversarial)

- **Findings:** 3
- **Verified real, fixed:** 3 (commits: 3a46710)
  - OOM-clamp loss on the rebind fallback (real-bug, high) — the fallback scan reused the pre-rebind `ScanAttemptDecision`, so an exit-137 delta scan's `--jobs 1` clamp never applied; fixed with `RebindBootstrap.FallbackAttemptAfterRebind` (re-evaluates with `bypassBackoff: true` only on `Failed`).
  - Partial-scan silence (real-bug, medium) — a `status=partial` delta report promoted as a clean rebind; fixed with `RebindBootstrapOutcome.Warning` + the required `DescribeScanWarning` seam wired to `ExtractReportLog.DescribeWarning`, logged on the Promoted arm.
  - Source-invariant over-statement (real-improvement) — "zero source writes" ignored the deliberate WAL-reader protocol (writability probe, `-shm`); fixed as contract-language precision + `-wal`-inclusive Scale fingerprint. Copy protocol unchanged.
- **Dismissed:** 0
- **Flagged for your review:** 0
- Cost: codex does not surface per-request token counts in its JSON output.

## Tests
- Branch gate at 3a467108: `dotnet build Miller.slnx -c Release` 0 warnings / 0 errors; `scripts/test.sh all` exit 0 (fast suite green incl. +8 pre-merge-fix tests; scale 129 passed / 0 failed / 5 skipped).
- Live Scale proof: a real `git worktree add` checkout bootstrapped by rebind + `no_change` delta; provenance keys present; source artifact and `-wal` byte-identical; staging debris cleaned.

## Blockers hit
- None. Push/PR withheld by design: the user explicitly holds all miller pushes (main is 3 commits ahead of origin, unpushed).

## Files changed
- 46 files changed, 7242 insertions(+), 2219 deletions(-) across b0d96b75..48d66b3f (excludes this report commit).

## Next steps
- User decision: how to integrate `rebind-p3-miller-wiring` (merge to local main / push+PR / keep as-is).
- P4 scale validation on the W10 74k-file fixture (fresh worktree open ≥10× faster than the full-scan baseline; 8-worktree fleet convergence; SIGKILL recovery; language parity).
- Standing observations for follow-up, none blocking:
  - Exit-3 rebind refusals record a null exit code in W8 (`IncompatibleExtractException` carries no code) — additive fix only if telemetry needs it.
  - The 30s source-scan heartbeat window also suppresses rebind for ~30s after a source scan finishes.
  - The SQLITE_BUSY copy branch is untested (unreachable via the production path by design).
  - A failed rebind consumes the W8 failure slot (design §7.4 bias, intentional).
- Plan assumptions to confirm at leisure: `MILLER_WORKTREE_REBIND` kill switch (default on) and `MILLER_REBIND_COPY_BUDGET` (default 3 minutes).
