# Task 3 report — Wait out the source-scan heartbeat window instead of falling back to a full scan

**Status:** complete. Implementation + tests landed, uncommitted (commit mode `parallel-lead-commit`).

## Worktree state

| Fact | Value |
| --- | --- |
| Path | `/Users/murphy/source/miller/.claude/worktrees/p4-findings-fixes` |
| Branch | `p4-findings-fixes` |
| Base HEAD at start | `bc808b26` |
| HEAD at handoff | `94162908` — the lead committed a sibling task mid-run; I made no commits |
| My dirty files | `src/Miller.Indexing/RebindBootstrap.cs`, `tests/Miller.Tests/Indexing/RebindBootstrapTests.cs` |

`git worktree list` shows two trees: `/Users/murphy/source/miller` (`bc808b26 [main]`) and this one. Other dirty
paths in `git status` belong to the sibling tasks (IndexerService/CrossWorkspaceRefreshService, IndexBootstrapService,
ScanGovernor, the julie-extract exception work). I touched none of them.

## What changed

### `src/Miller.Indexing/RebindBootstrap.cs`

1. **New seam** `RebindBootstrapSeams.WaitBeforeRetry` —
   `public Func<TimeSpan, CancellationToken, bool> WaitBeforeRetry { get; init; } = DefaultWaitBeforeRetry;`
   Production default is `!ct.WaitHandle.WaitOne(delay)`: it blocks for the span and returns `false` the moment
   cancellation ends the wait early. Defaulted (not `required`) per the plan's seam rule — an absent injection must
   still wait correctly in production. `CancellationToken.WaitHandle` is safe for a default/none token
   (`CancellationTokenSource` serves a never-canceled source), and the maximum slice is 60 s, well inside
   `WaitOne`'s range.

2. **New constant** `internal static readonly TimeSpan SourceScanWaitBudget = TimeSpan.FromSeconds(60)` — twice the
   30 s window so a heartbeat stamped one tick before the read still resolves. Its doc comment records the
   non-obvious reason waiting is correct: every Miller scan runs under the machine-wide governor admission and
   `TryRebind` already holds the target's, so a fresh heartbeat under a held admission almost always means a
   just-finished scan, and the fallback the old refusal chose is a 110–1,345 s full extraction under that same
   admission (P4 scale validation §6). `SourceScanHeartbeatWindow` itself is unchanged; its doc paragraph that
   described paying a full extraction inside the window was now false and was rewritten.

3. **`SourceScanLooksLive` → `SourceScanFreshnessRemainder`** — same read (`ReadSourceHeartbeatUtc` + `UtcNow`, both
   unchanged seams), but it returns how much of the window is left instead of a bool, so the wait can size its slice.

4. **New `WaitOutSourceScan`** — loops while the heartbeat is fresh, waiting the reported remainder each pass
   (clamped to the remaining budget), re-reading the heartbeat every iteration. Returns a private
   `SourceScanWait` record struct: `Settled` / `StillLive` / `Cancelled` plus the accumulated wait. The budget
   accumulates the **requested** slices rather than clock deltas, which keeps it monotonic so an injected clock that
   does not advance still terminates the loop (commented in place).

5. **Call site (was `:435`)** — `Settled` falls through to the unchanged rebind sequence. `StillLive` returns
   `Ineligible` with the original reason text plus `(waited 60s for its heartbeat to go stale)`. `Cancelled` returns
   `Ineligible` naming the cancellation and the wait. Nothing has been staged at this point, so neither refusal can
   leave debris and neither writes the failure journal — unchanged from before.

Durations render through a small `Format` helper (`0.#` + `s`, invariant culture) so the reason text is
culture-stable.

### `tests/Miller.Tests/Indexing/RebindBootstrapTests.cs`

The shared `Seams()` factory now carries a fake clock (`_now`, starting at `UnixEpoch` — the value the old fixed
`UtcNow` used, so every unrelated test is byte-equivalent) and an instant `WaitBeforeRetry` that records the call,
accumulates `_waited`, and advances `_now`. **No test sleeps.**

Five cases cover the decision, per the plan:

| Test | Proves |
| --- | --- |
| `TryRebind_WhenTheSourceHeartbeatIsStale_ProceedsToCopyWithoutWaiting` | stale on entry ⟹ rebind, `_waitCalls == 0` |
| `TryRebind_WhenTheSourceHasNoHeartbeatFile_ProceedsToCopyWithoutWaiting` | absent heartbeat ⟹ rebind, no wait |
| `TryRebind_WhenTheSourceHeartbeatGoesStaleWithinTheBudget_WaitsOutTheWindowThenRebinds` | a 5 s-old heartbeat waits exactly the 25 s remainder in ONE slice, then copies and promotes |
| `TryRebind_WhenTheSourceKeepsScanningPastTheBudget_IsIneligibleAndReportsTheWait` | a heartbeat that keeps restamping ⟹ `Ineligible`, `_waited == SourceScanWaitBudget`, reason contains `60s`, zero copies, no failure record |
| `TryRebind_WhenCancellationEndsTheWait_IsIneligibleAndStartsNoScan` | a `WaitBeforeRetry` that cancels ⟹ `Ineligible`, one wait call, zero copies, zero scans, no staging or live file, no failure record |

The two former heartbeat tests were replaced: the old `..._WhenTheSourceHeartbeatIsFresh_IsIneligibleAndNeverCopies`
asserted exactly the behavior this task removes, and its successor is the past-budget case.

## Verification

- Red first: `dotnet test --filter "FullyQualifiedName~RebindBootstrapTests"` failed to compile on
  `RebindBootstrap.SourceScanWaitBudget` and `RebindBootstrapSeams.WaitBeforeRetry` (CS0117 ×3) before the
  implementation existed.
- Green, worker scope: **32 passed / 0 failed, 94 ms**; re-run against the current tree after the lead's sibling
  commit (`94162908`): **32 passed / 0 failed.**
- Worker ceiling `scripts/test.sh` (fast suite): **6145 passed / 0 failed / 2 skipped, 52 s** — inside the 30 s
  budget tripwire's own reporting and well under the split's intent.
- `dotnet build src/Miller.Indexing/Miller.Indexing.csproj -c Release`: **0 warnings / 0 errors.**
- Builds and test runs used `MILLER_ALLOW_MISSING_JULIE_EXTRACT=1 MILLER_ALLOW_MISSING_SEMANTIC=1`; this worktree has
  no restored `.tools`, and those are the documented offline escape hatches. No Scale or `all` run — per the worker
  ceiling.

**Invariant verified:** a recently-finished source scan delays a rebind by at most the window remainder instead of
costing a full extraction, and a genuinely live scan still refuses inside the budget.

Transient sibling noise, attributed and not mine: earlier runs failed to compile on `JulieExtractExceptionExitCodeTests`
(Task 4), `BootstrapAdmissionRetryTests` / `IndexBootstrapService` (Task 2), and `IndexerService` (Task 1); an
intermediate fast-suite run failed `CrossWorkspaceRefreshServiceTests` and `BootstrapAdmissionRetryTests`. All cleared
on retry once the siblings settled. My own filter-scoped run was green throughout.

## Miller MCP calls used

- `inspect RebindBootstrap depth=overview` — children list gave `SourceScanHeartbeatWindow:342`, `TryRebind:397`,
  confirming the plan's line anchors.
- `inspect RebindBootstrapSeams depth=full` — full seam record body with production defaults; showed the
  `required` vs defaulted split the new seam had to match, and paged a continuation token for the remaining body.
- `inspect TryRebind depth=full scope=src/Miller.Indexing/RebindBootstrap.cs` — exact prefilter/heartbeat call-site
  body plus resolved callees (`SourceScanLooksLive` at `:576`, confidence 1.00) and the three callers
  (`IndexBootstrapService:1508`, unit test `:488`, scale test `:50`), which is how I knew the scale test also
  constructs the seams.

API-shape evidence: every line anchor in the plan (`:342`, `:435`, `:576-579`) matched what `inspect` reported, and I
confirmed each with a file read in the worktree before editing. `inspect`'s caller list is what surfaced
`RebindBootstrapScaleTests` as a second seam construction site — I then verified it backdates its heartbeat by an
hour (`RebindBootstrapScaleTests.cs:204`), so it uses the production `WaitBeforeRetry` default and never waits.

## Concerns

- **None blocking.** One note for the lead: `RebindBootstrapScaleTests` builds `RebindBootstrapSeams` without
  `WaitBeforeRetry`, so it exercises the real sleep-based default. Its fixture backdates the source heartbeat one
  hour, so it stays on the no-wait path; if that fixture ever stops backdating, that Scale test could sleep up to
  60 s. Left untouched — the file is outside my ownership and needs no change today.
