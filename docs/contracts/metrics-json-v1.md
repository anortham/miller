# Metrics JSON contract v1

Status: active additive contract for the CLI-only `miller metrics <churn|clones|complexity|risk> --json` commands.

Miller metrics are deterministic local facts over the selected workspace. They are not semantic rankings, cleanup
recommendations, suppressions, fleet history, or Eros workflow orchestration.

## Common envelope

All responses include:

| Field | Type | Description |
|---|---|---|
| `schema_version` | number | Contract version. Currently `1`. |
| `operation` | string | `churn`, `clones`, `complexity`, or `risk`. The set is additive: v1 consumers must ignore operations they do not recognize rather than treating the enum as closed (`risk` was added 2026-07-06). |

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
miller metrics clones --json [--min-count N] [--limit N] [--max-symbols-per-group N] [--near-duplicates]
```

Fields:

| Field | Type | Description |
|---|---|---|
| `groups` | array | Duplicate groups by identical non-empty `symbols.body_hash`, followed by Type-2 near-duplicate groups when `--near-duplicates` is supplied. |

Each exact group:

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

With `--near-duplicates`, Type-2 near-duplicate groups (renamed identifiers / changed literals, MinHash/LSH
over normalized token shingles, deterministic) are appended to the same `groups` array. They are the only
entries carrying `kind`; an absent `kind` means the v1 exact `body_hash` group. Nothing is appended when the
flag is off or no near-duplicates are found, so v1 output is byte-identical.

Each near-duplicate group:

| Field | Type | Description |
|---|---|---|
| `kind` | string | Always `near_duplicate`. |
| `similarity` | number | Weakest accepted pairwise Jaccard edge that linked the group (a floor, 4 dp). |
| `count` | number | Number of symbols in the group. |
| `symbol_limit` | number | Maximum symbols listed in `symbols` for this group. |
| `symbols_truncated` | boolean | `true` when `count` is larger than the listed `symbols` sample. |
| `symbols` | array | Bounded symbols, same shape and ordering as exact groups. |

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

## Risk

Command:

```bash
miller metrics risk --json [--range REV..REV] [--limit N] [--include-tests|--exclude-tests]
```

Deterministic churn×complexity ranking. A risk row exists only where churn evidence and complexity
evidence intersect on the current index; churn-only rows stay in `metrics churn` and
complexity-only rows stay in `metrics complexity`. Churn and complexity are joined over their full
sets for the range BEFORE `--limit` is applied, so a low-churn/high-complexity symbol can outrank a
high-churn/trivial one. `hotspots` remains an alias of `complexity` and is NOT this operation.

Fields:

| Field | Type | Description |
|---|---|---|
| `range` | string | Git revision range that was read. Defaults to `HEAD~20..HEAD`. |
| `score_formula` | string | Always `commit_count * (decision_count + loop_count + max_nesting_depth)`. |
| `mapping_note` | string | States the intersection semantics. |
| `rows` | array | Rows ordered by score, changed lines, commit count, path, then symbol name. |

Each row:

| Field | Type | Description |
|---|---|---|
| `basis` | string | `symbol` when a symbol-mapped churn row joined complexity by `symbol_id`; `file` when a `file_only` churn row joined the path-level complexity aggregate. |
| `symbol_id` / `symbol_name` / `symbol_kind` | string or null | Current symbol identity for `basis=symbol`; null for `basis=file`. |
| `path` | string | Workspace-relative path. |
| `line` | number or null | Current symbol line when mapped. |
| `commit_count` | number | Distinct commits in the range touching the row. |
| `changed_lines` | number | Added plus deleted lines touching the row. |
| `last_commit_at_utc` | string | Latest commit timestamp in UTC. |
| `decision_count` / `loop_count` | number | Summed over the joined complexity rows (one row for most symbols; the whole path for `basis=file`). |
| `max_nesting_depth` | number | Maximum over the joined complexity rows. |
| `severity` | string | `low`, `moderate`, or `high`, classified from the aggregated counters with the complexity thresholds above. |
| `is_test` | boolean | True when any joined complexity row belongs to a test symbol. |
| `score` | number | `commit_count * (decision_count + loop_count + max_nesting_depth)`. |

Performance: cost is bounded by the git range (churn parse) plus one complexity read filtered to the
churned paths. The default `HEAD~20..HEAD` range keeps this interactive; very large explicit ranges
pay proportionally in git history parsing, not in complexity table scans. Non-git workspaces return
the same command error as `metrics churn`.
