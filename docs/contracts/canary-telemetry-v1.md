# Canary Telemetry Contract v1

Status: **frozen** (P0). This document is the complete specification of the randomized-holdout canary
that gates semantic default-on in phase P5
([design §9.1](../plans/2026-07-19-miller-semantic-integration-design.md)). Phase P2b implements exactly
what is written here; P5 computes its gate from exactly these fields.

**Frozen means frozen.** An implementer may not add, rename, or repurpose a field. Every field, enum
value, bucket edge, window length, and derivation below is a decision, not a suggestion. A genuinely
required addition is a **v2**: a new `canary-telemetry-v2.md`, a bumped `canary_contract_version`, and a
re-frozen analysis plan. Rows written under different contract versions are never pooled.

**Privacy posture (load-bearing).** Every field defined here is an enum, a counter, a version string, a
bounded bucket label, or a SHA-256 hex digest produced by the *existing* `TelemetryScope.SetTarget`
mechanism. No field carries query text, source text, symbol bodies, absolute paths, or workspace roots.
Each field below states why it cannot. This is the same rule that governs `tool_telemetry` today; the
canary adds no new class of persisted data.

---

## Storage Location

Canary data rides the existing machine-global ledger `~/.miller/telemetry.db`, table `tool_telemetry`
([`TelemetryLedger`](../../src/Miller.Server/Telemetry/TelemetryLedger.cs)). One canary row **is** the
ordinary tool-call row for the assigned call — the canary never writes rows of its own.

**No new columns.** All canary fields land in the existing `metadata_json` TEXT column under the
`canary_` key prefix, written through `TelemetryScope.SetMetadata`. Rationale: `tool_telemetry` is a
`STRICT` table whose only column addition in this program is Task 2's `miller_version`; an experiment
surface with a defined sunset does not earn fifteen columns and an `ALTER` ladder. `metadata_json` is
already the established home for per-call experiment-shaped facts (`auto_rescue_kind`,
`auto_rescue_result_count`, `empty_reason`).

Existing columns the canary **reads and reuses** (it writes none of them specially):

| Column | Canary role |
|---|---|
| `id` | Row identity; also the call's correlation id. |
| `ts` | UTC ISO-8601 timestamp. Supplies the assignment date and the attribution-window clock. |
| `workspace_id` | Assignment-unit component. Already SHA-256 of the canonical root — not a path. |
| `tool`, `op` | Surface identity (`search`/`auto`, `search`/`content`, …). |
| `outcome` | `ok` / `empty` / `error`. First half of the success event. |
| `duration_ms` | End-to-end call latency. The canary defines **no** separate total-latency field. |
| `result_count` | Served result count for the arm actually rendered. |
| `target_hash` | The call's own target hash. Unchanged semantics; see attribution below. |
| `miller_version` | Task 2's TEXT column. Cohorts are version-relative; rows without it are excluded. The stamped value is the FULL build string `semver+gitsha` (e.g. `1.14.0+abc1234`); consumers group by exact string, or split on `+` for release-level grouping. Version strings are NOT lexicographically orderable (`'1.9.0' > '1.13.0'` as TEXT) — cohort gates use exact sets, never string `>=`. |

## Activation

`MILLER_SEMANTIC_CANARY` is a two-state contract: `off | on`. Default **`off`**.

- `off` — no assignment is computed, no `canary_*` key is written, no shadow arm executes, zero added
  latency. Absence of `canary_arm` in `metadata_json` is the definitive "not in the experiment" signal.
- `on` — assignment runs for every call on an instrumented surface. `on` requires `MILLER_SEMANTIC` to be
  `shadow` or `on`; with `MILLER_SEMANTIC=off` the canary flag is inert (design §3 makes `off` a permanent
  zero-side-effect guarantee, and that outranks this flag).

`0` aliases `off`; `1` aliases `on`. Any other value is treated as `off` and logged once at startup.

Instrumented surfaces (frozen): `search` with `op` in `auto`, `text`, `symbol`, `content`. No other tool
participates in v1 — the per-tool integrations of design §6.4 ship after the gate, not inside it.

## Assignment

### Unit

The assignment unit — and therefore the **unit of analysis** — is the triple:

```
(workspace_id, utc_date, query_class)
```

`utc_date` is the `YYYY-MM-DD` prefix of the row's `ts` in UTC.

One unit is one arm for its whole day. Rationale: per-call randomization makes an agent's own session
internally inconsistent (the same phrasing answered two ways minutes apart), which corrupts the
follow-up attribution the success event depends on. Per-workspace-forever randomization yields far too
few units for a confidence interval. Workspace × day × class is the coarsest unit that still accumulates
units quickly and keeps a session coherent.

**This makes the canary a cluster-randomized experiment.** The P5 gate is computed over per-unit success
rates, never over pooled per-call rates (pooling would understate the interval by treating correlated
calls as independent). This is part of the frozen contract, not an analysis preference.

### Derivation (exact)

```
key    = experiment_id + "|" + assignment_version + "|" + workspace_id + "|" + utc_date + "|" + query_class
digest = SHA256(UTF8(key))                       // same primitive as TelemetryScope.SetTarget
bucket = (uint32 big-endian read of digest[0..4]) % 100      // 0..99
arm    = bucket < 50 ? control : treatment
```

- `assignment_version` is the decimal integer `1` in v1. Bumping it re-randomizes every unit — the escape
  hatch for a mid-program rerandomization without changing `experiment_id`.
- Fields are joined with `|`; none of the components can contain `|` (`workspace_id` is hex,
  `utc_date` is digits and hyphens, `query_class` and `experiment_id` are fixed lowercase enums).
- 50/50 split. Rationale: maximum statistical power per unit, and the treatment arm is already gated
  behind fail-open degradation (design §6.5) so an even split carries no extra user risk.
- The derivation is pure and offline-reproducible: an analyst holding `workspace_id`, `ts`, and
  `query_class` recomputes the arm exactly. `canary_bucket` is persisted anyway so a rendered row is
  self-auditing.

### Ineligible calls

A call whose `query_class` or environment makes it non-canary still records `canary_arm=ineligible` with
`canary_eligibility` naming the reason, and records `canary_query_class`. Nothing else semantic is
recorded and the served behavior is exactly today's lexical path. Ineligible rows are the denominator
for "how much of real traffic did this experiment actually cover" — they are never pooled into the gate.

## Enums (complete)

Every enum below is exhaustive. An implementation encountering a value outside its enum has a bug; it
writes the enum's designated unknown value (`unknown` where one exists) and never invents a label.

### `experiment_id`

| Value | Meaning |
|---|---|
| `semantic_hybrid_search_v1` | The randomized holdout canary. Control = lexical, treatment = hybrid. |
| `semantic_identifier_noninferiority_v1` | The identifier shadow population (§ Shadow Population). |

### `arm`

| Value | Meaning |
|---|---|
| `control` | Eligible; served the lexical-only path, byte-identical to pre-program behavior. |
| `treatment` | Eligible; served the hybrid (fused) path. |
| `shadow` | Not part of the served comparison; hybrid executed off to the side and discarded. |
| `ineligible` | Not in the experiment. `canary_eligibility` says why. |

### `query_class`

Mirrors `SemanticQueryPolicy` (design §6.2) one-for-one. Frozen at six values.

| Value | Meaning |
|---|---|
| `identifier` | Identifier-shaped (CamelCase / snake_case / dotted member path). Routed lexical-only. |
| `path` | Path- or glob-shaped. Routed lexical-only. |
| `short_token` | Below the policy's token/length floor. Routed lexical-only. |
| `prose` | Clear natural-language phrase. Hybrid-eligible. |
| `docs_like` | Prose whose vocabulary indicates documentation/config intent. Hybrid-eligible. |
| `mixed` | Ambiguous; resolved by weak-lexical-evidence per §6.2. Hybrid-eligible. |

Canary-eligible classes: `prose`, `docs_like`, `mixed`. The other three are lexical-only by policy and
are covered by the shadow population instead.

### `eligibility`

| Value | Meaning |
|---|---|
| `eligible` | Assigned to `control` or `treatment`. |
| `ineligible_query_class` | Policy routes this class lexical-only. |
| `ineligible_semantic_disabled` | `MILLER_SEMANTIC=off`. |
| `ineligible_experiment_inactive` | `MILLER_SEMANTIC_CANARY=off` for this process (no row fields written; listed for completeness of the enum, never persisted). |
| `ineligible_vectors_unavailable` | No vector artifact (absent, building, downloading, disk-blocked). |
| `ineligible_vectors_incompatible` | Artifact present but `encoder_fingerprint` does not match this reader. |
| `ineligible_circuit_open` | Embedding circuit breaker is open. |
| `ineligible_cross_workspace_no_generation` | Foreign-workspace read with no ready compatible generation (design §5.3). |
| `ineligible_surface` | Tool/op outside the instrumented surface list. |

### `fallback_reason`

Why a `treatment` call did not actually get a semantic arm, or lost it mid-flight. `none` on the healthy
path. Control rows always record `none`.

| Value | Meaning |
|---|---|
| `none` | Semantic arm ran and contributed. |
| `vectors_missing` | Artifact absent at query time. |
| `vectors_stale` | Cursor lag beyond the query-time freshness bound. |
| `vectors_incompatible` | Fingerprint mismatch discovered at open. |
| `vectors_building` | Initial/shadow build in progress; generation not queryable. |
| `model_not_prepared` | Sidecar `health` reports the model is not downloaded. |
| `circuit_open` | Breaker open at query time. |
| `embed_timeout` | Query embed exceeded the per-request deadline; lexical returned. |
| `embed_error` | Sidecar returned an application-level embed error. |
| `knn_error` | sqlite-vec query failed. |
| `disk_blocked` | Disk preflight blocked artifact use. |
| `disabled` | Semantic disabled mid-process. |
| `unknown` | Degradation with no classified reason. Any nonzero rate here is an instrumentation bug. |

### `rescue_kind`

Extends the existing `auto_rescue_kind` metadata vocabulary (see
[`SearchTool`](../../src/Miller.Server/Tools/SearchTool.cs)) with the semantic rungs of design §6.3.

| Value | Meaning |
|---|---|
| `none` | No rescue rung fired. |
| `source` | Existing lexical source-content rescue. |
| `file` | Existing lexical file-name rescue. |
| `semantic_symbol` | Symbol-card KNN rescue. |
| `semantic_docs` | Docs/config chunk KNN rescue. |
| `semantic_mixed` | Both semantic rungs contributed rows. |
| `unavailable` | Rescue was attempted but no rung could run. |

### `backend`

| Value | Meaning |
|---|---|
| `metal` | Apple Metal. |
| `vulkan` | Vulkan. |
| `cuda` | Opt-in CUDA tier. |
| `cpu` | CPU build (including the "Vulkan slower than CPU" cached fallback). |
| `none` | No embed executed on this call (control rows, or treatment rows that fell back before embedding). |

### `embed_warmth`

| Value | Meaning |
|---|---|
| `warm` | The sidecar child was already running with the model loaded when the embed was issued. |
| `cold` | This call paid sidecar start and/or model load. |
| `none` | No embed executed on this call. |

Cold and warm rows are reported separately in the gate (design §9.4); they are never averaged together.

### `latency_bucket`

One ladder, applied to both semantic timing fields. Edges are milliseconds, **left-inclusive,
right-exclusive**, over the integer millisecond value (floor of the measured duration).

| Value | Range (ms) |
|---|---|
| `lt_10` | `[0, 10)` |
| `lt_25` | `[10, 25)` |
| `lt_50` | `[25, 50)` |
| `lt_100` | `[50, 100)` |
| `lt_250` | `[100, 250)` |
| `lt_500` | `[250, 500)` |
| `lt_1000` | `[500, 1000)` |
| `lt_3000` | `[1000, 3000)` |
| `gte_3000` | `[3000, ∞)` |
| `none` | The measured operation did not run on this call. |

Rationale for these edges: `10/25/50/100` resolve the warm query-embed target band (design §4.2 targets
10–150ms), `250/500/1000` resolve degraded-but-usable, and `3000` is the top of the documented cold-start
range — anything above it is a distinct failure mode and does not need finer resolution. Buckets, not raw
milliseconds, because a raw per-call latency on a semantic arm is a weak fingerprint of query length;
the bucket is not. (`duration_ms` remains raw because it is an existing, already-accepted column.)

### `shadow_status`

| Value | Meaning |
|---|---|
| `ok` | Shadow hybrid executed and comparison counters are valid. |
| `timeout` | Shadow execution exceeded its deadline; counters absent. |
| `error` | Shadow execution failed; counters absent. |
| `skipped` | Sampled in, but prerequisites (vectors/circuit) were unavailable; counters absent. |

## Result Identifiers And Attribution

### The mechanism (existing, named)

Miller already hashes a call's target through
[`TelemetryScope.SetTarget`](../../src/Miller.Server/Telemetry/TelemetryScope.cs): lowercase SHA-256 hex
of the UTF-8 raw target string, persisted to the `tool_telemetry.target_hash` column, with the raw string
**never** persisted. `inspect target=GetUser` therefore stores `sha256("GetUser")`.
[`WorkspaceTargetHashResolver`](../../src/Miller.Indexing/WorkspaceTargetHashResolver.cs) already reverses
those digests locally against the workspace index (confidence `symbol_name_hash`) to power
`workspace onboarding`'s hot-target view.

The canary reuses this mechanism verbatim. It does not introduce a new identifier scheme, and it does not
change what `target_hash` means on any existing row.

### Served-result hashes

A canary row records the digests of the results it served, using the identical derivation, so a later
`inspect`/`content read` row's `target_hash` can be matched against them:

- `canary_result_name_hashes` — `sha256(lower-hex)` of each served result's **symbol name exactly as an
  agent would pass it to `inspect target=`** (the bare name, not a qualified id).
- `canary_result_path_hashes` — `sha256(lower-hex)` of each served result's **workspace-relative path**,
  the form `inspect target=<path>` and `content operation=read` take.

Both are recorded because a follow-up may address a result either way; matching either set counts.
Each array is capped at the **first 10 served results in served order**; `canary_result_hash_truncated`
records whether the cap bit. Rationale for 10: the compact search surface renders far fewer than 10 rows,
attribution beyond rank 10 is not a meaningful conversion signal, and the cap bounds row size.

Privacy: these are digests of identifiers that already flow through `target_hash` today, in a
machine-local database, and they are **never** included in any aggregate export (§ Export). They are not
a new exposure class — but they are also not anonymization, since a local dictionary attack over the
local index is exactly what `WorkspaceTargetHashResolver` does on purpose. That is the same trade already
accepted for `target_hash`, and it is why hashes stay local-only.

### Attribution window

**Length: 600 seconds (10 minutes)** from the canary row's `ts`.

Rationale: the follow-up being measured is an agent's next tool call in the same task — normally seconds
away, occasionally minutes when a human reads the output or an intervening build runs. Ten minutes
comfortably covers a slow agent turn while staying far below the interval at which an unrelated later
visit to the same symbol would be miscredited. Fixed and frozen because a tunable window is a
researcher-degrees-of-freedom hole in a causal gate.

### Matching rule (exact)

A canary row `C` is **attributed** a follow-up if there exists a `tool_telemetry` row `F` with all of:

1. `F.workspace_id = C.workspace_id`
2. `F.tool = 'inspect'`, **or** `F.tool = 'content'` with `F.op = 'read'`
3. `F.outcome = 'ok'`
4. `F.target_hash` is non-null and present in `C.canary_result_name_hashes` ∪ `C.canary_result_path_hashes`
5. `F.ts > C.ts` and `F.ts − C.ts ≤ 600 s`
6. `C` is the **latest** canary row satisfying 1–5 for `F` (a follow-up is credited to the most recent
   preceding canary row that served that hash, never to several)

At most **one** follow-up is attributed per canary row: the earliest `F` satisfying the above. Conversion
is a binary per-row fact, not a count — repeat visits to one result are not extra evidence.

## The Success Event

```
canary_success(C) := C.outcome = 'ok'
                     AND C.result_count > 0
                     AND an attributed follow-up exists for C per the matching rule
```

This is **derived at analysis time, not stored.** No `canary_success` field exists, and an implementer
must not add one: the second clause depends on rows that do not exist yet when `C` is written. The write
path's whole job is to persist the served hashes and the arm; the read path computes success.

This is the design §9.3 definition — downstream acceptance, not "returned something". A call that
returned results nobody looked at is not a success.

**The gate** (design §9.1, §11): per assignment unit, compute the success rate over that unit's eligible
calls; compare the distribution of per-unit rates between `control` and `treatment` units; the gate
passes on a positive treatment effect whose confidence interval excludes zero, with no >20% p95
warm-latency regression on eligible queries (warm rows only, per `canary_embed_warmth`).

## Field Reference

All keys live in `tool_telemetry.metadata_json`. "Written when" states the exact condition; a field whose
condition does not hold is **absent**, never a fabricated zero or empty string.

| Key | Type | Values | Written when | Privacy note |
|---|---|---|---|---|
| `canary_contract_version` | int | `1` | Every row with any `canary_*` key | Constant. |
| `canary_experiment_id` | enum string | see `experiment_id` | Every canary/shadow row | Fixed literal. |
| `canary_assignment_version` | int | `1` | Every canary/shadow row | Constant. |
| `canary_arm` | enum string | see `arm` | Every row while the canary is `on` | 4-value enum. |
| `canary_bucket` | int | `0`–`99` | `arm` ∈ {`control`,`treatment`,`shadow`} | Integer derived from a digest of already-persisted, non-sensitive components. |
| `canary_query_class` | enum string | see `query_class` | Every row while the canary is `on` | 6-value enum computed from the query; the query itself is discarded. Six buckets cannot reconstruct text. |
| `canary_eligibility` | enum string | see `eligibility` | Every row while the canary is `on` | 9-value enum. |
| `canary_policy_version` | int | `SemanticQueryPolicy` version | Every row while the canary is `on` | Version integer. |
| `canary_fusion_profile` | string | Versioned profile id, e.g. `rrf-mixed-v1` | `arm=treatment` and the semantic arm ran | Build-time identifier; workspace-independent. |
| `canary_encoder_fingerprint` | string | Opaque lowercase hex, ≤32 chars | `arm` ∈ {`treatment`,`shadow`} and vectors were opened | Digest of model+tokenizer identity. Contains no workspace data. |
| `canary_storage_schema` | string | Opaque lane id, e.g. `vec0-int8-256-cosine-v1` | as above | Build/config identifier. |
| `canary_corpus_generation` | string | Opaque generation id | as above | Identifier of a schema version, not of content. |
| `canary_lexical_result_count` | int | `≥0` | Every eligible row (both arms) | Counter. |
| `canary_semantic_result_count` | int | `≥0` | `arm` ∈ {`treatment`,`shadow`} and the semantic arm ran | Counter. |
| `canary_fused_result_count` | int | `≥0` | `arm=treatment` and fusion ran | Counter. |
| `canary_semantic_contribution_count` | int | `≥0` | `arm=treatment` and fusion ran | Count of served rows whose top-ranked arm was semantic. Counter. |
| `canary_fallback_reason` | enum string | see `fallback_reason` | Every eligible row (`none` on the healthy path and on all control rows) | 13-value enum. |
| `canary_rescue_kind` | enum string | see `rescue_kind` | `tool=search`, `op=auto` | 7-value enum. |
| `canary_backend` | enum string | see `backend` | Every eligible row | 5-value enum; hardware identity only. |
| `canary_embed_warmth` | enum string | see `embed_warmth` | Every eligible row | 3-value enum. |
| `canary_embed_latency_bucket` | enum string | see `latency_bucket` | Every eligible row (`none` when no embed ran) | Bucketed; raw ms withheld because it weakly correlates with query length. |
| `canary_knn_latency_bucket` | enum string | see `latency_bucket` | Every eligible row (`none` when no KNN ran) | As above. |
| `canary_result_name_hashes` | array of string | ≤10 lowercase SHA-256 hex digests | `arm` ∈ {`control`,`treatment`} and `result_count > 0` | Same digest mechanism and same local-only exposure as the existing `target_hash` column; excluded from every export. |
| `canary_result_path_hashes` | array of string | ≤10 lowercase SHA-256 hex digests | as above | As above. |
| `canary_result_hash_truncated` | bool | `true`/`false` | Whenever either hash array is written | Boolean. |
| `canary_shadow_status` | enum string | see `shadow_status` | `arm=shadow` | 4-value enum. |
| `canary_shadow_overlap_at_10` | int | `0`–`10` | `arm=shadow`, `canary_shadow_status=ok` | Counter: results common to both arms' top 10. |
| `canary_shadow_top1_changed` | bool | `true`/`false` | as above | Boolean: would the hybrid arm have changed rank 1. |
| `canary_shadow_lexical_top1_rank` | int | `1`–`50`, or `0` for "absent from the hybrid top 50" | as above | Small integer rank. |

Reused column, not a metadata key: **`miller_version`** (TEXT column on `tool_telemetry`, added by the
telemetry version-stamping task). Rows lacking it are excluded from the gate cohort — the gate is
version-relative by construction.

## Shadow Population (identifier non-inferiority)

Identifier queries are routed lexical-only by `SemanticQueryPolicy`, so they can never be canary-eligible.
Non-inferiority for them is measured by a shadow population instead.

**Procedure (frozen):**

1. Sampling. For a call with `canary_query_class=identifier`, compute the bucket using the same derivation
   with `experiment_id = semantic_identifier_noninferiority_v1`. Sampled in when `bucket < 10`
   (**10% of identifier assignment units**). Rationale: identifier calls are the highest-volume class, so
   10% of units yields ample comparisons while bounding wasted embedding work and RAM on the class the
   feature is *not* trying to improve.
2. Serve first. The lexical result is computed, rendered, and returned **before** any shadow work is
   observable. Shadow execution runs after the served result is finalized, under its own deadline
   (same per-request deadline as a query embed).
3. Shadow-execute. Embed the query, run the semantic arm, fuse — producing a hybrid ranking that is
   **discarded**.
4. Compare and record counters only: `canary_shadow_overlap_at_10`, `canary_shadow_top1_changed`,
   `canary_shadow_lexical_top1_rank`. Neither ranking is persisted.
5. Fail silent-to-the-user, loud-to-telemetry. Any shadow failure records `canary_shadow_status` and
   nothing else. A shadow failure can never change the served result, alter the row's `outcome`, or turn a
   lexical success into an error — this is the design §6.5 invariant applied to the shadow path.

Non-inferiority is evaluated offline over the shadow rows: the hybrid arm must not displace the lexical
top-1 (`canary_shadow_top1_changed`) beyond a pre-registered margin, and top-10 overlap must stay above a
pre-registered floor. Those two thresholds are set by the P0 eval-protocol task on the sealed set, not
here — this contract fixes the *measurement*, and the eval protocol fixes the *bar*.

## Retention

Canary rows are ordinary `tool_telemetry` rows and are pruned by the existing retention policy: the
bootstrap opens the ledger with `retentionDays: 30`
([`IndexBootstrapService`](../../src/Miller.Server/Hosting/IndexBootstrapService.cs)). The canary adds no
retention exception and no second store.

Consequences that P5 must plan around, stated here so they are not discovered late:

- The maximum observable attribution history is 30 days. The measurement window (design §10 P5: 30 days)
  therefore has no slack — aggregate exports must be produced **before** rows age out, not after.
- A canary row and its follow-up row can straddle a prune. Attribution is computed over what is present;
  a pruned follow-up simply counts as no conversion. Because pruning is time-based and arm-independent,
  this loss is balanced across arms and does not bias the effect estimate.

## Aggregate Export

`miller telemetry canary --json` (a CLI verb on the existing `telemetry` command — **no new MCP tool**,
per the MCP-stinginess rule) emits the frozen aggregate envelope below. This is the only sanctioned way
canary data leaves a machine.

**Export invariants (load-bearing):**

- **Counters and enums only.** No hashes, no `workspace_id`, no `workspace_root`, no paths, no
  `target_hash`, no per-call rows, no `duration_ms`.
- Units are identified by `unit_id` = the first 12 hex characters of
  `SHA256(experiment_id|assignment_version|workspace_id|utc_date|query_class)` — the same digest the
  assignment already computes, truncated. It is a join key across exports from one operator, and it does
  not identify a repository to a recipient.
- A unit with fewer than **5** eligible calls is omitted (`suppressed_unit_count` reports how many),
  so a single-call unit cannot be reasoned back to one action.

```json
{
  "schema_version": 1,
  "canary_contract_version": 1,
  "experiment_id": "semantic_hybrid_search_v1",
  "generated_at_utc": "2026-08-01T12:00:00Z",
  "window": { "from_utc": "2026-07-02", "to_utc": "2026-08-01" },
  "suppressed_unit_count": 3,
  "units": [
    {
      "unit_id": "9f2c41ab77d0",
      "utc_date": "2026-07-14",
      "query_class": "prose",
      "arm": "treatment",
      "bucket": 73,
      "calls": 41,
      "ok_calls": 37,
      "empty_calls": 4,
      "error_calls": 0,
      "attributed_success_calls": 22,
      "semantic_contribution_calls": 29,
      "encoder_fingerprint": "3f9a1c22b0e4d781",
      "storage_schema": "vec0-int8-256-cosine-v1",
      "corpus_generation": "cards-v1-chunks-v1",
      "fusion_profile": "rrf-mixed-v1",
      "policy_version": 1,
      "miller_versions": ["1.14.0+abc1234"],
      "fallback_reason_counts": { "none": 38, "embed_timeout": 3 },
      "rescue_kind_counts": { "none": 30, "semantic_symbol": 8, "semantic_docs": 3 },
      "backend_counts": { "metal": 41 },
      "embed_warmth_counts": { "warm": 39, "cold": 2 },
      "embed_latency_bucket_counts": { "lt_25": 12, "lt_50": 21, "lt_100": 6, "gte_3000": 2 },
      "knn_latency_bucket_counts": { "lt_10": 33, "lt_25": 8 }
    }
  ],
  "shadow_units": [
    {
      "unit_id": "c104ee9b2a55",
      "utc_date": "2026-07-14",
      "query_class": "identifier",
      "calls": 18,
      "shadow_status_counts": { "ok": 17, "timeout": 1 },
      "top1_changed_calls": 2,
      "overlap_at_10_histogram": { "8": 3, "9": 6, "10": 8 },
      "lexical_top1_rank_histogram": { "1": 15, "2": 2 }
    }
  ]
}
```

Count maps omit zero-valued keys. `units` is ordered by `(utc_date, query_class, unit_id)` so an unchanged
window re-exports byte-identically; `shadow_units` follows the same ordering rule.

## Stability Rules

- v1 is **frozen**. Adding, renaming, removing, or repurposing any key, enum value, bucket edge, window
  length, sampling rate, or derivation step requires a v2 document and a `canary_contract_version` bump.
- Rows of differing `canary_contract_version` are never pooled in an analysis.
- The assignment derivation is a guarantee, not an implementation detail: any consumer may recompute the
  arm from `workspace_id`, `ts`, and `canary_query_class` and must get the same answer.
- The success event is defined only here. No other document, dashboard, or query may redefine "conversion"
  for this gate.
- The export envelope's field names and ordering are stable; additive, backward-compatible fields may
  appear without a `schema_version` bump, and any removal or rename bumps it.
- Absent-vs-zero is a guarantee: a field whose write condition did not hold is omitted, never zeroed.

## Boundary

Miller owns this local, deterministic instrumentation and the local aggregate export. It does **not** own:
cross-operator pooling of exports, fleet-level significance testing, confidence/evidence presentation
surfaces, or any per-call semantic detail on the dashboard (count-level only, per ADR-0002). Those need
fleet state and stay outside Miller. The dashboard may surface canary participation counters; per-result
hashes and per-call arm detail stay CLI-only, following the dead-code-candidates precedent.
