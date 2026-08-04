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
| `features` | Independently negotiated capability strings. Gate revision-delta behavior on `impact_index_revision_delta`, traversal evidence on `impact_traversal_evidence`, and impact test-role evidence on `impact_test_role_evidence`. |
| `json_commands` | CLI commands with stable JSON output. |
| `json_contracts` | Versioned JSON contracts with command, schema version, and doc path. |
| `supported_export_formats` | Streaming export feeds supported by this build. |

## Stable JSON commands

Current `json_commands` include:

| Command | Purpose |
|---|---|
| `workspace status --json` | Workspace identity, index DB path, revision/freshness facts, sidecar facts, and telemetry summary. See [`workspace-status-v1.md`](workspace-status-v1.md). |
| `workspace health --json` | Workspace readiness verdict, warnings/actions, sidecar state, extraction-quality aggregates, and telemetry outcome counts. See [`workspace-health-v1.md`](workspace-health-v1.md). |
| `workspace onboarding --json` | Privacy-safe startup guidance derived from local Miller telemetry: starter commands, hot current-index targets, common misses, and friction. See [`workspace-onboarding-v1.md`](workspace-onboarding-v1.md). |
| `workspace leader --json` | Indexer-leader diagnostics and optional graceful handoff request status. See [`workspace-leader-json-v1.md`](workspace-leader-json-v1.md). |
| `workspace list --json` | Registered workspaces from `~/.miller/workspaces.db`, with exact selection and missing-root totals. |
| `workspace refresh --json` | Incremental convergence result for a registered workspace. |
| `workspace full --json` | Forced full re-index result for a registered workspace. |
| `refresh --json --wait` | Eros-friendly top-level alias for registered-workspace convergence. Accepts `--workspace-id`, `--workspace`, and `--full`; returns after the synchronous refresh attempt. See [`refresh-wait-v1.md`](refresh-wait-v1.md). |
| `workspace open --json` | Register and index a workspace from the CLI. |
| `workspace remove --json` | Delete a registered workspace `.miller` index directory and unregister it. |
| `search --json` | Symbol/default search, marker audits, or explicit content/source/external/web/all-text search results. |
| `todos --json` | CLI compatibility alias for bounded TODO/FIXME/HACK/XXX marker audits over comment/doc-comment source regions. |
| `inspect --json` | File/symbol summary or full inspect result. |
| `context --json` | Token-budgeted code bundle. `--reference-mode usage` adds reason/confidence-labeled usage evidence. |
| `impact --json` | Downstream impact result for a symbol, changed paths, or diff. Index-revision mode is documented by [`impact-index-revision-delta-v1.md`](impact-index-revision-delta-v1.md); bounded graph evidence by [`impact-traversal-evidence-v1.md`](impact-traversal-evidence-v1.md); positive test-role evidence by [`impact-test-role-evidence-v1.md`](impact-test-role-evidence-v1.md). |
| `trace --json` | Structured path/refs/bridge trace result. Path defaults to call-like edges; `--path-kind dependency` opts into broad dependencies. See [`trace-json-v1.md`](trace-json-v1.md). |
| `patterns --json` | List, summarize, and search extractor-recognized code-shape facts. See [`patterns-json-v1.md`](patterns-json-v1.md). |
| `metrics churn --json` | Local git commit-range churn mapped to the current index. See [`metrics-json-v1.md`](metrics-json-v1.md). |
| `metrics clones --json` | Duplicate groups by identical non-empty body hash. See [`metrics-json-v1.md`](metrics-json-v1.md). |
| `metrics complexity --json` | Bounded complexity hotspot report with transparent thresholds. See [`metrics-json-v1.md`](metrics-json-v1.md). |
| `metrics risk --json` | Churn × complexity risk hotspot ranking with a transparent score formula. See [`metrics-json-v1.md`](metrics-json-v1.md). |
| `metrics history --json` | Read-only metric trend points from the append-only `history.db` sidecar. See [`metrics-history-v1.md`](metrics-history-v1.md). |
| `report --json` | Composed deterministic repo rollup (index, markers, complexity, clones, churn, risk). See [`report-json-v1.md`](report-json-v1.md). |
| `content import --json` | Import local external text into `content.db`. |
| `content add-markdown --json` | Import browser/fetched markdown with URL metadata into `content.db`. |
| `content search --json` | Search content DB rows. |
| `content read --json` | Read bounded content windows with per-line truncation facts. |
| `content list --json` | List imported content in the unchanged v1 flat JSON array. Default: `external_file`; `--kind all`: `external_file` then `web`. |
| `content remove --json` | Remove imported external/web content. |
| `telemetry export --jsonl` | Export raw Miller telemetry rows for Eros dashboard/history ingestion. |
| `symbols export --jsonl` | Bulk-export one row per symbol for fleet rollups (counts, kinds, doc coverage, clones). |
| `references export --jsonl` | Bulk-export one row per identifier/reference usage fact for dead-code candidate workflows. |
| `references candidates --json` | Deterministic dead-code candidate listing with named suppressions (experimental, evidence-gated; CLI-only). See [`references-candidates-v1.md`](references-candidates-v1.md). |
| `complexity export --jsonl` | Bulk-export per-symbol/per-file complexity metric rows for fleet hotspot ranking. |
| `patterns export --jsonl` | Bulk-export structural fact rows for fleet code-shape inventory. |
| `dashboard --json` | Start/reuse the local dashboard helper and return its URL. |
| `capabilities --json` | Discover this contract surface. |

`capabilities --json` reports `optional_features.reference_aware_context=true` when `context --reference-mode usage`
is available.

`capabilities --json` advertises impact through three additive, independent feature strings:

- `impact_index_revision_delta` means Miller can report the changed-path journal envelope and its unchanged
  `delta_status` completeness signal.
- `impact_traversal_evidence` means that envelope includes the v1 `traversal` object with bounded graph-execution
  evidence.
- `impact_test_role_evidence` means normal and index-revision result rows carry the v1 nested `test_evidence`
  object and result envelopes carry `test_evidence_scope`.

Eros must gate each behavior on its own string. `traversal.status: "exhausted"` is only relative to the reported
`seeded_paths` and current indexed edges. Dynamic dispatch, reflection, configuration, generated code, unresolved
references, and missing extractor edges are outside the claim. `tests[]` contains likely tests, so an empty list
does not exonerate tests; `unseeded_paths` are separate warnings. See
[`impact-traversal-evidence-v1.md`](impact-traversal-evidence-v1.md) for every field and status/reason pair.
Role flags are positive evidence only. The role scope is candidate-only and absence is unknown; compact
`likely tests` and JSON `tests[]` may contain lifecycle hooks, so use `test_evidence.test_case` when the role
feature is present. Eros—not Miller—owns runner inventory, freshness policy, scheduling, results, and verdicts.
See [`impact-test-role-evidence-v1.md`](impact-test-role-evidence-v1.md).

`patterns --json` is the stable way to consume `julie-extractors` structural facts. Eros should use this command
for known code-shape signals instead of reading Miller private SQLite tables directly.

`todos --json` is a CLI compatibility alias over `search --mode markers` for Eros/scripts. It uses the
source-region search sidecar, so callers should check
`optional_features.source_region_index` from `capabilities --json` and normal workspace sidecar health before
depending on it. It is a marker-audit surface, not a task tracker: rows identify code comments by marker,
file, line, region kind, language, containing symbol when known, and snippet text.

The `refresh --json --wait` response uses the same action shape as `workspace refresh --json`, plus
post-refresh artifact facts when available:

- `artifact_id`: workspace artifact generation id when the index artifact is known; required companion for
  Eros/Miller index-revision delta (`impact --from-index-revision` + `--from-artifact-id`).
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

`workspace onboarding --json` is read-only. It summarizes the shared telemetry ledger for the selected
workspace and may recover repeated target hashes only by matching them against the current local index. Raw
queries and raw targets are not stored or emitted.

`workspace leader --json` is read-only unless `--handoff` is supplied. With `--handoff`, Miller writes a local
request file asking the current leader to abdicate gracefully; with `--wait`, the command waits briefly for the
request to be observed. It never kills processes. Older leaders may not understand the request and can leave it
queued until timeout.

`metrics --json` surfaces deterministic local facts only: git churn over a selected commit range, identical
body-hash clone groups, and bounded complexity ranking. The churn mapper uses current-index symbols and labels
that basis explicitly. Clone and complexity outputs do not include cleanup recommendations, suppressions,
semantic similarity, or fleet history; Eros owns those workflows.

A `lock_busy` result exits `0` and its payload is ingestable: the latest readable DB is being served. Freshness is
NOT confirmed — consumers that need a confirmed-fresh index must gate on `status` (`refreshed`/`unchanged`) or
`index_fresh: true` in the payload, not on the exit code alone. Exit `3` is reserved for genuinely
unusable-index outcomes: `missing_root`, `missing_index`, `failed`, and `ineligible_extractor`.

`lock_busy` does NOT promise that anything is converging. It has two causes, and they differ on exactly that
point:

- **Busy per-workspace writer lock.** A live leader owns that workspace and keeps it fresh; for `full`, a leader
  full-scan request was enqueued for it.
- **Busy machine-wide scan admission.** Miller admits one whole-repo extractor scan (plus the sidecar
  convergence that follows it) per user at a time, so a fleet of git worktrees cannot run N concurrent
  extractors. The refusal means nobody is scanning THIS workspace right now. The payload carries `status:
  "lock_busy"`, `scanned: false`, and a `note` beginning `Machine-wide scan admission is busy`. A forced
  request (`workspace full`, `workspace open --full`) additionally queues a leader full-scan request so a Miller
  started in that root services it, and a refusal against a root with no readable index reports
  `missing_index` (exit `3`) instead — `lock_busy` exit `0` therefore never advertises a registered workspace
  with no `symbols.db`.
- **Deferred by the persisted scan-failure backoff.** An AUTOMATIC refresh — the refresh-first path behind a
  cross-workspace read with an explicit `workspace_id` — is deferred while `scan_failure.next_attempt_utc` is in
  the future. Nothing is scanning and nothing is queued; the record itself is the schedule. The `note` begins
  `The previous whole-repo scan of this workspace failed`, and a root with no readable index again reports
  `missing_index` (exit `3`) instead. A DIRECT request (`workspace refresh/full/open`, the MCP `workspace` tool,
  the dashboard) is never deferred this way.

Either way: ingestable, not confirmed-fresh, retry later. The user-global lease lives at
`~/.miller/scan/scan-v1.lock` with an advisory `scan-v1.owner.json` sidecar; `MILLER_SCAN_GOVERNOR=0` disables
admission entirely and `MILLER_SCAN_GOVERNOR_WAIT` (seconds or a `TimeSpan`) sets the budget for the one-shot CLI
and dashboard forced refresh. In-server paths (the MCP `workspace` tool, the indexer's own scans) deliberately use
a few-second budget instead and retry, so one queued scan can never stall an agent's tool call.

### `scan_governor` (additive, conditional)

`workspace status --json` and `workspace health --json` gain an OPTIONAL top-level `scan_governor` object. It is
**omitted entirely** when this process is idle, when the governor is disabled, and on every build that predates
the feature — default output is byte-identical to the previous contract, so Eros must treat its absence as "no
scan-admission contention to report", never as an error. It is NOT part of `workspace health --format json-summary`.

| Field | Type | Meaning |
|---|---|---|
| `state` | string | `waiting` (this process is queued for admission), `holding` (this process holds it), `holding_elsewhere` (another process holds it, read from the advisory owner file). There is no `idle` or `disabled` state — the object's absence covers both. |
| `reason` | string \| null | Why admission was requested — e.g. `leader-startup`, `leader-drain-rescan`, `leader-ondemand`, `leader-requested-full`, `leader-upgrade`, `bootstrap`, `bootstrap-auto-rebuild`, `cross-workspace-refresh`, `workspace-open-prime`. Advisory; do not branch on the exact token. |
| `since_utc` | string \| null | ISO-8601 UTC instant the current position began. |
| `waiting_seconds` | number \| null | Whole seconds since `since_utc`. |
| `holder_pid` | number \| null | The holder's process id, corroborated alive when rendered (possible for `waiting`/`holding_elsewhere`, always null for `holding`). |
| `holder_workspace_root` | string \| null | The workspace root that holder is scanning; null whenever `holder_pid` is. |

`holder_pid`/`holder_workspace_root` come from the advisory owner file, which is diagnostics only — a crashed or
SIGKILLed holder's OS lease is released by the kernel even though its owner file lingers. Miller therefore
corroborates the recorded pid's liveness before rendering it on EITHER arm, so a stale record naming a dead pid is
never reported as a holder. The two arms differ only in what is left once the attribution fails: for
`holding_elsewhere` the record was the sole evidence of a holder, so `scan_governor` is omitted entirely; for
`waiting` this process really is queued, so the object stays and `holder_pid`/`holder_workspace_root` render null.
Treat a present `holder_pid` as "a live process was recorded as the governor owner", never as proof of who holds
the OS lease right now. Corroboration deliberately never opens the lease, so a status read can neither block a
real acquirer nor mistake a concurrent status read for a holder. A one-shot CLI observer reports
`holding_elsewhere`, never `waiting` — `waiting` is a live in-process position only the queued process itself can
render.

`workspace health --json` additionally emits a `scan_waiting_on_machine_governor` warning at severity
`usable_with_warnings` while `state == "waiting"`. Queuing behind another worktree's scan is the governor
working as designed and the index stays readable, so it must never be read as a degraded workspace.

### `scan_failure` (additive, conditional)

`workspace status --json` and `workspace health --json` gain an OPTIONAL top-level `scan_failure` object carrying
the workspace's persisted whole-repo scan-failure record (`<workspace>/.miller/scan-failure.json`). It is
**omitted entirely** when no failure is recorded — after any successful whole-repo scan, and on every build that
predates the feature — so default output stays byte-identical to the previous contract and Eros must treat its
absence as "no recorded scan failure", never as an error. It is NOT part of
`workspace health --format json-summary`.

| Field | Type | Meaning |
|---|---|---|
| `intent` | string | Why the failed scan ran: `IncrementalReconcile`, `UserFullRebuild`, `RootRebind`, `SchemaHeal`, `CorruptionHeal`, `ExtractorUpgrade`, or `LevelUpgrade`. Treat unknown values as opaque — the set may grow. |
| `exit_code` | number \| null | The extractor's exit code; `137` is the OOM-killer/SIGKILL signature that clamps the next automatic attempt to `--jobs 1`. Null when the failure carried no exit code. |
| `consecutive_failures` | number | The current failure streak (≥ 1 whenever the object is present). Drives the backoff step. |
| `jobs` | number | The `--jobs` cap the failed attempt ran with. |
| `last_failure_utc` | string | ISO-8601 UTC instant of the most recent failure. |
| `next_attempt_utc` | string | ISO-8601 UTC instant before which no AUTOMATIC attempt runs. |
| `retry_in_seconds` | number | Whole seconds until `next_attempt_utc`, floored at `0`. |

Backoff is 30s → 2m → 10m → 30m-max, jittered upward by up to 25%, so the listed schedule is a floor. The record
is shared by every Miller process on that workspace and survives restarts — that is the point: a rebuild that
cannot succeed must not be re-forced by each fresh process. An explicit user request (`workspace full`,
`workspace refresh`, `workspace open`) bypasses the timer once but still records its own attempt.

The record is cleared only by a scan at least as strong as the one that failed: a delta reconcile clears a
delta-intent record, and only a force clears a force-intent record. So a routine `workspace refresh` against a
workspace whose `scan --force` keeps being OOM-killed leaves the throttle in place rather than erasing it.

A present `scan_failure` does NOT by itself mean the index is unreadable. On an AUTOMATIC path a retried
`UserFullRebuild` may run as a delta reconcile against a still-servable artifact; the workspace then serves the
prior artifact with degraded freshness, the `refresh`/`full` payload carries `downgraded: true` plus a note, and
the rebuild stays owed and retries on the next allowed attempt. A direct user request is never downgraded — it
runs the force scan or reports why it did not. Read the object as "scans are failing and here is when the next
one is allowed", and use the existing freshness/health fields for whether the artifact itself is usable.

### `index_level` (additive, conditional)

`workspace status --json` and `workspace health --json` gain an OPTIONAL top-level `index_level` object,
emitted ONLY while the workspace serves a SYMBOLS-level artifact (progressive indexing levels, julie-extract ≥
2.25.0). Full-level and pre-levels artifacts omit it entirely, so default output stays byte-identical; treat
its absence as "the full index is being served".

| Field | Type | Meaning |
|---|---|---|
| `level` | string | The artifact's recorded extraction level; currently always `symbols` when present. |
| `upgrade_owed` | boolean | Whether policy wants full and the background full-level upgrade rebuild is owed or running. `false` means the workspace is deliberately pinned at symbols level (`symbols-only` policy). |
| `policy` | string | The effective policy: `progressive`, `full`, or `symbols-only` (env `MILLER_INDEX_LEVELS` > per-workspace registry policy > default `progressive`). |

While the object is present, symbol definitions, search, structure, relationship edges, and all `metrics`
surfaces are complete; identifier-level reference results (trace refs, impact, inspect refs sections,
`references candidates`), source-region search, and `patterns` facts are still converging and return a
`reference_layer_converging` diagnostic instead of silently-empty results — see
[`diagnostic` on read-command JSON](#diagnostic-on-read-command-json-additive-conditional).

`miller workspace levels [--json] [--set progressive|full|symbols-only] [--clear]` shows or sets the
per-workspace policy; its JSON payload carries `operation: "levels"`, the `level_policy` object
(`effective`/`source`/`registry`), `index_level` (nullable), `level_upgrade_owed`, and `changed`
(`set`/`cleared`/null).

### `diagnostic` on read-command JSON (additive, conditional)

Read commands whose answer depends on a layer the artifact has not extracted yet return their normal payload
plus an OPTIONAL top-level `diagnostic` object and a `diagnostic_schema_version` integer. Both are omitted
entirely when the command's answer is complete, so full-level and pre-levels output stays byte-identical.
Compact (non-`--json`) output carries the same facts as trailing `diagnostic_code=` / `diagnostic_class=` /
`next:` lines appended after the result.

| Field | Type | Meaning |
|---|---|---|
| `code` | string | Stable machine token. `reference_layer_converging` is the levels code. |
| `class` | string | One of `expected_empty`, `ambiguity`, `refusal`, `unsupported`, `corruption`, `unavailable`, `internal_failure`. |
| `outcome` | string | `empty` for the first four classes, `error` for the last three. |
| `message` | string | Human-readable explanation; wording is not a contract. |
| `next_actions` | array | `{call, reason}` objects naming a recovery command. |

At symbols level the emitting commands are `search --mode markers`, `search --regions`, `todos`, `inspect`
(`--depth overview|full` only; `summary` is complete), `context`, `impact`, `trace`, and `patterns`. Exit code stays
`0` and the payload stays ingestable — `diagnostic.code = reference_layer_converging` means "this result is
empty or undercounted because the layer is still converging", NOT "this workspace has no such facts". The one
levels surface that refuses instead is `references candidates`, which exits `3` with a stderr message.

`miller patterns export` is a JSONL feed, so it signals the same degradation on **stderr** rather than in the
stream: stdout stays a pure sequence of `structural_facts` rows (empty at symbols level), the exit code stays
`0`, and stderr carries the compact rendering of the same diagnostic — a `diagnostic_code=` /
`diagnostic_class=` / `next:` block — written before the first row. A consumer parsing stdout line by line is
unaffected; a consumer that wants the degradation signal reads stderr or gates on `workspace status --json`'s
`index_level`. `symbols export` and `complexity export` read tables a symbols-level scan fully populates and
are never warned.

`miller references export` warns on stderr the same way, with its own wording, because it degrades differently:
its query unions `identifiers`, `identifier_resolutions`, `relationships`, and `pending_relationships`, and a
symbols-level scan empties the first two while leaving the rest populated. The feed therefore stays NON-EMPTY
and silently omits every identifier-derived reference — a partial answer that looks complete, which stderr
alone cannot fix for a consumer streaming stdout. So this feed ALSO carries the signal in-band: every emitted
row has an `index_level` field (`symbols` or `full`). Exit code stays `0`, stdout stays a pure JSONL stream, and
line-by-line parsers are unaffected. Treat `index_level = "symbols"` rows as an undercount of that workspace's
reference set, never as its complete one.

### `downgraded` on `refresh`/`full` (additive, conditional)

The `workspace refresh --json` / `workspace full --json` action payload gains an OPTIONAL `downgraded: true`
member, emitted ONLY when a requested from-scratch rebuild actually ran as a delta reconcile. It is omitted
entirely otherwise, so default output stays byte-identical to the previous contract. When it is present,
`scanned: true` refers to the delta that ran, NOT to the rebuild that was requested — treat `downgraded: true` as
"this workspace is serving a degraded artifact and still owes a rebuild".

## Export feeds

`miller content export [--kind KIND] [--content-workspace-id ID]` emits deterministic JSONL chunk rows for
semantic ingestion. This is a CLI-only process contract; the MCP `content` tool does not accept `export`.
Rows always end with a literal LF byte, including on Windows.
See `docs/contracts/content-corpus-v1.md` for field-level guarantees.

An export that covers workspace-derived kinds (`--kind` unset, `all`, or any `workspace_*` value) exits `3`
with an actionable stderr diagnostic when `content.db` was built from a superseded extract generation — the
state a full-rebuild promote leaves behind, which revision comparison alone cannot see because the promote
restarts julie's revision counter. Run `miller workspace refresh` and retry. Import-only exports
(`--kind external_file|web`) have no `symbols.db` counterpart and are unaffected.

Capabilities advertise this feed as:

```json
{
  "name": "content_corpus",
  "command": "miller content export",
  "format": "jsonl",
  "schema_version": 2,
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
- `is_test` — boolean; julie's cross-language positive test signal (prod/test candidate splits).
- `test_case`, `test_container`, `test_lifecycle` — additive boolean positive role facts. `test_case` is derived
  as `is_test && !test_lifecycle` in this schema.
- `test_evidence_status` — `current` or `unknown` file-evidence currency.
- `test_evidence_reason` — nullable reason for unknown currency: `file_status`, `parse_diagnostics`,
  `file_status_and_parse_diagnostics`, or `file_evidence_unavailable`.

These fields do not form runnable-test inventory. Eros owns runner discovery, freshness, scheduling, execution
results, and verdicts; false flags and zero counts are not proof of absence.

`miller references export --jsonl [--workspace-id SELECTOR] [--workspace DIR]` emits schema-2 canonical
reference assertions keyed by producer-owned `reference_site_id`. See
[`references-export-v2.md`](references-export-v2.md). Miller groups identifier, relationship, and resolution
provenance by canonical site, target, and kind; it never guesses identity from overlapping spans.

Every row additionally carries `index_level` (non-empty string, `symbols` or `full`), rendered from the
artifact's `artifact_metadata.index_level` and read once per export like `artifact_id` and `workspace_revision`.
An artifact without the key reads as `full`, since pre-levels artifacts are full-level artifacts. The field is
additive within `schema_version` 2 — no line shape or field ordering changed ahead of it — and exists because
this is the one export whose degradation is partial rather than empty (see above).

Read-command JSON is allowed to grow additive recovery fields. Current examples:

- `trace --json` includes `next_actions` for empty or diagnostic outcomes such as no path, no refs, no
  neighbours, or unsupported bridge providers.
- `content search --json` preserves its v1 process shape for complete searches:
  successful hits are a top-level array, while a genuine no-result outcome is
  a parseable object. A degraded current/all-workspace search returns the
  schema-v3 coverage object with `results`, `degraded_workspaces`, and
  `diagnostic_code=workspace_search_incomplete` when absence is unproven.
- `content read --json` parameter/source/window failures return a parseable object with `operation`, `error`,
  `diagnostic_code`, and `next_actions` when Miller can suggest recovery.
- Invalid `content list --kind` values return `diagnostic_code=invalid_content_kind`
  rather than the generic `content_error`.
- Successful `content read --json` line objects include `truncated`; the
  envelope includes `truncated_line_count`. Escape-heavy or very long lines
  return bounded successful output rather than a diagnostic.
- `patterns --json` list/no-match output may include `next_actions`; no-match search output may include
  `near_matches`, `empty_reason`, and `active_filters`.

`miller patterns export --jsonl [--workspace-id SELECTOR] [--workspace DIR]` emits one JSON line per
`structural_facts` row, ordered `(path, start_byte, structural_fact_id)`. Fields (`schema_version` 2; see
[`patterns-json-v2.md`](patterns-json-v2.md)):

- `structural_fact_id`, `path`, `language`, `pattern_id`, `capture_name`, `node_kind`.
- `containing_symbol_id` (nullable), `confidence`.
- `start_line`, `start_column`, `end_line`, `end_column`, `start_byte`, `end_byte`.
- `metadata_json` (nullable raw JSON text).

A symbols-level workspace has no `structural_facts` rows yet, so this feed is legitimately empty there and says
so on stderr. See [`diagnostic` on read-command JSON](#diagnostic-on-read-command-json-additive-conditional).

`miller complexity export --jsonl [--workspace-id SELECTOR] [--workspace DIR]` emits one JSON line per
`complexity_metrics` row (file-scope and symbol-scope; emitted broadly since julie-extract 2.3.0), ordered
`(path, start_byte, complexity_metric_id)`. Fields (`schema_version` 1):

- `complexity_metric_id`, `path`, `language`, `scope` (`file`|`symbol`), `symbol_id` (nullable; set for
  symbol scope), `algorithm_id`.
- `covered_lines`, `covered_bytes`, `decision_count`, `loop_count`, `max_nesting_depth`,
  `parameter_count` (nullable).
- `start_line`, `end_line`, `start_byte`, `end_byte`.

## Workspace selector rules

Code read commands (`search`, `todos` CLI alias, `inspect`, `context`, `impact`, `trace`, `patterns`, `metrics`, and the
`symbols`/`references`/`complexity` exports) target one workspace per call. Their `--workspace-id <selector>`
accepts a display ID, unique prefix, full workspace ID, registered root path, `current`, or `primary`. The path
alias `--workspace <path>` is normalized before selection. A selector flag supplied without a value is a usage
error (exit `2`) in every combination — it is never masked by the other selector flag and never falls back
silently to the current workspace.

The `workspace` lifecycle subcommands (`status`, `health`, `onboarding`, `leader`, `refresh`, `full`, `remove`) accept
the same selector flags: `--workspace-id` aliases `--id`, and `--workspace <path>` (normalized against the CLI's
cwd) aliases `--path`. A selector flag supplied without a value is a usage error (exit `2`); a command never
falls back silently to the current workspace when a selector was attempted.

If a caller needs workspace B while running from workspace A, it should call `workspace list --json`, choose B's
selector, and pass that selector to the read command. If B is not listed, call
`workspace open --path /absolute/repo --full --json` first, then retry the read command. The special
`--workspace-id all` selector is reserved for cross-workspace content/telemetry surfaces such as
`content search --workspace-id all` and `telemetry export --workspace-id all`; it is not a symbol/code read
selector.

`workspace list --json` reports `registered`, `matched`, `returned`, `omitted`, `omitted_errors`,
`registered_missing`, `matched_missing`, `returned_missing`, `filter`, and `limit` beside the `workspaces` array.
Every row includes `root_missing`. These totals come from one registry read plus root-existence checks; listing
never opens a workspace index. The exact missing-root totals require one synchronous existence probe per
registered row, including rows omitted by the output limit. `filter` and `limit` are `null` when inactive.
Compact output applies the default limit of 20 and prints the same primary totals plus the active filter and
limit before the returned rows.

`workspace remove` resolves a registered selector or registered root before deleting. An existing but
unregistered `.miller` directory returns `not_found` and is left untouched. The live workspace, sensitive roots,
the machine-global Miller directory, corrupt registry paths, and any workspace holding a write lease are refused
without deletion. The JSON `result` vocabulary is `removed`, `not_found`, `refused_live`, `refused_in_use`,
`refused_sensitive`, and `refused_invalid_registration`; a refusal never unregisters the row or deletes data.

`miller context <query> --json` returns ranked `symbol` pivots and neighbours with `role`, `reason`, and
`confidence`, plus a top-level evidence `disposition`. `--entry-symbol`, `--edited-files`, `--failing-test`, and
`--stack-trace` add task evidence to pivot ranking. Unresolved, ambiguous, or capped evidence appears in
`anchor_diagnostics`; an empty or insufficient result includes `next_actions`. A non-positive token budget
returns no bytes with or without `--json`. The context-specific contract, including CLI/MCP option mapping,
anchor work caps, and disposition rules, is
defined in [`context-json-v1.md`](context-json-v1.md).

`miller context <query> --reference-mode usage --json` keeps the same `bundle` array and adds mixed item types:
`implementation`, `identifier`, and `content_chunk`. Each item includes `reason` and `confidence`;
`confidence=name_based` means the identifier came from a same-name row and is a possible reference, not a
resolved target-symbol edge. `--exclude-tests` filters only this usage enrichment; it does not alter pivot
selection when reference mode is off.

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

The references export is intentionally narrow: Miller exports deterministic usage facts. Per the 2026-07-06
consensus, Miller also owns the deterministic dead-code **candidate** listing with named suppressions
(`references candidates`; [`references-candidates-v1.md`](references-candidates-v1.md), experimental and
CLI-only). Ranking beyond the deterministic rule, suppression **persistence**, candidate history, cleanup
tasks, confidence views, and multi-workspace reporting stay out of Miller.
