# Search Symbol Alias Canonicalization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Make disk and in-memory symbol search converge deterministically when a visible store view contains multiple file paths for the same logical `symbol_id`.

**Architecture:** Add one search-specific canonicalization seam shared by `SymbolSearchProjection` and `SearchIndexWriter`; it selects one stable representative per logical ID without changing the family-store, extractor, vector, or `ISymbolLookupIndex` contracts. Extend the store-delta writer to re-elect canonical aliases from all current candidates for affected IDs while preserving stable document IDs. Region rows remain path-specific because live Zod evidence contains no duplicate region IDs.

**Tech Stack:** .NET 10, C#, SQLite/FTS5, xUnit, Miller family-store read sessions.

**Architecture Quality:** Medium risk. The caller-facing interface remains one search result per logical `symbol_id`; complexity stays inside the search projection/writer boundary. The main risk is history-dependent canonical selection during incremental updates, so the test surface compares full rebuilds, delta convergence, and in-memory search through existing public interfaces.

## Global Constraints

- A family-store view may legally contain the same `symbol_id` under multiple visible file versions and paths.
- Exact aliases are rows with the same logical ID and identical search-relevant fields other than `DocId` and `FilePath`.
- Canonical selection is deterministic: lowest ordinal `FilePath`, then the existing deterministic symbol ordering as a total tie-break.
- A genuinely divergent same-ID pair is not an alias. Reject it with a bounded actionable `InvalidDataException` naming the ID and conflicting paths so health reports corruption instead of silently hiding data.
- Disk and in-memory lexical search must consume the same canonical symbol sequence and remain ranking/output equivalent.
- Incremental convergence must handle alias addition, noncanonical deletion, canonical deletion, canonical content change, and canonical re-election without duplicate rows or lost logical symbols.
- Canonical re-election preserves the existing `doc_id` for an unchanged logical ID. New logical IDs use the existing stable allocation policy.
- Keep `search_symbols.symbol_id` as the primary key. Do not bump `SearchIndexWriter.SchemaVersion`.
- Do not change `julie-extractors`, family-store schemas, vector schemas, `ISymbolLookupIndex`, or public MCP/CLI contracts.
- Do not canonicalize generic `SqliteSymbolReader.Read`/`ReadSession` results; path-sensitive non-search consumers retain every visible alias.
- Do not alter region indexing unless a failing fixture proves duplicate `source_region_id` values. Live Zod evidence is zero duplicate region IDs.
- Follow `razorback:test-driven-development`: every production behavior starts with a focused failing test and a witnessed expected failure.
- Tests contain no comments. Production comments are limited to non-obvious alias invariants that names and types cannot express.

## Architecture Quality

- **Affected modules:** `SymbolSearchProjection`, `SearchIndexWriter`, bounded symbol reads in `SqliteSymbolReader`, and store-delta orchestration in `SymbolSearchSidecar`.
- **Caller-facing interface:** unchanged `ISymbolLookupIndex`; callers still receive one `IndexedSymbol` for a logical ID.
- **Depth/locality check:** one internal canonicalization module owns equivalence, ordering, and dense-document projection; callers do not learn alias rules.
- **Test surface:** `SymbolSearchProjection.Build`, `SearchIndexWriter.Write`, and `SymbolSearchSidecar.EnsureStoreCurrent`.
- **Seams/adapters:** add an internal search-only canonicalizer; add one bounded `SqliteSymbolReader.ReadForSymbolIds(IWorkspaceReadSession, IReadOnlyCollection<string>)` adapter for delta re-election.
- **Rejected shortcuts:** ignoring duplicate rows, catching the exception without repairing output, generic-reader deduplication, composite `(symbol_id,path)` search identity, and extractor rekeying.
- **Architecture risk:** medium because full and incremental histories must choose byte-equivalent logical search corpora.

## Verification Strategy

**Project source of truth:** `AGENTS.md` testing/build sections and `tests/Miller.Tests/Miller.Tests.csproj` scale-trait conventions.

**Worker red/green scope:** Run only each new test by fully qualified name while cycling RED to GREEN. Then run the focused owning classes with `dotnet test --filter "FullyQualifiedName~SearchIndexWriterTests|FullyQualifiedName~SymbolSearchProjectionTests|FullyQualifiedName~FtsSymbolSearchIndexTests"` for Task 1 and `dotnet test --filter "FullyQualifiedName~FamilyStoreReadSessionTests"` for Task 2.

**Worker ceiling:** The focused classes above. Workers do not run `scripts/test.sh`, Scale, Release builds, or the Zod replay.

**Worker gate invariant:** Task 1 proves exact aliases collapse deterministically, divergent collisions fail honestly, document ordinals are valid, and disk/memory results are identical. Task 2 proves the same canonical result is reached through full build and every alias delta transition while stable `doc_id` values survive re-election.

**Lead affected-change scope:** `dotnet test --filter "FullyQualifiedName~SearchIndexWriterTests|FullyQualifiedName~SymbolSearchProjectionTests|FullyQualifiedName~FtsSymbolSearchIndexTests|FullyQualifiedName~FamilyStoreReadSessionTests|FullyQualifiedName~SymbolSearchSidecarTests"` once after both tasks land.

**Branch gate:** `dotnet build Miller.slnx -c Release`, then `scripts/test.sh`, then `scripts/test.sh scale`, each once on the final source tree.

**Security scope:** none declared.

**Replay/metric evidence:** Hard gates: a Release binary refreshes `/home/murphy/source/zod` without a duplicate-key failure; the Zod search sidecar stamp is current; `COUNT(search_symbols)` equals `COUNT(DISTINCT symbol_id)` in the visible store; lexical `ZodObject` search succeeds; all three CT workspaces remain disabled/stopped. Report-only: refresh duration, duplicate alias count, vector `leader_required` status, parse diagnostics, and capability-gap warnings.

**Escalation triggers:** Any public API/schema change, a region-ID collision, a divergent live collision, disk/memory ranking drift, a delta requiring an unbounded whole-view read, or changes outside the owned files require lead redesign before implementation continues.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp. Reuse a passing entry only when HEAD and the required scope are unchanged.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Canonical full projection and disk/memory parity | None - serial | Create `src/Miller.Indexing/SearchSymbolAliasCanonicalizer.cs`; modify `src/Miller.Indexing/SymbolSearchProjection.cs`, `src/Miller.Indexing/SearchIndexWriter.cs`; test `tests/Miller.Tests/Indexing/SearchIndexWriterTests.cs`, `tests/Miller.Tests/Indexing/SymbolSearchProjectionTests.cs`, `tests/Miller.Tests/Indexing/FtsSymbolSearchIndexTests.cs` | Yes | Task 2 consumes the canonicalizer and modifies `SearchIndexWriter.cs`. |
| Task 2: Alias-aware incremental store convergence | None - serial | Modify `src/Miller.Indexing/SqliteSymbolReader.cs`, `src/Miller.Indexing/SearchIndexWriter.cs`, `src/Miller.Indexing/SymbolSearchSidecar.cs`; test `tests/Miller.Tests/Indexing/FamilyStoreReadSessionTests.cs`, `tests/Miller.Tests/Indexing/SymbolSearchSidecarTests.cs` | Yes | Requires Task 1's canonical equivalence and ordering contract. |

### Task 1: Canonical full projection and disk/memory parity

**Files:**
- Create: `src/Miller.Indexing/SearchSymbolAliasCanonicalizer.cs`
- Modify: `src/Miller.Indexing/SymbolSearchProjection.cs:25-38`
- Modify: `src/Miller.Indexing/SearchIndexWriter.cs:94-218`, `347-414`, `627-653`
- Test: `tests/Miller.Tests/Indexing/SearchIndexWriterTests.cs`
- Test: `tests/Miller.Tests/Indexing/SymbolSearchProjectionTests.cs`
- Test: `tests/Miller.Tests/Indexing/FtsSymbolSearchIndexTests.cs`

**Interfaces:**
- Consumes: deterministic `IndexedSymbol` rows from `SqliteSymbolReader`; current `SymbolSearchProjection.Build(IReadOnlyList<IndexedSymbol>)`; current `SearchIndexWriter.Write`/`WriteStoreView` entry points.
- Produces: internal `SearchSymbolAliasCanonicalizer.Canonicalize(IReadOnlyList<IndexedSymbol>)` returning a deterministic, dense, one-row-per-ID search sequence used by both disk and memory backends.

**Contract inputs:** Family-store symbol key `(version_id, symbol_id)`; search key `symbol_id`; exact alias definition and canonical ordering from Global Constraints.

**File ownership:** Create `src/Miller.Indexing/SearchSymbolAliasCanonicalizer.cs`; modify `src/Miller.Indexing/SymbolSearchProjection.cs`, `src/Miller.Indexing/SearchIndexWriter.cs`; test the three named indexing test files.

**Serialization required:** Yes.

**Dependency reason:** Task 2 consumes the canonicalizer and modifies `SearchIndexWriter.cs`.

**What to build:** Add a search-only canonicalizer that groups by logical ID, validates that duplicates are exact aliases, selects the lowest ordinal path, sorts the survivor set deterministically, and reassigns dense 0-based `DocId` values only when canonicalization changes the input. Route both `SymbolSearchProjection.Build` and disk full-build input through it before building lookup tables, FTS rows, corpus statistics, and metadata.

**Approach:** Preserve existing explicit `DocId` behavior for duplicate-free direct writer inputs. For alias-bearing inputs, generate the same dense survivor sequence for memory and disk so BM25 document counts, average lengths, tie-breaking, file paths, and lookup tables remain identical. Keep qualification lookup one row per ID. Treat divergent rows as corruption with an actionable bounded exception rather than guessing.

**Acceptance criteria:**
- [x] `Build_DuplicateAliasIds_UsesLowestOrdinalPathAndOneLogicalDocument` fails before production changes, then passes for shuffled input orders.
- [x] `Build_DivergentDuplicateId_ThrowsActionableInvalidDataException` proves the error names the logical ID and both paths.
- [x] `Write_DuplicateAliasIds_ProducesOneSearchAndFtsRow` proves `search_symbols`, word FTS, trigram FTS, `meta.doc_count`, and `avgdl` count the logical symbol once.
- [x] `Search_DuplicateAliasCorpus_HasExactDiskMemoryParity` proves identical IDs, canonical paths, result order, and scores.
- [x] Duplicate-free writer tests, including explicit non-dense `DocId` persistence, remain unchanged and green.
- [x] Worker-scope verification passes and the worker commits only owned files with `serial-worker-commit`.

### Task 2: Alias-aware incremental store convergence

**Files:**
- Modify: `src/Miller.Indexing/SqliteSymbolReader.cs:95-168`
- Modify: `src/Miller.Indexing/SearchIndexWriter.cs:283-345`, `507-617`, `627-686`
- Modify: `src/Miller.Indexing/SymbolSearchSidecar.cs:314-393`
- Test: `tests/Miller.Tests/Indexing/FamilyStoreReadSessionTests.cs:494-650`
- Test: `tests/Miller.Tests/Indexing/SymbolSearchSidecarTests.cs`

**Interfaces:**
- Consumes: Task 1's `SearchSymbolAliasCanonicalizer`; `RevisionDeltaReader` changed/deleted paths; current sidecar rows and `StoreSidecarStamp`.
- Produces: `SqliteSymbolReader.ReadForSymbolIds(IWorkspaceReadSession, IReadOnlyCollection<string>)` returning every current visible candidate for bounded affected IDs; delta convergence that re-elects exactly one canonical row per affected ID and preserves stable `doc_id` values.

**Contract inputs:** Affected IDs are the union of old canonical sidecar rows under changed/deleted paths and current store rows under changed paths. Candidate re-election reads only those IDs from the pinned session, never the whole view.

**File ownership:** Modify the three named production files; test `FamilyStoreReadSessionTests.cs` and `SymbolSearchSidecarTests.cs`.

**Serialization required:** Yes.

**Dependency reason:** Requires Task 1's canonical equivalence and ordering contract.

**What to build:** Replace path-only symbol replacement with affected-ID replacement. Read old canonical rows before deletion, collect old and new IDs, fetch every current candidate for those IDs, canonicalize them, delete sidecar rows by affected ID, and insert the elected rows. Reuse the prior `doc_id` whenever the logical ID survives; allocate from the existing reusable/monotonic policy only for new IDs.

**Approach:** Alias addition may replace the resident row when the new path sorts earlier. Noncanonical deletion is a no-op. Canonical deletion re-elects a surviving alias outside the changed paths. Canonical content change handles removed old IDs and inserted new IDs in one transaction. Keep source-region deletion/insertion path-based; Zod proves region IDs are unique per path. Preserve transaction rollback, store stamping, and `TryApplyStoreDelta` fallback behavior.

**Acceptance criteria:**
- [x] `SearchSidecarAliasDelta_AddingLaterAliasKeepsCanonicalRowAndDocId` passes without full rebuild or duplicate-key failure.
- [x] `SearchSidecarAliasDelta_AddingEarlierAliasReelectsCanonicalPathAndPreservesDocId` passes in place.
- [x] `SearchSidecarAliasDelta_DeletingNoncanonicalAliasIsNoOp` preserves the sentinel table and sidecar stamp progression.
- [x] `SearchSidecarAliasDelta_DeletingCanonicalAliasReelectsSurvivorAndPreservesDocId` keeps the logical symbol searchable.
- [x] `SearchSidecarAliasDelta_ChangingCanonicalContentReelectsOldIdAndAddsNewIds` handles both identity sets atomically.
- [x] Full rebuild and every delta history produce the same `search_symbols` rows, canonical paths, document IDs, FTS results, and store stamp.
- [x] Region-enabled focused coverage proves alias symbol re-election neither deletes surviving-path regions nor duplicates region rows.
- [x] Existing non-alias incremental tests retain their sentinel tables, proving no unnecessary full rebuild.
- [x] Worker-scope verification passes and the worker commits only owned files with `serial-worker-commit`.

## Lead Integration and Dogfood Gate

1. Review each worker commit with Miller `inspect`, `trace`, and `impact`; reject generic-reader deduplication, schema changes, or unbounded candidate reads.
2. Run the lead affected-change scope once after both tasks are integrated.
3. Run the Release build, fast suite, and Scale suite once on the final source tree.
4. From `/home/murphy/source/zod`, run:

   ```bash
   /home/murphy/source/miller/.worktrees/search-alias-canonicalization/src/Miller.Server/bin/Release/net10.0/miller workspace refresh --json
   /home/murphy/source/miller/.worktrees/search-alias-canonicalization/src/Miller.Server/bin/Release/net10.0/miller workspace health --json
   /home/murphy/source/miller/.worktrees/search-alias-canonicalization/src/Miller.Server/bin/Release/net10.0/miller search ZodObject --mode symbol --arm lexical --json
   ```

5. Query the pinned Zod store and search sidecar read-only: visible `COUNT(DISTINCT symbol_id)` must equal `COUNT(search_symbols)`, the search store stamp must match revision, and duplicate logical IDs must not appear twice in FTS results.
6. Confirm Zod, more-itertools, and julie-semantic-sidecar CT status remains disabled/stopped. Do not start CT for this search-only repair.
7. Record a Goldfish checkpoint before the first implementation commit and before final integration commit.

## External Review Reconciliation

- Fresh Claude CLI review chose one canonical search row per logical ID and rejected composite search identity and extractor rekeying.
- Accepted: shared disk/memory canonicalization, deterministic path election, bounded by-ID delta re-election, stable `doc_id`, exact parity tests, and no schema bump.
- Refined: canonicalization stays search-specific instead of changing generic `SqliteSymbolReader.Read`/`ReadSession` results; only the new bounded by-ID adapter is added there.
- Audited: `LaggingSidecarSymbolLookup` builds its dictionary per file, while observed aliases are cross-file; no change is planned without a failing same-file fixture.
- Audited: Zod has zero duplicate source-region IDs; region rows remain path-specific.
- Policy note: no external-model policy declared — bounded architecture context sent to Anthropic read-only.
