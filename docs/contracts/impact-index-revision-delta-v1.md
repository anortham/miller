# Miller impact index-revision delta JSON v1 contract

`miller impact --json --from-index-revision <N>` returns a typed delta envelope describing every watched,
non-ignored file that changed on disk between a base index revision `N` and Miller's current index revision.
It is the machine channel Eros continuous testing uses to decide, after Miller's index revision moves, whether a
poll should skip, narrow, or fall back to a full run — sourced from Miller's own record of per-file change
revisions, never from `git diff`.

This is a distinct mode of `impact`. It does NOT overload `--base` (which stays a git ref for `--git`); the
index-revision base rides its own flag. The mode is advertised by capability — see [Capability](#capability).

## Command

```
miller impact --workspace-id <SELECTOR> --json --from-index-revision <N>
```

- `--from-index-revision <N>`: the base index revision (a non-negative integer — the `revision_id` counter
  reported by `workspace status`/`refresh` as `revision`/`LatestRevision`). The delta covers the half-open span
  `(N, current]`.
- `--workspace-id <SELECTOR>` / `--workspace <DIR>`: the usual read-context selector.
- `--json`: selects the envelope below. Without it, a human-readable compact summary is written instead; the
  envelope is the stable machine surface.
- `--max-depth <N>` / `--limit <N>`: bound the `impacted`/`tests` reverse-reachability, as for a normal impact.

The mode is exclusive: combining `--from-index-revision` with a symbol target, `--changed-paths`, `--diff`, or
`--git`/`--base`/`--staged` is a usage error (exit 2). A missing or non-integer value is a usage error (exit 2).

## Top-level shape

```json
{
  "workspace_id": "…",
  "delta_status": "complete",
  "from_revision": 267,
  "to_revision": 273,
  "changed_paths": ["src/Service.cs", "fixtures/sample-data.csv"],
  "impacted": [],
  "tests": []
}
```

## Fields

- `workspace_id` (string): echoes the caller's `--workspace-id` selector, or the resolved workspace identity when
  no selector was passed.
- `delta_status` (string): exactly `"complete"` or `"unavailable"`. This is the ONLY completeness signal.
  Consumers must treat a missing field, or any value other than `"complete"`, as unavailable, and must NEVER infer
  completeness from the presence or emptiness of `changed_paths`.
- `from_revision` (integer): the base revision passed in.
- `to_revision` (integer): the revision the delta was ACTUALLY computed to — the current index revision at read
  time. Reported even when `delta_status` is `"unavailable"` so a consumer can compare it against the revision it
  observed and reject a stale/mismatched response. `to_revision` is NOT assumed equal to `from_revision`.
- `changed_paths` (array of strings): workspace-relative paths that changed in the span `(from_revision,
  to_revision]`. Empty whenever `delta_status` is not `"complete"`.
- `impacted` (array of objects): the existing impact shape — impacted symbols reachable from the changed paths
  (`name`, `kind`, `file`, `line`, `hop`, `symbol_id`). Empty when the delta is unavailable.
- `tests` (array of objects): the existing impact shape — likely tests reachable from the changed paths, same
  object shape as `impacted`. Empty when the delta is unavailable.

## Semantics

### R1 — truthful inclusion

`changed_paths` covers ALL watched, non-ignored file changes in the span: creates, edits, deletes, and renames
(as a delete of the old path plus a create of the new). It includes files Miller does not parse into symbols —
fixture data, config, docs — because the mechanism records what Miller watched changing on disk, not what got
indexed into the symbol graph. Such a file appears in `changed_paths` even though it contributes nothing to
`impacted`/`tests`.

### R2 — truthful exclusion

Ignored and tooling paths never appear: `.git/`, `.miller/`, `.julie/`, `target/`, `node_modules/`, `bin/`,
`obj/`, and workspace `.gitignore`/`.julieignore` matches — Miller's existing watch/ignore policy
(`WatchPathFilter`). These are never fed to the extractor and never journaled; the delta additionally re-applies
the policy so a stale journal row can never leak an ignored path into `changed_paths`.

### R3 — honest span failure

When the mechanism cannot vouch for the span it returns `delta_status: "unavailable"` with an empty
`changed_paths` — never a guessed-empty "complete" delta. Unavailable is returned when:

- the base is ahead of the current revision (`from_revision > to_revision`) — a full rebuild restarted julie's
  revision counter (a new index generation), or the base is bogus;
- the base is below the retained history floor — earlier revisions were pruned/rebuilt and the span cannot be
  reconstructed;
- the extract has no change journal (an older julie-extract predating it), the extract DB is missing, or a read
  fails mid-stream.

`to_revision` still reports the real current revision on the ahead-of-current and pruned cases so consumers can
observe the skew.

A base equal to the current revision, or a span in which nothing changed, is `"complete"` with an empty
`changed_paths` — a truthful "nothing changed since the base", distinct from unavailable.

### Generation boundary (known limitation)

The base is a bare integer with no generation token. The common rebuild case — where the restarted counter is at
or below the base — is caught as `from_after_current`. A pathological case (a rebuild restarts the counter, then
the new generation climbs back past the old base before the consumer polls again) is not detectable from the
integer alone; it is closed on the consumer side by the `to_revision` echo plus the consumer's rejection of any
response whose `to_revision` differs from the revision it observed.

## Capability

The mode is advertised in `miller capabilities --json` under the top-level `features` array as
`impact_index_revision_delta`, and registered under `json_contracts` (name `impact_index_revision_delta`,
`schema_version` 1, pointing at this doc). Consumers must enable delta-dependent behavior only when the feature
string is present; an older Miller without the mechanism omits it, so version skew degrades by negotiation rather
than by interpreting a failed or legacy-shaped response.

## Mechanism

`changed_paths` is computed from julie-extract's per-file change journal (`revision_file_changes`): each row
stamps a `path` with the `revision_id` it changed at and a `change_kind` of inserted/updated/deleted. The delta is
`SELECT DISTINCT path WHERE revision_id > from AND revision_id <= to`. Miller keeps no separate journal — the
extract already is one, so this is a per-file-revision-stamp mechanism, not a snapshot hash-diff.

## Stability

v1. Field names, `delta_status` values, and the capability/flag strings are frozen. Additive fields may appear in
a future minor; consumers must ignore unknown fields. A breaking change ships as a new `schema_version` and a new
contract doc.
