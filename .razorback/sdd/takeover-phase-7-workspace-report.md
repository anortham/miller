# Phase 7C Workspace Worker Report

## Worktree

- Start path: `/Users/murphy/source/miller/.worktrees/miller-julie-takeover`
- Start branch: `codex/miller-julie-takeover`
- Start HEAD: `7306f9c672ea91f90899f7c51080308ef1f4cac3`
- Start state: clean
- End path: `/Users/murphy/source/miller/.worktrees/miller-julie-takeover`
- End branch: `codex/miller-julie-takeover`
- End HEAD: `7306f9c672ea91f90899f7c51080308ef1f4cac3`
- End state: dirty and unstaged, with this lane's files plus concurrent Phase 7 content/patterns files
- Staged or committed: no

## Implemented

- MCP and CLI workspace lists report exact registered, matched, returned, omitted, omitted-error, filter, and
  limit facts assembled from registry rows only.
- Compact health is capped at 14 lines. Dynamic values are single-line and capped at 240 characters; the output
  reports exact hidden extraction-row, warning, and action counts.
- Health JSON remains complete. CLI `--markdown` and MCP `format="markdown"` return the compact summary followed
  by the complete JSON report.
- Health sidecar warnings carry typed codes and explicit `workspace refresh` or `workspace full` recovery.
- `inspect` no longer resolves through the search-sidecar provider. It uses a dedicated authoritative symbol-read
  provider/context/cache backed by the current holder or a registered workspace's `symbols.db`.
- Search remains on its distinct provider and still fails visibly for missing, stale, or corrupt required
  `search.db`.
- Contracts, docs map, and Phase 7C findings evidence are current.

## Public API Shapes

- Existing `workspace` and `inspect` MCP tools only; no new MCP tool or parameter.
- `WorkspaceListFacts(Entries, Registered, Matched, Returned, Omitted, OmittedErrors, Filter, Limit)`.
- `WorkspaceHealthFormat`: `Compact`, `Json`, `Markdown`.
- `IWorkspaceSymbolReadProvider.ResolveSymbolRead(string? workspaceId, bool ensureFresh)`.
- `WorkspaceSymbolReadContext(Index, IndexDbPath, WorkspaceId, WorkspaceRoot, Revision, IndexFresh,
  FreshnessStatus, WarningText, DisplayId, IsCurrent)`.
- `IWorkspaceSearchProvider.ResolveSymbolSearch(...)` remains unchanged and sidecar-required.

## RED/GREEN Evidence

- The first focused RED was a compile failure at the new `WorkspaceHealthFormat` references; production had no
  format shape or complete markdown health path.
- After the minimal format shape restored shared compilation, the sidecar-recovery RED failed because
  `RecommendedActions` was empty for typed search/content sidecar warnings.
- Exact list and authoritative-read tests were written before production changes, but the initial format compile
  failure prevented their individual assertions from executing in that first shared RED run.
- GREEN for the directly affected list/render/health/inspect/search-separation slice: 29 passed.
- Missing/stale/corrupt sidecar separation GREEN: 6 passed. `inspect` served all three states; search missing/stale
  refusal and corruption diagnostics remained visible.
- Final workspace/read-path GREEN: 453 passed across workspace tool/facts/render/health/provider, CLI workspace,
  host registration, inspect, context, impact, and diagnostic integration tests.

## Worker Ceiling

- `dotnet build Miller.slnx -c Release --no-restore`: passed, 0 warnings, 0 errors.
- `git diff --check`: passed.
- No scale test was required: this slice does not spawn or change `julie-extract`.

## Claude Review

- Fresh read-only review approved the workspace slice with no findings.
- The review confirmed exact registry totals, the 14-line compact-health ceiling, complete JSON/markdown output,
  authoritative symbol reads, visible search-sidecar failures, cache behavior, and recovery actions.
- Lead verification confirmed CLI `workspace health --markdown` selects `WorkspaceHealthFormat.Markdown`.

## Architecture Quality

- Affected modules: registry facts/rendering, workspace health formatting, health readiness warnings, workspace
  provider routing/caching, inspect adapter, DI registration, and CLI workspace adapter.
- Caller-facing interface: additive facts/format/provider types; existing MCP tools and search contract preserved.
- Depth/locality: `WorkspaceFactsAssembler` owns selection and totals; `WorkspaceRender` is deterministic
  presentation; `WorkspaceIndexProvider` owns authoritative-versus-derived readiness policy.
- Test surface: public workspace list/health, CLI workspace, public inspect, provider search failure, and
  representative context/impact compatibility.
- Seams/adapters: the new symbol-read seam has a distinct context and cache, so authoritative projections cannot
  replace or disguise a required search backend.
- Rejected shortcuts: catch search-sidecar exceptions inside `InspectTool`, disable the search sidecar, hydrate
  full indexes for list, compute totals independently in MCP/CLI renderers, or truncate JSON.
- `Miller.Core` is unchanged and remains I/O-free.
- Architecture risk: medium before tests because provider routing and output contracts changed; low residual risk
  after the focused, 453-test, build, and diff gates.

## Owned Changed Files

- `docs/README.md` (Phase 7C finding pointer only; concurrent Phase 7 pointers preserved)
- `docs/contracts/cli-eros-v1.md` (workspace-list additions only; concurrent content additions preserved)
- `docs/contracts/workspace-health-v1.md`
- `docs/findings/2026-07-23-phase-7-workspace-bounds.md`
- `src/Miller.Server/Cli/CliDispatch.cs` (workspace list/health only; concurrent content edits preserved)
- `src/Miller.Server/Hosting/MillerServiceRegistration.cs`
- `src/Miller.Server/Tools/InspectTool.cs`
- `src/Miller.Server/Tools/ReadToolWorkspaceRouting.cs`
- `src/Miller.Server/Tools/WorkspaceFactsAssembler.cs`
- `src/Miller.Server/Tools/WorkspaceHealthFacts.cs`
- `src/Miller.Server/Tools/WorkspaceRender.cs`
- `src/Miller.Server/Tools/WorkspaceTool.cs`
- `src/Miller.Server/Workspaces/IWorkspaceSymbolReadProvider.cs`
- `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`
- `src/Miller.Server/Workspaces/WorkspaceSymbolReadContext.cs`
- `tests/Miller.Tests/ReadToolRoutingTestSupport.cs`
- `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` (workspace health markdown test only)
- `tests/Miller.Tests/Server/HolderRepointTests.cs`
- `tests/Miller.Tests/Server/InspectToolTests.cs`
- `tests/Miller.Tests/Server/ToolDiagnosticIntegrationTests.cs`
- `tests/Miller.Tests/Server/WorkspaceHealthLeaderTests.cs`
- `tests/Miller.Tests/Server/WorkspaceRenderTests.cs`
- `tests/Miller.Tests/Server/WorkspaceToolTests.cs`
- `.razorback/sdd/takeover-phase-7-workspace-report.md`

## Unresolved Risks

- Complete markdown intentionally contains the complete JSON report and can be large; compact is the bounded agent
  default.
- The compact omission contract currently names six extraction-detail groups. A future health section must update
  that explicit count and its tests rather than silently appearing complete.
- `context`, `impact`, and `trace` were already independent of optional sidecars through
  `IWorkspaceIndexProvider`; registered-workspace calls may still hydrate their full authoritative index by
  design. The new lean projection seam currently serves `inspect`.
- Branch-wide final gates remain lead-owned.
