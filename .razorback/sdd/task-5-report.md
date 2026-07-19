# Task 5 Report — Canary telemetry contract (frozen)

**Status:** COMPLETE
**Commit SHA:** none - parallel-lead-commit
**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/semantic-integration`, branch `worktree-semantic-integration`
**Files changed:** created `docs/contracts/canary-telemetry-v1.md` (only file touched; no edits to `docs/README.md`)

> Note: this file previously held an unrelated stale report ("Task 5 — Workspace list UX", from the
> `dashboard-ux-fixes` worktree). It was overwritten, as this task owns this report path.

## Implementation

Wrote the complete frozen contract following `metrics-history-v1.md` / `references-candidates-v1.md`
house style (status line + posture paragraph, sectioned spec, Stability Rules, Boundary).

Sections: Storage Location · Activation · Assignment (unit + exact derivation + ineligible calls) ·
Enums (all exhaustive) · Result Identifiers And Attribution · The Success Event · Field Reference
(28 keys, each with type/values/write-condition/privacy note) · Shadow Population · Retention ·
Aggregate Export · Stability Rules · Boundary.

Key structural decisions:

- **No new `tool_telemetry` columns.** All canary fields land in `metadata_json` under a `canary_`
  prefix via `TelemetryScope.SetMetadata`. Rationale stated in the doc: `tool_telemetry` is `STRICT`,
  its only column addition this program is Task 2's `miller_version`, and `metadata_json` already hosts
  per-call experiment-shaped facts (`auto_rescue_kind`, `empty_reason`). Existing columns are reused
  read-only (`ts`, `workspace_id`, `outcome`, `result_count`, `duration_ms`, `target_hash`,
  `miller_version`) and each reuse is tabulated.
- **Success is derived, never stored.** No `canary_success` field exists, and the doc forbids adding one
  — the conversion clause depends on rows that don't exist when the canary row is written.
- **Cluster-randomized analysis is part of the contract**, not an analysis preference: the gate is
  computed over per-unit success rates, never pooled per-call rates.

## Frozen decisions I made (design left these open)

| Decision | Value | Rationale |
|---|---|---|
| Assignment unit | `(workspace_id, utc_date, query_class)` | Per-call randomization makes an agent's own session internally inconsistent and corrupts the follow-up attribution the success event depends on; per-workspace-forever yields too few units for a CI. Coarsest unit that still accumulates units fast and keeps a session coherent. |
| Assignment derivation | `SHA256(experiment_id\|assignment_version\|workspace_id\|utc_date\|query_class)`, first 4 bytes big-endian as uint32, `% 100` → bucket; `<50` control | Same SHA-256 primitive already used by `SetTarget`; no component can contain `\|`; offline-reproducible by an analyst from persisted fields. `assignment_version` is the rerandomization escape hatch. |
| Split | 50/50 | Max power per unit; treatment is already fail-open-gated so an even split adds no user risk. |
| Attribution window | **600 s (10 min)** | Covers a slow agent turn (human read / intervening build) while staying far below the interval at which an unrelated later visit to the same symbol gets miscredited. Fixed because a tunable window is a researcher-degrees-of-freedom hole in a causal gate. |
| Attribution matching | same workspace, `tool='inspect'` or `tool='content' AND op='read'`, `outcome='ok'`, `target_hash` ∈ served-hash set, `0 < Δts ≤ 600 s`, credited to the *latest* preceding canary row; at most one follow-up per canary row (binary, not a count) | Prevents double-crediting across overlapping canary rows and prevents repeat visits inflating a single row. |
| Served-hash cap | first 10 served results, with `canary_result_hash_truncated` | Compact search renders far fewer than 10 rows; attribution past rank 10 isn't a meaningful conversion signal; bounds row size. |
| Latency bucket edges | `lt_10 / lt_25 / lt_50 / lt_100 / lt_250 / lt_500 / lt_1000 / lt_3000 / gte_3000 / none`, ms, left-inclusive right-exclusive over floored integer ms | 10–100 resolves the warm-embed target band (design §4.2: 10–150 ms); 250–1000 resolves degraded-but-usable; 3000 is the top of the documented cold-start range and above it is a distinct failure mode. |
| Shadow sampling rate | 10% of identifier assignment units (`bucket < 10` under `semantic_identifier_noninferiority_v1`) | Identifier is the highest-volume class; 10% gives ample comparisons while bounding wasted embedding work on the class the feature isn't trying to improve. |
| Activation flag | `MILLER_SEMANTIC_CANARY=off\|on`, default `off`; inert when `MILLER_SEMANTIC=off` | Mirrors the §3 `off`-means-zero-side-effects guarantee; absence of `canary_arm` is the definitive not-in-experiment signal. |
| Export unit id | first 12 hex of the assignment digest | Join key across one operator's exports; does not identify a repo to a recipient. |
| Export k-anonymity floor | units with <5 eligible calls omitted, counted in `suppressed_unit_count` | A single-call unit could otherwise be reasoned back to one action. |
| Retention | no exception — rides the existing `retentionDays: 30` prune | Stated with its two consequences (30-day max attribution history ⟹ exports must precede aging-out; straddling prunes are arm-independent so unbiased). |
| Export surface | `miller telemetry canary --json` CLI verb, no new MCP tool | MCP-stinginess rule. |
| Non-inferiority thresholds | deliberately **not** set here | Explicitly delegated to the P0 eval-protocol task on the sealed set: this contract fixes the measurement, the eval protocol fixes the bar. Flagged in-doc so it does not read as an omission. |

## Verification self-check (worker scope, doc-only)

- `grep -nEi "TBD|TODO|FIXME|XXX|<placeholder>|\?\?\?"` → **no matches**.
- All enums fully enumerated with value tables: `experiment_id` (2), `arm` (4), `query_class` (6),
  `eligibility` (9), `fallback_reason` (13), `rescue_kind` (7), `backend` (5), `embed_warmth` (3),
  `latency_bucket` (10), `shadow_status` (4).
- Every one of the 28 fields in the Field Reference has type, values, write condition, and a privacy
  note. Absent-vs-zero stated as a guarantee.
- Every referenced file path verified to exist on disk (`TelemetryLedger.cs`, `TelemetryScope.cs`,
  `WorkspaceTargetHashResolver.cs`, `SearchTool.cs`, `IndexBootstrapService.cs`, the design doc,
  `references-candidates-v1.md`). Relative links from `docs/contracts/` resolve.
- P2b-implementer read-through: assignment is computable from persisted inputs with the exact hash
  recipe; every field's write condition is unambiguous; the attribution query is expressible directly
  as SQL over `tool_telemetry` from the stated predicates; the export envelope has a concrete example
  with ordering and zero-omission rules.
- Privacy: no field carries query text, source text, or paths. The only digest fields reuse the
  existing `SetTarget` mechanism, are local-only, and are explicitly excluded from every export.

## Miller calls used + confirmations

- `search query="target_hash" mode=source` → located `TelemetryScope`, `WorkspaceTargetHashResolver`,
  `TargetHashFrequency`, `RecoveredTargetHash`, `TelemetryOnboardingReader`.
- `inspect target=src/Miller.Server/Telemetry/TelemetryScope.cs` → member list; confirmed
  `SetTarget`, `SetMetadata` (3 overloads), `SetEmptyReason`, `SetErrorCategory`, `TargetHash` property.
- `inspect target=src/Miller.Server/Telemetry/TelemetryLedger.cs` → confirmed `CreateTableDdl`,
  `Prune(int retentionDays = 30)`, `Measure`, `Record`.
- `search query="auto_rescue_kind" mode=source` → confirmed the existing rescue metadata vocabulary in
  `SearchTool.cs:318` (`auto_rescue_attempted`, `auto_rescue_kind`, `auto_rescue_result_count`) that
  `canary_rescue_kind` extends.
- `search query="CREATE TABLE tool_telemetry" mode=source` → no hits (phrase query); fell back to
  reading the DDL constant directly at `TelemetryLedger.cs:18`.

## API-shape evidence

- **Target-hash mechanism (real name/shape):** `TelemetryScope.SetTarget(string? raw)`
  (`src/Miller.Server/Telemetry/TelemetryScope.cs:193`) —
  `Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))`, stored to the `TargetHash`
  property and persisted to the `tool_telemetry.target_hash` TEXT column via `TelemetryRecord`. Doc
  comment states the raw string is NEVER persisted. Reversal path:
  `WorkspaceTargetHashResolver.Resolve(dbPath, TargetHashFrequency[])` →
  `RecoveredTargetHash(Confidence: "symbol_name_hash", …)` (`src/Miller.Indexing/`), consumed by
  `WorkspaceOnboardingAssembler`. The contract names all of these.
- **`tool_telemetry` schema:** read verbatim from `TelemetryLedger.CreateTableDdl` (line 18) — `STRICT`
  table; columns `id, ts, tool, op, workspace_id, workspace_root, duration_ms, outcome, error_kind,
  error_message, error_detail, result_count, bytes_examined, bytes_returned, source_bytes, est_tokens,
  index_fresh, target_hash, metadata_json`, with additive columns applied via `EnsureTextColumn`.
- **Task 2's column confirmed live in this worktree:** `miller_version TEXT` at `TelemetryLedger.cs:33`,
  in the insert column list at line 83, and `EnsureTextColumn(connection, "miller_version")` at line 153.
  The contract references it by exactly that name.
- **Retention value confirmed:** `IndexBootstrapService.cs:471` opens the ledger with `retentionDays: 30`.

## Judgment calls

- Chose `metadata_json` over new columns (reasoning above). If P2b later finds a query-performance
  need, that is a v2 conversation, not an implementer's call — the doc says so.
- Gave `canary_rescue_kind` its own key rather than overloading the existing `auto_rescue_kind`, so the
  frozen enum can't drift when the non-canary rescue vocabulary changes. The doc states the relationship.
- Listed `ineligible_experiment_inactive` in the eligibility enum while noting it is never persisted
  (the canary writes nothing when inactive) — kept for completeness of the analysis-side vocabulary.
- Did **not** set the non-inferiority margin/floor: those are sealed-set decisions owned by the P0 eval
  task. Delegated explicitly in-doc rather than inventing a number that would look authoritative.

## Concerns

- **30-day retention is tight for the P5 30-day measurement window** — zero slack. Flagged in-doc under
  Retention; P5 planning should schedule aggregate export on a rolling cadence, not once at the end.
- **`miller telemetry canary --json` is a new CLI verb** the contract commits P2b/P5 to building. CLI +
  export only (no MCP tool), so it stays within the stinginess rule, but the lead should confirm it is
  in scope for a later phase's plan.
- Served-result-hash arrays make canary rows larger than typical telemetry rows (≤20 digests). Bounded
  and capped, but worth a size sanity check on real fleet data during P4 shadow rollout.

## `docs/README.md` map line (for the lead → Task 1)

```
- [Canary Telemetry Contract v1](contracts/canary-telemetry-v1.md) — frozen randomized-holdout canary contract: assignment unit and derivation, arm/query-class/fallback/backend enums, target-hash attribution window, success event, per-field privacy notes, shadow non-inferiority population, retention and aggregate-export shapes.
```
