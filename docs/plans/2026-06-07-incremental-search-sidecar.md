# Incremental Search Sidecar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Keep Miller's `search.db` sidecar fresh through the same incremental watcher/write-through path that keeps `symbols.db` fresh, while making stale sidecar state visible instead of silently loading large in-memory search projections.

**Architecture:** Move the sidecar from a bulk-only derived artifact to a lock-maintained artifact with explicit symbol identity. Bulk rebuild remains the repair/schema/full-scan path; file updates and deletes update only rows for changed paths and stamp the new revision. Symbol search should normally require a revision-fresh sidecar when the sidecar is enabled.

**Tech Stack:** .NET 10, Microsoft.Data.Sqlite, SQLite FTS5, julie-extract `symbols.db`, xUnit.

**Architecture Quality:** Medium risk. Affected modules are `Miller.Indexing` sidecar schema/writer/reader and `Miller.Server` indexer/search routing. The caller-facing interfaces should stay small: add sidecar maintenance APIs on `SymbolSearchSidecar` or a focused writer helper, and keep search tools reading through `IWorkspaceSearchProvider`. The main rejected shortcut is preserving `rowid` alignment: it was acceptable for a bulk append-only artifact, but incremental updates need explicit `doc_id`/`symbol_id` identity and fail-visible freshness.

---

## File Structure

- Modify `src/Miller.Indexing/SearchIndexWriter.cs`
  - Add explicit `doc_id` to `search_symbols`.
  - Populate FTS rows with explicit row identity or join by `symbol_id`, not accidental insertion order.
  - Add incremental path maintenance that deletes/replaces rows for changed paths and recomputes meta stats.
- Modify `src/Miller.Indexing/FtsSymbolSearchIndex.cs`
  - Read `doc_id` explicitly instead of `search_symbols.rowid - 1`.
  - Order lookup results by `doc_id`.
  - Make malformed/stale sidecar failures visible to callers that require disk.
- Modify `src/Miller.Indexing/SymbolSearchSidecar.cs`
  - Keep bulk `EnsureBuilt` for missing/stale/schema/full repair.
  - Add an incremental update/delete API called only by the lock-holding writer after single-file extract ops.
- Modify `src/Miller.Server/Hosting/IndexerCore.cs` or `src/Miller.Server/Hosting/IndexerService.cs`
  - After watcher/write-through `UpdateOp`/`DeleteOp`, maintain `search.db` incrementally when enabled.
  - Keep full sidecar rebuild for startup scan, explicit refresh/full, overflow, and HEAD-change scans.
- Modify `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs` and CLI read routing if needed
  - Replace silent default fallback with explicit stale/missing/corrupt sidecar behavior when the sidecar is enabled.
  - Preserve opt-out behavior for `MILLER_SEARCH_SIDECAR=0`.
- Modify `src/Miller.Server/Tools/WorkspaceRender.cs` and related facts reader if needed
  - Surface `search.db` revision/freshness in `workspace status`.
- Tests:
  - `tests/Miller.Tests/Indexing/SearchIndexWriterTests.cs`
  - `tests/Miller.Tests/Indexing/FtsSymbolSearchIndexTests.cs`
  - `tests/Miller.Tests/Indexing/SymbolSearchSidecarTests.cs`
  - `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs`
  - `tests/Miller.Tests/Server/IndexerServiceScanTests.cs`
  - status rendering tests if status output changes.

## Tasks

### Task 1: Remove accidental rowid identity

Write failing tests proving a sidecar with non-contiguous SQLite rowids still resolves/searches by stable `doc_id` and `symbol_id`. Implement explicit sidecar identity and update all lookups/orderings to use `doc_id`.

### Task 2: Add incremental sidecar maintenance

Write failing tests for update and delete of one path after a new `symbols.db` revision. Implement sidecar path maintenance under the writer path: delete existing rows for affected paths, insert current rows for those paths, update FTS tables, and recompute meta counts/averages.

### Task 3: Wire watcher/write-through convergence

Write failing tests showing single-file write-through advances both `symbols.db` and `search.db`. Wire the sidecar maintenance call into the incremental extract op path, while full scan/refresh keeps using bulk repair.

### Task 4: Make fallback fail-visible by default

Write failing tests showing enabled sidecar + stale/missing/corrupt artifact does not silently load the memory projection for registered/current symbol search. Keep explicit opt-out behavior through `MILLER_SEARCH_SIDECAR=0`.

### Task 5: Surface sidecar status

Add `workspace status` output and JSON fields for `search.db` revision/freshness/missing state. Ensure dashboard/status readers do not hydrate the full index.

## Verification Strategy

**Project source of truth:** `AGENTS.md` / `CLAUDE.md`, especially the fast-vs-scale split.

**Worker red/green scope:** Focused `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~<changed-test-class> --no-restore`.

**Lead affected-change scope:** `scripts/test.sh` after a coherent implementation batch.

**Branch gate:** `dotnet build Miller.slnx -c Release` and `scripts/test.sh all` if the indexing/extract path changes.

**Escalation triggers:** If incremental FTS5 updates require schema changes that break existing sidecar readers, bump `SearchIndexWriter.SchemaVersion` and add compatibility tests. If default fail-visible fallback breaks CLI workflows without a recovery path, stop and review the user-facing error/refresh behavior before broadening the change.
