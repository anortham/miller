# Workspace Status JSON v1

Status: active Miller CLI JSON contract.

Command:

```bash
miller workspace status --json [--workspace-id SELECTOR] [--workspace DIR]
```

This contract reports the currently readable workspace index and sidecar state. It is intended for Eros
continuous testing, launcher smokes, fleet inventory, and local readiness checks.

## Top-level shape

```json
{
  "workspace": {},
  "indexer_leader": null,
  "index": {},
  "telemetry": {}
}
```

## Required fields

`workspace`:

- `root`: canonical workspace root path.
- `workspace_id`: stable Miller workspace ID, or `null` before bootstrap/registration.
- `display_id`: human display ID, or `null` when unavailable.
- `db`: path to `.miller/symbols.db`.
- `leader`: whether this process is the indexer leader.
- `role`: renderable role label, such as `leader`, `reader`, or an ineligibility-qualified reader role.
- `server_version`: Miller build version, or `null`.
- `server_pid`: process ID, or `null`.

`index`:

- `document_count`: number of symbols in the loaded index.
- `known_extensions`: number of known extracted extensions.
- `built_revision`: revision used to build the in-memory index.
- `latest_revision`: latest readable extract revision observed on disk.
- `artifact_id`: current extract artifact generation ID, or `null` when unavailable. Pair this with
  `latest_revision` when calling `impact --from-index-revision`.
- `index_fresh`: `true`, `false`, or `null` when freshness is not known.
- `freshness_status`: stable status string such as `ready`, `stale`, or `missing`.
- `warning`: warning text, or `null`.
- `queue_empty`: whether the leader work queue is empty from this process's perspective.
- `search_sidecar`: search sidecar facts, or `null`.
- `content_corpus`: content corpus sidecar facts, or `null`.

`content_corpus` when present:

- `state`: sidecar state, for example `current`, `missing`, `stale`, `disabled`, or `error`.
- `path`: path to `.miller/content.db`, or `null`.
- `schema_version`: content corpus schema version, or `null`.
- `workspace_revision`: workspace revision the sidecar reflects, or `null`.
- `source_count`, `chunk_count`, `indexed_source_bytes`, `stored_raw_bytes`: corpus counts.
- `status_skipped`, `scope_skipped`, `too_large_skipped`, `missing_skipped`, `hash_mismatch_skipped`,
  `non_utf8_skipped`, `io_skipped`: skipped-source counters.
- `error`: sidecar error text, or `null`.

`telemetry` uses the same nested object shape as Miller's telemetry summary renderer.

## Eros CT fields

Eros CT may depend on:

- `index.built_revision`
- `index.latest_revision`
- `index.artifact_id`
- `index.freshness_status`
- `index.queue_empty`
- `index.content_corpus.state`

Those field names are part of this v1 contract. Additive fields are allowed. Removing or renaming these fields
requires a new contract version.
