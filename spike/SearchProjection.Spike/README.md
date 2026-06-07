# SearchProjection.Spike

Measures the search questions in `TODO.md` before Miller commits to a runtime search architecture.

Run against any `julie-extractors` v1 SQLite artifact:

```bash
dotnet run -c Release --project spike/SearchProjection.Spike -- --db .miller/symbols.db
```

The spike also accepts legacy Julie DBs for raw projection scale measurements. In that mode it skips
`RepositoryIndexLoader.Load`, because production Miller only supports `julie-extractors` v1 artifacts.

Useful variants:

```bash
# Focus on docs/web-research-like files.
dotnet run -c Release --project spike/SearchProjection.Spike -- --db .miller/symbols.db --content-scope docs

# Measure source files plus docs/config as chunked content.
dotnet run -c Release --project spike/SearchProjection.Spike -- --db .miller/symbols.db --content-scope all --content-chunk-lines 160 --content-chunk-overlap 20

# Measure source-like files only.
dotnet run -c Release --project spike/SearchProjection.Spike -- --db .miller/symbols.db --content-scope source

# Keep the temporary SQLite FTS files for inspection.
dotnet run -c Release --project spike/SearchProjection.Spike -- --db .miller/symbols.db --keep-fts

# Run a smaller, quicker pass.
dotnet run -c Release --project spike/SearchProjection.Spike -- --db .miller/symbols.db --repetitions 5 --content-max-bytes 262144
```

The spike reports:

- Full `RepositoryIndexLoader.Load` cost.
- Current symbol projection cost (`name + signature`).
- Widened symbol projection cost (`doc_comment`, path tokens, bounded identifier context, literals).
- Content corpus read/hash/decode cost from disk through the `files` manifest.
- Content corpus source count, chunk count, source lines, raw text bytes, and indexed chunk bytes.
- In-memory BM25-style content index cost.
- SQLite FTS5 content index cost with normal and trigram tokenizers.
- Representative normal-FTS top hits for each query, including path, chunk line range, and a compact snippet.
