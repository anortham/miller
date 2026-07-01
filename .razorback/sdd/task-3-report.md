# Task 3 Report: Next.js Provider And Provider Selection

## Files Changed

- Created: `src/Miller.Core/Graph/NextJsBridgeProvider.cs`
- Modified: `src/Miller.Core/Graph/BridgeGraphBuilder.cs`
- Modified: `src/Miller.Indexing/BridgeProviderSelection.cs`
- Modified: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs`
- Modified: `tests/Miller.Tests/Indexing/RepositoryIndexLoaderBridgeTests.cs`
- Modified: `.razorback/sdd/task-3-report.md`

Note: the checkout already had approved dirty Task 1/2 changes in several of these files. I did not revert or reset them.

## Miller Calls Used

- `workspace status`: confirmed `/Users/murphy/source/miller` was fresh, queue empty, and sidecars current before work.
- `context` for Task 3 provider selection: confirmed the relevant seams were `IBridgeProvider`, `BridgeGraphBuilder`, `BridgeProviderSelection`, `NextRouteBridge`, and the requested test files.
- `search mode=file/content`: checked `RAZORBACK.md`, the plan path, task brief path, and plan sections; Miller found the plan in docs content and no repo-root `RAZORBACK.md`.
- `content read` on `docs/plans/2026-07-01-nextjs-bridge-trace-support.md`: confirmed Task 3 files, interfaces, approach, and acceptance criteria.
- `inspect` on `IBridgeProvider`, `BridgeGraphBuilder`, `BridgeProviderSelection`, `DotnetWebBridgeProvider`, `StructuralRouteFactAdapter`, `NextRouteBridge`, `NextRouteMatcher`, `BridgeGraph.NodeKindFor`, and target test methods: confirmed current signatures and existing behavior before edits.
- `trace mode=refs` on `BridgeProviderSelection` and `NextRouteBridge`: confirmed extracted refs were sparse and pointed to source-search fallback; no public seam change was needed.
- `impact target=BridgeGraphBuilder` and `impact target=BridgeProviderSelection`: checked planned blast radius before implementation.
- `impact` after edits: reported broad impact because the worktree already contains Task 1/2 dirty changes; focused worker tests were still the required gate.
- `workspace status` after edits: confirmed Miller re-indexed the new file and stayed fresh.
- `search/inspect` on `NextJsBridgeProvider` after edits: confirmed the new provider was indexed and implemented behind the existing provider seam.

## Tests Run

Red run after test patch:

```bash
dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter "FullyQualifiedName~BridgeGraphBuilderTests|FullyQualifiedName~RepositoryIndexLoaderBridgeTests"
```

Result: failed as expected after correcting a fixture pollution issue: 5 failed, 46 passed. Failures showed missing `nextjs` default provider, missing Next navigation edge, and configured `nextjs` not producing a graph edge.

Green run after implementation:

```bash
dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter "FullyQualifiedName~BridgeGraphBuilderTests|FullyQualifiedName~RepositoryIndexLoaderBridgeTests"
```

Result: passed, 51 passed, 0 failed, 0 skipped.

Post-refactor verification:

```bash
dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter "FullyQualifiedName~BridgeGraphBuilderTests|FullyQualifiedName~RepositoryIndexLoaderBridgeTests"
```

Result: passed, 51 passed, 0 failed, 0 skipped.

Invariant proved: provider-level Next.js graph population works from structural facts, default provider selection is additive, configured provider lists are authoritative, unknown providers do not fall back to defaults, and stable `nextjs.*` evidence counts are present.

## Acceptance Criteria

- [x] `BridgeGraphBuilder.Build(...)` with default providers can build a pure Next navigation edge from structural facts only.
- [x] `BridgeProviderSelection.ProvidersForDatabase(...)` returns both `dotnet-web` and `nextjs` when no config exists.
- [x] Explicit config with `["dotnet-web"]` does not run `nextjs`.
- [x] Explicit config with `["nextjs"]` does not run `dotnet-web`.
- [x] Unknown provider tests still show skipped-provider behavior and do not run defaults.
- [x] Capability report includes `nextjs` active/skipped status and stable evidence counts: `nextjs.routeReferences`, `nextjs.fileRoutes`, `nextjs.candidates`, `nextjs.ambiguousMatches`.
- [x] Worker-scope verification passes.

## Implementation Notes

- `NextJsBridgeProvider` is a concrete `IBridgeProvider` over structural facts plus `NextRouteBridge`.
- The provider filters to `nextjs.route_reference.v1` and `nextjs.file_route.v1`, uses `StructuralRouteFactAdapter`, and does not parse source or read SQLite.
- Observation nodes are emitted for Next route references (`TsType`) and file routes (`NextRoute`) so unmatched facts remain diagnosable.
- Default provider sets are now `[dotnet-web, nextjs]` in both `BridgeGraphBuilder` and `BridgeProviderSelection`.
- Configured provider lists remain authoritative; unknown providers still return a skipped-provider entry.

## Concerns Or Plan Mismatch

- No plan mismatch.
- One behavior to keep in mind for Task 4: `dotnet-web` can still become active on frontend route-reference/file-route evidence because Task 1 made those facts available for ASP.NET route diagnostics. I left that existing behavior intact and asserted only that `nextjs` produces its own active/skipped status and navigation candidates.
