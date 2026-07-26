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
  "indexer_leader": {},
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

`indexer_leader` (or `null` when leader facts were not gathered):

- `this_process`: whether the responding process is the workspace's indexer leader.
- `pid`, `version`, `process_path`, `started_at`, `extractor_version`: the recorded leader identity, each
  `null` when no identity is recorded.
- `alive`: liveness of the recorded leader process, or `null` without an identity.
- `own_extractor_version`: the responding process's bundled extractor version. The one-shot CLI reports
  `null` because it does not probe or launch the extractor for a read command.
- `artifact_extractor_version`: the extractor version recorded in the index artifact, or `null`.
- `own_eligibility`: `{ "eligible": bool, "reason": string }` for a live server process, or `null` when the
  caller did not gather its leadership verdict.

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
- `vectors`: vector sidecar facts. Present only when semantic retrieval is enabled (see below).

`content_corpus` when present:

- `state`: sidecar state, for example `current`, `imports_only`, `preservation_blocked`, `missing`, `stale`,
  `disabled`, or `error`. `preservation_blocked` means a rebuild refused to discard imported content.
- `path`: path to `.miller/content.db`, or `null`.
- `schema_version`: content corpus schema version, or `null`.
- `workspace_revision`: workspace revision the sidecar reflects, or `null` for an imports-only corpus.
- `source_count`, `chunk_count`, `indexed_source_bytes`, `stored_raw_bytes`: corpus counts.
- `status_skipped`, `scope_skipped`, `too_large_skipped`, `missing_skipped`, `hash_mismatch_skipped`,
  `non_utf8_skipped`, `io_skipped`: skipped-source counters.
- `error`: sidecar error text, or `null`.

`vectors` (additive, present only when semantic retrieval is enabled):

- Omitted entirely when `MILLER_SEMANTIC` is off or the sidecar is absent, so existing consumers see an
  unchanged document until the operator opts in.
- `state`: vectors-v1 status vocabulary — `ready`, `building`, `unavailable`, `incompatible`, `circuit-open`,
  `disk-blocked`, or `downloading`.
- `path`: path to the generation being reported, or `null`.
- `reason`: stated reason for a non-`ready` state, or `null`.
- `build_progress_percent`: 0-100 while building, else `null`.
- `serving_tag`, `serving_role`: the generation answering queries and whether it is the `active` artifact or a
  `retained` one. Both `null` when nothing is queryable.
- `artifact_id`: the `symbols.db` artifact this generation was built from, or `null`.
- `symbol_cursor`, `chunk_cursor`: `{completed_revision, target_revision, pending_files, last_error,
  last_error_at}`. `pending_files` is `null` when the extract delta journal cannot reconstruct the span.
- `identity`: the five generation-identity fields — `encoder_fingerprint`, `storage_schema`,
  `corpus_generation`, `writer_version`, `min_reader_version`, `fusion_profile`.
- `retained_generations`: array of `{tag, path}` for superseded generations still on disk.

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
