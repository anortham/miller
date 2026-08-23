# Task 1 report: canonical full projection and disk/memory parity

## Outcome

Implemented the search-only `SearchSymbolAliasCanonicalizer` seam and routed both full-build disk writes and in-memory projections through it. Duplicate logical IDs are validated as exact aliases, the lowest ordinal path survives, canonical survivors are sorted by the existing `(path, start_line, symbol_id)` ordering, and alias-bearing inputs receive dense zero-based `DocId` values. Divergent aliases throw a bounded `InvalidDataException` naming the ID and both paths.

## Miller/API evidence

- `context` identified `SymbolSearchProjection.Build`, `SearchIndexWriter.BuildInto`, `FtsSymbolSearchIndexTests`, and existing disk/memory parity tests as the relevant pivots.
- `inspect` confirmed `SymbolLookupTables.Build` requires contiguous zero-based `DocId` values, `BuildInto` owns full-build insertion/stats, and `SearchableDocument` carries `DocId`/path for scoring parity.
- `trace` showed `SymbolSearchProjection.Build` is used by projection loaders and parity tests; `SearchIndexWriter.BuildInto` is called only by full-build `WriteAtomic`.
- `impact` after edits showed the changed projection/writer paths and the indexing parity tests as the affected verification scope.
- `workspace refresh` indexed the new canonicalizer and refreshed the worktree search/content sidecars; vector convergence still requires a resident leader and was not part of this packet.

## TDD evidence

- RED: `dotnet test --filter "FullyQualifiedName~Miller.Tests.Indexing.SymbolSearchProjectionTests.Build_DuplicateAliasIds_UsesLowestOrdinalPathAndOneLogicalDocument"` failed because the pre-change projection rejected the non-contiguous alias `DocId` at position 0.
- GREEN: the same focused test passed after routing `Build` through the canonicalizer.
- GREEN: `dotnet test --filter "FullyQualifiedName~Miller.Tests.Indexing.SearchIndexWriterTests.Write_DuplicateAliasIds_ProducesOneSearchAndFtsRow"` passed after routing `BuildInto` through the canonicalizer.
- GREEN: `dotnet test --filter "FullyQualifiedName~Miller.Tests.Indexing.FtsSymbolSearchIndexTests.Search_DuplicateAliasCorpus_HasExactDiskMemoryParity"` passed with identical document IDs, canonical paths, and scores.
- GREEN: `dotnet test --filter "FullyQualifiedName~SearchIndexWriterTests|FullyQualifiedName~SymbolSearchProjectionTests|FullyQualifiedName~FtsSymbolSearchIndexTests"` passed: 73 passed, 0 skipped, 0 failed.

## Gate invariants

- Canonicalization occurs only when duplicate logical IDs are present, so duplicate-free explicit non-dense `DocId` writer inputs retain their existing behavior.
- Alias equality covers every `IndexedSymbol` field except `DocId` and `FilePath`, including test-evidence and visibility fields.
- The disk writer canonicalizes before `search_symbols`, both FTS tables, qualification lookup, corpus statistics, and metadata are built.
- The memory projection canonicalizes before lookup tables and BM25 documents are built.
- Regions, schemas, generic readers, public interfaces, and delta logic were not changed.

## Files

- `src/Miller.Indexing/SearchSymbolAliasCanonicalizer.cs`
- `src/Miller.Indexing/SymbolSearchProjection.cs`
- `src/Miller.Indexing/SearchIndexWriter.cs`
- `tests/Miller.Tests/Indexing/SearchIndexWriterTests.cs`
- `tests/Miller.Tests/Indexing/SymbolSearchProjectionTests.cs`
- `tests/Miller.Tests/Indexing/FtsSymbolSearchIndexTests.cs`

## Self-review and judgment calls

- The canonicalizer uses ordinal path comparison and the existing reader ordering `(path, start_line, symbol_id)`; `DocId` is only the final tie-break for otherwise identical aliases and is reassigned after collapse.
- Dictionary insertion order is never exposed: the survivor list is sorted before dense IDs are assigned.
- The exception intentionally reports only the logical ID and the two conflicting paths, keeping the error actionable without dumping full symbol payloads.
- The supplied lead checkpoint `.memories/2026-08-23/165613_6fab.md` was left staged and untouched.

## Concerns

- Incremental alias re-election and cross-path deletion remain Task 2 work; this packet intentionally does not alter delta logic.
- The worktree has the pre-staged lead checkpoint plus this packet's owned changes; no unrelated files were modified.

## State

- Path: `/home/murphy/source/miller/.worktrees/search-alias-canonicalization`
- Branch: `fix/search-alias-canonicalization`
- Starting commit: `d1e0348a`
- Commit: pending serial worker commit
- Dirty state before commit: staged lead checkpoint, owned source/tests/report changes unstaged
