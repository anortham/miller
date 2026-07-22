# Canary Telemetry Contract v3

Status: **frozen decision profile**. This contract inherits the complete v2 field, privacy, assignment,
attribution, retention, cohort, local-gate, and statistical definitions except where this document replaces
them. Rows from v2 and v3 are never pooled.

## Activation

- `MILLER_SEMANTIC_CANARY=decision` selects v3.
- `MILLER_SEMANTIC_CANARY=on|1` continues to select v2.
- `MILLER_SEMANTIC_CANARY=off|0`, an absent value, or an unknown value remains inactive and performs no canary
  assignment, vector probe, shadow execution, or telemetry stamp.
- `MILLER_SEMANTIC=off` continues to outrank every canary mode and guarantees no semantic work or canary
  telemetry.

## Replaced runtime fields and sampling

- `canary_contract_version` is `3` for every v3 ordinary or shadow row.
- The hybrid experiment id, assignment version, deterministic bucket derivation, 50/50 arm split, eligibility
  ladder, and serving policy are unchanged from v2.
- Identifier queries remain ineligible for semantic serving and always return the lexical result.
- Every identifier analysis unit is selected for the existing non-inferiority shadow measurement. The existing
  deterministic identifier bucket is still stamped; the selection threshold is `bucket < 100` instead of the
  v2 threshold `bucket < 10`.

## Privacy and separation

V3 metadata contains the same counters, enums, opaque semantic identity values, and cryptographic result
digests allowed by v2. It never contains query text, path text, result names, result content, workspace ids, or
raw embedding input. V2 and v3 rows form separate evidence cohorts, and no gate or export may combine them.

## Export additions

`miller telemetry canary --contract 3 --source-id ID` emits schema `3`. `ID` is exactly 32 lowercase
hexadecimal characters representing an operator-generated random 128-bit value for one telemetry ledger. It
must not be derived from a host, user, hardware identifier, workspace, or path. The envelope otherwise keeps
the frozen v2 fields and ordering, with these additions:

- `export_source_id` at the envelope root;
- `warm_total_latency_bucket_counts` on every treatment unit, containing the total-latency buckets for eligible
  warm treatment calls only. Its sum equals the unit's `embed_warmth_counts.warm` value; an empty map is valid
  when that value is zero. Control units do not carry the field.

Every v3 unit used outside its source ledger must carry a complete semantic identity: nonempty
`miller_version`, `encoder_fingerprint`, `storage_schema`, `corpus_generation`, and `fusion_profile`, plus a
positive `policy_version`. A v3 export with an incomplete identity remains locally readable, but the multi-file
combiner rejects it rather than creating a cohort that could appear to pass.

## Privacy-safe multi-export combiner

```text
miller telemetry canary combine <export.json>... [--json]
```

The combiner accepts only schema `3`, canary contract `3`, experiment `semantic_hybrid_search_v1`, the frozen
enum vocabularies, valid 12-character lowercase hexadecimal unit ids, complete identities, nonnegative scalar
counts, positive count-map entries, and units whose dates are inside their declared window. Outcome counts and
every always-written whole-population count map must sum to `calls`; conditional `rescue_kind_counts` may sum
to less but never more. Shadow status counts sum to shadow `calls`, while shadow histograms sum to the `ok`
status count. The arm must match the frozen bucket boundary. Unknown fields, enum
keys, schemas, experiments, malformed dates, inconsistent totals, duplicate unit ids within a document, and
identity/assignment conflicts fail closed.

Every document is validated before any evidence is combined. Exact byte-identical documents with the same
`(export_source_id, from_utc, to_utc)` are counted once. Different content for that key is an error. Inclusive
windows from one source must be disjoint; partial overlap is an error. Windows from different sources may
overlap. When the same `unit_id` appears under different source ids, its fixed date, class, arm, bucket, and
semantic identity must agree, then its counters are added before it becomes one statistical observation.

The aggregate partitions by the exact six-field semantic identity and reports:

- the frozen Welch success-rate clause over merged per-unit rates;
- the frozen identifier-shadow non-inferiority clause over merged unit histograms;
- control and treatment counter diagnostics, including attributed success, semantic contribution, fallback,
  rescue, backend, warmth, and latency buckets;
- one explicitly non-authoritative warm-latency screen.

The warm-latency screen computes each control unit's bucketed p95 from `total_latency_bucket_counts` and each
treatment unit's bucketed p95 from `warm_total_latency_bucket_counts`. It takes the nearest-rank median rung
(the item at index `ceil(0.5 * n)`, one-based) in each arm and reports `possible_regression` only when the
treatment rung is strictly higher. Otherwise it reports `no_higher_bucket`; below 100 control rows or 100 warm
treatment rows it reports `underpowered`. This is a coarse screen only. It cannot establish or rule out the
frozen 20% millisecond threshold, and aggregate output never contains `gate_passes`. The local v3 raw-row gate
remains authoritative.

Human and JSON aggregate output contains only cohort identities, document/source counts, suppressed-unit
counts, arm counters, count maps, bucket labels, and statistics. It never emits source ids, input filenames,
unit ids, workspace ids, query/result data, paths, or raw milliseconds.
