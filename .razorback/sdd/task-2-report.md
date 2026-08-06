# Task 2 report — bootstrap self-retry after an admission-wait timeout + ineligible-rebind logging

**Status:** complete. Implementation, tests, and worker-scope verification are done. Not committed
(commit mode `parallel-lead-commit`).

## Worktree state

- Path: `/Users/murphy/source/miller/.claude/worktrees/p4-findings-fixes`
- Branch: `p4-findings-fixes`
- Base HEAD at start: `bc808b26` (matches the main checkout, so Miller reads against
  `miller-b275269b2d7c` describe the same code)
- All work done in this worktree. No `git add` / `git commit` run.

## Files changed (only my owned set)

| File | Change |
|---|---|
| `src/Miller.Server/Hosting/ScanAdmissionTimeoutException.cs` | NEW — `sealed class ScanAdmissionTimeoutException : InvalidOperationException`, namespace `Miller.Server.Hosting` (the dominant namespace in that directory: 31 files vs 3). |
| `src/Miller.Server/Hosting/IndexBootstrapService.cs` | Typed throw site, retry scheduling, rebind-fallback logging (details below). |
| `tests/Miller.Tests/Server/BootstrapAdmissionRetryTests.cs` | NEW — 11 fast, pure tests (no `julie-extract`, so no `Category=Scale` trait needed). |

Nothing else was touched. `src/Miller.Indexing/RebindBootstrap.cs`,
`src/Miller.Server/Hosting/IndexerService.cs`, and
`src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs` show as modified in `git status`;
those are the sibling tasks' edits, not mine.

## What I built

### (a) Typed the admission-timeout failure — `IndexBootstrapService.cs:576`

`throw new InvalidOperationException(...)` became `throw new ScanAdmissionTimeoutException(...)`. The
message string is byte-identical to before. `ScanAdmissionTimeoutException` derives from
`InvalidOperationException`, so any caller that catches the base type still catches it.

The second admission site (`bootstrap-auto-rebuild`, `:687`) was deliberately left alone: it returns
`null` and logs a warning rather than throwing, so it never reaches `MarkBootstrapFailed`.

### (b) Delayed self-retry — `MarkBootstrapFailed` `:890`, `ScheduleAdmissionRetry` `:941`

- `MarkBootstrapFailed` now takes the run's `WorkspaceBindingResolver.WorkspaceSource` (threaded from
  `RunBootstrapInBackground` `:432`) so the retry re-runs with the ORIGINAL binding source instead of a
  hardcoded one. `source` is not read inside `RunBootstrap` today, but passing it verbatim keeps the
  retry a faithful re-run rather than a lookalike.
- After the registry-error marking, `if (error is ScanAdmissionTimeoutException)
  ScheduleAdmissionRetry(canonicalRoot, source, runGeneration);` (`:930`). Every other failure returns
  exactly as before — terminal.
- `ScheduleAdmissionRetry` captures `_shutdown.Token`, logs one Information line naming the delay, then
  `Task.Run(async () => { await Task.Delay(delay, shutdown); ... })`. After the delay it takes `_gate`
  and re-checks three things — shutdown not requested, `_phase == Failed`, `_runGeneration ==
  failedGeneration` — before calling `StartRunLocked(canonicalRoot)` and then
  `RunBootstrapInBackground(canonicalRoot, source, runGeneration)` with `rootReplaced` left at its
  default `false`, exactly the `:355-370` replaced-root shape.
- No new hosted service, no second timer system, no persisted state. `Task.Delay` + the existing
  shutdown CTS only.
- Unbounded by design: each failed retry re-enters `MarkBootstrapFailed` and re-schedules. One cycle is
  one bounded admission wait with no scan.

**`rootReplaced: false` is safe on a retry.** A `RootRebind` bootstrap that timed out still escalates on
the retried run, because the replacement fact is re-derived from the PERSISTED registry lineage inside
`RunBootstrap` (`persistedRootReplaced`, `:457-458`), not from the in-flight `rootReplaced` flag alone.

### (c) Jitter — `JitterAdmissionRetryDelay` `:997`

`baseDelay + baseDelay * (0.25 * Math.Clamp(sample, 0, 1))`, called with `Random.Shared.NextDouble()`.
Production base is `DefaultAdmissionRetryDelay = 60s` (`:1007`); `TestAdmissionRetryDelay` (`:1013`) is
the internal nullable override, matching the `TestBootstrapScanAdmissionWait` precedent (`:265`) exactly
(`TestAdmissionRetryDelay ?? DefaultAdmissionRetryDelay`).

### (d) Ineligible-rebind logging — `LogRebindFallback` `:809`, called at `:608`

The fallback arm's inline `Failed` warning moved into `internal static void LogRebindFallback(ILogger,
string canonicalRoot, RebindBootstrapOutcome)`. The `Failed` warning text and placeholders are
unchanged. The `Ineligible` case now logs one Information line:

```
"Worktree rebind not eligible for {Root} ({Reason}); scanning it in full."
```

`Promoted` never reaches this arm (it is the `if` branch), so promoted logging is untouched. Extracting
the helper is what makes both log shapes pinnable by a pure fast test — the arm itself only runs inside
a real extract-backed bootstrap.

## W8 confirmation (the nuance the brief demanded)

I read the code rather than assuming. **No plan mismatch.** A retry cycle cannot force-scan around a
standing W8 record without recording:

1. The retried run re-enters `RunBootstrap` and re-hits the pre-existing
   `failurePolicy.Evaluate(scanDecision.Intent, bypassBackoff: true)` at `:583`. That bypass is
   unchanged by this task.
2. Every scan that run can launch goes through `RunRecordedScan` (`:632` fallback scan, `:698`
   auto-rebuild). Its body: `RecordSuccess(attempt.EffectiveIntent)` on success;
   `RecordFailure(attempt.EffectiveIntent, JulieExtractException.ExitCodeOf(ex), attempt.Jobs ?? ...)`
   then `throw` on failure. So a retried run's scan records into the persisted journal exactly like the
   first run's.
3. A rethrown scan failure is not a `ScanAdmissionTimeoutException`, so `MarkBootstrapFailed` marks it
   terminal and schedules NOTHING. The cycle stops at the first run that actually reaches a scan.
4. Therefore the only failure that repeats is the one where admission was refused BEFORE
   `TestScanObserver`/the scan — no extractor spawned, no journal write, no `--jobs` consumed.

`bypassBackoff: true` is not passed at any new call site; `ScheduleAdmissionRetry` adds no
`bypassBackoff` argument at all.

## Miller calls used + API-shape evidence

Orientation was done with Miller MCP tools against `workspace_id=miller-b275269b2d7c`, then confirmed by
reading exact lines in the worktree before every edit.

| Call | What it gave me |
|---|---|
| `inspect target=IndexBootstrapService depth=overview` | Class doc, the `_gate`/`_shutdown`/`_runGeneration`/`_phase` field set, `IHostedService, IDisposable` implements, 86 dependents, test locations (`BootstrapReplacedRootTests`, `HostStartupRegistrationTests`). |
| `search query=AcquireBootstrapScanAdmission` | Definition at `:1108` plus BOTH call sites (`:572` `"bootstrap"`, `:685` `"bootstrap-auto-rebuild"`) and the `TestBootstrapScanAdmissionWait` property at `:265` — which is how I found the precedent the plan names, and how I knew the auto-rebuild site returns null rather than throwing. |
| `inspect target=RunRecordedScan depth=full` | Full body + callees resolved to `ScanFailurePolicyStore.RecordSuccess` / `RecordFailure` and `JulieExtractException.ExitCodeOf` — the direct evidence for the W8 confirmation above, without reading the file. |

API-shape evidence: `inspect depth=full` returned the method body AND a resolved `callees` list with
`source=identifier_direct confidence=1.00` pointing at `src/Miller.Indexing/ScanFailurePolicyStore.cs:36`
and `:47`. That resolved-callee list is what let me confirm the journal write in one call instead of a
grep chain. `search` returned a `Definition found:` block plus `Other matches:` grouped by file with the
declaring line text, which distinguished the two admission call sites by their `reason` string argument.

## Verification

Invariant under test: **an admission-timeout bootstrap failure self-heals; every other bootstrap failure
stays terminal; a retry can never clobber a newer run.**

```
dotnet test --filter "FullyQualifiedName~BootstrapAdmissionRetryTests"
  → Passed! Failed: 0, Passed: 11, Skipped: 0, Total: 11, Duration: 1 s
```

The 11: retry-until-bound (also asserts the phase observed inside the retried run is `Running`),
deterministic-failure-is-terminal, stale-generation-retry-is-a-no-op, 5 jitter-band theory cases, the
exact jitter endpoints, ineligible logs Information with the reason, failed keeps its Warning.

**Mutation checks** — I wrote the implementation before first running the suite, so instead of relying on
red-first ordering I broke each guard in turn and confirmed the tests catch it:

| Mutation | Result |
|---|---|
| `error is ScanAdmissionTimeoutException` → `error is InvalidOperationException` (retry any failure) | 2 failed: `ADeterministicBootstrapFailureStaysTerminal`, `ARetryWhoseGenerationAdvancedDoesNotStartASecondRun` |
| Drop `_runGeneration != failedGeneration` from the retry guard | 1 failed: `ARetryWhoseGenerationAdvancedDoesNotStartASecondRun` |
| Remove the `ScheduleAdmissionRetry` call entirely | 1 failed: `AnAdmissionTimeoutFailureRetriesUntilTheBootstrapBinds` |

File restored from a scratchpad backup after each; final green re-confirmed.

Build:

```
dotnet build Miller.slnx -c Release   → Build succeeded. 0 Warning(s), 0 Error(s)
```

Worker ceiling:

```
scripts/test.sh   → Failed: 1, Passed: 6138, Skipped: 2, Total: 6141, Duration: 1m 6s
```

The single failure is
`CrossWorkspaceRefreshServiceTests.Refresh_HoldsMachineScanAdmission_AcrossTheScanAndTheSidecarConvergence`
(`tests/Miller.Tests/Server/CrossWorkspaceRefreshServiceTests.cs:1029`). That test and its subject
(`src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`) are Task 1's owned files and both show
as modified in `git status` — this is a sibling's in-flight edit, not a regression from my change. My
change touches neither file, and no `IndexBootstrapService` test regressed.

Scale/all were not run (worker ceiling respected).

## Concerns for the lead

1. **Build guard needs an escape hatch in this worktree.** `.tools/julie-extract` is not restored here, so
   a bare `dotnet build` fails the `VerifyPinnedJulieExtractVersion` guard. Every build/test command above
   ran with `MILLER_ALLOW_MISSING_JULIE_EXTRACT=1 MILLER_ALLOW_MISSING_SEMANTIC=1`, the documented
   offline overrides. The lead should run restore before any scale/pre-merge verification.
2. **My test file references `RebindBootstrapOutcome.Ineligible(...)`, `RebindBootstrapOutcome.Failed(...)`,
   and `RebindStage.Copy`** from `RebindBootstrap.cs`, which Task 3 owns. Those compile green right now;
   if that sibling changes the factory signatures, `BootstrapAdmissionRetryTests` needs the matching
   update.
3. **`MarkBootstrapFailed` gained a parameter** (`WorkspaceBindingResolver.WorkspaceSource source`, second
   position). It is private with one call site, so nothing outside the file is affected.
4. **Retry log volume.** Each admission-timeout cycle writes one Information line plus the existing
   `LogError` from `MarkBootstrapFailed` and one registry error row. At a 60s base delay that is roughly
   one triple per minute while contention lasts. Acceptable for a state that used to be a dead server,
   but if the lead wants it quieter, downgrading the repeated `LogError` after the first cycle is the
   place to change it.
