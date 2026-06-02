# Search Projections Design

## Context

Miller currently routes `search`, `inspect`, `context`, `trace`, and `impact`
through one `IWorkspaceIndexProvider.Resolve(...)` method. That method returns a
`WorkspaceReadContext` carrying a full `MillerRepositoryIndex`, which means a
registered-workspace search loads symbols, dependency edges, identifier fallback
edges, bridge breadcrumbs, and the bridge graph before it can rank symbol names.

The 2026-06-02 projection spike showed that this is the wrong default for large
workspaces:

- OpenClaw full `RepositoryIndexLoader.Load`: about 6.7s and +582MB heap.
- OpenClaw current symbol projection: about 1.8s and +55MB heap.
- Hermes full `RepositoryIndexLoader.Load`: about 3.8s and +235MB heap.
- Hermes current symbol projection: about 0.8s and +18MB heap.

The earlier spike proved Miller's search mechanics were viable. It did not finish
the runtime read-model design after Miller moved to `julie-extract` artifacts.
This design closes that gap.

## Decision

Split Miller reads into explicit projections. Tool code must ask for the smallest
projection that can answer the request.

## Projection Types

### Symbol Search Projection

Purpose:

- Rank named symbols for `search`.
- Hydrate search hits back to `IndexedSymbol`.
- Support test filtering and compact/json rendering.

Data:

- `symbol_id`
- `name`
- `signature`
- `kind`
- `language`
- `path`
- `start_line`
- `end_line`
- `parent_symbol_id`
- `is_test`

Implementation:

- Load with `SqliteSymbolReader.Read(dbPath)`.
- Build the existing `MillerSearchIndex`.
- Keep the `DocId -> IndexedSymbol` array for result hydration.
- Do not read relationships, identifiers, bridge rows, file contents, or docs.

### Full Repository Projection

Purpose:

- Existing graph and bridge workflows.
- `trace`
- `impact`
- deep `context`
- full `inspect`
- edit and rename paths that need graph-aware resolution.

Implementation:

- Keep `RepositoryIndexLoader.Load(dbPath)` as the full loader.
- Cache separately from symbol search.
- Load only when a full-read tool asks for it.

### Content Search Projection

Purpose:

- Search docs and file content, including future Julie web-research parity.
- Return content/file results as a separate result kind, not as fake symbols.

Initial implementation:

- In-memory text index behind an interface.
- Build from root-relative files verified by the `files.content_hash` contract.
- Do not make SQLite FTS5 the default yet.

Reason:

- The spike showed docs/content in-memory rebuilds are cheap enough for the
  measured large repos.
- Persisted FTS adds invalidation and lifecycle complexity before the product
  shape is proven.
- FTS5 trigram is larger and slower for the measured docs workload, so it is not
  a default engine.

## Provider Shape

Introduce a search-specific provider interface:

```csharp
public interface IWorkspaceSearchProvider
{
    WorkspaceSymbolSearchContext ResolveSymbolSearch(string? workspaceId, bool ensureFresh);
}
```

Keep the existing full provider:

```csharp
public interface IWorkspaceIndexProvider
{
    WorkspaceReadContext Resolve(string? workspaceId, bool ensureFresh);
}
```

`WorkspaceIndexProvider` implements both interfaces. Its registered-workspace
caches are separate:

- `CacheKey(workspaceId, indexDbPath, revision)` -> full repository index.
- `CacheKey(workspaceId, indexDbPath, revision)` -> symbol search projection.

The cache key still includes `indexDbPath` and `revision` so path changes and
extract updates reload the correct projection. Older entries for the same
workspace are evicted after a newer entry is installed.

Current workspace search can use the already-built full index because the
bootstrap still seeds an `IndexHolder` with a full repository index. A later
startup-phase change can make even the current workspace bootstrap
projection-specific; this design does not require that to make registered
workspace search cheap.

## Tool Routing

Phase 1:

- `SearchTool` depends on `IWorkspaceSearchProvider`.
- `SearchTool.Run(...)` accepts the new `ISymbolSearchIndex` seam.
- Existing rendering, JSON shape, ordering, and test filtering stay unchanged.
- Status: implemented.

Phase 2:

- Basic `InspectTool` uses a lookup/search projection for file and symbol summary
  reads.
- Full inspect continues to request the full repository projection.
- Status: implemented.

Phase 3:

- Add content/docs search as its own projection and result kind.
- Keep symbol and content ranking independent until result merging is measured.

Phase 4:

- Widen symbol search fields deliberately.
- Add doc comments, identifier names, literals, or type facts only behind measured
  quality and cost tests.

## Rejected Shortcuts

- Widen the current full `MillerRepositoryIndex`.
  - Rejected because it makes the first-read tax worse.
- Hide full loading behind a lazy property on one context.
  - Rejected because cheap tools could accidentally hydrate the graph.
- Make docs/content search part of symbol search.
  - Rejected because symbols and prose have different result contracts.
- Start with persisted FTS.
  - Rejected for phase 1 because invalidation and persistence are not required to
    solve the measured bottleneck.
- Use FTS5 trigram as default.
  - Rejected because spike results showed it was larger and slower for docs.

## Acceptance Gates

Functional:

- `search` for a registered workspace does not call `RepositoryIndexLoader.Load`.
- `inspect depth=summary` for a registered workspace does not call
  `RepositoryIndexLoader.Load`.
- `inspect depth=full` still requests the full repository projection.
- `search` output stays byte-compatible for compact and JSON shapes.
- Workspace routing, freshness defaults, banners, and telemetry remain unchanged.
- Full-read tools still use the full provider.

Performance:

- Registered OpenClaw first `search` should align with the symbol projection
  range from the spike, not the full-load range.
- Registered Hermes first `search` should align with the symbol projection range.

Test plan:

- Unit test `SymbolSearchProjection` search and hydration behavior.
- Provider test that registered `ResolveSymbolSearch` uses the symbol loader and
  does not invoke the full loader.
- Provider cache tests for revision/path changes on the symbol projection cache.
- Search tool routing tests updated to the search provider interface.
- Existing fast suite via `scripts/test.sh`.
- Scale suite before commit when touching extract/load paths.

## Architecture Quality

Affected modules:

- `Miller.Indexing` read models and loaders.
- `Miller.Server.Workspaces` provider interfaces and cache ownership.
- `Miller.Server.Tools.SearchTool`.
- Tests under `tests/Miller.Tests/Server` and `tests/Miller.Tests/Indexing`.

Caller-facing interface:

- MCP `search` signature and output remain unchanged.
- Internal provider interface splits into full-read and search-read surfaces.

Depth/locality check:

- The change is intentionally cross-module but limited to the read boundary.
- Graph, bridge, edit, trace, impact, and context behavior should not change.

Test surface:

- Tests assert behavior through provider and tool interfaces, not private cache
  fields.

Seams/adapters:

- `ISymbolSearchIndex` earns its keep because it lets `SearchTool` consume either
  the existing full index or a lean registered-workspace projection.

Rejected shortcuts:

- Single context with lazy properties.
- Full index widening before projection split.
- Persisted FTS as phase 1.

Architecture risk:

- High, because this changes read ownership. The risk is bounded by keeping the
  existing full provider intact and moving only `search` in phase 1.
