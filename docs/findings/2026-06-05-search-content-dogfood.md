# Search And Content Dogfood

- **Date:** 2026-06-05
- **Workspace:** `/Users/murphy/source/miller`
- **Miller server:** `0.1.0+27c301040bc5`
- **Index state:** 5,988 symbols, revision 329, fresh
- **Decision:** do not widen the symbol projection before beta solely to cover prose, doc comments, literals, or docs. Keep the beta split explicit: symbol search for code symbols and file/path intent, `mode=content` for source/docs/prose text snippets, and `regions=` for opt-in scoped comment/doc-comment/string-literal search.

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

`mode=content` returned useful `path:line` snippets from design docs, findings, `AGENTS.md` / `CLAUDE.md`, `MILLER_AGENT_INSTRUCTIONS.md`, and source files. It is the right beta surface for documentation, prose, env-var explanation, and code text that is not a symbol name.

Current limits:

- Content search returns file snippets, not symbols. It is not a replacement for `inspect` or symbol search.
- `exclude_tests` is intentionally a no-op for content search today.
- Content search is in-memory and loaded from disk through the files manifest; it is not a persisted FTS sidecar.
- For comment/doc-comment/string-literal-only queries, use `regions=`. Region search remains opt-in for beta via `MILLER_REGION_INDEX=1`.

## Beta Routing

- Symbol search projection should stay `name + signature` for beta.
- Do not add `symbols.doc_comment`, `identifiers.code_context`, or `literals.literal_text` to symbol ranking before beta without fresh evidence that `mode=content` and `regions=` fail real workflows.
- Document the three search surfaces in README / CLI docs before beta:
  - `search` / `mode=symbol`: where is the code symbol?
  - `mode=content`: where does this text appear in files/docs?
  - `regions=comment|doc_comment|string_literal`: where does this text appear in scoped source regions?
