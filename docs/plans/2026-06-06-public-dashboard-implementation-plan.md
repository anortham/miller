# Public Dashboard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Build the approved public-ready Miller dashboard slice: local workspace/index transparency, basic operations, telemetry, and defensible context-savings metrics without adding Eros-owned intelligence views.

**Architecture:** Keep the dashboard read path aggregate-only. The UI reads `~/.miller/workspaces.db`, `~/.miller/telemetry.db`, and read-only aggregate SQL from each workspace `symbols.db`; it must not hydrate full repository indexes, bridge graphs, or BM25 search structures. Preserve existing `/workspaces.json` and `/telemetry.json`; add a richer `/snapshot.json` endpoint for the new dashboard read model.

**Tech Stack:** .NET 10, ASP.NET Core loopback host, static SSR Razor components, htmx fragments, SQLite via `Microsoft.Data.Sqlite`, xUnit v3 tests, repo test wrapper `scripts/test.sh`.

**Architecture Quality:** Approved shape from `docs/plans/2026-06-06-public-dashboard-design.md`: medium risk, constrained to `Miller.Dashboard`, small telemetry enrichment in `Miller.Server.Tools`, and aggregate readers. Main risk is accidentally turning dashboard reads into full-index hydration or inventing context-savings numbers from row counts.

---

## File Structure

- Modify: `src/Miller.Dashboard/DashboardData.cs:11-386`
  - Keep `DashboardWorkspaceRow`, `ReadWorkspaces`, `RenderWorkspacesJson`, `DashboardTelemetrySummary`, and `RenderTelemetryJson` backward-compatible.
  - Add richer snapshot records and context-savings records.
  - Add `ReadSnapshot` composition for workspace summaries, selected workspace details, telemetry, and context savings.
  - Add `RenderSnapshotJson`.
- Create: `src/Miller.Dashboard/DashboardIndexFactsReader.cs`
  - Own aggregate read-only SQL against workspace `symbols.db`.
  - Return file count, symbol count, language count, content bytes, top languages, top symbol kinds, revision/freshness signals available from the registry row and DB.
  - Fail closed to a non-fatal status object when the DB is missing, stale-schema, corrupt, or unreadable.
- Modify: `src/Miller.Dashboard/Program.cs:40-98`
  - Use rich snapshots for `/`, `/fragments/dashboard`, and `/fragments/workspaces`.
  - Add `/snapshot.json`.
  - Preserve `/workspaces.json`, `/telemetry.json`, `/healthz`, static asset, and refresh routes.
- Modify: `src/Miller.Dashboard/Components/DashboardContent.razor:3-10`
  - Render workspace table, selected workspace detail, context savings, and telemetry.
- Modify: `src/Miller.Dashboard/Components/WorkspacesPanel.razor:3-56`
  - Replace button-list layout with a scan-friendly table/list using rich workspace summaries and language bars.
- Create: `src/Miller.Dashboard/Components/WorkspaceDetailPanel.razor`
  - Render selected workspace stats, top languages, symbol kinds, freshness, sidecar/search health, last scan, and refresh action.
- Create: `src/Miller.Dashboard/Components/ContextSavingsPanel.razor`
  - Render returned tokens, tracked source bytes, saved bytes, savings ratio, and per-tool breakdown.
  - Render "not yet tracked" when source bytes are absent.
- Modify: `src/Miller.Dashboard/Components/TelemetryPanel.razor:3-126`
  - Keep the table but make it a supporting operational panel; include returned tokens consistently with context-savings summary.
- Modify: `src/Miller.Dashboard/Components/DashboardShell.razor`
  - Add `/snapshot.json` link and update header copy to local index transparency.
- Modify: `src/Miller.Dashboard/wwwroot/dashboard.css:1-260`
  - Implement the approved Miller theme: light, crisp, restrained teal/green plus neutral grays, dense table/detail layout, responsive mobile behavior.
- Modify: `src/Miller.Server/Tools/ContextTool.cs:69-90`
  - Populate `TelemetryScope.SourceBytes` only with real candidate/render source byte totals available from selected/candidate symbols.
- Modify: `src/Miller.Server/Tools/SearchTool.cs:106-170`
  - Populate `SourceBytes` only for search paths where the search index/projection can report a defensible byte count without full hydration. If a path cannot provide real bytes cheaply, leave `SourceBytes` at zero.
- Modify: `src/Miller.Server/Tools/InspectTool.cs:39-154`
  - Populate `SourceBytes` for file/body inspection when the file content or symbol body bytes are already read for output. Do not add extra disk reads only for telemetry.
- Test: `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs:34-470`
  - Extend current dashboard tests for rich snapshot reads, JSON contract, component rendering, context-savings rollups, missing/stale DB behavior, and backward-compatible existing endpoints.
- Test: existing targeted tool tests near `tests/Miller.Tests/Server/ContextToolTests.cs`, `tests/Miller.Tests/Server/SearchToolTests.cs`, and inspect-related tests.
  - Add telemetry assertions only where the test already exercises a path with real source bytes.

## Verification Strategy

**Project source of truth:** `AGENTS.md` testing section and `tests/Miller.Tests/Miller.Tests.csproj` default `Category!=Scale` filter.

**Worker red/green scope:** Focused fast tests for changed behavior using the repo wrapper when possible:

```bash
scripts/test.sh --filter "FullyQualifiedName~DashboardRegistryReadTests"
```

If the wrapper does not pass through a focused filter cleanly, use:

```bash
dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~DashboardRegistryReadTests"
```

**Worker ceiling:** Workers may run focused dashboard/tool tests plus `scripts/test.sh` after their own committed task. Workers should not run scale tests unless their task touches extract spawning or real `julie-extract` behavior.

**Worker gate invariant:** Focused tests prove the changed read model, component render, or telemetry enrichment behavior. `scripts/test.sh` proves the fast suite remains pure and below the default time-budget tripwire.

**Lead affected-change scope:** After the coherent dashboard batch, run:

```bash
scripts/test.sh
dotnet build Miller.slnx -c Release
git diff --check
```

**Branch gate:** Before public-release handoff or push:

```bash
scripts/test.sh
dotnet build Miller.slnx -c Release
git diff --check
```

Run `scripts/test.sh scale` only if implementation starts spawning `julie-extract`, changes extract/indexing paths, or the lead needs real-workspace dashboard evidence beyond browser/local DB checks.

**Replay/metric evidence:** Capture `/snapshot.json` from a real Miller workspace and browser screenshots at desktop and mobile widths. Hard gates: no crash, correct selected workspace, no full-index hydration in dashboard code, context savings says "not yet tracked" when `source_bytes = 0`, and tracked savings use real source bytes. Report-only metrics: exact saved-byte totals from local telemetry.

**Escalation triggers:** Broaden verification if dashboard reads call `RepositoryIndexLoader`, tool output changes, telemetry schema changes, `scripts/test.sh` exceeds the time budget, or browser verification shows layout overlap/overflow.

**Assigned verification failure:** Workers stop and report when assigned verification fails unless the failing assertion is the red phase of their own TDD cycle.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp in the final implementation evidence. For browser evidence, include URL, viewport, and the screenshot/check performed.

## Model Routing

**Project source of truth:** No repo-root `RAZORBACK.md` exists. Use inherited Codex session defaults for all tiers unless the user specifies a reviewer or model.

**Strategy tier:** planning, architecture, decomposition, lead review, finding triage.
- Harness mapping: inherit.

**Implementation tier:** bounded worker tasks from this approved plan.
- Harness mapping: inherit.

**Mechanical tier:** docs, CSS-only cleanup, fixture updates, rote endpoint wiring with no gate interpretation.
- Harness mapping: inherit.

**Gate-interpretation reviewer:** reading failing tests, browser evidence, and diffs to decide whether the implementation or expectation is wrong.
- Harness mapping: inherit.

**Escalation tier:** subtle telemetry semantics, accidental full-index hydration, schema/contract changes, repeated failures, browser layout failures.
- Harness mapping: inherit.

**Worker eligibility:** Implementation-tier workers are eligible once this plan is approved and tasks can be assigned with non-overlapping file ownership.

**Escalation triggers:** Escalate to lead if a worker needs to change telemetry schema, alter CLI/MCP output, add dashboard routes outside the approved boundary, or make `scripts/test.sh scale` mandatory.

**Mechanical exclusion:** Mechanical workers cannot own failing tests, context-savings semantics, replay evidence, metrics, or acceptance gates.

**Unsupported harness behavior:** If subagent execution cannot choose models per worker, use `inherit` and continue.

## Tasks

### Task 1: Rich Dashboard Read Model And Snapshot Endpoint

**Files:**
- Create: `src/Miller.Dashboard/DashboardIndexFactsReader.cs`
- Modify: `src/Miller.Dashboard/DashboardData.cs:11-386`
- Modify: `src/Miller.Dashboard/Program.cs:40-98`
- Test: `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs:34-470`

**What to build:** Add aggregate workspace/index facts and a rich dashboard snapshot without changing the existing lightweight JSON endpoints. The snapshot should include registered workspace summaries, selected workspace detail, telemetry, and context-savings rollup.

**Approach:** Use read-only aggregate SQL over `files`, `symbols`, and artifact metadata. Catch unreadable/missing/stale-schema DB errors and render a non-fatal facts status instead of throwing. Keep `/workspaces.json` and `/telemetry.json` stable; add `/snapshot.json` for the richer read model used by the UI.

**Acceptance criteria:**
- [ ] `ReadWorkspaces` and `RenderWorkspacesJson` keep their current shape.
- [ ] `ReadSnapshot` returns rich workspace summaries with file count, symbol count, language count, top languages, top symbol kinds, content bytes, and selected workspace.
- [ ] `RenderSnapshotJson` uses snake_case JSON and includes context-savings fields.
- [ ] Missing, corrupt, or schema-mismatched workspace DBs produce a safe status object, not a dashboard crash.
- [ ] Focused dashboard tests pass, committed.

### Task 2: Workspace Transparency UI

**Files:**
- Modify: `src/Miller.Dashboard/Components/DashboardContent.razor:3-10`
- Modify: `src/Miller.Dashboard/Components/WorkspacesPanel.razor:3-56`
- Create: `src/Miller.Dashboard/Components/WorkspaceDetailPanel.razor`
- Modify: `src/Miller.Dashboard/Components/DashboardShell.razor`
- Modify: `src/Miller.Dashboard/wwwroot/dashboard.css:1-260`
- Test: `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs:276-342`

**What to build:** Replace the current two-panel beta view with the approved workspace atlas: scan-friendly workspace table/list, language bars, selected workspace stat summary, language and symbol-kind breakdowns, freshness, last scan, sidecar/search health, and refresh action.

**Approach:** Keep SSR Razor plus htmx fragments. Use stable dimensions for metric tiles and language bars. Make paths truncate cleanly, keep table/list responsive on mobile, and keep the UI light and dense. Do not add navigation for Eros-owned intelligence pages.

**Acceptance criteria:**
- [ ] Dashboard renders workspace status, path, revision, files, symbols, language bar, and selected-state behavior.
- [ ] Selected workspace detail renders top languages, symbol kinds, freshness/scan data, and refresh action.
- [ ] Empty and error states remain visible and useful.
- [ ] CSS avoids text overlap/overflow at desktop and mobile widths.
- [ ] Focused component render tests pass, committed.

### Task 3: Context Savings Rollup And Defensible Telemetry

**Files:**
- Create: `src/Miller.Dashboard/Components/ContextSavingsPanel.razor`
- Modify: `src/Miller.Dashboard/DashboardData.cs:22-386`
- Modify: `src/Miller.Server/Tools/ContextTool.cs:69-90`
- Modify: `src/Miller.Server/Tools/SearchTool.cs:106-170`
- Modify: `src/Miller.Server/Tools/InspectTool.cs:39-154`
- Test: `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs:66-140`
- Test: relevant focused tool telemetry tests in `tests/Miller.Tests/Server/ContextToolTests.cs`, `tests/Miller.Tests/Server/SearchToolTests.cs`, and inspect-related tests.

**What to build:** Show Julie-style context savings in Miller using existing telemetry columns. Populate `source_bytes` only where tools already have a real byte count or can compute one cheaply from data already loaded for the request.

**Approach:** Dashboard rollup computes `saved_bytes = max(0, source_bytes - bytes_returned)` and `savings_ratio` only when `source_bytes > 0`. If `source_bytes = 0`, UI must say "not yet tracked". For tools, prefer adding small out parameters or local counters in pure run methods only when the count is defensible; otherwise leave telemetry unchanged.

**Acceptance criteria:**
- [ ] Dashboard context-savings rollup includes source bytes, returned bytes, saved bytes, savings ratio, returned estimated tokens, and per-tool breakdown.
- [ ] UI renders "not yet tracked" when source bytes are absent.
- [ ] Tool telemetry never invents savings from row counts or graph node counts.
- [ ] At least one focused tool test proves real `SourceBytes` is recorded for a path that already has source bytes.
- [ ] Focused telemetry/dashboard tests pass, committed.

### Task 4: Final Browser, Package, And Release-Readiness Evidence

**Files:**
- Modify: `docs/findings/2026-06-06-public-dashboard-dogfood.md`
- Modify if needed: `tests/Miller.Tests/Indexing/MillerExtractContractTests.cs` only if dashboard packaging expectations drift.

**What to build:** Validate the implemented dashboard as a public-release readiness slice and record durable evidence.

**Approach:** Use @browser:control-in-app-browser for local dashboard verification after implementation. Start the dashboard through `miller dashboard` or a direct project run with test env vars as appropriate. Capture desktop and mobile verification, `/snapshot.json`, and representative context-savings output after real tool calls.

**Acceptance criteria:**
- [ ] `scripts/test.sh` passes.
- [ ] `dotnet build Miller.slnx -c Release` passes with 0 warnings / 0 errors.
- [ ] `git diff --check` passes.
- [ ] Browser verification proves desktop and mobile layouts have no incoherent overlap or blank panels.
- [ ] `/snapshot.json` returns rich data for a real workspace.
- [ ] Release evidence documents context-savings behavior, including tracked and not-yet-tracked states.
- [ ] Static dashboard assets still package in release archive checks.

## Execution Notes

- Use @razorback:test-driven-development for implementation tasks.
- Use @miller-explore-area before editing unfamiliar symbols.
- Use @miller-impact-analysis before changing shared telemetry or dashboard snapshot contracts.
- Use @browser:control-in-app-browser for final visual verification.
- Keep commits task-sized. Do not push, publish, tag, or release without explicit user approval.
