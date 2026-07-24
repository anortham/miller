# Takeover Phase 6 report

## Requirements

- [x] Traversal preserves predecessor, edge kind, confidence, source, centrality, and visibility.
- [x] Hop remains primary; peer ranking is relationship priority, centrality, visibility, then stable location.
- [x] File/diff degradation seeds only actionable callable/type symbols.
- [x] Exact metadata-linked tests and separately labeled filename/role candidates are supported.
- [x] MCP exposes revision-delta inputs and shares preparation/rendering with the CLI.
- [x] Deleted/unseeded paths and depth/limit completion evidence are explicit.
- [x] Bridge/web reverse impact was evaluated and not added without a visible measured gap.
- [x] Compact output is capped at 6,000 characters; deterministic JSON remains complete.
- [x] Three contracts, findings evidence, and the docs map are current.
- [x] No MCP tool, compatibility alias, or deprecation tombstone was added.

## RED/GREEN record

- SQLite parity RED compared full `ReachedNode` values and exposed ID/hop-only behavior; GREEN preserved typed
  relationship, pending-resolution, and identifier evidence with matching order.
- Exact test linkage RED found no metadata edge in repository or SQLite graphs; GREEN recognized labeled
  `test_linkage`/`test_coverage` fixtures in both.
- Filename/role RED returned no likely test for a source seed; GREEN added a separately labeled heuristic candidate.
- MCP revision-delta RED failed at the missing public parameter; GREEN produced byte-equivalent MCP/CLI output.
- Deleted-path RED lacked `deleted_paths`; GREEN separated deletion from current unseeded paths and graph seeds.
- Traversal RED lacked normal-impact evidence and limit/depth reporting; GREEN exposes deterministic evidence.
- Compact-bound RED produced 15,163 characters; GREEN is at most 6,000 and points to JSON.

## Miller/API evidence

- `workspace onboarding` proved the selected worktree index was fresh and sidecars ready.
- `context` with the Phase 6 requirements identified `ImpactTool`, graph traversal/ranking, SQLite graph,
  revision delta, and caller-facing tests as the entry points.
- `inspect` proved `ImpactTool.Impact` is the existing MCP interface; `ImpactTool.Run` is the shared normal core;
  `CliDispatch.ImpactIndexRevisionDelta` previously duplicated delta preparation; `SymbolGraphReader` loads the
  in-memory graph; `SqliteSymbolGraphIndex` is the production on-demand adapter; and `RevisionDeltaReader`
  owns journal reconstruction.
- `impact git=true` after implementation reported 53 impacted symbols and 47 likely tests, including graph,
  impact, indexing, CLI, and search-sidecar callers.

## Architecture review

- `Miller.Core` remains pure: traversal and ranking policy live there with no storage dependencies.
- SQLite query/cache details remain in `Miller.Indexing`; the adapter now matches the in-memory evidence contract.
- MCP and CLI are adapters over shared delta preparation and rendering rather than separate policy.
- The existing `impact` surface was deepened instead of adding a tool or speculative abstraction.
- Exact and heuristic test evidence cannot be mistaken for each other because method, source, tier, and confidence
  are serialized per result.
- The bridge decision preserves the current `trace`/`patterns` ownership and avoids unsupported impact edges.

## Verification

- Phase 6 focused and compatibility filter: 235 passed, 0 failed.
- Fast suite: 4,727 passed, 2 environment skips.
- Scale suite: 87 passed, 0 failed against the real pinned extractor.
- Plugin contracts: 48 passed.
- Agent-efficiency Python harness: 99 passed.
- Retrieval evaluator: 95 passed.
- Native AOT `osx-arm64` publish: passed.
- `dotnet build Miller.slnx -c Release --no-restore`: succeeded, 0 warnings, 0 errors.
- `git diff --check`: clean.

## Claude review disposition

- Six findings were accepted and fixed with red/green coverage: cross-language filename-role conventions,
  normal-mode path evidence, dangling linkage parity, bounded frontier probing, edge-kind tie-breaking, and
  explicit heuristic candidate counts/truncation.
- The follow-up review accepted four more fixes: normal zero-seed traversals now report `not_run/no_seeds`,
  SQLite string tie-breaks are ordinal across cultures, and both stale traversal contract examples are current.
- The full gate exposed stale synthetic bridge and large-DB v1 schemas after Phase 6 added visibility and
  confidence reads. Those fixtures now match the pinned extractor columns.
- The scale latency tripwire failed once under suite contention, passed in isolation on both Phase 5 and the
  repaired Phase 6 tree, then passed in the full 87-test rerun.

## Changed files

- Graph/result policy: `GraphTraversal.cs`, `SymbolGraph.cs`, `ImpactRanker.cs`.
- Indexing adapters/projections: `IndexedSymbol.cs`, `MillerRepositoryIndex.cs`, `RevisionDeltaReader.cs`,
  `SqliteSymbolGraphIndex.cs`, `SqliteSymbolReader.cs`, `SymbolGraphReader.cs`, `TestLinkageReader.cs`.
- Existing public surface: `ImpactTool.cs` and the impact revision-delta path in `CliDispatch.cs`.
- Focused graph, indexing, MCP, and CLI tests.
- Impact contracts, Phase 6 findings, docs map, checkpoint, and this report.

## Unresolved risks

- The current artifact contains zero `test_linkage`/`test_coverage` metadata rows, so exact linkage is
  fixture-proven but dormant until the extractor emits labeled evidence.
- Exhaustion remains scoped to current indexed edges; dynamic dispatch, reflection, configuration, generated
  code, and unresolved extractor edges remain outside the claim.
- Branch-wide final gates and the nine-tool broad review remain later-plan responsibilities.
