# Source Regions Pillar 3 Implementation Plan

> Historical status: implemented. Current behavior is explicit `regions=comment|doc_comment|string_literal` search
> backed by the Miller-owned sidecar when region indexing is enabled.

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Build Miller's first `source_regions` consumer: explicit `regions=` search over comments, doc-comments, and string literals, backed by the Miller-owned `search.db` sidecar.

**Status 2026-06-05:** Implemented and verified. Final gates: `dotnet build Miller.slnx -c Release`,
`scripts/test.sh`, and `scripts/test.sh scale` pass. Scale probe evidence:
`region_build_ms=9.4`, `search_db_bytes=98304`, `source_regions=2`, `search_regions=2`.

**Architecture:** Keep symbol search, content/docs search, and region search as separate read models. `SearchIndexWriter` owns schema-v3 sidecar creation, `FtsRegionSearchIndex` owns fail-closed region reads, and `SearchTool` routes to region search only when `regions` is present. Symbol search remains unchanged except for a result-bounded `has_doc` annotation sourced from `symbols.doc_comment`.

**Tech Stack:** .NET 10, Microsoft.Data.Sqlite, SQLite FTS5, Miller.Core `CodeTokenizer`/`Bm25`, julie-extract schema v2 `source_regions`.

**Architecture Quality:** Affected modules: `Miller.Indexing` sidecar writer/readers, `Miller.Server.Workspaces` provider routing, `Miller.Server.Tools.SearchTool`, CLI search. Caller-facing interface: one optional `regions` search parameter and one CLI `--regions` option. Depth/locality check: no `ISymbolSearchIndex.Search` widening, no full repository load for `regions`, no `mode=content` reuse. Test surface: behavior through `SearchIndexWriter`, `FtsRegionSearchIndex`, `SearchTool`, CLI, and one Scale real-binary probe. Seams/adapters: add `IRegionSearchIndex`/`WorkspaceRegionSearchContext` only because explicit region search has no safe in-memory fallback. Rejected shortcuts: post-filtering content search, silently falling back to symbol results, and loading doc comments into the symbol projection. Architecture risk: medium, because this adds a durable sidecar schema and public tool parameter.

---

## Current Prerequisite State

- `julie-extract` release verified: `v2.1.1`, published 2026-06-05.
- Miller pin updated locally: `scripts/julie-pins.json` + `MillerExtractContract.PinnedJulieExtractVersion = "2.1.1"`.
- `bash scripts/restore-julie-extract.sh` restored `.tools/julie-extract` and verified the archive checksum.
- Fresh 2.1.1 extract of this repo: `source_regions=20887`; C# has `comment=3607`, `doc_comment=5505`, `string_literal=11062`.
- Verification already run for the pin/doc update: `dotnet build Miller.slnx -c Release`, `scripts/test.sh`, `scripts/test.sh scale`.

## File Structure

Create:
- `src/Miller.Core/Search/RegionSearchHit.cs` - pure result record for region hits.
- `src/Miller.Indexing/RegionIndexOptions.cs` - pure build/read options: enabled flag, indexed kinds, max region bytes.
- `src/Miller.Indexing/SourceRegionRow.cs` - source region row model read from `symbols.db`.
- `src/Miller.Indexing/SqliteSourceRegionReader.cs` - schema-gated SQLite reads for `source_regions` and `symbols.doc_comment` annotations.
- `src/Miller.Indexing/FtsRegionSearchIndex.cs` - read-only region search over `search.db`.
- `src/Miller.Indexing/IRegionSearchIndex.cs` - small search interface.
- `src/Miller.Server/Workspaces/IWorkspaceRegionSearchProvider.cs` - explicit provider seam for fail-closed region search.
- `src/Miller.Server/Workspaces/WorkspaceRegionSearchContext.cs` - region context with freshness envelope.
- `tests/Miller.Tests/Indexing/FtsRegionSearchIndexTests.cs`
- `tests/Miller.Tests/Indexing/SqliteSourceRegionReaderTests.cs`
- `tests/Miller.Tests/Search/RegionSearchScaleTests.cs`

Modify:
- `src/Miller.Indexing/SearchIndexWriter.cs`
- `src/Miller.Indexing/SymbolSearchSidecar.cs`
- `src/Miller.Server/Hosting/IndexerService.cs`
- `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`
- `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`
- `src/Miller.Server/Hosting/MillerServiceRegistration.cs`
- `src/Miller.Dashboard/DashboardData.cs`
- `src/Miller.Server/Tools/SearchTool.cs`
- `src/Miller.Server/Cli/CliDispatch.cs`
- `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`
- `tests/Miller.Tests/Indexing/JulieDbFixture.cs`
- `tests/Miller.Tests/Indexing/JulieDbFixtureV2SchemaTests.cs`
- Existing sidecar/search/provider/CLI tests in `tests/Miller.Tests/Indexing` and `tests/Miller.Tests/Server`

## Task 1: Fixture Schema And SQLite Reader

**Files:**
- Create: `src/Miller.Indexing/SourceRegionRow.cs`
- Create: `src/Miller.Indexing/SqliteSourceRegionReader.cs`
- Modify: `tests/Miller.Tests/Indexing/JulieDbFixture.cs`
- Rename/modify: `tests/Miller.Tests/Indexing/JulieDbFixtureV2SchemaTests.cs`
- Test: `tests/Miller.Tests/Indexing/SqliteSourceRegionReaderTests.cs`

**Work:**
- Add `JulieDbFixture.SourceRegionRow` with all 13 julie columns.
- Add `source_regions` DDL and indexes to the fixture, matching the verified schema in `docs/plans/2026-06-04-source-regions-pillar3-design.md`.
- Add fixture insertion support and make default schema tests assert table and index presence.
- Implement `SqliteSourceRegionReader.ReadIndexedRegions(dbPath)`:
  - Calls `JulieSchemaGate.Verify`.
  - Bulk reads `comment`, `doc_comment`, and `string_literal`; leaves `embedded` for later.
  - Joins `files` for `content_hash` and `content_bytes`.
  - Orders by `path, start_byte, source_region_id`.
- Implement `ReadHasDocComment(dbPath, symbolIds)` from `symbols.doc_comment IS NOT NULL`, not `source_regions`.

**Acceptance:**
- Reader returns nullable `containing_symbol_id`/`metadata_json` safely.
- Reader skips `embedded` for v1 region search.
- `ReadHasDocComment` is result-bounded and returns only requested IDs.

## Task 2: Region Text Extraction And Sidecar Schema V3

**Files:**
- Create: `src/Miller.Indexing/RegionIndexOptions.cs`
- Modify: `src/Miller.Indexing/SearchIndexWriter.cs`
- Modify: `src/Miller.Indexing/SymbolSearchSidecar.cs`
- Modify: `src/Miller.Server/Hosting/IndexerService.cs`
- Modify: `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`
- Modify: `src/Miller.Dashboard/DashboardData.cs`
- Tests: `tests/Miller.Tests/Indexing/SearchIndexWriterTests.cs`, `tests/Miller.Tests/Indexing/SymbolSearchSidecarTests.cs`, server writer-path tests.

**Work:**
- Bump `SearchIndexWriter.SchemaVersion` from 2 to 3.
- Extend schema:
  - `regions_fts(region_id UNINDEXED, body, tokenize='unicode61 remove_diacritics 0')`
  - `search_regions(region_id, kind, path, language, containing_symbol_id, start_line, end_line, start_byte, end_byte, raw_text, doc_len)`
  - `meta` gains `region_count` and `region_avgdl`.
- Always create region tables in schema v3, but populate them only when `RegionIndexOptions.Enabled`.
- Add `MILLER_REGION_INDEX` with default off; keep `MILLER_SEARCH_SIDECAR` default on.
- Add a max region byte cap, default `65536`, with an env override only if already supported cleanly; otherwise keep it constant for v1.
- Change `SearchIndexWriter.Write`/`SymbolSearchSidecar.EnsureBuilt` so writer paths can pass `workspaceRoot`.
- Under region enabled:
  - Read source regions from `symbols.db`.
  - Resolve each path with `WorkspaceRelativePath.ResolveUnderRoot`.
  - Read file bytes once per path.
  - Verify `ContentHasher.Blake3Hex(bytes)` equals stored `files.content_hash`.
  - Slice by UTF-8 byte offsets, strict-decode, tokenize with `CodeTokenizer`, store token stream in FTS body and raw text in `search_regions.raw_text`.
  - Skip stale, missing, unreadable, invalid UTF-8, invalid span, and oversize regions; do not fail the whole symbol sidecar build.

**Acceptance:**
- Region-disabled build produces schema v3 with empty region tables and no source-file reads.
- Region-enabled build populates only the allowed kinds.
- Existing symbol disk search tests stay green after schema bump.
- Both `IndexerService` and `CrossWorkspaceRefreshService` pass the right workspace root.

## Task 3: Fail-Closed Region Reader

**Files:**
- Create: `src/Miller.Core/Search/RegionSearchHit.cs`
- Create: `src/Miller.Indexing/IRegionSearchIndex.cs`
- Create: `src/Miller.Indexing/FtsRegionSearchIndex.cs`
- Tests: `tests/Miller.Tests/Indexing/FtsRegionSearchIndexTests.cs`

**Work:**
- Implement `FtsRegionSearchIndex.Open(searchDbPath, expectedRevision)`:
  - Throws a clear `InvalidOperationException` for missing file, stale revision, schema < 3, missing region tables, or malformed meta.
  - Loads resident region metadata and corpus stats.
- Implement `Search(query, kinds, limit, excludeTests)`:
  - Parse `regions` kinds elsewhere; this method receives validated kinds.
  - Tokenize query with `CodeTokenizer`.
  - Use FTS5 for recall and C# `Bm25` for ranking over region corpus stats.
  - Filter by requested kinds and `IsTestPath` path heuristic when `excludeTests`.
  - Return `RegionSearchHit` with path, line, kind, snippet/raw text, optional containing symbol ID/name if cheaply available from `search_symbols`.
- Keep ranking deterministic: score descending, then path, line, region ID.

**Acceptance:**
- `regions=comment` finds a token only inside a comment and excludes a code-only occurrence.
- `regions=string_literal` finds string-literal text.
- Kind unions work.
- Missing/stale schema raises actionable errors and never falls back to symbols.

## Task 4: Provider, Tool, CLI, And Rendering

**Files:**
- Create: `src/Miller.Server/Workspaces/IWorkspaceRegionSearchProvider.cs`
- Create: `src/Miller.Server/Workspaces/WorkspaceRegionSearchContext.cs`
- Modify: `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`
- Modify: `src/Miller.Server/Hosting/MillerServiceRegistration.cs`
- Modify: `src/Miller.Server/Tools/SearchTool.cs`
- Modify: `src/Miller.Server/Cli/CliDispatch.cs`
- Modify: `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`
- Tests: `tests/Miller.Tests/Server/SearchToolTests.cs`, `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`, `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs`, `tests/Miller.Tests/Server/AgentInstructionsTests.cs`

**Work:**
- Add `IWorkspaceRegionSearchProvider.ResolveRegionSearch(workspace_id, ensureFresh)`.
- Region provider path resolves freshness like symbol/content search, then opens `FtsRegionSearchIndex` fail-closed.
- If `MILLER_REGION_INDEX` is off, explicit region search returns: "region search requires MILLER_REGION_INDEX=1 and a refreshed search sidecar."
- `SearchTool.Search` gains optional `regions` parameter.
- Routing:
  - `regions` present -> region search regardless of `mode`; output notes when a conflicting mode was ignored.
  - `regions` absent -> current symbol/content behavior.
- `SearchTool.Run` refactor:
  - Preserve current symbol rendering when no doc annotations are provided.
  - Add result-bounded `has_doc` annotation for symbol results using `SqliteSourceRegionReader.ReadHasDocComment`.
  - For compact output, append `has_doc` only when true.
  - For JSON, add `"has_doc": true|false`.
- CLI search parses `--regions comment,doc_comment,string_literal`.
- Agent instructions document `regions`, `MILLER_REGION_INDEX`, and `has_doc`.

**Acceptance:**
- Existing `search` behavior is unchanged without `regions`.
- Explicit region search fails closed when disabled/missing/stale.
- Region compact output is `path:line  kind  symbol?` plus indented snippet.
- Region JSON includes `file`, `line`, `kind`, `score`, `snippet`, and containing symbol info when present.
- CLI mirrors MCP routing.

## Task 5: Scale Probe And Final Gates

**Files:**
- Create: `tests/Miller.Tests/Search/RegionSearchScaleTests.cs`
- Modify: `docs/plans/2026-06-04-source-regions-pillar3-design.md` with measured build-cost evidence.

**Work:**
- Add a `[Trait("Category","Scale")]` real-binary test using `ScaleTestSupport.RequireJulieServer()`.
- Extract a fixture tree containing at least one C# comment-only token and string-literal token.
- Build/refresh with `MILLER_REGION_INDEX=1`, assert `search.db` has populated `search_regions`.
- Query `regions=comment` through the tool or CLI and assert it returns the comment hit, not a code occurrence.
- Record report-only metrics: region build duration and `search.db` size delta for this repo or the fixture.
- Run final verification gates.

**Acceptance:**
- Scale test proves real 2.1.1 emits source regions and Miller takes the disk region path.
- Build-cost metrics are recorded as report-only unless they reveal a clear regression.
- Final gates pass with 0 warnings/errors.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` / `AGENTS.md` testing section.

**Worker red/green scope:** For each task, run the narrowest relevant `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter FullyQualifiedName~<TestClassOrMethod>` after writing the failing test, then after implementation.

**Worker ceiling:** Workers may run `scripts/test.sh` only when their task touches shared sidecar/tool behavior and the narrow tests pass. Workers do not own `scripts/test.sh scale`.

**Worker gate invariant:** Narrow tests prove the specific reader/writer/routing behavior under TDD; fast suite proves no non-scale regression or analyzer warning.

**Lead affected-change scope:** `dotnet build Miller.slnx -c Release`, `scripts/test.sh`, targeted affected tests for indexing/search/server routing.

**Branch gate:** `dotnet build Miller.slnx -c Release`; `scripts/test.sh`; `scripts/test.sh scale`.

**Replay/metric evidence:** Hard gates: source-region coverage exists for C# on a fresh 2.1.1 extract; `regions=` fails closed when unavailable; no symbol/content behavior regression. Report-only: region build duration, `search.db` size delta.

**Escalation triggers:** Any API shape that requires widening `ISymbolSearchIndex`, any need to read full repository indexes for region search, any scale run showing region indexing cost too high to keep default behavior, or any extractor coverage gap for a supported language present in the fixture.

**Assigned verification failure:** Workers stop and report when assigned verification fails unless the plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp. For metrics, record hard-gate and report-only metrics separately.

## Model Routing

**Project source of truth:** No repo-local `RAZORBACK.md` is present.

**Strategy tier:** Lead session, inherit harness default.

**Implementation tier:** Worker subagents, inherit harness default.

**Mechanical tier:** Worker subagents only for docs/fixture-only patches, inherit harness default.

**Gate-interpretation reviewer:** Lead session.

**Escalation tier:** Lead session; use external review only if repeated failures or public schema ambiguity appear.

**Worker eligibility:** Tasks 1 and 3 are good worker candidates after the plan is accepted. Tasks 2 and 4 touch shared lifecycle/public tool shape and need lead review after each batch. Task 5 is lead-owned because it interprets scale evidence.

**Escalation triggers:** Two failed worker attempts, any stale sidecar read/write mismatch, any tool output contract ambiguity, any scale or extractor coverage surprise.

**Mechanical exclusion:** Mechanical workers cannot own failing tests, metric interpretation, or acceptance gates.

**Unsupported harness behavior:** If per-agent model selection is unavailable, inherit the session model and continue.
