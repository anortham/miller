# CT agent usefulness implementation plan

**Status — 2026-09-08:** Source implementation, corrective dogfood work and native provider checks are recorded in the [CT verification ledger](../findings/2026-09-08-agent-usefulness-dogfood.md#continuous-testing). Provider scopes, skipped tests and outstanding integration checks remain explicit there; Windows and release completion are not claimed. The checklist below preserves the original acceptance criteria; use the ledger for current verified and outstanding status.

> **For agentic workers:** Use `razorback:subagent-driven-development` for independent tasks, or `razorback:executing-plans` for serial execution. This document is a task-level plan. Implementation proceeded in the later user-directed dogfood session; this plan is not release approval.

**Goal:** Make CT's first run diagnosable, preserve request identity through waiting, avoid work whose result is already unusable, and return usable runner instructions when CT is off.

**Architecture:** Keep selection and freshness policy in Miller.Testing and fact retrieval in Miller.Indexing. Keep one TestsCore projection for MCP, CLI and dashboard. Provider code owns executable commands and selector syntax; the server renders those facts.

**Tech Stack:** .NET 10, SQLite, existing CT providers and process supervision.

**Architecture Quality:** Medium risk in command lifecycle correlation and selection scheduling. Preserve one owner for queue/store mutation. Do not solve an unresponsive loop by adding an independent timer that falsely reports selection progress. New internal contracts below are proposals, not existing APIs. Review those contracts against the protocol and family-daemon tests before implementation.

## Global constraints

- No new MCP tools. Extend existing contracts only where an existing field cannot express the fact.
- `MILLER_CT=off` remains a permanent zero-work guarantee; status never starts the daemon or writes a CT store.
- CT remains opt-in. No implicit enable/start, whole-suite fallback, or automatic restart/kill.
- Truncated, degraded or unavailable impact means Unknown: stale the affected project conservatively and execute nothing through ordinary automatic selection.
- Preserve inventory seed and idle drain as the existing bounded exceptions. Preserve the five-minute idle-drain cooldown and explicit-run red rules.
- Preserve revision plus index identity, red retention, owed stamps, per-project inventory, family-worktree routing and the user-global execution budget.
- Providers write only under supervised CT paths. Advice must not reuse a live daemon's generation or write into its artifacts.
- Cover all registered provider families and backends in task 6. A missing runner selector is reported explicitly, never replaced with an unlabelled whole-suite command.
- Existing user changes, other worktrees and daemon state are outside this planning task. Do not start or stop CT to validate historical timings.
- JSON carries facts; next-step advice remains compact-only. Document intentional additive JSON changes and preserve old records' readability.

## Evidence and corrections

Validated against `40f7c71a7703b369998903e5f9a536fda006da57`, `/home/murphy/source/miller`, branch `main`. The review and docs index were already dirty. Sources were inspected through Miller; no expensive CT replay was run.

| Claim | Disposition | Evidence and task |
|---|---|---|
| Selection blocks command processing and loop ticks | Verified | `ContinuousTestDaemonHost.cs:561` processes commands before `CollectCompletedPollReads`; selection executes through queue enqueue. `ContinuousTestImpactSelector.cs:92`. Tasks 1–3. |
| Identifier resolution happens after graph truncation is known | Verified | `ContinuousTestImpactSelector.cs:167` reads graph impact, `:175` enriches identifiers, `:190` checks truncation. Task 1. |
| Inbound resolution performs a discarded forward pass and per-name reads | Verified structure, historical counts unverified | `CtFactAdapter.cs:120` calls `ReadInboundExact`; `QueryTimeResolutionReader.cs:299,658,680,701,740` uses Both and loops over names. The reported 85,995 seeds, 510,000 rows and 10,427 queries are not independently remeasured. Task 1. |
| `run wait=true` can return unacked after five seconds | Verified | `CtCommandChannel.cs:26,94` has five-second ACK timeout; `TestsCore.cs:969` returns before settle-wait when unacked status is not busy. Task 3. |
| Loop stall hint can interrupt legitimate selection | Verified | `CtDaemonLoopHealth.cs:116`, `TestsTool.cs:317`, `CtCommandChannel.cs:113` report loop lag, advise stop, then escalate a stop after two graceful waits. Tasks 2 and 4. |
| Discovery failure has no log | Rejected as stated | `ContinuousTestDaemonQueue.cs:1042` already logs full exception detail through `CtDaemonLog.FailureDetail`. Only the stored summary is first-line at `:1112`. A structured attempt artifact and a link from failures are missing. Task 5. |
| Discovery failure turns the workspace red and hints at a nonexistent test | Verified mechanism; historical cause unverified | Queue `:1055` creates a synthetic project-status case; TestsTool `:155,165` sends any nonempty failure result to inspect. Red is valid because a tracked project failed; the defect is classification/navigation, not the aggregate red verdict. Task 5. |
| Green status and empty failures form a hint loop | Verified | `TestsTool.cs:329` always suggests failures; `:161,171` sends empty failures back to status. Task 4. |
| Per-project command is always null | Partially verified | `ContinuousTestProjectInventory.cs:139` leaves discovered Command unset; materialization `:338` propagates a stored override. `TestsCore.cs:2375` renders it. Command is configuration, not a generated runner recipe. Task 6 adds a separate contract. |
| There is no runnable selector in impact/status | Verified gap | Provider-specific commands exist internally, but `IContinuousTestProvider` at `ProviderContracts.cs:140` exposes only discovery and execution. Public BuildRunCommand seams are not universally complete recipes: Dotnet `:621` depends on an existing/would-be generation; Rust `:485` explicitly returns one representative command. Task 6. |
| Waiting proves the requested run completed | New verified defect | `TestsCore.WaitForDaemonToSettle` at `:1960` does not compare commandId to activity; any executing-to-idle transition completes it. Unacked submission drops request ID in `CtCommandChannel.Run`. Task 3. |
| Disabled status consistently prefers a one-off run | New verified guidance defect | `TestsCore.AppendDisabledNextStep` at `:1385` says direct run, but `TestsTool.StatusHint` at `:309` appends enable. Task 4. |
| First idle drain runs immediately after the quiet window | Rejected interpretation | `ContinuousTestDaemonHost.TryScheduleIdleDrain` at `:1432` initializes LastIdleDrainAt to now. The first drain waits the cooldown as well as quiet time. Keep the existing behavior, expose its reason and next eligibility. Task 2. |

### Historical trial rows

These are observations reported by the original review, not fresh benchmark results. Local daily files at the usual `.miller/logs` and `~/.miller/logs` paths did not supply matching rows during this pass. The code establishes the mechanisms, not the original wall-clock, RSS or process sample counts.

| Review row | Validation |
|---|---|
| 14:02:40 start, 368 ms, status-only | Startup status-only behavior verified at host `:588`; timestamp and duration unverified. |
| 14:02:59 wait returns after 5 s | Five-second path verified; original invocation and elapsed measurement unverified. |
| 14:02:46–14:14:08 no tick, CPU/RSS samples | Blocking selection mechanism verified; sampled interval and resource figures unverified. |
| 14:04:36 loop_stalled stop hint | Rule and hint verified; original onset unverified. |
| 14:05:56, 14:09:03, 14:12:25 Unknown project selections | Per-project repeated selector work and Unknown gate verified; exact timings unverified. |
| 14:14:06 delayed ACK | Commands run between loop passes; precise 11m7s remains historical. |
| 14:14:18 and 14:14:28 eval passes | Historical result, not rerun. |
| 14:15:06–14:17:39, 9,632 of 9,980 cases | Historical result and selection size, not rerun. Do not treat 153 seconds as pure selector time. |
| 14:19:12 green with stale zero | Historical result, not rerun. |
| 14:29:54 FusionArm discovery failure during bare dotnet test | Synthetic failure mechanism verified. Concurrent-build causality is unverified; do not implement a concurrency fix solely from this correlation. |
| 14:30 warm run in 52 seconds | Historical result, not a comparable cold-selection benchmark. |

The 1.3% adoption statistic counts explicit calls only. It does not measure daemon adoption or shell tests and does not justify changing opt-in policy. The reported 65-second fast-suite baseline includes different work from CT and is report-only.

## Task 1: Stop selection work when Unknown is already established

**Own:** `src/Miller.Testing/Selection/ContinuousTestImpactSelector.cs`, `src/Miller.Indexing/Testing/CtFactAdapter.cs`, `src/Miller.Indexing/Reads/QueryTimeResolutionReader.cs`, `tests/Miller.Tests/Testing/Selection/ContinuousTestImpactSelectorTests.cs`, `tests/Miller.Tests/Testing/Selection/FakeMillerFactSource.cs`, `tests/Miller.Tests/Indexing/Reads/QueryTimeResolutionReaderTests.cs`.

1. Add a fact-source spy test returning depth-truncated and limit-truncated graph results. Assert Unknown, all project cases stale, no executable cases, and zero identifier enrichment calls after truncation. Also cover earlier unmappable evidence: skipping later enrichment must preserve the same conservative outcome.
2. Move the known-Unknown gate directly after the graph result. Keep any evidence already collected, explicitly report why additional evidence was omitted, and preserve project filtering. Do not raise limits to make truncation disappear.
3. Add inbound-versus-forward operation counters using the existing `QueryTimeResolutionCounters`. Record the fixed-workload baseline before changing the reader. Change inbound-only reads to reverse direction where results remain identical; check all existing `ReadInboundExact` consumers, not only CT.
4. For a nontruncated large fixture, measure per-name query counts and resolution time. Replace repeated per-name reads with bounded batch reads only if that cost remains material. Preserve case comparison, version membership, resolution policy and exact/fallback separation. Scope any reuse to the immutable index identity and revision, never just a project path or wall-clock interval.

- [ ] Truncated graph cases issue zero later identifier calls and never schedule tests.
- [ ] Exact inbound evidence stays identical in legacy and family-store fixtures; no forward read is needed for inbound-only requests.
- [ ] Performance ledger records before/after operation counts and identical-workload timings; no speculative cache is left behind.
- [ ] Existing Unknown, watermark, red-retention and per-project selection tests pass.

## Task 2: Report selection progress and keep the command loop responsive

**Depends on:** Task 1 measurement, so scheduling work addresses the remaining cost.

**Own:** `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs`, `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs`, `src/Miller.Testing/Daemon/CtDaemonProtocol.cs`, `src/Miller.Testing/Daemon/CtDaemonLoopHealth.cs`, `src/Miller.Testing/Daemon/CtIdleDrainPolicy.cs`, `src/Miller.Server/Tools/TestsCore.cs`, `tests/Miller.Tests/Testing/Daemon/ControlPlane/CtDaemonLoopStallTests.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestIdleDrainTests.cs`.

Introduce a proposed immutable selection-progress record with workspace/project, phase, revision key, started time and progress timestamp/counter. Publish actual bounded-work completion, not fake loop ticks. Elapsed time is factual; show no estimated completion time without a measured estimator.

Separate expensive fact computation from application of queue/store mutations. Use one supervised active selection with a pinned read session; pending per-workspace requests carry metadata only. Collect the active result on the loop owner. Process status and command ingestion while computation runs. Recheck the requested identity/revision before applying a completed selection; discard obsolete work, keep freshness conservative and select the latest pending revision. Cancellation and workspace detach must dispose read sessions after worker completion. Never concurrently mutate the queue or SQLite connection from the worker.

Allow one active selection computation across the family by default. Coalesce pending revisions per workspace into bounded latest-key requests, with fair scheduling across registered workspaces. Pending requests do not acquire read sessions before dispatch. Moving work off the command loop must not multiply the selection memory footprint across every worktree.

Classify selecting separately from idle and child execution. A stale phase-progress timestamp can report unresponsive selection; the pulse alone cannot prove progress. Retain genuine loop-stall detection. Expose idle-drain waiting reason and next eligible time from actual quiet/cooldown state, including the initial five-minute delay.

- [ ] A blocked fake selector permits command receipt and status reads without a fabricated healthy-selection signal.
- [ ] Selection completion for an old identity/revision cannot clear current owed work or overwrite current verdicts.
- [ ] Stop/detach cancellation cleans up without overlapping store mutation or reader disposal races.
- [ ] Many queued workspaces still create only one active selection worker and bounded pending metadata/read sessions.
- [ ] Status distinguishes selection, build/discovery, queued execution, test execution and drain cooldown; unknown old-record fields stay unknown.
- [ ] The five-minute cooldown, unavailable pause and single-workspace execution budget remain enforced.

## Task 3: Wait for the submitted command, not unrelated daemon activity

**Depends on:** Task 2 protocol changes; serialize ownership of the host and protocol files.

**Own:** `src/Miller.Testing/Daemon/CtCommandChannel.cs`, `src/Miller.Testing/Daemon/CtDaemonProtocol.cs`, `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs`, `src/Miller.Server/Tools/TestsCore.cs`, `tests/Miller.Tests/Server/TestsRunDaemonAckTests.cs`, `tests/Miller.Tests/Server/TestsWaitOutcomeTests.cs`, `tests/Miller.Tests/Server/TestsWorktreeRoutingTests.cs`.

Retain the generated request ID in `CtRunResult` even without an ACK. Add proposed request lifecycle facts for submitted, acknowledged, selecting/queued, running, completed, rejected and cancelled. Correlate each command with its requested workspace, lease identity and all project runs it caused. Completion includes a proved no-work result; it cannot require observing a short-lived executing snapshot.

Have `TestsCore.Run` and `WaitForDaemonToSettle` poll that command lifecycle through the full caller wait budget, which includes ACK time. A short ACK timeout may return pending for `wait=false`; it must not cause a duplicate foreground run. For old daemon protocols, return bounded unknown/pending rather than falsely claiming a request completed. Preserve daemon-stopped, lease-lost and timeout outcomes.

**Dogfood clarification, 2026-09-08:** a live MCP call with `wait_seconds=1` and a stopped daemon entered the synchronous foreground fallback and reached the 300-second MCP transport timeout. That path had no command lifecycle and could not honor the caller's wait budget. MCP `wait=true` now returns the existing `daemon_stopped` outcome before reading facts, discovering projects or executing tests. Compact output gives a workspace-scoped start hint; JSON remains advice-free. CLI and MCP `wait=false` keep the foreground one-shot contract. The focused tool/wait/CLI gate passed 156 cases after the regression failed on the old behavior.

- [ ] Unacked work retains its command ID and receives useful pending status and a status hint.
- [ ] An unrelated project or worktree run completing never satisfies the requested command.
- [ ] Multiple project runs finish before request completion; an empty valid selection completes without a visible executing sample.
- [ ] A 240-second wait budget is not silently reduced to a five-second ACK deadline and is not extended by 240 seconds after ACK.
- [ ] Stop, replacement, rejection and lost lease remain distinguishable and never duplicate execution.

## Task 4: Make CT next-step advice depend on the actual state

**Depends on:** Tasks 2–3 for new activity facts; task 5 supplies discovery navigation; task 6 supplies one-off commands.

**Own:** `src/Miller.Server/Tools/TestsTool.cs`, `src/Miller.Server/Tools/TestsCore.cs`, `tests/Miller.Tests/Server/TestsToolTests.cs`, `tests/Miller.Tests/Server/TestsStatusProjectRowsTests.cs`. Coordinate impact handoff and hook wording with the guidance/output plan.

Use one compact advice decision in the tool path. Disabled status points at the generated direct-run recipe; offer enable/start only as secondary ongoing-use advice when supported. Active selection or pending commands point to status with phase and elapsed time. Red test cases point to failures; project discovery errors point to their attempt artifact. Green idle state needs no action. Empty failures must not create a status/failures loop. Replace unconditional stop advice on loop lag with observed phase and diagnostics; retain explicit user-controlled stop without an automatic kill or restart.

For enabled, idle CT with stale or owed cases, expose the known candidate count and a copyable `run wait=true` action. State when scope is unknown or nearly the whole project; do not imply the run is cheap from the stale count alone. This is advice, never automatic execution. The impact/edit plan consumes the same state facts for its handoff.

- [ ] Table tests cover disabled, unsupported-only, no projects, pending ACK, selecting, queued, executing, stale/owed idle cases, red cases, project discovery error, green, empty failures, paused and truly unresponsive states.
- [ ] No contradictory direct-run and primary enable advice; no green/empty round trip.
- [ ] Advice contains an actionable selector/root where required and stays compact-only.

## Task 5: Give discovery failures durable artifacts and correct navigation

**Own:** `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs`, `src/Miller.Testing/Daemon/CtDaemonLog.cs`, `src/Miller.Testing/Contracts/ProviderContracts.cs`, `src/Miller.Server/Tools/TestsCore.cs`, `src/Miller.Server/Tools/TestsTool.cs`, `tests/Miller.Tests/Server/TestsFailuresOutputTests.cs`; add proposed `src/Miller.Testing/Providers/Shared/CtDiscoveryAttempt.cs` and proposed `tests/Miller.Tests/Testing/Daemon/Engine/CtDiscoveryAttemptTests.cs`.

Keep existing full exception logging. Add a unique attempt ID before discovery, with project, provider, revision key, stage, bounded stdout/stderr artifacts, exception detail and exit status when a child exists. Exception-before-spawn must also create a diagnostic artifact. Attach the artifact reference to the synthetic project failure record and return its classification and project path from failures. Retain failed attempt files under supervised CT retention; never link a fabricated test source location or a file already deleted by generation cleanup.

The workspace should remain red when a tracked project cannot discover. Make the project failure distinct from failing test cases in output and group hints. Do not suppress eval projects or swallow build errors. Investigate the historical concurrent-build hypothesis with an isolated reproducible fixture before changing build isolation; if reproduced, repair the exact unsafe path and preserve provider supervision.

- [ ] Multiline exception, nonzero discovery exit and failure before process creation all retain complete diagnostic evidence with a readable path.
- [ ] Synthetic discovery cases point at the project and artifact, never `inspect` of a nonexistent test.
- [ ] Repeated failures at the same revision produce distinct attempt artifacts; recovery clears the synthetic failure using existing policy.
- [ ] Retention does not erase artifacts referenced by the current failure; status remains read-only.

## Task 6: Provide provider-owned runner recipes for CT-off and impacted-test workflows

**Own:** `src/Miller.Testing/Contracts/ProviderContracts.cs`, `src/Miller.Testing/Daemon/ContinuousTestProviderFactory.cs`, `src/Miller.Testing/Daemon/ContinuousTestProjectInventory.cs`, all provider files listed below, `src/Miller.Server/Tools/TestsCore.cs`; add proposed `src/Miller.Testing/Selection/ContinuousTestRunRecipe.cs` and proposed `tests/Miller.Tests/Testing/Selection/ContinuousTestRunRecipeTests.cs`. Coordinate with the impact output task before adding impact fields.

Add a proposed recipe contract separate from the stored `Command` override. Its factual fields are working directory, ordered executable/argv steps, prerequisite state, selector scope, exclusions, selection provenance and an explicit unavailable reason. Shell presentation is derived from argv with platform-correct quoting. A recipe can include build/discovery then execution; do not expose a test-only command against a nonexistent private assembly.

Reuse existing provider parsing, selector encoding and command construction. Extract reusable pure decisions where needed. Do not simply call public `BuildRunCommand`: Dotnet depends on generation artifacts and Rust exposes only a representative command. Read-only recipe generation cannot allocate a generation, run discovery, or start any process. Use registered/discovered project metadata and stored provider identities; when test identities are missing, offer a clearly labelled project recipe and its prerequisite discovery/build steps instead of inventing a precise test selector. Heuristic impact hits are separately labelled and never presented as an exact runnable subset.

| Existing owner | Required selector/recipe cases |
|---|---|
| `Providers/Dotnet/DotnetTestProvider.cs` | xUnit, NUnit, MSTest and generic .NET backends; C#, F#, VB; traits, parameterized cases, backend-specific filters, build prerequisite, unsupported xUnit v2 direct-run guidance. |
| `Providers/Node/JavaScriptTestProvider.cs` | Vitest, Jest, node:test; package scripts, file/name matching, names requiring escaping, JS/TS. |
| `Providers/Python/PythonTestProvider.cs` | pytest node IDs, parameterized IDs, project/config cwd. |
| `Providers/Rust/RustTestProvider.cs` | package/target grouping, exact names, all command chunks, custom command aggregate scope. |
| `Providers/Go/GoTestProvider.cs` | package, nested subtests, anchored regex escaping. |
| `Providers/Ruby/RubyTestProvider.cs` | RSpec file/example identity and Bundler/config context. |
| `Providers/Php/PhpTestProvider.cs` | PHPUnit and Pest; file/filter identity and project tool path. |
| `Providers/Jvm/JvmTestProvider.cs` | Gradle, Maven, sbt; Java/Kotlin/Scala identities and backend selection limits. |
| `Providers/Qml/QtQuickTestProvider.cs` | CMake/CTest and qmake backends, configure/build prerequisites and test function mapping. |
| `Providers/Godot/GodotTestProvider.cs` | GUT project/import prerequisites and supported script/test selection. |

Prefix table paths with `src/Miller.Testing/`. Check each backend against its pinned/local runner contract before changing syntax; this plan deliberately introduces no guessed external CLI flags.

- [ ] Every registered provider/backend returns a verified recipe or an explicit capability limitation with a usable, labelled broader recipe where supported.
- [ ] All commands include correct cwd, full argv, necessary prerequisites and configured exclusions; custom commands preserve their declared scope.
- [ ] Unknown identities, homonyms, fixtures/hooks/classes and heuristic hits never become exact test selectors.
- [ ] Quotes, spaces, Unicode, regex characters and command-length splitting have provider conformance tests on Linux and Windows.
- [ ] Status/impact recipe projection performs no process spawn, CT enablement, filesystem mutation or private-generation reuse.
- [ ] Impact output links exact provider identities when available and keeps likely tests separate; CT-off status supplies practical runner steps.

## Task 7: Document and verify the complete workflow

**Depends on:** Tasks 1–6 and the guidance/output plan's impact contract.

**Own:** `docs/continuous-testing.md`, `docs/contracts/tests-cli-v1.md`, relevant test fixtures and the performance ledger. Guidance plan owns routing block, embedded instructions and mirrors.

Document selecting versus stalled, request completion versus current workspace verdict, discovery artifact lookup, first-drain cooldown and direct-run recipes. Preserve existing CLI/MCP vocabulary explicitly. Add an isolated multi-project, family-worktree replay with a large changed-file delta, a delayed selector, a discovery failure and a second run request. Include both legacy and family-store evidence where the reader changed. Cover direct-run recipe conformance with real extracts across every supported language that has CT mapping; extraction-only languages must report unsupported or unmapped honestly rather than silently disappearing.

- [ ] A new agent can identify pending work, recover a discovery log and obtain a runner recipe without enabling CT.
- [ ] Deterministic tests prove command correlation and no expensive post-truncation read.
- [ ] Recorded replay includes request-to-ACK time, selection query counts, request-to-completion time and output bytes; compare identical workloads and report the old review timings only as history.
- [ ] Every mapped language/provider backend has real-extract and runner evidence before shipping; unavailable toolchains are recorded as missing verification, not a pass.

## Verification strategy

**Project source of truth:** `CLAUDE.md`, `docs/continuous-testing.md`, `tests/Miller.Tests/Miller.Tests.csproj` and `scripts/test.sh`.

**Worker red/green scope:** `dotnet test --filter "FullyQualifiedName~<TestClassName>"` for the exact classes in each task. Examples: `ContinuousTestImpactSelectorTests`, `QueryTimeResolutionReaderTests`, `CtDaemonLoopStallTests`, `TestsRunDaemonAckTests`, `TestsWaitOutcomeTests`, `TestsWorktreeRoutingTests`, `TestsFailuresOutputTests` and proposed `ContinuousTestRunRecipeTests`.

**Worker ceiling:** Assigned focused classes only. Fakes and injected clocks for fast behavior tests. Real extractor/provider processes require class-level Scale and the existing `ScaleTestSupport.RequireJulieServer()` or `CtProviderTestSupport.Require*` launch signals.

**Worker gate invariant:** Unknown executes nothing, waits complete only their own command, status is read-only, and recipes preserve provider identity/scope. Investigate and fix failures within assigned scope; report a blocker only after safe diagnostic paths are exhausted.

**Lead affected-change scope:** Run the affected CT, server and indexing classes once after a coherent batch; include output contract tests when JSON changes. Do not repeat passing tests on an unchanged tree.

**Branch gate:** `dotnet build Miller.slnx -c Release` with zero warnings/errors, one `dotnet test` fast-suite run, and `scripts/test.sh scale` because this plan changes CT providers and indexing reads. Add Windows provider/argv verification and required Windows fast-suite gate before a release. No release is authorized here.

**Security scope:** none declared. Use argv-based recipes and retain existing process/path supervision.

**Replay/metric evidence:** Zero post-truncation identifier reads and correct command correlation are hard gates. Warm performance comparisons use the same dataset and concurrency, discard cold warm-up, and record individual samples plus median and operation counts. Report p95 only with a sample count large enough to support that tail statistic. Historical 11m7s, 52s and 65s values are report-only and are not acceptance thresholds.

**Escalation triggers:** Any protocol/storage change requires old-record/family-routing tests; any provider selector or path change requires Scale conformance; any query rewrite requires legacy/family evidence parity. A slower corrected path must be measured and explained rather than hidden by a larger timeout.

**Verification ledger:** Record invariant, exact command, scope, commit, timestamp, result and metric workload. Reuse unchanged-tree passing evidence.

## Execution and file ownership

Execute tasks 1, 2 and 3 serially because host/queue/protocol contracts overlap. Task 5 can be developed independently of task 1 but must integrate serially before task 4. Task 6 may split by provider after the recipe contract is agreed; one worker owns the shared contract and server projection. Task 4 integrates final state advice. Task 7 is the lead gate. Checkpoint each accepted milestone and continue through remaining tasks; do not stop at slice boundaries. Commit policy and any push/release remain under the lead's existing authorization.
