# Phase 7 workspace bounds and readiness evidence

- **Date:** 2026-07-23
- **Scope:** Phase 7C `workspace` bounds and authoritative symbol-read readiness
- **Worktree baseline:** `7306f9c672ea91f90899f7c51080308ef1f4cac3`

## Result

- `workspace list` now assembles `WorkspaceListFacts` from registry rows only. MCP and CLI report exact
  selection, error, and missing-root totals without opening a workspace index.
- Compact health is capped at 14 lines. Dynamic values are flattened and capped at 240 characters, and the final
  line reports the exact hidden extraction-row, unavailable-section, warning, and action counts.
- MCP health is compact or summary JSON under the 12 KiB workspace ceiling. Exhaustive JSON and markdown are
  CLI-only.
- Onboarding aggregates in SQLite, reads one snapshot, and reports exact totals and omissions for every bounded
  section while keeping all privacy notes.
- Status gathers leader and extractor-version facts for current and selected registered workspaces.
- Open, remove, refresh, health, list, prune, and leader options use operation-specific validation and typed
  diagnostics. Removal resolves a registered target through one shared CLI/MCP/dashboard safety core.
- `inspect` now resolves through `IWorkspaceSymbolReadProvider`, whose authoritative input is `symbols.db`.
  Missing, stale, or corrupt `search.db` does not block the read. `IWorkspaceSearchProvider` remains separate and
  still refuses missing or stale required search artifacts.
- Health keeps derived-artifact failures as typed `HealthWarning` values and pairs missing/stale artifacts with a
  `workspace refresh` action and corrupt/unreadable artifacts with a `workspace full` action.

## Public shapes

```text
WorkspaceListFacts(
  Entries, Registered, Matched, Returned, Omitted, OmittedErrors, Filter, Limit,
  RegisteredMissing, MatchedMissing, ReturnedMissing)

Workspace MCP health = Compact | JsonSummary
Workspace CLI health = Compact | Json | Markdown

IWorkspaceSymbolReadProvider.ResolveSymbolRead(workspaceId, ensureFresh)
  -> WorkspaceSymbolReadContext(
       Index, IndexDbPath, WorkspaceId, WorkspaceRoot, Revision,
       IndexFresh, FreshnessStatus, WarningText, DisplayId, IsCurrent)
```

No MCP tool or parameter was added. The unsafe health `detail` parameter and MCP markdown format were removed.

## Verification

- Sidecar separation regression: 6 passed, covering `inspect` with missing/stale/corrupt `search.db`, search refusal
  for missing/stale `search.db`, and corruption diagnostics.
- Workspace/read-path gate: 453 passed across workspace tool/facts/render/health/provider, CLI workspace,
  host registration, inspect, context, impact, and diagnostic integration tests.
- Deep Workspace re-audit gate: 367 passed across Workspace, renderer, removal, onboarding SQL, health snapshot,
  prune, and CLI tests.
- `dotnet build Miller.slnx -c Release --no-restore`: passed with 0 warnings and 0 errors.
- `git diff --check`: passed.

The tests preserve current-workspace binding, refresh/freshness routing, provider cache invalidation, host
construction, version-aware leader health, registry lifecycle, and search-sidecar refusal behavior. The list path
continues to consume registry rows only, so dashboard and registry read constraints are unchanged.

## Architecture review

- The facts assembler owns selection and totals; renderers consume immutable facts and do no registry I/O.
- Removal derives its delete path from a validated registry root, rejects machine-global/sensitive targets, and
  never deletes an unregistered `.miller` directory.
- The provider owns the difference between authoritative symbol reads and optional search-sidecar readiness;
  `InspectTool` does not catch or interpret sidecar exceptions.
- Symbol-read and symbol-search contexts and caches are separate, preventing an in-memory authoritative projection
  from replacing or disguising a required on-disk search backend.
- `Miller.Core` remains unchanged and I/O-free.

The deep re-audit and Julie comparison are recorded in
[`2026-07-25-workspace-correctness-safety-and-bounds.md`](2026-07-25-workspace-correctness-safety-and-bounds.md).
