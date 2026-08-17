# Autonomous Execution Report - Windows Dogfood Read Availability

**Status:** Complete
**Plan:** docs/plans/2026-08-17-windows-dogfood-read-availability-plan.md
**Branch:** fix/windows-dogfood-read-availability
**PR:** pending — filled in after PR creation
**Duration:** ~2h 40m
**Phases:** 3/3 complete
**Tasks:** 12/12 complete
**External-model policy:** no policy declared — openai/codex received the branch diff

## What shipped
- Search serves the last current sidecar while store resolve/sidecar rebuild runs; named inspect uses the generation reader when the search sidecar is not current.
- Coordinator quantum miss is retryable; the prior view stays readable.
- Idle 250 ms bind heartbeat no longer reads the store pointer or writes Information logs.
- Content import can open a live Windows log (FileShare.ReadWrite|Delete).
- Edit no-op preview is expected_empty, not an MCP error.
- Status eligibility uses one extractor version string.
- Same-pid scan admission queues on the existing drain instead of waiting 5 s to refuse.
- Locked content inspect reports converging/database_locked, not a dead corpus.
- `references candidates --limit` no longer blanks stdout or treats limit as a scan cap.
- Scale/CLI e2e helpers expose and guard a temp registry path.
- Diagnosed julie-extractors `locking protocol` as a stranded claimed import (operator refresh, not a Miller code fix).
- Resolution slowness stays on the August 13 recovery plan.

## Judgment calls
- `StoreWorkspaceCoordinator.cs` — Classified quantum miss by English message plus `coordinator_quantum` code; did not raise the 4000 ms cap.
- `IndexerService.cs` — Same-pid hold uses in-memory lease generation, not the advisory owner file.
- `IndexerLeadershipCoordinator.cs` — Production ctor still wires `ReadForLeadership`; tests inject the artifact version.
- `ContentCorpusExternalStore.cs` — Routed default `File.ReadAllBytes` through `OpenRead` so small live logs also work.
- `DeadCodeCandidateReader.cs` — After review, `--limit` is display-only again; a safety file cap withholds candidates instead of publishing a partial scan.
- `docs/plans/...plan.md:3` — Kept the standard razorback agentic-worker header.

## External review (codex, adversarial)
- **Passes:** general 6 / security 2

- **Findings:** 8
- **Verified real, fixed:** 6 (commits: 1806fa62, 1f118b42, f7ea532d)
  - [general] Sidecar-only convergence still disables last-good reads — last-good now allowed after exact resolve while the sidecar is behind.
  - [general] Named inspect can resolve through stale search data — inspect uses generation when the sidecar is not current; search keeps last-good.
  - [general] Last-good readers are cached under the live snapshot identity — cache key includes the served stamp.
  - [general] Explicit candidate limits produce incomplete dead-code evidence — `--limit` no longer caps the literal scan.
  - [general] Stamp validation and sidecar opening race atomic replacement — bounded retry on stamp/open/revision mismatch.
  - [general] A disposing governor lease can erase its successor’s held marker — generation-owned hold clear.
- **Dismissed:** 2
  - [security] Repository plan contains agent-directed prompt injection — dismissed; this is the standard razorback plan header used by every Miller plan, not untrusted attacker input.
  - [security] Last-good fallback can disclose deleted source content — dismissed as inherent last-good staleness the plan accepted; status still reports stale; generation/hash guards remain for inspect of live files.
- **Flagged for your review:** 0
- **Cost:** not reported by codex-cli (508,443 tokens shown on the general pass only)

## Review campaign
- **State:** clean
- **Evidence:** external-reviewed
- **Round:** 2/2
- **External invocations:** 2/2
- **Open critical/high:** 0
- **Open medium/low:** 0
- **Open at/above floor:** 0

REVIEW CAMPAIGN STATUS
state: clean
evidence: external-reviewed
round: 2/2
external_invocations: 2/2
open_critical_high: 0
open_medium_low: 0
open_above_floor: 0
campaign_closed: yes

## Tests
- Worktree `scripts/test.ps1` at 1806fa62: Failed 0, Passed 6654, Skipped 27, wall 98s. Security scope: none declared.
- Earlier worktree run had 3 real failures (fixed in 0350b441) and one timing flake (`started 0s ago` vs `1s ago`) that passed on rerun.

## Blockers hit
- None

## Files changed
39 files, +2846 / −116 (see `git diff --stat 5f089521..1806fa62` plus review-fix commits).

## Source control
- **Outstanding:** `C:\source\miller` on `main` still has uncommitted copies of the plan/findings/README from the planning session. They ride on this branch. Leave the main working tree dirty; do not commit on main.
- **Worktrees left in place:** `C:\source\miller\.worktrees\fix-windows-dogfood-read-availability` on `fix/windows-dogfood-read-availability` — kept, PR open.

## Next steps
- Review PR: pending — filled in after PR creation
- Optional operator refresh for julie-extractors after reading `docs/findings/2026-08-17-julie-extractors-windows-lock.md` (do not `workspace full` without approval).
- Resolution wall time remains on `docs/plans/2026-08-13-miller-performance-recovery-plan.md`.
