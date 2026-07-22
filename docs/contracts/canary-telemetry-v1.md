# Canary Telemetry Contract v1

Status: **frozen** (P0). This document is the complete specification of the randomized-holdout canary
that gates semantic default-on in phase P5
([design §9.1](../plans/2026-07-19-miller-semantic-integration-design.md)). Phase P2b implements exactly
what is written here; P5 computes its gate from exactly these fields.

**Frozen means frozen.** An implementer may not add, rename, or repurpose a field. Every field, enum
value, bucket edge, window length, and derivation below is a decision, not a suggestion. A genuinely
required addition is a **v2**: a new `canary-telemetry-v2.md`, a bumped `canary_contract_version`, and a
re-frozen analysis plan. Rows written under different contract versions are never pooled.

The one exception is **pre-ship amendment**: until P2b writes the first row under this contract, no data
exists that a v2 could protect, so a defect found in review is fixed in place at
`canary_contract_version` 1. Three such amendments have been made (the `miller_version` exact-set cohort
rule; the gate-computability and qualified-attribution fixes of § Where each clause is computed,
§ Frozen analysis parameters, `canary_result_qualified_hashes`, and `total_latency_bucket_counts`; the
`canary_encoder_fingerprint` derivation rule tying it to `vectors_meta.encoder_fingerprint` in
`vectors-v1.md`). Once the first canary row is written this exception is spent and the v2 rule is absolute.

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
| `duration_ms` | End-to-end call latency, and the **only** input to the warm-latency gate clause. The canary defines **no** separate total-latency metadata key: locally it reads this column raw, and the export carries a bucketed histogram of it (`total_latency_bucket_counts`). |
| `result_count` | Served result count for the arm actually rendered. |
| `target_hash` | The call's own target hash. Unchanged semantics; see attribution below. |
| `miller_version` | Task 2's TEXT column. Cohorts are version-relative; rows without it are excluded. The stamped value is the FULL build string `semver+gitsha` (e.g. `1.14.0+abc1234`); consumers group by exact string, or split on `+` for release-level grouping. Version strings are NOT lexicographically orderable (`'1.9.0' > '1.13.0'` as TEXT) — cohort gates use exact sets, never string `>=`. |

## Activation

`MILLER_SEMANTIC_CANARY` is a two-state contract: `off | on`. Default **`off`**.

- `off` — no assignment is computed, no `canary_*` key is written, no shadow arm executes, zero added
  latency. Absence of `canary_arm` in `metadata_json` is the definitive "not in the experiment" signal.
- `on` — under `MILLER_SEMANTIC=on`, assignment and hybrid row stamping run for every call on an instrumented
  surface. Under `MILLER_SEMANTIC=shadow`, only the sampled identifier-shadow population is stamped. With
  `MILLER_SEMANTIC=off` the canary flag is inert (design §3 makes `off` a permanent zero-side-effect guarantee,
  and that outranks this flag).

`MILLER_SEMANTIC=shadow` is non-serving. It may exercise vector readiness and the identifier shadow
population, but it writes no hybrid control/treatment arm evidence because every served result remains
lexical. Causal success-rate and warm-latency evidence is written only under `MILLER_SEMANTIC=on`.

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

One ladder, applied to both per-row semantic timing fields and — in the aggregate export only — to
`duration_ms` as `total_latency_bucket_counts`. Edges are milliseconds, **left-inclusive,
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
the bucket is not. (`duration_ms` remains raw **on the local row** because it is an existing,
already-accepted column; it is bucketed on the way out of the machine.)

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
  agent would pass it to `inspect target=`** (the bare name; the qualified spelling is the third array
  below).
- `canary_result_path_hashes` — `sha256(lower-hex)` of each served result's **workspace-relative path**,
  the form `inspect target=<path>` and `content operation=read` take.
- `canary_result_qualified_hashes` — `sha256(lower-hex)` of each served result's **qualified spelling**
  `<ParentName>.<Name>`, written **only for results whose qualified spelling differs from the bare name**
  (i.e. the symbol has a parent). Bare-name-only results contribute nothing to this array.

  The qualified spelling is derived, not read from a column: the artifact's `symbols` table has no
  qualified/fully-qualified name field, so the value is composed from the two real columns
  `symbols.parent_symbol_id` → the parent row's `symbols.name`, joined to the result's own `symbols.name`
  with `.`. This is deliberately the *same* one-level `Parent.Member` shape that
  [`SmartTargetResolver.ResolveQualifiedMember`](../../src/Miller.Server/Resolution/SmartTargetResolver.cs)
  already accepts on `inspect target=` — it matches on the last dot segment and the immediate parent's
  name, so a deeper spelling an agent might type (`Ns.Type.Member`) resolves through the same immediate
  parent. Only the one-level form is hashed; matching a deeper spelling is out of scope for v1 and simply
  counts as no conversion, the same conservative direction as a missed follow-up.

All three are recorded because a follow-up may address a result any of these ways, and
[`TelemetryScope.SetTarget`](../../src/Miller.Server/Telemetry/TelemetryScope.cs) hashes the *exact string
the agent passed* — an agent that disambiguates with `inspect target=Parent.Member` produces a digest that
neither the bare-name nor the path array can contain. Without the qualified array those follow-ups are
silently lost, and the loss is not guaranteed symmetric across arms (the arms serve different result sets,
so they invite different disambiguation). Matching **any** of the three sets counts.

Each array is capped at the **first 10 served results in served order**; a single shared
`canary_result_hash_truncated` flag records whether the cap bit — one flag for all three arrays, not one
per array, because the cap is applied to the served-result list itself before any hashing, so the three
arrays are always truncated at the same result boundary. (The qualified array may still be *shorter* than
the other two, because results without a parent contribute no entry; that is absence, not truncation.)
Rationale for 10: the compact search surface renders far fewer than 10 rows, attribution beyond rank 10 is
not a meaningful conversion signal, and the cap bounds row size.

Privacy: all three arrays are digests of identifiers that already flow through `target_hash` today — a
qualified spelling is just another string an agent types into `inspect target=` and that `SetTarget`
already hashes, so the qualified array is the same mechanism and the same exposure as the other two — held
in a machine-local database, and **never** included in any aggregate export (§ Export). They are not
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
4. `F.target_hash` is non-null and present in `C.canary_result_name_hashes` ∪
   `C.canary_result_path_hashes` ∪ `C.canary_result_qualified_hashes` (membership in **any** of the three
   arrays counts; a hash present in more than one array is still one match)
5. `F.ts > C.ts` and `F.ts − C.ts ≤ 600 s`
6. `C` is the **latest** canary row satisfying 1–5 for `F` (a follow-up is credited to the most recent
   preceding canary row that served that hash, never to several)

At most **one** follow-up is attributed per canary row: the earliest `F` satisfying the above. Conversion
is a binary per-row fact, not a count — repeat visits to one result are not extra evidence.

**Conformance cases.** An implementation of the write path and the analysis must reproduce all of these
for a canary row that served the method `Save` on class `LedgerWriter` in
`src/Miller.Server/Telemetry/LedgerWriter.cs`:

- *Bare target.* `inspect target=Save` within the window ⟹ attributed, via
  `canary_result_name_hashes` containing `sha256("Save")`.
- *Qualified target.* `inspect target=LedgerWriter.Save` within the window ⟹ attributed, via
  `canary_result_qualified_hashes` containing `sha256("LedgerWriter.Save")`. This case fails to attribute
  if the qualified array is omitted — it is the reason the array exists.
- *Path target.* `inspect target=src/Miller.Server/Telemetry/LedgerWriter.cs` or
  `content operation=read` on that path within the window ⟹ attributed, via
  `canary_result_path_hashes`.
- *Top-level result.* A served result with no parent (e.g. the class `LedgerWriter` itself) contributes a
  name hash and a path hash and **no** qualified-array entry; `inspect target=LedgerWriter` still attributes
  through the name array.
- *Deeper spelling.* `inspect target=Miller.Server.Telemetry.LedgerWriter.Save` does **not** attribute in
  v1 (only the one-level `Parent.Member` form is hashed) and counts as no conversion.
- *Double counting.* A follow-up whose hash appears in two of the three arrays is one attribution, and the
  canary row is still credited at most one follow-up overall.

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
warm-latency regression on eligible queries (warm rows only, per `canary_embed_warmth`), and with the
identifier non-inferiority clause passing. Every clause must pass; underpowered and indeterminate are not
passes.

### Where each clause is computed (load-bearing)

The gate has two computation surfaces and they are not interchangeable:

- **Local (authoritative).** The gate is computed by reading raw `tool_telemetry` rows on the machine
  that wrote them. Local rows carry `duration_ms`, `outcome`, `result_count`, `miller_version`, the
  `canary_*` metadata, and the served-result hashes needed for attribution. Every clause below is defined
  over these rows, and a clause's stated value is the local value.
- **Export (approximation).** `miller telemetry canary --json` (§ Aggregate Export) carries per-unit
  counters only. It can *approximate* both causal clauses — success rates exactly, latency only at bucket
  resolution — and a pooled multi-operator read is by construction an approximation of the local gates it
  aggregates. Where the two disagree, the local computation is authoritative and the disagreement is
  reported, not silently resolved.

**Success-rate clause (local).** Restrict to eligible rows (`canary_eligibility=eligible`) with a
`miller_version` in the cohort's exact version set. Group by assignment unit. A unit is included when it
has at least the minimum eligible calls fixed below. The unit's success rate is
`attributed successes ÷ eligible calls` per § The Success Event. The treatment effect is the difference of
arm means over per-unit rates; the interval is the frozen estimator below. Per-call pooling is forbidden
(§ Assignment).

**Warm-latency clause (local).** This clause is about *end-to-end call latency*, the existing raw
`tool_telemetry.duration_ms` column — the canary defines no separate total-latency metadata key and needs
none locally. Exactly:

- Treatment population: eligible `treatment` rows with `canary_embed_warmth = warm`.
- Control population: **all** eligible `control` rows. Control rows never embed, so they always record
  `canary_embed_warmth = none` and have no warm/cold split to condition on; the whole control arm *is*
  the steady-state lexical baseline the warm treatment arm must not degrade.
- Statistic: nearest-rank p95 over the ascending integer `duration_ms` values of each population —
  the value at index `ceil(0.95 × n)` (1-based), with no interpolation.
- Clause passes when `p95(treatment warm) ≤ 1.20 × p95(control)`. It is **indeterminate** (and the gate
  therefore does not pass) below the minimum row counts fixed below.

**Warm-latency clause (export approximation).** The export carries `total_latency_bucket_counts` per unit:
the `latency_bucket` ladder applied to `duration_ms` over that unit's eligible calls. A unit's bucketed
p95 is the bucket that contains its nearest-rank p95 — computed by walking the ladder in ascending order
and taking the first bucket whose cumulative count reaches `ceil(0.95 × calls)`. At export granularity the
regression check compares the *median across units* of each arm's bucketed p95 rung, and flags a possible
regression when the treatment rung is strictly higher than the control rung. This is a coarse screen, not
the gate: one ladder rung spans up to a 2.5× range, so it can neither confirm nor rule out a 20%
regression on its own. A flag means "go compute the local clause", nothing more.

**Identifier non-inferiority (local, shadow population).** Computed over shadow rows per
§ Shadow Population, with the population, estimator, and margins fixed below.

### Frozen analysis parameters

These are part of the frozen contract. An analysis that changes one of them is not this gate.

| Parameter | Frozen value | Source |
|---|---|---|
| Unit of analysis | assignment unit `(workspace_id, utc_date, query_class)` | § Assignment |
| Minimum eligible calls for a unit to enter the analysis | **5** | Same floor as the export's `suppressed_unit_count` rule, so local and exported analyses include the same units |
| Minimum included units per arm | **30** | Judgment call (design is silent). Below this the t-interval is too wide to be informative and the gate is reported as underpowered, never as a pass |
| Treatment-effect estimator | difference in arm means of per-unit success rates | Design §9.1 ("per-unit", cluster-randomized) |
| Confidence interval | **Welch two-sample 95% t-interval** over per-unit rates (unequal variances, Welch–Satterthwaite df), two-sided | Judgment call (design says "confidence interval excludes zero" without naming a method). Welch because arm variances differ whenever the arms differ in unit size mix |
| Success-rate pass rule | interval **lower bound > 0** | Design §9.1, §11 |
| p95 estimator | **nearest-rank**, index `ceil(0.95 × n)` (1-based), ascending, no interpolation | Judgment call. Nearest-rank because `duration_ms` is integer and interpolation invents values between observed samples |
| Warm-latency regression threshold | `p95(treatment warm) ≤ 1.20 × p95(control)` | Design §9.1/§11 ("no >20% p95 warm-latency regression") |
| Minimum rows for the latency clause | **100** eligible warm treatment rows **and** 100 eligible control rows | Judgment call. Below 100, a nearest-rank p95 is determined by fewer than 5 observations |
| Identifier non-inferiority population | shadow rows with `canary_shadow_status = ok`, grouped into shadow units `(workspace_id, utc_date, identifier)`, same 5-call floor | Judgment call (population was previously unstated) |
| Identifier non-inferiority margins | per-unit `top1_changed` rate: **95% t-interval upper bound ≤ 0.05**; per-unit mean `overlap_at_10`: 95% t-interval lower bound **≥ 8.0**; minimum **30** shadow units | Judgment call. These are the *field-telemetry* margins over shadow rows; the sealed-set retrieval bar remains the eval protocol's (§ Shadow Population) and the two are reported separately, never merged |

Cohort rule: all local clauses are computed within one exact `miller_version` set and one
`canary_contract_version`. Rows outside the set are excluded before any statistic is computed. The aggregate
export applies the stricter semantic-identity partition defined below before suppression or aggregation.

## Field Reference

All keys live in `tool_telemetry.metadata_json`. "Written when" states the exact condition; a field whose
condition does not hold is **absent**, never a fabricated zero or empty string.

| Key | Type | Values | Written when | Privacy note |
|---|---|---|---|---|
| `canary_contract_version` | int | `1` | Every row with any `canary_*` key | Constant. |
| `canary_experiment_id` | enum string | see `experiment_id` | Every canary/shadow row | Fixed literal. |
| `canary_assignment_version` | int | `1` | Every canary/shadow row | Constant. |
| `canary_arm` | enum string | see `arm` | Every instrumented row under semantic `on`; sampled identifier-shadow rows under semantic `shadow` | 4-value enum. |
| `canary_bucket` | int | `0`–`99` | `arm` ∈ {`control`,`treatment`,`shadow`} | Integer derived from a digest of already-persisted, non-sensitive components. |
| `canary_query_class` | enum string | see `query_class` | Every stamped canary/shadow row | 6-value enum computed from the query; the query itself is discarded. Six buckets cannot reconstruct text. |
| `canary_eligibility` | enum string | see `eligibility` | Every stamped canary/shadow row | 9-value enum. |
| `canary_policy_version` | int | `SemanticQueryPolicy` version | Every stamped canary/shadow row | Version integer. |
| `canary_fusion_profile` | string | Versioned profile id, e.g. `rrf-mixed-v1` | `arm=treatment` and the semantic arm ran | Build-time identifier; workspace-independent. |
| `canary_encoder_fingerprint` | string | Opaque lowercase hex, ≤32 chars: the first 16 hex chars of `vectors_meta.encoder_fingerprint` (`vectors-v1.md`) with its `sha256:` tag stripped | `arm` ∈ {`treatment`,`shadow`} and vectors were opened | Digest of model+tokenizer identity. Contains no workspace data. |
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
| `canary_result_qualified_hashes` | array of string | ≤10 lowercase SHA-256 hex digests | as above, and only for served results whose `<ParentName>.<Name>` differs from the bare name (absent when no served result has a parent) | As above — a qualified spelling is an identifier that already flows through `target_hash`; same mechanism, same local-only exposure, excluded from every export. |
| `canary_result_hash_truncated` | bool | `true`/`false` | Whenever any hash array is written; one shared flag for all three arrays | Boolean. |
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
pre-registered floor. Two distinct bars exist and are reported separately, never merged:

- **Sealed-set retrieval bar** — set by the P0 eval-protocol task on the sealed acceptance set, not here.
  This contract fixes the *measurement*; the eval protocol fixes that *bar*.
- **Field-telemetry margins** over the shadow rows defined by this contract — frozen in
  § Frozen analysis parameters, because a gate clause computed from fields defined here must be computable
  from this document alone.

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
  `target_hash`, no per-call rows, **no raw `duration_ms` — bucketed total-latency counters only**
  (`total_latency_bucket_counts`, the `latency_bucket` ladder applied to `duration_ms` over the unit's
  eligible calls). A per-call millisecond value never leaves the machine; a bucket histogram over ≥5 calls
  is a counter like every other field here, and it is what makes the latency clause approximable off-box
  at all (§ Where each clause is computed).
- Rows are partitioned into exact analysis strata by `(workspace_id, utc_date, query_class, arm, bucket,
  miller_version, encoder_fingerprint, storage_schema, corpus_generation, fusion_profile, policy_version)`.
  Null is a distinct, explicit identity value and is never filled from another row. This rule applies to
  hybrid and shadow rows.
- Units are identified by `unit_id` = the first 12 hex characters of SHA-256 over a canonical
  length-prefixed encoding of `experiment_id`, `assignment_version`, and every field in that exact stratum.
  Null is encoded distinctly from an empty string. The resulting value is a stable join key across exports
  from one operator and does not identify a repository to a recipient.
- A stratum with fewer than **5** eligible calls is omitted (`suppressed_unit_count` reports how many),
  so a single-call unit cannot be reasoned back to one action.

```json
{
  "schema_version": 2,
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
      "miller_version": "1.14.0+abc1234",
      "encoder_fingerprint": "3f9a1c22b0e4d781",
      "storage_schema": "vec0-int8-256-cosine-v1",
      "corpus_generation": "cards-v1-chunks-v1",
      "fusion_profile": "rrf-mixed-v1",
      "policy_version": 1,
      "fallback_reason_counts": { "none": 38, "embed_timeout": 3 },
      "rescue_kind_counts": { "none": 30, "semantic_symbol": 8, "semantic_docs": 3 },
      "backend_counts": { "metal": 41 },
      "embed_warmth_counts": { "warm": 39, "cold": 2 },
      "embed_latency_bucket_counts": { "lt_25": 12, "lt_50": 21, "lt_100": 6, "gte_3000": 2 },
      "knn_latency_bucket_counts": { "lt_10": 33, "lt_25": 8 },
      "total_latency_bucket_counts": { "lt_100": 9, "lt_250": 27, "lt_500": 3, "gte_3000": 2 }
    }
  ],
  "shadow_units": [
    {
      "unit_id": "c104ee9b2a55",
      "utc_date": "2026-07-14",
      "query_class": "identifier",
      "miller_version": "1.14.0+abc1234",
      "encoder_fingerprint": null,
      "storage_schema": null,
      "corpus_generation": null,
      "fusion_profile": null,
      "policy_version": 1,
      "calls": 18,
      "shadow_status_counts": { "ok": 17, "timeout": 1 },
      "top1_changed_calls": 2,
      "overlap_at_10_histogram": { "8": 3, "9": 6, "10": 8 },
      "lexical_top1_rank_histogram": { "1": 15, "2": 2 }
    }
  ]
}
```

`total_latency_bucket_counts` is written for every exported unit in both arms (control rows have a
`duration_ms` like any other row), and its counts sum to the unit's `calls`. The other bucket maps stay
semantic-only: `embed_latency_bucket_counts` and `knn_latency_bucket_counts` are absent-or-`none`-heavy on
control units by construction. All three are separate marginal distributions — the export cannot express a
joint (warmth × total latency) distribution, which is exactly why the exported latency check is labeled an
approximation and the local raw-row clause is authoritative.

Count maps omit zero-valued keys. `units` is ordered by `(utc_date, query_class, unit_id)` so an unchanged
window re-exports byte-identically; `shadow_units` follows the same ordering rule. The six identity fields
are always present in schema v2; unknown values serialize as JSON `null` rather than disappearing or borrowing
the first non-null value in a group.

## Stability Rules

- The row-level canary contract v1 is **frozen**. Adding, renaming, removing, or repurposing any metadata key,
  enum value, bucket edge, window
  length, sampling rate, or derivation step requires a v2 document and a `canary_contract_version` bump.
- Rows of differing `canary_contract_version` are never pooled in an analysis.
- The assignment derivation is a guarantee, not an implementation detail: any consumer may recompute the
  arm from `workspace_id`, `ts`, and `canary_query_class` and must get the same answer.
- The success event is defined only here. No other document, dashboard, or query may redefine "conversion"
  for this gate.
- Aggregate export schema v2 is stable. Additive, backward-compatible fields may appear without another
  `schema_version` bump; a removal, rename, or identity-grouping change requires one.
- Absent-vs-zero is a guarantee: a field whose write condition did not hold is omitted, never zeroed.

## Boundary

Miller owns this local, deterministic instrumentation and the local aggregate export. It does **not** own:
cross-operator pooling of exports, fleet-level significance testing, confidence/evidence presentation
surfaces, or any per-call semantic detail on the dashboard (count-level only, per ADR-0002). Those need
fleet state and stay outside Miller. The dashboard may surface canary participation counters; per-result
hashes and per-call arm detail stay CLI-only, following the dead-code-candidates precedent.
