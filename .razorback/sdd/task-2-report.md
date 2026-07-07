# Task 2 Report — Indexing reader + v4 fixture extension

**Status:** DONE
**Branch:** `feat/dead-code-candidates`
**Commit mode:** serial-worker-commit (owned files only; not pushed)

## What was built

`DeadCodeCandidateReader` (Miller.Indexing) — the read-side of `miller references candidates`. It opens the
artifact read-only, runs `JulieSchemaGate.Verify`, then does the ordered pipeline from the brief:

1. **Required-table validation** (after the metadata gate): `sqlite_master` check for
   `identifier_resolutions`, `pending_resolutions`, `pending_relationships`; any missing ⇒
   `IncompatibleExtractException` (→ CLI exit 3). One reader test per table.
2. **Closure inputs over ALL symbols**: `parent_symbol_id` map + `is_test` set (one pass), plus the DISTINCT
   `structural_facts.containing_symbol_id` set and DISTINCT `symbol_annotations.symbol_id` set.
3. **Candidate rows** (`kind IN DeadCodeCandidates.CandidateKinds`): read tuples first (reader closed), then per
   candidate compute the ancestor closures (`IsTestSelfOrAncestor`, `HasStructuralFactSelfOrAncestor`, cycle-safe
   walk) and the four inbound counts via **per-symbol indexed subqueries** (never materializing all identifiers).
   Inside-S test is NULL-safe (`COALESCE(... , 0) = 0`) so a NULL span / NULL containing-symbol reads as "outside".
   - `NameMatchesOutside`, `ResolvedInbound` (identifier_resolutions JOIN identifiers, outside S),
     `PendingResolvedInbound` (pending_resolutions JOIN pending_relationships, outside S — independent of
     identifier_resolutions), `CallsInbound` (relationships to S from ≠ S). `LiteralMatch = null`.
4. **Coverage universe** = UNION of `symbols.language` and `files.language`, LEFT-joined to identifier/resolved
   counts. A language with symbols but zero identifiers (css/html) is emitted with `IdentifierCount = 0` so Core's
   `low_evidence_language` fires. Proven by `Read_LanguageWithSymbolsButZeroIdentifiers_...`.
5. `DeadCodeCandidates.Evaluate(rows, coverage)`.
6. **Literal scan LAST**, only over `result.NeedsLiteralScan` survivors; skipped entirely when empty. Reads each
   literal-bearing file at most once (path-keyed cache), mirrors `SearchIndexWriter.ReadVerifiedFileText`
   (blake3 + content_bytes freshness guard); stale/missing ⇒ `FilesSkippedStale++` and NO suppression. Slices with
   `SourceTextDecoder.SliceUtf8ByteSpan`, Ordinal substring match ⇒ `ApplyLiteralScan`.
7. **Artifact block**: `artifact_id`, `MAX(revision_id)`, `reference_resolution_status` (fallback `"unknown"`),
   `reference_resolution_version` (null when absent).

### Reader API produced (Task 3 renders exactly this — stable)
- `DeadCodeCandidateReader.Read(string symbolsDbPath, string workspaceRoot) -> DeadCodeCandidateReport`
- `DeadCodeCandidateReport(DeadCodeResult Result, IReadOnlyList<LanguageCoverageRow> LanguageCoverage,
  DeadCodeLiteralScan LiteralScan, DeadCodeArtifact Artifact)`
- `DeadCodeLiteralScan(int FilesScanned, int FilesSkippedStale)`
- `DeadCodeArtifact(string? ArtifactId, long? Revision, string ReferenceResolutionStatus,
  string? ReferenceResolutionVersion)`

`Read` returns the FINAL report (two-phase literal scan already applied). Task 3 only renders.

## Fixture builder method names + signatures added (Task 3 needs these)

All are **public instance methods on `JulieDbFixture`** that mutate the already-created DB over a fresh
`ReadWrite`, `Pooling=false`, `ForeignKeys=false` connection (same precedent as `ReplaceFileBytesAndRefreshHash`;
FKs relaxed like `Create`, but the `identifier_resolutions` CHECK is still enforced):

```csharp
public void AddIdentifierResolution(
    string identifierId, string? targetSymbolId, string outcome = "resolved",
    int tier = 1, double confidence = 1.0, string method = "exact", int candidates = 1,
    long resolvedAtRevision = 1);

public void AddPendingRelationship(
    string pendingRelationshipId, string fromSymbolId, string filePath,
    string? callerScopeSymbolId = null, int? startByte = null, int? endByte = null,
    string kind = "call", string targetDisplayName = "Target", string targetTerminalName = "Target",
    int startLine = 1, double confidence = 1.0);

public void AddPendingResolution(
    string pendingRelationshipId, string targetSymbolId,
    int tier = 1, double confidence = 1.0, string method = "exact", long resolvedAtRevision = 1);

public void AddStructuralFact(
    string structuralFactId, string? containingSymbolId, string path,
    string language = "csharp", string patternId = "custom.pattern.v1",
    string captureName = "attribute", string nodeKind = "attribute");

public void AddSymbolAnnotation(
    string annotationId, string symbolId, string annotation = "Obsolete", string annotationKey = "obsolete");
```

`JulieDbFixture.Create(...)` gained two trailing optional params (default-included for v4 fidelity; pass `null` to
omit and exercise the reader's `unknown`/null fallbacks):
```csharp
string? referenceResolutionStatus = "partial",
string? referenceResolutionVersion = "1"
```

### Fixture DDL changes
- `pending_relationships` upgraded to the pinned v4 shape: added the three FKs (`from_symbol_id`,
  `caller_scope_symbol_id`, `file_id`) and the four indexes (`idx_pending_terminal`, `idx_pending_file`,
  `idx_pending_from`, `idx_pending_caller_scope`).
- New tables `identifier_resolutions` (with the `CHECK ((outcome='resolved') = (target_symbol_id IS NOT NULL))`)
  and `pending_resolutions`, plus indexes `idx_identifier_resolutions_target`, `idx_pending_resolutions_target`.
- New v4 metadata keys `reference_resolution_status` / `reference_resolution_version`.

## Verification
- **worker-red-green** — RED first (guard tests failed on missing tables/indexes/CHECK; reader tests failed via
  `NotImplementedException`), then GREEN:
  `dotnet test ... --filter "FullyQualifiedName~DeadCodeCandidateReader"` → **18 passed**;
  `... ~JulieDbFixtureCurrentSchema` → **19 passed** (14 pre-existing + 5 new v4 guards).
- **worker-ceiling** — `scripts/test.sh` (fast suite, `Category!=Scale`): **2943 passed, 0 failed** (17s wall,
  under the 30s ceiling). Invariant: the fast suite stays green and pure (no julie subprocess). The
  `ScaleTraitConventionTests` guard passed — the new fixture-based tests correctly carry NO `[Trait("Category",
  "Scale")]` and do not spawn julie-extract.
- **Build** — Release with `TreatWarningsAsErrors`: **0 warnings / 0 errors**.
- Known-flaky `IndexerServiceScanTests.StartAsync_WhenEnabledLeaderAndSidecarBuildFails_StillMarksRegistryScanned`
  did not fail in this run.

## Seam files read directly (Miller search/inspect down) + what they confirmed
- `src/Miller.Indexing/ReferenceExportReader.cs` — `SqliteReadOnlyAccess.Open` + `JulieSchemaGate.Verify` first,
  `artifact_id` metadata read, `MAX(revision_id) FROM extraction_revisions`. Mirrored for the artifact block.
- `src/Miller.Indexing/JulieSchemaGate.cs` — reuse of `IncompatibleExtractException` and its missing-table message
  style; confirmed the gate checks metadata VALUES only, not table presence (hence the required-table step).
- `src/Miller.Indexing/SqliteSourceRegionReader.cs` + `SourceRegionRow.cs` — the `source_regions`⋈`files`
  (content_hash/content_bytes) shape and `kind='string_literal'` filter.
- `src/Miller.Indexing/SearchIndexWriter.cs` (`ReadVerifiedFileText`, ~691–722) — the exact freshness guard
  (`ResolveUnderRoot` → `File.ReadAllBytes` → length + `Blake3Hex`/`NormalizeHash` match → `TryDecode`) mirrored
  verbatim for the literal scan.
- `src/Miller.Indexing/SourceTextDecoder.cs` — `SliceUtf8ByteSpan` returns `null` on an out-of-range/empty span.
- `src/Miller.Indexing/IncompatibleExtractException.cs` — public sealed, `(string)` and `(string, Exception)` ctors.
- `tests/Miller.Tests/Indexing/PatternFactsReaderTests.cs` — the `Create(...)` + post-creation `Exec`/`DROP TABLE`
  convention the reader tests follow (structural_facts INSERT column set, missing-table test pattern).

## Concerns / notes for downstream
- **Per-candidate subqueries at scale.** Four indexed subqueries per candidate-kind symbol, as the brief
  prescribed ("do NOT materialize all identifiers"). Fixture-fast; on a ~38k-symbol real artifact this is many
  round-trips. Task 3's Scale test should confirm the wall-clock is acceptable; if it is not, the fix is a batched
  aggregate query, not a change to the Core contract.
- **FK enforcement is ON by default in Microsoft.Data.Sqlite.** The new fixture write helpers set
  `ForeignKeys=false` to match `Create`'s `PRAGMA foreign_keys=OFF`, so a builder row may reference an unseeded
  symbol/file. The `identifier_resolutions` CHECK is enforced regardless — `AddIdentifierResolution` callers must
  keep `outcome='resolved'` consistent with a non-null target.
- **Report not committed.** Per the strict `serial-worker-commit` "owned files only" instruction, this report and
  the pre-existing `task-1-report.md` / `.memories/` changes were left unstaged. (This path previously held an
  unrelated stale report; overwritten per the lead's explicit instruction to write here.)

## Files
- Create: `src/Miller.Indexing/DeadCodeCandidateReader.cs`
- Modify: `tests/Miller.Tests/Indexing/JulieDbFixture.cs`
- Modify: `tests/Miller.Tests/Indexing/JulieDbFixtureCurrentSchemaTests.cs`
- Create: `tests/Miller.Tests/Indexing/DeadCodeCandidateReaderTests.cs`
