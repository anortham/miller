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
