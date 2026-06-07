# Content corpus FTS5 baseline - 2026-06-07

Phase 0 measurement for `docs/plans/2026-06-07-content-corpus-fts5-search-plan.md`.

This is spike evidence, not production behavior. The spike change is intentionally limited to
`spike/SearchProjection.Spike/Program.cs` plus this report and the v1 contract doc.

## Command

Built once:

```bash
dotnet build spike/SearchProjection.Spike/SearchProjection.Spike.csproj -c Release
```

Measured with:

```bash
dotnet run -c Release --no-build --project spike/SearchProjection.Spike/SearchProjection.Spike.csproj -- \
  --db <workspace>/.miller/symbols.db \
  --content-scope all \
  --repetitions 5 \
  --limit 5 \
  --queries <six comma-separated queries>
```

The spike used BLAKE3 verification, a 1 MiB per-file cap, 160-line chunks, and 20-line overlap.

## Corpus and FTS metrics

Normal FTS5 used `unicode61 remove_diacritics 0`. Trigram output still exists in the spike as a report-only
comparison path, but the approved production design starts with word FTS only.

| Workspace | DB | Sources | Chunks | Source lines | Raw text | Indexed chunk text | Corpus build | FTS build | FTS DB | Query p50 | Query p95 | Command wall |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Miller worktree | `/Users/murphy/source/miller/.worktrees/content-corpus-fts5/.miller/symbols.db` | 452 | 834 | 85,766 | 4.0 MB | 4.3 MB | 30.8 ms | 55.8 ms | 8.2 MB | 0.488 ms | 0.848 ms | 1,670 ms |
| Julie | `/Users/murphy/source/julie/.miller/symbols.db` | 1,559 | 3,914 | 449,844 | 16.1 MB | 17.8 MB | 121.3 ms | 251.7 ms | 30.4 MB | 1.335 ms | 3.069 ms | 5,156 ms |
| julie-extractors | `/Users/murphy/source/julie-extractors/.miller/symbols.db` | 1,056 | 2,345 | 265,731 | 8.1 MB | 8.9 MB | 65.6 ms | 87.9 ms | 14.9 MB | 0.639 ms | 1.542 ms | 2,576 ms |

Interpretation:

- The normal FTS DB size is about 1.7x to 2.1x raw text for these repos.
- Query latency is comfortably below the current Miller tool-call overhead at this size.
- Build cost is low enough for an opt-in sidecar path and plausible for refresh convergence, but Phase 1 still needs atomic writer and incremental update tests.
- Chunk overlap adds 7 percent to 11 percent stored raw text on these repositories.

## Query classes

The run covered the required classes with real strings from the measured repositories.

| Class | Miller query | Julie query | julie-extractors query |
|---|---|---|---|
| Error string | `hosted-service constructor` | `workspace path does not exist` | `too many SQL variables` |
| Env var/config key | `MILLER_SEARCH_SIDECAR` | `JULIE_WORKSPACE` | `JULIE_EXTRACT` |
| Route/path literal | `.miller/search.db` | `$JULIE_HOME/indexes` | `/api/messages/active` |
| Assertion text | `Assert.Equal` | `assert_eq` | `assert_eq` |
| Natural-language implementation phrase | `content corpus` | `find this bug pattern elsewhere` | `source_regions` |
| Docs phrase | `Language parity` | `exact substring` | `cargo xtask test default` |

## Representative top hits

### Miller worktree

- `MILLER_SEARCH_SIDECAR`: `docs/plans/2026-06-07-incremental-search-sidecar.md:1-77`
- `hosted-service constructor`: `tests/Miller.Tests/Server/HostStartupRegistrationTests.cs:1-46`
- `.miller/search.db`: `docs/plans/2026-06-07-content-corpus-fts5-search-plan.md:1-160`
- `Assert.Equal`: `tests/Miller.Tests/SearchQuality/SearchQualityParsersTests.cs:1-143`
- `content corpus`: `docs/plans/2026-06-07-content-corpus-fts5-search-plan.md:1-160`
- `Language parity`: `docs/plans/2026-05-30-live-test-engine-design.md:141-286`

### Julie

- `JULIE_WORKSPACE`: `crates/julie-runtime/src/workspace/mod.rs:141-300`
- `workspace path does not exist`: `src/tests/cli_tests.rs:141-219`
- `$JULIE_HOME/indexes`: `docs/WORKSPACE_ARCHITECTURE.md:141-178`
- `assert_eq`: `crates/julie-core/src/test_support/db/tests.rs:141-193`
- `find this bug pattern elsewhere`: `TODO.md:1-12`
- `exact substring`: `src/tests/utils/exact_match_boost/tests.rs:1-160`

### julie-extractors

- `too many SQL variables`: `docs/release-evidence/2026-06-02-scan-report-profiling.md:1-32`
- `JULIE_EXTRACT`: `docs/plans/2026-05-31-migration-inventory.md:141-266`
- `/api/messages/active`: `crates/julie-extract-cli/tests/operations_contract.rs:1121-1280`
- `assert_eq`: `crates/julie-extract-artifact/tests/writer_contract.rs:1-160`
- `source_regions`: `docs/plans/2026-06-03-source-regions.md:141-300`
- `cargo xtask test default`: `docs/plans/2026-05-31-julie-code-migration-implementation-plan.md:421-452`

## Product conclusions

- Error/config/path/assertion queries are the strongest immediate justification for workspace source text search.
- Returning line ranges and snippets from chunk hits is mandatory. The top hits are useful because they include enough surrounding text to decide whether to inspect the file.
- Whole-file documents are too coarse for source search. The chunked run keeps result line ranges bounded while preserving enough surrounding context.
- `mode=content` should remain docs/config until Phase 2 migration. The baseline supports adding `mode=source` first without changing default symbol search.
- The v1 contract should store raw chunk text. Reopening files would not cover external/web imports and would make Eros export less deterministic.

## TDD note

This phase is a planned spike/prototype exception to production TDD. No production behavior changed. The next
production phase should start with failing tests around the content DB schema, writer, reader, search mode routing,
and stale/corrupt sidecar behavior.
