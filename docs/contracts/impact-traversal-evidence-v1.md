# Miller impact traversal evidence JSON v1 contract

`miller impact --json --from-index-revision <N>` adds a `traversal` object to the index-revision delta envelope.
It reports whether Miller's bounded reverse traversal exhausted the graph frontier it could see, which changed
paths seeded that traversal, and which changed paths could not seed it.

> **Scope of an exhausted result:** `status: "exhausted"` is only relative to the reported `seeded_paths` and
> the current indexed edges. Dynamic dispatch, reflection, configuration, generated code, unresolved references,
> and missing extractor edges are outside the claim. `tests[]` contains **likely tests**; an empty array does not
> exonerate tests. Treat `unseeded_paths` as separate warnings, not as paths covered by the exhausted traversal.

This is deterministic execution evidence, not a semantic completeness or safety verdict. The existing
`delta_status` meaning is unchanged: it says whether Miller can vouch for the changed-path revision span, while
`traversal.status` says what happened after those paths were handed to the current indexed graph.

## Capability and command

The feature string is `impact_traversal_evidence`. It is advertised independently of
`impact_index_revision_delta` in the top-level `features` array from `miller capabilities --json`. The capability
response also contains a `json_contracts` row named `impact_traversal_evidence`, command
`impact --json --from-index-revision N --from-artifact-id ID`, `schema_version` 1, and this document's path.

The full invocation remains:

```text
miller impact --workspace-id <SELECTOR> --json \
  --from-index-revision <N> --from-artifact-id <ID> \
  [--max-depth <N>] [--limit <N>]
```

See [`impact-index-revision-delta-v1.md`](impact-index-revision-delta-v1.md) for the enclosing delta fields,
artifact-generation guard, usage errors, and `delta_status` rules.

## Shape

```json
{
  "traversal": {
    "status": "exhausted",
    "reason": "complete",
    "max_depth": 2,
    "limit": 100,
    "reached_count": 12,
    "returned_count": 12,
    "truncated_by_depth": false,
    "truncated_by_limit": false,
    "seeded_paths": ["src/Service.cs"],
    "unseeded_paths": ["fixtures/sample-data.csv"]
  }
}
```

`traversal` has exactly these fields in schema v1:

- `status` (string): exactly `exhausted`, `truncated`, or `not_run`; interpret it with `reason` using the matrix
  below.
- `reason` (string): exactly `complete`, `depth`, `limit`, `depth_and_limit`, `delta_unavailable`, `no_changes`,
  `index_unavailable`, or `no_seeds`.
- `max_depth` (integer): the effective reverse-traversal depth bound. Values below 1 are normalized to 1.
- `limit` (integer): the effective cap on graph nodes returned for `impacted[]` plus `tests[]`. Values below 1
  are normalized to 1.
- `reached_count` (integer): total non-seed graph nodes reached before the `limit` prefix is applied. It is 0 when
  traversal was not run.
- `returned_count` (integer): graph nodes actually rendered across `impacted[]` and `tests[]` after the limit and
  indexed-symbol lookup. It is 0 when traversal was not run.
- `truncated_by_depth` (boolean): true when a node at `max_depth` had an unseen indexed neighbour, so deeper
  traversal was omitted.
- `truncated_by_limit` (boolean): true when `reached_count` exceeded `limit`.
- `seeded_paths` (array of strings): changed paths with one or more current indexed symbols used as traversal
  seeds. Exhaustion is relative only to these paths.
- `unseeded_paths` (array of strings): changed paths that had no current indexed symbols and therefore did not
  seed traversal. These are separate warnings; they do not make an otherwise exhausted graph traversal cover
  those paths.

## Status and reason matrix

Only these pairs are valid:

| `status` | `reason` | Meaning |
|---|---|---|
| `exhausted` | `complete` | Traversal found no unseen neighbour beyond the reported seeded paths within the current indexed edges, and the result was not cut by `limit`. This is the scoped claim described above, not whole-program completeness. |
| `truncated` | `depth` | An unseen indexed neighbour existed beyond `max_depth`; the limit did not truncate the result. |
| `truncated` | `limit` | More graph nodes were reached than `limit`; the depth bound did not leave an unseen indexed neighbour. |
| `truncated` | `depth_and_limit` | Both the depth boundary and result limit truncated the traversal. |
| `not_run` | `delta_unavailable` | `delta_status` is `unavailable`; no changed-path traversal was attempted. |
| `not_run` | `no_changes` | The delta is complete but `changed_paths` is empty. |
| `not_run` | `index_unavailable` | Changed paths exist, but no usable current symbol index/graph was available. |
| `not_run` | `no_seeds` | A usable index existed, but every changed path was unseeded. |

When `status` is `not_run`, both truncation flags are false, both counts are 0, and `seeded_paths` /
`unseeded_paths` reflect only information available before the reason stopped traversal. In particular,
`no_seeds` reports the changed paths in `unseeded_paths`; earlier stop reasons may leave both path arrays empty.

## Consumer rules

- Gate use of `traversal` on `impact_traversal_evidence`; gate changed-path span semantics separately on
  `impact_index_revision_delta` and `delta_status`.
- Do not reinterpret `delta_status: "complete"` as graph completeness, and do not reinterpret
  `traversal.status: "exhausted"` as revision-span completeness.
- Treat every `unseeded_paths` entry as a separate warning/fallback input even when `status` is `exhausted`.
- Treat `tests[]` as likely tests. An empty `tests[]` does not prove that no tests are affected.
- Treat unknown `status`/`reason` values conservatively. Additive top-level or traversal fields may appear in a
  future minor; consumers must ignore unknown fields.

## Stability

Schema v1 freezes the ten traversal field names and the status/reason pairs above. A breaking change requires a
new `schema_version` and contract document.
