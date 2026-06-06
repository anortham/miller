# Large-Repo Read-Path Dogfood

- **Date:** 2026-06-05
- **Workspace used for scale evidence:** `/Users/murphy/source/openclaw`
- **OpenClaw index:** 640,317 symbols, 12,781 files, revision 1
- **OpenClaw DB sizes:** `symbols.db` 1.7G, `search.db` 664M
- **Decision:** beta read paths are acceptable with a fresh sidecar. MCP summary `inspect`, `context`, workspace `status`, and workspace `list` stay cheap on a very large registered workspace once the relevant projections/full index are cached. One-shot CLI `search` and summary `inspect` now avoid both full graph hydration and eager sidecar snapshot loading. Full `inspect` and graph-heavy one-shot CLI commands can still be expensive and should be documented as heavier than summary inspect/search.

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
| Full inspect by symbol id | `duration_ms=8373` | Expensive; document as heavier than summary inspect |

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
| `miller inspect noteWorkspaceStatus --depth full` | not measured in first pass | `real=6.74s` | Expected full path |
| `miller context "workspace status command"` | `real=6.67s` | `real=6.75s` | Expected full path |

Repeated `miller search "workspace status"` runs were stable at `real=0.26s`. The remaining expensive one-shot CLI paths are graph-heavy by design. If post-beta CLI workflows need faster full `inspect` / `context`, evaluate lazy graph/bridge loading or persisted read models there, not symbol-search widening or per-file in-memory patching first.

## Full Inspect Ambiguity Preflight

Follow-up on 2026-06-06 split ambiguous `depth=full` inspect from genuinely full symbol inspection.
Before the fix, `miller inspect noteWorkspaceStatus --workspace-id openclaw --depth full` returned only
the same ambiguity candidate list as summary inspect, but paid the full repository graph load first.

Measured from `/Users/murphy/source/miller` against OpenClaw with the Release CLI:

| Operation | Before | After | Result |
|---|---:|---:|---|
| `miller inspect noteWorkspaceStatus --workspace-id openclaw --depth full` | `real=8.75s`, max RSS ~1.45G | `real=0.60s`, max RSS ~69M | Fixed: ambiguous full inspect uses symbol preflight and returns candidates without full load |

The full path still loads the repository graph after the cheap preflight when the target resolves to a
single symbol. `context`, `impact`, `trace`, and unique-symbol full `inspect` remain the follow-up surface
for lazy graph/bridge or persisted read-model work.

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
