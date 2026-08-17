# Windows Dogfood Read Availability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Keep Miller search and named inspect serving on Windows while store resolution and sidecar converge run in the background, and fix the v1.19.4 Windows dogfood defects that made a fresh session unusable for several minutes.

**Architecture:** Split the work into two layers. Layer 1 is a read-availability contract: a converging or unbound family-store view must not throw from `search` or named `inspect`; those tools serve the last current sidecar and the last readable generation, while `workspace status`/`health` stay truthful. Layer 2 is a set of local Windows-dogfood repairs (idle 250 ms bind heartbeat disk I/O, live-file import share, edit no-op channel, eligibility version field, same-process scan-governor refuse, content-inspect lock, dead-code candidate wall time, registry fixture leak). Store-view resolution wall time stays a separate investigation; this plan records the Windows measurements and does not change the producer resolver.

**Tech Stack:** .NET 10, C#, xUnit, SQLite, Serilog, Miller MCP/CLI, pinned `julie-extract` 2.33.5.

**Architecture Quality:** Medium-risk read-path change at the family-store sidecar open seam, plus local behavior-local repairs. No new MCP tool. No Store Contract version bump. No producer pin bump unless a later investigation proves one is required.

## Global Constraints

- Do not add a new MCP tool, public CLI verb, Store Contract version, dependency, release, tag, push, or pin bump without explicit approval.
- `MILLER_SEMANTIC=off` remains a permanent zero-work guarantee. Lexical-only output stays byte-identical.
- `Miller.Core` stays pure logic with zero I/O dependencies.
- Search sidecar remains lexical-only. Vectors stay in `vectors.db`.
- A missing first sidecar on a brand-new family (no last-good stamp) may still refuse. A previously current sidecar for the same family/view must keep serving while the snapshot is `converging` or `unbound`.
- Status and health must not lie: if resolution is converging or a sidecar is behind the live sequence, report that. The lie this plan forbids is turning that fact into a hard `search`/`inspect` error.
- Named `inspect` of a file or symbol must not require a current search sidecar. Use the store generation / symbols reader. Existing test `Inspect_Summary_RegisteredWorkspace_UsesSymbolsWhenSearchSidecarCannotServe` is the oracle for that rule.
- Do not raise the julie-extract 4000 ms coordinator quantum as the Miller fix. Treat a quantum timeout as a retryable producer fault that keeps the prior view readable. A producer quantum change, if needed, is a julie-extractors follow-up.
- Resolution wall-time work is out of this plan. Do not change resolver SQL, crossover, or base rotation here. Record Windows numbers and point at `docs/plans/2026-08-13-miller-performance-recovery-plan.md`.
- Windows compatibility is required. File opens that must work against a live writer use `FileShare.ReadWrite` (and `Delete` when the existing writer already does).
- Fast-suite tests stay `Category!=Scale`. Any test that spawns `julie-extract` is `[Trait("Category","Scale")]` and uses `ScaleTestSupport.RequireJulieServer()`.
- Inner-loop verification is a focused `dotnet test --filter "FullyQualifiedName~<TestClassName>"`. Fast suite runs once per landed batch. Scale suite runs only when a task touches extract/index admission.
- Do not prune the user's live `~\.miller\workspaces.db` as part of this plan. Fix the leak so new Temp fixtures do not register there; leave an optional operator note for the existing 27 missing rows.
- Do not apply `edit` to production source in dogfood or verification. Previews only.

## Evidence

Source of truth: [`docs/findings/2026-08-17-windows-dogfood-1.19.4.md`](../findings/2026-08-17-windows-dogfood-1.19.4.md).

Issue list this plan covers:

1. Search/inspect throw while store resolution or sidecar converge is in flight.
2. Startup incremental scan fails when a coordinator quantum is 4359 ms against a 4000 ms cap, then marks `scan-failing` and defers the upgrade.
3. Leader debounce loop calls `EnsureBindingPointer` every 250 ms, reads the store pointer, and writes INF `indexer_phase_record bind` to both daily logs. Live 1.19.4 session: 5735 bind lines, 3.8/s, 1.4 MB `.log` + 3.2 MB `.jsonl`, ~6 KB growth every 5 s while CPU-idle. Not a tight spin loop; a permanent heartbeat that should not do file I/O or Information logging on a no-work tick.
4. `content import` cannot open a live Miller log on Windows.
5. Edit no-op preview is an MCP `internal_failure`.
6. Eligibility reason says artifact `2.33.2` while `artifact_extractor_version` is `2.33.5`.
7. Scan governor refuses the same pid's `leader-ondemand` while it holds `leader-drain-rescan`.
8. Content sidecar inspect reports `SQLite Error 5: 'database is locked'` during converge.
9. `miller references candidates --limit 5` is empty after 278 s.
10. Scale/CLI e2e Temp roots leak into the user registry (27 of 30 list rows).
11. `julie-extractors` workspace stuck on `locking protocol` / `scan_failing` since 2026-08-14 (diagnose; do not silently rebuild).
12. Resolve 97 s / coordinator 121 s / sidecar 56 s — investigation only.

## Architecture Quality

**Affected modules:** `SymbolSearchSidecar`, `FtsRegionSearchIndex`, `ContentCorpusSidecar` / `ContentCorpusExternalStore`, `WorkspaceIndexProvider`, `StoreWorkspaceCoordinator`, `IndexerService` scan admission, startup delta, and debounce-loop bind, `LoggingIndexerPhaseSink`, `LeadershipEligibility` / `StoreArtifactVersionReader` / `WorkspaceTool` leader facts, `EditTool` / `EditService` diagnostic channel, `DeadCodeCandidateReader`, workspace registry test isolation.

**Caller-facing interface:** Existing MCP/CLI tool names and JSON envelopes stay. New behavior is: stale-or-converging sidecar is a degraded serve of last-good, not `internal_failure`, plus truthful status. Edit no-op stays an empty preview, not an MCP error.

**Depth/locality check:** Last-good serve is decided at the sidecar open / index-provider seam. Status assembly already knows sidecar state. Do not push freshness policy into `SearchTool` query ranking or into `Miller.Core`.

**Test surface:** Fast tests on sidecar stamp matching, provider fallback, inspect-without-search-sidecar, quantum-timeout classification, debounce tick not calling bind, bind-log level, file-share import, edit empty outcome, eligibility field pairing, governor same-pid reentry, content inspect under a writer lock, dead-code candidate time bound, and registry isolation. Scale only for admission/extract paths.

**Seams/adapters:** Reuse `StoreSidecarStamp`, `WorkspaceReadSnapshot`, `IWorkspaceReadSession`, `ScanGovernor`, `ToolDiagnostic`, `EditResult.Outcome`. Add a last-good stamp lookup next to `StoreSidecarCatalog.IsCurrent`; do not invent a second sidecar catalog.

**Rejected shortcuts:** Raising the 4000 ms quantum in Miller; disabling the search sidecar; serving a sidecar from a different family/view; lying that the index is fresh; auto-pruning the user's registry; optimizing the producer resolver in this plan; adding a new MCP tool for "degraded search".

**Architecture risk:** medium.

## File structure

| Area | Files | Responsibility |
| --- | --- | --- |
| Last-good sidecar open | `src/Miller.Indexing/SymbolSearchSidecar.cs`, `src/Miller.Indexing/FtsRegionSearchIndex.cs`, `src/Miller.Indexing/StoreSidecarStamp.cs`, `src/Miller.Indexing/ContentCorpusSidecar.cs` | Open last current stamp for the same family/view when the live snapshot is not exact. |
| Read provider | `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs` | Search uses last-good open. Named inspect uses the generation/symbols reader when search cannot serve. |
| Quantum / startup | `src/Miller.Server/Workspaces/StoreWorkspaceCoordinator.cs`, `src/Miller.Server/Hosting/IndexerService.cs` | Classify quantum timeout as retryable. Do not make IncrementalReconcile failure the reason search throws. |
| Idle bind heartbeat | `src/Miller.Server/Hosting/IndexerService.cs` (debounce loop), `src/Miller.Server/Hosting/IndexerPhaseRecord.cs` | Do not call `EnsureBindingPointer` every 250 ms. Successful no-work `bind` is Debug, not Information. |
| Live import | `src/Miller.Indexing/ContentCorpusExternalStore.cs` | `FileShare.ReadWrite` on import reads. |
| Edit channel | `src/Miller.Server/Tools/EditTool.cs`, `src/Miller.Server/Tools/EditService.cs` | No-op preview is empty, not MCP error. |
| Eligibility | `src/Miller.Indexing/Store/StoreArtifactVersionReader.cs`, `src/Miller.Server/Tools/WorkspaceTool.cs`, `src/Miller.Server/Hosting/IndexerLeadershipCoordinator.cs` | One version field for display and `Evaluate`. |
| Governor | `src/Miller.Server/Hosting/IndexerService.cs`, `src/Miller.Indexing/ScanGovernor.cs` | Same pid already holding the workspace lease does not 5 s-refuse its own on-demand. |
| Content inspect lock | `src/Miller.Indexing/ContentCorpusSidecar.cs` | Status inspect retries or reports `converging`, never a raw locked error that looks like a dead corpus. |
| Dead-code CLI | `src/Miller.Indexing/DeadCodeCandidateReader.cs`, `src/Miller.Server/Cli/CliDispatch.cs` | Bound or progress the literal scan; empty after minutes is a bug. |
| Registry isolation | Scale/CLI e2e test helpers that call `workspace open` | Isolated `MILLER_HOME` / registry so Temp roots never land in the user db. |

## Verification Strategy

**Project source of truth:** `CLAUDE.md` / `AGENTS.md` testing and build sections; `scripts/test.ps1` / `scripts/test.sh`; `tests/Miller.Tests/Miller.Tests.csproj` (`VSTestTestCaseFilter=Category!=Scale`).

**Worker red/green scope:** `dotnet test --filter "FullyQualifiedName~<TestClassName>"` for the class named in the task.

**Worker ceiling:** The focused test class or the small group of classes that task lists. Workers do not run Scale, `scripts/test.ps1`, or Release rebuilds unless the task says so.

**Worker gate invariant:** The named test class fails before the behavior change and passes after, and it proves the acceptance criteria for that task.

**Lead affected-change scope:** After a parallel batch, run the union of that batch's test classes once. After the serial read-availability lane (Tasks 1, 2, 7, 3), run those class sets together.

**Branch gate:** `scripts/test.ps1` (fast suite). Add `scripts/test.ps1 scale` only if Tasks 2, 7, or 10 changed extract/admission/registry Scale tests.

**Security scope:** none declared.

**Replay/metric evidence:** Windows dogfood timings in the findings file are report-only. Hard gates are the unit/contract assertions in each task. Do not treat the 666 ms semantic or 278 s candidates numbers as CI budgets.

**Escalation triggers:** A change that reintroduces `OpenStoreRequired` throws for a converging snapshot; a test that starts spawning `julie-extract` without `Category=Scale`; a pin bump; any write to a live family store during development.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp. If the same HEAD already has a passing ledger entry for the required scope, reuse it.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Last-good search/inspect during resolve | Serial lane R | `src/Miller.Indexing/SymbolSearchSidecar.cs`; `src/Miller.Indexing/FtsRegionSearchIndex.cs`; `src/Miller.Indexing/StoreSidecarStamp.cs`; `src/Miller.Indexing/ContentCorpusSidecar.cs` (open/required only); `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`; `tests/Miller.Tests/Indexing/SymbolSearchSidecarTests.cs`; `tests/Miller.Tests/Indexing/StoreSidecarStampTests.cs`; `tests/Miller.Tests/Server/InspectToolTests.cs` | Yes | Product contract all later read fixes assume. |
| Task 2: Quantum timeout stays readable | Serial lane R | `src/Miller.Server/Workspaces/StoreWorkspaceCoordinator.cs`; `src/Miller.Server/Hosting/IndexerService.cs` (startup delta / scan-failure classification only); `tests/Miller.Tests/Server/StoreWorkspaceCoordinatorTests.cs`; `tests/Miller.Tests/Server/IndexerServiceScanTests.cs` | Yes | After Task 1 so a failed incremental scan cannot re-break last-good serve. Shares `IndexerService.cs` with Task 7. |
| Task 3: Stop idle bind heartbeat I/O | Serial lane R | `src/Miller.Server/Hosting/IndexerService.cs` (debounce loop only, `DebounceInterval` / `EnsureBindingPointer` at lines 501 and 530-532); `src/Miller.Server/Hosting/IndexerPhaseRecord.cs`; `tests/Miller.Tests/Server/IndexerPhaseRecordTests.cs`; `tests/Miller.Tests/Server/IndexerServiceScanTests.cs` only for a drain-tick bind-count assertion if one fits without rewriting Task 2 tests | Yes | After Task 7 because both edit `IndexerService.cs`. |
| Task 4: Live-file content import | Batch A | `src/Miller.Indexing/ContentCorpusExternalStore.cs`; `tests/Miller.Tests/Indexing/ContentCorpusExternalStoreTests.cs` (or the existing content-import test class that covers `OpenRead`) | No | None - safe parallel batch. |
| Task 5: Edit no-op is empty | Batch A | `src/Miller.Server/Tools/EditTool.cs`; `src/Miller.Server/Tools/EditService.cs`; `tests/Miller.Tests/Server/EditToolTests.cs`; `tests/Miller.Tests/Server/ToolDiagnosticTests.cs` if the filter classifies the result | No | None - safe parallel batch. |
| Task 6: Eligibility version field | Batch A | `src/Miller.Indexing/Store/StoreArtifactVersionReader.cs`; `src/Miller.Server/Tools/WorkspaceTool.cs`; `src/Miller.Server/Hosting/IndexerLeadershipCoordinator.cs`; `tests/Miller.Tests/Indexing/LeadershipEligibilityTests.cs`; `tests/Miller.Tests/Server/WorkspaceRenderTests.cs` | No | None - safe parallel batch. Do not edit `IndexerService.cs`. |
| Task 7: Same-pid scan admission | Serial lane R | `src/Miller.Server/Hosting/IndexerService.cs` (TryAcquireScanAdmission / same-pid reuse); `src/Miller.Indexing/ScanGovernor.cs` only if a same-pid query is missing; `tests/Miller.Tests/Server/ScanGovernor*` existing class that covers admission | Yes | After Task 2 because both edit `IndexerService.cs`. |
| Task 8: Content inspect under writer lock | Batch A | `src/Miller.Indexing/ContentCorpusSidecar.cs` (Inspect/InspectStore only); `tests/Miller.Tests/Indexing/ContentCorpusSidecarTests.cs` | No | None - safe parallel batch. Task 1 must not take Inspect methods; Task 8 must not take Open/Required methods. |
| Task 9: Dead-code candidates bound | Batch A | `src/Miller.Indexing/DeadCodeCandidateReader.cs`; `src/Miller.Server/Cli/CliDispatch.cs` (`ReferencesCandidates` only); `tests/Miller.Tests/Indexing/DeadCodeCandidateReaderTests.cs` | No | None - safe parallel batch. |
| Task 10: Registry fixture isolation | Batch A | The existing Scale/CLI e2e helper that registers Temp roots (find via Miller `search query="miller-scan-governor" mode=source` in `tests/`); do not edit production prune | No | None - safe parallel batch. |
| Task 11: julie-extractors stuck workspace | Serial lane D | `docs/findings/2026-08-17-julie-extractors-windows-lock.md` (create); Miller code only if the `locking protocol` string is Miller-owned and the fix is local | Yes | Needs a live status read of `C:\source\julie-extractors` after Tasks 1–7 so diagnosis is not mixed with a still-broken miller session. |
| Task 12: Resolution slowness charter | Serial lane D | `docs/findings/2026-08-17-windows-resolution-investigation.md` (create); pointer in `docs/README.md` | Yes | Docs-only. After Task 11 so the two findings do not collide on `docs/README.md`. |

Completion follows `parallel-lead-commit` for Batch A and `serial-worker-commit` for Serial lanes R and D.

---

### Task 1: Last-good search/inspect during resolve

**Files:**
- Modify: `src/Miller.Indexing/StoreSidecarStamp.cs` (add last-good lookup next to `IsCurrent` at line 251)
- Modify: `src/Miller.Indexing/SymbolSearchSidecar.cs:285-310` (`OpenStoreRequired`)
- Modify: `src/Miller.Indexing/FtsRegionSearchIndex.cs:55` (same throw)
- Modify: `src/Miller.Indexing/ContentCorpusSidecar.cs` (store open/required path only, not Inspect)
- Modify: `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs:406-427` (`ResolveCurrentSymbolSearchIndex`) and the inspect/symbol-lookup path that currently calls `OpenStoreRequired`
- Test: `tests/Miller.Tests/Indexing/StoreSidecarStampTests.cs`
- Test: `tests/Miller.Tests/Indexing/SymbolSearchSidecarTests.cs`
- Test: `tests/Miller.Tests/Server/InspectToolTests.cs` (`Inspect_Summary_RegisteredWorkspace_UsesSymbolsWhenSearchSidecarCannotServe` must keep passing)

**Interfaces:**
- Consumes: `StoreSidecarStamp.FromSnapshot`, `StoreSidecarCatalog.PathFor`, `WorkspaceReadSnapshot` (`ResolutionState`, `ViewId`, `StoreLogSequence`)
- Produces: `OpenStoreRequired` / region open return the last current sidecar for the same family/view when the live snapshot is `converging` or `unbound`; throw only when no last-good sidecar exists

**Contract inputs:** A last-good sidecar is one whose stamp family id and view id match, and whose stamp was current for an earlier exact snapshot. Different family or view is never last-good.

**File ownership:** `src/Miller.Indexing/SymbolSearchSidecar.cs`; `src/Miller.Indexing/FtsRegionSearchIndex.cs`; `src/Miller.Indexing/StoreSidecarStamp.cs`; `src/Miller.Indexing/ContentCorpusSidecar.cs` (open/required only); `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`; `tests/Miller.Tests/Indexing/SymbolSearchSidecarTests.cs`; `tests/Miller.Tests/Indexing/StoreSidecarStampTests.cs`; `tests/Miller.Tests/Server/InspectToolTests.cs`

**Serialization required:** Yes

**Dependency reason:** Product contract all later read fixes assume.

**What to build:** Stop `search` and named `inspect` from throwing `Search sidecar for view '…' is missing or stale` while the store is resolving. Serve the last current sidecar. Keep status/health reporting `sidecar_stale` / `resolving`.

**Approach:** Add a stamp helper that finds the newest sidecar file for that view whose stamp matches an earlier sequence, then open it read-only. `WorkspaceIndexProvider` must route named inspect through the generation/symbols reader when the live search sidecar is not current, matching `Inspect_Summary_RegisteredWorkspace_UsesSymbolsWhenSearchSidecarCannotServe`. Do not fall back to an in-memory BM25 index (that path is the explicit `MILLER_SEARCH_SIDECAR=0` opt-out only).

**Acceptance criteria:**
- [x] A fixture with an exact sidecar at sequence N and a live snapshot at sequence N+k with `resolution=converging` serves search hits from the N sidecar
- [x] The same fixture's named inspect of a known symbol succeeds without opening a current search sidecar
- [x] A family/view with no sidecar at all still throws the current missing-sidecar message
- [x] `workspace status` still reports the live sidecar as stale/unready
- [x] Worker-scope verification passes and the change is committed by the worker (`serial-worker-commit`)

---

### Task 2: Quantum timeout stays readable

**Files:**
- Modify: `src/Miller.Server/Workspaces/StoreWorkspaceCoordinator.cs:815-838` (`RequireCommitted` / exception wrap of `result.Failure.Message`)
- Modify: `src/Miller.Server/Hosting/IndexerService.cs` (`RunStartupDeltaScan` near the 12:07:14 log site; scan-failure classification)
- Test: `tests/Miller.Tests/Server/StoreWorkspaceCoordinatorTests.cs`
- Test: `tests/Miller.Tests/Server/IndexerServiceScanTests.cs`

**Interfaces:**
- Consumes: `StoreWorkspaceOperationException`, `ScanFailureJournal`, `ScanIntent.IncrementalReconcile`
- Produces: A quantum-timeout / `request_not_terminal` incremental failure is retryable, keeps the prior view, and does not make Task 1's last-good serve throw

**Contract inputs:** Observed producer message: `coordinator quantum took 4359 ms; maximum is 4000 ms`. Do not raise that maximum from Miller.

**File ownership:** `src/Miller.Server/Workspaces/StoreWorkspaceCoordinator.cs`; `src/Miller.Server/Hosting/IndexerService.cs` (startup delta / scan-failure classification only); `tests/Miller.Tests/Server/StoreWorkspaceCoordinatorTests.cs`; `tests/Miller.Tests/Server/IndexerServiceScanTests.cs`

**Serialization required:** Yes

**Dependency reason:** After Task 1 so a failed incremental scan cannot re-break last-good serve. Shares `IndexerService.cs` with Task 7.

**What to build:** A coordinator quantum miss must not turn a healthy prior index into a session that cannot search. Keep the prior view. Retry later. Do not defer an already-owed extractor upgrade behind a quantum miss in a way that leaves tools broken.

**Approach:** Classify the producer failure class/message for quantum timeout as retryable in the startup-delta path. `RecordFailure` may still record IncrementalReconcile, but Task 1 last-good serve must remain the user-visible path. Do not change julie-extract. Add a findings sentence in the Task 2 commit message that a producer quantum follow-up belongs in julie-extractors if Windows incrementals keep losing by a few hundred milliseconds.

**Acceptance criteria:**
- [ ] A coordinator result whose message matches the quantum-timeout shape does not throw out of startup in a way that disables last-good search
- [ ] The prior store generation remains the served generation
- [ ] A genuine `StoreRequestState.Failed` that is not a quantum timeout is still a hard failure
- [ ] Worker-scope verification passes and the change is committed by the worker (`serial-worker-commit`)

---

### Task 3: Stop idle bind heartbeat I/O

**Files:**
- Modify: `src/Miller.Server/Hosting/IndexerService.cs:501` (`DebounceInterval` delay) and `:530-532` (per-tick `EnsureBindingPointer`)
- Modify: `src/Miller.Server/Hosting/IndexerPhaseRecord.cs:126-141` (`LoggingIndexerPhaseSink.Record`)
- Create: `tests/Miller.Tests/Server/IndexerPhaseRecordTests.cs`
- Test: `tests/Miller.Tests/Server/IndexerServiceScanTests.cs` only if a drain-tick assertion can land without rewriting Task 2 cases

**Interfaces:**
- Consumes: `IndexerService.RunLeadershipSessionAsync` debounce loop, `StoreWorkspaceCoordinator.EnsureBindingPointer`, `IndexerPhaseRecord` (`Phase`, `DidWork`, `Outcome`)
- Produces: A no-event drain tick does not read the store pointer and does not write Information bind lines. Session start and rebind still call `EnsureBindingPointer` once.

**Contract inputs:** Live 1.19.4 Windows session, pid 14212, 2026-08-17: 5735 INF bind lines at 3.8/s; `didWork=false`; idle CPU ~0 over 5 s; log+jsonl grew ~6 KB in 5 s. Call sites: session start at `IndexerService.cs:433` (keep), per-tick at `:530-532` (remove). Do not change `DebounceInterval` itself. Do not demote `import` / `resolve` / `sidecar_total`.

**File ownership:** `src/Miller.Server/Hosting/IndexerService.cs` (debounce loop only, `DebounceInterval` / `EnsureBindingPointer` at lines 501 and 530-532); `src/Miller.Server/Hosting/IndexerPhaseRecord.cs`; `tests/Miller.Tests/Server/IndexerPhaseRecordTests.cs`; `tests/Miller.Tests/Server/IndexerServiceScanTests.cs` only for a drain-tick bind-count assertion if one fits without rewriting Task 2 tests

**Serialization required:** Yes

**Dependency reason:** After Task 7 because both edit `IndexerService.cs`.

**What to build:** Stop the idle leader from looking like a runaway process. The 250 ms watcher debounce stays. The per-tick store-pointer read and the Information bind flood go away.

**Approach:** Remove `EnsureBindingPointer` from the debounce loop. Keep the call at leadership-session start (`:433`). Call it again only when the binding actually changes: new `bindingGeneration`, store rebind, or after a drain tick that ran a scan/`didWork` extract. Do not re-read the pointer on a quiet tick. Defense in depth: in `LoggingIndexerPhaseSink.Record`, if `Phase == Bind` and `Outcome` is completed and `DidWork` is false, `LogDebug`. Failed binds and did-work binds stay Information.

**Acceptance criteria:**
- [ ] A quiet drain tick (no watcher events, no scan) does not call `EnsureBindingPointer` and does not write an Information bind line
- [ ] Leadership claim / session start still writes or verifies the pointer once
- [ ] A completed no-work bind record, if any remain, is Debug
- [ ] A failed bind record is still Information
- [ ] A completed import/resolve record is still Information
- [ ] Worker-scope verification passes and the change is committed by the worker (`serial-worker-commit`)

---

### Task 4: Live-file content import

**Files:**
- Modify: `src/Miller.Indexing/ContentCorpusExternalStore.cs:282-290` (`OpenRead`)
- Test: existing content-import test class that covers `OpenRead` (Miller `inspect target=ContentCorpusExternalStore` and follow the test locations)

**Interfaces:**
- Consumes: `FileStream` open used by `content import`
- Produces: Import of a file another process has open for append/write succeeds on Windows

**Contract inputs:** Writer share used by Miller logs is Serilog `shared:true`. Match `ContentCorpusWriter` header probe, which already uses `FileShare.ReadWrite | FileShare.Delete`.

**File ownership:** `src/Miller.Indexing/ContentCorpusExternalStore.cs`; `tests/Miller.Tests/Indexing/ContentCorpusExternalStoreTests.cs` (or the existing content-import test class that covers `OpenRead`)

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** `content import` of `.miller/logs/miller-*.log` must succeed while the server is writing that file.

**Approach:** Change `OpenRead` from `FileShare.Read` to `FileShare.ReadWrite` (add `Delete` if tests show the writer also deletes/renames). Keep sequential scan and the 16 KiB buffer. Add a test that opens a file with `FileShare.ReadWrite`, writes to it, and imports through the production open path.

**Acceptance criteria:**
- [x] Opening a file that another handle holds with write share succeeds
- [x] Missing-file and directory errors stay the same
- [x] Worker-scope verification passes and the change is handed to the lead (`parallel-lead-commit`)

---

### Task 5: Edit no-op is empty

**Files:**
- Modify: `src/Miller.Server/Tools/EditTool.cs:64-183`
- Modify: `src/Miller.Server/Tools/EditService.cs:1498-1574` only if `Preview` needs an explicit `ToolDiagnostic` with `expected_empty`
- Test: `tests/Miller.Tests/Server/EditToolTests.cs`
- Test: `tests/Miller.Tests/Server/ToolDiagnosticTests.cs` if the filter/diagnostic mapping changes

**Interfaces:**
- Consumes: `EditResult.Outcome` (`ok` | `empty` | `error`), `ToolDiagnostic`
- Produces: Same-text preview is MCP non-error empty (`no_change` / `expected_empty`), not `internal_failure` / `unknown`

**Contract inputs:** Observed compact text `No change — the edit is a no-op.` must remain. `Outcome` is already `"empty"` in `Preview`. The bug is the MCP error channel / diagnostic class.

**File ownership:** `src/Miller.Server/Tools/EditTool.cs`; `src/Miller.Server/Tools/EditService.cs`; `tests/Miller.Tests/Server/EditToolTests.cs`; `tests/Miller.Tests/Server/ToolDiagnosticTests.cs` if the filter classifies the result

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** A same-text `replace_text` preview is an empty success, not `Failed to call edit`.

**Approach:** Trace `EditTool.Edit` into `TelemetryCallToolFilter` / `ToolDiagnosticContext`. If the filter sets `IsError` because no `ToolDiagnostic` is attached, attach `expected_empty` / `no_change`. Do not change the compact sentence. Keep apply=false.

**Acceptance criteria:**
- [x] Same-text preview `Outcome` is `empty` and MCP `IsError` is false
- [x] A real preview remains `ok` and still writes nothing
- [x] A genuine edit failure remains `error`
- [x] Worker-scope verification passes and the change is handed to the lead (`parallel-lead-commit`)

---

### Task 6: Eligibility version field

**Files:**
- Modify: `src/Miller.Indexing/Store/StoreArtifactVersionReader.cs`
- Modify: `src/Miller.Server/Tools/WorkspaceTool.cs:501-505` (`ReadLeaderFacts`)
- Modify: `src/Miller.Server/Hosting/IndexerLeadershipCoordinator.cs` (the `Evaluate` call that supplies `artifactBinaryVersion`)
- Test: `tests/Miller.Tests/Indexing/LeadershipEligibilityTests.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceRenderTests.cs`

**Interfaces:**
- Consumes: `LeadershipEligibility.Evaluate(ownExtractorVersion, artifactBinaryVersion, allowDowngrade)`
- Produces: `artifact_extractor_version` and `own_eligibility.reason` name the same artifact version string

**Contract inputs:** Observed split: display `2.33.5` vs reason `2.33.2`. `Evaluate` already renders whatever string it is given. The bug is passing two different fields.

**File ownership:** `src/Miller.Indexing/Store/StoreArtifactVersionReader.cs`; `src/Miller.Server/Tools/WorkspaceTool.cs`; `src/Miller.Server/Hosting/IndexerLeadershipCoordinator.cs`; `tests/Miller.Tests/Indexing/LeadershipEligibilityTests.cs`; `tests/Miller.Tests/Server/WorkspaceRenderTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch. Do not edit `IndexerService.cs`.

**What to build:** Status must not say the extractor is newer than `2.33.2` when `artifact_extractor_version` is `2.33.5`. Equal versions must use the `matches the index artifact` reason.

**Approach:** Make `ReadLeaderFacts` and the leadership coordinator read the same store/legacy function (`TryReadOrFallback` vs `ReadForLeadership`). Add a render test that equal versions produce the matches reason and that the JSON field equals the reason's artifact token.

**Acceptance criteria:**
- [x] When own and artifact extractor versions are `2.33.5`, reason contains `matches` and does not contain `newer`
- [x] `artifact_extractor_version` JSON equals the version string `Evaluate` rendered
- [x] A truly older artifact still reports `newer` and still schedules upgrade
- [x] Worker-scope verification passes and the change is handed to the lead (`parallel-lead-commit`)

---

### Task 7: Same-pid scan admission

**Files:**
- Modify: `src/Miller.Server/Hosting/IndexerService.cs` (`TryAcquireScanAdmission` near line 1149)
- Modify: `src/Miller.Indexing/ScanGovernor.cs` only if a same-pid holder query is missing
- Test: existing ScanGovernor / IndexerService admission tests (Miller `impact target=TryAcquireScanAdmission`)

**Interfaces:**
- Consumes: `ScanGovernor.DescribeHolder()`, recorded owner pid, `leader-ondemand` / `leader-drain-rescan` reasons
- Produces: If this process already holds the lease for this workspace root, on-demand does not wait 5 s and refuse

**Contract inputs:** Observed warning: refused `leader-ondemand` after 5 s while holder is the same pid `14212` with `leader-drain-rescan`.

**File ownership:** `src/Miller.Server/Hosting/IndexerService.cs` (TryAcquireScanAdmission / same-pid reuse); `src/Miller.Indexing/ScanGovernor.cs` only if a same-pid query is missing; `tests/Miller.Tests/Server/ScanGovernor*` existing class that covers admission

**Serialization required:** Yes

**Dependency reason:** After Task 2 because both edit `IndexerService.cs`. Task 3 edits the same file after this task.

**What to build:** A leader that already holds machine-wide admission for this root must not refuse its own refresh. Queue the on-demand work on the existing drain, or treat same-pid holder as already admitted.

**Approach:** Before the 5 s wait, if `DescribeHolder()` / owner record is this pid and this workspace root, return the existing admission or a queued outcome without a refusal warning. Do not allow a second OS lease (the governor comments forbid same-thread double-wrap). Prefer "already holding, proceed / queue" over "wait then refuse".

**Acceptance criteria:**
- [ ] Same-pid holder + `leader-ondemand` does not log the 5 s refuse warning
- [ ] A different pid still waits and can be refused
- [ ] The OS lease remains single-holder
- [ ] Worker-scope verification passes and the change is committed by the worker (`serial-worker-commit`)

---

### Task 8: Content inspect under writer lock

**Files:**
- Modify: `src/Miller.Indexing/ContentCorpusSidecar.cs` (Inspect / InspectStore only)
- Test: `tests/Miller.Tests/Indexing/ContentCorpusSidecarTests.cs`

**Interfaces:**
- Consumes: Content sidecar inspect used by `workspace status`/`health`
- Produces: During a writer lock, inspect returns `converging`/`unreadable` with a retryable reason, not a raw `SQLite Error 5` as the only health line, and does not throw into the tool

**Contract inputs:** Observed status: `content_corpus.state=unreadable`, `error=SQLite Error 5: 'database is locked'.`

**File ownership:** `src/Miller.Indexing/ContentCorpusSidecar.cs` (Inspect/InspectStore only); `tests/Miller.Tests/Indexing/ContentCorpusSidecarTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch. Task 1 must not take Inspect methods; Task 8 must not take Open/Required methods.

**What to build:** Status must survive a content-sidecar writer. Prefer a short busy retry, then a `converging` state. Do not fail the whole `workspace` tool.

**Approach:** Set a small busy timeout on the inspect connection, retry once, then map locked to the existing unreadable/converging envelope. Keep `error` non-null and stable (`database_locked`), not the raw SQLite sentence if a code already exists.

**Acceptance criteria:**
- [ ] Inspect against a locked content db does not throw
- [ ] Status still returns and names the corpus as not current
- [ ] A truly missing/corrupt corpus still reports missing/corrupt
- [ ] Worker-scope verification passes and the change is handed to the lead (`parallel-lead-commit`)

---

### Task 9: Dead-code candidates bound

**Files:**
- Modify: `src/Miller.Indexing/DeadCodeCandidateReader.cs:357` (`RunLiteralScan`) and `Read` at lines 50-88
- Modify: `src/Miller.Server/Cli/CliDispatch.cs` `ReferencesCandidates` only (progress / empty rendering)
- Test: `tests/Miller.Tests/Indexing/DeadCodeCandidateReaderTests.cs`

**Interfaces:**
- Consumes: `DeadCodeCandidateReport`, `--limit N` (already documented as bounding the listing, not the scan)
- Produces: CLI returns a non-blank report in bounded time; if the literal scan is the cost, it is incremental or skipped with an explicit reason

**Contract inputs:** Observed: 278043 ms, blank stdout, then the next CLI verb ran. `--limit 5` must not imply an unbounded workspace-wide literal scan with no output.

**File ownership:** `src/Miller.Indexing/DeadCodeCandidateReader.cs`; `src/Miller.Server/Cli/CliDispatch.cs` (`ReferencesCandidates` only); `tests/Miller.Tests/Indexing/DeadCodeCandidateReaderTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** `miller references candidates --limit 5` must print a report (including "0 candidates" or a coverage/unavailable reason) without a multi-minute silent scan. If full-level resolution tables are missing or still converging, say that and exit 0/3 per the existing incompatible-extract contract — do not scan the whole tree first.

**Approach:** Short-circuit when required resolution tables are empty or `ReferenceResolutionStatus` is not ready. If a literal scan remains necessary, bound files scanned by the same limit or stream the first page before finishing the scan. Never return a blank body after success. Keep Core (`DeadCodeCandidates`) pure.

**Acceptance criteria:**
- [x] A symbols-level / unresolved fixture returns a named unavailable/empty report without a full-tree literal scan
- [x] `--limit 5` never produces zero bytes on success
- [x] The existing suppression/evidence rules stay in `Miller.Core.DeadCode`
- [x] Worker-scope verification passes and the change is handed to the lead (`parallel-lead-commit`)

---

### Task 10: Registry fixture isolation

**Files:**
- Modify: the Scale/CLI e2e helper that registers Temp `miller-scan-governor-*` / `miller-cli-e2e-*` roots (locate with Miller `search query="miller-scan-governor" mode=source` under `tests/`)
- Test: the same test class after isolation

**Interfaces:**
- Consumes: `WorkspaceRegistry.Open`, `MILLER_HOME` / registry path helpers
- Produces: Temp fixtures use a private registry under the test temp directory

**Contract inputs:** Observed 27 missing Temp roots in the user `~\.miller\workspaces.db`. Do not prune that database in this task.

**File ownership:** The existing Scale/CLI e2e helper that registers Temp roots; do not edit production prune

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** New Scale/CLI e2e runs must not insert rows into the user's real workspace registry.

**Approach:** Point those tests at an isolated `MILLER_HOME` or explicit registry path already used by other tests. Add a guard test that the helper's registry path is under `Path.GetTempPath()` (or the test directory), not `~\.miller`.

**Acceptance criteria:**
- [x] The helper's registry path is under the test temp directory
- [x] Production `workspace prune` is unchanged
- [x] Worker-scope verification passes and the change is handed to the lead (`parallel-lead-commit`)

---

### Task 11: julie-extractors stuck workspace

**Files:**
- Create: `docs/findings/2026-08-17-julie-extractors-windows-lock.md`
- Modify: Miller source only if the `locking protocol` string is Miller-owned and the fix is local and file-owned here
- Modify: `docs/README.md` current-docs pointer (after Task 12 if both land in one commit batch; otherwise Task 12 owns the README edit)

**Interfaces:**
- Consumes: `workspace status` / `health` on `julie-extractors` with `ensure_fresh=false`, `C:\source\julie-extractors\.miller\scan-failure.json`
- Produces: A findings note with the lock owner, whether Miller or julie-extract owns the string, and the repair command. No silent `workspace full`.

**Contract inputs:** Observed list error `locking protocol`; scan-failure `IncrementalReconcile` at `2026-08-14T00:04:05Z` with `next_attempt_at` already past.

**File ownership:** `docs/findings/2026-08-17-julie-extractors-windows-lock.md` (create); Miller code only if the `locking protocol` string is Miller-owned and the fix is local

**Serialization required:** Yes

**Dependency reason:** Needs a live status read of `C:\source\julie-extractors` after Tasks 1–7 so diagnosis is not mixed with a still-broken miller session.

**What to build:** Diagnose the three-day stuck workspace. Document the owner and the repair. Change Miller only if we emit `locking protocol` and the message is wrong or unactionable.

**Approach:** Read status/health/list with `ensure_fresh=false`. Open the scan-failure journal and miller log for that workspace. Search Miller and, if needed, the julie-extractors repo for `locking protocol`. Do not run `workspace full` or delete store files without an explicit user approval line in the findings.

**Acceptance criteria:**
- [ ] Findings file names the process/file that holds the lock or proves it is stale
- [ ] Findings file states whether the next step is a Miller code fix, a julie-extractors repair, or an operator prune/refresh
- [ ] No store or registry mutation happens in this task
- [ ] Worker-scope verification is the findings file plus any local Miller string fix tests; change is committed by the worker (`serial-worker-commit`)

---

### Task 12: Resolution slowness charter

**Files:**
- Create: `docs/findings/2026-08-17-windows-resolution-investigation.md`
- Modify: `docs/README.md` (add the dogfood findings, this charter, and the read-availability plan to Current docs)

**Interfaces:**
- Consumes: Phase timings from `docs/findings/2026-08-17-windows-dogfood-1.19.4.md` and the budgets in `docs/plans/2026-08-13-miller-performance-recovery-plan.md`
- Produces: A follow-up investigation charter, not a resolver implementation

**Contract inputs:** Windows phases: import 23 s, resolve 97 s, coordinator 121 s, sidecar 56 s. August 13 budgets: full resolution 120 s on constrained Windows, one-file 10 s, warm inspect 2 s, warm context/impact/trace 5 s. Warm impact 7234 ms and context 9501 ms on this box missed those warm budgets after settle.

**File ownership:** `docs/findings/2026-08-17-windows-resolution-investigation.md` (create); pointer in `docs/README.md`

**Serialization required:** Yes

**Dependency reason:** Docs-only. After Task 11 so the two findings do not collide on `docs/README.md`.

**What to build:** A short charter that says resolution slowness is still owned by the August 13 recovery plan, lists the new Windows numbers, and forbids implementing resolver changes under this read-availability plan.

**Approach:** Compare each dogfood phase to the published budget. Mark resolve 97 s as inside the 120 s Windows full-resolution budget but still too long to sit on the user-visible tool path (Task 1 is the relief). Mark warm impact/context as over budget and point them back at the August 13 query work, not this plan.

**Acceptance criteria:**
- [ ] Charter file exists and states "no resolver SQL or pin bump in the read-availability plan"
- [ ] `docs/README.md` Current docs lists the dogfood finding, this charter, and the plan
- [ ] Worker-scope verification is the doc set; change is committed by the worker (`serial-worker-commit`)

---

## Execution notes

TDD applies to Tasks 1–10: write the failing test, see it fail, implement, see it pass. Tasks 11–12 are evidence/docs.

After approval, create an isolated worktree from `main` (no outstanding worktrees at plan time). Batch A may dispatch in parallel. Serial lane R runs Task 1, then 2, then 7, then 3. Serial lane D runs after R, Task 11 then 12.

Windows dogfood after the branch gate: start a fresh MCP session on this repo, call `search` during the first minute, and confirm it returns hits while status may still say resolving.
