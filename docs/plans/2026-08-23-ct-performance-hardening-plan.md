# CT Performance Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Repair xUnit whole-suite result transport and remove detailed CT state materialization from the daemon's 250 ms idle loop, with measured before/after evidence.

**Architecture:** Whole-suite xUnit runs use bounded verbose progress plus the existing JUnit artifact/import seam; selected and coverage runs keep JSON. The daemon replaces per-case status/watermark object materialization with one indexed aggregate query whose verdict is still decided by the pure status-projection policy.

**Tech Stack:** .NET 10, C# 14, xUnit v3.2.2, Microsoft.Data.Sqlite, SQLite WAL.

**Architecture Quality:** Medium risk. No public CLI, MCP, schema, plugin, or artifact contract changes. Result validation stays in the xUnit provider/import boundary; aggregate verdict inputs stay in the store/projection/daemon boundary.

## Global Constraints

- Do not add an MCP tool or change MCP/CLI JSON schemas.
- Preserve JSON result handling byte-for-byte for selected, chunked, generic-dotnet, and per-test coverage runs.
- Artifact-only transport applies only to xUnit whole-suite runs with `CoverageMode=None`.
- Keep the ten-minute output-silence guard and child-liveness signal active through verbose progress.
- Whole-suite JUnit import may create genuinely new artifact cases, but must preflight attribution and fail before mutation when none of a non-empty selected inventory maps.
- Selected-but-unreported cases remain stale and produce a lifecycle diagnostic.
- Preserve the permanent `MILLER_CT=off` zero-work guarantee and primary-context-only daemon snapshot semantics.
- Do not change `ct.db` schema or existing index definitions.
- Keep fast and Scale tests separate under the repository's documented trait rules.
- No timing assertions; use deterministic operation/allocation guards and report wall time separately.

## Verification Strategy

**Project source of truth:** `AGENTS.md`, especially the fast/Scale split and Release build requirements.

**Worker red/green scope:** `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~<owned test class>"` for each packet.

**Worker ceiling:** Owned focused test classes only. Workers do not run `scripts/test.sh`, Scale, or full CT workloads.

**Worker gate invariant:** Provider tests prove argv/transport/failure behavior; importer/coordinator tests prove mutation-atomic attribution and residue diagnostics; projection/store/daemon tests prove row-parity and aggregate use.

**Lead affected-change scope:** Run the focused classes from each accepted batch, then the real CT measurement packets at their serialized boundaries.

**Branch gate:** `scripts/test.sh`, then `scripts/test.sh scale` because provider/execution paths changed, then `git diff --check`. Do not rerun a green gate on an unchanged tree.

**Security scope:** none declared.

**Replay/metric evidence:** Correct result counts, stale counts, verdicts, process exits, query-plan index use, and allocation ratios are hard gates. Wall time, CPU, RSS, artifact size, poller time, and projection time are report-only before/after evidence.

**Escalation triggers:** Any public contract/schema change, provider behavior outside xUnit whole-suite/no-coverage, aggregate parity failure, unmatched selected cases with no diagnostic, or real provider failure requires lead re-planning before broader changes.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp in the findings document. For replay or metric evidence, record hard-gate metrics and report-only metrics. Reuse a passing ledger entry for an unchanged tree.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
| --- | --- | --- | --- | --- |
| Task 1: Artifact attribution preflight | Batch A | `src/Miller.Testing/Importers/JunitTestArtifactImporter.cs`; `src/Miller.Testing/Daemon/ContinuousTestCoordinator.cs`; `tests/Miller.Tests/Testing/Analysis/JunitTestArtifactImporterTests.cs`; `tests/Miller.Tests/Testing/Analysis/ContinuousTestStoreApplierTests.cs` | No | None - safe parallel batch. |
| Task 2: xUnit whole-suite artifact transport | Batch A | `src/Miller.Testing/Providers/Dotnet/DotnetTestProvider.cs`; `tests/Miller.Tests/Testing/Providers/Dotnet/DotnetTestProviderTests.cs`; `tests/Miller.Tests/Testing/Providers/Dotnet/DotnetProviderScaleTests.cs` | No | None - safe parallel batch. |
| Task 3: Whole-suite proof and idle baseline | None - serial | `docs/findings/2026-08-23-performance-audit.md` measurement sections only | Yes | Requires accepted Tasks 1 and 2 on one built tree. |
| Task 4: Aggregate daemon projection | None - serial | `src/Miller.Testing/Contracts/ContinuousTestingModels.cs`; `src/Miller.Testing/Store/ContinuousTestStore.cs`; `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs`; `tests/Miller.Tests/Testing/ContinuousTestStatusProjectionTests.cs`; `tests/Miller.Tests/Testing/Store/Core/ContinuousTestStoreTests.cs`; `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestVerdictTests.cs` | Yes | Requires Task 3's successful CT state and idle baseline. |
| Task 5: Final measurement and evidence | None - serial | `docs/findings/2026-08-23-performance-audit.md`; `docs/plans/2026-08-23-ct-performance-hardening-plan.md` acceptance/ledger only | Yes | Requires Task 4 implementation and focused verification. |

Batch A uses `parallel-lead-commit`: workers do not commit. Every later task also hands its verified diff/evidence to the lead; the lead checkpoints, reviews, stages, and commits coherent slices.

### Task 1: Artifact attribution preflight

**Files:**
- Modify: `src/Miller.Testing/Importers/JunitTestArtifactImporter.cs:7-250`
- Modify: `src/Miller.Testing/Daemon/ContinuousTestCoordinator.cs:845-875`
- Test: `tests/Miller.Tests/Testing/Analysis/JunitTestArtifactImporterTests.cs`
- Test: `tests/Miller.Tests/Testing/Analysis/ContinuousTestStoreApplierTests.cs`

**Interfaces:**
- Consumes: existing `JunitTestArtifactImportRequest.TestCaseIdsBySelector`, selected provider ids, and `ContinuousTestCoordinatorOptions.LifecycleLog`.
- Produces: optional selected-id preflight input and report counts for mapped selected ids, selected residue, and new artifact cases; no existing offline-import behavior changes when the new input is absent.

**Contract inputs:** Provider artifact imports know the complete selected id set. Whole-suite artifacts may contain new tests absent from that set. Existing exact/escaped/collapsed theory reconciliation remains authoritative.

**File ownership:** `src/Miller.Testing/Importers/JunitTestArtifactImporter.cs`; `src/Miller.Testing/Daemon/ContinuousTestCoordinator.cs`; `tests/Miller.Tests/Testing/Analysis/JunitTestArtifactImporterTests.cs`; `tests/Miller.Tests/Testing/Analysis/ContinuousTestStoreApplierTests.cs`

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**What to build:** Add a provider-import preflight that resolves the parsed artifact before store mutation. Fail when a non-empty selected set maps zero artifact rows; otherwise preserve current creation of genuinely new artifact cases, expose selected/new/residue counts, leave residue stale, and log one bounded lifecycle diagnostic.

**Approach:** Extend the import request with an optional selected-id set. Resolve parsed rows and compute counts before any artifact/run/case/result write. Offline imports without selected ids remain byte/behavior compatible. Coordinator provider fallback supplies selected ids and logs `reported/selected` residue without logging case ids.

**Acceptance criteria:**
- [x] Zero selected matches fails before any store mutation.
- [x] Partial selected matches import correctly, leave residue stale, and emit one bounded diagnostic.
- [x] New artifact rows still create artifact cases.
- [x] Exact, escaped, collapsed, ambiguous, and theory-row mappings remain covered.
- [x] Worker-scope verification passes and the diff is handed to the lead.

### Task 2: xUnit whole-suite artifact transport

**Files:**
- Modify: `src/Miller.Testing/Providers/Dotnet/DotnetTestProvider.cs:163-260`
- Modify: `src/Miller.Testing/Providers/Dotnet/DotnetTestProvider.cs:344-447`
- Test: `tests/Miller.Tests/Testing/Providers/Dotnet/DotnetTestProviderTests.cs`
- Test: `tests/Miller.Tests/Testing/Providers/Dotnet/DotnetProviderScaleTests.cs`

**Interfaces:**
- Consumes: `ContinuousTestProviderRunRequest.WholeSuite`, `CoverageMode`, existing JUnit result paths, `JunitTestResultParser`, and `ProviderRunResult.ResultArtifactPath`.
- Produces: xUnit whole-suite/no-coverage commands using `-reporter verbose -noAutoReporters`; validated artifact-only run results with empty immediate cases and correct pass/fail status.

**Contract inputs:** Selected/chunked and coverage runs retain JSON exactly. Truncated verbose output is acceptable only for artifact-only runs because it is liveness text, not result data.

**File ownership:** `src/Miller.Testing/Providers/Dotnet/DotnetTestProvider.cs`; `tests/Miller.Tests/Testing/Providers/Dotnet/DotnetTestProviderTests.cs`; `tests/Miller.Tests/Testing/Providers/Dotnet/DotnetProviderScaleTests.cs`

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**What to build:** Branch xUnit whole-suite/no-coverage execution onto artifact-only result validation. Parse JUnit for non-empty cases and run status, accept a normal red test exit with a failed artifact, reject missing/malformed/empty/inconsistent artifacts, and return the existing artifact-only provider result without parsing stdout.

**Approach:** Keep generation, build, chunk progress, result-path, cleanup, and coverage behavior intact. Add a small predicate for artifact-only eligibility and a parser/validator local to `DotnetTestProvider`. Do not disable output capture or stall detection; verbose output keeps both live.

**Acceptance criteria:**
- [x] Whole-suite/no-coverage argv uses verbose/no-auto-reporters/JUnit and never requires complete stdout.
- [x] Green and red artifacts return honest artifact-only results.
- [x] Missing, malformed, empty, inconsistent-exit, and rejected-flag cases fail actionably.
- [x] Selected/chunked/coverage command bytes and immediate case attribution remain unchanged.
- [x] Real Scale coverage proves the target project's xUnit runner accepts the flags and produces importable JUnit.
- [x] Worker-scope verification passes and the diff is handed to the lead.

### Task 3: Whole-suite proof and idle baseline

**Files:**
- Modify: `docs/findings/2026-08-23-performance-audit.md`

**Interfaces:**
- Consumes: accepted Batch A Release binary and the real Miller workspace CT database.
- Produces: successful whole-suite result/stale counts plus a reproducible 60-second pre-aggregate idle CPU/RSS baseline.

**Contract inputs:** Restore the daemon to stopped state after sampling. Do not call an idle result valid while ready/stale work remains.

**File ownership:** `docs/findings/2026-08-23-performance-audit.md` measurement sections only

**Serialization required:** Yes.

**Dependency reason:** Requires accepted Tasks 1 and 2 on one built tree.

**What to build:** No production code. Build Release, run the exact foreground CT workload with `/usr/bin/time -v`, verify artifact/result/status facts in read-only SQLite, then start the daemon only after no ready work remains. Sample `/proc/<pid>/stat` CPU ticks and `/proc/<pid>/status` RSS across 60 seconds, stop it, and record the commands/numbers.

**Approach:** Treat failed/partial/provider-error results as diagnosis, not a baseline. Record poller-vs-projection evidence available from focused probes; if exact phase timing is not observable yet, record that explicitly and use Task 4's side-by-side store allocation probe.

**Acceptance criteria:**
- [x] Whole-suite no longer fails on output truncation and reported cases persist.
- [x] Daemon is genuinely idle for the complete sample and restored to stopped.
- [x] Baseline command, CPU delta, RSS range, result counts, stale count, and artifact size are recorded.
- [x] Evidence diff is handed to the lead.

### Task 4: Aggregate daemon projection

**Files:**
- Modify: `src/Miller.Testing/Contracts/ContinuousTestingModels.cs:132-185`
- Modify: `src/Miller.Testing/Store/ContinuousTestStore.cs:277-328`
- Modify: `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs:1041-1070`
- Test: `tests/Miller.Tests/Testing/ContinuousTestStatusProjectionTests.cs`
- Test: `tests/Miller.Tests/Testing/Store/Core/ContinuousTestStoreTests.cs`
- Test: `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestVerdictTests.cs`

**Interfaces:**
- Consumes: status rows, optional selected freshness key, existing watermark/index tables, and `watchHealthy`.
- Produces: `ContinuousTestStatusAggregate` with total/pending/stale/fresh-red counts, a pure projection overload, and `ContinuousTestStore.AggregateContinuousTestStatuses(workspaceId, selectedKey)`.

**Contract inputs:** No-cursor stale count uses stored `state=stale`; selected-key freshness uses committed identity+revision equality or green watermark identity equality with revision `>=`; primary context only.

**File ownership:** `src/Miller.Testing/Contracts/ContinuousTestingModels.cs`; `src/Miller.Testing/Store/ContinuousTestStore.cs`; `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs`; `tests/Miller.Tests/Testing/ContinuousTestStatusProjectionTests.cs`; `tests/Miller.Tests/Testing/Store/Core/ContinuousTestStoreTests.cs`; `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestVerdictTests.cs`

**Serialization required:** Yes.

**Dependency reason:** Requires Task 3's successful CT state and idle baseline.

**What to build:** Add one aggregate query and pure aggregate projection, then switch daemon `Evaluate` to them. Detailed status and watermark APIs remain unchanged for user status, failures, queue work, and other per-case consumers.

**Approach:** Use separate null-cursor and selected-key SQL shapes when that keeps plans simple. The selected query left-joins watermarks on existing keys and returns one row without sorting. Add parity fixtures that feed the same cases through detailed and aggregate projection. Add a report-only side-by-side allocation probe and a hard assertion that aggregate allocation is materially below detailed materialization on the same synthetic store.

**Acceptance criteria:**
- [x] Aggregate and detailed projection agree on every state/freshness/watch edge.
- [x] Query plan uses existing indexes and no temporary sort.
- [x] Daemon `Evaluate` materializes zero detailed status/watermark collections.
- [x] Detailed APIs and callers remain unchanged.
- [x] Relative allocation guard passes on identical synthetic data.
- [x] Worker-scope verification passes and the diff is handed to the lead.

### Task 5: Final measurement and evidence

**Files:**
- Modify: `docs/findings/2026-08-23-performance-audit.md`
- Modify: `docs/plans/2026-08-23-ct-performance-hardening-plan.md`

**Interfaces:**
- Consumes: final accepted source tree and Task 3 baseline.
- Produces: identical after-measurements, completed acceptance checklist, and verification ledger.

**Contract inputs:** Never compare different data, concurrency, commands, or daemon state. Do not rerun a green gate on an unchanged tree.

**File ownership:** `docs/findings/2026-08-23-performance-audit.md`; `docs/plans/2026-08-23-ct-performance-hardening-plan.md` acceptance/ledger only

**Serialization required:** Yes.

**Dependency reason:** Requires Task 4 implementation and focused verification.

**What to build:** Repeat the exact 60-second idle sample and whole-suite evidence where the tree changed the measured path. Record before/after, tradeoffs, hard-gate results, and the untouched poller-reopen candidate. Mark each plan criterion complete only from evidence.

**Approach:** Run affected focused tests, then one fast suite and one Scale suite at the branch gate. Check Release warnings/errors through the wrapper build, `git diff --check`, and every related worktree state. Update findings and Goldfish before any local commit.

**Acceptance criteria:**
- [ ] Whole-suite and idle before/after comparisons use identical workloads.
- [ ] All hard gates pass; report-only metrics are recorded without flaky thresholds.
- [ ] Poller reopen and other deferred hot spots remain explicit, not silently declared fixed.
- [ ] Plan checklist and verification ledger match executed evidence.
- [ ] Final integration verification passes and the diff is handed to the lead.
