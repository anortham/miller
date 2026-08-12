# Task 4 graph-reach report

## Result

- Root cause: plain `SqliteSymbolGraphIndex.Reach` delegated to generic per-node BFS. A depth-two fixture with a 200-node frontier executed 805 SQL families while returning the correct 201-node result.
- Fix: plain `Reach` now advances one BFS level at a time through the existing bounded `BatchNeighbourEvidence` query and preserves the existing limit, hop, and ordinal-id ordering contract.
- Internal fixed-cardinality telemetry reports executions, returned rows, and elapsed time for scalar lookup, legacy directional, name-resolution, batched-frontier, and supplemental-edge families. There are no dynamic SQL/id labels and no new public contract.
- No process-level context or other dogfood was rerun.

## RED/GREEN evidence

- Instrumentation RED: the single filtered test failed to compile because `GraphQueryTelemetrySnapshot`, `GraphQueryFamilyTelemetry`, and `QueryTelemetry` did not exist (`CS0246`, `CS1061`).
- Instrumentation GREEN: `Reach_QueryTelemetryReportsFixedSqlFamilies` passed in 83 ms after one cache-aware literal correction (`SymbolExists` is 1 execution/1 row).
- Amplification RED: `Reach_HighFrontierUsesBoundedSqlBatches` preserved the hand-derived result but failed `SQL executions <= 10` with `SQL executions: 805`; test duration was 126 ms.
- Amplification GREEN: the two exact tests passed 2/2 in 125 ms. The high-frontier contract now observes 2 frontier-batch executions and 400 returned relationship rows; the total query ceiling is 10 (actual composition is 4: one seed existence lookup, two frontier queries, one supplemental load).
- Assigned ceiling: `SqliteSymbolGraphIndexTests` passed 19/19 in 578 ms with no warnings or errors.
- `git diff --check` passed.

## Inline review correction

- Review found that the diagnostic property and snapshot records were accidentally public on the public index class.
- Verified `Miller.Indexing.csproj` already grants `InternalsVisibleTo` to `Miller.Tests`, then narrowed `QueryTelemetry`, `GraphQueryTelemetrySnapshot`, and `GraphQueryFamilyTelemetry` to `internal`.
- Post-correction exact scope passed 2/2 in 125 ms; post-correction class ceiling passed 19/19 in 575 ms.
- Scoped `dotnet format --verify-no-changes` and `git diff --check` passed.

## Parity proof

- `BatchNeighbourEvidence` includes forward and reverse arms for relationships, pending resolutions, resolved identifiers, and unresolved unique-name identifiers.
- Its live-symbol joins preserve dangling-target filtering; its unique-name `NOT EXISTS` rule matches the legacy `ResolveNameIds(...).Count == 1` homonym rule.
- Supplemental test-linkage and Blazor edges are added in both directions with the same existence filter; self edges are discarded and neighbours are ordinal by id.
- The outer `Read` still pins the entire traversal to one family-store session. Starts still require `Contains`, starts are excluded from output, and output remains hop-then-ordinal-id ordered before applying `limit`.

## Architecture quality self-review

- Complexity stays local to `Miller.Indexing`; `ISymbolGraphReachability` and MCP/CLI/schema contracts are unchanged.
- Tests exercise the real caller interface and hand-derived graph result, not private helpers or mocks.
- The existing batch seam earned reuse because it already implements every graph edge family and evidence tie-break; no full graph hydration or speculative adapter was added.
- Mutation checks: restoring per-node `GraphTraversal.Reach` fails the query ceiling; removing either BFS level fails node/hop assertions; dropping an edge family is covered by the existing parity/homonym/supplemental class tests.

## Handoff

- No commit was made.
- Owned changes: `src/Miller.Indexing/SqliteSymbolGraphIndex.cs`, `tests/Miller.Tests/Indexing/SqliteSymbolGraphIndexTests.cs`, and this report.
- Lead-owned dirty files were not modified: `PERF.md`, `src/Miller.Server/Tools/ContextTool.cs`, `tests/Miller.Tests/Server/ContextToolTests.cs`.
- Remaining concern: rebuilt-process context must be rerun by the lead after review/build to prove the <=2-second product gate; this worker intentionally did not run it.

## Round 2: family resolution reverse reads

- Real-store plan evidence isolated the remaining failure to the compatibility `identifier_resolutions` and `pending_resolutions` `UNION ALL` views. With `automatic_index=OFF`, adding `target_version_id` to the view and constraining both target columns still produced `SCAN b`; the outer predicate did not push through the overlay union.
- The producer base indexes are target-leading: `idx_read_resolution_identifiers_target(target_version_id,target_symbol_id,version_id,identifier_id)` and `idx_read_resolution_pending_target(target_version_id,target_symbol_id,version_id,pending_relationship_id)`. The old reverse arms discarded the candidate version and triggered an automatic index on `target_symbol_id`, which accounted for the real 7.81 GB read amplification.
- The failed graph-owned attached-table SQL was removed. `FamilyStoreReadSession` now implements an internal optional `IFamilyGraphResolutionReader`; public `IWorkspaceReadSession`, MCP/CLI, and schema contracts are unchanged.
- The family capability resolves each bounded candidate id to its pinned visible version, executes base and delta branches separately below the compatibility union, and applies target-version plus target-symbol predicates inside each reverse base branch. `SqliteSymbolGraphIndex` omits only its pending/resolved-identifier compatibility arms when this capability exists; relationship, unresolved-name, supplemental, homonym, filtering, ordering, and the legacy standalone path remain unchanged.
- Overlay semantics stay local to the family reader: a selected delta row suppresses its base identifier/pending row, pending delta tombstones emit no edge, and pending replacements emit the replacement edge. No full-graph hydration or temp workspace-sized index was added.
- The additive `target_version_id` compatibility-view columns remain independently valid for other internal readers and preserve every existing column/name.

## Round 2 RED/GREEN and plan evidence

- View-shape RED: `ResolutionViewsExposeTargetVersionForIndexedReverseGraphReads` failed because both compatibility views omitted `target_version_id`; it passed after the additive projection change (1/1, 72 ms).
- Chosen-seam RED: `ReverseGraphReadsUseTargetVersionIndexesOnPinnedFamilyView` failed to compile with `CS1061` because the real family session lacked the internal graph-resolution capability/plan diagnostic.
- Caller GREEN: the same test passed through `ISymbolGraphReachability.Reach` with hand-derived reverse output `[identifierCaller, pendingCaller]` (1/1, 118 ms). With `PRAGMA automatic_index=OFF`, captured plans contain both checked-in target indexes with `target_version_id=? AND target_symbol_id=?` and contain no `AUTOMATIC` index.
- Overlay parity initially failed only because the test expected evidence fields from plain `Reach`, whose contract returns hop-only nodes; the observed graph membership was already correct. The corrected caller contract proves identifier replacement removes the base-target edge and pending tombstone removes the base pending edge.
- Exact round-2 tests passed 2/2 in 119 ms.
- Assigned class ceiling passed 49/49 in 569 ms (`FamilyStoreReadSessionTests` plus `SqliteSymbolGraphIndexTests`, Release, no build on the already compiled tree).
- `git diff --check` passed. No rebuilt-host/context dogfood was run; lead owns product acceptance.

## Round 3: rebuilt acceptance miss and isolated reverse-name evidence

- Rebuilt acceptance on `85c51f81` still timed out at 7,007.282 ms in `graph_reach`, with 7.42 GB logical reads, 4.95 GB logical writes, and 112.7 MB physical writes. The indexed family resolution capability remains valid but is not sufficient by itself.
- The one permitted isolated real family-session unresolved-name reverse arm used the same pinned family view and explicit `WorkspaceIndexProvider` symbol id (`a6a374fb8554e68e3a7a0b217670d32a`). A five-second outer bound protected only execution after a separate clean compile.
- The persisted plan is `/tmp/miller-name-reverse-85c51f81.txt`. It materializes the `identifier_resolutions` compatibility union, scans the resolution base, and builds `AUTOMATIC COVERING INDEX (identifier_id=?)` for the left join. That plan shape is inefficient, but the isolated caller completed in 142 ms and returned zero rows, so it does not explain the seven-second wall miss for this exact payload.
- No production telemetry, seam, or query change was added because the required hypothesis was rejected. The opt-in real-store diagnostic source was removed after collecting evidence.
- Next isolated arm: unresolved-name forward. It is the next unmeasured fallback arm after rejecting unresolved-name reverse; this is a diagnostic selection, not a root-cause claim. Do not rerun the combined context query until that arm is isolated or main-frontier phase telemetry changes.

## Round 4: remaining post-fix frontier arms exhausted

- One reusable opt-in Scale harness compiled cleanly, then each remaining fixed SQL arm ran exactly once against the same pinned real family view and `WorkspaceIndexProvider` id. Each invocation persisted `EXPLAIN` before execution and had its own five-second outer timeout.
- Unresolved-name forward: `/tmp/miller-name-forward-85c51f81.txt`; 0 rows; 1,397.130 ms query wall. Its plan materialized the identifier-resolution union, scanned the resolution base, and built `AUTOMATIC COVERING INDEX (identifier_id=?)` before using `idx_read_identifiers_containing` and symbol-name indexes.
- Relationship forward: `/tmp/miller-relationship-forward-85c51f81.txt`; 0 rows; 0.110 ms query wall. Its plan used `idx_read_relationships_from(from_symbol_id=?)`.
- Relationship reverse: `/tmp/miller-relationship-reverse-85c51f81.txt`; 0 rows; 0.111 ms query wall. Its plan used `idx_read_relationships_to(to_symbol_id=?)`.
- Together with round 3 unresolved-name reverse (0 rows, 142 ms test duration), every relationship/name arm remaining after the family resolution capability is individually below the required two-second stop threshold. No caller-facing RED or production fix was added because no isolated arm satisfied the gate.
- The selected remaining family is the composed frontier query itself: SQLite's planning/materialization interaction across the fixed `UNION ALL` arms, rather than an individually slow relationship/name arm. This is an evidence-bounded diagnosis, not yet a root cause. The combined query and process context were not rerun.
- The diagnostic harness was removed. Next work must add fixed internal per-arm/composition telemetry or construct a production-fixture RED for the combined query shape without another process context replay.

## Round 5: composed frontier root cause and split statements

- ContextTool source confirms graph expansion calls `Reach(..., Direction.Both)`. The exact runtime pivot list was not persisted, and resolving it would have required another context call, so the bounded reproduction used the known explicit `WorkspaceIndexProvider` entry id as its single candidate and records that limitation.
- One real family-session execution copied the exact post-`8dbea3c3` four-arm production frontier SQL (relationship forward/reverse plus unresolved-name forward/reverse), `Direction.Both`, plan persisted before rows, five-second outer bound. The SQL recorded 0 rows and 2,052.165 ms before the outer test process expired during teardown. Plan: `/tmp/miller-frontier-combined-85c51f81.txt`.
- The combined plan materialized `identifier_resolutions` twice—once per name arm—and scanned the resolution base twice, while the same individual arms were all below two seconds. This proves the composed `UNION ALL` planner/materialization interaction is the smallest reproduced cause family.
- Caller-facing RED: `CombinedFamilyFrontierUsesSeparateBoundedStatementsWithExactParity` failed to compile only because `FrontierRelationships`, `FrontierUnresolvedNames`, and the internal fixed statement-plan seam did not exist (`CS0117`/`CS1061`).
- Minimal GREEN: `BatchNeighbourEvidence` now executes fixed directional relationship and unresolved-name commands separately and merges their edges in memory. The family resolution reader remains a separate optional capability. Legacy sessions retain pending/resolved-identifier semantics through separate fixed directional resolution commands. No public API, dynamic family labels, attached-table graph SQL, temp indexes, or graph hydration were added.
- Fixed internal telemetry reports relationship and unresolved-name statement executions/rows/elapsed independently while preserving the existing logical `FrontierBatch` counter. The internal plan seam is test-only and captures statements as fixed families.
- Exact GREEN: 1/1 in 109 ms, hand-derived six-neighbour `Direction.Both` output, two relationship executions, two unresolved-name executions, one logical frontier batch, two plan entries per family, and no resolution materialization in relationship plans.
- Assigned class ceiling: `FamilyStoreReadSessionTests` plus `SqliteSymbolGraphIndexTests` passed 50/50 in 551 ms, Release, zero build warnings/errors.
- The opt-in real combined-query harness was removed. No context acceptance was rerun; lead owns the rebuilt product gate.
