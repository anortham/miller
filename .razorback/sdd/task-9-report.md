# Task 9 — Async refresh with progress

## Ledger

| Item | Value |
| --- | --- |
| Status | COMPLETE |
| Worktree | `/Users/murphy/source/miller/.claude/worktrees/dashboard-ux-fixes` |
| Branch | `worktree-dashboard-ux-fixes` |
| Base commit | `9f00d9b` (T8) |
| Commit | this file's own commit on `worktree-dashboard-ux-fixes` (SHA returned to the lead) |
| Fast suite | 3584 passed / 0 failed, 24s wall (ceiling 30s) |
| Release build | `dotnet build Miller.slnx -c Release` — 0 warnings / 0 errors |

## What changed

The Refresh button used to run the whole converge inside the POST. It now starts a background job and
answers immediately with an in-progress stack that polls itself to the outcome.

| File | Change |
| --- | --- |
| `src/Miller.Dashboard/DashboardRefreshJobs.cs` (new) | One-per-workspace in-memory job store. `Start(workspaceId, Func<WorkspaceRefreshResult>)` returns the running job or starts one; `Peek(workspaceId)` → `null` \| Running \| Completed (consumed on observation). |
| `src/Miller.Dashboard/Endpoints/DashboardEndpoints.cs` | POST `/fragments/refresh` starts a job and renders the stack from the job status; new GET `/fragments/refresh-status?workspace_id=`; both share `DetailStackResult`. `DashboardIndexFactsCache.Clear()` moved into `RefreshAndInvalidateFacts`, which runs on the job thread. |
| `src/Miller.Dashboard/DashboardHostPipeline.cs` | ETag middleware skips `/fragments/refresh-status` (exclusion only). |
| `src/Miller.Dashboard/Components/RefreshStatusPanel.razor` | Takes `Job` + `WorkspaceId`. Running → `Refreshing… started Ns ago` plus the poll attributes; terminal/none → unchanged result label with no poll attributes. |
| `src/Miller.Dashboard/Components/WorkspaceDetailPanel.razor` | `RefreshResult` → `RefreshJob`; refresh button upgraded to `hx-ext="morph"` + `hx-swap="morph:outerHTML"`. |
| `src/Miller.Dashboard/Components/WorkspaceDetailStack.razor` | Pass-through param `RefreshResult` → `RefreshJob` (see judgment calls). |
| `tests/Miller.Tests/Server/DashboardRefreshJobsTests.cs` (new) | 7 unit tests, fake refresh funcs only. |
| `tests/Miller.Tests/Server/DashboardMutationEndpointTests.cs` | 3 HTTP tests: fast POST, running→terminal-once, ETag exclusion. |
| `tests/Miller.Tests/Server/DashboardActivityFeedTests.cs` | Unowned, 1 line: `["Result"]` → `["Job"]` for the renamed parameter. |

## Miller calls (API-shape evidence)

| Call | Proved |
| --- | --- |
| `context(query='dashboard refresh endpoint fragments')` | Seeds: `RefreshStatusPanel`, `DashboardMutationEndpointTests`, `JsonRefreshEndpoint_UsesNonThrowingRefreshPath`. |
| `inspect(target='TryRefreshWorkspace', depth='full')` | `DashboardData.cs:1478` — `public static WorkspaceRefreshResult TryRefreshWorkspace(string registryDbPath, string toolsRoot, string workspaceId)`; never throws (wraps in `Failed`). Callers: `DashboardEndpoints.cs:32,212`. (Plan said `DashboardData.cs:1417` — real line is 1478.) |
| `inspect(target='WorkspaceRefreshResult', depth='full')` | `src/Miller.Server/Workspaces/WorkspaceRefreshResult.cs:25` — record `(Status, WorkspaceId, WorkspaceRoot, IndexDbPath, Revision=null, Scanned=false, WarningText=null, Error=null, ScanDuration=null, TotalDuration=null, ArtifactId=null)` + `StatusText`. Not modified. |
| Read `Endpoints/DashboardEndpoints.cs` | `RequireDashboardRequestHeader` first-statement pattern; `MapPost("/fragments/refresh", (string workspace_id, HttpContext context))`; `RazorComponentResult<T>` + `PreventStreamingRendering = true` registration idiom. |
| Read `DashboardHostPipeline.cs` | `FragmentETagAsync` gate: `HttpMethods.IsGet && Path.StartsWithSegments("/fragments")`. |
| Read `DashboardIndexFactsCache.cs` | `public static void Clear() => Entries.Clear();` (`DashboardIndexFactsCache.cs:36`). |
| `grep RefreshResult\|RefreshStatusPanel` over `tests/` + `src/Miller.Dashboard` | Exactly one unowned test passes `["Result"]` to `RefreshStatusPanel` (`DashboardActivityFeedTests.cs:604`); no test pins `WorkspaceDetailStack.RefreshResult`. |

## Contract inputs honored

1. **ETag exclusion** — `/fragments/refresh-status` skips `FragmentETagAsync`. Pinned by
   `RefreshStatusFragment_IsExcludedFromFragmentETagCaching` (no ETag header, `If-None-Match: *` → 200),
   with `/fragments/activity` as the control proving the middleware is still live (→ 304). **Mutation-checked**:
   removing the exclusion turns the test red.
2. **CSRF ordering** — `RequireDashboardRequestHeader(context)` is still the first statement in the POST
   handler; `FragmentRefreshPost_WithoutDashboardHeader_Returns400` still passes. The GET status route has no
   header requirement.
3. **JSON POST `/workspaces/{id}/refresh`** — untouched, still synchronous via `TryRefreshWorkspace`.
4. **Consumed types** — `TryRefreshWorkspace`, `WorkspaceRefreshResult`, `WorkspaceRefreshStatus` unmodified.
5. **Client wiring** — poll is `hx-get` + `hx-trigger="every 2s"` + `hx-target="#workspace-detail-stack"` +
   `hx-ext="morph"` + `hx-swap="morph:outerHTML"` + `data-poll-trigger="every 2s"` (matches
   `ActivityFeedPanel`, so the existing visibility-pause logic covers it for free).
6. **Test-shape caution** — `JsonRefreshEndpoint_UsesNonThrowingRefreshPath`'s 700-char window still passes
   untouched: the new `RefreshAndInvalidateFacts` helper (the only other `TryRefreshWorkspace` caller) is
   placed BEFORE `MapDashboardJsonEndpoints`, keeping that test's stated invariant ("the sole other
   TryRefreshWorkspace call precedes the route") true.
7. **`DashboardIndexFactsCache.Clear()`** — verified shape, now called in a `finally` on the job thread.

## Thread-safety

- `Lazy<Task<WorkspaceRefreshResult>>` with `LazyThreadSafetyMode.ExecutionAndPublication`; `AddOrUpdate`
  factories may run more than once, but a losing `Job`'s `Lazy` is never valued, so the func cannot run twice.
- `Task.Run` inside the `Lazy` factory → `Start` returns without waiting.
- `Run` catches everything and returns a `Failed` result: a throwing refresh is a terminal state, never an
  unobserved exception or a job stuck Running.
- `Peek` consumes with `TryRemove(KeyValuePair)` (remove-if-same-instance), so a `Start` racing the
  observation keeps its fresh job.
- `IsFinished` reads `Lazy.IsValueCreated` before `.Value` — a `Peek` racing a `Start` must never be the
  thread that starts the work.

## Self-review

- TDD: tests written first, compile-error red, then implementation → green.
- Acceptance 1: `FragmentRefreshPost_AnswersWhileTheRefreshIsStillRunning` asserts the POST returns 200 with
  in-progress markup while a `TaskCompletionSource`-gated func is still blocked (`Assert.False(gate.Task.IsCompleted)`)
  — no sleeps, no wall-clock assertion.
- Acceptance 2: `RefreshStatusFragment_RendersRunningThenTheTerminalResultExactlyOnce` (running markup → terminal
  `rev 43` with no poll attributes → next poll has neither) and
  `Start_WhileAJobIsRunning_ReturnsThatJobAndDoesNotRefreshTwice` (run counter == 1).
- No real refreshes in tests: every job is injected. Existing
  `FragmentRefreshPost_WithDashboardHeader_RendersDetailStack` still exercises the real func, as it did before,
  and still passes.
- Tests carry zero comments except one explaining the non-obvious ETag/304 trap.
- Static job store isolation: every new test uses a Guid-suffixed workspace id.
- Poll self-termination: the terminal render drops `hx-trigger`/`data-poll-trigger`; idiomorph removes the
  attributes on the morph and htmx re-processes the swapped stack (`makeAjaxLoadTask` → `processNode` →
  `initNode` sees a changed attribute hash → `deInitNode` → `cancelPolling`). `applyVisibilityPolling` only
  re-adds `hx-trigger` for elements that still carry `data-poll-trigger`, so it cannot resurrect the poll.

## Judgment calls

1. **`WorkspaceDetailStack.razor` edited though not in the ownership list.** It is the pass-through between the
   endpoint and `WorkspaceDetailPanel` (owned), so the parameter rename could not stop at the panel. Change is
   two lines (`RefreshResult` → `RefreshJob`), no behavior of its own. Plan file-list gap, not a redesign.
2. **`DashboardActivityFeedTests.cs:604` (unowned) updated**, one line: the component parameter is now `Job`
   (a `DashboardRefreshJobStatus`), so the dictionary key changed. Test name and all assertions unchanged.
3. **Cache clear lives in the endpoint's injected func, not the job store.** `DashboardRefreshJobs` stays a
   pure mechanism (run this func once per workspace, in the background) and unit-testable without touching the
   facts cache; `RefreshAndInvalidateFacts` composes the two. Still "clears at job completion" as the plan says.
4. **Running state reuses the existing `neutral` CSS class** rather than a new `.refresh-status.running`:
   `dashboard.css` is Task 10's file, and an unstyled class would render an unmuted label. A job in flight is
   not an outcome, so `neutral` is honest.
5. **Refresh button upgraded to `morph:outerHTML`** (it was plain `outerHTML`): the POST response now carries
   the running state that the poll morphs over, and Task 1's rule is that fragments morph.
6. **A finished-but-unobserved job is replaced on the next `Start`.** Without it, a user who closed the tab
   mid-refresh would get the stale result replayed and no actual refresh. Covered by
   `Start_AfterACompletionNobodyObserved_RunsAFreshRefresh`.

## Concerns / follow-ups (not blockers)

- **Reload during a refresh loses the progress indicator.** `/workspace` and `/fragments/dashboard` render the
  stack without a job status, so a page reload mid-refresh shows no "Refreshing…" and no poll; the job still
  finishes and converges the index, but its result is only rendered if a poll is live. Wiring the page GET to a
  non-consuming peek would fix it — out of Task 9's scope (the plan wires the POST and the status route only).
- **Blank `?workspace_id=`** (hand-typed only; the panel always emits a real id) now hits
  `ArgumentException.ThrowIfNullOrWhiteSpace` in the job store → 500 text via the exception wrapper, where the
  old synchronous handler rendered a `Failed` body. Unregistered-but-non-blank ids still degrade to a `Failed`
  render as before.
- Job entries for a workspace whose result nobody observes linger until the next `Start` for that workspace.
  Bounded by workspace count on a local dashboard; no eviction added deliberately.
- **One unreproduced suite failure, investigated.** A single full-suite run failed (1 of 3584) right after a
  `git commit --amend`; the run was tailed and the test name was lost. It did not reproduce in 16 further runs
  (8 focused × dashboard/refresh subset, 8 full fast suite). The plausible mechanism is mine: the gated fake
  refresh funcs block thread-pool threads by design, and under a loaded pool a 5s wait budget in the test
  helpers could expire (the same box produced a 46s suite run under load, vs 20s typical). Both polling helpers
  now wait up to 30s instead of 5s — they are "wait until", never a speed assertion, so a longer ceiling cannot
  weaken a test; the in-progress POST assertion still proves speed by the gate being unreleased, not by time.
  Re-verified green ×3 after the change. If this ever recurs, capture the test name (`--logger "console;verbosity=detailed"`)
  before assuming it is this.
- `scripts/test.sh` reported 43s once on a loaded machine (build contention); warm re-runs are 20–26s.
