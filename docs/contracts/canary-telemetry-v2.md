# Canary Telemetry Contract v2

Status: **frozen**. This is the active semantic-canary contract. It inherits the complete v1 field, privacy,
assignment, attribution, retention, export, and statistical definitions except where this document replaces
them. Rows from v1 and v2 are never pooled.

## Why v2 exists

The v1 local gate grouped only by exact Miller version while the aggregate export separated semantic
identities. Grouping same-version runs from different encoders or vector schemas could falsely pass. Simply
partitioning v1 rows fails closed forever because control rows did not record the generation the treatment arm
would have used. V2 makes the randomized comparison both exact and computable.

## Replaced field conditions

- `canary_contract_version` is `2`.
- Every eligible control or treatment row records `canary_encoder_fingerprint`, `canary_storage_schema`,
  `canary_corpus_generation`, and `canary_fusion_profile` from the ready vector generation inspected during
  eligibility. A control request reads metadata only; it does not launch the model, embed, run KNN, or alter
  lexical output.
- A shadow row that opens vectors records the same four identity fields, including `canary_fusion_profile`.
- Identity fields remain absent when no compatible ready generation was available. Null remains a distinct
  value and is never filled from another call.
- For a foreign-workspace read, `ineligible_cross_workspace_no_generation` takes precedence over generic
  missing, building, or incompatible vector states. This distinguishes an intentionally read-only foreign
  corpus from a current workspace whose resident writer should converge vectors.

## Replaced cohort rule

The local authoritative gate partitions rows by the exact tuple
`(miller_version, encoder_fingerprint, storage_schema, corpus_generation, fusion_profile, policy_version)` and
`canary_contract_version=2`. All success-rate, warm-latency, and identifier-shadow clauses are computed inside
one tuple. The JSON and human gate reports identify all six identity values. A null-identity cohort is reported
separately and cannot borrow rows from a complete cohort.

The aggregate export uses the same tuple in its existing analysis-unit key. Its envelope schema remains v2;
the row-level `canary_contract_version` inside that envelope is now `2`.

## Promotion consequence

Only v2 rows count toward promotion. Existing v1 traffic remains useful as historical diagnostics but cannot
pass the repaired gate. Underpowered, indeterminate, mixed-identity, or null-identity cohorts are not passes.
