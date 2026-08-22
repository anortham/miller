# Refresh Wait JSON v1

Status: active Miller CLI JSON contract.

Command:

```bash
miller refresh --json --wait [--workspace-id SELECTOR] [--workspace DIR] [--full]
```

This is the Eros-friendly top-level alias for registered-workspace convergence. The Miller CLI refresh path is
synchronous: it returns after the refresh attempt finishes, observes another writer, or reports an operational
failure. The `--wait` flag is retained as a contract marker for Eros and scripts.

## Top-level shape

```json
{
  "operation": "refresh",
  "workspace_id": "...",
  "root": "...",
  "status": "refreshed",
  "scanned": true,
  "swapped": false,
  "revision": 123,
  "artifact_id": "artifact-1782961603875467000",
  "scan_duration_ms": 100,
  "duration_ms": 125,
  "index_fresh": true,
  "note": null,
  "search_sidecar": {},
  "content_corpus": {},
  "sidecars": {
    "target_sequence": 123,
    "did_work": true,
    "pending": false,
    "leader_required": false,
    "reason": null,
    "content": {},
    "search": {},
    "vector": {}
  }
}
```

## Required fields

- `operation`: `refresh` or `full`.
- `workspace_id`: stable Miller workspace ID, or `null` when unavailable.
- `root`: workspace root, or `null` when unavailable.
- `status`: status string. Expected values include `refreshed`, `unchanged`, `lock_busy`, `missing_root`,
  `missing_index`, `failed`, and `ineligible_extractor`.
- `scanned`: whether this process ran a julie-extract scan.
- `swapped`: whether the served in-memory index swapped to a newer artifact.
- `revision`: revision now reflected or last known revision.
- `artifact_id`: workspace artifact generation id when known, or `null` when unavailable. Eros CT persists this
  for Miller `impact --from-index-revision` + `--from-artifact-id` delta calls.
- `scan_duration_ms`: scan duration in milliseconds, or `null` when no scan ran or the path does not measure it.
- `duration_ms`: total refresh attempt duration in milliseconds, or `null` when unmeasured.
- `index_fresh`: `true`, `false`, or `null` when freshness is not known.
- `note`: diagnostic note, or `null`.
- `search_sidecar`: search sidecar facts, or `null`.
- `content_corpus`: content corpus sidecar facts, or `null`.
- `sidecars`: optional bounded convergence evidence. It is omitted when no convergence result exists, preserving
  legacy output bytes. `target_sequence` is the exact store sequence inspected; `did_work`, `pending`, and
  `leader_required` are aggregate booleans; `reason` is bounded to 240 characters. `content`, `search`, and `vector`
  each contain `status`, `did_work`, `pending`, `leader_required`, and a bounded nullable `reason`. Status values
  are `disabled`, `current`, `repaired`, `queued`, `leader_required`, or `failed`. `queued` means a resident
  leader has accepted work; `leader_required` means this process cannot perform it and a resident Miller leader
  must be opened or kept running; `failed` names the bounded failure and leaves bounded retry posture visible.

`content_corpus` uses the same shape as `workspace status --json` `index.content_corpus`.

## Exit code contract

Exit `0` means the JSON payload is ingestable. A `lock_busy` payload is still ingestable but does not prove
freshness; consumers that require confirmed freshness must check `status` for `refreshed` or `unchanged`, or
check `index_fresh: true`.

Exit `3` is reserved for unusable-index outcomes such as `missing_root`, `missing_index`, `failed`, and
`ineligible_extractor`. Exit `2` is a usage or selector error.

## Eros CT fields

Eros CT may depend on:

- `revision`
- `artifact_id`
- `status`
- `index_fresh`
- `content_corpus.state`

Those field names are part of this v1 contract. Additive fields are allowed. Removing or renaming these fields
requires a new contract version.
