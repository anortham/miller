# Post-Beta Julie Vs Miller Search Quality Rerun

- **Date:** 2026-06-06
- **Purpose:** Re-run the Julie-vs-Miller search quality check after publishing `v0.1.0-beta.1`, using
  current Miller and current Julie behavior.
- **Miller binary:** `/Users/murphy/source/miller/src/Miller.Server/bin/Release/net10.0/miller`
  reported `0.1.0+76b272a1abe4`.
- **Julie binary:** `/Users/murphy/source/julie/target/debug/julie-server` reported `julie-server 7.13.3`.
- **Repos sampled:** `/Users/murphy/source/julie`, `/Users/murphy/source/express`,
  `/Users/murphy/source/flask`.

## Method

The structured Julie `cargo xtask search-matrix baseline --profile smoke` path could not be used directly
because the active Julie daemon registry did not have ready matrix workspaces, and Julie's standalone CLI
intentionally refuses registry operations such as `workspace open`.

The comparison therefore used direct standalone Julie searches and Miller CLI searches over the same repos:

```bash
/Users/murphy/source/julie/target/debug/julie-server \
  --workspace <repo> --standalone --json search <query> --limit 5

/Users/murphy/source/miller/src/Miller.Server/bin/Release/net10.0/miller \
  search <query> --workspace <repo> --mode <auto|file|content> --limit 5 --json
```

Miller's registered indexes for `julie`, `express`, and `flask` were refreshed to the current schema before
the comparison. Existing generated `.miller` and `.julie` directories were already present in those checkouts.

## Result

After the fixes below, Miller is at least as good as Julie on the current smoke sample: 8 scored rows pass,
1 stale row is skipped, and 1 passing row still has a useful Julie UX hint Miller does not yet mirror.

| Case | Query | Julie first signal | Miller first signal | Score |
| --- | --- | --- | --- | --- |
| Stale historical identifier | `WorkspacePool` | `docs/plans/2026-05-06-daemon-windows-lifecycle.md:160` | `fixtures/search-quality/search-matrix-cases.toml:4` | Skip. The old Julie matrix expectation is stale on current Julie. |
| Exact symbol | `FastSearchTool` | `Definition found: FastSearchTool` | `FastSearchTool struct crates/julie-tools/src/search/mod.rs:58` | Pass. |
| Snake-case symbol | `line_matches` | `Definition found: line_matches` | `line_matches function crates/julie-tools/src/search/query.rs:219` | Pass. |
| Scoped content miss | `line_matches`, `file_pattern=src/ui/**` | `NOTE: 0 matches within file_pattern=src/ui/**. Showing 2 results from the full codebase...` | `No results` | Pass, but Julie is more helpful for agent recovery. |
| Exact file path | `crates/julie-tools/src/search/mod.rs` | `Definition found: crates/julie-tools/src/search/mod.rs` | `backend namespace crates/julie-tools/src/search/mod.rs:23` | Pass for lookup; JSON still uses symbol rows, not file rows. |
| Basename file lookup | `mod.rs` | `Definition found: mod.rs` | `analysis namespace src/tests/mod.rs:12` | Pass for lookup; JSON still uses symbol rows. |
| Docs/prose | `line mode scoped filter miss fixed` | `5 matches for "line mode scoped filter miss fixed"` | `content docs/release-notes/v7.13.1.md:55` | Pass. |
| Express quoted content | `"router use"` | `5 matches for ""router use""` | `content History.md:193` | Pass. |
| Express file lookup | `index.js` | `Definition found: index.js` | `exports.index function examples/mvc/controllers/main/index.js:3` | Pass for lookup; JSON still uses symbol rows. |
| Flask exclude-tests | `Flask --exclude-tests` | `Definition found: Flask` | `Flask class src/flask/app.py:109` | Pass after fixes. |

## Fixes Applied

1. **CLI parity:** `miller search` now accepts `--exclude-tests`, not only `--include-tests`.
   This exposes the forced test-exclusion path that already existed in `SearchTool.Run`.
   Regression: `CliDispatchTests.Search_ExcludeTestsFlag_FiltersExactIdentifierTestHits`.

2. **Exact definition ranking:** exact-name concrete definitions now get a small shared BM25 boost after
   the exact-name boost. This keeps a class/function/type first when it ties with same-name manifest/config
   properties, while keeping those property rows visible.
   Regressions:
   - `MillerSearchIndexTests.Search_ExactNameConcreteDefinitionOutranksManifestProperty`
   - `FtsSymbolSearchIndexTests.Search_ExactNameConcreteDefinitionOutranksManifestProperty_ParityWithInMemory`

## Remaining Work

1. **Add a Miller-native search matrix runner.** Julie's matrix is useful, but Miller should own a repeatable
   scorecard that can run direct Miller and direct Julie comparisons without relying on Julie daemon registry
   state. It should support stale-case marking, expected first signals, top-k relevance, latency, and JSON output.

2. **Update copied Julie cases before reusing them.** `WorkspacePool` is stale on current Julie; use a current
   exact-symbol case or keep it marked as historical regression coverage only.

3. **Decide scoped-miss UX.** Julie's out-of-scope hint is helpful: it says no in-scope result exists and shows
   outside-scope fallbacks. Miller's strict `No results` is correct but less useful. If agents keep hitting this,
   add an explicit out-of-scope hint instead of silently broadening filtered search.

4. **Decide JSON file-level result objects.** Compact file mode already gives Julie-like file signals. JSON still
   returns symbol rows from matching files for compatibility. If scorecards judge file lookup on structured JSON,
   add an explicit file-result shape or a versioned result kind.

## Verification

```text
dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~Search_ExcludeTestsFlag_FiltersExactIdentifierTestHits --no-restore
Passed: 1

dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~Search_ExactNameConcreteDefinitionOutranksManifestProperty --no-restore
Passed: 2

dotnet build Miller.slnx -c Release --no-restore
Build succeeded. 0 warnings, 0 errors.

miller search Flask --workspace /Users/murphy/source/flask --mode auto --limit 8 --json --exclude-tests
rank 1: Flask class src/flask/app.py:109
rank 2: flask property pyproject.toml:83
```
