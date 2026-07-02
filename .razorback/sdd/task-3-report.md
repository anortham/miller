# Task 3 — `context`: rank neighbours by relevance, not symbol id

## What I implemented

`ContextTool.BuildCandidates` step 3 previously appended reached (hop>0) neighbours in Reach's native
`(hop asc, id asc)` order. Symbol id is arbitrary, so the 12 rendered neighbours were effectively random —
the pre-release audit saw unrelated symbols (`UnifiedDiff.Write`, dashboard `TableExists`) crowd out
relevant ones on a sidecar-convergence query.

Change: neighbours are now ordered `(hop asc, relevance desc, id asc)`.

- New private, pure, deterministic `NeighbourRelevanceScorer` struct (built once per bundle):
  - **+2** per query/seed identifier token that appears (case-insensitive substring) in the neighbour's `Name`.
    Tokens = `ExtractIdentifierTokens(query)` ∪ each seed's whole `Name`, deduped case-insensitively.
  - **+1** if the neighbour's `FilePath` equals any seed's `FilePath`.
  - **+1** if the neighbour's `FilePath` directory equals any seed's directory.
  - Same-file neighbours therefore score above same-directory-only ones (they earn both points), which is
    strictly monotonic and matches the spec.
- Step 3 scores each reached neighbour once (preserving Reach's `(hop, id asc)` order), then does a **stable**
  `OrderBy(Hop).ThenByDescending(Score)`. Because LINQ `OrderBy`/`ThenBy` is a stable sort and the input is
  already id-asc, `id asc` remains the final tiebreak within an equal `(hop, score)` — no explicit id compare
  needed. Hop is the primary key, so hop-1 always precedes hop-2 regardless of score.
- Seeds (hop 0) unchanged; `candidatesExamined` semantics unchanged (still `candidates.Count` = seeds + reached);
  packer and renderers untouched (they preserve caller order).

## Miller calls used + what each confirmed

- `inspect ReachedNode depth=overview` — confirmed `record ReachedNode(string Id, int Hop)`; `Id` is a string and
  Reach yields min-hop per node. Drove the stable-sort-preserves-id-asc approach.
- `inspect IndexedSymbol depth=overview` — confirmed the record shape: `Name`, `FilePath`, `SymbolId` are
  non-nullable `string` (so no `?? ""` guard needed on `Name`, avoiding a nullable warning-as-error).
- `ToolSearch` (miller tool schemas) + direct `Read` of `ContextTool.cs` / `ContextToolTests.cs` — confirmed
  `BuildCandidates` :305 structure, the `Candidate` record struct, `ExtractIdentifierTokens` (regex
  `[A-Za-z_][A-Za-z0-9_]*`, no camelCase split, drops <2-char tokens), and existing fixture conventions
  (`IndexedSymbol` ctor arg order, `GraphEdge`, `MillerRepositoryIndex.Build`).

## API-shape evidence

- `IndexedSymbol(int DocId, string SymbolId, string Name, string Signature, string Kind, string Language,
  string FilePath, int StartLine, int EndLine, string? ParentId, bool IsTest)` — from existing fixtures.
- `graph.Reach(seedOrder, maxHops, ReachCap, Direction.Both)` returns `IReadOnlyList<ReachedNode>` in
  `(hop asc, id asc)`.
- `ExtractIdentifierTokens(string?)` yields identifier-like tokens; empty/whitespace → none.

## Verification

- **Invariant proven:** at equal hop, a neighbour with same-file and/or query/seed name overlap ranks ahead of an
  unrelated neighbour that has a *smaller* symbol id (the audit case); and hop strictly dominates relevance — a
  hop-2 relevant neighbour never leapfrogs a hop-1 unrelated one.
- **Scope (assigned worker-red-green):** `dotnet test tests/Miller.Tests --filter "FullyQualifiedName~ContextToolTests"`
- **Result:** Passed! Failed: 0, Passed: 30, Skipped: 0 (28 existing unmodified + 2 new).
- **Red first:** new tests initially failed — current code returned unrelated `Helper` (lowest id) first
  (`Expected: "OrderRepo" Actual: "Helper"`), reproducing the audit failure. Green after the scorer landed.
- **Build:** 0 warnings / 0 errors (warnings-are-errors; test build compiled Miller.Server + Miller.Tests clean).
- **Timestamp:** 2026-07-02.

## Files changed (owned)

- `src/Miller.Server/Tools/ContextTool.cs` — step 3 reorder + `NeighbourRelevanceScorer`.
- `tests/Miller.Tests/Server/ContextToolTests.cs` — `BuildRelevanceFixture`, `BundleNames`, and two tests
  (`Run_Neighbours_RelevanceBeatsLowerIdUnrelated_AtEqualHop`, `Run_Neighbours_HopStillDominatesRelevance`).

## Judgment calls

- Used stable LINQ `OrderBy/ThenByDescending` instead of an explicit id comparator so `id asc` is inherited from
  Reach's existing order — matches the spec's "id asc" final tiebreak exactly and avoids re-deriving id ordering.
- `DirectoryOf` splits on both `/` and `\` (julie normalizes to `/`, but robustness is free).
- Seed name added as a whole token per spec even though `ExtractIdentifierTokens` would drop <2-char strings;
  seed names are realistically longer, and the spec explicitly says "seed names count as whole tokens too".
- The +1 file and +1 directory points are independent/additive per spec, so same-file = +2 (file+dir), which
  correctly orders same-file above same-directory-only.

## Self-review findings

- Confirmed existing order-sensitive tests still pass unmodified: `Run_Compact_GroupsNeighboursByFileAfterSeeds`
  (per-file `Assert.Contains` blocks are group-order-independent) and `Run_Compact_CapsNeighboursAndNotesOmission`
  (all 15 wide-fixture neighbours share the same same-directory score, so stable id-asc order is preserved → same
  12 shown / 3 omitted).
- No nullable warnings: `Name`/`FilePath` are non-nullable; `PathSeparators` is a single static readonly array
  (no per-call allocation); score precomputed once per neighbour (not recomputed inside the sort comparer).

## Concerns

- Sibling workers (esp. Task 2, workspace list) were mid-edit in `WorkspaceRender.cs`/`WorkspaceTool.cs` during my
  runs, transiently breaking the shared `Miller.Server` build; I retried past the churn per the file-ownership
  rules. My final green run compiled the full server + tests clean. The lead should re-run the fast suite after all
  workers land to confirm the combined tree still builds 0/0.
