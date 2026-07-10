# Miller impact test-role evidence JSON v1 contract

`miller impact --json` adds positive test-role evidence to every reached row in both normal and index-revision
delta results. The envelope also states the deliberately limited scope of that evidence.

This is candidate evidence, not runnable-test inventory. A false role flag or an empty `tests[]` array means the
extractor did not positively classify that role in the current artifact; it does not prove absence.

## Capability

The independently negotiated feature string is `impact_test_role_evidence`. `miller capabilities --json` also
advertises a `json_contracts` row named `impact_test_role_evidence`, command `impact --json`, `schema_version` 1,
and this document's path. Consumers must gate this additive shape independently from
`impact_index_revision_delta` and `impact_traversal_evidence`.

## Shape

Every object in `impacted[]` and `tests[]` has this nested object in addition to the existing row fields:

```json
{
  "test_evidence": {
    "is_test": true,
    "test_case": false,
    "test_container": false,
    "test_lifecycle": true,
    "status": "current",
    "reason": null
  }
}
```

`test_evidence` has exactly these schema-v1 fields:

- `is_test` (boolean): the extractor positively classified the symbol as a test-related symbol.
- `test_case` (boolean): derived as `is_test && !test_lifecycle` for this contract.
- `test_container` (boolean): the extractor positively classified a suite/container role.
- `test_lifecycle` (boolean): the extractor positively classified a setup/teardown lifecycle role.
- `status` (string): `current` when file evidence is current; otherwise `unknown`.
- `reason` (nullable string): null for current evidence, otherwise `file_status`, `parse_diagnostics`,
  `file_status_and_parse_diagnostics`, or `file_evidence_unavailable`.

Every result-bearing normal or index-revision delta envelope contains this object, including results whose
`impacted[]` and `tests[]` arrays are empty:

```json
{
  "test_evidence_scope": {
    "status": "candidate_only",
    "absence": "unknown"
  }
}
```

Usage and note-only error envelopes that do not contain result arrays do not carry `test_evidence_scope`.

## Compatibility and ownership

- The existing `impacted[]` / `tests[]` membership, order, counts, traversal evidence, and compact text are
  unchanged. Because the legacy partition uses `is_test`, lifecycle hooks remain in compact `likely tests` and
  in JSON `tests[]`; check `test_case` before treating a row as a runnable case.
- Treat positive flags as extractor evidence. Treat false flags, unknown currency, and missing candidates as
  unknown rather than negative proof.
- Miller owns deterministic extraction consumption, graph reachability, and this candidate presentation. Eros
  owns runner inventory, freshness policy, scheduling, execution results, and test verdicts.
- Consumers must ignore additive unknown fields. Changing or removing the six nested fields or the two scope
  fields requires a new schema version.
