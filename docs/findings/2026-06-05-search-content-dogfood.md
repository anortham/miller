# Search And Content Dogfood

- **Date:** 2026-06-05
- **Workspace:** `/Users/murphy/source/miller`
- **Miller server:** `0.1.0+27c301040bc5`
- **Index state:** 5,988 symbols, revision 329, fresh
- **Decision:** do not widen the symbol projection before beta solely to cover prose, doc comments, literals, or docs. Keep the beta split explicit: symbol search for code symbols and file/path intent, `mode=content` for docs-like file/prose snippets, and `regions=` for opt-in scoped comment/doc-comment/string-literal search.

## Symbol Search

Representative MCP queries:

```text
search "collapsed trigram" --limit 8
```

Returned code-level hits such as `TrigramWindow`, `TrigramCandidates`, `Fts5`, `Build`, and `SearchIndexWriter.SchemaDdl`. The prior import/module noise did not recur for this natural-language phrase.

```text
search "same revision sidecar repair" --limit 8
```

Returned sidecar/routing symbols such as `TryBuildSidecar`, `_sidecar`, and `SymbolSearchSidecar`. This is acceptable for symbol intent but does not find prose comments or test names; use `mode=content` for that.

```text
search "MILLER_REGION_INDEX" --limit 8
```

Found `RegionIndexOptions.EnvVar`, `FtsRegionSearchIndex`, and related region-search symbols. Docs also appeared because markdown/code-block symbols are extracted; this is acceptable but reinforces that env-var explanation queries should use `mode=content`.

```text
search "SearchTool.cs" --limit 8
```

The restarted server still returned file import rows first because it was running before the file-mode low-signal filter patch. The working tree now fixes this: file-mode search hides `import`/`module` rows and overfetches to preserve useful file symbols.

## Content Search

Representative MCP queries:

```text
search "same revision sidecar repair" mode=content --limit 8
search "MILLER_REGION_INDEX" mode=content --limit 8
search "collapsed trigram" mode=content --limit 8
search "region search fails closed" mode=content --limit 8
```

`mode=content` returned useful `path:line` snippets from design docs, findings, `AGENTS.md` / `CLAUDE.md`, and `MILLER_AGENT_INSTRUCTIONS.md`. It is the right beta surface for documentation and prose text in docs-like files.

The first live pass exposed weak term-overlap misses: large-app queries such as `add user to organization` and `contact permissions` returned arbitrary JSON/config snippets; Julie source-comment/literal probes (`canonicalized to prevent duplicate workspace IDs`, `JULIE_EMBEDDING_HOST_SPAWN_TIMEOUT_SECS`) returned nearby docs even though the exact text lives in source comments/string literals. The local fix now makes content search more conservative:

- longer prose queries require meaningful query-term coverage before rendering;
- the selected snippet line must carry that meaningful coverage instead of terms spread across the whole file;
- token-phrase matches get a ranking boost and phrase lines are preferred for snippets;
- env-var/path/code-like content queries require the exact token phrase.

Release CLI dogfood after the fix:

```text
miller/content  "MILLER_REGION_INDEX"                 -> docs/plans/2026-06-05-source-regions-pillar3-implementation-plan.md:158 (~0.39s)
miller/content  "region search fails closed"          -> docs/plans/2026-06-05-source-regions-pillar3-implementation-plan.md:173 (~0.12s)
miller/content  "same revision sidecar repair"        -> docs/findings/2026-06-05-search-content-dogfood.md:20 (~0.11s)
julie/content   "line mode scoped filter miss fixed"  -> docs/release-notes/v7.13.1.md:55 (~0.22s)
julie/content   "where does fast search dispatch..."  -> docs/eval/semantic-value/results/2026-05-23T17-35-16Z.json:198 (~0.20s)
julie/content   "canonicalized to prevent duplicate workspace IDs" -> No results (~0.16s)
julie/content   "JULIE_EMBEDDING_HOST_SPAWN_TIMEOUT_SECS"          -> No results (~0.17s)
openclaw/content "gateway health checks"              -> docs/gateway/health.md:32 (~0.33s)
MyraNext/content "add user to organization"           -> No results (~0.10s)
MyraNext/content "contact permissions"                -> No results (~0.10s)
```

Current limits:

- Content search returns file snippets, not symbols. It is not a replacement for `inspect` or symbol search.
- `exclude_tests` is intentionally a no-op for content search today.
- Content search is in-memory and loaded from disk through the files manifest; it is not a persisted FTS sidecar.
- Content search indexes docs-like files only; source comments and string literals intentionally stay out of this projection.
- For comment/doc-comment/string-literal-only queries, use `regions=`. Region search remains opt-in for beta via `MILLER_REGION_INDEX=1`.

## Beta Routing

- Symbol search projection should stay `name + signature` for beta.
- Do not add `symbols.doc_comment`, `identifiers.code_context`, or `literals.literal_text` to symbol ranking before beta without fresh evidence that `mode=content` and `regions=` fail real workflows.
- Document the three search surfaces in README / CLI docs before beta:
  - `search` / `mode=symbol`: where is the code symbol?
  - `mode=content`: where does this text appear in files/docs?
  - `regions=comment|doc_comment|string_literal`: where does this text appear in scoped source regions?
