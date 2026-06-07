# Miller Content Corpus and FTS5 Search Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Build a Miller-owned content corpus and FTS5 search path that can find source-body text, docs/config text, large external files, browser-fetched web content, and Eros-ready semantic chunks without pushing full source contents into `julie-extractors`.

**Architecture:** Add a dedicated `<workspace>/.miller/content.db` sidecar for chunked text content. Keep `julie-extractors` as the structured extraction source of truth; Miller re-sources workspace files from disk, verifies them against `files.content_hash`, chunks them, indexes the chunks with SQLite FTS5, and stores enough metadata for snippets, containing-symbol routing, large-file reads, web research, and Eros semantic ingestion. Keep default symbol search narrow; expose text search through explicit modes and content tools.

**Tech Stack:** .NET 10, Microsoft.Data.Sqlite, SQLite FTS5 `unicode61`, Miller.Core tokenization/ranking helpers, BLAKE3 content verification, `julie-extract` SQLite `files`/`symbols` metadata, MCP tools, Miller CLI, optional `browser39` skill workflow, JSONL export for Eros.

**Architecture Quality:** Affected modules: `Miller.Indexing` sidecar writers/readers, `Miller.Core.Search` hit/chunk models and ranking helpers, `Miller.Server.Tools.SearchTool`, a new content/large-file tool surface, `WorkspaceIndexProvider`, CLI dispatch, workspace status/dashboard facts, plugin skills, and docs/contracts. Caller-facing interface: explicit search modes (`content`, `source`, Phase 4 `web`, Phase 3 `external`, Phase 6 `all-text`) plus a content-corpus tool for indexing and bounded reads of non-workspace text. Depth/locality check: source/docs text indexing is local to `content.db`; symbol search remains in `search.db`; `julie-extractors` does not gain full-content storage; Eros consumes a stable corpus contract without adding embeddings to Miller. Test surface: fast unit tests through writer/reader/search/tool/CLI interfaces, status rendering tests, plugin skill mirror tests, and opt-in scale checks for build size/time. Seams/adapters: introduce `ITextContentSearchIndex`/`WorkspaceTextContentSearchContext` only if reusing `IContentSearchIndex` would blur docs-only `mode=content` with full source/external/web modes. Rejected shortcuts: storing full source text in `julie-extractors`, blending source-body hits into default symbol search, adding Lucene.NET before FTS5 evidence fails, and dirtying user repos with `docs/web/**` as the primary web-research storage. Architecture risk: medium-high because this adds a persistent text corpus, public search modes, external-file lifecycle, and an Eros-facing data contract.

---

## Scope

This is an umbrella plan. Each phase should land as working, testable software and can be executed independently after the prior phase is accepted.

- Phase 0 records the content corpus contract and benchmark baseline.
- Phase 1 ships workspace source-file text search.
- Phase 2 folds existing docs/config `mode=content` onto the same corpus.
- Phase 3 adds large external file/log indexing and bounded reading.
- Phase 4 adds browser/web research workflow support.
- Phase 5 exposes stable Eros semantic-ingestion data.

Out of scope for this plan:

- Adding full source contents to `julie-extractors`.
- Adding embeddings or semantic ranking to Miller.
- Replacing symbol search ranking with full-text search.
- Adding Lucene.NET unless FTS5 measurements fail concrete quality or performance requirements.

## Locked Design Decisions

- `julie-extractors` remains lean. It provides path, language, byte ranges, content hashes, source regions, symbols, and relationships. Miller reads full text from the source tree or from explicit content imports.
- Use a new `.miller/content.db` sidecar instead of overloading `.miller/search.db`. `search.db` remains the symbol/source-region sidecar keyed directly to `symbols.db`; `content.db` can carry workspace-derived text plus external and web corpora with different lifecycle rules.
- Store chunk text in `content.db`. This costs disk, but it enables line snippets, bounded reads, external file stability after import, web content, and Eros semantic chunk export.
- Use word FTS5 first. Do not add full-corpus trigram indexing in the first implementation.
- Preserve current `mode=content` as docs/prose/config search. Add `mode=source` for source-body text. Keep current `mode=text` behavior unchanged until there is explicit evidence and a migration plan.
- Make `mode=all-text` a Phase 6 union mode, not a Phase 1 default.
- Keep search results concise: path, best line, snippet window, content kind, language, source bytes, and containing symbol when available.
- External files and web pages are Miller-owned content sources; they do not require `julie-extract`.
- Eros reads the content corpus contract and adds semantic indexing externally; Miller only exports deterministic chunks and metadata.

## Current Codebase Anchors

- `src/Miller.Indexing/SearchIndexWriter.cs` currently owns `.miller/search.db` for symbols and source regions.
- `src/Miller.Indexing/SymbolSearchSidecar.cs` owns sidecar freshness, rebuild, and incremental convergence.
- `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs` routes symbol/content/region providers and caches by workspace, DB path, and revision.
- `src/Miller.Server/Tools/SearchTool.cs` parses modes, renders compact/JSON search output, and already has `mode=content` and `regions=...`.
- `src/Miller.Indexing/ContentSearchProjectionLoader.cs` builds the current in-memory docs-like `mode=content` projection by re-sourcing files and verifying BLAKE3.
- `src/Miller.Core/Search/ContentSearchIndex.cs`, `ContentDocument.cs`, and `ContentSearchHit.cs` already implement line/snippet selection for docs-like content.
- `src/Miller.Server/Cli/CliDispatch.cs` mirrors MCP search behavior for CLI.
- `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` and `.agents/skills/*` document the current search mode split.
- `docs/plans/2026-06-02-search-projections-design.md` is the prior content-search design.
- `docs/plans/2026-06-04-symbol-search-collapsed-trigram-design.md` is the symbol sidecar design.
- `docs/plans/2026-06-05-source-regions-pillar3-implementation-plan.md` is the region-search sidecar precedent.
- `spike/SearchProjection.Spike/Program.cs` already contains measured content corpus loading and FTS experiments.

## File Structure

### Core and Indexing

- Create: `src/Miller.Core/Search/TextContentDocument.cs`
  - Immutable chunk/document input model with `SourceId`, `ChunkId`, `ContentKind`, `Path`, `Language`, `LineStart`, `LineEnd`, `ByteStart`, `ByteEnd`, `Text`, `IsTest`, and optional containing-symbol fields.
- Create: `src/Miller.Core/Search/TextContentSearchHit.cs`
  - Result model for FTS-backed text hits: score, kind, path/url/display path, line, snippet, source bytes, chunk lines, optional containing symbol, and source id.
- Create: `src/Miller.Indexing/TextContentKind.cs`
  - String constants or enum values: `workspace_source`, `workspace_docs`, `workspace_config`, `external_file`, `web`.
- Create: `src/Miller.Indexing/ContentCorpusSchema.cs`
  - Schema version, DDL strings, and table/column names for `.miller/content.db`.
- Create: `src/Miller.Indexing/ContentCorpusWriter.cs`
  - Atomic build and incremental update writer for workspace-derived chunks. Uses BLAKE3 verification and the same path-safety rules as `ExtractReader` and `ContentSearchProjectionLoader`.
- Create: `src/Miller.Indexing/ContentCorpusChunker.cs`
  - Splits text into bounded line chunks with overlap. First defaults: 160 lines per chunk, 20-line overlap, hard cap of 1 MiB per workspace file, hard cap of 25 MiB per external file import unless caller passes an explicit higher cap.
- Create: `src/Miller.Indexing/FtsTextContentSearchIndex.cs`
  - Read-only FTS5 index reader over `content.db`; returns candidates and re-ranks/snippets in C# using the existing content-search line/snippet behavior.
- Create: `src/Miller.Indexing/ITextContentSearchIndex.cs`
  - Read seam for FTS-backed text corpus search.
- Create: `src/Miller.Indexing/ContentCorpusFacts.cs`
  - Facts for status/dashboard: state, path, schema version, workspace revision, source count, chunk count, indexed bytes, raw text bytes, largest source, last build/update outcome.
- Create: `src/Miller.Indexing/ContentCorpusSidecar.cs`
  - Sidecar lifecycle: disabled/enabled env parsing, path resolution, inspect facts, build, incremental update, and read-open helpers.
- Create: `src/Miller.Indexing/ContentCorpusExternalStore.cs`
  - Adds/removes external and web sources from `content.db` without touching `symbols.db`.
- Create: `src/Miller.Indexing/ContentCorpusExportReader.cs`
  - Stable reader for Eros/export JSONL and direct SQLite consumers.

### Server and CLI

- Modify: `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`
  - Add text-content provider/caching for `content.db`. Preserve existing symbol, content, and region paths while Phase 2 migrates docs-like `mode=content`.
- Create: `src/Miller.Server/Workspaces/IWorkspaceTextContentSearchProvider.cs`
  - `ResolveTextContentSearch(workspaceId, ensureFresh)` returning a `WorkspaceTextContentSearchContext`.
- Create: `src/Miller.Server/Workspaces/WorkspaceTextContentSearchContext.cs`
  - Carries `ITextContentSearchIndex`, `ContentDbPath`, workspace identity, revision, root, freshness, and source counts.
- Modify: `src/Miller.Server/Tools/SearchTool.cs`
  - Add `mode=source`; route to text-content search with `ContentKind=workspace_source`.
  - Keep `mode=content` docs/config only.
  - Add explicit `mode=external` in Phase 3, `mode=web` in Phase 4, and `mode=all-text` in Phase 6.
  - Render compact/JSON with kind/source metadata and containing symbol.
- Create: `src/Miller.Server/Tools/ContentTool.cs`
  - MCP tool for external and web corpus actions: `list`, `add_file`, `add_markdown`, `search`, `read`, `remove`, and `export`.
- Modify: `src/Miller.Server/Cli/CliDispatch.cs`
  - Add `miller content ...` commands mirroring `ContentTool`.
  - Add CLI `search --mode source`.
- Modify: `src/Miller.Server/Hosting/IndexBootstrapService.cs`
  - Ensure `content.db` is built/converged after `symbols.db` and under the writer lock.
- Modify: `src/Miller.Server/Hosting/CrossWorkspaceRefreshService.cs`
  - Ensure registered workspace refresh converges `content.db`.
- Modify: `src/Miller.Server/Hosting/MillerServiceRegistration.cs`
  - Register new content corpus sidecar/provider/tool dependencies.
- Modify: `src/Miller.Server/Tools/WorkspaceRender.cs`
  - Report `content_db` facts beside `search_db`.
- Modify: `src/Miller.Dashboard/DashboardIndexFactsReader.cs`
  - Read and display content corpus facts.
- Modify: `src/Miller.Dashboard/Components/WorkspaceDetailPanel.razor`
  - Show content corpus status, source counts, chunk counts, and bytes.

### Skills, Docs, and Contracts

- Create: `docs/contracts/content-corpus-v1.md`
  - Stable schema and JSONL export contract for Eros and other consumers.
- Create: `docs/plans/2026-06-07-content-corpus-fts5-search-plan.md`
  - This plan.
- Modify: `docs/search-quality-runner.md`
  - Add text-content search evaluation guidance in Phase 1 with source-mode runner cases.
- Modify: `tools/Miller.SearchQuality/SearchQuality.cs`
  - Add cases/provider support for `mode=source`, `mode=content`, and Phase 6 `mode=all-text`.
- Modify: `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`
  - Document mode split and large-file/web workflows.
- Modify: `.agents/skills/miller-search-debug/SKILL.md`
  - Add text corpus diagnostics.
- Modify: `.agents/skills/miller-explore-area/SKILL.md`
  - Add source-text and large-file search guidance.
- Create: `.agents/skills/miller-web-research/SKILL.md`
  - Browser39-based workflow: fetch markdown without printing full content, import via `content add_markdown`, search/read through Miller.
- Create: `.agents/skills/miller-large-file/SKILL.md`
  - Token-efficient workflow for logs/reports/large text outside the workspace.
- Modify: `skills/**`
  - Generated mirror of `.agents/skills/**`; run `scripts/sync-plugin-skills.sh` after adding or changing repo agent skills.
- Modify: `tests/plugin/plugin-manifest.test.cjs`
  - Keep the byte-for-byte `.agents/skills` to `skills` mirror assertion passing after new skills are added.
- Preserve: `.claude-plugin/plugin.json`, `.codex-plugin/plugin.json`, `.agents/plugins/marketplace.json`
  - These already point at `./skills/`; change them only if the plugin contract itself changes.

### Tests

- Create: `tests/Miller.Tests/Indexing/ContentCorpusSchemaTests.cs`
- Create: `tests/Miller.Tests/Indexing/ContentCorpusWriterTests.cs`
- Create: `tests/Miller.Tests/Indexing/FtsTextContentSearchIndexTests.cs`
- Create: `tests/Miller.Tests/Indexing/ContentCorpusExternalStoreTests.cs`
- Create: `tests/Miller.Tests/Server/ContentToolTests.cs`
- Modify: `tests/Miller.Tests/Server/SearchToolTests.cs`
- Modify: `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs`
- Modify: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`
- Modify: `tests/Miller.Tests/Server/WorkspaceRenderTests.cs`
- Modify: `tests/Miller.Tests/Server/AgentInstructionsTests.cs`
- Modify: `tests/Miller.Tests/Indexing/SymbolSearchSidecarTests.cs` only if shared sidecar helpers move.
- Modify: `tests/plugin/*.test.cjs` when Phase 3, Phase 4, or Phase 6 ships new skills.
- Create or extend scale tests only for large corpus metrics; tag them `[Trait("Category","Scale")]` if they spawn `julie-extract` or build large fixtures.

## Content Corpus Contract

`content.db` should contain these logical tables in schema v1:

- `content_sources`
  - `source_id TEXT PRIMARY KEY`
  - `content_kind TEXT NOT NULL`
  - `workspace_id TEXT NULL`
  - `workspace_revision INTEGER NULL`
  - `path TEXT NULL`
  - `url TEXT NULL`
  - `display_path TEXT NOT NULL`
  - `language TEXT NOT NULL`
  - `content_hash TEXT NOT NULL`
  - `source_bytes INTEGER NOT NULL`
  - `line_count INTEGER NOT NULL`
  - `is_test INTEGER NOT NULL`
  - `status TEXT NOT NULL`
  - `indexed_at_utc TEXT NOT NULL`
- `content_chunks`
  - `chunk_id TEXT PRIMARY KEY`
  - `source_id TEXT NOT NULL`
  - `content_kind TEXT NOT NULL`
  - `path TEXT NULL`
  - `url TEXT NULL`
  - `display_path TEXT NOT NULL`
  - `language TEXT NOT NULL`
  - `line_start INTEGER NOT NULL`
  - `line_end INTEGER NOT NULL`
  - `byte_start INTEGER NOT NULL`
  - `byte_end INTEGER NOT NULL`
  - `raw_text TEXT NOT NULL`
  - `doc_len INTEGER NOT NULL`
  - `is_test INTEGER NOT NULL`
  - `containing_symbol_id TEXT NULL`
  - `containing_symbol_name TEXT NULL`
- `content_fts`
  - FTS5 virtual table with `chunk_id UNINDEXED, body`, tokenizer `unicode61 remove_diacritics 0`.
- `content_meta`
  - `schema_version INTEGER NOT NULL`
  - `workspace_revision INTEGER NULL`
  - `source_count INTEGER NOT NULL`
  - `chunk_count INTEGER NOT NULL`
  - `indexed_source_bytes INTEGER NOT NULL`
  - `stored_raw_bytes INTEGER NOT NULL`

`content.db` should be rebuilt atomically for workspace-derived content. External/web mutations should run in transactions and update `content_meta` without rewriting workspace chunks.

## Phase 0: Measurement and Contract Baseline

**Files:**
- Modify: `spike/SearchProjection.Spike/Program.cs`
- Create: `docs/findings/2026-06-07-content-corpus-fts5-baseline.md`
- Create: `docs/contracts/content-corpus-v1.md`

**What to build:** Extend the existing spike to measure source+docs chunking, FTS5 size, build time, query latency, and result usefulness on at least Miller, Julie, and julie-extractors workspaces. Capture the content corpus schema contract before production code depends on it.

**Approach:** Reuse the spike's current content corpus loader, but add source-file scope, chunking, and report-only metrics. Use the same file caps and BLAKE3 verification behavior planned for production. The contract doc should define schema, search modes, lifecycle, Eros consumption, and privacy/storage consequences.

**Acceptance criteria:**
- [ ] Metrics document records source count, chunk count, indexed bytes, raw text bytes, FTS DB bytes, full rebuild time, and representative query latency for each measured workspace.
- [ ] Report includes at least these query classes: error string, env var/config key, route/path literal, assertion text, natural-language implementation phrase, docs phrase.
- [ ] Contract doc defines every required `content_kind`, source/chunk table field, export field, and lifecycle rule explicitly.
- [ ] No production behavior changes in this phase.
- [x] Worker-scope verification passes and changes are committed.

## Phase 1: Workspace Source Text Search

**Files:**
- Create: `src/Miller.Core/Search/TextContentDocument.cs`
- Create: `src/Miller.Core/Search/TextContentSearchHit.cs`
- Create: `src/Miller.Indexing/TextContentKind.cs`
- Create: `src/Miller.Indexing/ContentCorpusSchema.cs`
- Create: `src/Miller.Indexing/ContentCorpusChunker.cs`
- Create: `src/Miller.Indexing/ContentCorpusWriter.cs`
- Create: `src/Miller.Indexing/FtsTextContentSearchIndex.cs`
- Create: `src/Miller.Indexing/ITextContentSearchIndex.cs`
- Create: `src/Miller.Indexing/ContentCorpusFacts.cs`
- Create: `src/Miller.Indexing/ContentCorpusSidecar.cs`
- Create: `src/Miller.Server/Workspaces/IWorkspaceTextContentSearchProvider.cs`
- Create: `src/Miller.Server/Workspaces/WorkspaceTextContentSearchContext.cs`
- Modify: `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`
- Modify: `src/Miller.Server/Tools/SearchTool.cs`
- Modify: `src/Miller.Server/Cli/CliDispatch.cs`
- Modify: `src/Miller.Server/Hosting/IndexBootstrapService.cs`
- Modify: `src/Miller.Server/Hosting/CrossWorkspaceRefreshService.cs`
- Modify: `src/Miller.Server/Hosting/MillerServiceRegistration.cs`
- Test: `tests/Miller.Tests/Indexing/ContentCorpusSchemaTests.cs`
- Test: `tests/Miller.Tests/Indexing/ContentCorpusWriterTests.cs`
- Test: `tests/Miller.Tests/Indexing/FtsTextContentSearchIndexTests.cs`
- Test: `tests/Miller.Tests/Server/SearchToolTests.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs`
- Test: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`

**What to build:** Add `mode=source` search over verified workspace source-file text. It should find error strings, assertion text, route strings, config keys, SQL snippets, and implementation-body phrases without changing default symbol search.

**Approach:** Build `content.db` from `symbols.db.files` rows whose status is `indexed`, whose file is under the workspace root, whose BLAKE3 matches `content_hash`, whose UTF-8 content is valid, and whose path is not docs-like according to the existing classifier. Chunk text by lines, store raw chunk text, index tokenized chunk text in FTS5, and compute snippets/line hits in C#. Use `search_symbols` line ranges from `search.db` or symbol rows from `symbols.db` to attach containing-symbol metadata when the best hit line falls inside a symbol span.

**Acceptance criteria:**
- [x] `search(query="known error string", mode="source")` returns the file/line/snippet containing that source-body string.
- [x] `mode=source` supports `file_pattern`, `language`, and `exclude_tests`.
- [x] `mode=source` compact output includes path, line, snippet, content kind, and containing symbol when available.
- [x] `mode=source` JSON output includes source id, chunk id, content kind, path, language, line span, byte span, score, snippet, source bytes, and containing symbol fields.
- [x] `mode=content` behavior remains byte-compatible except for optional workspace/status banners already accepted by existing tests.
- [x] Default `search` and `mode=symbol` do not include source-body text hits.
- [x] Missing, stale, unreadable, non-UTF-8, oversize, or out-of-root files are skipped and reported through facts, not surfaced as raw exceptions.
- [x] `content.db` rebuilds atomically and rejects stale schema versions visibly.
- [x] Worker-scope verification passes and changes are committed.

## Phase 2: Unify Docs/Config Content Search on Content Corpus

**Files:**
- Modify: `src/Miller.Indexing/ContentSearchProjectionLoader.cs`
- Modify: `src/Miller.Indexing/ContentFileClassifier.cs`
- Modify: `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`
- Modify: `src/Miller.Server/Tools/SearchTool.cs`
- Modify: `src/Miller.Server/Cli/CliDispatch.cs`
- Modify: `tests/Miller.Tests/Indexing/ContentSearchProjectionLoaderTests.cs`
- Modify: `tests/Miller.Tests/Search/ContentSearchIndexTests.cs`
- Modify: `tests/Miller.Tests/Server/SearchToolTests.cs`
- Modify: `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs`

**What to build:** Route existing docs-like `mode=content` through `content.db` so docs/config/source text search share one corpus and one result model while preserving the current public semantics of `mode=content`.

**Approach:** Populate `workspace_docs` and `workspace_config` source kinds in `content.db` from the files currently accepted by `ContentFileClassifier`. Keep `mode=content` scoped to those kinds. Retire or reduce the in-memory content projection after parity tests pass. Preserve line/snippet behavior from `ContentSearchIndex`.

**Acceptance criteria:**
- [x] Existing `mode=content` tests pass with FTS-backed corpus search.
- [x] `mode=content` continues to exclude normal source-body hits.
- [x] Docs/config chunks record `content_kind` as `workspace_docs` or `workspace_config`.
- [x] Current compact and JSON result shapes remain compatible or receive explicitly versioned additions only.
- [x] Registered workspace `mode=content` uses content corpus loading without loading the full graph.
- [x] Worker-scope verification passes and changes are committed.

## Phase 3: Large External File and Log Tool

**Files:**
- Create: `src/Miller.Indexing/ContentCorpusExternalStore.cs`
- Create: `src/Miller.Server/Tools/ContentTool.cs`
- Modify: `src/Miller.Server/Cli/CliDispatch.cs`
- Modify: `src/Miller.Server/Hosting/MillerServiceRegistration.cs`
- Create: `.agents/skills/miller-large-file/SKILL.md`
- Modify: `tests/Miller.Tests/Indexing/ContentCorpusExternalStoreTests.cs`
- Create: `tests/Miller.Tests/Server/ContentToolTests.cs`
- Modify: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`
- Modify: `tests/plugin/*.test.cjs`

**What to build:** Add a token-efficient large-file workflow for text files outside the workspace: index, search, list, read bounded windows, and remove. This is for logs, CI output, generated reports, large JSON/text dumps, and other text that would trash an agent context if read directly.

**Approach:** `ContentTool` imports external files into `content.db` with `content_kind=external_file`, a stable `source_id`, full content hash, source byte count, and chunked raw text. It must not require `julie-extract`; it must not print full content. The read action accepts source id plus line/window or hit id and returns bounded text only. CLI mirrors the MCP operations for local debugging.

**Acceptance criteria:**
- [x] A large external log file can be imported without printing its full content.
- [x] Search returns concise snippets from imported external files.
- [x] Read returns a bounded line window by source id and line number.
- [x] Remove deletes source/chunk/FTS rows for one imported source.
- [x] List shows imported sources with kind, display path, bytes, chunks, and indexed time.
- [x] Hard caps prevent accidental huge imports unless the caller passes an explicit max-byte override.
- [x] The large-file skill instructs agents to use the tool instead of `cat`/full reads for large text.
- [x] Worker-scope verification and plugin skill tests pass, and changes are committed.

## Phase 4: Web Research Workflow

**Files:**
- Create: `.agents/skills/miller-web-research/SKILL.md`
- Modify: `src/Miller.Server/Tools/ContentTool.cs`
- Modify: `src/Miller.Server/Cli/CliDispatch.cs`
- Modify: `tests/Miller.Tests/Server/ContentToolTests.cs`
- Modify: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`
- Modify: `tests/plugin/*.test.cjs`
- Modify: `README.md`
- Modify: `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`

**What to build:** Add a Miller-native web research workflow that fetches via `browser39` at the skill layer, imports markdown into `content.db`, and lets agents search/read web pages without dirtying the repo with `docs/web/**`.

**Approach:** Keep network/browser fetching outside the Miller binary. The skill checks for `browser39`, fetches markdown to a temp file without leaking full content to stdout, then calls `content add_markdown` or the MCP equivalent with `content_kind=web`, URL metadata, and display path. Add `search mode=web` or `content search --kind web` once web sources exist.

**Acceptance criteria:**
- [ ] The web skill fetches a URL with `browser39`, imports markdown, and reports source id, byte count, and chunk count.
- [ ] Web search returns concise snippets scoped to `content_kind=web`.
- [ ] Web read returns bounded windows or sections from imported markdown.
- [ ] The workflow does not create or modify `docs/web/**`.
- [ ] Missing `browser39` produces an actionable prerequisite message.
- [ ] Plugin skill mirror tests pass.
- [ ] Worker-scope verification passes and changes are committed.

## Phase 5: Eros Semantic Search Feed

**Files:**
- Create: `src/Miller.Indexing/ContentCorpusExportReader.cs`
- Create: `docs/contracts/content-corpus-v1.md`
- Modify: `src/Miller.Server/Tools/ContentTool.cs`
- Modify: `src/Miller.Server/Cli/CliDispatch.cs`
- Create: `tests/Miller.Tests/Indexing/ContentCorpusExportReaderTests.cs`
- Modify: `tests/Miller.Tests/Server/ContentToolTests.cs`
- Modify: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`

**What to build:** Expose stable chunk data for Eros semantic indexing without adding embeddings to Miller. Eros should be able to consume source/docs/external/web chunks with deterministic ids and freshness metadata.

**Approach:** Document `content.db` as a stable local data contract and add a JSONL export for tools that do not want direct SQLite reads. Export rows should include workspace id, source id, chunk id, content kind, path/url/display path, language, line/byte span, content hash, workspace revision, raw text, and containing-symbol metadata.

**Acceptance criteria:**
- [ ] JSONL export is deterministic for unchanged content.
- [ ] Export includes all metadata Eros needs to embed, refresh, and delete stale chunks.
- [ ] Export can be scoped by content kind and workspace id.
- [ ] Miller does not invoke embedding models or Eros code.
- [ ] Contract tests pin required fields and schema version.
- [ ] Worker-scope verification passes and changes are committed.

## Phase 6: Cross-Workspace and Audit Workflows

**Files:**
- Modify: `src/Miller.Server/Tools/SearchTool.cs`
- Modify: `src/Miller.Server/Tools/ContentTool.cs`
- Modify: `.agents/skills/miller-search-debug/SKILL.md`
- Modify: `.agents/skills/miller-explore-area/SKILL.md`
- Create: `.agents/skills/miller-text-audit/SKILL.md`
- Modify: `tools/Miller.SearchQuality/SearchQuality.cs`
- Create: `docs/findings/2026-06-07-content-corpus-dogfood.md`

**What to build:** Use the content corpus to support cross-workspace text search, audits for dangerous strings, and better `context` seeding.

**Approach:** Start with explicit content searches across registered workspaces. Add audit skill workflows for debt/security/compatibility terms. Feed high-confidence content hits into `context` only after direct source-mode quality is proven.

**Acceptance criteria:**
- [ ] Cross-workspace text search reports workspace id/display id for every hit.
- [ ] Audit skill can search for a configured set of terms and produce concise file/line summaries.
- [ ] Search quality runner has source/content cases for error strings, config keys, assertions, docs, and web/external content.
- [ ] `context` integration remains opt-in until quality evidence shows it improves results.
- [ ] Worker-scope verification passes and changes are committed.

## Verification Strategy

**Project source of truth:** `AGENTS.md` and `CLAUDE.md` define Miller's build/test rules. The default command is `scripts/test.sh`; build confidence is `dotnet build Miller.slnx -c Release`; scale tests are opt-in through `scripts/test.sh scale` or `scripts/test.sh all`.

**Worker red/green scope:** Use the narrowest affected test command for the task. Examples: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~ContentCorpusWriterTests --no-restore`, `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~SearchToolTests --no-restore`, `scripts/test-plugin.sh` for skill/package changes.

**Worker ceiling:** Workers may run focused `dotnet test` filters, `scripts/test-plugin.sh`, `npx -y node@22 --test tests/plugin/*.test.cjs`, and `scripts/test.sh`. Workers should not run scale tests unless assigned a task that touches real extract/index scale behavior.

**Worker gate invariant:** Each worker gate must prove the public behavior of its phase through writer/reader/tool/CLI interfaces, not only private helpers. Search result tests must assert compact and JSON output shape where the public surface changes.

**Lead affected-change scope:** After a coherent phase batch, run `git diff --check`, `dotnet build Miller.slnx -c Release --no-restore`, `scripts/test.sh`, and any plugin tests when `.agents/skills`, plugin manifests, or `bin/miller-plugin-launcher.cjs` are touched.

**Branch gate:** Before handoff, push, or PR, run `dotnet build Miller.slnx -c Release --no-restore`, `scripts/test.sh`, `scripts/test-plugin.sh` if plugin/skill files changed, and `scripts/test.sh scale` if the phase touches extract/refresh/indexing behavior that depends on real `julie-extract`.

**Replay/metric evidence:** Hard gates: explicit mode behavior, no default symbol-search blending, stale/missing/corrupt sidecar failure behavior, bounded output for large files, deterministic Eros export fields. Report-only metrics: content DB size, chunk count, indexed bytes, build time, query latency, top-k quality, and context token savings.

**Escalation triggers:** Broader tiers are required if FTS5 schema changes break existing symbol/region sidecar readers, content corpus build makes default startup or refresh materially slower, external/web content lifecycle risks deleting user files, AOT/build packaging pulls in new unsupported dependencies, or Eros contract requirements conflict with Miller's local deterministic boundary.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp. For replay or metric evidence, also record hard-gate metrics and report-only metrics. If the same HEAD already has a passing ledger entry for the required scope, reuse that evidence instead of rerunning the same expensive gate.

## Model Routing

**Project source of truth:** No `RAZORBACK.md` exists in this repo at plan time. Use the current harness default model unless the user supplies a reviewer/model choice at approval.

**Strategy tier:** Planning, architecture, decomposition, lead review, finding triage.
- Harness mapping: inherit.

**Implementation tier:** Bounded worker tasks from this plan after explicit approval.
- Harness mapping: inherit.

**Mechanical tier:** Docs, fixtures, manifest mirrors, formatting, and test data changes with no gate ownership.
- Harness mapping: inherit.

**Gate-interpretation reviewer:** Review failing tests, replay/metric evidence, and diffs when deciding whether the test or implementation is wrong.
- Harness mapping: inherit unless the approval message requests `codex`, `gemini`, or `claude` review.

**Escalation tier:** Security/privacy, high blast radius, weak tests, repeated verification failures, stale sidecar correctness, Eros contract disputes.
- Harness mapping: inherit unless the approval message requests a specific reviewer.

**Worker eligibility:** Implementation-tier workers are eligible only for tasks with exact files, public acceptance criteria, and focused verification commands from this plan.

**Escalation triggers:** Any change that stores additional raw text, changes public search defaults, touches release packaging/AOT dependency surface, or changes the Eros contract requires lead review before merge.

**Mechanical exclusion:** Mechanical workers cannot own failing tests, replay evidence, metrics, or acceptance gates. Split docs-only updates from evidence interpretation.

**Unsupported harness behavior:** If the harness cannot choose models per agent, use `inherit`, note it in the verification ledger, and continue.

## Execution Notes

- Use TDD for each phase: write public behavior tests first, verify failure, implement minimal production code, verify pass.
- Commit at phase boundaries or smaller coherent slices.
- Keep `mode=source` and `mode=content` output concise. A match should give enough information to jump to a bounded read or `inspect`, not dump large text.
- Update `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` whenever public tool guidance changes; `AgentInstructionsTests` should pin it.
- Update `CLAUDE.md` first if any generated `AGENTS.md` guidance changes, then run `scripts/sync-agents.sh`.
- Do not publish, release, or push without explicit user approval.
