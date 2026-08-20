# CT Dogfood Defects and the 2.34.4 Pin — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Give the CT daemon a working diagnostic log, let it store a pre-enumerated test inventory without a database error, and finish the julie-extract 2.34.4 pin.

**Architecture:** Three independent repairs in `Miller.Testing`, plus documentation for the pin. The logging repair adds a fail-safe write and connects an existing but unused callback. The discovery repair removes a unique index that theory pre-enumeration made wrong, and fixes the result attribution that the removal exposes. The freshness repair is NOT in this plan — it is a decision, recorded at the end.

**Tech Stack:** C# / .NET 10, xUnit v3, SQLite (`Microsoft.Data.Sqlite`).

**Architecture Quality:** No new modules and no new interfaces. Two existing seams carry the work: the `Action<string>? lifecycleLog` callback on `ContinuousTestDaemonQueue`, and the `ct.db` schema in `CtSchema`. The one new piece of shape is a schema-version migration path in `CtSchema`, which the file does not have today. Architecture risk: **medium**, because Task 4 changes a table constraint that existing `ct.db` files in the field already carry.

## Global Constraints

- `ct.db` is a Miller-owned derived sidecar. It holds no foreign key into `symbols.db` or `search.db`.
- `MILLER_CT=off` (also `0`, `false`, `no`) stays a permanent zero-work guarantee. No task may create `.miller/logs`, `.miller/ct/`, or `ct.db` on the disabled path.
- Status reads never create `ct.db`, never create `.miller/ct/`, and never start the daemon.
- Green requires complete results at the selected composite key. No task may let one result row stand for several test cases.
- The build must be 0 warnings and 0 errors. `TreatWarningsAsErrors` is on.
- A test that spawns a real CT provider (`dotnet test`, `cargo test`, node, pytest) MUST carry `[Trait("Category","Scale")]` at class level and MUST get its toolchain from `CtProviderTestSupport`.
- `Miller.Core` keeps zero I/O dependencies.
- Do not delete `ct.db` as a repair. A fresh file is built from the same DDL, so it fails the same way.
- Do not edit `RevisionDeltaReader.cs`. `search.db`, `content.db`, and `vectors.db` all depend on its family-id comparison.
- Do not change `WorkspaceReadSnapshot.IndexIdentity`. `FreshnessService` and `IndexBootstrapService` need it to change per revision.

## Verification Strategy

**Project source of truth:** [`CLAUDE.md`](../../CLAUDE.md), section "Testing — read this before running tests".

**Worker red/green scope:** `dotnet test --filter "FullyQualifiedName~<TestClassName>" -c Debug --no-restore` for the one class the task touches.

**Worker ceiling:** One test class, or the two or three classes the task changed. Workers do not run the fast suite and do not run the Scale suite.

**Worker gate invariant:**
- Task 1: a failing log append does not stop the daemon loop.
- Task 2: a discovery failure writes its reason, with exception type and stack, to the shared daily log; a disabled daemon writes no file.
- Task 3: a theory method with N data rows produces N result rows, not one.
- Task 4: an inventory holding two cases that share one selector is stored in full, and an existing `ct.db` is migrated in place.
- Task 5: documentation only, no gate.

**Lead affected-change scope:** `dotnet test -c Debug --no-restore --filter "FullyQualifiedName~Testing"` after Tasks 1–4 land together.

**Branch gate:** `CONFIG=Debug scripts/test.ps1` then `CONFIG=Debug scripts/test.ps1 scale`.
Use `CONFIG=Debug` while the MCP server (pid 5140 at time of writing) holds the Release output. When the user has restarted the server, run the branch gate again with the default Release configuration, because the Release binary is what ships.

**Security scope:** none declared.

**Replay/metric evidence:** The four live experiments in "Live experiments still owed" are hard gates for calling Task 3 and Task 4 done. Case counts are report-only; the pass condition is structural, not a fixed number.

**Escalation triggers:**
- Any change under `src/Miller.Testing/Providers/` or `src/Miller.Testing/Store/` requires the Scale suite before handoff.
- Any change to `CtSchema.cs` requires the live migration experiment on a copy of the real `ct.db`.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp. Reuse a passing entry at the same HEAD instead of rerunning the same gate.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Make the CT log write fail-safe | Batch A | Modify `src/Miller.Testing/Daemon/CtDaemonLog.cs`; test `tests/Miller.Tests/Testing/Daemon/ControlPlane/CtDaemonLogTests.cs` | No | None - safe parallel batch. |
| Task 2: Connect the daemon's diagnostics | None - serial | Modify `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs`, `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs`, `src/Miller.Server/Tools/TestsCore.cs`, `tests/Miller.Tests/Conventions/CtDiagnosticSinkConventionTests.cs` | Yes | Task 1 must land first. Task 2 adds three call sites inside last-resort catch blocks; without Task 1's guard an `IOException` from the append ends `RunAsync` and kills the daemon. |
| Task 3: Keep one result row per theory data row | Batch A | Modify `src/Miller.Testing/Daemon/ContinuousTestCoordinator.cs`, `src/Miller.Testing/Importers/JunitTestArtifactImporter.cs`; test `tests/Miller.Tests/Testing/Analysis/JunitTestArtifactImporterTests.cs` | No | None - safe parallel batch. |
| Task 4: Let many cases share one selector | None - serial | Modify `src/Miller.Testing/Store/CtSchema.cs`, `src/Miller.Testing/Store/ContinuousTestStore.cs`, `src/Miller.Testing/Providers/Dotnet/DotnetTestProvider.cs`; test `tests/Miller.Tests/Testing/CtSchemaTests.cs`, `tests/Miller.Tests/Testing/Store/Core/ContinuousTestStoreTests.cs` | Yes | Task 3 must land first. Removing the unique index is what creates the extra rows that Task 3 teaches the importer to attribute. Landing Task 4 alone turns a hard database error into a silent false green. |
| Task 5: Record the 2.34.4 pin | Batch A | Create `docs/findings/2026-08-20-julie-extract-2.34.4-adoption.md`; modify `docs/README.md:37` | No | None - safe parallel batch. |

Commit mode: `parallel-lead-commit`. Workers hand a verified diff to the lead. The lead stages and commits after inline review.

---

### Task 1: Make the CT log write fail-safe

**Files:**
- Modify: `src/Miller.Testing/Daemon/CtDaemonLog.cs:30-62`
- Test: `tests/Miller.Tests/Testing/Daemon/ControlPlane/CtDaemonLogTests.cs` (exists — extend it)

**Interfaces:**
- Consumes: nothing from an earlier task.
- Produces: `CtDaemonLog.Write(string workspaceRoot, string message, DateTimeOffset? utcNow = null, int? pid = null)` keeps its signature and never throws `IOException` or `UnauthorizedAccessException`. Task 2 relies on that guarantee.

**Contract inputs:** The daemon already uses this exact guard shape for its status writes at `ContinuousTestDaemonHost.cs:379`, `:428`, and `:527`. Match it.

**File ownership:** Modify `src/Miller.Testing/Daemon/CtDaemonLog.cs`; test `tests/Miller.Tests/Testing/Daemon/ControlPlane/CtDaemonLogTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** `CtDaemonLog.Write` opens a file and appends. Today any I/O error escapes. Task 2 calls it from inside last-resort catch blocks, where an escaping exception ends the daemon's `RunAsync` loop. Swallow the two I/O exception types so a log failure degrades to no log, never to a dead daemon.

**Approach:** Wrap the body of `Write` — including `Directory.CreateDirectory` and both `AppendLine` calls — in `catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }`. Do not swallow anything else; an `ArgumentException` from a bad workspace root is a caller bug and must still throw. Keep the two argument guards at the top outside the try.

**Acceptance criteria:**
- [x] `Write` returns normally when the log path cannot be created or opened.
- [x] `Write` still throws `ArgumentException` for a null, empty, or whitespace `workspaceRoot` or `message`.
- [x] A test proves a caller loop survives a failing append.
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode.

---

### Task 2: Connect the daemon's diagnostics

**Files:**
- Modify: `src/Miller.Server/Tools/TestsCore.cs:381` and `:804` (queue construction), `:353` and `:385` (host options)
- Modify: `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs:632` (`RecordDiscoveryFailure`)
- Modify: `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs` (poll-error catch; disabled branch at `:160-172`)
- Test: `tests/Miller.Tests/Conventions/CtDiagnosticSinkConventionTests.cs:29`

**Interfaces:**
- Consumes: `CtDaemonLog.Write` never throws on I/O (Task 1).
- Produces: `ContinuousTestDaemonHostOptions` gains `Action<string>? Diagnostic`. `ContinuousTestDaemonQueue` keeps its existing `Action<string>? lifecycleLog` parameter, which is parameter 5 with a `null` default at `ContinuousTestDaemonQueue.cs:35`.

**Contract inputs:**
- `ContinuousTestDaemonQueue.Log` is `private void Log(string message) => _lifecycleLog?.Invoke(message);` at `:793`. It is already called at four places. All four are dead because both production constructions omit the argument.
- `TestsCore.cs:381` is `new ContinuousTestDaemonQueue(store, selector, coordinator, runActivity: runActivity);`
- `TestsCore.cs:804` is `new ContinuousTestDaemonQueue(store, selector, coordinator);`
- The daemon is the `ct-daemon` CLI verb (`CliDispatch.cs:156`). The CLI branch starts no Serilog sink, so `CtDaemonLog` is the only path to `.miller/logs`.

**File ownership:** Modify `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs`, `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs`, `src/Miller.Server/Tools/TestsCore.cs`, `tests/Miller.Tests/Conventions/CtDiagnosticSinkConventionTests.cs`

**Serialization required:** Yes

**Dependency reason:** Task 1 must land first. Task 2 adds three call sites inside last-resort catch blocks; without Task 1's guard an `IOException` from the append ends `RunAsync` and kills the daemon.

**What to build:** The daemon records failures only into `ct.db`. Finding the last discovery failure needed a database query. Route the daemon's failures to the shared daily log with `role:ct`, so the next dogfood run can be read instead of queried.

**Approach:**
1. Pass `lifecycleLog: message => CtDaemonLog.Write(root, message)` at both `TestsCore.cs:381` and `:804`. That alone revives four existing log lines.
2. Add one `Log(...)` call at the top of `RecordDiscoveryFailure`. Write the exception type, the **full** message, and a flattened stack on ONE line. `FailureSummary` deliberately keeps only the first line for the database column; the log must not inherit that truncation.
3. Add `Action<string>? Diagnostic` to `ContinuousTestDaemonHostOptions` and wire it at `TestsCore.cs:353` and `:385`. Use it in the poll-error catch, which throws its exception away today. **Invoke it only on the live branch.** The disabled branch returns at `ContinuousTestDaemonHost.cs:160-172` before it builds anything; a log call there would create `.miller/logs` under `MILLER_CT=off`.
4. Generalize `CtDiagnosticSinkConventionTests`. It hard-codes the single token `onDiagnostic:` at line 29, so it stays green while the queue is silent. Make the helper take a (construction, token, expected site count) triple, then add rows for `lifecycleLog:` and `Diagnostic =`.

**Known and out of scope, record but do not fix here:** `CtDaemonLog` stamps the file name and timestamp in UTC while Serilog uses local time, so a CT line written after 19:00 local lands in the next day's file. Serilog also rolls the daily file at 32 MiB and `CtDaemonLog` always writes the un-suffixed name. Raise both as follow-ups.

**Acceptance criteria:**
- [x] Both production queue constructions pass a `lifecycleLog`.
- [x] A discovery failure writes one log line carrying the exception type, the full message, and a stack.
- [x] A poll error writes one log line instead of being discarded.
- [x] A test proves a disabled daemon (`MILLER_CT=off`) writes no log file and creates no directory.
- [x] `CtDiagnosticSinkConventionTests` fails if any of the three sinks is dropped.
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode.

**Lead notes.** The disabled branch is wired with a live sink on purpose: the zero-work guarantee now
rests on the branch returning before the loop starts, not on a caller remembering to pass null, and
`ForbiddenEnqueueTests.A_disabled_daemon_writes_no_log_line_and_creates_no_logs_directory` holds it.
Fix round 1 moved the duplicated `FailureDetail`/`Flatten` helpers into `CtDaemonLog` so both failure
lines share one format.

---

### Task 3: Keep one result row per theory data row

**Files:**
- Modify: `src/Miller.Testing/Daemon/ContinuousTestCoordinator.cs:981-985` (`TestCaseIdsByArtifactSelector`)
- Modify: `src/Miller.Testing/Importers/JunitTestArtifactImporter.cs`
- Test: `tests/Miller.Tests/Testing/Analysis/JunitTestArtifactImporterTests.cs` (exists — extend it)

**Interfaces:**
- Consumes: nothing from an earlier task.
- Produces: an artifact-row-to-case-id mapping that resolves by full display name first, so N theory rows resolve to N distinct case ids. Task 4 depends on this being in place before it relaxes the constraint.

**Contract inputs:**
- `TestCaseIdsByArtifactSelector` keys a dictionary by selector **and** by `<class>::<method>`. Both keys collapse for a theory, because `XunitMethodName` cuts the display name at the first `(` (`DotnetTestProvider.cs:1622-1626`).
- Result ids are upserted on `(workspace_id, test_case_id, test_run_id)`.
- Case id is `"xunit:" + DisplayName` (`DotnetTestProvider.cs:1620`).

**File ownership:** Modify `src/Miller.Testing/Daemon/ContinuousTestCoordinator.cs`, `src/Miller.Testing/Importers/JunitTestArtifactImporter.cs`; test `tests/Miller.Tests/Testing/Analysis/JunitTestArtifactImporterTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Today every theory data row maps to the same case id, so N results write ONE row and the last write wins. A red row can be overwritten by a green sibling. That breaks the rule that green requires complete results. Fix the mapping so each data row keeps its own result.

**Approach:** Resolve an artifact row to a case id by its full display name before falling back to the selector or the `<class>::<method>` key. The JUnit artifact carries the full display name for each data row. Keep the existing selector fallback for providers that do not pre-enumerate, and for a theory whose data cannot be enumerated up front — that case legitimately stays one row in both discovery and run.

This task is verifiable on its own even before Task 4, because the mapping is exercised by fixtures, not by a live inventory.

**Acceptance criteria:**
- [ ] A JUnit artifact holding three rows of one theory resolves to three distinct case ids.
- [ ] A red data row is not overwritten by a green sibling of the same theory.
- [ ] A provider row that carries no per-row display name still resolves through the selector fallback.
- [ ] Worker-scope verification passes and the change is handed to the lead per commit mode.

---

### Task 4: Let many cases share one selector

**Files:**
- Modify: `src/Miller.Testing/Store/CtSchema.cs:50` (the `UNIQUE (workspace_id, selector, source)` line) and `CtSchema.Apply`
- Modify: `src/Miller.Testing/Store/ContinuousTestStore.cs:219` if the upsert needs adjusting after the index is gone
- Modify: `src/Miller.Testing/Providers/Dotnet/DotnetTestProvider.cs:1620` (`XunitTestCaseId`)
- Test: `tests/Miller.Tests/Testing/CtSchemaTests.cs`, `tests/Miller.Tests/Testing/Store/Core/ContinuousTestStoreTests.cs`

**Interfaces:**
- Consumes: Task 3's per-display-name result attribution.
- Produces: `test_cases` no longer requires a unique selector. `CtSchema` gains a schema-version migration path.

**Contract inputs:**
- The failure recorded in the live `ct.db` is exactly `SQLite Error 19: 'UNIQUE constraint failed: test_cases.workspace_id, test_cases.selector, test_cases.source'.` for all three projects.
- Measured on this repo's `Miller.Tests` assembly with `-list full/json -noLogo -noColor -preEnumerateTheories`: **7697 cases discovered, 7695 distinct case ids, 6237 distinct selectors, 407 selectors shared by more than one case, 1460 colliding rows.** The worst single method carries 42 rows.
- `ContinuousTestStoreApplier` runs the prune and every insert in ONE transaction, so the first collision rolls back the whole inventory update.
- `CtSchema.Apply` today runs `CREATE TABLE IF NOT EXISTS` only. There is no `ALTER TABLE`, no `user_version` handling, and no migration code.
- The live index is the implicit `sqlite_autoindex_test_cases_2`.

**File ownership:** Modify `src/Miller.Testing/Store/CtSchema.cs`, `src/Miller.Testing/Store/ContinuousTestStore.cs`, `src/Miller.Testing/Providers/Dotnet/DotnetTestProvider.cs`; test `tests/Miller.Tests/Testing/CtSchemaTests.cs`, `tests/Miller.Tests/Testing/Store/Core/ContinuousTestStoreTests.cs`

**Serialization required:** Yes

**Dependency reason:** Task 3 must land first. Removing the unique index is what creates the extra rows that Task 3 teaches the importer to attribute. Landing Task 4 alone turns a hard database error into a silent false green.

**What to build:** Three connected changes.

**4a — Remove the selector uniqueness.** One xUnit `-method` selector legitimately runs many theory rows. The design already knows this: the comment at `DotnetTestProvider.cs:1614` says `XunitSelectionUnits` collapses a method's rows to one `-method` unit. Row identity is already the primary key `id`. Do **not** replace the constraint with `UNIQUE (workspace_id, source, selector, qualified_name)` — `id`, `FullyQualifiedName`, and `qualified_name` all derive from the same display name, so that constraint can never fire. It would only restate the primary key.

**4b — Write a real migration.** Without one, the fix passes its tests and every existing `ct.db` in the field still fails. Add `PRAGMA user_version` handling to `CtSchema.Apply`: read the version, and when it is below the new value, rebuild `test_cases` without the unique index (create the new table, copy rows, drop the old, rename), then set the version. Do this inside a transaction. `ct.db` is a derived sidecar with no foreign key into `symbols.db`, but `test_results`, `ct_test_states`, and `ct_case_fresh_watermarks` all reference `test_cases(id)` — take `PRAGMA foreign_keys` state into account so the copy does not cascade-delete them.

**4c — Stop deriving identity from a truncated string.** xUnit truncates long theory display names. Three rows of `Miller.Tests.Indexing.ScanFailureJournalTests.TryRead_ADamagedRecord_DegradesToNoRecordedFailureInsteadOfThrowing` share one display name in the live output, so `XunitTestCaseId` gives all three the same id and they upsert onto each other. Two cases vanish silently. This survives 4a and 4b.

The runner's own `ID` is not usable — the comment at `DotnetTestProvider.cs:1580-1581` records that it hashes the assembly path and changes with every build generation. **This sub-task needs judgment, so state the choice in the handoff rather than picking silently.** The plan-consistent option is to disambiguate a repeated display name with a stable ordinal, assigned by position among the rows that share that display name, after sorting the discovery output deterministically. That keeps the id stable across builds as long as the theory's data rows keep their order and count. Record the weakness plainly: adding or removing a data row in the middle of a truncated-name theory renumbers the rows after it, and those cases lose their history.

**Acceptance criteria:**
- [ ] Storing an inventory in which two cases share one selector succeeds, and both rows are present afterwards.
- [ ] An existing `ct.db` built with the old schema is migrated in place, and its `test_results`, `ct_test_states`, and `ct_case_fresh_watermarks` rows survive.
- [ ] `CtSchemaTests` asserts the new `user_version` and the absence of the selector unique index.
- [ ] Two cases whose display names are identical after xUnit truncation both survive storage.
- [ ] The migration is proven on a **copy** of the real `C:\source\miller\.miller\ct.db`, not only on a fixture.
- [ ] Worker-scope verification passes and the change is handed to the lead per commit mode.

---

### Task 5: Record the 2.34.4 pin

**Files:**
- Create: `docs/findings/2026-08-20-julie-extract-2.34.4-adoption.md`
- Modify: `docs/README.md:37`

**Interfaces:**
- Consumes: nothing from an earlier task.
- Produces: the documentation pointer for the current pin.

**Contract inputs — all values below are measured, not quoted from release notes:**
- Pin moved `2.34.1` → `2.34.4`. Upstream tag `v2.34.4`, published `2026-08-20T17:14:32Z`, four assets, reported Latest.
- SHA-256, verified against the live release and re-verified by the restore script:
  - `aarch64-apple-darwin`: `0284de63b9f15b3aa546e234d40e1949cf88076415ab17c8842e1d5e76a0843b`
  - `x86_64-apple-darwin`: `f8a4a00319dc43a62a3116ad130df652823be8affdd5a003614c3404bbd7a23c`
  - `x86_64-unknown-linux-gnu`: `eb0aecba3963f246a2d2e05325d8536cbd585e03014e8e34356b16e9db078af8`
  - `x86_64-pc-windows-msvc`: `57f93f95165fdc5c0472c36fcf8864f27e758a4a2423d421efc769061708e86a`
- Contract constants UNCHANGED, verified by scanning a probe fixture with the restored binary: schema 7, sqlite schema 7, extract contract 4, report schema 3, JSONL 5, `blake3`.
- What changed upstream: 2.34.2 promotes `test_container` / `test_lifecycle` on QML, GDScript, Bash, and Scala symbols. 2.34.3 narrows test detection for Python, Scala, and Elixir — `pytest.fixture` and `unittest.mock.*` stop being test evidence, and a bare `test_` name now needs test-path evidence. 2.34.4 adds Windows test hardening and test-role closure.
- Measured consumer impact on THIS repo: extracting the repo's Python and bash sources with 2.34.4 and diffing against the live 2.34.1 store gave **5,866 common symbols and zero differences** in `(is_test, test_container, test_lifecycle)`. All 376 bash symbols carry zero for all three roles.
- Miller reads `is_test`. It does **not** read `test_container` or `test_lifecycle` anywhere.
- Four consumers read the bare `is_test` flag with no path fallback: `src/Miller.Indexing/ComplexityRankingReader.cs:56` and `:151`, `src/Miller.Indexing/ImpactAnalysis.cs:80` and `:144`.
- Verification already run: Debug build 0 warnings / 0 errors with the `VerifyPinnedJulieExtractVersion` guard passing; fast suite 7548 passed / 1 failed / 27 skipped / 7576 total, where the single failure (`SharedSemanticBrokerConnectionFactoryTests.PassiveObservation_DisposesAConnectedStreamCanceledBeforeSessionAcceptance`) passed on a focused re-run and failed only under parallel-agent load; Release compiles clean and fails only when copying DLLs into the running server's output folder.

**File ownership:** Create `docs/findings/2026-08-20-julie-extract-2.34.4-adoption.md`; modify `docs/README.md:37`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** An adoption finding in the same shape as `docs/findings/2026-08-18-julie-extract-2.34.1-adoption.md`, and a repointed map line.

**Approach:** Follow the existing finding's headings: pin moved, upstream, tag provenance, release state, what changed, compatibility, verification. Repoint `docs/README.md:37` from the 2.34.1 finding to the new one. **Leave `docs/README.md:170` alone** — it describes the v1.20.0 release's pin as history and is correct. `README.md` carries no julie-extract version, so it needs no edit. Add a standing note: for the next pin bump, prove safety with a producer diff on real sources, not with an argument about consumer robustness.

**Acceptance criteria:**
- [x] The finding records all four checksums and the measured contract constants.
- [x] The finding records the producer diff result and its method.
- [x] `docs/README.md:37` points at the new finding and names 2.34.4 as the current pin.
- [x] No historical release note or earlier finding is edited.

**Correction found during execution:** the contract input "`README.md` carries no julie-extract
version" is WRONG. `README.md:170` reads "`julie-extract` 2.23.1 ships hand-written extractors for
38 languages". That is stale drift against the 2.34.4 pin, it predates this task, and it sits
outside Task 5's file ownership. Deferred, not fixed here.

---

## Decision 1 — Defect 3, the freshness key

**No task is written for this. It needs your decision first.**

My earlier account of this defect was wrong, and I am correcting it plainly. I said a 106-second suite cannot finish before the index revision advances. Two checks refute that:

- All 12 run rows in `ct.db` have `revision == selected_revision == completed_revision`. No run had the revision move under it.
- A full build plus the 7576-test fast suite moved `store_log_sequence` by **zero**. `bin` and `obj` do not reach the index.

The 25068 → 25743 jump I blamed on one suite actually spans 14 hours between two separate runs.

**What is really happening.** Three facts combine, and none is an accident:

1. `ContinuousTestDurableFreshness.IsCommittedFreshAt` (`:15-24`) requires the identity to be equal **and** `status.Revision == selected.Revision`.
2. `WorkspaceReadSnapshot.IndexIdentity` in family-store mode joins the whole cursor, including `StoreLogSequence`. You can read the revision inside the live strings: `…:25068:full:…` and `…:25743:full:…`. So the identity itself changes on every index write.
   *Measured while writing this plan:* saving this one markdown file moved `store_log_sequence` from 25791 to 25797 — **six counts for one file save**. Every stored CT result was invalidated by writing a document.
3. Nothing ever carries a result forward. `AdvanceContinuousTestFreshWatermark` and `IsWatermarkFreshAt` have zero production callers — only `DurableFreshnessTests.cs` calls them.

So repairing the identity alone removes **zero** staleness, because the revision half must match too. Both halves must change together, or neither is worth changing.

**One safety point before anything is touched.** `TestsCore.SelectedFrom` (`:941-950`) picks the reported key from the stored rows themselves. The guard is self-referential. Remove the automatic staleness signal without replacing it and a uniform set of committed rows always reads Green however far the index has moved. That turns a fail-safe into a false green.

**The order, if you want CT verdicts to survive editing:**

1. Make the reported key come from the live index rather than from `ct.db`.
2. Then pick a freshness policy — this is the decision.
3. Then add `IndexGenerationIdentity` as a **new** property. `CtFactAdapter.cs:49` is the only place CT takes its identity, so the change is contained.
4. Then fix the CT side of the delta seam by passing the family id, without touching `RevisionDeltaReader.cs`.

**The three policies, honestly costed:**

| Policy | Cost | State today |
|---|---|---|
| Carry a green forward by watermark, using a keep-set from the impact selector | Needs a production caller and a keep-set | This is the intended design. The tables and methods exist. Nothing calls them. |
| Key a result on the content hash of the test's dependencies | Needs the dependency data to exist | The schema exists but is empty: `ct_coverage_maps`, `ct_coverage_map_files`, `test_links`, and `coverage_files` all hold 0 rows against 6108 cases. It also has a soundness hole — a test that depends on a file it never executed looks fresh when it is not. |
| Accept a result at the key the run started at | Cheapest | Accepts a green for code that changed during the run. |

I recommend the **watermark** policy. It is the design the schema was built for, the machinery is already written and tested, and it is the only one of the three with no soundness hole.

## Decision 2 — the extraction epoch pointer

A verifier simulated a pre-2.34.2 store and re-imported with 2.34.4. `store_meta.extraction_identity_epoch` **stayed at 1**. julie wrote a full parallel epoch-4 row set beside the epoch-1 rows and left the pointer alone.

Miller filters four capability views on that pointer (`src/Miller.Indexing/Reads/FamilyStoreReadSession.cs:479`, and four `WHERE extraction_epoch=(SELECT extraction_identity_epoch FROM _miller_session)` clauses at `:906-923`). Miller's own rebuild reuses the existing store, so it takes exactly this path. The consequence is that Miller keeps serving the epoch-1 capability snapshot forever. The visible wrong value is the fixture count, 211 old against 219 new. Nothing throws.

**Limit of this evidence:** the epoch-1 store was hand-edited, not created by a real 2.32.1 binary. The two facts Miller depends on are solid. Confirm on the first real re-extract.

Pick one owner:
- **(a)** julie-extractors advances the pointer when a newer-epoch binary imports into an existing store.
- **(b)** Miller scopes the four capability views by the highest `extraction_epoch` present, not by the pointer.
- **(c)** A fresh store family is minted when the extractor epoch changes.

## Live experiments still owed

The verification agents were forbidden to start the daemon or run a suite. These need a live run.

1. **Does Task 4 produce a complete inventory?** After Tasks 3 and 4, run `miller tests start`, then count `test_cases` where `source='ct-provider:dotnet'` and where `id LIKE '%(%'`. The count must rise well above 6105 and the theory-row count must be non-zero. Do not treat any single number as the target — measured discovery gave 7697 on the Debug assembly.
2. **Does a theory keep one result per data row?** Run one project with a known multi-row theory, then group `test_results` by `test_case_id` for that run. This is the false-green hazard from Task 3.
3. **Does `language_capability_gaps.status` stay inside `open|exception`?** `src/Miller.Indexing/WorkspaceHealthReader.cs:108-113` throws on any other value. Check after the first leader claim on the new pin.
4. **Does the extraction epoch pointer advance on the real store?** After the first re-extract, read `store_meta.extraction_identity_epoch` and group `language_capability_fixtures` by `extraction_epoch`.

## Sequencing note

The pin-bump changes are uncommitted on `main`. Commit them before starting Tasks 1–4, then run Tasks 1–4 in a worktree, so the pin bump stays separately revertible from the CT repairs. The Release build and the workspace re-extract on 2.34.4 both wait until the Miller MCP server is restarted.

## Follow-ups raised, not scheduled

- `CtDaemonLog` stamps UTC while Serilog stamps local time, so CT lines can land in the next day's file.
- Serilog rolls the daily log at 32 MiB; `CtDaemonLog` always writes the un-suffixed name, so the two streams split after a roll.
- `FailureSummary` keeps only the first line of an exception message, so `ct.db` loses the type and the stack.
- Two numbers from the earlier dogfood report have no source and should stop being quoted: "6105 stored against 7534 real" and "about 1429 missing theory rows". Use the measured figures in Task 4 instead.
