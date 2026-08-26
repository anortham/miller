# Autonomous run report — CT dogfood campaign (2026-08-26)

**Status:** Complete (local; push/PR withheld — user approval boundary)
**Plan:** docs/plans/2026-08-26-ct-dogfood-campaign.md
**Branch:** worktree-ct-dogfood-campaign (worktree .claude/worktrees/ct-dogfood-campaign), 16 commits over main @ 2a5a80ec
**Tasks:** 13/13 complete. **PR:** not created — pushes need explicit user approval.

## What shipped (finding → fix)

1. CT build output moved INSIDE the workspace (`<ws>/.miller/ct/build/<proj12>`), so repo-root-relative tests pass with zero project settings; Windows MAX_PATH fallback to the legacy temp root (cd50b343).
2. Vitest "never ran" decomposed: it HAD run (evidence in Tycho ct.db). Shipped per-project status rows (verdict/cases/stale/red/last_run), project-named `covers_all`, `reason=no_selection` drain logging, and a daemon stdout breadcrumb naming the real log path (80eaca08, a3aa13dc).
3. `tests disable` now retires the project's cases from every read; re-enable restores them (54333b8b).
4. `run wait=true` during an active run joins it and returns the settled verdict; reason `run already active` (c0329cc1).
5. Stale-count spikes: the watermark seed predicate never matched on live workspaces (0 rows, two real DBs) — fixed; the poller cursor can no longer outrun unapplied staleness (a074e213).
6. Reds stay listed until they pass on EVERY staling path; impacted reds still re-run, no red loop (cd8025a7).
7. `failures` MCP output paged within 12 KiB, summaries bounded to 400 bytes, `project=` filter, `group=error_class` with `infra_shaped` flag (959bc545).
8. csproj search: julie-extractors xml spec claims MSBuild extensions (branch `feat/msbuild-xml-extensions` @ ee0da1a7, NOT pushed/released); Miller classifier treats MSBuild XML as config (83ec910f). End-to-end works after the next julie-extract pin bump.
9. `inspect target="<file>::<symbol>"` resolves as a file-scoped lookup across inspect/trace/impact/edit/CLI (23970685).
10. `activity: executing` with no run block renders an honest compact line (e5c87217).
Docs/contracts/CLAUDE.md updated + AGENTS.md synced (5ad9c233).

## Tests

Fast suite green at every batch boundary; branch gate `scripts/test.sh all`: fast 8861/0, Scale 197 passed + 1 Scale-only fixture failure on the old build-root shape, fixed (cc54e3cc) and its class rerun green. Security scope: none declared.

## Judgment calls

- Proceeded from plan to execution without a plan-approval pause (user CLAUDE.md autonomy instructions override the razorback approval gate).
- Task 9 deviation approved: head-keyed `::` parse joining tails via the qualified-member machinery.
- Task 2 plan mismatches accepted: the unavailable-delta paths already held the cursor invariant; the real gap was the zero-projects cursor save.
- Task 3 reading change accepted: a flaky-retry-window red reads fresh-red (verdict Red) instead of Partial.
- Lead fixed two small review-caught defects directly (convention-guard fixture literal 9249d503; Scale fixture cc54e3cc).

## Deferred minors (ledger)

- Greens recorded with no saved cursor (daemon-less runs) can seed a watermark across one unobserved window; bounded by identity flips.
- Poller reason string says `enqueued` when every enqueue declined (cosmetic).
- `PreserveStaleResult`/`MarkUnreportedRunCasesStale` still write `stale` over reds on aborted/unreported runs.
- Per-project unknown/never rows also render for discovered-but-untracked projects.
- `BuildRootFallbackReason` carried on the work item but not yet logged by a caller.

## Source control

- This branch: clean, 16 commits, unpushed.
- Main checkout: dirty `TODO.md` only (the user's dogfood notes — untouched by the run; campaign statuses annotated separately).
- `.worktrees/perf-ct-audit-v1.20.1` (detached, clean) and `.worktrees/qml-first-class-miller` (merged, clean): pre-existing, user's, untouched.
- julie-extractors: `feat/msbuild-xml-extensions` @ ee0da1a7 committed, unpushed; repo returned to clean main.

## Next steps (user decisions)

1. Approve merge of `worktree-ct-dogfood-campaign` into main (and whether to push).
2. julie-extractors: release with the xml-extension change and bump Miller's pin to finish csproj search.
3. Residual from finding 1: a general answer for hostile repo build hooks (Tycho's `npm ci` per build) — backlog decision.
