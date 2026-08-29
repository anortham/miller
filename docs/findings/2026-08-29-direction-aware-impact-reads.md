# Direction-aware impact reads — fixed replay

Date: 2026-08-29. Branch: `fix/tool-latency-health` at `2becdaac`. This finding records the
measurement after the direction-aware graph scratch change; it does not change the producer store,
query-time resolution policy, or public output contracts.

## Result

The direction-aware graph reads pass both latency gates on the fixed impact workload:

- Warm resident calls were `4,513 / 4,505 / 4,510 / 4,621 / 4,600 ms`; nearest-rank p95/max was
  `4,621 / 4,621 ms`.
- The warm p95 improved `44.3%` from the `8,296 ms` baseline. This passes the `6,222 ms` keep gate
  and closes the `5,000 ms` product gate.
- The one-shot result stayed byte-identical: SHA-256
  `fc9ad40c061d620c346a90866dda9ea47fcb81ce3af081caa00ec3931e2ca483`, with 53 impacted symbols
  and 147 likely tests.
- The resident cold call was `9,756 ms` and is reported for completeness but excluded from the
  warm p95, as required by the replay protocol.

The change is accepted by this replay. No sidecar, cache, producer schema change, or second
optimization is justified by this result.

## Fixed replay

The successful one-shot parity call used the rebuilt Release binary and this exact sequential
workload:

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

The resident replay used the same six paths, workspace, depth, limit, and sequential tool
arguments in one branch `miller serve` process. Its correlation IDs and wall times were:

| call | correlation ID | tool ms |
|---:|---|---:|
| cold | `01a04cdc-c519-7c83-8a46-e8a652a11ef8` | 9,756 |
| warm 1 | `01a04cdc-ef12-784e-89c8-3ba8d8ff9eb3` | 4,513 |
| warm 2 | `01a04cdd-049b-777d-b239-3b362ba78dc1` | 4,505 |
| warm 3 | `01a04cdd-1a1c-7cf3-8d6a-e6057acf13e8` | 4,510 |
| warm 4 | `01a04cdd-2fa2-7dc7-9edf-9a715b63c9c4` | 4,621 |
| warm 5 | `01a04cdd-4598-7c25-8cd6-3ccfac750cb5` | 4,600 |

## Resolution phase evidence

Every resident call emitted seven complete nine-field resolution breakdowns. The candidate batch
shape was identical on every call: `396 / 329 / 457 / 500 / 171 / 286 / 286`. The five
named/reverse passes (`396`, `329`, `457`, `171`, `286`) reported zero identifier-within and
pending-within work (`0 rows / 0 ops / 0 ms`). The two within/forward passes (`500`, `286`) reported
zero identifier-named and pending-named work (`0 rows / 0 ops / 0 ms`). This is the required proof
that the paired graph consumers receive only the direction-specific site collections.

Rows and operations were deterministic across all calls. Each `ms` cell below is the sum over the
seven breakdowns for that call; warm values are in warm-call order. The warm median is the middle
value of those five samples.

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

The summed warm-median breakdown is `4,144 ms`; the warm median wall time is `4,513 ms`. The
largest single measured residual is identifier resolution at `682 ms` warm median. Identifier
details plus identifier and pending resolution total `1,508 ms` warm median. The remaining wall
time is outside these nine measured subphases and is not assigned to a new optimization by this
finding.

## Gates and verification

- One-shot output parity: PASS — hash, 53 impacted symbols, and 147 likely tests match the recorded
  baseline.
- Complete breakdowns: PASS — 7/7 passes per call, with the expected direction-specific zero arms.
- Keep gate: PASS — warm p95 `4,621 ms` is below `6,222 ms`, and no warm sample exceeds `8,296 ms`.
- Product gate: PASS — warm p95 `4,621 ms` is below `5,000 ms`.
- `git diff --check`: PASS.
- Broader tests, build, Scale, security, dependency, and final worktree gates remain lead-owned.

Miller onboarding was run for this workspace. `context` located `GraphResolutionBreakdown`,
`ResolveQuery`, and `TakeResolutionBreakdown`; `search` located the changed-path CLI contract and
`ObserveGraphStatement`; `inspect` confirmed the nine breakdown fields, direction-aware resolver
entry point, and correlation-bearing log shape before the replay.

## Worktree state

- Path: `/home/murphy/source/miller/.worktrees/tool-latency-health`
- Branch: `fix/tool-latency-health`
- Replay source HEAD: `2becdaac7011e9271f9f37455bb54f3cb93ec670`
- Dirty state before documentation edits: clean.
