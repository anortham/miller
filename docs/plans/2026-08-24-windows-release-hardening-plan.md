# Windows Release Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Close the Windows correctness and fast-suite performance findings exposed after the CT performance audit, with local NTFS evidence before another push or patch-release decision.

**Architecture:** Persisted cache-debt paths stay platform-neutral at the coordinator boundary. Test fixtures model control files and symlink capability honestly. Large synthetic SQLite setup uses one explicit batch-write seam, while real subprocess and allocation-performance tests live in the existing Scale tier.

**Tech Stack:** .NET 10, xUnit v3, Microsoft.Data.Sqlite, PowerShell, `win-test` Windows 11/NTFS guest.

**Architecture Quality:**

- **Affected modules:** `ContinuousTestCoordinator` maintenance persistence and Miller test infrastructure only.
- **Caller-facing interface:** no production API change; one test-only structural-fact batch interface.
- **Depth/locality check:** normalization stays beside persistence; batching hides connection/transaction details from fixture callers.
- **Test surface:** existing maintenance behavior, real janitor outcomes, structural-fact readers, and fast/Scale test discovery.
- **Seams/adapters:** no new production seam; the existing `win-test` CLI is the Windows execution adapter.
- **Rejected shortcuts:** changing slash assertions, raising CI timeouts, or tagging every SQLite test Scale without measuring it.
- **Architecture risk:** low.

## Global Constraints

- Leave the user's QML Goldfish planning files on `main` untouched.
- Keep fast and Scale suites separate; real subprocess and performance-measurement tests do not remain in the fast suite.
- Use the exact Windows guest clone on NTFS through `win-test`; never SMB, virtiofs, direct SSH, `virsh`, or guest-agent exec.
- Use TDD for behavior changes and preserve existing assertions unless the fixture itself is proven wrong.
- Do not change release versions, manifests, tags, or published releases in this plan.

## Verification Strategy

**Project source of truth:** `AGENTS.md` testing/build/release sections and `.github/workflows/ci.yml`.

**Worker red/green scope:** the narrowest affected test class or method via `dotnet test --filter "FullyQualifiedName~<name>"`; existing Windows failures provide RED for Tasks 1 and 2. Task 3 adds a batch behavior test before implementation. Task 4 proves test discovery under both `Category!=Scale` and `Category=Scale`.

**Worker ceiling:** focused classes only; workers do not run `scripts/test.sh`, Scale suites, or `win-test`.

**Worker gate invariant:** Task 1 proves persisted cache paths are slash-normalized; Task 2 proves cache bytes exclude test control-marker content and symlink assertions run only where symlinks exist; Task 3 proves batch fixture rows are reader-visible through one batch operation; Task 4 proves measured integration/performance cases cannot enter the default suite.

**Lead affected-change scope:** focused Linux classes plus the identical Windows cache classes and measured hot tests through `win-test`.

**Branch gate:** `dotnet build Miller.slnx -c Release`; `scripts/test.sh`; `scripts/test.sh scale`; `scripts/test-plugin.sh`; Windows `scripts/test.ps1` and `scripts/test.ps1 scale` through `win-test`; `git diff --check`; `cmp -s CLAUDE.md AGENTS.md`.

**Security scope:** `security-secrets`: `gitleaks detect --source . --no-banner --log-opts=HEAD`; `security-deps`: `dotnet list Miller.slnx package --vulnerable --include-transitive`.

**Replay/metric evidence:** hard gates are zero test failures, correct fast/Scale discovery, and all five Windows regressions passing. Windows wall time is report-only but must use the same guest, SHA, suite command, and NTFS clone; baseline is 9m53s-10m for 8,381 tests, with the top three fixture tests at 1m45s, 1m40s, and 1m31s.

**Escalation triggers:** any Windows failure after the focused fixes; a final fast-suite test above 30 seconds; missing Windows Scale toolchain; or changed files outside task ownership.

**Assigned verification failure:** Workers stop and report when assigned verification fails unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp. Reuse passing evidence only for the same HEAD and scope.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Normalize persisted cache paths | Batch A | `src/Miller.Testing/Daemon/ContinuousTestCoordinator.cs`; `tests/Miller.Tests/Testing/Daemon/Engine/CtBuildCacheMaintenanceTests.cs` | No | None - safe parallel batch. |
| Task 2: Make janitor fixtures portable | Batch A | `tests/Miller.Tests/Testing/Daemon/Engine/CtBuildCacheJanitorTests.cs` | No | None - safe parallel batch. |
| Task 3: Batch large structural-fact fixtures | Batch A | `tests/Miller.Tests/Indexing/JulieDbFixture.cs`; `tests/Miller.Tests/Indexing/JulieDbFixtureTests.cs`; `tests/Miller.Tests/Server/MarkerSearchTests.cs`; `tests/Miller.Tests/Indexing/MetricSnapshotAggregatesTests.cs` | No | None - safe parallel batch. |
| Task 4: Restore the fast/Scale boundary | Batch B | `tests/Miller.Tests/Testing/Store/Core/ContinuousTestStoreTests.cs`; `tests/Miller.Tests/Indexing/SharedSemanticBrokerConnectionFactoryTests.cs`; `tests/Miller.Tests/Testing/Daemon/ControlPlane/CtDaemonLauncherTests.cs`; `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` | No | None - safe parallel batch. |
| Task 5: Record closure evidence | None - serial | `docs/findings/2026-08-23-performance-audit.md`; this plan's acceptance boxes and verification ledger | Yes | Requires final Linux and Windows evidence from Tasks 1-4. |

### Task 1: Normalize persisted cache paths

**Files:**
- Modify: `src/Miller.Testing/Daemon/ContinuousTestCoordinator.cs:519-553`
- Test: `tests/Miller.Tests/Testing/Daemon/Engine/CtBuildCacheMaintenanceTests.cs`

**Interfaces:**
- Consumes: `Path.GetRelativePath` and existing `ReapLedger` persistence.
- Produces: slash-normalized relative paths for both removed cache entries and reap debts.

**Contract inputs:** Stored relative paths use `/` on every OS; existing Linux assertions remain unchanged.

**File ownership:** `src/Miller.Testing/Daemon/ContinuousTestCoordinator.cs`; `tests/Miller.Tests/Testing/Daemon/Engine/CtBuildCacheMaintenanceTests.cs`

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**What to build:** Normalize both `cache.RemovedPaths` and `cache.Debts` at `RunMaintenanceTail` before they enter `ReapLedger`. Keep the helper private and local to the coordinator.

**Approach:** Follow existing persisted-path code: `Path.GetRelativePath(...).Replace(Path.DirectorySeparatorChar, '/')`. Existing Windows failures are the RED; do not change their expected slash strings.

**Acceptance criteria:**
- [x] Both cache success and debt paths persist with `/` on Windows.
- [x] Existing maintenance tests pass without weakening assertions.
- [x] Worker-scope verification passes and changes are handed to the lead for review.

### Task 2: Make janitor fixtures portable

**Files:**
- Test: `tests/Miller.Tests/Testing/Daemon/Engine/CtBuildCacheJanitorTests.cs`

**Interfaces:**
- Consumes: real `CtBuildCacheJanitor` byte accounting and platform symlink capability.
- Produces: fixtures whose marker contributes zero data bytes and tests with one behavior per platform capability.

**Contract inputs:** Production correctly counts every file byte; Windows cannot assume unprivileged symbolic-link creation.

**File ownership:** `tests/Miller.Tests/Testing/Daemon/Engine/CtBuildCacheJanitorTests.cs`

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**What to build:** Make `MarkRoot` create a zero-byte marker. Split the mixed foreign-path/symlink test so foreign-path behavior runs everywhere and symlink behavior explicitly skips on Windows using the repository's `Assert.Skip` pattern.

**Approach:** Preserve production accounting. Do not special-case marker filenames in production and do not leave a Windows branch asserting evidence it never created.

**Acceptance criteria:**
- [ ] The three Windows janitor failures pass on NTFS.
- [x] The POSIX symlink behavior remains covered and Windows reports an explicit skip.
- [x] Worker-scope verification passes and changes are handed to the lead for review.

### Task 3: Batch large structural-fact fixtures

**Files:**
- Modify: `tests/Miller.Tests/Indexing/JulieDbFixture.cs:356-380`
- Create: `tests/Miller.Tests/Indexing/JulieDbFixtureTests.cs`
- Modify: `tests/Miller.Tests/Server/MarkerSearchTests.cs:88-155`
- Modify: `tests/Miller.Tests/Indexing/MetricSnapshotAggregatesTests.cs:67-98`

**Interfaces:**
- Consumes: the existing single-row structural-fact shape.
- Produces: one test-only batch API that prepares one command, uses one connection/transaction, and keeps single-row callers compatible.

**Contract inputs:** Rows retain the exact fields/defaults of `AddStructuralFact`; reader-visible behavior is unchanged.

**File ownership:** `tests/Miller.Tests/Indexing/JulieDbFixture.cs`; `tests/Miller.Tests/Indexing/JulieDbFixtureTests.cs`; `tests/Miller.Tests/Server/MarkerSearchTests.cs`; `tests/Miller.Tests/Indexing/MetricSnapshotAggregatesTests.cs`

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**What to build:** Add a compact structural-fact input type and batch insert method to `JulieDbFixture`; make `AddStructuralFact` use the same implementation. Convert the three 500-row fixture loops to one batch call.

**Approach:** TDD through a reader-visible batch test. Keep transaction/command details inside the fixture. Do not disable SQLite durability globally and do not change production readers.

**Acceptance criteria:**
- [x] Batch and single-row APIs produce identical reader-visible rows.
- [x] The three Windows hot tests no longer perform hundreds of connection/autocommit cycles.
- [ ] Focused Windows durations are recorded before and after on the same VM.
- [x] Worker-scope verification passes and changes are handed to the lead for review.

### Task 4: Restore the fast/Scale boundary

**Files:**
- Modify: `tests/Miller.Tests/Testing/Store/Core/ContinuousTestStoreTests.cs:601-620`
- Modify: `tests/Miller.Tests/Indexing/SharedSemanticBrokerConnectionFactoryTests.cs:13-578`
- Modify: `tests/Miller.Tests/Testing/Daemon/ControlPlane/CtDaemonLauncherTests.cs:14-709`
- Modify: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs:3432-3498`

**Interfaces:**
- Consumes: xUnit `Category=Scale` and the project default `Category!=Scale` filter.
- Produces: correct discovery classification for the allocation benchmark and measured real-subprocess tests.

**Contract inputs:** Method-level traits are required for the mixed `CliDispatchTests` class; class-level traits are used where the whole class owns the expensive behavior.

**File ownership:** `tests/Miller.Tests/Testing/Store/Core/ContinuousTestStoreTests.cs`; `tests/Miller.Tests/Indexing/SharedSemanticBrokerConnectionFactoryTests.cs`; `tests/Miller.Tests/Testing/Daemon/ControlPlane/CtDaemonLauncherTests.cs`; `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**What to build:** Mark the allocation benchmark, shared-broker-host class, daemon-launcher subprocess class, and the shared-broker CLI theory as Scale. Keep pure tests in mixed files fast.

**Approach:** Use class traits only when every test in that class owns the costly behavior; use method traits for mixed classes. Verify both include and exclude discovery filters.

**Acceptance criteria:**
- [x] `Category!=Scale` excludes all assigned tests.
- [x] `Category=Scale` discovers all assigned tests.
- [x] No unrelated `CliDispatchTests` are removed from the fast suite.
- [x] Worker-scope verification passes and changes are handed to the lead for review.

### Task 5: Record closure evidence

**Files:**
- Modify: `docs/findings/2026-08-23-performance-audit.md`
- Modify: `docs/plans/2026-08-24-windows-release-hardening-plan.md`

**Interfaces:**
- Consumes: reviewed commits and final Linux/Windows verification ledger.
- Produces: a blunt post-merge Windows closure matrix with no hidden open/deferred item.

**Contract inputs:** Record exact SHAs, test counts, before/after Windows timings, and any remaining blocker honestly.

**File ownership:** `docs/findings/2026-08-23-performance-audit.md`; this plan's acceptance boxes and verification ledger

**Serialization required:** Yes.

**Dependency reason:** Requires final Linux and Windows evidence from Tasks 1-4.

**What to build:** Append a dated Windows release-gate section covering path portability, janitor fixtures, fixture-write amplification, Scale leakage, and the local `win-test` evidence.

**Approach:** Status every finding fixed/deferred/open. Do not repeat the prior claim that none remain unless every listed gate passes on the final SHA.

**Acceptance criteria:**
- [ ] Every newly discovered Windows finding has a status and fixed SHA/file evidence.
- [ ] Before/after timings use the same Windows guest workload and distinguish hard gates from report-only metrics.
- [ ] The document names any release blocker that remains.
- [ ] Worker-scope verification passes and changes are committed after lead review.
