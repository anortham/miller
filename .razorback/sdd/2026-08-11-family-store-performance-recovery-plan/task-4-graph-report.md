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

## Round 6: cancellation-safe graph statement instrumentation

- Rebuilt `b71263c1` still timed out at 7,007 ms after `pivot_ranking`, with 7.165 GB logical reads. Query behavior was intentionally left unchanged and context was not rerun.
- Architecture evidence: `WorkspaceIndexProvider` already constructs every family `SqliteSymbolGraphIndex` inside `MeasuredSymbolGraphReachability`; `TelemetryContext.Current` supplies request correlation at execution time. This supports an internal observer without widening `ISymbolGraphReachability`, `IWorkspaceReadSession`, MCP/CLI, or ContextTool signatures.
- Miller.Indexing now exposes only an assembly-internal fixed enum/record callback. The `miller` server assembly is a friend solely so its existing measured wrapper can attach the callback; Miller.Indexing has no Serilog dependency.
- Fixed completion phases are `relationship_forward`, `relationship_reverse`, `unresolved_name_forward`, `unresolved_name_reverse`, `family_resolution`, `supplemental`, and `completion`. Each event is emitted only after its statement/family completes and carries fixed phase, rows, elapsed milliseconds, and ambient correlation id. A cancellation or process timeout therefore leaves the last completed boundary durable instead of losing the whole graph interval.
- Observer RED: the two-test scope failed to compile because `GraphStatementObservation`, the graph observer property, and provider constructor callback did not exist. The first GREEN compile also exposed the actual Server assembly name (`miller`, not project name `Miller.Server`); the friend declaration was corrected to the real assembly boundary.
- Exact GREEN: 2/2 in 112 ms. The graph cancellation test observes only relationship forward/reverse and unresolved-name forward before the observer throws, with no later family/supplemental/completion events. The provider construction test observes the full forward sequence through the measured family graph.
- `SqliteSymbolGraphIndexTests` passed 20/20 in 575 ms. `dotnet build Miller.slnx -c Release --no-restore` passed in 9.40 s with zero warnings/errors. ContextTool was not modified, so its phase suite was not rerun.
- No candidate process/context acceptance was run; lead owns the one correlated replay.

## Round 7: family resolution arm instrumentation

- Rebuilt `921ccdff` timed out at 7,006.9 ms. Correlated completed phases were relationship forward 519 ms/1 row, relationship reverse 512 ms/1 row, unresolved-name forward 1,432 ms/0 rows, and unresolved-name reverse 1,973 ms/65 rows during shutdown; no `family_resolution` completion followed. This selects the internal family resolution operation without yet selecting one of its arms.
- Query behavior remains unchanged. The internal `IFamilyGraphResolutionReader.ReadResolutionEdges` callback now receives the existing graph statement observer; no Server/Serilog dependency enters the read session and no public interface changes.
- Eight fixed completion phases are emitted after each actual reader closes: `identifier_base_forward`, `identifier_delta_forward`, `pending_base_forward`, `pending_delta_forward`, `identifier_base_reverse`, `identifier_delta_reverse`, `pending_base_reverse`, and `pending_delta_reverse`. Each carries rows and elapsed time through the already correlated provider boundary.
- RED: the exact family graph test failed to compile because all eight fixed phase enum values were absent (`CS0117`).
- GREEN: `FamilyResolutionObserverReportsOnlyCompletedArmsBeforeCancellation` passed 1/1 in 90 ms. It stops after completed `pending_base_forward` and observes only the four prior main frontier phases plus identifier base/delta forward and pending base forward; no pending delta, reverse, outer resolution, supplemental, or completion event is emitted.
- `FamilyStoreReadSessionTests` plus `SqliteSymbolGraphIndexTests` passed 52/52 in 554 ms. Release build passed in 9.33 s with zero warnings/errors.
- No candidate process/context call was run; lead owns the one replay that will select the first incomplete family resolution arm.

## Round 8: isolated identifier-base-forward evidence

- The `a9bf810b` replay completed unresolved-name reverse and none of the eight family arms, selecting the first executed arm `identifier_base_forward` for one isolated real-session measurement.
- A temporary opt-in Scale harness reflected the production `IdentifierBaseForwardSql` constant, persisted EXPLAIN before execution, then invoked the actual `IFamilyGraphResolutionReader.ReadResolutionEdges` path and stopped through its observer immediately after that arm completed. The same pinned family view and known explicit `WorkspaceIndexProvider` id were used; execution had a five-second outer bound.
- Result: 28 rows in 3.364 ms query wall; test duration 162 ms. Plan: `/tmp/miller-identifier-base-forward-a9bf.txt`.
- Plan uses `idx_export_resolution_identifiers_order(version_id=?)`, the identifier primary index `(version_id,identifier_id)`, target-symbol primary index `(version_id,symbol_id)`, and visible-version index. It contains no scan of the resolution base.
- The isolated known-entry candidate is therefore bounded and does not reproduce the real timeout. Per the gate, no RED or production behavior fix was added. The missing evidence is the exact real four-pivot candidate/version shape entering family resolution; the explicit entry alone is insufficient.
- The temporary diagnostic harness was removed. No context call was run and `PERF.md` was not touched.

## Round 9: bounded candidate-shape observations

- The remaining diagnostic gap was the exact multi-pivot input shape. `GraphStatementObservation` now carries exact `CandidateCount` plus an immutable `CandidateSample` capped at eight ids in original `missingIds` order.
- Candidate shape is populated at every completed relationship/name/family-resolution/supplemental/completion observation. The family session receives the original caller ids separately from its version-resolved rows, so SQLite join order cannot reorder the sample.
- The Server's existing correlated fixed event now adds `GraphStatementCandidateCount` and a JSON `GraphStatementCandidateSample`; phase labels remain a fixed enum-to-string mapping. No public contract, MCP/telemetry schema, or dynamic label was added.
- RED: the exact >8-candidate test failed to compile because `CandidateCount` and `CandidateSample` did not exist (`CS1061`).
- GREEN: the same test passed 1/1 in 79 ms, proving total count 12, immutable sample length 8, exact first-eight input order, and completion-only behavior when the observer cancels after unresolved-name forward.
- `SqliteSymbolGraphIndexTests` plus the provider wiring test passed 22/22 in 549 ms. Release build passed in 9.67 s with zero warnings/errors.
- No context call was run; this instrumentation earns the lead's one replay to recover the real four ids.

## Round 10: exact four-candidate family-resolution evidence

- The lead replay recovered the exact ordered frontier: `a6a374fb8554e68e3a7a0b217670d32a`, `ac38a31eba3de6a7a7fcb778bf24e33a`, `9639df0e830f9b3520b25bb6b3aa837a`, `72d24b5950320bbbd03e1bf7dca3e52a`.
- One temporary opt-in Scale harness opened the real pinned family session, persisted EXPLAIN for all eight production SQL constants, separately timed the private candidate hydration, then invoked the actual `IFamilyGraphResolutionReader.ReadResolutionEdges` operation once with `Direction.Both`. A five-second outer bound covered the real invocation and the harness was removed after evidence capture.
- Candidate hydration returned the same four ids in input order with versions `1227`, `1021`, `1605`, and `1078` in 7.066 ms.
- Actual completed arms, rows/query wall: identifier base forward 37/5.044 ms; identifier delta forward 0/9.718 ms; pending base forward 20/0.445 ms; pending delta forward 1/0.768 ms; identifier base reverse 102/1.029 ms; identifier delta reverse 2/20.869 ms; pending base reverse 18/0.236 ms; pending delta reverse 0/1.050 ms. The operation merged 180 edges; the xUnit test completed in 200 ms.
- Forward base plans use `idx_export_resolution_identifiers_order(version_id=?)` and `idx_export_resolution_pending_order(version_id=?)`. Reverse base plans use the checked-in target-leading `idx_read_resolution_identifiers_target(target_version_id=? AND target_symbol_id=?)` and `idx_read_resolution_pending_target(target_version_id=? AND target_symbol_id=?)`. Delta plans use their view/generation primary indexes; no base resolution arm is scanned.
- Every arm is far below the two-second behavior-fix gate. The exact four-candidate family resolution operation therefore does not reproduce the rebuilt process timeout, so no caller-facing RED or production query change was justified. The remaining discrepancy is outside this proven family-reader operation or depends on a process-only state not present in the same pinned-session invocation.
- No context call was run and `PERF.md` was not touched.

## Round 11: exact caller Reach deadline evidence

- One temporary opt-in Scale harness opened the same pinned family session and invoked only `new SqliteSymbolGraphIndex(session).Reach(exactFourIds, 1, 1000, Direction.Both)`, with the existing statement observer persisted after every completed phase. The process had a five-second outer bound; no direct `ReadResolutionEdges` call or context call was made.
- The command reached the hard timeout after 5.00 seconds. The actual graph interval completed relationship forward in 509.904 ms/1 row and relationship reverse in 501.883 ms/1 row, then was interrupted inside unresolved-name forward. VSTest startup consumed the balance before graph execution, so this run is not a new behavior RED and cannot supply a completed total/result count.
- `/usr/bin/time -v` recorded 0.36 seconds user CPU, 0.06 seconds system CPU, 113,388 KB maximum RSS, zero filesystem input/output blocks, 277 voluntary context switches, and 16 involuntary context switches. The wait-heavy profile is consistent with the real read path rather than CPU work in the test harness.
- Query-plan capture was enabled on the real graph object, but `LastFrontierQueryPlan` is published only after all four frontier statements return; the hard timeout therefore correctly left no new combined plan. Prior one-shot evidence already persisted the exact unresolved-name plans, including compatibility-view materialization and resolution-base scans.
- Lifecycle inspection found no leaked reader or deadlocked nested session lock. Every frontier command and reader is `using`-disposed before the next statement. The outer `FamilyStoreReadSession.Read` lock is held during `Reach`, but C# monitor locks are reentrant when the family reader later enters the same `_gate`; the exact direct family operation independently completed all eight arms in tens of milliseconds.
- The rebuilt-process phase sequence and the bounded direct family reader are therefore consistent: context consumed roughly 2.7 seconds before graph, then relationship forward/reverse plus unresolved-name forward/reverse consumed roughly 4.3 seconds cumulatively, and the request deadline arrived immediately after name reverse but before any fast family arm could report completion. There is no family-resolution stall to fix.
- The selected next bottleneck is cumulative unresolved-name fallback below the compatibility view, approximately 3.3 seconds in the rebuilt process. No production behavior changed in this round; the diagnostic harness was removed and `PERF.md` was not touched.

## Round 12: family unresolved-name storage capability

- Root cause was cumulative compatibility-view fallback work: the rebuilt graph spent about 1.0 seconds in relationship statements and 3.3 seconds in unresolved-name forward/reverse after roughly 2.7 seconds of pre-graph context work. The exact four-candidate family resolution operation remained bounded at 0.2–20.9 ms per arm.
- Caller-facing RED used `ISymbolGraphReachability.Reach` through a real pinned family fixture. Exact output parity passed, then the bounded-work assertion failed because family mode still executed two `FrontierUnresolvedNames` compatibility statements (expected 0, actual 2). The internal plan seam separately failed to compile with `CS1061`, proving it did not exist before production changes.
- Minimal GREEN adds an optional internal `IFamilyGraphUnresolvedNameReader`. `FamilyStoreReadSession` owns version-pinned base/delta overlay checks and returns storage-neutral fallback edges; `SqliteSymbolGraphIndex` merges those edges and omits only the family compatibility-view name statements. Legacy standalone sessions retain the existing fixed forward/reverse SQL and observations.
- Overlay parity covers unique forward and reverse edges, base-resolved exclusion from fallback, delta replacement with a non-null target exclusion, delta unresolved/null-target inclusion, and homonym ambiguity exclusion. Exact resolution edges remain present through the independent family resolution capability.
- Family fallback emits the existing fixed `UnresolvedNameForward` and `UnresolvedNameReverse` observations only after each real arm completes, with actual rows and elapsed time. The existing provider observer sequence remains green.
- Exact producer schema evidence names `idx_read_identifiers_containing(containing_symbol_id,version_id)` and `idx_read_identifiers_name_kind(name,kind,version_id)`. The GREEN plan contains both checked-in indexes and contains neither `MATERIALIZE` nor a resolution-base scan.
- Exact GREEN passed 1/1 in 118 ms. `FamilyStoreReadSessionTests`, `SqliteSymbolGraphIndexTests`, and the provider observer test passed 54/54 in 564 ms. Scoped format, `git diff --check`, and the Release solution build passed with zero warnings/errors.
- Architecture quality: overlay/tombstone rules remain local to the family read session, traversal remains local to the graph index, and the caller-facing interface is unchanged. The seam is internal, optional, storage-neutral, and no producer schema, attached-schema graph SQL, temp index, public MCP/CLI surface, or workspace-sized hydration was added.

## Round 13: production WorkspaceReadHandle capability forwarding

- Rebuilt acceptance showed the family name phases still had legacy timings. Production constructs `SqliteSymbolGraphIndex` with a `WorkspaceReadHandle`, not the wrapped `FamilyStoreReadSession`; because the handle did not expose internal family capabilities, both family type checks failed and the graph silently used legacy compatibility-view SQL. The same wrapper also prevented the round-2 indexed resolution reader from being selected.
- Production-shape RED changed the real family caller fixture to construct `WorkspaceReadHandle(FamilyStoreReadSession)` and pass the handle into `SqliteSymbolGraphIndex`. Exact graph parity still passed through compatibility views, then the bounded work contract failed with `FrontierUnresolvedNames` expected 0, actual 2.
- Minimal GREEN gives `WorkspaceReadHandle` nullable internal properties for its wrapped resolution and unresolved-name capabilities. The public handle still implements only `IWorkspaceReadSession`; it does not unconditionally advertise either family interface. `SqliteSymbolGraphIndex` captures the nullable capabilities in its session constructor and uses them independently.
- A load-bearing resolution mutation check temporarily disabled only wrapped resolution forwarding. The production-shape test failed because `LastGraphResolutionQueryPlan` was empty; after restoration, the session plan contains both `idx_read_resolution_identifiers_target` and `idx_read_resolution_pending_target`.
- Legacy regression wraps `LegacyArtifactReadSession` in the same handle, proves both family capability properties are null, and observes two legacy `FrontierUnresolvedNames` executions. No legacy behavior was redirected.
- Exact production/legacy GREEN passed 2/2 in 88 ms. `WorkspaceReadSessionTests`, `FamilyStoreReadSessionTests`, `SqliteSymbolGraphIndexTests`, and `WorkspaceIndexProviderTests` passed 121/121 in 951 ms. Scoped format, `git diff --check`, and the Release solution build passed with zero warnings/errors.
- Architecture quality: the wrapper remains storage-agnostic at its public boundary; internal capability discovery stays nullable and local. No public interface, MCP/CLI/schema, attached-table graph SQL, or unconditional family marker was added.

## Round 14: family relationship storage capability

- Rebuilt acceptance at `d42f2626` completed context in 4.332 seconds and graph in 1.296 seconds. Fixed observations selected relationship forward at 507 ms/1 row and reverse at 511 ms/1 row; unresolved names completed in 0/1 ms, all resolution arms totaled about 43 ms, and supplemental work took 168 ms. Earlier isolated direct relationship arms were 0.110/0.111 ms using the producer indexes.
- Compatibility projection inspection proved relationships have no overlay or tombstone layer. The family view includes `main.relationships` rows only when their source `version_id` is in `_miller_visible_entries`; graph endpoint joins additionally require the target/source symbol to be visible.
- Production-shape RED wrapped the real family session in `WorkspaceReadHandle`, preserved exact forward/reverse output, then failed the bounded work assertion because `FrontierRelationships` executed twice instead of zero.
- Minimal GREEN adds internal optional `IFamilyGraphRelationshipReader`. `FamilyStoreReadSession` runs fixed forward/reverse queries against version-pinned base tables, emits the existing `RelationshipForward`/`RelationshipReverse` observations after actual reader completion, and returns storage-neutral edges. `SqliteSymbolGraphIndex` merges them and omits only family compatibility relationship statements.
- `WorkspaceReadHandle` forwards the nullable internal capability from the start. A legacy handle exposes null for all three family graph capabilities and retains two compatibility relationship plus two compatibility unresolved-name statements.
- Caller parity covers one visible forward and one visible reverse edge plus stale-version forward/reverse rows, which are excluded. The plan uses exact checked-in `idx_read_relationships_from(from_symbol_id,version_id)` and `idx_read_relationships_to(to_symbol_id,version_id)` indexes and contains neither `MATERIALIZE` nor a relationship-table scan.
- Exact family/legacy GREEN passed 2/2 in 87 ms. `WorkspaceReadSessionTests`, `FamilyStoreReadSessionTests`, `SqliteSymbolGraphIndexTests`, and `WorkspaceIndexProviderTests` passed 121/121 in 953 ms. Scoped format, `git diff --check`, and the Release solution build passed with zero warnings/errors.
- Architecture quality: relationship visibility policy stays in the family read session, traversal/edge merging stays in the graph index, and the public `IWorkspaceReadSession`/MCP/CLI/schema contracts remain unchanged. No producer schema, temp index, workspace hydration, or attached-table SQL entered the graph layer.

## Round 15: fixed lookup-family phase telemetry

- Rebuilt acceptance at `8e711985` completed context in 3.153 seconds and graph in 290 ms. The remaining dominant measured interval was symbol lookup: 1,602 calls totaling 1,750 ms.
- Existing architecture already had the required internal correlation boundary: `WorkspaceReadContext.ReadTelemetry` carries a per-read `ReadPhaseTelemetry`, and `ContextTool.CompletePhase` is the single completion hook for the seven actionable context phases. No public interface, broad callback, MCP payload, or persisted telemetry schema changed.
- Caller-facing REDs failed to compile because fixed method-family snapshots, context-phase observations, baseline advancement, and the internal observer seam did not exist. The exact contract covers all twelve lookup families and the seven fixed phases `query_retrieval`, `term_retrieval`, `anchor_resolution`, `graph_reach`, `symbol_hydration`, `file_neighbours`, and `candidate_ordering`.
- Minimal GREEN replaces the aggregate wrapper counters with twelve fixed cumulative counter/timer slots while preserving the prior aggregate snapshot by summing those slots. `ReadPhaseTelemetry` records an immutable context baseline and advances an independent phase baseline, returning both fixed delta and total snapshots.
- `ContextTool` emits one correlated structured log event after each completed fixed phase, not one event per lookup. The event contains a fixed enum phase and fixed twelve-property delta/total records; no dynamic labels or identifiers are introduced.
- Cancellation is completion-honest: an exact test cancels from the observer after `anchor_resolution` and retains only the three completed phase snapshots. Later phases emit nothing.
- Exact lookup/phase/cancellation tests passed 3/3 in 120 ms. `WorkspaceIndexProviderTests` plus `ContextToolTests` passed 168/168 in 891 ms. Scoped format verification and `git diff --check` passed; the Release solution build completed with zero warnings/errors.
- Lookup behavior is unchanged. The completed-phase instrumentation is deliberately attached to the existing actionable (`reference_mode=off`) seven-phase pipeline; reference-aware mode has a different phase model and was not widened or relabeled.
