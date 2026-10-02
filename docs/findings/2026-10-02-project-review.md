# Miller project review — 2026-10-02

## Scope

Reviewed indexing and lifecycle safety, CLI/MCP contracts, continuous testing,
dashboard reads and mutations, plugin installation, build scripts, public setup
guidance, and the current Julie dependency. Work used the main checkout; no
release, push, or unrelated worktree changes were made.

## Findings and fixes

| Finding | Resolution |
| --- | --- |
| Miller pinned Julie 2.42.0 while the latest published release is 3.7.2. Julie 3 removed family-store and export commands. | Upgrade all four pinned assets and use the existing standalone artifact path. Refuse unsupported store opt-in and preserve imported content before retiring old pointers. See [migration instructions](../julie-3-migration.md). |
| Concurrent cold plugin launches could delete the package installed by another launcher. | Serialize promotion, check for a completed installation under the lock, and release the lock by renaming before cleanup. |
| Windows canonicalization could turn a drive root into a drive-relative path. | Preserve the root separator during the path walk. |
| Edit cleanup could delete unrelated files matching a broad temporary-file pattern. | Limit cleanup to generated temporary names for the planned target files. |
| A CT rebuild left the old cursor baseline, repeatedly reporting the same rebuild. | Persist and adopt the new generation after successful rebuild handling; retain retry behavior when handling or persistence fails. |
| Very small nonzero semantic vectors bypassed normalization validation. | Reserve the exception for exact zero vectors. |
| Watcher filtering missed basename-selected sources, including go.mod and go.sum. | Match the pinned extractor's source names and hard exclusions. |
| Dashboard and CT could present stale legacy facts or vague errors while store migration was required. | Refuse stale reads and expose actionable rebuild guidance through existing results. |
| Python proxy tests raced subprocess startup and leaked captured streams. | Synchronize startup and close streams while preserving the timeout and EOF assertions. |
| Windows PowerShell archive restoration could fail without an interactive console despite local progress suppression. | Suppress the archive module's global progress preference during extraction and restore the caller's prior value afterward. |
| Setup examples referenced old archives and omitted the semantic restore step. | Align published download examples and document both runtime restores. |

## Validation

| Check | Result |
| --- | --- |
| Release build | 0 warnings, 0 errors |
| Linux fast suite | 10,520 passed, 9 skipped, 0 failed |
| Plugin checks | 104 passed, 0 failed |
| Python script checks | 436 tests, 3 skipped, 0 failed; declared requirements installed in an isolated venv |
| Site checks | 1 passed |
| NuGet vulnerability audit | No vulnerable packages reported across the solution, including transitive dependencies |
| Scale suite | 208 passed, 64 skipped, 0 failed |
| Windows verification | Clean commit `e725b252` synced; native verification in progress |
| Windows plugin checks | 97 passed, 7 platform skips, 0 failed |

The pinned runtime extracted the upstream `fixtures/extraction` corpus without
modifying its source: 975 scanned files, 974 indexed, one unsupported
`.gitattributes`, and zero failed files. All 42 capability languages have indexed
files and structural facts. The extract contains 659,696 symbols and 701,562
structural facts. `gosum` intentionally has no symbols and 11 checksum facts.
The [grouped SQL coverage matrix](2026-10-02-julie-language-coverage.tsv) records
file outcomes, symbol kinds, and structural-fact patterns per language.

The [live smoke results](2026-10-02-project-review-smoke.json) cover a real
Julie 2.42.0 family store with imported content and a deliberately stale local
artifact. Migration rebuilt current symbols, retained the imported document,
removed the pointer and exact registry membership, and left the recorded family
files unchanged (SQLite shared-memory files excluded). Fresh CLI checks cover
capabilities, workspace open, search, inspect, context, trace, impact, patterns,
report, symbol export, and CT-off status. MCP initialization, tool discovery,
workspace open, and explicitly scoped search also passed.

Dashboard HTTP checks passed for list/detail pages and JSON projections while
a store pointer blocks standalone reads. They show `source_rebuild_required`
and the Refresh index action, without serving stale legacy details.

## Upgrade boundary

Stop and restart older Miller MCP servers and CT daemons when switching builds.
Migration does not support simultaneous older family-store writers. Existing
shared family data is retained; this review does not publish new release archives.
