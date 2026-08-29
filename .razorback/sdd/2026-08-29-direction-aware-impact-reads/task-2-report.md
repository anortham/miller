# Task 2 report: direction-aware fixed replay

## Status

Accepted. The direction-aware graph scratch change passes the byte-level parity check, the 25%
warm-p95 keep gate, and the 5-second product gate. This packet changed documentation only; it did
not change production code or tests.

## Worktree and replay source

- Path: `/home/murphy/source/miller/.worktrees/tool-latency-health`
- Branch: `fix/tool-latency-health`
- Replay source HEAD: `2becdaac7011e9271f9f37455bb54f3cb93ec670`
- Release binary: `src/Miller.Server/bin/Release/net10.0/miller`
- Binary version: `1.25.0+a3156713d5ce`
- Dirty state before documentation edits: clean.

## Fixed workload

The one-shot and resident calls used the same six changed paths, task worktree, depth 2, limit 200,
JSON output, and sequential order:

```text
src/Miller.Server/bin/Release/net10.0/miller impact --changed-paths \
  src/Miller.Dashboard/Endpoints/DashboardEndpoints.cs,\
  src/Miller.Server/Cli/CliDispatch.cs,\
  src/Miller.Server/Tools/WorkspaceRender.cs,\
  src/Miller.Server/Tools/WorkspaceTool.cs,\
  src/Miller.Server/Workspaces/WorkspaceRegistryPrune.cs,\
  src/Miller.Server/Workspaces/WorkspaceRemoval.cs \
  --workspace /home/murphy/source/miller/.worktrees/tool-latency-health \
  --max-depth 2 --limit 200 --json
```

The resident replay sent the equivalent `impact` MCP arguments to one branch `miller serve`
process, with `workspace_id` set to the task worktree. No dataset, view, concurrency, or command
shape changed during either replay role.

## One-shot parity

One successful rebuilt Release CLI call returned exit code 0. Its output was:

- SHA-256: `fc9ad40c061d620c346a90866dda9ea47fcb81ce3af081caa00ec3931e2ca483`
- impacted symbols: 53
- likely tests: 147

The hash and counts match the recorded pre-change output exactly. This is the hard output-parity
gate; the one-shot process duration is not mixed into the resident timing series.

## Resident timing

One cold call was followed by five warm calls in the same resident process:

| call | correlation ID | tool ms |
|---:|---|---:|
| cold | `01a04cdc-c519-7c83-8a46-e8a652a11ef8` | 9,756 |
| warm 1 | `01a04cdc-ef12-784e-89c8-3ba8d8ff9eb3` | 4,513 |
| warm 2 | `01a04cdd-049b-777d-b239-3b362ba78dc1` | 4,505 |
| warm 3 | `01a04cdd-1a1c-7cf3-8d6a-e6057acf13e8` | 4,510 |
| warm 4 | `01a04cdd-2fa2-7dc7-9edf-9a715b63c9c4` | 4,621 |
| warm 5 | `01a04cdd-4598-7c25-8cd6-3ccfac750cb5` | 4,600 |

The cold call is excluded. Warm nearest-rank p95/max is `4,621 / 4,621 ms`, versus the recorded
`8,296 ms` baseline p95/max. Improvement is `44.3%`.

## Phase evidence

Every call emitted seven complete nine-field breakdowns. Candidate batches were
`396 / 329 / 457 / 500 / 171 / 286 / 286` on every call. The five named/reverse passes
(`396`, `329`, `457`, `171`, `286`) reported zero identifier-within and pending-within rows,
operations, and milliseconds. The two within/forward passes (`500`, `286`) reported zero
identifier-named and pending-named rows, operations, and milliseconds.

Each `ms/rows/ops` row below sums the seven breakdowns for a call. Warm milliseconds are in warm
call order; rows and operations are constant across all six calls.

| subphase | cold ms / rows / ops | warm ms (1..5) | rows / ops |
|---|---:|---:|---:|
| candidate lookup | 609 / 2,425 / 23 | 607, 612, 604, 607, 607 | 2,425 / 23 |
| identifier within | 704 / 33,622 / 7 | 669, 650, 670, 688, 668 | 33,622 / 7 |
| identifier named | 236 / 26,613 / 1,582 | 209, 204, 202, 210, 218 | 26,613 / 1,582 |
| pending within | 577 / 9,233 / 7 | 584, 575, 585, 598, 590 | 9,233 / 7 |
| pending named | 186 / 13,530 / 1,582 | 185, 198, 186, 185, 184 | 13,530 / 1,582 |
| identifier details | 587 / 60,235 / 475 | 546, 534, 552, 564, 558 | 60,235 / 475 |
| identifier resolution | 1,050 / 60,235 / 60,235 | 685, 682, 670, 679, 706 | 60,235 / 60,235 |
| pending resolution | 422 / 22,763 / 22,763 | 265, 274, 271, 275, 277 | 22,763 / 22,763 |
| relationships | 381 / 6,129 / 23 | 371, 381, 388, 382, 381 | 6,129 / 23 |

Warm median summed breakdown is `4,139 ms`; warm median wall time is `4,513 ms`. The largest
single measured residual is identifier resolution at `682 ms` warm median. Identifier details plus
identifier and pending resolution total `1,508 ms` warm median. The difference between the median
wall time and the summed breakdown is `374 ms`; it is not assigned to a new change.

## Gate verdicts

- Output parity: PASS — recorded hash, 53 impacted symbols, and 147 likely tests match.
- Breakdown completeness: PASS — 7/7 resolution passes per call, with direction-opposite arms zeroed.
- Keep gate: PASS — warm p95 `4,621 ms` <= `6,222 ms`; no warm sample exceeds `8,296 ms`.
- Product gate: PASS — warm p95 `4,621 ms` <= `5,000 ms`.
- `git diff --check`: PASS.
- Broader test, Release build, Scale, secrets, dependency, and final worktree gates: lead-owned.

## Miller evidence and concerns

- `workspace onboarding` identified the task workspace and recent resolution-reader telemetry.
- `context` located `GraphResolutionBreakdown`, `ResolveQuery`, and `TakeResolutionBreakdown`.
- `search` located the changed-path CLI contract and `ObserveGraphStatement` log shape.
- `inspect` confirmed the nine measurement fields, direction-aware resolver entry point, and
  correlation-bearing observer before replay.

No blocker remains for this packet. The product gate is closed. The final branch gates and any
integration decision remain with the lead.
