# Workspace Onboarding JSON v1

Status: active Miller CLI JSON contract.

Command:

```bash
miller workspace onboarding --json [--workspace-id SELECTOR] [--workspace DIR]
```

This contract summarizes local Miller tool telemetry for one indexed workspace into startup guidance for agents.
It is generic to any Miller-indexed repo and does not write `CLAUDE.md`, `AGENTS.md`, or `ONBOARDING.md`.

## Top-level shape

```json
{
  "operation": "onboarding",
  "workspace": {},
  "telemetry": {},
  "start_here": [],
  "tool_mix": [],
  "successful_flows": [],
  "hot_targets": [],
  "common_misses": [],
  "friction": [],
  "instruction_notes": [],
  "privacy": {}
}
```

## Required fields

`workspace`:

- `root`: workspace root path.
- `workspace_id`: stable Miller workspace ID, or `null` when unavailable.
- `display_id`: human display ID, or `null`.
- `db`: path to `.miller/symbols.db`.

`telemetry`:

- `available`: whether the shared telemetry DB was readable.
- `state`: `ready`, `sparse`, `missing_telemetry_db`, `missing_telemetry_table`, or `unreadable_telemetry_db`.
- `total_calls`: calls in the scoped onboarding window.
- `window_start_ts`, `window_end_ts`: UTC timestamp bounds, or `null`.
- `error`: telemetry read error text, or `null`.

`start_here`:

- Array of short guidance strings derived from observed tool mix and flows.

`tool_mix` rows:

- `tool`, `op`: tool and operation/mode.
- `calls`, `ok_count`, `empty_count`, `error_count`: outcome counts.
- `avg_ms`, `p95_ms`, `max_ms`: duration metrics.
- `result_count`, `bytes_returned`, `est_tokens`: aggregate output/work counters.

`successful_flows` rows:

- `from`, `to`: tool labels such as `search:auto` or `inspect:summary`.
- `calls`: adjacent successful call count in the telemetry window.

`hot_targets` rows:

- `label`: renderable current-index target label.
- `confidence`: `symbol_id_hash`, `scoped_symbol_hash`, `file_path_hash`, `symbol_name_hash`, or `unresolved_hash`.
- `symbol_id`, `name`, `kind`, `path`, `start_line`: recovered current-index facts, nullable.
- `calls`: repeated telemetry target count.
- `candidate_count`: number of current-index candidates for the chosen confidence class.

`common_misses` rows:

- `tool`, `op`: tool and operation/mode.
- `reason`: miss reason from telemetry metadata/error kind.
- `calls`: count.

`friction` rows:

- `tool`, `op`, `calls`, `avg_ms`, `p95_ms`, `max_ms`, `bytes_returned`, `est_tokens`, `empty_count`, `error_count`.

`instruction_notes`:

- Array of short caution or guidance strings derived from misses, unresolved hashes, sparse telemetry, or tool errors.

`privacy`:

- `raw_queries_stored`: always `false`.
- `raw_targets_stored`: always `false`.
- `notes`: privacy notes. Target hashes are matched only against the current local index; unresolved hashes are not emitted.

## Privacy and stability

Telemetry stores SHA-256 target hashes, not raw query or target text. Onboarding may recover a target only when
the hash matches a current symbol ID, scoped `path:name`, file path, or symbol name in the selected workspace's
index. Additive fields are allowed. Removing or renaming fields requires a new contract version.
