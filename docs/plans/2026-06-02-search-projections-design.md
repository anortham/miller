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

Initial implementation (decisions locked 2026-06-02):

- In-memory BM25 text index behind `IContentSearchIndex`. Its own
  `ContentSearchIndex` in `Miller.Core.Search` (same `CodeTokenizer` as symbol
  search, K1=1.2 / B=0.75), NOT a reuse of `MillerSearchIndex` — that index
  scores only `Name + Signature` with an exact-name boost and is symbol-coupled.
- Scope is **docs-like only**: keep `files.status='indexed'` rows whose path or
  language is prose/markup/config (the spike's `IsDocsLike`: `.md/.mdx/.markdown/
  .txt/.rst/.adoc/.org` and config like `.json/.yaml/.yml/.toml`). Source files
  are already covered by symbol search; content search complements it for files
  that may carry zero symbols.
- Build from root-relative files re-sourced from disk and BLAKE3-verified against
  `files.content_hash`, reusing `ExtractFileHashReader` + `ContentHasher` and the
  existing under-root path-safety. Skip — never error — files that are
  out-of-scope, non-`indexed`, oversize (`> 1 MiB`), missing, hash-mismatched
  (stale), non-UTF-8, or unreadable.
- Each hit returns `path + best line + snippet window` (the best-scoring line by
  query-term hits, plus ±2 lines of context), not a fake symbol.
- Do not make SQLite FTS5 the default. FTS5 trigram was measured larger and
  slower for the docs workload; persisted FTS adds invalidation/lifecycle
  complexity before the product shape is proven.

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

CLI one-shot reads do not have an `IndexHolder` cache to amortize the full load.
The 2026-06-05 large-repo rerun extended the same projection boundary to the
CLI: `miller search` and summary `miller inspect` now open a fresh lazy
`search.db` sidecar when available and fall back to `SymbolSearchProjectionLoader`;
full `inspect`, `context`, `impact`, and `trace` still use `RepositoryIndexLoader`.
The disk sidecar reader must not eagerly materialize every `search_symbols` row
on open; it fetches FTS candidates and lookup rows on demand.

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
- Status: implemented.

Phase 3 shape (locked):

- `SearchTool` gains `SearchToolMode.Content` (`mode=content`, alias `docs`).
  `mode=content` routes to the content provider and renders content hits; every
  other mode (`auto`/`symbol`/`text`/`file`) is untouched and byte-compatible.
  `exclude_tests` is a no-op for content.
- New `IWorkspaceContentSearchProvider.ResolveContentSearch(workspaceId,
  ensureFresh)` returning `WorkspaceContentSearchContext`. `WorkspaceIndexProvider`
  implements it with a third, separate revision/path-keyed cache and single-flight
  load, reusing `ResolveRegisteredState` and the generic eviction. Current
  workspace builds and caches the content projection lazily on first content
  query (no bootstrap change). Registered `workspace_id` parity is supported.
- New components: `Miller.Core.Search.ContentSearchIndex` (+ `ContentDocument`,
  `ContentSearchHit`); `Miller.Indexing` `IContentSearchIndex`,
  `ContentSearchProjection`, `ContentSearchProjectionLoader`;
  `Miller.Server.Workspaces` `IWorkspaceContentSearchProvider`,
  `WorkspaceContentSearchContext`.

Phase 3 acceptance gates:

- `mode=content` returns content hits (path + line + snippet), never fake symbols.
- Only docs-like, `indexed`, in-size, freshness-verified files are indexed; bad
  files are skipped, not errored; rooted or root-escaping manifest paths are
  never read from disk.
- Registered-workspace `mode=content` calls neither `RepositoryIndexLoader.Load`
  nor the symbol-search loader.
- The content projection cache invalidates on revision/path change.
- Symbol modes (`auto`/`symbol`/`text`/`file`) stay byte-compatible.

Phase 3 test plan:

- Core: `ContentSearchIndex` ranking, best-line/snippet, window bounds,
  empty/whitespace query, tie-break determinism.
- Indexing: loader docs-scope filter, size cap, status skip, freshness skips
  (stale/missing/non-UTF-8/IO), under-root path-safety.
- Provider: registered `ResolveContentSearch` uses the content loader only (no
  full or symbol load), caches by revision/path, reloads on path change.
- Tool: `mode=content` compact + JSON shapes; symbol-mode byte-compat.
- Scale: large-repo content build cost.

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
- CLI `miller search` and summary `miller inspect` do not call
  `RepositoryIndexLoader.Load`; full CLI read tools still may.
- `FtsSymbolSearchIndex.Open` does not eagerly read every resident symbol row.

Performance:

- Registered OpenClaw first `search` should align with the symbol projection
  range from the spike, not the full-load range.
- Registered Hermes first `search` should align with the symbol projection range.
- One-shot OpenClaw CLI `search` / summary `inspect` should align with lazy
  candidate/lookup SQL cost, not full graph/bridge load or eager sidecar
  snapshot cost.

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
