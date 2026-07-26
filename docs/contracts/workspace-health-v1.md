# Miller workspace health v1 contract

`miller workspace health --json` and MCP `workspace(operation="health", format="json")` return a local readiness
verdict for one workspace. The report is deterministic and based only on Miller artifacts; it does not claim code
quality, security status, semantic search quality, or enterprise readiness.

## Output formats and bounds

- `compact` is hard-bounded to at most 14 lines, and each dynamic value is flattened to one line and capped at
  240 characters. It renders aggregate quality counts, the first warning, and the first recommended action. Its
  final `omitted` line reports the number of available extraction-detail groups hidden as `groups`, the number
  whose source was unavailable as `unavailable`, their exact grouped-row total, and the exact warning/action
  counts not rendered.
- CLI `--json` is complete and follows the shape below; no extraction rows, warnings, or actions are capped.
- MCP `format="json"` is a 12 KiB agent-facing summary. It preserves the verdict,
  workspace identity, actionable index and sidecar state, extraction section availability and exact row counts,
  telemetry outcome counts, and bounded warnings/actions. Exhaustive JSON stays CLI-only so an MCP call cannot
  flood agent context.
- CLI `--markdown` contains the compact summary followed by the complete JSON report in a fenced block. MCP
  accepts only `format="compact"` or `format="json"`.

## Top-level shape

```json
{
  "verdict": {
    "state": "ready | usable_with_warnings | degraded | unavailable",
    "summary": "index readable with warnings"
  },
  "workspace": {},
  "indexer_leader": {},
  "index": {},
  "extraction_quality": {},
  "telemetry": {},
  "warnings": [],
  "recommended_actions": []
}
```

This v1 contract is additive: consumers must ignore unknown fields and unknown `extraction_quality` subsections.
Removing or renaming documented fields requires a new contract version.

The MCP summary adds `detail: "summary"`. Its extraction sections report `available`, `error`, and `row_count`
instead of copying every grouped row. `warnings` and `recommended_actions` return at most three entries each,
with `warnings_total_count`, `warnings_omitted_count`, `recommended_actions_total_count`, and
`recommended_actions_omitted_count` preserving exact coverage. `next_action` points to
`miller workspace health --json`.

## Sections

- `workspace`: `root`, `workspace_id`, `display_id`, `db`, `leader`, `server_version`, `server_pid`.
- `indexer_leader` (additive in v1; may be `null` when leader facts were not gathered): `this_process`, plus the
  recorded leader identity from `.miller/leader.json` — `pid`, `version`, `process_path`, `started_at`,
  `extractor_version` (the leader's bundled `julie-extract` version; all null when no identity is recorded, and
  `extractor_version` is also null for identities written by builds that predate version-aware leadership) — and
  `alive` (a liveness probe of that pid; null without an identity). Index convergence is owned by whichever
  process leads, so a dead pid or a version mismatch here explains stale-index symptoms in multi-process setups.
  Additive version-aware-leadership fields (null when the responding process could not gather them):
  - `own_extractor_version`: the responding process's bundled `julie-extract` version.
  - `artifact_extractor_version`: the `binary_version` recorded in the index artifact's `artifact_metadata`.
  - `own_eligibility`: `null`, or `{ "eligible": bool, "reason": string }` — the responding process's
    version-aware leadership verdict for this workspace (an older extractor never rewrites a newer artifact;
    `MILLER_ALLOW_EXTRACTOR_DOWNGRADE=1` is the explicit override for intentional downgrades).

  Related warning codes: `indexer_leader_unknown`, `indexer_leader_dead`, `indexer_leader_version_mismatch`,
  `leader_extractor_older_than_artifact` (a live leader's bundled extractor is strictly older than the
  artifact's `binary_version`, so it can never rebuild without regressing the index), and
  `index_frozen_extractor_outdated` (the responding process is ineligible AND no live leader exists — nobody
  can index; upgrade miller or restore the pinned extractor via `scripts/restore-julie-extract`).
- `index`: `document_count`, `known_extensions`, `built_revision`, `latest_revision`, `index_fresh`,
  `freshness_status`, `warning`, `queue_empty`, `search_sidecar`, `content_corpus`, `history_db`, and —
  additively, only when semantic retrieval is enabled — `vectors` (same object as
  [`workspace-status-v1.md`](workspace-status-v1.md); omitted entirely when `MILLER_SEMANTIC` is off).
  `history_db` is `null` only when history facts were not gathered; otherwise it is an object with:
  - `present`: whether `<workspace>/.miller/history.db` exists.
  - `unreadable`: whether a present sidecar could not be opened/read.
  - `schema_version`: metric-history schema version, or `0` when absent/unreadable.
  - `snapshot_count`: number of metric snapshots readable from the sidecar.
  - `size_bytes`: sidecar file size in bytes.
  - `corrupt_recovered`: whether a prior corrupt bundle has been preserved aside.
- `extraction_quality.parse_diagnostics`: `available`, `error`, and grouped rows with `language`, `kind`, `count`.
- `extraction_quality.capability_gaps`: `available`, `error`, and grouped rows with `language`, `capability`,
  `status`, `count`.
- `extraction_quality.language_capabilities`: `available`, `error`, and target/actual counts by language for
  symbols, relationships, pending relationships, identifiers, and types. Each row also carries
  `kind_coverage`: an object keyed by extraction domain (julie-extract v2.3.0 emits ten, e.g. `symbols`,
  `doc_comments`, `structural_facts`), each with `supported` and `not_applicable` kind-string arrays plus an
  `open_gaps` array. `open_gaps` entries are passed through verbatim from the artifact and take two shapes:
  a plain kind string (legacy artifacts), or — since julie-extract v2.12 (`test_detection`) — an object whose
  `kind` string names the gap alongside explanatory fields such as `reason`, `required_closure`, and
  `planned_closure_task`. Consumers must tolerate both shapes and unknown object fields. An entry that fits
  neither shape is still passed through, never dropped: an extractor-declared gap must not silently read as
  an empty capability, so treat uninterpretable entries as "gap of unknown kind", not as absence of a gap.
  Consumers must treat the domain set as open-ended; absent or empty `kind_coverage` means the artifact
  predates the depth contract.
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

Missing, stale, corrupt, or incompatible derived sidecars remain typed warnings rather than changing the
authoritative `symbols.db` readiness verdict. Missing/stale `search_sidecar` and `content_corpus` warnings carry a
`workspace refresh` recovery action; corrupt or otherwise unreadable derived artifacts carry a `workspace full`
recovery action. Non-search symbol reads such as `inspect`, `context`, `impact`, and `trace` continue from a fresh,
compatible `symbols.db` when an unrelated search/content/vector sidecar is unavailable. Symbol search still fails
visibly when its required `search.db` is missing, stale, or corrupt.
An `imports_only` content corpus is `usable_with_warnings` and recommends `workspace refresh`.
`preservation_blocked` is degraded and recommends preserving/recovering the imported sources before replacing the
sidecar, starting with the concrete CLI `miller content export`; it never recommends a destructive full rebuild as
though the imports were disposable.

## State rules

- `ready`: no warnings.
- `usable_with_warnings`: the index is readable, but non-blocking warnings exist, such as parse diagnostics,
  capability gaps, missing rebuildable sidecars, missing optional health-detail tables, or telemetry errors.
- `degraded`: the workspace is readable, but an important freshness, sidecar, or indexer-leader warning (e.g. a
  dead leader pid) should be investigated before relying on results.
- `unavailable`: the target index DB is missing or otherwise cannot provide the basic workspace report.

The health path must not hydrate the full repository index. It reads cheap status facts, sidecar metadata,
telemetry aggregates, and grouped SQLite counts from `symbols.db`. Parser-backed structural facts and complexity
metrics are reported as primitive extractor facts only; Miller does not assign quality scores, risk thresholds, or
commercial dashboard labels in this contract. All grouped extraction reads use one SQLite read transaction so
section counts and availability describe one artifact snapshot.
