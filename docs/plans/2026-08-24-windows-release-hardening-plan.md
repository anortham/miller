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
| Task 5: Batch fact-cache fixtures and bound the lock probe | Batch C | `tests/Miller.Tests/Indexing/Resolution/ResolutionStoreFixture.cs`; `tests/Miller.Tests/Indexing/Resolution/ResolutionStoreFixtureTests.cs`; `tests/Miller.Tests/Indexing/Resolution/BoundedRevisionFactCacheTests.cs`; `tests/Miller.Tests/Indexing/FamilyStoreReadSessionTests.cs` | No | None - safe parallel batch. |
| Task 6: Batch failure-page seeding | Batch C | `tests/Miller.Tests/Server/TestsToolTests.cs` | No | None - safe parallel batch. |
| Task 7: Record closure evidence | None - serial | `docs/findings/2026-08-23-performance-audit.md`; this plan's acceptance boxes and verification ledger | Yes | Requires final Linux and Windows evidence from Tasks 1-6. |

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
- [x] The three Windows janitor failures pass on NTFS.
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
- [x] Focused Windows durations are recorded before and after on the same VM.
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

### Task 5: Batch fact-cache fixtures and bound the lock probe

**Files:**
- Modify: `tests/Miller.Tests/Indexing/Resolution/ResolutionStoreFixture.cs`
- Create: `tests/Miller.Tests/Indexing/Resolution/ResolutionStoreFixtureTests.cs`
- Modify: `tests/Miller.Tests/Indexing/Resolution/BoundedRevisionFactCacheTests.cs:406-484`
- Modify: `tests/Miller.Tests/Indexing/FamilyStoreReadSessionTests.cs:370-407`

**Interfaces:**
- Consumes: existing `ResolutionStoreFixture` row builders and the snapshot-preserving family read session.
- Produces: one test-fixture transaction scope with a deterministic write-connection count; an immediate expected-lock probe.

**Contract inputs:** `Populate` currently opens about 78 write connections; the family lock probe currently waits the default 30-second SQLite timeout before accepting the expected lock refusal.

**File ownership:** `tests/Miller.Tests/Indexing/Resolution/ResolutionStoreFixture.cs`; `tests/Miller.Tests/Indexing/Resolution/ResolutionStoreFixtureTests.cs`; `tests/Miller.Tests/Indexing/Resolution/BoundedRevisionFactCacheTests.cs`; `tests/Miller.Tests/Indexing/FamilyStoreReadSessionTests.cs`

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**What to build:** Add an explicit fixture transaction scope that lets existing row builders reuse one connection/transaction, with a count guard proving the connection amplification is gone. Wrap `BoundedRevisionFactCacheTests.Populate` in it. Set `PRAGMA busy_timeout=0` before the family test's deliberately conflicting delete.

**Approach:** Keep production loaders unchanged. Preserve all parity/accessor assertions and the snapshot lock-refusal behavior. Follow the existing `PRAGMA busy_timeout=0` test pattern.

**Acceptance criteria:**
- [x] The populated parity fixture uses one write connection instead of about 78.
- [x] The family snapshot test still proves its lazy slice survives the attempted mutation without a 30-second wait.
- [x] Focused Linux and Windows timings are recorded before and after.
- [x] Worker-scope verification passes and changes are handed to the lead for review.

### Task 6: Batch failure-page seeding

**Files:**
- Modify: `tests/Miller.Tests/Server/TestsToolTests.cs:715-773`

**Interfaces:**
- Consumes: `ContinuousTestStore.Transaction`, `PutTestCase`, `StartContinuousTestRun`, and `CompleteContinuousTestRun`.
- Produces: identical real completion-path fixtures inside one outer transaction.

**Contract inputs:** Five measured tests currently create 630 lock/open/commit boundaries; nested store writes already reuse an outer transaction.

**File ownership:** `tests/Miller.Tests/Server/TestsToolTests.cs`

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**What to build:** Wrap the existing `SeedCases` loop in one `store.Transaction` without bypassing any production store API or changing case/run/result content.

**Approach:** Preserve the real run-completion path and every paging assertion. The before metric is 630 writer boundaries and 36-50 seconds under full Windows contention; the after hard count is five outer transactions across the five measured tests.

**Acceptance criteria:**
- [x] The five measured tests retain identical paging/render/MCP behavior.
- [x] Writer boundaries fall from 630 to 5 for the fixed workload.
- [x] Focused Linux and Windows timings are recorded before and after.
- [x] Worker-scope verification passes and changes are handed to the lead for review.

### Task 7: Record closure evidence

**Files:**
- Modify: `docs/findings/2026-08-23-performance-audit.md`
- Modify: `docs/plans/2026-08-24-windows-release-hardening-plan.md`

**Interfaces:**
- Consumes: reviewed commits and final Linux/Windows verification ledger.
- Produces: a blunt post-merge Windows closure matrix with no hidden open/deferred item.

**Contract inputs:** Record exact SHAs, test counts, before/after Windows timings, and any remaining blocker honestly.

**File ownership:** `docs/findings/2026-08-23-performance-audit.md`; this plan's acceptance boxes and verification ledger

**Serialization required:** Yes.

**Dependency reason:** Requires final Linux and Windows evidence from Tasks 1-6.

**What to build:** Append a dated Windows release-gate section covering path portability, janitor fixtures, fixture-write amplification, Scale leakage, and the local `win-test` evidence.

**Approach:** Status every finding fixed/deferred/open. Do not repeat the prior claim that none remain unless every listed gate passes on the final SHA.

**Acceptance criteria:**
- [x] Every newly discovered Windows finding has a status and fixed SHA/file evidence.
- [x] Before/after timings use the same Windows guest workload and distinguish hard gates from report-only metrics.
- [x] The document names any release blocker that remains.
- [x] Worker-scope verification passes and changes are committed after lead review.

#### Task 7 closure verification ledger (2026-08-24)

The Windows rows use the same local Windows NTFS guest, `win-test` clone, suite commands, and final source
`933fc39dab683ac1e105ee8b083a1fd88ab9f4fe`. Counts and zero failures are hard gates; elapsed time is report-only.

| Invariant | Exact command/scope | Commit | Result | Evidence class |
| --- | --- | --- | --- | --- |
| Windows Release build | `win-test run miller -- dotnet build Miller.slnx -c Release` | `933fc39d` | PASS, 0 warnings / 0 errors | hard gate |
| Original focused Windows regressions | Cache maintenance/janitor focused classes | `933fc39d` | PASS, `19 passed / 1 expected skip / 0 failed` | hard gate |
| Windows fast suite | `win-test run miller -- powershell -File scripts/test.ps1` | `933fc39d` | PASS, `8,326 passed / 25 skipped / 0 failed`; `538s` wrapper | counts hard; time report-only |
| Windows Scale suite | `win-test run miller -- powershell -File scripts/test.ps1 scale` | `933fc39d` | PASS, `198 passed / 13 skipped / 0 failed` | hard gate |
| Linux Release build | `dotnet build Miller.slnx -c Release` | `933fc39d` | PASS, 0 warnings / 0 errors | hard gate |
| Linux fast suite | `scripts/test.sh` | `933fc39d` | PASS, `8,342 passed / 9 skipped / 0 failed` | hard gate |
| Linux Scale suite | `scripts/test.sh scale` | `933fc39d` | PASS, `195 passed / 16 skipped / 0 failed` | hard gate |
| Plugin suite | `scripts/test-plugin.sh` | `933fc39d` | PASS, `49 passed / 0 failed` | hard gate |
| Secret scan | `gitleaks detect --source . --no-banner --log-opts=HEAD` | `933fc39d` | PASS, no leaks | hard gate |
| Dependency scan | `dotnet list Miller.slnx package --vulnerable --include-transitive` | `933fc39d` | PASS, no vulnerable packages | hard gate |
| Documentation/source hygiene | `git diff --check`; `cmp -s CLAUDE.md AGENTS.md` | working tree | PASS, clean | hard gate |

No code, release version, tag, or publish remains in this task. The final unchecked criterion is intentionally
owned by the lead: commit these reviewed documentation edits before declaring the task complete.
