# CT Sidecar Migration Implementation Plan (v2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

v2 incorporates the 2026-08-18 Codex plan review (all major findings accepted; evidence
verified against Eros main @ 71d78cd and this worktree @ f0b9e62d).

**Goal:** Port Eros's continuous-testing engine into Miller as `Miller.Testing`, served by a `miller tests serve` daemon, a `ct.db` sidecar, `tests` CLI verbs, and a new MCP `tests` tool.

**Architecture:** The `Eros.ContinuousTesting` project plus the CT partials of `Eros.Store` and the CT policies of `Eros.Hub` move into `src/Miller.Testing`. Three new seams replace Eros's projection layer: (1) a **self-contained `ct.db` schema** keyed by external Miller identifiers and the composite `(index identity, revision)` freshness key — Eros's schema has foreign keys into its workspace-store tables and cannot be copied; (2) a **public typed Miller fact/impact adapter** in `Miller.Indexing` — `RevisionFactCache` is `internal` and `ImpactTool`'s typed core is private in `Miller.Server`, so the selector must get a real public surface, never rendered-text parsing and never `InternalsVisibleTo`; (3) a **daemon control plane** (lease, PID+start-time identity, heartbeat, command channel, detached launcher) — Eros ran CT inside its hub process and has no detached-daemon protocol to port.

**Tech Stack:** .NET 10, Microsoft.Data.Sqlite, xUnit. No new package dependencies expected.

**Architecture Quality:** Architecture risk: **high** (Codex concurs). Approved shape: `Miller.Testing` references `Miller.Core` + `Miller.Indexing`; `Miller.Server` references `Miller.Testing`. Rejected shortcuts (do not take them; report a plan mismatch instead): `InternalsVisibleTo` into `Miller.Indexing`, any `Miller.Testing → Miller.Server` reference, parsing rendered impact text, exposing a raw SQLite connection from the CT store. `Miller.Core` keeps zero I/O deps.

## Global Constraints

- Build must stay 0 warnings / 0 errors (`TreatWarningsAsErrors`).
- `Miller.Core` keeps ZERO I/O dependencies.
- Fast/Scale split is load-bearing. Any test spawning a provider process or `julie-extract` is `[Trait("Category","Scale")]` at class level. **Focused filters must append `&Category!=Scale`** (an explicit `--filter` overrides the csproj default); Scale scopes run separately with `&Category=Scale`.
- No `Eros` identifier may remain. Namespace `Miller.Testing`; descriptive `ContinuousTest*` type names carry over. Explicit semantic replacements (not just a final grep): `EROS_WORKSPACE_ROOT` → `MILLER_CT_WORKSPACE_ROOT`; the `eros-ct` temp-path prefix in `CtTempPaths` → `miller-ct`.
- Env vars: `MILLER_CT=off` is a permanent zero-work kill switch (mirror `MILLER_SEMANTIC=off`). Other CT env vars use the `MILLER_CT_` prefix.
- Explicit start only: no code path may start the daemon as a side effect of status reads, server boot, or workspace open. `tests status` never creates `ct.db` or any CT state on read.
- Start executes nothing until a new change arrives or an explicit `run` is requested. **Unavailable delta/impact NEVER enqueues work** — Eros's poller full-scope fallback (`ContinuousTestRevisionPoller.cs:416`) is a forbidden behavior to replace, with regression tests.
- Global budget is **execution-scoped**, not daemon-lifetime: a lease held only while tests execute, with owner metadata and stale-owner recovery, modeled on `ScanGovernor` (`src/Miller.Indexing/ScanGovernor.cs`). An idle daemon must not starve other workspaces.
- Freshness is the composite `(index identity, revision)` — from `WorkspaceReadSnapshot.IndexIdentity` (`src/Miller.Indexing/Reads/WorkspaceReadSnapshot.cs`) — persisted through runs, statuses, durable freshness, and coverage maps. Revision alone is forbidden (revisions restart on rebuild; a stale green must never collide with a new revision number).
- Verdict semantics: aggregate `Green` only with complete results at the selected `(index identity, revision)`; known staleness → `Partial`; unknown execution/watch health → `Unknown`.
- All durable CT state lives in `<workspace>/.miller/ct.db`; providers write only bounded build/result/temp artifacts under supervised CT paths.
- MCP guidance budgets: `tests` description ≤900 chars, params ≤250 each, embedded core ≤1,900 chars, total descriptions ≤9,000. Gates: `tests/Miller.Tests/Server/AgentInstructionsTests.cs`.
- **Windows file-locking discipline:** CT builds/executes only inside per-generation dirs (never the workspace's `bin`/`obj`); undeletable generation dirs become recorded reap debt retried later, never run failures; artifact moves retry on sharing violations (the `MILLER_PROMOTE_RETRY_TIMEOUT` precedent); process termination kills the entire process tree (`Process.Kill(entireProcessTree: true)` — no POSIX signals on Windows); generation dir names stay short (hashed) to respect MAX_PATH; an app-control block (`0x800711C7` / Code Integrity) is a run-level execution outcome — affected tests stay stale and the verdict is `Partial`/`Unknown`, never `Green` on incomplete results.
- Port source of truth: `~/source/eros` main @ 71d78cd. Port sources: `src/Eros.ContinuousTesting/`, `src/Eros.Store/WorkspaceStore.ContinuousTesting|CtGenerations|CtGenerationDisk|CoverageMaps.cs` (+ CT-relevant `Graph`/`Queries` reads and raw-SQL-via-`Conn` call sites, which become store methods), `src/Eros.Hub/ContinuousTesting/`. Read them directly; do not rewrite logic that ports cleanly, and do not port behaviors this plan marks forbidden.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` (Testing section) + `scripts/test.sh`.

**Worker red/green scope:** `dotnet test --filter "FullyQualifiedName~<TestClassName>&Category!=Scale"` for touched classes; Scale-owning tasks additionally run their own `--filter "FullyQualifiedName~<TestClassName>&Category=Scale"` scope.

**Worker ceiling:** the focused filters above. Workers do not run the full fast suite or the full Scale suite.

**Worker gate invariant:** each task's acceptance criteria state the behavior its focused tests prove.

**Lead affected-change scope:** `scripts/test.sh` (fast suite) after each accepted batch.

**Branch gate:** `scripts/test.sh all` once before hand-off/PR. The ledger records, per provider (dotnet, rust, JavaScript, Python), whether its Scale smoke **actually executed** on this host. A provider whose toolchain was absent is reported as NOT VERIFIED in the final report — skipping is not evidence.

**Security scope:** `security-secrets` scan via razorback:security-review at the branch gate. Additionally, Tasks 9–11 carry a process-safety review in their acceptance criteria: provider argument construction (no shell interpolation of workspace strings), path containment under supervised CT dirs, PID-reuse safety (PID + process-start-time identity), graceful termination, artifact-parser robustness against hostile output, and `WorkspaceRootSafety` refusal reuse.

**Replay/metric evidence:** none — wall-clock timings are report-only per project rule.

**Escalation triggers:** touching `CliDispatch.cs`, `Program.cs`, or `MillerServiceRegistration.cs` → fast suite before batch acceptance. Touching the indexing/extract path → Scale suite at the branch gate (already owed). Task 13 → Native AOT publish smoke (read `.github/workflows/release.yml` for the exact publish invocation; the new tool's JSON must be source-gen registered).

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, timestamp, and (branch gate) per-provider execution evidence. Reuse passing entries for the same HEAD instead of rerunning.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Foundation — scaffold, contracts, schema, protocols | None - serial | Create: `src/Miller.Testing/Miller.Testing.csproj`, `src/Miller.Testing/Contracts/**`, `src/Miller.Testing/Store/CtSchema.cs`, `src/Miller.Testing/Daemon/CtDaemonProtocol.cs`, `docs/plans/2026-08-18-ct-foundation-notes.md`; Modify: `Miller.slnx`, `tests/Miller.Tests/Miller.Tests.csproj` | Yes | Everything downstream compiles against these contracts. |
| Task 2: `ct.db` store — core CRUD | Batch A | Create: `src/Miller.Testing/Store/ContinuousTestStore.cs`, `src/Miller.Testing/Store/ContinuousTestStore.Runs.cs`, `tests/Miller.Tests/Testing/Store/Core/**` | No | None - safe parallel batch. |
| Task 3: Pure parsers + classifiers port | Batch A | Create: `src/Miller.Testing/Parsing/**`, `tests/Miller.Tests/Testing/Parsing/**` | No | None - safe parallel batch. |
| Task 4: Public Miller fact/impact adapter | Batch A | Create: `src/Miller.Indexing/Testing/CtFactAdapter.cs` (+ any lifted typed impact core files it needs), `src/Miller.Testing/Selection/IMillerFactSource.cs`, `tests/Miller.Tests/Testing/FactAdapter/**` | No | None - safe parallel batch. |
| Task 5: Dotnet provider + shared process infra | Batch A | Create: `src/Miller.Testing/Providers/Shared/**`, `src/Miller.Testing/Providers/Dotnet/**`, `tests/Miller.Tests/Testing/Providers/Shared/**`, `tests/Miller.Tests/Testing/Providers/Dotnet/**` | No | None - safe parallel batch. |
| Task 6: `ct.db` store — coverage maps + generations | Batch B | Create: `src/Miller.Testing/Store/ContinuousTestStore.Coverage.cs`, `src/Miller.Testing/Store/ContinuousTestStore.Generations.cs`, `src/Miller.Testing/Store/CtGenerationPaths.cs`, `src/Miller.Testing/Store/CtTempPaths.cs`, `tests/Miller.Tests/Testing/Store/Coverage/**` | Yes (after Batch A) | Extends Task 2's store class and schema. |
| Task 7: Selector rewire | Batch B | Create: `src/Miller.Testing/Selection/ContinuousTestImpactSelector.cs`, `tests/Miller.Tests/Testing/Selection/**` | Yes (after Batch A) | Consumes Task 1 contracts, Task 2 store reads, Task 4 adapter. |
| Task 8: Rust + JavaScript + Python providers | Batch B | Create: `src/Miller.Testing/Providers/Rust/**`, `src/Miller.Testing/Providers/Node/**`, `src/Miller.Testing/Providers/Python/**`, matching `tests/Miller.Tests/Testing/Providers/{Rust,Node,Python}/**` | Yes (after Batch A) | Consumes Task 5's shared process infra and Task 1 contracts. |
| Task 9: Store-dependent analysis + importers | None - serial | Create: `src/Miller.Testing/Analysis/**` (confidence, pre-edit confidence, readiness, quality, coverage narrower), `src/Miller.Testing/Importers/**`, `src/Miller.Testing/ContinuousTestStoreApplier.cs`, `tests/Miller.Tests/Testing/Analysis/**` | Yes | Consumes Task 6 coverage/generation store surface. |
| Task 10: Daemon control plane + lifecycle locks | None - serial | Create: `src/Miller.Testing/Daemon/CtDaemonLease.cs`, `CtDaemonLauncher.cs`, `CtCommandChannel.cs`, `CtDaemonLog.cs`, `tests/Miller.Tests/Testing/Daemon/ControlPlane/**`; Modify: `src/Miller.Indexing/SingleWriterLock.cs` (WorkspaceWriteLeases + `ct.lock`), `src/Miller.Server/Workspaces/WorkspaceRemoval.cs` (+ its tests) | Yes | Implements Task 1's protocol; touches shared lock ordering. |
| Task 11: Daemon engine — policies, queue, coordinator, poller | None - serial | Create: `src/Miller.Testing/Daemon/ContinuousTestPolicy.cs`, `CtExecutionBudget.cs`, `CtDegradationBackoff.cs`, `CtWatchHealth.cs`, ported `ContinuousTestDaemonRunner.cs`, `ContinuousTestDaemonQueue.cs`, `ContinuousTestCoordinator.cs`, `ContinuousTestRevisionPoller.cs`, `ContinuousTestProviderFactory.cs`, `ContinuousTestProjectInventory.cs`, `ContinuousTestDaemonHost.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/**` | Yes | Consumes Tasks 2–10 (store, selector, providers, analysis, control plane). |
| Task 12: CLI verbs + contracts | None - serial | Modify: `src/Miller.Server/Cli/CliDispatch.cs`, `src/Miller.Server/Cli/CliCapabilities.cs`, `src/Miller.Server/Miller.Server.csproj`, `docs/contracts/cli-eros-v1.md`; Create: `src/Miller.Server/Tools/TestsCore.cs`, `docs/contracts/tests-cli-v1.md`, `tests/Miller.Tests/Server/Cli/TestsCliTests.cs` | Yes | Consumes Task 11 daemon host + Task 10 control plane. |
| Task 13: MCP `tests` tool + guidance + AOT | None - serial | Create: `src/Miller.Server/Tools/TestsTool.cs`; Modify: `src/Miller.Server/Program.cs` (`.WithTools<TestsTool>()` + AOT/JSON source-gen registration), `src/Miller.Server/Telemetry/WorkspaceBindingCallToolFilter.cs`, `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`, `tests/Miller.Tests/Server/AgentInstructionsTests.cs`, tool tests under `tests/Miller.Tests/Server/` | Yes | Consumes Task 12's `TestsCore`. |
| Task 14: Docs, guards, working notes | None - serial | Create: `tests/Miller.Tests/Testing/CtProviderTestSupport.cs`, `tests/Miller.Tests/Conventions/CtScaleTraitConventionTests.cs`; Modify: `README.md`, `docs/README.md`, `CLAUDE.md`, `AGENTS.md` (sync script) | Yes | Documents and guards the shipped surfaces. |

Commit mode: `serial-worker-commit` for serial tasks (1, 9–14); `parallel-lead-commit` for Batches A and B.

## Tasks

### Task 1: Foundation — scaffold, contracts, schema, protocols

**Files:**
- Create: `src/Miller.Testing/Miller.Testing.csproj` (refs: `Miller.Core`, `Miller.Indexing`, Microsoft.Data.Sqlite per `Miller.Indexing.csproj` versions); `src/Miller.Testing/Contracts/` — ported `ProviderContracts.cs`, `ContinuousTestSelectionContracts.cs`, `ContinuousTestDaemonContracts.cs` plus new CT domain models replacing `Eros.Core.Models` types (inspect Eros usage; define minimal Miller.Testing records); `src/Miller.Testing/Store/CtSchema.cs` — the full self-contained `ct.db` DDL; `src/Miller.Testing/Daemon/CtDaemonProtocol.cs` — control-plane types; `docs/plans/2026-08-18-ct-foundation-notes.md` — decisions record for downstream tasks
- Modify: `Miller.slnx`, `tests/Miller.Tests/Miller.Tests.csproj` (add the `Miller.Testing` project reference)
- Test: compile-only plus contract-shape tests where meaningful

**Interfaces:**
- Consumes: Eros sources (paths in Global Constraints); Eros schema reference `~/source/eros/src/Eros.Store/Migrations/Workspace/0007_test_confidence.sql` and sibling CT migrations — as a semantic reference only.
- Produces: (a) the `ct.db` schema: self-contained, **no foreign keys into any other database**; rows reference Miller facts by external identifiers (file path + blake3 content hash, symbol name+path keys) and every freshness-bearing table carries `(index_identity, revision)`; (b) `CtDaemonProtocol`: lease file layout under `<workspace>/.miller/ct/` (PID + process-start-time identity + heartbeat timestamps), command channel (file-based request/ack for `run`/`stop`), daemon status record (running/paused/stopped + reason); (c) ported contract types under `Miller.Testing.Contracts`. Downstream tasks compile against exactly these.

**Contract inputs:** Global Constraints; `MetricHistoryStore` and `ScanGovernor` as pattern references.

**File ownership:** Create: `src/Miller.Testing/Miller.Testing.csproj`, `src/Miller.Testing/Contracts/**`, `src/Miller.Testing/Store/CtSchema.cs`, `src/Miller.Testing/Daemon/CtDaemonProtocol.cs`, `docs/plans/2026-08-18-ct-foundation-notes.md`; Modify: `Miller.slnx`, `tests/Miller.Tests/Miller.Tests.csproj`

**Serialization required:** Yes

**Dependency reason:** Everything downstream compiles against these contracts.

**What to build:** The load-bearing decisions, made once: schema, identity, protocol, and contract types. Record non-obvious choices (identifier keying, PID-reuse handling, channel semantics) in the foundation-notes doc so batch implementers do not re-decide them.

**Approach:** Read the Eros CT migrations and partials first; translate FK relationships into external-identifier columns. Protocol: simplest thing that satisfies status/stop/run reliability — files under `.miller/ct/`, no sockets.

**Acceptance criteria:**
- [x] `dotnet build Miller.slnx -c Release` 0/0 with the new project and test reference.
- [x] `CtSchema` DDL creates a database with no cross-database references; every run/status/freshness/coverage table carries `(index_identity, revision)`.
- [x] `CtDaemonProtocol` defines lease identity (PID + start time), heartbeat, command channel, and status record.
- [x] Foundation-notes doc records the decisions above.
- [x] Worker commit created and SHA recorded. (`a462364e`)

### Task 2: `ct.db` store — core CRUD

**Files:**
- Create: `src/Miller.Testing/Store/ContinuousTestStore.cs`, `src/Miller.Testing/Store/ContinuousTestStore.Runs.cs`
- Test: `tests/Miller.Tests/Testing/Store/Core/`

**Interfaces:**
- Consumes: Task 1 schema + contract types. Eros behavioral reference: `WorkspaceStore.ContinuousTesting.cs` and the raw-SQL call sites reachable from CT code via `WorkspaceStore.Conn` (those queries become named store methods here — the store never exposes its connection).
- Produces: test-case CRUD (`ListTestCases`, `PutTestCase`, `DeleteTestCase`), statuses (`ListContinuousTestStatuses`, `MarkContinuousTestsStale`), runs (`StartContinuousTestRun`, `CompleteContinuousTestRun`), artifacts (`PutRunArtifact`, `LinkContinuousTestRunArtifact`), `ScoreContinuousTestFlakiness`, `Transaction` — signatures adapted to Task 1 models, single-writer via `ct.lock` following `MetricHistoryWriteLock`.

**Contract inputs:** Task 1 outputs; Global Constraints.

**File ownership:** Create: `src/Miller.Testing/Store/ContinuousTestStore.cs`, `src/Miller.Testing/Store/ContinuousTestStore.Runs.cs`, `tests/Miller.Tests/Testing/Store/Core/**`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** The core store over `ct.db`. Corruption and newer-schema files fail visibly with an actionable error (follow the search-sidecar precedent); reads on a missing db return empty/disabled without creating the file.

**Acceptance criteria:**
- [x] Round-trip tests for cases, statuses, runs, artifacts, flakiness under `dotnet test --filter "FullyQualifiedName~Testing.Store.Core&Category!=Scale"`.
- [x] Newer-schema and corrupt files produce visible, actionable failures; status reads never create the db.
- [x] Verified diff handed to the lead (parallel-lead-commit).

### Task 3: Pure parsers + classifiers port

**Files:**
- Create: `src/Miller.Testing/Parsing/` — ported `JunitTestResultParser.cs`, `CoverageArtifactParser.cs`, `CargoMetadata.cs`, `CargoTestList.cs`, `CargoTestOutput.cs`, `RustTestCaseId.cs`, `RustCoverageFlagPolicy.cs`, `ContinuousTestClassifier.cs`, `ContinuousTestStatusSummary.cs`
- Test: `tests/Miller.Tests/Testing/Parsing/` (ported fast tests)

**Interfaces:**
- Consumes: Task 1 contract types.
- Produces: the parser/classifier types above under `Miller.Testing.Parsing`, store-free.

**Contract inputs:** Purity rule: if a ported file turns out to reference `WorkspaceStore`, it does NOT belong here — leave it for Task 9 and report the reclassification to the lead.

**File ownership:** Create: `src/Miller.Testing/Parsing/**`, `tests/Miller.Tests/Testing/Parsing/**`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Mechanical port of the genuinely pure files with namespace rename and Eros-model swaps. Parser tests must include hostile/malformed input cases (security scope: artifact-parser robustness).

**Acceptance criteria:**
- [x] Ported tests pass under `dotnet test --filter "FullyQualifiedName~Testing.Parsing&Category!=Scale"`.
- [x] No store or I/O dependency in any ported file.
- [x] Verified diff handed to the lead (parallel-lead-commit).

### Task 4: Public Miller fact/impact adapter

**Files:**
- Create: `src/Miller.Indexing/Testing/CtFactAdapter.cs` (public, typed; plus any files needed to lift the typed impact computation out of `Miller.Server` into `Miller.Indexing`/`Miller.Core` — inspect `ImpactTool` with Miller first and move the minimal core, keeping `ImpactTool` consuming the moved code so behavior is unchanged), `src/Miller.Testing/Selection/IMillerFactSource.cs`
- Test: `tests/Miller.Tests/Testing/FactAdapter/`

**Interfaces:**
- Consumes: `internal RevisionFactCache` (`src/Miller.Indexing/Resolution/RevisionFactCache.cs`), `QueryTimeResolver`, `WorkspaceReadSnapshot.IndexIdentity`, and the impact core currently private in `src/Miller.Server/Tools/ImpactTool.cs`.
- Produces: `IMillerFactSource` (in `Miller.Testing`) with typed reads the selector needs: symbols for changed files, references/propagation for impacted symbols, identifier evidence, current `(index identity, revision)`; `CtFactAdapter` (in `Miller.Indexing`) implementing it. **No `InternalsVisibleTo`; no Testing→Server reference; no rendered-text parsing.**

**Contract inputs:** Task 1 contract types; existing `ImpactTool` behavior must not change (its tests stay green).

**File ownership:** Create: `src/Miller.Indexing/Testing/CtFactAdapter.cs` (+ lifted impact core files), `src/Miller.Testing/Selection/IMillerFactSource.cs`, `tests/Miller.Tests/Testing/FactAdapter/**`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** The one public typed seam between CT and Miller's fact machinery. If a fact the Eros selector used has no Miller equivalent, report the gap — do not fake it.

**Acceptance criteria:**
- [x] Adapter serves typed facts from a real test artifact fixture; `(index identity, revision)` exposed.
- [x] `ImpactTool` behavior unchanged (existing impact tests green under `dotnet test --filter "FullyQualifiedName~Impact&Category!=Scale"`).
- [x] Verified diff handed to the lead (parallel-lead-commit).

### Task 5: Dotnet provider + shared process infra

**Files:**
- Create: `src/Miller.Testing/Providers/Shared/` — ported `DotnetProcessRunner.cs` (generalized as the shared runner), `ContinuousTestToolingPaths.cs`; `src/Miller.Testing/Providers/Dotnet/DotnetTestProvider.cs`
- Test: `tests/Miller.Tests/Testing/Providers/Shared/`, `tests/Miller.Tests/Testing/Providers/Dotnet/` (Scale-tagged where processes spawn)

**Interfaces:**
- Consumes: Task 1 provider contracts.
- Produces: the shared process runner + tooling-path resolution Task 8 providers build on; the dotnet provider.

**Contract inputs:** Env replacements from Global Constraints (`MILLER_CT_WORKSPACE_ROOT`); security scope — argument construction without shell interpolation, output paths contained under supervised CT dirs.

**File ownership:** Create: `src/Miller.Testing/Providers/Shared/**`, `src/Miller.Testing/Providers/Dotnet/**`, matching test dirs

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Port runner + tooling paths + dotnet provider. Process-spawning tests: `[Trait("Category","Scale")]`, obtain toolchains via the (temporary, local) skip helper — Task 14 centralizes it as `CtProviderTestSupport`.

**Acceptance criteria:**
- [x] Fast tests pass under `dotnet test --filter "FullyQualifiedName~Testing.Providers&Category!=Scale"`.
- [x] Dotnet Scale smoke really executes a `dotnet test` on a tiny fixture and parses results (`--filter "FullyQualifiedName~Testing.Providers.Dotnet&Category=Scale"`).
- [x] The shared runner kills the entire process tree on cancel/stop and builds only into per-generation dirs (test-guarded; Windows-specific assertions run when the suite runs on Windows).
- [x] Verified diff handed to the lead (parallel-lead-commit).

### Task 6: `ct.db` store — coverage maps + generations

**Files:**
- Create: `src/Miller.Testing/Store/ContinuousTestStore.Coverage.cs`, `ContinuousTestStore.Generations.cs`, `CtGenerationPaths.cs`, `CtTempPaths.cs`, ported `ContinuousTestDurableFreshness.cs`
- Test: `tests/Miller.Tests/Testing/Store/Coverage/`

**Interfaces:**
- Consumes: Task 2 store class. Eros references: `WorkspaceStore.CoverageMaps.cs` (delta application, narrowing evidence, maintenance offers), `WorkspaceStore.CtGenerations.cs`, `WorkspaceStore.CtGenerationDisk.cs`.
- Produces: coverage puts/reads (`PutCoverageFile`, `PutCoverageSpan`, `UpsertCtCoverageMap`, delta application, narrowing evidence), generation records (`PutCtGenerationAllocated`, `UpsertCtGenerationDisk`, `UpsertCtGenerationPressure`, `UpsertCtGenerationReapDebt`), durable freshness keyed `(index identity, revision)`.

**Contract inputs:** Task 1 schema; `miller-ct` temp prefix.

**File ownership:** Create: the files above + `tests/Miller.Tests/Testing/Store/Coverage/**`

**Serialization required:** Yes (after Batch A)

**Dependency reason:** Extends Task 2's store class and schema.

**What to build:** The remaining durable state surface, including the coverage-map maintenance logic Eros kept in the store layer.

**Acceptance criteria:**
- [x] Coverage delta application and narrowing-evidence reads match Eros behavior on ported test scenarios (`--filter "FullyQualifiedName~Testing.Store.Coverage&Category!=Scale"`).
- [x] Durable freshness round-trips the composite key; a changed index identity invalidates stored freshness (test-guarded).
- [x] Reap debt: a generation dir that cannot be deleted (held handle) is recorded and retried later, never a run failure; generation dir names are short/hashed (MAX_PATH headroom, test-guarded).
- [x] Verified diff handed to the lead (parallel-lead-commit).

### Task 7: Selector rewire

**Files:**
- Create: `src/Miller.Testing/Selection/ContinuousTestImpactSelector.cs`
- Test: `tests/Miller.Tests/Testing/Selection/`

**Interfaces:**
- Consumes: Task 1 selection contracts, Task 2 store reads, Task 4 `IMillerFactSource`.
- Produces: `ContinuousTestSelectionResult Select(ContinuousTestImpactSelectionRequest)` preserving Eros's evidence classes and weights (impacted_test 0.88 etc. — carry the table verbatim).

**Contract inputs:** Eros `ContinuousTestImpactSelector.cs` as behavioral reference.

**File ownership:** Create: `src/Miller.Testing/Selection/ContinuousTestImpactSelector.cs`, `tests/Miller.Tests/Testing/Selection/**`

**Serialization required:** Yes (after Batch A)

**Dependency reason:** Consumes Task 1 contracts, Task 2 store reads, Task 4 adapter.

**What to build:** The selection algorithm over the new fact seam. Highest-risk task: if a Miller fact shape cannot express an Eros evidence class, report the mismatch — never silently drop an evidence class.

**Acceptance criteria:**
- [x] Ported selector scenarios pass with a fake `IMillerFactSource` + real store (`--filter "FullyQualifiedName~Testing.Selection&Category!=Scale"`).
- [x] All evidence classes present or a reported gap acknowledged by the lead.
- [x] Verified diff handed to the lead (parallel-lead-commit).

### Task 8: Rust + JavaScript + Python providers

**Files:**
- Create: `src/Miller.Testing/Providers/Rust/RustTestProvider.cs`, `src/Miller.Testing/Providers/Node/JavaScriptTestProvider.cs`, `src/Miller.Testing/Providers/Python/PythonTestProvider.cs`
- Test: `tests/Miller.Tests/Testing/Providers/{Rust,Node,Python}/`

**Interfaces:**
- Consumes: Task 5 shared runner + tooling paths; Task 3 cargo parsers; Task 1 contracts.
- Produces: the three providers behind the provider contract (factory wiring happens in Task 11).

**Contract inputs:** Scale tagging + skip pattern; security scope (argument construction, containment).

**File ownership:** Create: the provider files above + matching test dirs

**Serialization required:** Yes (after Batch A)

**Dependency reason:** Consumes Task 5's shared process infra and Task 1 contracts.

**What to build:** Port the three providers. **Each provider gets a real-execution Scale smoke on a tiny fixture** — Eros has no real Python smoke, so write one; do not inherit the gap.

**Acceptance criteria:**
- [x] Fast tests pass (`--filter "FullyQualifiedName~Testing.Providers&Category!=Scale"`).
- [x] Rust, JS, and Python Scale smokes exist; each really executes when its toolchain is present and skips visibly when absent.
- [x] Verified diff handed to the lead (parallel-lead-commit).

### Task 9: Store-dependent analysis + importers

**Files:**
- Create: `src/Miller.Testing/Analysis/` — ported `ContinuousTestConfidenceEngine.cs`, `ContinuousTestPreEditConfidence.cs`, `ContinuousTestReadinessBuilder.cs`, `ContinuousTestQualityAnalyzer.cs`, `ContinuousTestCoverageNarrower.cs`; `src/Miller.Testing/Importers/` — `JunitTestArtifactImporter.cs`, `CoverageArtifactImporter.cs`; `src/Miller.Testing/ContinuousTestStoreApplier.cs`
- Test: `tests/Miller.Tests/Testing/Analysis/`

**Interfaces:**
- Consumes: Tasks 2+6 store surface (their raw-SQL-via-`Conn` reads become store method calls), Task 3 parsers.
- Produces: the analysis + import pipeline Task 11 wires into the daemon.

**Contract inputs:** No store-connection leakage: if a ported query has no store method, add one to the store partials you consume (coordinate via lead; store files are Tasks 2/6 ownership — request the addition, do not edit their files in parallel; this task is serial so direct edits are permitted here, recorded in the task report).

**File ownership:** Create: the files above + `tests/Miller.Tests/Testing/Analysis/**`

**Serialization required:** Yes

**Dependency reason:** Consumes Task 6 coverage/generation store surface.

**What to build:** Port with data source swapped to `ContinuousTestStore`. Importer tests include hostile artifact inputs.

**Acceptance criteria:**
- [x] Ported analysis/importer tests pass (`--filter "FullyQualifiedName~Testing.Analysis&Category!=Scale"`).
- [x] No raw SQL outside the store partials.
- [x] Worker commit created and SHA recorded. (`368bd5e8`)

### Task 10: Daemon control plane + lifecycle locks

**Files:**
- Create: `src/Miller.Testing/Daemon/CtDaemonLease.cs`, `CtDaemonLauncher.cs`, `CtCommandChannel.cs`, `CtDaemonLog.cs`
- Modify: `src/Miller.Indexing/SingleWriterLock.cs` — add `ct.lock` to `WorkspaceWriteLeases`' fixed acquisition order; `src/Miller.Server/Workspaces/WorkspaceRemoval.cs` + its tests — removal must hold `ct.lock`
- Test: `tests/Miller.Tests/Testing/Daemon/ControlPlane/`

**Interfaces:**
- Consumes: Task 1 `CtDaemonProtocol`.
- Produces: per-workspace daemon singleton (lease under `.miller/ct/`, PID + process-start-time identity, heartbeat); `CtDaemonLauncher.SpawnDetached(workspaceRoot)` resolving the current executable cross-platform; `CtCommandChannel` for `run`/`stop` with ack; stale-state recovery (dead-PID lease reclaim); daemon logging into the shared `.miller/logs` pair with `role=ct`.

**Contract inputs:** Security scope: PID-reuse safety, graceful termination (signal/command then bounded wait), `WorkspaceRootSafety` refusal before any daemon start.

**File ownership:** As listed (includes the two Modify files and removal tests).

**Serialization required:** Yes

**Dependency reason:** Implements Task 1's protocol; touches shared lock ordering.

**What to build:** Everything Eros never needed because CT lived in its hub: the detached-daemon lifecycle. Defined behavior for `run` with no daemon: a foreground one-shot pass in the calling process (no daemon spawn).

**Acceptance criteria:**
- [x] Second daemon start on the same workspace is refused by the lease; a dead-PID stale lease is reclaimed (test-guarded, PID+start-time identity).
- [x] `stop` terminates only the leased daemon, gracefully, with bounded wait, and reaps the daemon's entire process tree on Windows and Unix alike.
- [x] Workspace removal acquires `ct.lock` in the fixed order; removal tests cover an active CT store.
- [x] Fast suite passes (escalation: shared lock + removal touched).
- [x] Worker commit created and SHA recorded. (`3298d407`)

### Task 11: Daemon engine — policies, queue, coordinator, poller

**Files:**
- Create: `src/Miller.Testing/Daemon/ContinuousTestPolicy.cs` (opt-in flag, `MILLER_CT=off`, status-only start), `CtExecutionBudget.cs` (execution-scoped user-global lease, `ScanGovernor`-modeled, owner metadata + stale recovery), `CtDegradationBackoff.cs` (degraded index → no enqueue, jittered backoff), `CtWatchHealth.cs`; ported+rewired `ContinuousTestDaemonRunner.cs`, `ContinuousTestDaemonQueue.cs`, `ContinuousTestCoordinator.cs`, `ContinuousTestRevisionPoller.cs`, `ContinuousTestProviderFactory.cs`, `ContinuousTestProjectInventory.cs`; new `ContinuousTestDaemonHost.cs` (`RunAsync(workspaceRoot, options, ct)`)
- Test: `tests/Miller.Tests/Testing/Daemon/Engine/`

**Interfaces:**
- Consumes: everything from Tasks 2–10. Revision source reads the live artifact directly (`FreshnessService` pattern: reopen per poll, rebuild detection by index identity change, never revision alone) — replaces `HubMillerRevisionSource`/`HubMillerImpactSource`.
- Produces: `ContinuousTestDaemonHost.RunAsync` — the single entry Task 12 wires behind `tests serve`; status packets (running/paused/verdict/selected `(index identity, revision)`/stale counts) written for `status` readers.

**Contract inputs:** Forbidden behaviors (Global Constraints): no enqueue on unavailable delta/impact, no idle catch-up on start, no full-suite fallback. Each gets a dedicated regression test.

**File ownership:** As listed + `tests/Miller.Tests/Testing/Daemon/Engine/**`

**Serialization required:** Yes

**Dependency reason:** Consumes Tasks 2–10 (store, selector, providers, analysis, control plane).

**What to build:** The daemon loop with the safety policies as new code (the Eros hub gate and poller fallback are references for what to REPLACE, not port). One Scale end-to-end: real dotnet provider on a fixture workspace, change → selection → execution → Green.

**Acceptance criteria:**
- [x] Regression tests prove: unavailable impact enqueues nothing; start executes nothing until change or explicit run; budget is execution-scoped (idle daemon holds nothing; second workspace reports paused only during the first's execution); `MILLER_CT=off` constructs zero CT machinery with honest status.
- [x] Verdict tests prove Green/Partial/Unknown at the composite key; a rebuild (new index identity) demotes prior Green.
- [x] An execution-blocked outcome (e.g. Windows app-control `0x800711C7`) keeps selected tests stale and the verdict `Partial`/`Unknown` — a policy-blocked run can never report Green (test-guarded with a faked provider outcome).
- [x] Fast engine tests pass (`--filter "FullyQualifiedName~Testing.Daemon&Category!=Scale"`); the dotnet end-to-end passes (`&Category=Scale`).
- [x] Worker commit created and SHA recorded. (`3cf26c1d`)

### Task 12: CLI verbs + contracts

**Files:**
- Modify: `src/Miller.Server/Cli/CliDispatch.cs` (add `tests` verb: `status|serve|run|enable|disable|stop`, `--json` on status/run), `src/Miller.Server/Cli/CliCapabilities.cs` (advertise the `tests` surface), `src/Miller.Server/Miller.Server.csproj` (reference `Miller.Testing`), `docs/contracts/cli-eros-v1.md` (CT ownership line: Miller now owns CT execution and verdicts)
- Create: `src/Miller.Server/Tools/TestsCore.cs` (the shared pure core CLI and MCP both call), `docs/contracts/tests-cli-v1.md`, `tests/Miller.Tests/Server/Cli/TestsCliTests.cs`

**Interfaces:**
- Consumes: Task 11 `ContinuousTestDaemonHost`; Task 10 launcher/lease/channel; Task 2 status reads.
- Produces: `TestsCore` operations (status/failures/start/stop/enable/disable/run) returning typed results with compact + JSON renderers; the documented `tests status --json` contract (enabled projects, daemon running/paused + reason, aggregate verdict, selected `(index_identity, revision)`, stale counts, last run, budget holder). **Enablement contract:** `tests enable [--project <path>]` — default discovers test projects via `ContinuousTestProjectInventory` and enables all discovered, persisting per-project rows (path, framework, provider config, exclusions) in `ct.db`; `--project` scopes one; `disable` mirrors. `run` semantics: with a live daemon, submit via the command channel; without one, a foreground one-shot pass; `--wait` waits for the verdict either way.
- Note: Eros's `TestsCommands.cs` is a reference for enable/status/inventory semantics only — `serve`/`start`/`stop` have no Eros precedent; their behavior is defined here.

**Contract inputs:** `CliDispatch` branch rule; CLI owns stdout; `workspace` verb wiring as the template; contract doc format per `docs/contracts/cli-eros-v1.md`.

**File ownership:** As listed.

**Serialization required:** Yes

**Dependency reason:** Consumes Task 11 daemon host + Task 10 control plane.

**What to build:** The verb family over `TestsCore`. Status on a never-enabled workspace: cheap honest read, nothing created.

**Acceptance criteria:**
- [x] Contract test asserts the documented `tests status --json` fields; `capabilities --json` advertises `tests`.
- [x] `enable`/`disable` persist per-project rows; `status` reflects them; `status` on a stopped daemon starts nothing (test-guarded).
- [x] `cli-eros-v1.md` no longer assigns CT execution to Eros.
- [x] Fast suite passes (escalation: `CliDispatch.cs` touched).
- [x] Worker commit created and SHA recorded. (`e6f286f1`)

### Task 13: MCP `tests` tool + guidance + AOT

**Files:**
- Create: `src/Miller.Server/Tools/TestsTool.cs`
- Modify: `src/Miller.Server/Program.cs` (`.WithTools<TestsTool>()` after the existing nine, plus JSON source-gen registration for the tool's payload types per the existing AOT pattern), `src/Miller.Server/Telemetry/WorkspaceBindingCallToolFilter.cs` (define unbound behavior for `tests`: status renders an honest not-ready result like `workspace`; all other operations blocked until bound), `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` (one routing line), `tests/Miller.Tests/Server/AgentInstructionsTests.cs` (tenth tool in documented-tool + budget gates)
- Test: tool tests beside the existing tool test classes under `tests/Miller.Tests/Server/`

**Interfaces:**
- Consumes: Task 12 `TestsCore`. Pattern: `[McpServerToolType]` + `[McpServerTool(Name = "tests")]` with `operation` = `status|failures|start|stop|enable|disable|run`; `start` calls `CtDaemonLauncher.SpawnDetached` — the only starting path; compact output carries a `NextStepHint`; JSON untouched.
- Produces: the agent-facing `tests` tool, registered and AOT-safe.

**Contract inputs:** Guidance budgets; description ≤900 chars, most-important-first: status is cheap, start is explicit, enable is opt-in.

**File ownership:** As listed.

**Serialization required:** Yes

**Dependency reason:** Consumes Task 12's `TestsCore`.

**What to build:** The tool wrapper plus every registration point Codex flagged: `Program.cs` tools chain, binding filter, guidance gates, AOT JSON source-gen.

**Acceptance criteria:**
- [x] `AgentInstructionsTests` green with the tenth tool and all budgets.
- [x] Tool tests prove `status` never starts the daemon and `start` is the only starting operation; unbound behavior matches the binding-filter contract.
- [x] Native AOT publish smoke succeeds (exact command read from `.github/workflows/release.yml`).
- [x] Fast suite passes (escalation: `Program.cs` touched).
- [x] Worker commit created and SHA recorded. (`ad99819d`)

### Task 14: Docs, guards, working notes

**Files:**
- Create: `tests/Miller.Tests/Testing/CtProviderTestSupport.cs` (centralized toolchain-locate/skip helper — migrate Tasks 5/8 tests onto it), `tests/Miller.Tests/Conventions/CtScaleTraitConventionTests.cs` (source-scan guard: any test file referencing the CT provider-launch helper must be Scale-tagged, mirroring `ScaleTraitConventionTests`)
- Modify: `README.md` (CT blurb + quickstart line), `docs/README.md` (map: design doc, this plan, `tests-cli-v1.md`), `CLAUDE.md` (CT section: `ct.db` ownership, safety rules, `MILLER_CT=off`, provider-test Scale rule, composite freshness rule), `AGENTS.md` via `scripts/sync-agents.sh`
- Test: the new convention guard itself

**Interfaces:**
- Consumes: shipped surfaces from Tasks 10–13.
- Produces: current docs, one launch-signal helper, one guard. Release notes are explicitly deferred to release prep on main (design doc updated to match).

**Contract inputs:** CLAUDE.md-first rule; `cmp -s CLAUDE.md AGENTS.md`.

**File ownership:** As listed.

**Serialization required:** Yes

**Dependency reason:** Documents and guards the shipped surfaces.

**Acceptance criteria:**
- [ ] Convention guard fails on an untagged provider-spawning test (proved by a temporary fixture during development) and passes on the real tree.
- [ ] All provider Scale tests use `CtProviderTestSupport`.
- [ ] `cmp -s CLAUDE.md AGENTS.md` passes; docs map updated.
- [ ] Worker commit created and SHA recorded.

## Branch gate (after Task 14)

- `scripts/test.sh all` — fast + Scale, one run, ledger-recorded with **per-provider real-execution evidence** (dotnet, rust, JS, Python). Providers whose toolchain was absent are reported NOT VERIFIED in the final report.
- `dotnet build Miller.slnx -c Release` 0/0.
- razorback:security-review `security-secrets` scan.
- Then razorback:finishing-a-development-branch.
