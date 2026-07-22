# P5 Canary Stage — operator runbook

**Date:** 2026-07-21
**Scope:** how to run the randomized-holdout semantic canary end to end — enable, observe, export, gate,
interpret — plus swapping the embedding model and rolling back.
**Field definitions are not repeated here.** The normative field, enum, window, and derivation spec is the
frozen contract [`contracts/canary-telemetry-v1.md`](../contracts/canary-telemetry-v1.md); this document is
operational only. Where the two ever differ, the contract wins.

## What the canary is

A cluster-randomized holdout that measures whether hybrid (lexical + semantic) retrieval beats lexical-only
on real agent traffic without changing what `MILLER_SEMANTIC=off` users see. Causal treatment evidence is
recorded only while `MILLER_SEMANTIC=on`. It rides the existing machine-global telemetry ledger — no new store,
no new MCP tool. Every canary row
**is** an ordinary `tool_telemetry` row with `canary_*` keys in `metadata_json`.

## Enable

Two switches, both required. The canary flag is inert unless semantic retrieval is itself active.

| Variable | Values | Effect |
| --- | --- | --- |
| `MILLER_SEMANTIC` | `off` (default) · `shadow` · `on` | `off` is a permanent zero-work guarantee. `shadow` is a non-serving operational and identifier-shadow soak; it records no causal hybrid control/treatment evidence. `on` serves the assigned arm and supplies treatment-gate evidence. |
| `MILLER_SEMANTIC_CANARY` | `off` (default) · `on` (aliases: `0`/`1`) | `on` enables causal hybrid rows when semantic mode is `on`, plus identifier-shadow sampling in `shadow` or `on`. Any unrecognized value is treated as `off` and logged once at startup. |

Rules that hold regardless of what you set:

- `MILLER_SEMANTIC=off` **outranks** the canary flag. With semantic off there is zero canary work — no
  assignment, no `canary_*` key, no shadow arm. Turning the canary on requires `MILLER_SEMANTIC` set to
  `shadow` or `on`.
- Enabling the canary changes **nothing** about served results for control-arm and ineligible calls: they
  stay byte-identical to the lexical-only path. Only treatment-arm calls under `MILLER_SEMANTIC=on` serve the
  fused hybrid ranking.

Typical operator setups:

- **Soak without changing what agents see** — `MILLER_SEMANTIC=shadow` + `MILLER_SEMANTIC_CANARY=on`.
  All served output stays lexical. This validates operational readiness and accumulates identifier-shadow
  non-inferiority evidence, but it does not accumulate causal hybrid treatment evidence.
- **Serve hybrid to the treatment half and measure** — `MILLER_SEMANTIC=on` + `MILLER_SEMANTIC_CANARY=on`.
  The 50/50 split still holds out the control arm on lexical-only.

Set both in the MCP server env block (or the shell that launches `miller serve`) and restart the server so
the process re-reads them.

### What gets recorded, and where

- **Store:** the machine-global ledger `~/.miller/telemetry.db`, table `tool_telemetry`, shared across every
  workspace on the machine. No per-workspace canary file exists.
- **Instrumented surfaces (frozen):** `search` with `op` in `auto`, `text`, `symbol`, `content`. No other
  tool participates.
- **Assignment unit:** the triple `(workspace_id, utc_date, query_class)` — one arm per unit for the whole UTC
  day. Eligible classes are `prose`, `docs_like`, `mixed`; the split is 50/50 control/treatment.
- **Identifier shadow population:** identifier-class calls are lexical-only by policy, so they can never be
  canary-eligible. When the canary is on and `MILLER_SEMANTIC` is `shadow` or `on`, ~10% of identifier
  assignment units (bucket `< 10` under `semantic_identifier_noninferiority_v1`) are sampled: the lexical
  result is served first, then a hybrid ranking is computed off to the side, compared, and discarded —
  recording overlap/top-1-changed/rank counters only. A shadow failure can never change the served result.

## Observe / export

`miller telemetry canary` reads the local ledger and always emits JSON. It is the **only** sanctioned way
canary data leaves a machine — counters and enums only, never hashes, workspace ids, paths, or raw
millisecond latencies.

```
miller telemetry canary [--json] [--from YYYY-MM-DD] [--to YYYY-MM-DD]
```

- Default window is the **last 30 days** (`--to` today, `--from` 30 days earlier). Pass `--from`/`--to`
  (`YYYY-MM-DD`, UTC) to narrow it; a malformed date is a usage error (exit 2).
- Units with fewer than 5 eligible calls are suppressed and counted in `suppressed_unit_count`, so a
  single-call unit cannot be reasoned back to one action.
- Schema v2 partitions each assignment unit into exact analysis strata by arm, bucket, full Miller build,
  encoder fingerprint, storage schema, corpus generation, fusion profile, and policy version. A missing
  identity is an explicit `null` stratum; it never borrows another row's identity. The 5-call suppression
  floor is applied after this partition, so incompatible identities are never pooled to cross the floor.
- Output is deterministic: an unchanged window re-exports byte-identically. `generated_at_utc` is derived
  from the window (00:00:00 UTC the day after `--to`), not the wall clock, so repeated exports of the same
  window match byte-for-byte.
- Only frozen-vocabulary counter keys are serialized; unknown or out-of-range metadata labels are excluded
  fail-closed (they never reach the export) and remain inspectable only in the local raw ledger.

### Retention squeeze — export before rows age out (load-bearing)

Canary rows are ordinary telemetry rows and are pruned by the existing 30-day retention policy
(`retentionDays: 30` in the ledger bootstrap). The canary adds **no** retention exception and no second
store. Consequences you must plan around:

- The maximum observable history is 30 days, so the P5 measurement window has **no slack**. Run the export
  **before** rows age out, not after — a pruned row is gone.
- A canary row and its follow-up can straddle a prune; a pruned follow-up simply counts as no conversion.
  Because pruning is time-based and arm-independent, the loss is balanced across arms and does not bias the
  effect estimate.

Operational rule: export on a cadence shorter than 30 days (weekly is comfortable) and archive the JSON off
the machine. Do not rely on being able to reconstruct an old window later.

## Gate

```
miller telemetry canary --gate [--json]
```

The gate is computed **locally and authoritatively** from raw `tool_telemetry` rows, one verdict per exact
`miller_version` cohort (never a lexicographic version comparison — version strings are not orderable as
text). Exit code is 0 whenever the gate computes; usage/bad-flag errors exit 2. Add `--json` for machine
output, omit it for the human render.

Three clauses are reported. `gate_passes` for a cohort is **success-rate pass AND warm-latency pass AND
identifier-shadow pass**. A fail, underpowered, or indeterminate clause can never produce an overall pass.
The causal success-rate and latency clauses require traffic served with `MILLER_SEMANTIC=on`; a `shadow`
soak alone cannot satisfy them.

| Clause | Passes when | Minimums (else not a pass) |
| --- | --- | --- |
| `success-rate` | Welch two-sample 95% CI lower bound on the treatment-minus-control per-unit success rate is `> 0` | ≥5 eligible calls/unit; ≥30 units/arm |
| `warm-latency` | p95 of warm treatment `duration_ms` ≤ 1.20 × p95 of all control `duration_ms` | ≥100 warm treatment rows AND ≥100 control rows |
| `identifier-shadow` | per-unit `top1_changed` 95% upper bound ≤ 0.05 AND mean `overlap_at_10` 95% lower bound ≥ 8.0 | ≥30 shadow units |

## Interpret

Each clause renders one of four verdicts:

- **`pass`** — the clause met its bar with enough data. A cohort passes the gate only when all three clauses
  are `pass`.
- **`fail`** — enough data, bar not met. For `success-rate` the CI lower bound was ≤ 0; for `warm-latency`
  the treatment p95 exceeded 1.20× control (a regression); for `identifier-shadow` a margin was breached.
- **`underpowered`** — too few *units* to decide (below the 30-unit floor for `success-rate` or
  `identifier-shadow`). Not a pass. Keep the canary running to accumulate units; do not lower the floor.
- **`indeterminate`** — too few *rows* for the `warm-latency` clause (below 100 warm-treatment or 100
  control rows). Not a pass. Same remedy: gather more traffic.

`underpowered`/`indeterminate` mean "not enough evidence yet," not "failing" — the fix is more time and
traffic, never a threshold change. The frozen analysis parameters are part of the contract; an analysis that
changes one is a different gate.

The `--json` export can *approximate* these clauses off-box (success rates exactly, latency only at bucket
resolution). Where the export and the local `--gate` disagree, the local computation is authoritative.

## Model swap

The active embedding model is selected by one env var from a fixed registry:

| Variable | Values |
| --- | --- |
| `MILLER_SEMANTIC_MODEL` | `bge-small-en-v1.5-f32` (pinned default, 384-dim) · `qwen3-0.6b-f16` (512-dim) |

- Unset, empty, or whitespace selects the pinned default. An **unknown** value selects it plus
  one stderr warning naming the known encoders; it never fails the process.
- Download the model before serving with it: `miller semantic prepare --model <id> [--json]`. Running the
  verb is the consent act; Miller never auto-downloads.

### What swapping triggers

Changing `MILLER_SEMANTIC_MODEL` changes the pinned encoder identity, which the invalidation matrix
classifies as a **shadow rebuild**: a fresh generation is built off to the side under the new encoder while
the old generation keeps serving, then converges in normally. The previous generation keeps serving until the
new one converges, except that a generation built by a different encoder is not queryable by the active one,
so semantic serving degrades to lexical-with-reason during the rebuild rather than serving uninterrupted.

### Rollback

Retained generations are the rollback path. To roll back a model swap: revert `MILLER_SEMANTIC_MODEL` to the
previous value (or unset it for the default) and restart the server. The previously retained generation for
that encoder is still on disk and serves immediately — no re-embedding needed. Rollback is therefore a
restart, not a rebuild.

### Model comparison — later phase

The registry currently holds exactly the two encoders listed above. A
broader model comparison list and the head-to-head evaluation that would justify adding or switching the
default are **out of scope for P5** and land in a later phase. Treat the two-entry registry as the shipped
surface for now; do not read the presence of the fallback entry as a recommendation to switch.

## Quick reference

```
# soak operational readiness and identifier shadow without changing served output
MILLER_SEMANTIC=shadow MILLER_SEMANTIC_CANARY=on   miller serve

# serve hybrid to the treatment half and measure
MILLER_SEMANTIC=on     MILLER_SEMANTIC_CANARY=on   miller serve

# export (do this before rows age out at 30 days)
miller telemetry canary --json --from 2026-07-01 --to 2026-07-21 > canary-2026-07.json

# read the local gate
miller telemetry canary --gate
miller telemetry canary --gate --json

# swap the embedding model
miller semantic prepare --model bge-small-en-v1.5-f32
MILLER_SEMANTIC_MODEL=bge-small-en-v1.5-f32 MILLER_SEMANTIC=shadow MILLER_SEMANTIC_CANARY=on miller serve
```
