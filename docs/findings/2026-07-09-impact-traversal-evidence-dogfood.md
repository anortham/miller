# Impact traversal evidence dogfood (2026-07-09)

## Result

The additive impact-delta traversal evidence passed local Eros-facing dogfood against live registered workspaces:

- a seeded source delta exhausted the reachable graph with wide bounds;
- the same delta reported depth and limit truncation when deliberately bounded;
- a watched comment-only config path with no symbols reported `not_run` / `no_seeds` and remained visible in `unseeded_paths`.

These facts describe deterministic traversal of Miller's extracted relationship graph. **`exhausted` does not mean semantic test-impact completeness.** Missing extraction, unresolved references, dynamic dispatch, generated code, runtime wiring, and other graph gaps can still hide real impact.

## Binary and contract evidence

The probe used the already-built Debug output from feature commit `bc199f804cba224613235566e6f23cc47e69795c`, copied to `/tmp/miller-task4-bc199f8` with the pinned extractor placed beside it under `.tools/`. No release or pin was changed.

```text
$ /tmp/miller-task4-bc199f8/miller version
1.6.0+bc199f804cba

$ /tmp/miller-task4-bc199f8/.tools/julie-extract --version
julie-extract 2.11.0
```

Current CLI help proved the delta flags before use:

```text
usage: miller impact <symbol>|--changed-paths PATH[,PATH...]|--diff DIFF|--git [--base REF] [--staged]|--from-index-revision N [--from-artifact-id ID] [--workspace-id SELECTOR] [--workspace DIR] [--max-depth N] [--limit N] [--json]
```

The copied `bc199f8` probe binary intentionally predates the parallel Task 3 capability-advertisement lane. Its `miller capabilities --json` output therefore advertised only feature `impact_index_revision_delta` and JSON contract `impact_index_revision_delta`, schema version 1, command `impact --json --from-index-revision N --from-artifact-id ID`. This is historical probe-binary evidence, not the final combined behavior: Task 3 and the final branch gates separately verify that the combined branch advertises `impact_traversal_evidence`.

## Initial registered workspace facts

The exact discovery command was:

```text
/tmp/miller-task4-bc199f8/miller workspace list --json
```

Each row below was then confirmed with `workspace status --workspace-id <selector> --json`. All status payloads reported server version `1.6.0+bc199f804cba`.

| Workspace | Selector used | Full workspace ID | Initial artifact ID | Initial revision |
|---|---|---|---|---:|
| Eros | `c9b7c0ec5c56` | `c9b7c0ec5c564fe35f97ca3e29d2ce9b48162ede26b39b67b0fef545c5638d6e` | `artifact-1783611385044716000` | 4 |
| Miller | `b275269b2d7c` | `b275269b2d7c4ead963d69f68620a4d6c6616e64611daf7cec877722a5b4c044` | `artifact-1783512853054110000` | 168 |
| julie-extractors | `91c17adbdab9` | `91c17adbdab980133df7f38d3b51be0b614b50eb888e9d98ca9e7f216bbc1ec4` | `artifact-1783618925952878000` | 7 |

## Probe 1: seeded source, exhausted traversal

Miller-first selection used:

```text
/tmp/miller-task4-bc199f8/miller context "Eros CLI command dispatch and application startup" --workspace-id c9b7c0ec5c56 --token-budget 5000 --max-hops 1 --json
/tmp/miller-task4-bc199f8/miller search "CliApp" --workspace-id c9b7c0ec5c56 --mode symbol --limit 30 --json
/tmp/miller-task4-bc199f8/miller inspect "CliApp" --workspace-id c9b7c0ec5c56 --depth full --json
```

`inspect` showed `CliApp` in `src/Eros.Cli/CliApp.cs` with production and test callers, including `Main`, `Cli_assembly_name_matches_product_executable_name`, and the test host's `InvokeAsync`. A single C# comment was inserted after the namespace declaration, then removed after capture.

Refresh command and timing:

```text
/usr/bin/time -p /tmp/miller-task4-bc199f8/miller refresh --json --wait --workspace-id c9b7c0ec5c56
real 2.17
```

The refresh payload advanced revision 4 to 5 on the same artifact and reported `duration_ms: 2096`. It returned `status: lock_busy` because another live Miller build owned the indexer lock, but the readable index, search sidecar, and content corpus all converged to revision 5 before the impact query.

Exact impact command:

```text
/tmp/miller-task4-bc199f8/miller impact --from-index-revision 4 --from-artifact-id artifact-1783611385044716000 --workspace-id c9b7c0ec5c56 --max-depth 20 --limit 1000 --json
```

Faithful bounded JSON (only the two potentially long result arrays were replaced by their lengths):

```json
{
  "workspace_id": "c9b7c0ec5c56",
  "delta_status": "complete",
  "artifact_id": "artifact-1783611385044716000",
  "from_artifact_id": "artifact-1783611385044716000",
  "delta_reason": "complete",
  "from_revision": 4,
  "to_revision": 5,
  "changed_paths": ["src/Eros.Cli/CliApp.cs"],
  "impacted_count": 244,
  "tests_count": 478,
  "traversal": {
    "status": "exhausted",
    "reason": "complete",
    "max_depth": 20,
    "limit": 1000,
    "reached_count": 722,
    "returned_count": 722,
    "truncated_by_depth": false,
    "truncated_by_limit": false,
    "seeded_paths": ["src/Eros.Cli/CliApp.cs"],
    "unseeded_paths": []
  }
}
```

Timed replay: `real 0.14`, `user 0.10`, `sys 0.03` seconds.

## Probe 2: deliberately truncated traversal

The same Eros revision delta was replayed with depth and limit bounds so both truncation paths were exercised.

Depth command:

```text
/tmp/miller-task4-bc199f8/miller impact --from-index-revision 4 --from-artifact-id artifact-1783611385044716000 --workspace-id c9b7c0ec5c56 --max-depth 0 --limit 1000 --json
```

The requested zero depth was normalized and explicitly reported as effective `max_depth: 1`:

```json
{
  "workspace_id": "c9b7c0ec5c56",
  "delta_status": "complete",
  "artifact_id": "artifact-1783611385044716000",
  "from_artifact_id": "artifact-1783611385044716000",
  "delta_reason": "complete",
  "from_revision": 4,
  "to_revision": 5,
  "changed_paths": ["src/Eros.Cli/CliApp.cs"],
  "impacted_count": 18,
  "tests_count": 113,
  "traversal": {
    "status": "truncated",
    "reason": "depth",
    "max_depth": 1,
    "limit": 1000,
    "reached_count": 131,
    "returned_count": 131,
    "truncated_by_depth": true,
    "truncated_by_limit": false,
    "seeded_paths": ["src/Eros.Cli/CliApp.cs"],
    "unseeded_paths": []
  }
}
```

Timed replay: `real 0.10`, `user 0.08`, `sys 0.02` seconds.

Limit command:

```text
/tmp/miller-task4-bc199f8/miller impact --from-index-revision 4 --from-artifact-id artifact-1783611385044716000 --workspace-id c9b7c0ec5c56 --max-depth 20 --limit 1 --json
```

```json
{
  "workspace_id": "c9b7c0ec5c56",
  "delta_status": "complete",
  "artifact_id": "artifact-1783611385044716000",
  "from_artifact_id": "artifact-1783611385044716000",
  "delta_reason": "complete",
  "from_revision": 4,
  "to_revision": 5,
  "changed_paths": ["src/Eros.Cli/CliApp.cs"],
  "impacted_count": 1,
  "tests_count": 0,
  "traversal": {
    "status": "truncated",
    "reason": "limit",
    "max_depth": 20,
    "limit": 1,
    "reached_count": 722,
    "returned_count": 1,
    "truncated_by_depth": false,
    "truncated_by_limit": true,
    "seeded_paths": ["src/Eros.Cli/CliApp.cs"],
    "unseeded_paths": []
  }
}
```

Timed replay: `real 0.12`, `user 0.09`, `sys 0.03` seconds.

Both deliberately bounded traversals said `truncated`, never `exhausted`. `delta_status` remained `complete`; it describes revision-delta availability, not graph exhaustion.

## Probe 3: watched path with no seeds

A temporary root file named `miller-impact-traversal-probe.yml` was created in julie-extractors with one YAML comment and no data. This was a reversible comment-only probe, not a production file. After refresh, Miller proved it had no symbols:

```text
$ /tmp/miller-task4-bc199f8/miller inspect miller-impact-traversal-probe.yml --workspace-id 91c17adbdab9 --json
{"file":"miller-impact-traversal-probe.yml","children":[]}
```

Refresh advanced revision 7 to 8 on `artifact-1783618925952878000`; the payload reported `status: refreshed`, `duration_ms: 8`, and `/usr/bin/time` reported `real 0.09` seconds.

Exact impact command:

```text
/tmp/miller-task4-bc199f8/miller impact --from-index-revision 7 --from-artifact-id artifact-1783618925952878000 --workspace-id 91c17adbdab9 --max-depth 20 --limit 1000 --json
```

```json
{
  "workspace_id": "91c17adbdab9",
  "delta_status": "complete",
  "artifact_id": "artifact-1783618925952878000",
  "from_artifact_id": "artifact-1783618925952878000",
  "delta_reason": "complete",
  "from_revision": 7,
  "to_revision": 8,
  "changed_paths": ["miller-impact-traversal-probe.yml"],
  "impacted_count": 0,
  "tests_count": 0,
  "traversal": {
    "status": "not_run",
    "reason": "no_seeds",
    "max_depth": 20,
    "limit": 1000,
    "reached_count": 0,
    "returned_count": 0,
    "truncated_by_depth": false,
    "truncated_by_limit": false,
    "seeded_paths": [],
    "unseeded_paths": ["miller-impact-traversal-probe.yml"]
  }
}
```

Timed replay: `real 0.09`, `user 0.06`, `sys 0.02` seconds. The watched changed path did not disappear merely because it had no graph seed.

## Restoration and gates

The Eros comment was removed with the exact inverse patch, and the temporary julie-extractors comment-only file was deleted. No `reset`, `checkout`, or `stash` was used. Before the restoration refreshes, both repositories were already clean.

- Eros restoration refresh advanced the same artifact to revision 6; search and content sidecars reported revision 6. It returned `lock_busy` after `duration_ms: 2093` (`real 2.18` seconds) because the existing live leader owned convergence.
- julie-extractors restoration refresh advanced the same artifact to revision 9; search and content sidecars reported revision 9. It returned `lock_busy` after `duration_ms: 2090` (`real 2.17` seconds) for the same leadership reason.

A final read-only replay after restoration re-queried Eros revision 4 through current revision 6 and julie-extractors revision 7 through current revision 9. It reproduced the same graph counts and statuses: Eros wide `exhausted` (722 reached), depth `truncated` (131 reached), limit `truncated` (722 reached / 1 returned), and julie-extractors `not_run` / `no_seeds` with the removed temporary path still preserved in the revision history's `changed_paths` and `unseeded_paths`.

Hard-gate results:

- PASS: wide traversal alone reported `exhausted`, after reaching all 722 graph nodes within the stated bounds.
- PASS: deliberately depth-truncated traversal reported `truncated` / `depth`, never `exhausted`.
- PASS: deliberately limit-truncated traversal reported `truncated` / `limit`, never `exhausted`.
- PASS: the unseeded changed path remained in both `changed_paths` and `traversal.unseeded_paths`.
- PASS: all temporary probe edits were removed and affected workspaces were refreshed after removal.
- PASS: no release, pin, schema, extractor, Eros private state, or MCP tool was changed.

Counts and timings above are dogfood observations only, not contractual performance thresholds.
