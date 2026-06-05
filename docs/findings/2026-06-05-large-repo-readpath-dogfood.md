# Large-Repo Read-Path Dogfood

- **Date:** 2026-06-05
- **Workspace used for scale evidence:** `/Users/murphy/source/openclaw`
- **OpenClaw index:** 640,317 symbols, 12,781 files, revision 1
- **OpenClaw DB sizes:** `symbols.db` 1.7G, `search.db` 664M
- **Decision:** beta read paths are acceptable with a fresh sidecar. Summary `inspect`, `context`, workspace `status`, and workspace `list` stay cheap on a very large registered workspace. Full `inspect` can still be expensive and should be documented as heavier than summary inspect.

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
