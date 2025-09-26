# Miller Project Review (September 26, 2025)

## Overview
- Bun-based MCP server (`src/mcp-server.ts`) fronts an `EnhancedCodeIntelligenceEngine` that orchestrates Tree-sitter parsing, SQLite storage, fuzzy search (MiniSearch), and optional semantic embeddings (Vectra TF-IDF fallback).
- Code intelligence pipeline: file discovery → extractor-per-language symbol harvesting → SQLite persistence (`src/database/schema.ts`) → search indexing (`src/search/search-engine.ts`) and watcher-driven delta updates (`src/watcher/file-watcher.ts`).
- Semantic layer is intended to stream embeddings into a Vectra index (`src/embeddings/vectra-vector-store.ts`) and expose hybrid search via `HybridSearchEngine`.
- Tooling includes MCP tools for navigation/search plus a diff-match-patch editing utility (`src/tools/edit-tool.ts`) and a context extractor for summarisation.

## Strengths
- Comprehensive extractor coverage with consistent base APIs (`src/extractors`) and sensible fallbacks for parser failures allow multi-language support to scale.
- Database schema leverages FTS5 and clear separation of symbols, relationships, types, bindings, and files for incremental updates (`src/database/schema.ts`).
- File watcher debounce and hash-based delta logic guard against noisy rebuilds and throttled reindexing (`src/watcher/file-watcher.ts`, `src/engine/enhanced-code-intelligence.ts`).
- Search engine couples MiniSearch fuzzy querying with ripgrep fallback, providing both speed and precision (`src/search/search-engine.ts`).

## High Severity Findings
- **Crash on headless runtimes** (`src/engine/enhanced-code-intelligence.ts:139`, `src/workers/embedding-worker-pool.ts:67`, `src/workers/embedding-process-pool.ts:79`): `navigator` is accessed directly; in Bun/Node there is no global `navigator`, so the constructor throws `ReferenceError` before the engine starts. While optional chaining is used, referencing an undefined identifier still faults. *Recommendation:* Gate the lookup via `typeof navigator !== 'undefined'` or fall back to `Bun.os.cpus().length` / `require('os').cpus().length`.
- **Semantic store loses symbol linkage** (`src/embeddings/vectra-vector-store.ts:159-199`): The Vectra adapter queries a non-existent `type` column (should be `kind`) and coerces string symbol IDs with `parseInt`. Most IDs are md5 hashes, so `parseInt` yields `NaN`, which is then written into metadata and the `symbol_id_mapping` table. Result: stored embeddings cannot be rejoined with symbols, hybrid search returns empty results, and SQLite emits errors. *Recommendation:* Keep symbol IDs as strings end-to-end, fix the column name to `kind`, and drop the integer mapping unless a numeric surrogate key is actually maintained.
- **Scoped ripgrep search always misses** (`src/search/search-engine.ts:284-305`): When callers set `options.path`, the code passes that path both as the ripgrep search root *and* as `cwd`. For a path like `src`, ripgrep is invoked from `src/` searching inside `src/`, effectively targeting `src/src` (which does not exist). The result is silent zero matches and fallback to the slower LIKE query. *Recommendation:* Keep `cwd` at the workspace root and supply an absolute/relative search path argument, or omit the extra argument and rely solely on `cwd`.
- **Type metadata mislabeled as TypeScript** (`src/engine/enhanced-code-intelligence.ts:771-778`): `storeExtractionResults` stamps every inferred type row with `'typescript'`, even when the extractor is Python, Go, etc. Hover and cross-language queries will surface incorrect typing data. *Recommendation:* propagate the extractor language when recording types (e.g., maintain a map of symbolId → language from the extractor output).

## Medium Severity Findings
- **Semantic indexing hard-caps at 500 symbols** (`src/engine/enhanced-code-intelligence.ts:409-415`): Large workspaces will never push more than 500 embeddings, so semantic search quality degrades sharply. Consider batching with OFFSET/PAGE or switching to an "unprocessed" flag in the DB.
- **Embeddings ignore most symbols** (`src/engine/enhanced-code-intelligence.ts:409-415`): The filter requires `signature` or `doc_comment`, excluding typical functions/classes without either—especially in dynamic languages. Expand the criteria or synthesise content from names/bodies.
- **Workspace stats never update** (`src/engine/enhanced-code-intelligence.ts:954-976`): The enhanced engine never calls `CodeIntelDB.updateWorkspaceStats`, so MCP `index_workspace(list)` responses report zero symbols/files. Reintroduce the update after indexing completes.
- **Tests are non-portable diagnostics** (`src/__tests__/engine/file-discovery-debug.test.ts:19-75`, `src/__tests__/engine/production-parser-debug.test.ts:10-82`): Hard-coded absolute paths (`/Users/murphy/...`) and console-driven assertions mean the suite cannot run in CI. Elevate these to real fixtures or remove from automated runs.

## Low Severity & Observations
- Extensive console logging (e.g. `console.log` in `src/engine/enhanced-code-intelligence.ts:984-1015`) pollutes MCP stdio streams; route everything through the structured logger.
- `startSemanticIndexing` computes `const priorityBatches = ...` but never consumes it—dead code that obscures intent.
- `TFIDFEmbedder` max features default (1000) may under-represent larger corpora; surface this in config if semantics remain TF-IDF-based for now.

## Testing & Tooling Notes
- No automated integration tests cover the MCP entrypoints; most existing tests focus on one component and depend on direct SQLite access. Add smoke tests that spin up the MCP server, run `index_workspace`, and exercise `explore`/`navigate` tools.
- Ripgrep availability is assumed; when absent, we silently fall back to SQL LIKE. It would help to expose a health check item showing whether ripgrep is active.

## Suggested Next Steps
1. Address the high-severity defects (navigator access, Vectra ID handling, ripgrep pathing, type language attribution) before declaring semantic features GA.
2. Rework semantic indexing batching and broaden symbol eligibility so hybrid search scales beyond trivial projects.
3. Replace the debugger-style tests with portable fixtures and add automated MCP smoke coverage to guard regressions.
