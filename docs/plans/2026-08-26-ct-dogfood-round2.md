# CT Dogfood Round 2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Close the seven round-2 CT dogfood findings (TODO.md "CT dogfood round 2 — 2026-08-26 evening") so CT converges to an honest verdict on a hostile-build-hook repo (Tycho) with zero project-side configuration.

**Architecture:** All fixes land inside existing seams: the ct.db store run-commit path, the daemon poller/queue/host loop, the provider TRX parsing, the tool diagnostic layer, and the CT build-path policy. No new MCP tools, no new processes, no schema changes to julie-owned tables.

**Tech Stack:** .NET 10, xUnit v3, SQLite (Microsoft.Data.Sqlite), warnings-as-errors.

**Architecture Quality:** Main risks: (1) Task 3 adds a bounded idle-drain exception to the load-bearing "Unknown ⟹ nothing executes" rule — modeled on the existing inventory-seed exception, guarded by health + cooldown; (2) Task 8 relayouts the CT build root, which the coordinator's sibling-walk and the Windows path budget both pin. If code reality contradicts the shapes recorded here, workers report a plan mismatch rather than redesigning locally.

## Key orientation facts (verified 2026-08-26 on fa38f826)

- julie-extract's `version_id` is content-keyed (`file_versions` unique on `(path, content_hash, extraction_epoch)`; lookup-before-insert in `julie-extract-artifact/src/store/writer.rs:385-500`). A byte-identical rewrite reuses the row, produces no manifest change, and `store update` on unchanged content is a semantic noop (`store_operations_contract.rs:650` proves generation stays 1). `RevisionDeltaReader.ReadStore` (`src/Miller.Indexing/RevisionDeltaReader.cs:148-219`) compares `version_id` + `observed_content_hash`, both content-derived — **the hash gate already exists on the live store path**. The churn loop is therefore Miller-side: truncation/unavailable handling and the missing idle drain.
- The all-stale collapse has two mechanisms: (a) `CtFactAdapter.Impact` (`src/Miller.Indexing/Testing/CtFactAdapter.cs:78`) uses `limit=100`; a churn-scale seed set returns `TruncatedByLimit`, and `MillerFactImpactSource` (`src/Miller.Testing/Daemon/ContinuousTestRevisionPoller.cs:556-566`) turns that into `Unavailable/"impact_truncated"` — **no enqueue, cursor pinned, interval grows, auto-runs paused** (`CtUnavailableDeltaTracker`, limit 8) — a self-sustaining stall; (b) `ContinuousTestImpactSelector.HasUnaccountedChangedPath` (`src/Miller.Testing/Selection/ContinuousTestImpactSelector.cs:259`) flips the selection to Unknown on the first unmappable changed path. CLAUDE.md already says "Truncated ... impact means Unknown"; the implementation disagrees for case (a).
- On Unknown, `ContinuousTestDaemonQueue.ReconcilePendingOnNoRun` (`ContinuousTestDaemonQueue.cs:293,313-321`) deletes both pendings, and nothing in the idle loop (`ContinuousTestDaemonHost.cs:546-667`, gate at :583 `HasReadyWork`) can ever convert store staleness back into ready work. Owed work only re-enters via an `Impacted` selection (`ContinuousTestImpactSelector.cs:186-196`).
- Red retirement: `StartContinuousTestRun` (`src/Miller.Testing/Store/ContinuousTestStore.Runs.cs:8`, SQL :19-36) unconditionally overwrites state (`red` → `running`); `MarkUnreportedRunCasesStale` (`Runs.cs:355-372`) unconditionally sets `state='stale'` for requested-but-unreported cases. `MarkContinuousTestsStale` (`ContinuousTestStore.cs:425`, CASE arms :456-513) is the documented red-preserving invariant the run path fails to mirror. No completeness comparison exists at `CompleteContinuousTestRun` (`Runs.cs:48`).
- Edit hard-fail: identity assertion throws in the lazy index factory (`src/Miller.Server/Hosting/FreshnessService.cs:396-413`, `src/Miller.Server/Hosting/IndexBootstrapService.cs:1150-1166`); `IndexHolder.CreateLazyState` (`src/Miller.Indexing/IndexHolder.cs:117-133`) uses `LazyThreadSafetyMode.ExecutionAndPublication`, which **caches the exception** — one race poisons the generation until the next swap. `allow_stale` only relaxes the index-vs-disk gate inside `EditService` and cannot help (`EditTool.cs:128-136`). Reopen-loop precedent: `SymbolSearchSidecar.cs:294-312` with `StoreSidecarCatalog.ReadableOpenAttempts = 4` (`StoreSidecarStamp.cs:280`). `FreshnessServicePollNowTests.cs:327-361` pins the current throw and must be rewritten.
- failure_summary: the binding truncation is `ContinuousTestStore.OneLine` (`ContinuousTestStore.cs:767-775`, applied at `Runs.cs:259,303,350`) — first line only, at write time. Dotnet: `TrxFailureSummary` (`DotnetTestProvider.cs:2143-2147`) takes the first `<Message>` and drops `ErrorInfo`/`StackTrace`; `ParseTrxRun` reads `RunInfo` text only when zero case results (`:1716-1721`). Vitest already supplies multi-line messages (`JavaScriptTestProvider.cs:658-686`) that `OneLine` then destroys. Render bound `FailureSummaryMaxBytes = 400` (`TestsCore.cs:297`) is not the problem.
- Pause invisibility: the pause is free text in `CtDaemonStatusRecord.Reason` (`ContinuousTestDaemonHost.cs:655-658`; wording from `CtUnavailableDeltaTracker.Describe` `:82-85`); the record (`CtDaemonProtocol.cs:187-195`) has no pause field; `TestsCore.cs:1226` computes `paused` from lifecycle state only; no `role:ct` log line exists for the transition.
- impact staged: `staged` is fully plumbed (`GitDiffReader.cs:43-44` adds `--cached`); the `empty_git_diff` diagnostic arm emits zero next actions (`ImpactTool.cs:390-416` falls into the empty default).
- Build depth: `<root>/.miller/ct/build/<proj12>/g<hash12>/out/<ProjName>` = 7 levels. Builder `ContinuousTestProjectInventory.Materialize` (`ContinuousTestProjectInventory.cs:292`), tail budget literal `:326-331` (`WindowsPathBudget=260` :314, provider artifact 86 :320), generation ids `CtGenerationPaths.IdForOrdinal` (`CtGenerationPaths.cs:231-240`), validation `ContinuousTestDaemonQueue.ValidateBuildOutputRoot` (`:1218-1232`, containment in `<root>/.miller` — layout-agnostic), sibling-walk `ContinuousTestCoordinator.GenerationContentRoots` (`ContinuousTestCoordinator.cs:711-731`), `out` naming from `-p:ArtifactsBinOutputName=out` + `GenerateProjectSpecificOutputFolder=true` (`DotnetTestProvider.cs:1270-1297`).

## Global Constraints

- `dotnet build Miller.slnx -c Release` must end 0 warnings / 0 errors (TreatWarningsAsErrors).
- `Miller.Core` stays pure logic, zero I/O dependencies.
- `ct.db` stays self-contained: no foreign keys into `symbols.db`/`search.db`; rows name files by path+hash and symbols by name+path.
- Any test that spawns `julie-extract` or a real CT provider toolchain gets `[Trait("Category","Scale")]` and uses `ScaleTestSupport`/`CtProviderTestSupport` helpers; fast tests stay pure.
- ADR-0001: agent-facing nudges are compact-only; machine JSON changes are additive and documented in `docs/contracts/tests-cli-v1.md` before the task closes. Tool `[Description]` budgets untouched. No new MCP tools.
- The load-bearing rule "Truncated/degraded/unavailable impact means Unknown — everything stale, NOTHING executes, never a whole-suite fallback" is amended, not violated: truncation becomes a first-class Unknown (Task 2) and the idle drain (Task 3) is a documented bounded exception mirroring the inventory-seed exception. CLAUDE.md is updated in Task 9 and `scripts/sync-agents.sh` regenerates AGENTS.md.
- Comments per repo/user convention: no narration comments; only non-obvious constraints. Tests carry zero comments.
- No new environment variables unless a task explicitly names one.

## Verification Strategy

**Project source of truth:** CLAUDE.md "Testing" section; `scripts/test.sh`.

**Worker red/green scope:** `dotnet test --filter "FullyQualifiedName~<TestClassName>"` for the classes each task names.

**Worker ceiling:** the focused filter runs above. Workers do not run the fast or Scale suites.

**Worker gate invariant:** each task's acceptance criteria name the behavior its focused tests prove; a task is not done while its named test classes are red.

**Lead affected-change scope:** `scripts/test.sh` (fast suite, Category!=Scale) once per completed batch, not per edit.

**Branch gate:** `scripts/test.sh all` (fast + Scale) once at the end — this campaign touches the CT provider and indexing paths, so Scale is mandatory (skips honestly when toolchains are absent).

**Security scope:** none declared.

**Replay/metric evidence:** none — all gates are hard test gates.

**Escalation triggers:** changes under `src/Miller.Indexing` or `src/Miller.Testing/Providers` require the Scale suite at the branch gate (already planned).

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless the task explicitly says the test pins current-wrong behavior and must be rewritten (Tasks 1, 2, 4 name such tests).

**Verification ledger:** the lead records command, scope, SHA, result per batch in the final report; a green scope on an unchanged tree is cited, never rerun.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Red survives unexecuted runs | Batch A | `src/Miller.Testing/Store/ContinuousTestStore.Runs.cs`, `src/Miller.Testing/Store/CtSchema.cs` (only if a column is added), `src/Miller.Testing/ContinuousTestStoreApplier.cs`, `tests/Miller.Tests/Testing/Store/Core/ContinuousTestStoreTests.cs` | No | None - safe parallel batch. |
| Task 2: Truncated impact is Unknown | Batch A | `src/Miller.Testing/Daemon/ContinuousTestRevisionPoller.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestRevisionPollerTests.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/CtStickyUnavailableDeltaTests.cs`, `tests/Miller.Tests/Indexing/RevisionDeltaReaderTests.cs` | No | None - safe parallel batch. |
| Task 4: edit survives generation movement | Batch A | `src/Miller.Server/Hosting/FreshnessService.cs`, `src/Miller.Server/Hosting/IndexBootstrapService.cs`, `src/Miller.Indexing/IndexHolder.cs`, `src/Miller.Server/Tools/ToolDiagnostic.cs`, `tests/Miller.Tests/Server/FreshnessServicePollNowTests.cs`, `tests/Miller.Tests/Server/ToolDiagnosticTests.cs`, `tests/Miller.Tests/Indexing/IndexHolderTests.cs` (create if absent) | No | None - safe parallel batch. |
| Task 7: impact suggests staged=true | Batch A | `src/Miller.Server/Tools/ImpactTool.cs`, `src/Miller.Server/Git/GitDiffReader.cs` (only if a probe helper is added), `tests/Miller.Tests/Server/ImpactToolTests.cs`, `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` (impact rows only) | No | None - safe parallel batch. |
| Task 3: Idle owed-backlog drain | Batch B | `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs`, `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs`, `docs/continuous-testing.md` (drain section), `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestIdleDrainTests.cs` (create), `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestDebouncedAutoRunTests.cs` | Yes | Consumes Task 2's Unknown-outcome semantics; Batch B starts after Batch A lands. |
| Task 5: failures keeps the real error | Batch B | `src/Miller.Testing/Store/ContinuousTestStore.cs` (`OneLine` region), `src/Miller.Testing/Providers/Dotnet/DotnetTestProvider.cs` (TRX parse region :1600-2200 only), `src/Miller.Testing/Providers/Node/JavaScriptTestProvider.cs` (summary region only), `docs/contracts/tests-cli-v1.md` (failure_summary rows), `tests/Miller.Tests/Server/TestsFailuresOutputTests.cs`, `tests/Miller.Tests/Testing/Providers/Dotnet/DotnetTestProviderTests.cs` | Yes | Task 1 edits `ContinuousTestStore.Runs.cs` call sites this task touches; Batch B starts after Batch A lands. |
| Task 6: pause visible in tests status | Batch C | `src/Miller.Testing/Daemon/CtDaemonProtocol.cs`, `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs` (status-publish + log sites), `src/Miller.Server/Tools/TestsCore.cs` (status region), `docs/contracts/tests-cli-v1.md` (daemon rows), `tests/Miller.Tests/Testing/Daemon/Engine/CtStickyUnavailableDeltaTests.cs`, `tests/Miller.Tests/Server/TestsToolTests.cs`, `tests/Miller.Tests/Server/Cli/TestsCliTests.cs` | Yes | Task 3 edits `ContinuousTestDaemonHost.cs`; Task 5 edits `TestsCore.cs`-adjacent render tests; Batch C starts after Batch B lands. |
| Task 8: CT build path ≤5 levels | Batch C | `src/Miller.Testing/Daemon/ContinuousTestProjectInventory.cs`, `src/Miller.Testing/Daemon/ContinuousTestCoordinator.cs`, `src/Miller.Testing/Providers/Shared/CtGenerationPaths.cs` (only if needed), `docs/continuous-testing.md` (layout rows), `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProjectInventoryTests.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/BuildOutputRootValidationTests.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/CtBuildCacheJanitorTests.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/CtBuildCacheMaintenanceTests.cs` | Yes | Task 5 owns `DotnetTestProvider.cs` in Batch B; this task may touch its build-command region (:1270-1300), so it waits for Batch C. |
| Task 9: docs, CLAUDE.md sync, TODO, branch gate | None - serial | `CLAUDE.md`, `AGENTS.md` (generated), `TODO.md`, `docs/continuous-testing.md` (final pass), `.memories/` checkpoint | Yes | Needs every prior task's landed behavior to document; runs last. |

Commit mode: **serial-worker-commit within a batch is forbidden** — this campaign uses `parallel-lead-commit`: workers hand verified diffs; the lead reviews inline, stages the task's owned files, and commits per task.

---

### Task 1: Red survives requested-but-unexecuted runs (finding 1)

**Files:**
- Modify: `src/Miller.Testing/Store/ContinuousTestStore.Runs.cs` (`StartContinuousTestRun` :8-36, `CompleteContinuousTestRun` :48-100, `MarkUnreportedRunCasesStale` :355-372)
- Modify (only if the chosen design needs a column): `src/Miller.Testing/Store/CtSchema.cs`
- Modify: `src/Miller.Testing/ContinuousTestStoreApplier.cs` (`CompleteRun` :91 — bounded diagnostic)
- Test: `tests/Miller.Tests/Testing/Store/Core/ContinuousTestStoreTests.cs`

**Interfaces:**
- Consumes: `MarkContinuousTestsStale`'s red-preserving CASE arms (`ContinuousTestStore.cs:456-513`) as the invariant to mirror.
- Produces: run-commit semantics other tasks may rely on — a requested-but-unreported case that was red before the run stays `state='red'`, keeps its committed `index_identity`/`revision`, and carries a stamped `stale_since_revision` (owed rerun); a non-red unreported case still retires to `stale`. `CompleteContinuousTestRun` exposes the unreported-case count to its caller.

**Contract inputs:** the invariant text in CLAUDE.md ("An impacted RED keeps its state string and committed key on EVERY staling path"); existing tests `ContinuousTestStoreTests.cs:252,283,307` (red-preserving staling) and `:412` (current unreported behavior, seeded stale — extend with a red-seeded sibling, do not delete).

**File ownership:** `src/Miller.Testing/Store/ContinuousTestStore.Runs.cs`, `src/Miller.Testing/Store/CtSchema.cs` (only if a column is added), `src/Miller.Testing/ContinuousTestStoreApplier.cs`, `tests/Miller.Tests/Testing/Store/Core/ContinuousTestStoreTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** The run path must obey the same red-preservation invariant as `MarkContinuousTestsStale`. Today `StartContinuousTestRun` overwrites `red` with `running`, and `MarkUnreportedRunCasesStale` retires the case to `stale` at commit, so a red the provider filter silently dropped (the SkiaSharp case) loses its verdict without ever executing.

**Approach:** Pick the design after inspecting: either (a) capture the pre-run state (a `pre_run_state` column written by `StartContinuousTestRun`, cleared by every commit path), or (b) reconstruct red from `last_result_status='failed'` in `MarkUnreportedRunCasesStale`'s UPDATE. Prefer (b) if it is honest for every seeding path (a case whose last executed result failed and never passed since is legitimately still red); fall back to (a) if inspection finds a path where `last_result_status='failed'` coexists with a legitimately non-red state. The restored red keeps `index_identity`/`revision` (committed key) and gets `stale_since_revision` stamped once (owed), exactly like the `MarkContinuousTestsStale` CASE arms. Add the requested-vs-reported set difference count to the completion path and log one bounded `role:ct` diagnostic from the applier when it is nonzero (the `running_run_id = $run` predicate at commit time IS that set). ct.db schema changes, if any, follow the existing `CtSchema` versioning pattern.

**Acceptance criteria:**
- [x] A red case selected into a run and absent from results is still `red` after `CompleteContinuousTestRun`, with its committed key intact and `stale_since_revision` stamped.
- [x] A green/stale case absent from results still retires to `stale` (existing `:412` behavior preserved for non-red seeds).
- [x] An executed red that passes goes green; an executed red that fails stays red — no regression in `CommitFreshResult`/`PreserveStaleResult` paths.
- [x] A nonzero unreported count logs one bounded diagnostic naming the count and run id.
- [x] Focused scope green: `dotnet test --filter "FullyQualifiedName~ContinuousTestStoreTests"`; diff handed to lead per commit mode.

### Task 2: Truncated impact becomes a first-class Unknown (finding 2, stall half)

**Files:**
- Modify: `src/Miller.Testing/Daemon/ContinuousTestRevisionPoller.cs` (`MillerFactImpactSource.ReadAttempt` :512-566)
- Test: `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestRevisionPollerTests.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/CtStickyUnavailableDeltaTests.cs`, `tests/Miller.Tests/Indexing/RevisionDeltaReaderTests.cs`

**Interfaces:**
- Consumes: `ContinuousTestImpactSelector.SelectAtRevision`'s existing `truncated ⟹ Unknown` arm (`ContinuousTestImpactSelector.cs:165-180`) — already correct, unchanged.
- Produces: a churn-scale delta reaches the selector as a truncated-but-readable delta instead of `Unavailable/"impact_truncated"`. Consequences relied on by Task 3: the Unknown outcome flows through `EnqueueCore` → `ApplyRevisionAdvance(Unknown)`, so staleness is applied, the poller saves its cursor, intervals stop growing, and `CtUnavailableDeltaTracker` no longer pauses auto-runs for truncation.

**Contract inputs:** CLAUDE.md rule "Truncated/degraded/unavailable impact means Unknown — everything stale, NOTHING executes"; the cursor invariant ("never save its cursor past an interval whose staleness was not applied" — an Unknown advance that reaches `ApplyRevisionAdvance` applies staleness, so saving is legal); `moving_cursor` and store read errors stay `Unavailable`.

**File ownership:** `src/Miller.Testing/Daemon/ContinuousTestRevisionPoller.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestRevisionPollerTests.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/CtStickyUnavailableDeltaTests.cs`, `tests/Miller.Tests/Indexing/RevisionDeltaReaderTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** When `CtFactAdapter.Impact` reports `TruncatedByLimit`, `MillerFactImpactSource` currently answers `Unavailable/"impact_truncated"`: no enqueue happens, the cursor never advances, the interval grows every poll, and after 8 misses the tracker pauses auto-runs — the observed permanent stall. The stated product rule says truncation means Unknown. Align the implementation: deliver the delta with a truncated marker so the selector's existing Unknown arm fires.

**Approach:** Change the truncation branch to return a successful read whose result carries the truncation hint the selector already consumes (`truncated`/`unmappableHint` inputs to `SelectAtRevision`) instead of `Unavailable`. Keep `moving_cursor`, identity drift, and store read failures as `Unavailable` — those are genuinely unreadable. Rewrite the sticky-unavailable test for truncation (`CtStickyUnavailableDeltaTests` — truncation no longer counts toward the pause) while keeping the pause behavior for real unavailability. Add one regression test in `RevisionDeltaReaderTests` pinning the now-load-bearing hash gate: a store fixture where a file's manifest entry is identical across generations (same `version_id`, same `observed_content_hash`) yields no changed path for that file.

**Acceptance criteria:**
- [x] A `TruncatedByLimit` impact read produces an Unknown selection that reaches `ApplyRevisionAdvance` (staleness applied, watermarks cleared) and the poller saves its cursor to the interval end.
- [x] Truncation no longer increments the unavailable streak; `moving_cursor` still does and still pauses after the limit.
- [x] Byte-identical manifest entries produce no changed path (regression pin on `ReadStore`).
- [x] Focused scope green: `dotnet test --filter "FullyQualifiedName~ContinuousTestRevisionPollerTests|FullyQualifiedName~CtStickyUnavailableDeltaTests|FullyQualifiedName~RevisionDeltaReaderTests"`; diff handed to lead.

### Task 3: Idle owed-backlog drain (finding 2, convergence half; observation 9)

**Files:**
- Modify: `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs` (main loop :546-667)
- Modify: `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs` (backfill mint; `ReconcilePendingOnNoRun` :293-321 reviewed, not necessarily changed)
- Modify: `docs/continuous-testing.md` (drain behavior)
- Create: `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestIdleDrainTests.cs`
- Test: `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestDebouncedAutoRunTests.cs`

**Interfaces:**
- Consumes: Task 2's guarantee that churn resolves to an applied Unknown with a saved cursor (so "settled" is detectable); the existing backfill pending machinery (`EnqueueCore` :251-261) and the stale-set selection the explicit-run path uses.
- Produces: an idle daemon with staleness in the store eventually schedules ONE backfill drain per settled state: guards are (a) queue empty, (b) last poll healthy and cursor at the live revision, (c) quiet for at least the debounce window, (d) a 5-minute per-context cooldown between idle drains (constant, no new env var), (e) auto-runs not paused. The drain executes the stale/owed set as an explicit test-ID list (stamped reds included), like an explicit run's stale set — it is never a blind whole-suite run, even when the stale set happens to span every case.

**Contract inputs:** the inventory-seed exception (`ContinuousTestDaemonQueue.TryEnqueueInventorySeed`) as the precedent for a bounded exception to "Unknown executes nothing"; the user-global execution budget (one workspace executes at a time) which the drain must respect like any run.

**File ownership:** `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs`, `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs`, `docs/continuous-testing.md` (drain section), `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestIdleDrainTests.cs` (create), `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestDebouncedAutoRunTests.cs`

**Serialization required:** Yes

**Dependency reason:** Consumes Task 2's Unknown-outcome semantics; Batch B starts after Batch A lands.

**What to build:** After a churn window resolves to Unknown, the store holds a large stale set, the queue is empty, and nothing ever converts that staleness back into work — CT can never converge to green without a human typing `tests run`. Add an idle drain: when the daemon has been idle and healthy long enough, it runs the stale backlog once, exactly as an explicit run would select it.

**Approach:** Add the drain decision as a small pure policy (a method or type the loop consults each tick with: queue emptiness, stale count from the store, poller health + cursor/live-revision equality, last-activity timestamp, last-drain timestamp). When it fires, mint a backfill pending through the existing queue path with the stale/owed explicit ID selection. The cooldown plus Task 2's cursor advance bounds the loop: a drain whose own build re-stales cases fires again at most once per cooldown, and a byte-identical rebuild (the julie noop) re-stales nothing. Do not change `ReconcilePendingOnNoRun`'s Unknown-drop unless tests prove it fights the drain — the drain replaces the lost pendings by design. Existing debounce tests (`An_unknown_selection_never_becomes_ready_work_even_after_the_quiet_period`) stay green: the Unknown selection itself still never becomes ready work; the drain is a NEW selection made later under healthy conditions. Update `docs/continuous-testing.md` with the drain behavior and its guards.

**Acceptance criteria:**
- [x] With staleness in the store, an empty queue, healthy poller, quiet ≥ debounce, and no recent drain, one backfill run is scheduled and executes the stale set as explicit IDs.
- [x] No drain while: a run executes, the poller is unhealthy/behind, auto-runs are paused, or the cooldown has not elapsed.
- [x] Two consecutive drains require the cooldown between them (loop-bound proof).
- [x] Existing debounce/Unknown tests green unmodified except where they pin the old dead-end (each such edit named in the worker report).
- [x] Focused scope green: `dotnet test --filter "FullyQualifiedName~ContinuousTestIdleDrainTests|FullyQualifiedName~ContinuousTestDebouncedAutoRunTests"`; diff handed to lead.

### Task 4: edit survives index-generation movement (finding 3)

**Files:**
- Modify: `src/Miller.Server/Hosting/FreshnessService.cs` (`LoadPinnedStoreIndex` :396-413)
- Modify: `src/Miller.Server/Hosting/IndexBootstrapService.cs` (lazy factory :1150-1166)
- Modify: `src/Miller.Indexing/IndexHolder.cs` (`CreateLazyState` :117-133, `Current` :46)
- Modify: `src/Miller.Server/Tools/ToolDiagnostic.cs` (`FromException` switch :96-137)
- Test: `tests/Miller.Tests/Server/FreshnessServicePollNowTests.cs` (rewrite :327-361), `tests/Miller.Tests/Server/ToolDiagnosticTests.cs`, IndexHolder coverage (create `tests/Miller.Tests/Indexing/IndexHolderTests.cs` if no suitable home exists)

**Interfaces:**
- Consumes: the bounded reopen precedent `SymbolSearchSidecar.cs:294-312` with `StoreSidecarCatalog.ReadableOpenAttempts`.
- Produces: `IndexHolder.Current` no longer replays a cached factory exception forever — a faulted lazy state is discarded so the next read rebuilds; the lazy load retries against the CURRENT generation up to the attempt bound; if it still fails, the tool result classifies as `Unavailable` with a plain-English message and a retry next step, not `internal_failure`.

**Contract inputs:** the pinned-read rule (a bounded cache never advances onto a newer generation) governs fact reads within one session, not which generation a fresh materialization loads — re-resolving to current on retry is consistent; `ToolDiagnostic` message-keyed narrowing precedent at :130-133 (`IsWorkspaceSelectorMistake`).

**File ownership:** `src/Miller.Server/Hosting/FreshnessService.cs`, `src/Miller.Server/Hosting/IndexBootstrapService.cs`, `src/Miller.Indexing/IndexHolder.cs`, `src/Miller.Server/Tools/ToolDiagnostic.cs`, `tests/Miller.Tests/Server/FreshnessServicePollNowTests.cs`, `tests/Miller.Tests/Server/ToolDiagnosticTests.cs`, `tests/Miller.Tests/Indexing/IndexHolderTests.cs` (create if absent)

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** A background CT build promotes generations while an agent edits; the lazy index factory asserts the generation it captured at swap time still matches, throws on mismatch, and `Lazy` (ExecutionAndPublication) caches that exception — so three consecutive `edit apply=true` calls failed with jargon and no way out. Make the load self-heal and the residual failure honest.

**Approach:** Three layers. (1) In both factory sites, replace the single-shot identity assertion with a bounded loop (reuse `ReadableOpenAttempts`): on mismatch, re-resolve the current generation/identity and reload; throw only after exhaustion, with a message naming the retry count. (2) In `IndexHolder`, stop memoizing failure: when `Index.Value` throws, discard that lazy state (swap in a fresh lazy for the same snapshot under the existing lock discipline) so the next access re-runs the factory; keep ExecutionAndPublication for the success path. (3) In `ToolDiagnostic.FromException`, add a message-keyed arm (helper like the existing `IsWorkspaceSelectorMistake`) classifying this condition as `Unavailable("index_reloading", ...)` with a plain message — "The index was replaced while loading. Retry the call; the next attempt loads the new index." — and a retry next action. Rewrite `PollNow_FamilyStoreLazyLoadRejectsANewerGenerationThanItsMetadata` to assert the new behavior (retry succeeds when the newer generation is loadable).

**Acceptance criteria:**
- [x] A generation swap between session open and lazy load no longer fails the call when the new generation is loadable (retry proof at the factory level).
- [x] A faulted load does not poison the holder: after a failing factory run, a subsequent access re-runs the factory and can succeed.
- [x] Exhausted retries classify as `Unavailable` with the plain-English message and a retry action — never `internal_failure`.
- [x] Focused scope green: `dotnet test --filter "FullyQualifiedName~FreshnessServicePollNowTests|FullyQualifiedName~ToolDiagnosticTests|FullyQualifiedName~IndexHolderTests"`; diff handed to lead.

### Task 5: failures keeps the first real error line (finding 4)

**Files:**
- Modify: `src/Miller.Testing/Store/ContinuousTestStore.cs` (`OneLine` :767-775 → bounded error-aware summarizer)
- Modify: `src/Miller.Testing/Providers/Dotnet/DotnetTestProvider.cs` (TRX parse region only: `TrxFailureSummary` :2143-2147, `ParseTrxRun` :1705-1721)
- Modify: `src/Miller.Testing/Providers/Node/JavaScriptTestProvider.cs` (summary region, only if needed)
- Modify: `docs/contracts/tests-cli-v1.md` (failure_summary description)
- Test: `tests/Miller.Tests/Server/TestsFailuresOutputTests.cs`, `tests/Miller.Tests/Testing/Providers/Dotnet/DotnetTestProviderTests.cs`

**Interfaces:**
- Consumes: Task 1's landed `Runs.cs` (the three summary write sites :259/:303/:350 may have moved — re-locate, do not assume line numbers).
- Produces: `failure_summary` as persisted contains the first error-shaped line of the underlying output (assertion text, exception line, or run-level error like the StaticWebAssets message), bounded to 400 bytes at write time; the render layer's 400-byte bound and `error_class` derivation are unchanged.

**Contract inputs:** `FailureSummaryMaxBytes = 400` (`TestsCore.cs:297`) as the byte bound to mirror at write time (share or duplicate the constant — `Miller.Testing` must not reference `Miller.Server`); `IsErrorLine` shape (`DotnetTestProvider.cs:1501-1504`) as the error-line heuristic precedent; `DeriveErrorClass` (`TestsCore.cs:567-576`) must still classify the widened summaries.

**File ownership:** `src/Miller.Testing/Store/ContinuousTestStore.cs` (`OneLine` region), `src/Miller.Testing/Providers/Dotnet/DotnetTestProvider.cs` (TRX parse region :1600-2200 only), `src/Miller.Testing/Providers/Node/JavaScriptTestProvider.cs` (summary region only), `docs/contracts/tests-cli-v1.md` (failure_summary rows), `tests/Miller.Tests/Server/TestsFailuresOutputTests.cs`, `tests/Miller.Tests/Testing/Providers/Dotnet/DotnetTestProviderTests.cs`

**Serialization required:** Yes

**Dependency reason:** Task 1 edits `ContinuousTestStore.Runs.cs` call sites this task touches; Batch B starts after Batch A lands.

**What to build:** The store's `OneLine` keeps only the first line of any summary at write time, so "OneTimeSetUp: dotnet failed." survives while the actual StaticWebAssets error is discarded before any renderer can show it. Widen capture at the provider and stop destroying it at the store.

**Approach:** (1) Replace `OneLine` with a summarizer that keeps the first line PLUS the first later error-shaped line (contains an exception type, `error`, or assertion marker) joined with ` | `, bounded to 400 bytes — first line alone when nothing later qualifies. (2) Dotnet TRX: `TrxFailureSummary` additionally scans the result's `ErrorInfo`/`StackTrace`/sibling `Message` text for the first error-shaped line and appends it; `ParseTrxRun` folds `RunInfo` error text into each failed case's summary when both case results AND a run-level error exist (today the run text is read only when zero cases parsed). (3) Vitest already supplies multi-line messages; verify the new summarizer surfaces its assertion line and only touch `JavaScriptTestProvider` if a fixture proves a gap. Fixture-based tests: a TRX with an NUnit OneTimeSetUp failure whose StaticWebAssets line sits below the banner must yield a `failure_summary` containing `StaticWebAssets`; existing 400-byte and `error_class` tests stay green.

**Acceptance criteria:**
- [x] The OneTimeSetUp fixture's persisted `failure_summary` contains the real error line, within 400 bytes.
- [x] A single-line summary is unchanged; multi-line summaries keep first line + first error-shaped line.
- [x] `error_class` grouping still classifies the widened summaries (no `unclassified` regressions in existing fixtures).
- [x] Contract doc updated to describe the two-part summary.
- [x] Focused scope green: `dotnet test --filter "FullyQualifiedName~TestsFailuresOutputTests|FullyQualifiedName~DotnetTestProviderTests"`; diff handed to lead.

### Task 6: pause state visible in tests status (finding 5)

**Files:**
- Modify: `src/Miller.Testing/Daemon/CtDaemonProtocol.cs` (`CtDaemonStatusRecord` :187-195 — trailing optional fields)
- Modify: `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs` (status publish :646-658; one `role:ct` log line on pause enter/clear near the tracker feed :1143-1164)
- Modify: `src/Miller.Server/Tools/TestsCore.cs` (`TestsStatusResult`, JSON writer :1221-1227, compact render :1272-1284)
- Modify: `docs/contracts/tests-cli-v1.md` (daemon rows :62-110)
- Test: `tests/Miller.Tests/Testing/Daemon/Engine/CtStickyUnavailableDeltaTests.cs`, `tests/Miller.Tests/Server/TestsToolTests.cs`, `tests/Miller.Tests/Server/Cli/TestsCliTests.cs`

**Interfaces:**
- Consumes: `CtUnavailableDeltaTracker.StuckReason`/`Describe` (`CtUnavailableDeltaTracker.cs:50,82-85`) as the pause source; Task 2's change (truncation no longer pauses — the surviving pause reasons are real unavailability).
- Produces: `CtDaemonStatusRecord` carries `AutoRunsPaused` (bool) and `PauseReason` (code string) as trailing optional fields; `tests status` JSON gains `daemon.auto_runs_paused` + `daemon.pause_reason` (additive, documented); compact output prints `auto-runs paused: <reason>` while paused; one `role:ct` line logs each pause enter/clear transition.

**Contract inputs:** the loop-stall precedent (`loop_stalled`/`loop_stall_seconds` as derived additive fields, `tests-cli-v1.md:109-110`); absence of the new fields in an old record means unknown, never paused; `daemon.paused` keeps its lifecycle meaning and its doc row is clarified to distinguish the two.

**File ownership:** `src/Miller.Testing/Daemon/CtDaemonProtocol.cs`, `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs` (status-publish + log sites), `src/Miller.Server/Tools/TestsCore.cs` (status region), `docs/contracts/tests-cli-v1.md` (daemon rows), `tests/Miller.Tests/Testing/Daemon/Engine/CtStickyUnavailableDeltaTests.cs`, `tests/Miller.Tests/Server/TestsToolTests.cs`, `tests/Miller.Tests/Server/Cli/TestsCliTests.cs`

**Serialization required:** Yes

**Dependency reason:** Task 3 edits `ContinuousTestDaemonHost.cs`; Task 5 edits `TestsCore.cs`-adjacent render tests; Batch C starts after Batch B lands.

**What to build:** The auto-run pause lives only as free text in the record's non-normative `Reason` field, so `tests status` printed `daemon: running (idle)` while the daemon had paused auto-runs for 6 minutes, and the daily log never mentioned it. Make the pause a first-class, documented status fact with a log trail.

**Approach:** Publish `AutoRunsPaused`/`PauseReason` from the host wherever `StuckReason` is non-null (primary record; adopted-worktree records get the fields when their next transition write happens — a worktree reader judging the family daemon's record is the documented pattern). Surface through `ReadLiveStatus` → `TestsStatusResult` → JSON + compact. Log one `Diagnostic` line on enter (with reason) and one on clear. Update the contract doc: new fields, the clarified `daemon.paused` row, a sample payload.

**Acceptance criteria:**
- [x] While the tracker reports stuck, `tests status --json` carries `daemon.auto_runs_paused: true` and the reason code; compact prints the pause line; both clear when the tracker clears.
- [x] An old record without the fields reads as not-paused/unknown, never crashes.
- [x] Pause enter and clear each write exactly one `role:ct` log line.
- [x] Contract doc documents the fields and the lifecycle-vs-auto-run distinction.
- [x] Focused scope green: `dotnet test --filter "FullyQualifiedName~CtStickyUnavailableDeltaTests|FullyQualifiedName~TestsToolTests|FullyQualifiedName~TestsCliTests"`; diff handed to lead.

### Task 7: impact suggests staged=true on a staged-only diff (finding 6)

**Files:**
- Modify: `src/Miller.Server/Tools/ImpactTool.cs` (empty-diff branch :183-233, `ImpactEmptyDiagnostic` :373-427)
- Modify (only if a probe helper is cleaner there): `src/Miller.Server/Git/GitDiffReader.cs`
- Test: `tests/Miller.Tests/Server/ImpactToolTests.cs`, `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` (impact rows only)

**Interfaces:**
- Consumes: `GitDiffRequest(root, baseRef, staged)` (`GitDiffReader.cs:6`, `--cached` at :43-44) — already plumbed.
- Produces: when an unstaged/base diff is empty and `staged` was not requested, the tool probes the staged diff; if staged changes exist, the empty-diff diagnostic message says so and `next_actions` gains a normal (JSON-visible, not compact-only) action naming the same call with `staged=true`. No staged changes ⟹ behavior unchanged.

**Contract inputs:** this is a same-tool parameter correction, NOT a cross-tool handoff — it does not go through `CrossToolHandoff` and is not `CompactOnly`, so JSON consumers see it; existing exact-message tests (`ImpactToolTests.cs:2091,2104,2195`, `CliDispatchTests.cs:3353`) pin the current diagnostic and are updated deliberately where the new conditional line applies.

**File ownership:** `src/Miller.Server/Tools/ImpactTool.cs`, `src/Miller.Server/Git/GitDiffReader.cs` (only if a probe helper is added), `tests/Miller.Tests/Server/ImpactToolTests.cs`, `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` (impact rows only)

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** `impact git=true` with a staged-only working tree reports "git diff is empty" and stops — the agent has no clue `staged=true` exists. Probe and say so, only when it is true.

**Approach:** In the empty-diff branch, when `staged` is false, run one extra `GitDiffReader.Read` with `Staged=true` (bounded, only on the already-empty path); pass a `stagedChangesExist` flag into `ImpactEmptyDiagnostic`; the `empty_git_diff` arm emits the conditional action and extends the message ("Staged changes exist; retry with staged=true." on the MCP path, the CLI flag spelling on the CLI path). MCP and CLI mirrors both covered by tests.

**Acceptance criteria:**
- [x] Staged-only tree: the empty-diff diagnostic names staged changes and `diagnostic.next_actions` carries the staged retry in JSON.
- [x] Genuinely empty tree (nothing staged): output byte-identical to today.
- [x] `staged=true` path itself unchanged.
- [x] Focused scope green: `dotnet test --filter "FullyQualifiedName~ImpactToolTests|FullyQualifiedName~CliDispatchTests"`; diff handed to lead.

### Task 8: CT build path ≤5 levels below the workspace root (finding 7)

**Files:**
- Modify: `src/Miller.Testing/Daemon/ContinuousTestProjectInventory.cs` (`Materialize` :266-299, tail budget :314-367)
- Modify: `src/Miller.Testing/Daemon/ContinuousTestCoordinator.cs` (`GenerationContentRoots` :711-731 and sibling scans :649,:670,:1146)
- Modify (only if generation/cache shapes must move): `src/Miller.Testing/Providers/Shared/CtGenerationPaths.cs`
- Modify: `docs/continuous-testing.md` (:116, :230 layout rows)
- Test: `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProjectInventoryTests.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/BuildOutputRootValidationTests.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/CtBuildCacheJanitorTests.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/CtBuildCacheMaintenanceTests.cs`

**Interfaces:**
- Consumes: `ValidateBuildOutputRoot`'s containment-in-`.miller` check (layout-agnostic, unchanged); `CtGenerationPaths.For` subdir shapes; `-p:ArtifactsBinOutputName=out` + `GenerateProjectSpecificOutputFolder=true` (test assembly at `<gen>/out/<ProjName>` — required because `--artifacts-path` hosts referenced projects' outputs too, so the per-project level cannot be dropped).
- Produces: the per-project build root becomes `<workspace>/.miller/ct-<proj12>` (was `.miller/ct/build/<proj12>`), so the deepest assembly dir is `.miller`(1)/`ct-<proj12>`(2)/`g<hash12>`(3)/`out`(4)/`<ProjName>`(5) — 5 levels. The Windows tail budget is recomputed from the new literal (root budget grows). The over-budget temp fallback shape is unchanged. The janitor also sweeps the legacy `<workspace>/.miller/ct/build` tree when no live process holds it, so old generations are reclaimed once.

**Contract inputs:** `WindowsPathBudget = 260`, `LongestProviderArtifactNameLength = 86`; `ContinuousTestProjectInventoryTests.cs:48-70` asserts the composed longest path EQUALS the budget — update that expected value deliberately with the new tail; `GenerationContentRoots` currently enumerates siblings of the build root's parent as peer build roots — with the parent now `.miller`, it must filter to the `ct-` prefix (and keep `HoldsGenerationContent` as the content check) so it never treats `logs/`, `ct/`, or sidecar files as peer roots.

**File ownership:** `src/Miller.Testing/Daemon/ContinuousTestProjectInventory.cs`, `src/Miller.Testing/Daemon/ContinuousTestCoordinator.cs`, `src/Miller.Testing/Providers/Shared/CtGenerationPaths.cs` (only if needed), `docs/continuous-testing.md` (layout rows), `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProjectInventoryTests.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/BuildOutputRootValidationTests.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/CtBuildCacheJanitorTests.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/CtBuildCacheMaintenanceTests.cs`

**Serialization required:** Yes

**Dependency reason:** Task 5 owns `DotnetTestProvider.cs` in Batch B; this task may touch its build-command region (:1270-1300), so it waits for Batch C.

**What to build:** Tests that walk up from their own assembly to find the repo root commonly cap at 8 ascents and burn one on `AppContext.BaseDirectory`'s trailing separator; at 7 levels deep they stop one short and go red only under CT. Flatten to 5 levels so the cap-8 pattern clears with margin, with zero project-side configuration.

**Approach:** Change only the build-root prefix in `Materialize`: `Path.Combine(root, ".miller", "ct-" + ShortSegment(project.Id))`. Everything below (generation dirs, `out`, cache sibling, markers) keeps its shape, so `CtGenerationPaths` likely needs no change. Recompute `WorkspaceLocalTailLength` from the new literal and update the doc comment. Fix `GenerationContentRoots` and every sibling scan to filter on the `ct-` prefix under `.miller`. Extend the janitor to sweep `<root>/.miller/ct/build` (legacy location) under the same no-live-process rules, so upgraded workspaces do not strand ~635MB generations. Watcher/extractor invisibility holds automatically (`.miller` is excluded wholesale — `WatchPathFilter.cs:58`, `JulieIgnoreSeeder.cs:94`). Update `docs/continuous-testing.md` and note the depth guarantee (≤5 levels) as a documented property.

**Acceptance criteria:**
- [x] The composed deepest assembly directory sits exactly 5 levels below the workspace root, and a test pins the depth (count of separators), not just the string.
- [x] The budget-equality test passes with the recomputed tail; the over-budget fallback still triggers at the new threshold.
- [x] Peer-root enumeration never treats non-`ct-` entries under `.miller` as build roots.
- [x] The janitor reclaims a fixture legacy `.miller/ct/build/<proj>` generation when idle, and never while a marker/process holds it.
- [x] Docs updated (`docs/continuous-testing.md`); the CLAUDE.md layout line is queued for Task 9.
- [x] Focused scope green: `dotnet test --filter "FullyQualifiedName~ContinuousTestProjectInventoryTests|FullyQualifiedName~BuildOutputRootValidationTests|FullyQualifiedName~CtBuildCacheJanitorTests|FullyQualifiedName~CtBuildCacheMaintenanceTests"`; diff handed to lead.

### Task 9: docs, CLAUDE.md sync, TODO, branch gate

**Files:**
- Modify: `CLAUDE.md` (CT section: build-root layout line, truncation-means-Unknown alignment, idle-drain bounded exception, red-preservation rule extension to the run path, pause fields)
- Generate: `AGENTS.md` via `scripts/sync-agents.sh`
- Modify: `TODO.md` (round-2 findings statuses)
- Modify: `docs/continuous-testing.md` (final consistency pass)
- Create: `.memories/` checkpoint (before the final commit)

**Interfaces:**
- Consumes: every landed task's actual behavior (document what shipped, not what was planned).
- Produces: docs that match the code; the branch-gate verification ledger.

**Contract inputs:** `cmp -s CLAUDE.md AGENTS.md` must pass after sync; the pre-commit hook enforces it.

**File ownership:** `CLAUDE.md`, `AGENTS.md` (generated), `TODO.md`, `docs/continuous-testing.md` (final pass), `.memories/` checkpoint

**Serialization required:** Yes

**Dependency reason:** Needs every prior task's landed behavior to document; runs last.

**What to build:** Bring the load-bearing docs in line with the shipped behavior, mark the round-2 findings addressed in TODO.md (awaiting merge approval), checkpoint, and run the branch gate.

**Approach:** Edit CLAUDE.md only, run `scripts/sync-agents.sh`, verify with `cmp`. Update TODO.md round-2 entries to done-with-one-line-evidence, keeping observation 8 (under-selection watch) open. Run `scripts/test.sh` and `scripts/test.sh all`; record the ledger. Goldfish checkpoint before the final commit.

**Acceptance criteria:**
- [ ] `cmp -s CLAUDE.md AGENTS.md` passes; docs name the new layout, drain, and pause facts.
- [ ] TODO.md round-2 section shows per-finding status.
- [ ] Branch gate green: fast suite + Scale suite (or honest skips), ledger recorded.
- [ ] Checkpoint saved and included in the final commit.
