# Metrics JSON contract v1

Status: active additive contract for the CLI-only `miller metrics <churn|clones|complexity> --json` commands.

Miller metrics are deterministic local facts over the selected workspace. They are not semantic rankings, cleanup
recommendations, suppressions, fleet history, or Eros workflow orchestration.

## Common envelope

All responses include:

| Field | Type | Description |
|---|---|---|
| `schema_version` | number | Contract version. Currently `1`. |
| `operation` | string | `churn`, `clones`, or `complexity`. |

The command accepts the normal single-workspace selectors: `--workspace-id SELECTOR` and `--workspace DIR`.

## Churn

Command:

```bash
miller metrics churn --json [--range REV..REV] [--limit N] [--include-commits]
```

Fields:

| Field | Type | Description |
|---|---|---|
| `range` | string | Git revision range that was read. Defaults to `HEAD~20..HEAD`. |
| `mapping_note` | string | Always states that hunks are mapped to the current index. |
| `rows` | array | Bounded churn rows sorted by commit count, changed lines, last commit time, path, and symbol. |

Each row:

| Field | Type | Description |
|---|---|---|
| `mapping_basis` | string | `current_index` for symbol rows, `file_only` when no current symbol could be mapped. |
| `symbol_id` | string or null | Current symbol id when mapped. |
| `symbol_name` | string or null | Current symbol name when mapped. |
| `symbol_kind` | string or null | Current symbol kind when mapped. |
| `path` | string | Workspace-relative path. |
| `line` | number or null | Current symbol line when mapped. |
| `commit_count` | number | Distinct commits in the range touching the row. |
| `changed_lines` | number | Added plus deleted lines touching the row. |
| `last_commit_at_utc` | string | Latest commit timestamp in UTC. |
| `commits` | array | Commit ids only when `--include-commits` is supplied; otherwise empty. |

Non-git workspaces return a clear command error; Miller does not call the network.

## Clones

Command:

```bash
miller metrics clones --json [--min-count N] [--limit N] [--max-symbols-per-group N]
```

Fields:

| Field | Type | Description |
|---|---|---|
| `groups` | array | Duplicate groups by identical non-empty `symbols.body_hash`. |

Each group:

| Field | Type | Description |
|---|---|---|
| `body_hash` | string | Normalized body hash from `julie-extractors`. |
| `count` | number | Number of symbols in the group. |
| `symbol_limit` | number | Maximum symbols listed in `symbols` for this group. |
| `symbols_truncated` | boolean | `true` when `count` is larger than the listed `symbols` sample. |
| `symbols` | array | Bounded symbols in deterministic path/line/name order. |

Each symbol:

| Field | Type |
|---|---|
| `symbol_id` | string |
| `name` | string |
| `kind` | string |
| `language` | string |
| `path` | string |
| `line` | number |
| `is_test` | boolean |

The clone surface does not emit source body text and does not suggest cleanup.

## Complexity

Command:

```bash
miller metrics complexity --json [--min-severity low|moderate|high] [--include-tests|--exclude-tests] [--limit N]
```

Fields:

| Field | Type | Description |
|---|---|---|
| `min_severity` | string | Applied filter. Defaults to `moderate`. |
| `thresholds` | object | Transparent Miller-owned severity thresholds. |
| `hotspots` | array | Deterministically ordered complexity rows. |

Thresholds:

| Field | Value |
|---|---:|
| `moderate_decision_count` | 8 |
| `moderate_max_nesting_depth` | 4 |
| `high_decision_count` | 15 |
| `high_max_nesting_depth` | 6 |

Each hotspot:

| Field | Type |
|---|---|
| `severity` | `low`, `moderate`, or `high` |
| `complexity_metric_id` | string |
| `path` | string |
| `language` | string |
| `scope` | string |
| `symbol_id` | string or null |
| `symbol_name` | string or null |
| `symbol_kind` | string or null |
| `algorithm_id` | string |
| `covered_lines` | number |
| `covered_bytes` | number |
| `decision_count` | number |
| `loop_count` | number |
| `max_nesting_depth` | number |
| `parameter_count` | number or null |
| `start_line` | number |
| `end_line` | number |
| `start_byte` | number |
| `end_byte` | number |
| `is_test` | boolean |

`miller complexity export --jsonl` remains the raw streaming feed. `metrics complexity --json` is a bounded
interactive/top-N report over existing extracted facts.
