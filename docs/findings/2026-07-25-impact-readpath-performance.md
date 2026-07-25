# Impact read-path performance

Date: 2026-07-25

## Result

Impact keeps the on-demand SQLite graph and no longer performs per-node neighbour, degree,
visibility, or depth-frontier queries. Reverse BFS is batched once per depth in chunks of at most
500 IDs; frontier truncation short-circuits by chunk; centrality and visibility hydrate only the
bounded ranking window; normal and revision-delta Impact share the same ranking path. The
per-traversal evidence cache is capped at 4,000 entries and cleared after each walk. Frontier proof
uses smaller 100-ID chunks and does not populate that cache.

The rejected alternative loaded the complete repository graph. That made a small Miller run faster
but reversed the large-repo result recorded in
[`2026-06-05-large-repo-readpath-dogfood.md`](2026-06-05-large-repo-readpath-dogfood.md): the OpenClaw
Impact path had already improved from 6.17 seconds and about 1.52 GB RSS to 0.31 seconds and about
69 MB by moving to on-demand reads.

## Miller dogfood

Measurements used the Release `miller impact` CLI, `/usr/bin/time -l`, the current 65k-symbol Miller
artifact, and JSON output. They exercise `SqliteSymbolGraphIndex`; the MCP `impact` method uses the
resident in-memory graph and is not the source of these SQLite read-path timings.

| Input | Before | After | Max RSS after |
|---|---:|---:|---:|
| `ImpactTool.cs`, depth 1, limit 20 | 36.80 s | 1.10 s warm | 76.7 MB |
| six changed files, depth 2, limit 120 | interrupted after 90 s | 1.89 s warm | 81.6 MB |

The first after-change run with cold filesystem pages took 5.22 seconds at 77.8 MB; the immediately
repeated process took 1.10 seconds. The final post-review regression check took 5.35 seconds for the
single-file input and 2.49 seconds for the six-file input at 77–82 MB RSS. The performance contract is
therefore not based only on a long-lived in-process cache.

Both runs reported truthful truncation evidence. The single-file result reached and returned 14
graph rows with depth truncation; the six-file result reached 325 graph rows, returned 120, and
reported both depth and result-limit truncation.

## OpenClaw replay

The historical `runWorkspaceStatusHealth` command was not rerun because the registered OpenClaw
artifact is schema 3 while this branch requires the pinned schema 4 / julie-extract 2.17.0 contract.
A truthful replay would require `workspace full`, which would replace an unrelated workspace's
derived index. This audit did not mutate that workspace.

## Verification

- 190 focused Impact, graph traversal, SQLite graph, bridge-reader, Blazor, and path-lookup tests passed.
- SQLite and fully loaded graphs return the same evidence, including Blazor component edges.
- `ImpactTool.Run` produces the same ranked-window counts and truncation disposition over the SQLite
  graph used by the CLI.
- The ranking core clamps depth to 5, result limit to 1,000, and its internal candidate window to
  2,000.
- A 1,201-caller fixture crosses multiple SQL batches and completes with all 1,201 reached rows.
- Fast suite: 4,891 passed with 2 environment skips.
- Scale suite: 91 passed with 3 configured sidecar/platform skips against the real pinned extractor.
- Release build: 0 warnings, 0 errors.
