# Report JSON contract v1

Status: active additive contract for the CLI-only `miller report --json` command.

`miller report` is one composed repo-quality rollup over facts Miller already extracts for the
selected workspace. It is pure composition: no new extraction, no semantic ranking, no cleanup
recommendations, no suppressions, and no fleet history. There is deliberately **no dead-code
section**: reference resolution in current artifacts is not strong enough to support one (see the
2026-07-06 standalone bolstering assessment); it will be added only when candidate quality earns it.

## Command

```bash
miller report --json [--workspace-id SELECTOR] [--workspace DIR] [--range REV..REV] [--limit N] [--include-tests|--exclude-tests]
```

- `--range` feeds the churn and risk sections. Defaults to `HEAD~20..HEAD`.
- `--limit` is the per-section top-N (default 10, max 100).
- Exit codes match other read commands: `0` renderable report, `2` usage/selector errors,
  `3` operational index failures.

## Envelope

| Field | Type | Description |
|---|---|---|
| `schema_version` | number | Contract version. Currently `1`. |
| `operation` | string | Always `report`. |
| `range` | string | Git range used by churn/risk sections. |
| `section_limit` | number | Applied per-section top-N. |

Every section object carries `available` (boolean). An unavailable section carries a human-readable
`reason` and omits its data fields; the report itself still exits `0`. Consumers must ignore
sections and fields they do not recognize — the contract is additive.

## Sections

### `index`

| Field | Type | Description |
|---|---|---|
| `symbols` | number | Named symbol rows in the artifact. |
| `files` | number | Distinct symbol-bearing paths. |
| `languages` | number | Distinct languages. |

### `extraction_health`

| Field | Type | Description |
|---|---|---|
| `parse_diagnostic_count` | number | Total extractor parse diagnostics. |
| `capability_gap_count` | number | Total open capability gaps. |

### `markers`

Marker counts ride the region search sidecar; when the sidecar is disabled or `search.db` cannot be
opened the section is unavailable with a reason.

| Field | Type | Description |
|---|---|---|
| `bounded_at` | number | Region fetch bound; counts saturate here. |
| `truncated` | boolean | True when the total hit the bound. |
| `counts` | array | `{marker, count}` for `TODO`, `FIXME`, `HACK`, `XXX`. A region matching several markers counts once per marker. |
| `total` | number | Distinct marker regions found (bounded). |

### `complexity`

Top-N complexity hotspots at `min_severity=moderate`, same ordering as `metrics complexity`. Each
hotspot: `severity`, `path`, `symbol_name`, `decision_count`, `loop_count`, `max_nesting_depth`,
`start_line`, `is_test`.

### `clones`

Top-N body-hash clone groups, same ordering as `metrics clones`. Each group: `body_hash`, `count`,
and a `sample` of up to 3 `{path, line, name}` symbols.

### `churn`

Top-N churn rows for `range`, same semantics as `metrics churn` (`mapping_basis`, `symbol_name`,
`path`, `line`, `commit_count`, `changed_lines`). Unavailable with a reason on non-git workspaces.

### `risk`

Top-N churn×complexity risk rows for `range`, same semantics and `score_formula` as `metrics risk`
(see `docs/contracts/metrics-json-v1.md`). Unavailable with a reason on non-git workspaces. The
underlying git history is parsed once and shared with the churn section.

## Performance

Cost is one bounded SQLite read per non-git section plus a single git history parse for
churn+risk. The default `HEAD~20..HEAD` range keeps the report interactive; large explicit ranges
pay proportionally in git history parsing.
