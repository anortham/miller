# Large-Repo Read-Path Dogfood

- **Date:** 2026-06-05
- **Workspace used for scale evidence:** `/Users/murphy/source/openclaw`
- **OpenClaw index:** 640,317 symbols, 12,781 files, revision 1
- **OpenClaw DB sizes:** `symbols.db` 1.7G, `search.db` 664M
- **Decision:** beta read paths are acceptable with a fresh sidecar. MCP summary `inspect`, full `inspect`, `context`, workspace `status`, and workspace `list` stay cheap on a very large registered workspace once the relevant projections/full index are cached. One-shot CLI `search`, `inspect`, graph-only `context`, `impact`, and non-bridge `trace` now avoid both full graph/bridge hydration and eager sidecar snapshot loading. Bridge trace still uses the full bridge graph.

## Setup

After the v4 `search.db` schema bump, OpenClaw's existing sidecar was stale. A symbol search with `ensure_fresh=false` correctly rejected the stale sidecar and self-healed to memory:

```text
search "workspace status" workspace_id=openclaw ensure_fresh=false
```

Telemetry:

```text
duration_ms=3066 metadata_json={"search_backend":"memory"}
```

The fallback was correct but expensive because it loaded the symbol projection from the 1.7G `symbols.db`.

An explicit refresh rebuilt the v4 sidecar:

```text
workspace refresh openclaw
status: unchanged
revision: 1
duration_ms=16406
```

Sidecar verification:

```text
meta: revision=1 schema_version=4 doc_count=640317
symbols_trigram rows with qual_collapsed: 640309
```

## Read-Path Results

After the v4 sidecar rebuild:

| Operation | Evidence | Result |
|---|---:|---|
| Cold disk-backed symbol search | `duration_ms=1730`, `search_backend=disk` | Acceptable cold sidecar open cost |
| Warm disk-backed symbol search | `duration_ms=76`, `search_backend=disk` | Good |
| Warm phrase symbol search | `duration_ms=58`, `search_backend=disk` | Good |
| Content search | `duration_ms=6` | Good |
| Summary inspect by symbol id | `duration_ms=4` | Good |
| Context bundle | `duration_ms=21` | Good |
| Workspace status | `duration_ms=81` | Good |
| Workspace list | `duration_ms=2` | Good |
| Full inspect by symbol id | `duration_ms=8373` | Superseded by projection routing below |

Representative calls:

```text
search "workspace status" workspace_id=openclaw ensure_fresh=false
search "doctor workspace status" workspace_id=openclaw ensure_fresh=false
search "gateway health checks" mode=content workspace_id=openclaw ensure_fresh=false
inspect d89f2940bee8dbd0887e8e653c76f1b6 depth=summary workspace_id=openclaw ensure_fresh=false
inspect d89f2940bee8dbd0887e8e653c76f1b6 depth=full workspace_id=openclaw ensure_fresh=false
context "OpenClaw doctor workspace status flow" entry_symbols=d89f2940bee8dbd0887e8e653c76f1b6 workspace_id=openclaw ensure_fresh=false
workspace status openclaw
workspace list
```

## CLI Rerun After Search-Quality Fixes

After the compact-output/search-quality fixes, the CLI read path exposed two separate issues:

- `miller search` and default `miller inspect` still used `RepositoryIndexLoader.Load`, so one-shot CLI calls paid the full graph/bridge load cost even when they only needed symbol lookup.
- The disk sidecar reader eagerly materialized every `search_symbols` row into `SymbolLookupTables` at open time. On OpenClaw, `SELECT ... FROM search_symbols ORDER BY path, start_line, symbol_id` alone took `real=1.82s`, while FTS candidate lookup plus candidate metadata fetch was tens of milliseconds.

The fix has two parts: route the lightweight CLI commands through symbol lookup, then make `FtsSymbolSearchIndex` lazy. It now opens by reading/validating only metadata and FTS tables, fetches FTS candidates plus metadata on demand, and uses small lazy path lookups for file-mode/extension checks.

Measured from `/Users/murphy/source/openclaw` with the Release CLI:

| Operation | Before | After | Result |
|---|---:|---:|---|
| `miller search "workspace status"` | `real=6.55s` | `real=0.26s` | Fixed: lazy disk symbol search |
| Scoped `miller search "workspace status"` | `real=6.53s` | `real=0.26s` | Fixed: lazy disk symbol search |
| `miller search "doctor-workspace-status.ts" --mode file` | not measured in first pass | `real=0.31s` | Cheap file lookup |
| `miller search "gateway health checks" --mode content` | `real=0.26s` | `real=0.32s` | Unchanged and cheap |
| `miller inspect noteWorkspaceStatus` | `real=6.57s` | `real=0.31s` | Fixed: summary inspect uses lazy symbol lookup |
| `miller inspect noteWorkspaceStatus --depth full` | not measured in first pass | `real=6.74s` | Superseded by projection routing below |
| `miller context "workspace status command"` | `real=6.67s` | `real=6.75s` | Superseded by graph-only CLI routing below |

Repeated `miller search "workspace status"` runs were stable at `real=0.26s`.

## Full Inspect Projection Routing

Follow-up on 2026-06-06 removed the repository-graph dependency from full `inspect`.
Before the first fix, `miller inspect noteWorkspaceStatus --workspace-id openclaw --depth full` returned only
the same ambiguity candidate list as summary inspect, but paid the full repository graph load first. Before the
second fix, unique-symbol full inspect still loaded the full graph only to read child symbols; refs/callers/
callees/body were already available from lookup projection plus direct SQLite readers.

Measured from `/Users/murphy/source/miller` against OpenClaw with the Release CLI:

| Operation | Before | After | Result |
|---|---:|---:|---|
| `miller inspect noteWorkspaceStatus --workspace-id openclaw --depth full` | `real=8.75s`, max RSS ~1.45G | `real=0.60s`, max RSS ~69M | Fixed: ambiguous full inspect uses symbol preflight and returns candidates without full load |
| `miller inspect runWorkspaceStatusHealth --workspace-id openclaw --depth full` | not previously measured | `real=0.38s`, max RSS ~68M | Fixed: unique-symbol full inspect renders children/callees/body without full load |

Implementation note: `ISymbolLookupIndex` now exposes direct child lookup. The in-memory projection serves it
from parent-id lookup tables, and the FTS sidecar serves it from `search_symbols.parent_symbol_id`. Full inspect
uses that child lookup plus `ExtractReader.ReadDetail` / `ReadReferences` / `ReadCallees` / `ReadBody`, so it no
longer needs `RepositoryIndexLoader.Load`.

## Graph-Only CLI Routing

The remaining one-shot CLI cost was graph-only read verbs loading the full repository projection, including the
bridge graph. The beta fix keeps graph reachability on-demand: `SqliteSymbolGraphIndex` reads `relationships`,
`identifiers`, and `symbols` from `symbols.db`, while candidate hydration uses batched sidecar symbol-id lookup
when the FTS sidecar is active. CLI `context`, `impact`, and `trace mode=auto|path` now use that path;
`trace mode=bridge` still uses the full bridge graph.

Measured from `/Users/murphy/source/miller` against OpenClaw with the Release CLI:

| Operation | Before | After | Result |
|---|---:|---:|---|
| `miller context "OpenClaw doctor workspace status flow" --workspace-id openclaw --token-budget 1800 --max-hops 1` | `real=8.35s`, max RSS ~1.52G | `real=0.77s`, max RSS ~119M | Fixed: graph-only context uses SQLite reachability |
| `miller impact runWorkspaceStatusHealth --workspace-id openclaw --max-depth 1` | `real=6.17s`, max RSS ~1.52G | `real=0.31s`, max RSS ~69M | Fixed: impact uses SQLite reverse reachability |
| `miller trace runWorkspaceStatusHealth --workspace-id openclaw --depth 1` | `real=6.32s`, max RSS ~1.52G | `real=0.32s`, max RSS ~69M | Fixed: non-bridge trace uses SQLite reachability |

This closes TODO #5 for the beta CLI path. Current evidence says do not widen symbol search and do not add
incremental in-memory patching. Reopen only if bridge trace cold-start cost becomes a real workflow bottleneck.

## Region Search Fail-Closed Check

Region search was not enabled for the default beta path. Explicit region queries failed closed instead of returning misleading symbol or content results:

```text
search "TODO" regions=comment workspace_id=openclaw ensure_fresh=false
search "TODO" regions=comment workspace_id=current ensure_fresh=false
```

Both returned:

```text
search failed: region search requires MILLER_REGION_INDEX=1 and a refreshed search sidecar.
```

## Stale Artifact Note

`/Users/murphy/source/hermes-agent` is registered as ready but its `symbols.db` predates the v2 artifact contract and lacks `artifact_metadata`. Search/inspect fail with a clear restore/scan message:

```text
DB has no 'artifact_metadata' table; it is not a julie-extract v2 artifact. Re-run restore + `scan` with the pinned julie-extract.
```

This is acceptable behavior for beta, but README/troubleshooting should mention that old registered workspaces may need `workspace refresh` or `workspace full` after the extractor contract changes.
