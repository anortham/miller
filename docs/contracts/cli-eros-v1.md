# Miller CLI/Eros contract v1

Status: active local contract for Eros-facing Miller CLI integration.

Miller is the deterministic local code-intelligence core. Eros should consume Miller through stable CLI JSON,
JSONL exports, and documented local artifacts instead of private Miller .NET types.

## Discovery

Use `miller capabilities --json` before choosing an integration path. The command does not open a workspace
index and has no side effects.

Required top-level fields:

| Field | Meaning |
|---|---|
| `miller.version` | Miller build version, including the git SHA suffix when available. |
| `julie_extract.pinned_version` | `julie-extract` product version restored/packaged with this Miller build. |
| `julie_extract.schema_version` | Expected `schema_version` in the extract artifact metadata. |
| `julie_extract.sqlite_schema_version` | Expected SQLite schema version in the extract/report contract. |
| `julie_extract.extract_contract_version` | Expected extract data contract version. |
| `julie_extract.report_schema_version` | Expected `julie-extract` report envelope version. |
| `julie_extract.hash_algorithm` | File-content hash algorithm Miller expects in `symbols.db`; currently `blake3`. |
| `artifacts.search_sidecar_schema_version` | Miller-owned `.miller/search.db` schema version. |
| `artifacts.content_corpus_schema_version` | Miller-owned `.miller/content.db` schema version. |
| `artifacts.content_corpus_chunker_version` | Chunk identity/range strategy for `content.db` export rows. |
| `optional_features.symbol_search_sidecar` | Whether this process has the search sidecar enabled. |
| `optional_features.source_region_index` | Whether this process will populate/search source-region text. |
| `optional_features.source_region_max_bytes` | Per-region byte cap used when source-region indexing is enabled. |
| `json_commands` | CLI commands with stable JSON output. |
| `json_contracts` | Versioned JSON contracts with command, schema version, and doc path. |
| `supported_export_formats` | Streaming export feeds supported by this build. |

## Stable JSON commands

Current `json_commands` include:

| Command | Purpose |
|---|---|
| `workspace status --json` | Workspace identity, index DB path, revision/freshness facts, sidecar facts, and telemetry summary. |
| `workspace health --json` | Workspace readiness verdict, warnings/actions, sidecar state, extraction-quality aggregates, and telemetry outcome counts. See [`workspace-health-v1.md`](workspace-health-v1.md). |
| `workspace list --json` | Registered workspaces from `~/.miller/workspaces.db`. |
| `workspace refresh --json` | Incremental convergence result for a registered workspace. |
| `workspace full --json` | Forced full re-index result for a registered workspace. |
| `refresh --json --wait` | Eros-friendly top-level alias for registered-workspace convergence. Accepts `--workspace-id`, `--workspace`, and `--full`; returns after the synchronous refresh attempt. |
| `workspace open --json` | Register and index a workspace from the CLI. |
| `workspace remove --json` | Delete a workspace `.miller` index directory and unregister it. |
| `search --json` | Symbol/default search or explicit content/source/external/web/all-text search results. |
| `todos --json` | Bounded TODO/FIXME/HACK/XXX marker audit over comment/doc-comment source regions. |
| `inspect --json` | File/symbol summary or full inspect result. |
| `context --json` | Token-budgeted code bundle. `--reference-mode usage` adds reason/confidence-labeled usage evidence. |
| `impact --json` | Downstream impact result for a symbol, changed paths, or diff. |
| `trace --json` | Structured auto/path/refs/bridge trace result. See [`trace-json-v1.md`](trace-json-v1.md). |
| `patterns --json` | List, summarize, and search extractor-recognized code-shape facts. See [`patterns-json-v1.md`](patterns-json-v1.md). |
| `content import --json` | Import local external text into `content.db`. |
| `content add-markdown --json` | Import browser/fetched markdown with URL metadata into `content.db`. |
| `content search --json` | Search content DB rows. |
| `content read --json` | Read bounded content windows. |
| `content list --json` | List imported external/web content. |
| `content remove --json` | Remove imported external/web content. |
| `telemetry export --jsonl` | Export raw Miller telemetry rows for Eros dashboard/history ingestion. |
| `symbols export --jsonl` | Bulk-export one row per symbol for fleet rollups (counts, kinds, doc coverage, clones). |
| `complexity export --jsonl` | Bulk-export per-symbol/per-file complexity metric rows for fleet hotspot ranking. |
| `dashboard --json` | Start/reuse the local dashboard helper and return its URL. |
| `capabilities --json` | Discover this contract surface. |

`capabilities --json` reports `optional_features.reference_aware_context=true` when `context --reference-mode usage`
is available.

`patterns --json` is the stable way to consume `julie-extractors` structural facts. Eros should use this command
for known code-shape signals instead of reading Miller private SQLite tables directly.

`todos --json` uses the source-region search sidecar, so callers should check
`optional_features.source_region_index` from `capabilities --json` and normal workspace sidecar health before
depending on it. It is a marker-audit surface, not a task tracker: rows identify code comments by marker,
file, line, region kind, language, containing symbol when known, and snippet text.

The `refresh --json --wait` response uses the same action shape as `workspace refresh --json`, plus
post-refresh artifact facts when available:

- `index_fresh`: `true` for `refreshed`/`unchanged`, `false` for lock-busy or failed convergence.
- `scan_duration_ms`: wall milliseconds of the julie-extract scan attempt when one ran — present even for
  `failed` (a timed-out, killed scan reports roughly the timeout), `null` when no scan ran (e.g. `lock_busy`).
  Use this for fleet-sweep extract-duration telemetry.
- `duration_ms`: wall milliseconds of the whole refresh attempt (lock wait, scan, sidecar convergence), when
  measured; `null` on paths that do not measure it.
- `search_sidecar`: state, path, revision, expected revision, document count, and error for `.miller/search.db`.
- `content_corpus`: state, path, schema version, workspace revision, source/chunk counts, byte counts, skip counts,
  and error for `.miller/content.db`.

`--wait` is a contract flag. The Miller CLI refresh path is already synchronous: it returns only after the
lock-holding refresh attempt converges, observes another writer, or reports an operational failure.

A `lock_busy` result exits `0` and its payload is ingestable: the latest readable DB is being served and a live
leader owns convergence (for `full`, a leader full-scan request was enqueued). Freshness is NOT confirmed —
consumers that need a confirmed-fresh index must gate on `status` (`refreshed`/`unchanged`) or `index_fresh:
true` in the payload, not on the exit code alone. Exit `3` is reserved for genuinely unusable-index outcomes:
`missing_root`, `missing_index`, `failed`, and `ineligible_extractor`.

## Export feeds

`miller content export [--kind KIND] [--content-workspace-id ID]` emits deterministic JSONL chunk rows for
semantic ingestion. See `docs/contracts/content-corpus-v1.md` for field-level guarantees.

Capabilities advertise this feed as:

```json
{
  "name": "content_corpus",
  "command": "miller content export",
  "format": "jsonl",
  "schema_version": 1,
  "chunker_version": "line-v1",
  "filters": ["--kind", "--content-workspace-id"]
}
```

`miller telemetry export --jsonl [--workspace-id ID|all]` emits raw rows from the machine-global
`~/.miller/telemetry.db`. The default is all workspaces; `--workspace-id` is an exact stored workspace ID
filter, not a display-id selector.

`miller symbols export --jsonl [--workspace-id SELECTOR] [--workspace DIR]` emits one JSON line per symbol of
ONE workspace's index, ordered `(path, start_line, symbol_id)` so an unchanged artifact re-exports
byte-identically. The selector flags are the normal read-command selectors. An incompatible artifact exits `3`
with the standard rebuild message. Fields (`schema_version` 1):

- `symbol_id`, `name`, `kind`, `language`, `path` — identity (strings; `symbol_id` is julie's stable id).
- `start_line`, `end_line`, `start_byte`, `end_byte` — the symbol's whole span (1-based lines).
- `visibility`, `parent_symbol_id`, `signature` — nullable strings (containment via `parent_symbol_id`).
- `has_doc` — boolean; true when the symbol carries a non-empty doc comment (doc-coverage rollups).
- `body_hash` — nullable string; julie's normalized body hash (clone-candidate rollups).
- `is_test` — boolean; julie's cross-language test signal (prod/test splits).

`miller complexity export --jsonl [--workspace-id SELECTOR] [--workspace DIR]` emits one JSON line per
`complexity_metrics` row (file-scope and symbol-scope; emitted broadly since julie-extract 2.3.0), ordered
`(path, start_byte, complexity_metric_id)`. Fields (`schema_version` 1):

- `complexity_metric_id`, `path`, `language`, `scope` (`file`|`symbol`), `symbol_id` (nullable; set for
  symbol scope), `algorithm_id`.
- `covered_lines`, `covered_bytes`, `decision_count`, `loop_count`, `max_nesting_depth`,
  `parameter_count` (nullable).
- `start_line`, `end_line`, `start_byte`, `end_byte`.

## Workspace selector rules

Code read commands (`search`, `todos`, `inspect`, `context`, `impact`, `trace`, `patterns`, and the `symbols`/`complexity`
exports) target one workspace per call. Their `--workspace-id <selector>` accepts a display ID, unique prefix,
full workspace ID, registered root path, `current`, or `primary`. The path alias `--workspace <path>` is
normalized before selection. A selector flag supplied without a value is a usage error (exit `2`) in every
combination — it is never masked by the other selector flag and never falls back silently to the current
workspace.

The `workspace` lifecycle subcommands (`status`, `health`, `refresh`, `full`, `remove`) accept the same selector
flags: `--workspace-id` aliases `--id`, and `--workspace <path>` (normalized against the CLI's cwd) aliases
`--path`. A selector flag supplied without a value is a usage error (exit `2`); a command never falls back
silently to the current workspace when a selector was attempted.

If a caller needs workspace B while running from workspace A, it should call `workspace list --json`, choose B's
selector, and pass that selector to the read command. If B is not listed, call
`workspace open --path /absolute/repo --full --json` first, then retry the read command. The special
`--workspace-id all` selector is reserved for cross-workspace content/telemetry surfaces such as
`content search --workspace-id all` and `telemetry export --workspace-id all`; it is not a symbol/code read
selector.

`miller context <query> --reference-mode usage --json` keeps the normal `bundle` array but adds mixed item types:
`symbol`, `identifier`, and `content_chunk`. Each item includes `reason` and `confidence`; `confidence=name_based`
means the identifier came from a same-name row and is a possible reference, not a resolved target-symbol edge.

Telemetry JSONL fields:

| Field | Required | Description |
|---|---:|---|
| `schema_version` | yes | Telemetry export schema version; currently `1`. |
| `id` | yes | Telemetry row/correlation ID. |
| `ts` | yes | UTC timestamp stored by the telemetry ledger. |
| `tool` | yes | Tool or CLI surface name. |
| `op` | no | Tool operation/mode when known. |
| `workspace_id` | no | Stored Miller workspace ID. |
| `workspace_root` | no | Stored workspace root. |
| `duration_ms` | yes | Tool duration in milliseconds. |
| `outcome` | yes | `ok`, `empty`, or `error`. |
| `error_kind` | no | Error classifier when outcome is `error`. |
| `result_count` | no | Result count when the tool reported one. |
| `bytes_examined` | yes | Work proxy recorded by the tool. |
| `bytes_returned` | yes | Serialized output byte count. |
| `source_bytes` | yes | Source bytes touched, when known. |
| `est_tokens` | no | Estimated returned tokens. |
| `index_fresh` | no | Whether the served index was fresh when known. |
| `target_hash` | no | SHA-256 hash of the target/query; raw target text is not stored. |
| `metadata_json` | yes | Tool-specific metadata as a JSON string. |

Content export lines include raw chunk text. Eros owns embeddings, semantic ranking, deletion/reconciliation of
stale semantic chunks, and commercial dashboard/history views. Telemetry export does not include raw queries; it
exports the stored target hash and tool metadata only.

## Exit codes

Miller CLI commands use the same process-level exit code contract:

| Code | Meaning |
|---:|---|
| `0` | Success — the JSON payload is ingestable. Includes `refresh`/`workspace refresh|full|open` returning `lock_busy` (index served, freshness unconfirmed; gate on `status`/`index_fresh`). |
| `2` | Usage or selector error. |
| `3` | Operational failure such as no usable index, missing restore, refused workspace operation, or failed refresh (`missing_root`, `missing_index`, `failed`, `ineligible_extractor`). |
| `1` | Unexpected failure converted to a clean CLI error line. |

Eros should treat non-zero as non-ingestable unless a command-specific workflow explicitly allows an idempotent
result such as `workspace remove` returning `not_found` with exit code `0`.

## Boundary

Miller should add new CLI JSON/export surfaces when Eros needs stable code facts or operations. Do not add a
private Eros-to-Miller protocol until documented JSON, JSONL, and local artifacts are proven insufficient.
