# Linux CT dogfood repair verification

Date: 2026-08-22
Platform: Linux
Branch: `plan/linux-dogfood-fixes`
Worktree: `/home/murphy/source/miller/.worktrees/linux-dogfood-fixes-plan`
Branch HEAD at the branch-gate replay: `1c4d770d`
Helper locator: `c35fe7af`

## Outcome

The repaired continuous-testing lifecycle passed the Linux Miller, Razorback, and more-itertools
replays. Miller's helper-host and Linux-fixture defects were isolated and fixed; the final Miller
status was green with `selected_count=8191`, `stale_count=0`, and no failures. The required
`scripts/test.sh all` rerun on the corrected test tree was green. The composite acceptance
criterion remains open solely for the post-commit clean worktree-state check.

## Launcher and Miller replay

The Unix launcher correction sequence was `3135849b` followed by `103e9b65`; the final live replay
used helper locator `c35fe7af`. `tests serve --json` reported ready in about 0.17 seconds. The
daemon PID remained stable, and status showed it running with its own process-group and session IDs.

The first full live xUnit run, before the final helper correction, selected 8,190 cases through
`ct-provider:dotnet` with `workspace_scope/foreground` and reason `eligibility_gate`. It requested
6,667 unique units in 122 chunks; the observed activity reached part 86/122 and retained final
part 119. Provider elapsed time was 213.39 seconds and the wait completed in 297.52 seconds. The
only red cases were 13 SharedBroker host fixtures. The cause was `Path.ChangeExtension` mishandling
the dotted extensionless apphost path. After `c35fe7af`, the retry was green in 39.18 seconds.

The final Miller status was green (`selected_count=8191`, `stale_count=0`, `failures=0`). Neither
`CtDaemonShadowCopyTests` nor `TestProcessStallTests` had a failure.

## Cross-provider replays

The three repositories were run serially because the execution budget is user-global.

| Repository | Before replay | Replay result | Restored state |
| --- | --- | --- | --- |
| Miller | Enabled; one xUnit project; daemon stopped; no budget holder | Green; 8,191 selected; no stale cases or failures | Family daemon stopped; no budget holder |
| Razorback | Disabled; projects hidden/none; daemon stopped; green; 34 selected; no stale cases | Node test run green; CT wait completed in 6.01s; JUnit: 276 tests, 276 passed, 0 failed, 4,169 ms | Disabled; `projects=[]`; daemon stopped; no budget holder |
| more-itertools | Disabled; `projects=[]`; daemon stopped; green; 2 selected; no stale cases | Pytest run green; 2 file cases; CT wait completed in 8.36s; JUnit: 736 tests, 0 failures, 0 errors, 7.086s | Disabled; `projects=[]`; daemon stopped; no budget holder |

For Razorback and more-itertools, activity completed before a provider/chunk sample was captured.
The `node:test` and `pytest` labels above are the configured framework mappings, not invented
provider or chunk facts.

To invalidate the existing CT watermark without changing source behavior, the Razorback derived
index revision moved from 566 to 1126 and the more-itertools revision moved from 56 to 57.

## Focused verification

- Launcher: 19 fast tests, 1 Linux Scale test, and a build passed.
- Helper locator: 9 support tests, 11 semantic-broker tests, 2 CLI theory tests, and a Release build passed.
- Branch-gate rerun: fast 8,232 passed, 0 failed, 9 skipped (8,241 total; test duration 51s, wrapper wall 55s); Scale 154 passed, 0 failed, 16 skipped (170 total; duration 52s). Wrapper Release builds reported 0 warnings/errors.
- Explicit final build at `1c4d770d`: `dotnet build Miller.slnx -c Release --no-restore`, 0 warnings and 0 errors in 2.45s.
- The final cleanup found no `ct-daemon` process (`pgrep`), the Miller family daemon was stopped, and both the main checkout and task worktree had no budget holder. The main source checkout remained clean.

At replay capture, the main checkout was `/home/murphy/source/miller` on `main` at `da6be63f`,
clean; before this documentation packet, the task worktree was
`/home/murphy/source/miller/.worktrees/linux-dogfood-fixes-plan` on `plan/linux-dogfood-fixes` at
`1c4d770d`, clean. This documentation packet intentionally leaves the task worktree dirty until
the lead commits the evidence, map, plan-status, and ignored ledger updates.

## Branch-gate replay

The first `scripts/test.sh all` fast attempt reported 8,231 passed, 1 failed, and 9 skipped
(8,241 total). Its only failure was the worktree-adoption duplicate-read assertion. Commit
`1c4d770d` corrected the assertion by capturing nonempty worktree statuses and the acknowledged
run in one wait while retaining primary isolation. Its focused verification was 30/30, including
the affected class at 21/21, with a clean Release build.

The rerun on that changed test tree passed 8,232 fast tests, failed 0, and skipped 9 (8,241 total;
test duration 51 seconds, wrapper wall 55 seconds). Scale passed 154, failed 0, and skipped 16
(170 total; 52 seconds). The wrapper's Release builds had 0 warnings and 0 errors. The explicit
`dotnet build Miller.slnx -c Release --no-restore` then passed at `1c4d770d` in 2.45 seconds with
0 warnings and 0 errors. The plan's composite exact-tree criterion remains pending solely for the
post-commit clean worktree-state check.

## Pre-commit state

The pre-commit inventory was the task worktree
`/home/murphy/source/miller/.worktrees/linux-dogfood-fixes-plan`, branch
`plan/linux-dogfood-fixes`, HEAD `1c4d770d`; its only Git-visible dirty files were the intended
`docs/README.md`, `docs/plans/2026-08-22-linux-ct-dogfood-repair-plan.md`, and
`docs/findings/2026-08-22-linux-ct-dogfood-repair-verification.md`. The ignored Razorback progress
ledger was also intentionally updated. The main checkout `/home/murphy/source/miller` was clean on
`main` at `da6be63f`. `git worktree list` contained only the main checkout and this task worktree.

## Remaining branch gate

The `scripts/test.sh all` portion and explicit Release build are green on `1c4d770d`. The composite
criterion remains pending solely for the post-commit clean worktree-state check; no claim is made
here that that final check has passed.
