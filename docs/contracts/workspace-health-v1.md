# Miller workspace health v1 contract

`miller workspace health --json` and MCP `workspace(operation="health", format="json")` return a local readiness
verdict for one workspace. The report is deterministic and based only on Miller artifacts; it does not claim code
quality, security status, semantic search quality, or enterprise readiness.

## Top-level shape

```json
{
  "verdict": {
    "state": "ready | usable_with_warnings | degraded | unavailable",
    "summary": "index readable with warnings"
  },
  "workspace": {},
  "index": {},
  "extraction_quality": {},
  "telemetry": {},
  "warnings": [],
  "recommended_actions": []
}
```

This v1 contract is additive: consumers must ignore unknown fields and unknown `extraction_quality` subsections.
Removing or renaming documented fields requires a new contract version.

## Sections

- `workspace`: `root`, `workspace_id`, `display_id`, `db`, `leader`, `server_version`, `server_pid`.
- `index`: `document_count`, `known_extensions`, `built_revision`, `latest_revision`, `index_fresh`,
  `freshness_status`, `warning`, `queue_empty`, `search_sidecar`, and `content_corpus`.
- `extraction_quality.parse_diagnostics`: `available`, `error`, and grouped rows with `language`, `kind`, `count`.
- `extraction_quality.capability_gaps`: `available`, `error`, and grouped rows with `language`, `capability`,
  `status`, `count`.
- `extraction_quality.language_capabilities`: `available`, `error`, and target/actual counts by language for
  symbols, relationships, pending relationships, identifiers, and types.
- `extraction_quality.structural_facts`: `available`, `error`, and grouped rows with `language`, `pattern_id`,
  `capture_name`, `count`.
- `extraction_quality.complexity_metrics`: `available`, `error`, and grouped rows with `language`, `scope`,
  `algorithm_id`, `count`, `max_decision_count`, `max_loop_count`, `max_nesting_depth`, and
  `max_parameter_count`.
- `extraction_quality.files`: `available`, `error`, and grouped rows with `language`, `status`, `count`.
- `telemetry.outcomes`: `ok_count`, `empty_count`, `error_count`, `total_calls`.
- `telemetry.summary`: the same per-tool summary shape used by `workspace status --json`.
- `warnings`: objects with `code`, `severity`, and `message`.
- `recommended_actions`: short strings intended for agents and downstream dashboards.

## State rules

- `ready`: no warnings.
- `usable_with_warnings`: the index is readable, but non-blocking warnings exist, such as parse diagnostics,
  capability gaps, missing rebuildable sidecars, missing optional health-detail tables, or telemetry errors.
- `degraded`: the workspace is readable, but an important freshness or sidecar warning should be investigated
  before relying on results.
- `unavailable`: the target index DB is missing or otherwise cannot provide the basic workspace report.

The health path must not hydrate the full repository index. It reads cheap status facts, sidecar metadata,
telemetry aggregates, and grouped SQLite counts from `symbols.db`. Parser-backed structural facts and complexity
metrics are reported as primitive extractor facts only; Miller does not assign quality scores, risk thresholds, or
commercial dashboard labels in this contract.
