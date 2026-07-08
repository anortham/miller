# Dashboard & Registry Hygiene Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Fix the dashboard 500 on schema-mismatched workspace artifacts, make dashboard failures observable, stop the test suite from polluting the real workspace registry, and give the registry/dashboard a prune + hygiene story for dead workspace entries.

**Architecture:** Four defect/hygiene slices against existing components — no new projects, no new MCP tools. The dashboard panel readers get the missing exception handling; the dashboard host gets minimal logging; `IndexBootstrapService` gets an injectable registry home so tests isolate; the existing `workspace` tool/CLI verb gains a `prune` operation backed by `WorkspaceRegistry`, and the all-workspaces view learns to separate live entries from dead ones. A final ops task cleans the real machine state.

**Tech Stack:** .NET 10, ASP.NET Razor Components (SSR), Microsoft.Data.Sqlite, xUnit.

**Architecture Quality:** No new module boundaries. One deliberate seam change: `IndexBootstrapService` gains an internal home-directory override (same pattern as its existing `TestRunBootstrapOverride`/`TestBootstrapInterceptor` hooks) so every registry write it makes is routed through one testable path. Risk: low — all changes follow existing patterns (`ScaleTraitConventionTests`-style guard, `workspace remove`-style operation plumbing).

## Global Constraints

- `dotnet build Miller.slnx -c Release` must stay 0 warnings / 0 errors (`TreatWarningsAsErrors`).
- Fast suite stays pure: no new test may spawn `julie-extract` or write outside per-test temp dirs. Any test needing julie is `[Trait("Category","Scale")]` + `ScaleTestSupport.RequireJulieServer()` (none is expected in this plan).
- **No test may read or write `~/.miller/workspaces.db`, `~/.miller/telemetry.db`, or any path under the real user home.** This is the invariant Task 3 exists to enforce.
- No new MCP tool. `prune` is a new *operation* on the existing `workspace` tool (allowed: "prefer improving an existing tool"). The `workspace` tool `[Description]` must stay ≤900 chars (gated by `AgentInstructionsTests`).
- Test fixtures seeding `symbols.db` must be contract-faithful: a "schema 3 artifact" fixture carries the real 2.8.x metadata keys (`schema_version=3`, `extract_contract_version=3`, `binary_version=2.8.1`, `hash_algorithm=blake3`), not invented values.
- Dashboard stays local-first: no new external network dependency.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` (Testing section) + `scripts/test.sh`.

**Worker red/green scope:** `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~<TestClassName>"` for the specific test class(es) each task touches.

**Worker ceiling:** `scripts/test.sh` (fast suite, <30s budget). Workers do not run the Scale suite.

**Worker gate invariant:** Each task's new tests prove the task's acceptance criteria (500 no longer thrown / registry write lands in temp path / prune removes dead rows / index annotates missing roots).

**Lead affected-change scope:** `scripts/test.sh` after each merged batch.

**Branch gate:** `dotnet build Miller.slnx -c Release` (0 warnings) + `scripts/test.sh all` before handoff/PR. Nothing here touches the extract path, but `all` is cheap insurance since Task 3 touches `Miller.Server/Hosting` and Task 4 touches `Miller.Indexing`.

**Replay/metric evidence:** Task 6 records live-machine evidence (HTTP status codes before/after, registry row counts before/after) in the final report — report-only, not a hard gate.

**Escalation triggers:** Any change to `JulieExtractRunner`, freshness, or leadership paths (not planned) ⟹ run Scale suite.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp per task. Reuse passing evidence for the same HEAD instead of rerunning.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Dashboard survives incompatible artifacts | Batch A | Modify `src/Miller.Dashboard/DashboardData.cs`; Test `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs` | No | None - safe parallel batch. |
| Task 2: Dashboard error logging | Batch A | Modify `src/Miller.Dashboard/Program.cs` | No | None - safe parallel batch. |
| Task 3: Registry test isolation + guard | Batch A | Modify `src/Miller.Server/Hosting/IndexBootstrapService.cs` and ALL test files constructing the service: `tests/Miller.Tests/Server/WorkspaceBindingServiceTests.cs`, `HostStartupRegistrationTests.cs`, `IndexerServiceLeadershipTests.cs`, `WorkspaceToolTests.cs`, `IndexerServiceScanTests.cs`, `IndexerWatcherExtensionGateTests.cs`, `LeaderWriteThroughTests.cs`, `LiveWorkspaceTests.cs`, `FreshnessServicePollNowTests.cs`, `VersionAwareLeadershipScaleTests.cs` (all under `tests/Miller.Tests/Server/`); Create `tests/Miller.Tests/Conventions/RegistryIsolationConventionTests.cs` | No | None - safe parallel batch. |
| Task 4: `workspace prune` operation | Batch A | Modify `src/Miller.Indexing/WorkspaceRegistry.cs`, `src/Miller.Server/Tools/WorkspaceTool.cs`, `src/Miller.Server/Cli/CliDispatch.cs`; Test `tests/Miller.Tests/Indexing/WorkspaceRegistryTests.cs`; Create `tests/Miller.Tests/Server/WorkspaceToolPruneTests.cs` | No | None - safe parallel batch. (Tool-level prune tests live in a NEW file so no test file is shared with Task 3.) |
| Task 5: All-workspaces view hygiene | None - serial (after Batch A) | Modify `src/Miller.Dashboard/DashboardData.cs` (`ReadIndex`/`DashboardWorkspaceIndexEntry` region), `src/Miller.Dashboard/Components/WorkspaceIndex.razor`, `src/Miller.Dashboard/Components/WorkspacesShell.razor`; Test `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs` | Yes | Task 1 also edits `DashboardData.cs` and `DashboardRegistryReadTests.cs`; serialize to avoid same-file conflicts. |
| Task 6: One-time machine cleanup + AccessIQ rebuild | None - serial (last) | No repo files; operates on `~/.miller/workspaces.db`, `$TMPDIR/miller-bindsvc-*`, `/Users/murphy/source/AccessIQ` | Yes | Needs Task 4's prune verb shipped and Tasks 1–2 built so the dashboard verification is meaningful. |

Commit mode: `parallel-lead-commit` for Batch A; `serial-worker-commit` acceptable for Tasks 5–6 if executed by the lead directly.

---

### Task 1: Dashboard survives incompatible artifacts (fixes the AccessIQ 500)

**Files:**
- Modify: `src/Miller.Dashboard/DashboardData.cs` — `ReadLocalMetricsPanel` (:834), `ReadPatternInventoryPanel` (:882), `ReadWorkspaceHealthPanel` (:953), `ReadWorkspaceOnboardingPanel` (:1008), `ReadExtractionHealthOrUnavailable` (:1144)
- Test: `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs`

**Interfaces:**
- Consumes: `Miller.Indexing.IncompatibleExtractException` (`src/Miller.Indexing/IncompatibleExtractException.cs:9`, derives directly from `Exception`); `JulieSchemaGate.Verify` throw path via `WorkspaceHealthReader.Read`.
- Produces: `ReadSnapshot` never throws for a registered workspace whose `symbols.db` has an older/newer schema; the health panel carries the actionable rebuild message in its `Error`/summary fields.

**Contract inputs:** The live failure: schema-3 artifact (julie-extract 2.8.1) hits `JulieSchemaGate.Verify` → `IncompatibleExtractException` → escapes the panel readers' catch filter (`KeyNotFoundException, SqliteException, IOException, InvalidOperationException, UnauthorizedAccessException`) → Kestrel 500. Verified by direct `ReadSnapshot` repro on `workspace_id=b45f89f1…`.

**File ownership:** Modify `src/Miller.Dashboard/DashboardData.cs`; Test `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Make every panel reader in the `ReadSnapshot` fan-out degrade to its existing "unavailable" shape when the workspace artifact is schema-incompatible, instead of letting `IncompatibleExtractException` escape as a 500. The health panel is the one surface that should *display* the rebuild guidance ("rebuild the index with `workspace full`…"), so the exception message must land in the panel's error/summary, not be swallowed.

**Approach:** Add `IncompatibleExtractException` to the exception filter of all four panel readers AND to the internal catch in `ReadExtractionHealthOrUnavailable` (it is named "OrUnavailable" — honor that). Follow the existing degrade pattern in each reader (return the record with `"unavailable"` state and `Error: ex.Message`). Do not blanket-catch `Exception`; the repo style is precise filters. New test seeds a temp registry + a contract-faithful schema-3 `symbols.db` (set `artifact_metadata` rows: `schema_version=3`, `extract_contract_version=3`, `binary_version=2.8.1`, `hash_algorithm=blake3` — mirror the existing fixture helpers in `DashboardRegistryReadTests`, e.g. the `ReadSnapshot_UnreadableWorkspaceDbReturnsFactsErrorNotCrash` arrangement at :687) and asserts `ReadSnapshot` returns a snapshot whose health panel state is `"unavailable"` with the schema message present, and that no exception escapes.

**Acceptance criteria:**
- [x] New test: `ReadSnapshot` over a schema-3 artifact returns a snapshot (no throw); health panel `Error` contains the schema/rebuild message.
- [x] All four panel readers catch `IncompatibleExtractException` (helper lets it propagate so Error surfaces — see Task 1 report judgment call).
- [x] Existing `DashboardRegistryReadTests` still pass.
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode.

### Task 2: Dashboard error logging (no more silent 500s)

**Files:**
- Modify: `src/Miller.Dashboard/Program.cs` (whole file is 90 lines; host built at :24–:84)

**Interfaces:**
- Consumes: the dashboard launcher already redirects the process's stdout/stderr to `~/.miller/dashboard.out.log` / `dashboard.err.log` (verified live: fds 1/2 point there).
- Produces: unhandled request exceptions appear in those files with stack traces; failing requests return a plain-text 500 body naming the exception type/message.

**Contract inputs:** Current `Program.cs` uses a bare `new HostBuilder()` — zero logging providers, so ASP.NET's unhandled-exception logging goes nowhere (both log files are 0 bytes on a machine with live 500s).

**File ownership:** Modify `src/Miller.Dashboard/Program.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Minimal observability: console logging (simple console formatter, minimum level **Information** — the dashboard is low-traffic and local, and Information-level `Microsoft.Hosting.Lifetime` startup lines give Task 6 a deterministic "logging pipeline works" signal in `dashboard.out.log`) wired into the host so Kestrel/endpoint exceptions reach the log files, plus a leading exception-handling middleware (`app.Use(...)` try/catch before `UseRouting`) that logs the exception at `LogError` and returns `500` with a short plain-text body (`"miller-dashboard error: <ExceptionType>: <Message>"`). Keep it dependency-free — no Serilog; `Microsoft.Extensions.Logging` console provider only.

**Approach:** `webBuilder.ConfigureLogging(logging => logging.AddSimpleConsole(...).SetMinimumLevel(LogLevel.Information))`. The middleware resolves `ILogger` from `app.ApplicationServices`. Do not add a developer exception page (leaks stack traces to the browser); the plain-text body plus logged stack is enough for a local-first tool. Host wiring is not unit-testable; deterministic verification = Task 6's startup-line evidence (Information lifetime logs in `dashboard.out.log` after restart) plus build gates. Note: after Task 1 lands, the schema-mismatch route intentionally no longer throws, so do not plan on it as an error-path repro.

**Acceptance criteria:**
- [x] Dashboard build succeeds with 0 warnings; fast suite unaffected.
- [ ] Startup lifetime log lines are emitted to stdout (verified live in Task 6 via `dashboard.out.log`); the exception middleware logs at Error and returns the plain-text 500 body.
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode.

### Task 3: Registry test isolation + convention guard (stop the bleed)

**Files:**
- Modify: `src/Miller.Server/Hosting/IndexBootstrapService.cs` — `MarkBootstrapFailed` (:541) and any other `WorkspaceContext.Create` call inside the service (`RunBootstrap` region)
- Modify: ALL test files constructing `IndexBootstrapService` (verified by `grep -rln "new IndexBootstrapService(" tests/`): `tests/Miller.Tests/Server/WorkspaceBindingServiceTests.cs` (`CreateTempDir` :438 and every construction), `IndexerServiceLeadershipTests.cs`, `WorkspaceToolTests.cs`, `IndexerServiceScanTests.cs`, `IndexerWatcherExtensionGateTests.cs`, `LeaderWriteThroughTests.cs`, `LiveWorkspaceTests.cs`, `FreshnessServicePollNowTests.cs`, `VersionAwareLeadershipScaleTests.cs` (Scale-tagged files get the same override treatment)
- Modify: `tests/Miller.Tests/Server/HostStartupRegistrationTests.cs` (constructs the service via `MillerServiceRegistration`/DI, not `new` — set the override on the resolved instance wherever a bootstrap failure path can run)
- Create: `tests/Miller.Tests/Conventions/RegistryIsolationConventionTests.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceBindingServiceTests.cs` (new direct test for the override)

**Interfaces:**
- Consumes: `WorkspaceContext.Create(root, baseDir, homeDirectory)` 3-arg overload (exists — see `WorkspaceContextTests.Create_PutsTheRegistryDbUnderTheUserHomeMillerDir_NotTheRepo` at `tests/Miller.Tests/Server/WorkspaceContextTests.cs:39`).
- Produces: `IndexBootstrapService.TestHomeDirectoryOverride` (internal `string?`, same style as the existing `TestRunBootstrapOverride`/`TestBootstrapInterceptor` hooks). When set, every `WorkspaceContext` the service creates uses it as the home directory, so `RegistryDbPath`/`TelemetryDbPath` land under the override. Convention guard consumed by all future test authors.

**Contract inputs:** Root cause: `MarkBootstrapFailed` builds `WorkspaceContext.Create(canonicalRoot, AppContext.BaseDirectory)` (2-arg → real `~`) and calls `MarkRegistryError`, so every fast-suite run appends `miller-bindsvc-*` error rows ("synthetic async failure"/"synthetic rebind failure") to the real `~/.miller/workspaces.db` — 124 rows accumulated, newest today. The tests' `CreateTempDir` also leaks: 1,185 `miller-bindsvc-*` dirs currently sit in `$TMPDIR`.

**File ownership:** Modify `src/Miller.Server/Hosting/IndexBootstrapService.cs`, the four test files listed; Create `tests/Miller.Tests/Conventions/RegistryIsolationConventionTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** (a) An internal home-override hook on `IndexBootstrapService` routed through a single private helper (`CreateWorkspaceContext(canonicalRoot)`) that both `RunBootstrap` and `MarkBootstrapFailed` use; (b) every test that constructs `IndexBootstrapService` sets the override to a per-test temp dir; (c) tests clean up their temp dirs (`try/finally` or `IDisposable` fixture — fix `CreateTempDir` leakage in `WorkspaceBindingServiceTests`); (d) a source-scanning convention guard, modeled on `tests/Miller.Tests/Conventions/ScaleTraitConventionTests.cs`, that FAILS if any test source file contains `new IndexBootstrapService(` without also referencing `TestHomeDirectoryOverride`.

**Approach:** Keep the override test-only (internal settable property, no env var, no production behavior change — production continues to resolve the real home). The direct regression test: construct the service with the override pointing at a temp dir, drive the `TestRunBootstrapOverride`-throws failure path (mirror `BootstrapForRoot_WhenRunFails_MarksFailedCompletesRunWaitAndRetries` at :104), then open the temp registry with `WorkspaceRegistry.Open` and assert the error row exists there — and assert the path used is under the temp dir, never `~`. Do not weaken the guard to make a test pass; tag any offender instead.

**Acceptance criteria:**
- [x] New direct test proves a failed bootstrap writes its registry error row under the override home, not the real one.
- [x] Convention guard fails on an un-isolated `new IndexBootstrapService(` in test sources (verify by inspection of the guard's scan logic + it passes on the current tree).
- [x] All existing bootstrap/binding/leadership/tool tests pass with overrides applied; temp dirs are cleaned up on test completion.
- [x] Running `scripts/test.sh` adds zero rows to the real `~/.miller/workspaces.db` (worker verifies by row-count before/after on their machine).
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode.

### Task 4: `workspace prune` operation (registry GC)

**Files:**
- Modify: `src/Miller.Indexing/WorkspaceRegistry.cs` (has `List` :264, `Remove` :239, `MarkMissing` :184 — add nothing unless a batched helper is genuinely needed; prune logic composes the existing API)
- Modify: `src/Miller.Server/Tools/WorkspaceTool.cs` — operation switch (:202–:218), usage notes (:1009 region), tool `[Description]`
- Modify: `src/Miller.Server/Cli/CliDispatch.cs` — workspace verb dispatch (:1479–:1482), usage text (:2442 region); follow the existing `remove` implementation (:2095–:2230) for lock/exit-code conventions
- Test: `tests/Miller.Tests/Indexing/WorkspaceRegistryTests.cs`
- Create: `tests/Miller.Tests/Server/WorkspaceToolPruneTests.cs` (tool-level prune tests live in their own file so Task 3's edits to `WorkspaceToolTests.cs` never conflict)

**Interfaces:**
- Consumes: `WorkspaceRegistry.List()/Remove(workspaceId)`; registry rows' `canonical_root`; the existing `remove`-by-path precedent that already best-effort prunes a gone-root row (`CliDispatch.cs:2132`).
- Produces: `workspace(operation="prune")` MCP operation and `miller workspace prune [--dry-run] [--json]` CLI verb (MCP mirror: `dry_run` boolean param, default false). Semantics: for every registry row whose `canonical_root` does not exist on disk, `Remove` the row; with dry-run, list candidates without removing. Compact output: `pruned: N` (or `would prune: N` for dry-run) + up to 10 example `display_id root` lines + `kept: M`. JSON: `{ "dry_run": bool, "pruned": [ { "workspace_id", "display_id", "root" } ], "kept": M }`. Task 6 consumes this verb; Task 5 does NOT depend on it (different signal: annotation vs deletion).
- Prune only removes rows whose root directory is missing. It never deletes on-disk `.miller` data (nothing exists to delete) and never touches rows whose roots exist — junk-but-present workspaces remain the user's call via `workspace remove`.

**Contract inputs:** Registry currently holds 294 rows; 116 non-bindsvc rows plus (post-Task-6 dir deletion) 124 bindsvc rows have nonexistent roots. Row states include `missing` in the schema CHECK but nothing sets/uses it for GC. Removal is safe/reversible: `workspace open` re-registers.

**File ownership:** Modify `src/Miller.Indexing/WorkspaceRegistry.cs`, `src/Miller.Server/Tools/WorkspaceTool.cs`, `src/Miller.Server/Cli/CliDispatch.cs`; Test `tests/Miller.Tests/Indexing/WorkspaceRegistryTests.cs`; Create `tests/Miller.Tests/Server/WorkspaceToolPruneTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** A prune pass exposed on both surfaces of the existing workspace tool. Never prune the current/primary workspace's row even if its root check races (guard by workspace_id). Prune must not spawn julie, must not open any `symbols.db`, and must complete on a 294-row registry in well under a second.

**Approach:** Implement the scan/remove loop in the tool layer (or a small static helper in `Miller.Server/Workspaces/`) composing `WorkspaceRegistry.List` + `Directory.Exists` + `Remove` — keep `Miller.Indexing` free of policy if a plain composition suffices. Wire `case "prune"` into both switches; update the `workspace` tool `[Description]` mention of operations (stay ≤900 chars; `AgentInstructionsTests` gates this) and CLI usage strings. Tests: registry-level (temp registry: rows with existing/missing roots → prune removes exactly the missing ones, returns them), tool-level (compact render lists pruned entries; JSON shape as specified; current workspace never pruned).

**Acceptance criteria:**
- [x] `miller workspace prune` (CLI) and `workspace(operation="prune")` (MCP) remove exactly the rows whose roots are missing and report them.
- [x] `--dry-run` / `dry_run=true` lists the same candidates without removing any row (tested).
- [x] `--json` output matches the shape above; compact output caps examples at 10.
- [x] `AgentInstructionsTests` (description budgets) still pass.
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode.

### Task 5: All-workspaces view hygiene (dashboard separates live from dead)

**Files:**
- Modify: `src/Miller.Dashboard/DashboardData.cs` — `DashboardWorkspaceIndexEntry` (:413), `DashboardWorkspaceIndex` (:422), `ReadIndex` (:441)
- Modify: `src/Miller.Dashboard/Components/WorkspaceIndex.razor`, `src/Miller.Dashboard/Components/WorkspacesShell.razor`
- Test: `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs`

**Interfaces:**
- Consumes: `DashboardData.ReadIndex(registryDbPath)` current shape; Task 1's merged `DashboardData.cs`.
- Produces: `DashboardWorkspaceIndexEntry` gains `bool RootExists`; `DashboardWorkspaceIndex` gains counts (`LiveCount`, `MissingRootCount`, `ErrorCount`). The all-workspaces view renders live workspaces first, then a collapsed/de-emphasized "stale (missing root or errored)" section with counts and a hint to run `workspace prune`.

**Contract inputs:** Today the view dumps all 294 rows in one list. `ReadIndex` already reads every row; adding `Directory.Exists(canonical_root)` per row is acceptable at this scale (registry is ~hundreds of rows).

**File ownership:** Modify `src/Miller.Dashboard/DashboardData.cs` (`ReadIndex` region), `src/Miller.Dashboard/Components/WorkspaceIndex.razor`, `src/Miller.Dashboard/Components/WorkspacesShell.razor`; Test `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs`

**Serialization required:** Yes

**Dependency reason:** Task 1 also edits `DashboardData.cs` and `DashboardRegistryReadTests.cs`; serialize after Batch A merges.

**What to build:** Annotation + grouping only — no deletion from the dashboard (prune stays a deliberate CLI/tool action; the dashboard is a read surface per the repo rule that it must not hydrate/modify). Sort order inside each group stays the current recency order. The stale section header shows `N stale registrations — run \`miller workspace prune\` to clean up`.

**Approach:** Compute `RootExists` in `ReadIndex` (one `Directory.Exists` per row); group in the razor component, not in SQL. Keep markup consistent with the existing card/list styles in `WorkspaceIndex.razor` (match its class names; no new CSS file). Tests assert the new counts and flags from a temp registry with a mix of existing/missing roots and error states.

**Acceptance criteria:**
- [ ] `ReadIndex` returns `RootExists` per entry and correct live/missing/error counts (unit-tested).
- [ ] All-workspaces view groups live entries above a de-emphasized stale section with the prune hint (verified by rendering test if a precedent exists in the suite, else by Task 6 live check).
- [ ] Worker-scope verification passes and the change is committed per commit mode.

### Task 6: One-time machine cleanup + AccessIQ rebuild (ops)

**Files:**
- No repo changes. Operates on live machine state: `$TMPDIR/miller-bindsvc-*` (1,185 leaked test dirs), `~/.miller/workspaces.db` (294 rows), `/Users/murphy/source/AccessIQ` index.

**Interfaces:**
- Consumes: Task 4's `miller workspace prune`; Tasks 1–2 built into the dashboard binary.
- Produces: a clean registry (~50 live rows), a schema-4 AccessIQ artifact, a 200 on the AccessIQ dashboard page, and before/after evidence in the final report.

**Contract inputs:** The bindsvc temp dirs still exist, so prune alone won't catch their rows — delete the dirs first, then prune. AccessIQ artifact is schema 3 (julie-extract 2.8.1); an incremental refresh cannot upgrade it — it needs a force rebuild (`workspace full`, which promotes `symbols.db.rebuild` atomically). The eros workspace has the same mismatch; leave it unless the user asks (eros is deprioritized).

**File ownership:** No repo files; live machine state only.

**Serialization required:** Yes

**Dependency reason:** Requires Task 4 shipped and Tasks 1–2 in the running dashboard build.

**What to build (ordered, with rollback guards):**
1. Record before-evidence: registry row count, AccessIQ page HTTP status on the running dashboard.
2. **Back up the registry**: copy `~/.miller/workspaces.db` to `~/.miller/workspaces.db.bak-<UTC timestamp>` before any mutation. This is the rollback path for every step below.
3. Verify no test run is active (`pgrep -fl "dotnet test|vstest"` empty), then delete only `miller-bindsvc-*` directories directly under `$TMPDIR` **with mtime older than 1 hour** (age guard against racing a concurrent test run). Record the candidate list and deleted count.
4. Restart the dashboard from the new build (kill the running `Miller.Dashboard.dll` process, relaunch via the `workspace` tool `operation=dashboard`).
5. Run `miller workspace prune --dry-run --json` first and record the candidate list; sanity-check it contains no live workspace (e.g., miller, razorback, goldfish roots) before running `miller workspace prune` for real; record pruned/kept counts.
6. Run `workspace full` for AccessIQ (via CLI with `workspace_id` selector or an MCP call against the AccessIQ workspace) and wait for the rebuild to promote; confirm `artifact_metadata.schema_version=4`.
7. After-evidence: AccessIQ dashboard page returns 200 and renders panels; all-workspaces view shows the live/stale split; `dashboard.out.log` contains the Information startup lines (proves the Task 2 logging pipeline end-to-end).

**Acceptance criteria:**
- [ ] Registry backup exists before the first mutation; its path is recorded in the report.
- [ ] Prune dry-run candidate list recorded and sanity-checked before the destructive pass; deleted temp dirs passed the age guard.
- [ ] Registry contains only rows whose roots exist; bindsvc rows and dirs are gone.
- [ ] AccessIQ `symbols.db` reports `schema_version=4` and its dashboard page returns 200.
- [ ] Before/after evidence recorded in the final report (row counts, HTTP statuses, pruned count).
- [ ] No other workspace's data was touched; eros left as-is (noted for the user).
