# Linux CT Dogfood Repair Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Make Miller's released continuous-testing path authoritative on Linux while preserving the verified Windows behavior and the existing opt-in, fail-closed safety contract.

**Architecture:** Keep build-layout policy inside `DotnetTestProvider`, selection policy inside the daemon queue, and caller-facing lifecycle facts inside the existing `tests` CLI/MCP contract. Add bounded diagnostics and wait/readiness outcomes to existing records; do not add an MCP tool, expose `ct.db`, or bypass provider isolation.

**Tech Stack:** .NET 10, xUnit v3, MSBuild, SQLite CT store, MCP/CLI JSON schema 1.

**Architecture Quality:** Affected modules are `Miller.Testing` providers/daemon and `Miller.Server` tests rendering. The caller-facing interface remains `tests status|run|start|stop|enable|disable|failures`; the existing provider and daemon-activity seams absorb the changes. Tests exercise those same interfaces plus a real Scale provider fixture. Rejected shortcuts are Miller-specific helper copying, globally forcing whole-suite mode, raising argv caps, treating spawn as readiness, or extending the internal ten-minute wait. Architecture risk is medium because the work crosses provider output, daemon scheduling, and a public additive JSON contract.

## Global Constraints

- Do not add a new MCP tool; improve the existing `tests` tool and CLI verbs.
- Preserve `schema_version: 1`; new JSON fields and objects are additive and omitted when unavailable.
- `tests status` remains a cheap read that starts nothing; `start` remains the only MCP daemon spawn and `serve` remains the CLI spelling.
- Preserve opt-in default-off behavior and the `MILLER_CT=off` zero-work guarantee.
- Preserve stale-only automatic runs, explicit red retries, full test-ID evidence beside `WholeSuite`, empty/unknown fail-closed behavior, and no full-suite fallback.
- Preserve the 6 KiB/120-unit argv chunking contract and order/flag-value atomicity.
- Apply TDD per task. Tests contain no narration comments.
- Parallel workers use `parallel-lead-commit`; serialized tasks use `serial-worker-commit` after lead review.
- Do not rerun a green verification scope on an unchanged tree.
- Cross-plan acceptance tasks run in this fixed order to avoid `docs/README.md` conflicts: CT Task 6, sidecar Task 5, generated-ignore Task 3. Each later task preserves earlier map entries.

---

## Verification Strategy

**Project source of truth:** `AGENTS.md`, `docs/contracts/tests-cli-v1.md`, and the Testing section of `AGENTS.md`.

**Worker red/green scope:** Run only the named test-class filter for the task. Task 1 may also run its single Scale test through a class/method filter after `CtProviderTestSupport.RequireDotnet()` confirms the toolchain.

**Worker ceiling:** Focused fast test classes and one assigned Scale class/method. Workers do not run `scripts/test.sh`, the full Scale suite, or live cross-repo dogfood.

**Worker gate invariant:** Task 1 proves a generic referenced executable survives generation layout; Task 2 proves the Linux fixtures model the production behavior they claim; Tasks 3-5 prove selection, chunk, lifecycle, wait, and JSON facts remain bounded and honest.

**Lead affected-change scope:** After each coherent batch, run the union of its focused filters once and `dotnet build Miller.slnx -c Release`.

**Branch gate:** Run `scripts/test.sh all` once because this plan changes a real CT provider and daemon/process-control paths. The fast suite must report zero failures; Scale may skip only for documented missing toolchains.

**Security scope:** none declared.

**Replay/metric evidence:** Hard gates are no missing helper-host pair, no dropped/duplicated selection unit, bounded JSON/compact output, truthful non-final wait output, start readiness that never fabricates success, and the existing zero-work safety tests. Wall time and chunk count are report-only, but record the Miller dogfood before/after values.

**Escalation triggers:** Any change to process-tree containment, CT generation reaping/cache placement, or real provider launch requires the Scale suite. Any new public response field requires CLI, MCP, contract-doc, and source-generation coverage.

**Assigned verification failure:** Workers stop and report when assigned verification fails unless this plan explicitly assigns the gate update.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp. Include dogfood selected count, chunk count, elapsed time, failures, stale count, and final daemon state.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Deterministic .NET project output | Batch A | `src/Miller.Testing/Providers/Dotnet/DotnetTestProvider.cs`; `tests/Miller.Tests/Testing/Providers/Dotnet/DotnetTestProviderTests.cs`; `tests/Miller.Tests/Testing/Providers/Dotnet/DotnetProviderScaleTests.cs`; `tests/Miller.Tests/Testing/SharedBrokerHostTestSupport.cs`; `tests/Miller.Tests/Testing/SharedBrokerHostTestSupportTests.cs` | No | None - safe parallel batch. |
| Task 2: Correct Linux-only fixtures | Batch A | `tests/Miller.Tests/Testing/Daemon/ControlPlane/CtDaemonShadowCopyTests.cs`; `tests/Miller.Tests/Testing/Providers/Shared/TestProcessStallTests.cs` | No | None - safe parallel batch. |
| Task 3: Selection eligibility diagnostics | Batch A | `src/Miller.Testing/Contracts/ContinuousTestDaemonContracts.cs`; `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs`; `tests/Miller.Tests/Testing/Analysis/ContinuousTestWholeSuiteRunTests.cs`; `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestDebouncedAutoRunTests.cs` | No | None - safe parallel batch. |
| Task 4: Provider/chunk run facts | None - serial | `src/Miller.Testing/Contracts/ProviderContracts.cs`; `src/Miller.Testing/Providers/Shared/CtArgvChunking.cs`; `src/Miller.Testing/Providers/Dotnet/DotnetTestProvider.cs`; `src/Miller.Testing/Daemon/ContinuousTestCoordinator.cs`; `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs`; `src/Miller.Testing/Daemon/CtRunActivityCell.cs`; `src/Miller.Testing/Daemon/CtDaemonProtocol.cs`; corresponding focused tests | Yes | Consumes Task 3's selection diagnostics and follows Task 1's provider-layout edit. |
| Task 5: Honest start/wait and additive rendering | None - serial | `src/Miller.Server/Tools/TestsCore.cs`; `src/Miller.Server/Tools/TestsTool.cs`; `src/Miller.Server/ServerJson.cs`; `src/Miller.Server/Cli/CliDispatch.cs`; `src/Miller.Testing/Daemon/CtDaemonLauncher.cs`; `src/Miller.Testing/Daemon/CtCommandChannel.cs`; `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs`; `docs/contracts/tests-cli-v1.md`; `docs/contracts/cli-eros-v1.md`; corresponding focused tests | Yes | Consumes Task 4's bounded run facts and changes the public response contract once. |
| Task 6: Linux cross-provider acceptance | None - serial | `docs/findings/2026-08-22-linux-ct-dogfood-repair-verification.md`; `docs/README.md`; plan status checkboxes | Yes | Runs only after Tasks 1-5 and the branch gate are green. |

### Task 1: Deterministic .NET project output

**Files:**
- Modify: `src/Miller.Testing/Providers/Dotnet/DotnetTestProvider.cs`
- Modify: `tests/Miller.Tests/Testing/Providers/Dotnet/DotnetTestProviderTests.cs`
- Modify: `tests/Miller.Tests/Testing/Providers/Dotnet/DotnetProviderScaleTests.cs`
- Modify: `tests/Miller.Tests/Testing/SharedBrokerHostTestSupport.cs`
- Modify: `tests/Miller.Tests/Testing/SharedBrokerHostTestSupportTests.cs`

**Interfaces:**
- Consumes: `ContinuousTestWorkspace.BuildOutputRoot`, `CtGenerationPaths`, MSBuild `OutDir`, and `ReferenceOutputAssembly=false` project references.
- Produces: project-specific generation subdirectories whose executable/apphost and companion DLL stay colocated; all provider path resolvers use the same layout.

**Contract inputs:** Keep `--artifacts-path <BuildOutputRoot>` project-stable for the compiler cache. Add MSBuild `GenerateProjectSpecificOutputFolder=true` beside the generation `OutDir`; do not move the intermediate cache into the generation.

**File ownership:** `src/Miller.Testing/Providers/Dotnet/DotnetTestProvider.cs`; `tests/Miller.Tests/Testing/Providers/Dotnet/DotnetTestProviderTests.cs`; `tests/Miller.Tests/Testing/Providers/Dotnet/DotnetProviderScaleTests.cs`; `tests/Miller.Tests/Testing/SharedBrokerHostTestSupport.cs`; `tests/Miller.Tests/Testing/SharedBrokerHostTestSupportTests.cs`

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**What to build:** Make the .NET provider's generation output project-specific for every referenced project. Update xUnit and generic target-path resolution, coverage assembly enumeration, and test-only helper lookup to use nested project output without hardcoding `Miller.SharedBrokerTestHost` in production.

**Approach:** Add one internal project-output path helper and use it in build, discover, run, target-path evaluation, and recursive instrumentable-assembly discovery. Extend `DotnetProviderScaleTests` with a temporary test project that references an executable project using `ReferenceOutputAssembly=false`; assert both output pairs exist and the helper launches. Retain repo-layout fallback in `SharedBrokerHostTestSupport` for ordinary `scripts/test.sh` runs.

**Acceptance criteria:**
- [x] A generic referenced executable and its companion DLL are colocated in a separate generation subdirectory on Linux and Windows.
- [x] xUnit, MSTest, NUnit, coverage discovery, generation reaping, and the stable compiler cache retain their existing contracts.
- [x] The generic Scale fixture launches the referenced helper from the project-specific generation path.
- [x] Focused provider/support tests and the assigned Scale test pass; the change is handed to the lead per `parallel-lead-commit`.

### Task 2: Correct Linux-only fixtures

**Files:**
- Modify: `tests/Miller.Tests/Testing/Daemon/ControlPlane/CtDaemonShadowCopyTests.cs`
- Modify: `tests/Miller.Tests/Testing/Providers/Shared/TestProcessStallTests.cs`

**Interfaces:**
- Consumes: `CtDaemonShadowCopy.IsIdleCopy`'s real executable probe and `TestProcessRunner`'s last-output stall clock.
- Produces: portable fixtures that exercise a running image and continuous output for the full observation window.

**Contract inputs:** Linux read handles do not model a running executable for the write/ETXTBSY probe. The chatty child must emit through `SamplingWindow` plus a deterministic margin.

**File ownership:** `tests/Miller.Tests/Testing/Daemon/ControlPlane/CtDaemonShadowCopyTests.cs`; `tests/Miller.Tests/Testing/Providers/Shared/TestProcessStallTests.cs`

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**What to build:** Replace the shadow-copy read-handle simulation with a real running copied image on Unix, retaining the Windows handle path where it models the platform correctly. Make the chatty child derive its emission count/duration from the six-second sampling window instead of stopping after roughly two seconds.

**Approach:** Keep production code unchanged unless the corrected fixture proves a real defect. Every spawned fixture process must be condition-waited, bounded, and cleaned in `finally`.

**Acceptance criteria:**
- [x] The shadow-copy test proves a live image is retained and an idle image is removed on Linux and Windows.
- [x] The stall test emits for the entire sampling window and keeps the observed silence near zero.
- [x] Repeating each focused class three times is green without timing-threshold inflation.
- [x] Focused tests pass and the change is handed to the lead per `parallel-lead-commit`.

### Task 3: Selection eligibility diagnostics

**Files:**
- Modify: `src/Miller.Testing/Contracts/ContinuousTestDaemonContracts.cs`
- Modify: `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs`
- Modify: `tests/Miller.Tests/Testing/Analysis/ContinuousTestWholeSuiteRunTests.cs`
- Modify: `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestDebouncedAutoRunTests.cs`

**Interfaces:**
- Consumes: pending-run lane/scope, known inventory, selected IDs, fresh trimming, retained reds, and `CoversEveryKnownCase`.
- Produces: bounded typed selection facts and one reason code explaining why `WholeSuite` is true or false.

**Contract inputs:** Whole-suite keeps the full test-ID list beside the flag. Impact-derived coverage, unknown inventory, backfill, and post-refresh trimming remain fail-closed.

**File ownership:** `src/Miller.Testing/Contracts/ContinuousTestDaemonContracts.cs`; `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs`; `tests/Miller.Tests/Testing/Analysis/ContinuousTestWholeSuiteRunTests.cs`; `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestDebouncedAutoRunTests.cs`

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**What to build:** Add a bounded selection-facts record carrying scope, lane, known count, pre/post-trim selected counts, retained-red count, coverage result, eligibility, reason code, and a deterministic selection digest. Correct the stale XML/documentation that still describes the removed empty-list design.

**Approach:** Compute diagnostics where `DrainReadyAsync` already owns the policy decision. Add explicit coverage for an explicit workspace run merged over an impact-derived pending run; do not change eligibility until that test proves the merge currently loses a legitimate workspace-scope signal.

**Acceptance criteria:**
- [ ] Every `WholeSuite=false` run has one bounded, deterministic reason code.
- [ ] Existing fresh, red, backfill, unknown, and impact-derived behavior remains unchanged.
- [ ] Focused tests distinguish every intentional partial-selection reason without listing case names.
- [ ] Focused whole-suite/debounce tests pass and the change is handed to the lead per `parallel-lead-commit`.

### Task 4: Provider/chunk run facts

**Files:**
- Modify: `src/Miller.Testing/Contracts/ProviderContracts.cs`
- Modify: `src/Miller.Testing/Providers/Shared/CtArgvChunking.cs`
- Modify: `src/Miller.Testing/Providers/Dotnet/DotnetTestProvider.cs`
- Modify: `src/Miller.Testing/Daemon/ContinuousTestCoordinator.cs`
- Modify: `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs`
- Modify: `src/Miller.Testing/Daemon/CtRunActivityCell.cs`
- Modify: `src/Miller.Testing/Daemon/CtDaemonProtocol.cs`
- Test: `tests/Miller.Tests/Testing/Providers/Shared/CtArgvChunkingTests.cs`
- Test: `tests/Miller.Tests/Testing/Providers/Dotnet/DotnetTestProviderTests.cs`
- Test: `tests/Miller.Tests/Testing/Daemon/ControlPlane/CtRunActivityCellTests.cs`
- Test: `tests/Miller.Tests/Testing/Daemon/ControlPlane/ContinuousTestDaemonActivityTests.cs`

**Interfaces:**
- Consumes: Task 3 selection facts, `ContinuousTestProviderResolution.ProviderSource`, provider run timestamps/display names, and chunk units.
- Produces: `CtDaemonRunProgress` with provider identity, selection facts, requested unique unit count, chunk count, current part, elapsed time, and bounded case-name samples/digests.

**Contract inputs:** Never publish thousands of IDs or argv text. The sum of chunk units must equal the requested unique selection, and provider metadata must originate at provider resolution/run creation rather than renderer inference.

**File ownership:** `src/Miller.Testing/Contracts/ProviderContracts.cs`; `src/Miller.Testing/Providers/Shared/CtArgvChunking.cs`; `src/Miller.Testing/Providers/Dotnet/DotnetTestProvider.cs`; `src/Miller.Testing/Daemon/ContinuousTestCoordinator.cs`; `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs`; `src/Miller.Testing/Daemon/CtRunActivityCell.cs`; `src/Miller.Testing/Daemon/CtDaemonProtocol.cs`; corresponding focused tests

**Serialization required:** Yes.

**Dependency reason:** Consumes Task 3's selection diagnostics and follows Task 1's provider-layout edit.

**What to build:** Propagate provider and selection identity into the persisted daemon activity record. Extend chunking results with bounded manifest facts so status can report `part 53/N`, counts, and digest without exposing a large selection.

**Approach:** Keep `CtArgvChunking` generic across .NET, Node, Python, and generic providers. Use optional activity fields for compatibility with older daemon records; do not infer provider names from file extensions in the renderer.

**Acceptance criteria:**
- [ ] Run activity names the resolved provider, run ID, start time, elapsed time, selection reason/digest, total chunks, current part, and bounded names when available.
- [ ] Chunk totals prove no selection unit is dropped, duplicated, or split across flag/value boundaries.
- [ ] Old activity JSON remains readable and optional fields are omitted when absent.
- [ ] Focused chunk/provider/activity tests pass and the serialized worker commit is recorded.

### Task 5: Honest start/wait and additive rendering

**Files:**
- Modify: `src/Miller.Server/Tools/TestsCore.cs`
- Modify: `src/Miller.Server/Tools/TestsTool.cs`
- Modify: `src/Miller.Server/ServerJson.cs`
- Modify: `src/Miller.Server/Cli/CliDispatch.cs`
- Modify: `src/Miller.Testing/Daemon/CtDaemonLauncher.cs`
- Modify: `src/Miller.Testing/Daemon/CtCommandChannel.cs`
- Modify: `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs`
- Modify: `docs/contracts/tests-cli-v1.md`
- Modify: `docs/contracts/cli-eros-v1.md`
- Test: `tests/Miller.Tests/Server/TestsToolTests.cs`
- Test: `tests/Miller.Tests/Server/Cli/TestsCliTests.cs`
- Test: `tests/Miller.Tests/Server/TestsRunDaemonAckTests.cs`
- Test: `tests/Miller.Tests/Testing/Daemon/ControlPlane/CtCommandChannelTests.cs`
- Test: `tests/Miller.Tests/Testing/Daemon/ControlPlane/CtDeadDaemonStatusTests.cs`

**Interfaces:**
- Consumes: Task 4 `CtDaemonRunProgress`, existing `TestsCoreRequest.WaitTimeout`, command acknowledgement, lease/status publication, and schema-1 renderers.
- Produces: bounded start readiness and wait outcome objects plus additive status/run/failure metadata.

**Contract inputs:** CLI `--wait` retains the internal ten-minute completion default. MCP `wait=true` deliberately changes from the same ten-minute core default to 240 seconds and accepts an optional `wait_seconds` override bounded to 1-240 seconds. Start readiness observation is bounded to two seconds. `WaitForDaemonToSettle` must classify every exit: `completed`, `queued_timeout`, `not_picked_up`, `wait_timeout`, `daemon_stopped`, or `lease_lost`; only observed execution followed by live-daemon idle is complete.

**File ownership:** `src/Miller.Server/Tools/TestsCore.cs`; `src/Miller.Server/Tools/TestsTool.cs`; `src/Miller.Server/ServerJson.cs`; `src/Miller.Server/Cli/CliDispatch.cs`; `src/Miller.Testing/Daemon/CtDaemonLauncher.cs`; `src/Miller.Testing/Daemon/CtCommandChannel.cs`; `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs`; `docs/contracts/tests-cli-v1.md`; `docs/contracts/cli-eros-v1.md`; corresponding focused tests

**Serialization required:** Yes.

**Dependency reason:** Consumes Task 4's bounded run facts and changes the public response contract once.

**What to build:** After a successful spawn, observe lease/status publication for up to two seconds and report `ready`, `not_published_within_grace`, or `daemon_exited_before_publish` without changing the meaning of process-launch acceptance. Return a typed wait object for every completion or early-exit path with `wait_complete`, wait state, elapsed/timeout, command ID, and run ID; never present queued, not-picked-up, timed-out, stopped, or lease-lost snapshots as final.

**Approach:** Add `wait_seconds` only to the existing MCP operation. Keep CLI's ten-minute behavior. Correct the MCP parameter description and both public contracts so wait follows daemon activity rather than verdict value. Render optional `provider`, `selection`, `elapsed_seconds`, `wait`, bounded `case_names`, `names_truncated`, and digest objects across status/run/failures, preserving current fields, enums, compact limits, and schema version.

**Acceptance criteria:**
- [ ] A started daemon never immediately renders as a confirmed dead daemon solely because publication lagged.
- [ ] Every non-settled exit has `wait_complete=false` and its exact state; only observed execution followed by live-daemon idle has `wait_complete=true`.
- [ ] MCP `wait=true` returns an honest in-progress response by 240 seconds when a run continues; CLI completion waiting remains ten minutes.
- [ ] Status/run/failure JSON exposes bounded correlation and provider facts without direct DB inspection.
- [ ] Old response assertions remain byte-identical when optional facts are absent.
- [ ] Focused server/control-plane/contract tests pass and the serialized worker commit is recorded.

### Task 6: Linux cross-provider acceptance

**Files:**
- Create: `docs/findings/2026-08-22-linux-ct-dogfood-repair-verification.md`
- Modify: `docs/README.md`
- Modify: `docs/plans/2026-08-22-linux-ct-dogfood-repair-plan.md`

**Interfaces:**
- Consumes: completed Tasks 1-5 and the released dogfood workload.
- Produces: durable before/after evidence and completed acceptance checkboxes.

**Contract inputs:** Run CT workspaces serially because the execution budget is user-global. Restore every daemon/config state after the replay.

**File ownership:** `docs/findings/2026-08-22-linux-ct-dogfood-repair-verification.md`; `docs/README.md`; plan status checkboxes

**Serialization required:** Yes.

**Dependency reason:** Runs only after Tasks 1-5 and the branch gate are green.

**What to build:** Repeat the Miller xUnit, Razorback node:test, and more-itertools pytest lifecycle on Linux. Record selection reason, provider, chunks, elapsed time, verdict/failures/stale counts, wait outcome, and final stopped/disabled state.

**Approach:** Treat correctness fields as hard gates and elapsed time as report-only. Confirm the helper-host failures and Linux fixture failures are gone, and compare the Miller chunk path against the newly exposed whole-suite reason rather than assuming chunking itself is a defect.

**Acceptance criteria:**
- [ ] Miller CT has no missing helper-host or Linux-fixture failures and leaves no stale cases caused by those defects.
- [ ] JavaScript and Python remain green with their original CT state restored.
- [ ] No daemon or global execution-budget lease remains.
- [ ] `scripts/test.sh all`, Release build, and worktree-state checks are recorded on the exact final tree.
- [ ] Verification evidence is mapped in `docs/README.md`, all plan checkboxes are updated, and the serialized worker commit is recorded.
