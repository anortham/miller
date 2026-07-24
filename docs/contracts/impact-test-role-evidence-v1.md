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

Every reached row also carries the graph or candidate evidence used to rank it:

```json
{
  "impact_evidence": {
    "reached_via_symbol_id": "seed-id",
    "edge_kind": "test_linkage",
    "edge_confidence": 0.95,
    "edge_source": "test_linkage",
    "tier": "exact",
    "centrality": 4,
    "visibility": "public"
  }
}
```

Exact test links are read only when an `is_test` symbol's `metadata_json` contains labeled `test_linkage` or
`test_coverage` evidence. Supported values are a symbol-id string, an array, or an object containing
`symbol_id`, `target_symbol_id`, `source_symbol_id`, or `symbol_ids`, with optional `confidence`. The current
extractor artifact emits neither key, so this tier is honestly dormant unless evidence is present.

After exact graph reachability, Miller may fill remaining result capacity with filename/role candidates such as
`ServiceTests` for `Service`. Those rows are always labeled `edge_kind: "test_candidate"`,
`edge_source: "filename_role"`, `tier: "heuristic"`, and confidence `0.35`; they are never blended with exact
links.

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

- Graph results keep hop primary, then rank peers by relationship priority, centrality, visibility, and stable
  location. Separately labeled filename/role candidates may add likely tests after graph results within `limit`.
  Lifecycle hooks remain in compact `likely tests` and JSON `tests[]`; check `test_case` before treating a row
  as a runnable case.
- Treat positive flags as extractor evidence. Treat false flags, unknown currency, and missing candidates as
  unknown rather than negative proof.
- Miller owns deterministic extraction consumption, graph reachability, and this candidate presentation. Eros
  owns runner inventory, freshness policy, scheduling, execution results, and test verdicts.
- Consumers must ignore additive unknown fields. Changing or removing the six nested fields or the two scope
  fields requires a new schema version.
