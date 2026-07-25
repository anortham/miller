# Phase 6 impact evidence

Date: 2026-07-23

## Result

Miller impact now preserves the evidence needed to explain and rank reverse reachability:
predecessor, edge kind, confidence, source, centrality, and visibility. Hop remains primary;
relationship priority, centrality, visibility, and stable location order peers. SQLite and
in-memory traversal return the same evidence.

File and diff inputs seed actionable callable/type symbols. Exact tests enter the graph only
through labeled `test_linkage` or `test_coverage` metadata. Filename/role candidates fill only
remaining capacity and remain explicitly heuristic (`test_candidate`, `filename_role`, `0.35`).
The heuristic recognizes suffix and prefix conventions used across supported ecosystems:
`ServiceTests`, `service_test`, `service.test`, `service_spec`, and `test_service`.
The current Miller artifact contains zero symbols with either exact-link metadata key, so that
tier is implemented and fixture-proven but dormant.

MCP `from_index_revision` and optional `from_artifact_id` now use the same preparation and
rendering core as the CLI. Delta traversal separates deleted paths from current unseeded paths;
normal changed-path and diff modes now preserve the same seeded/unseeded evidence. Graph rows and
heuristic test candidates have separate counts and truncation signals. Compact output is capped
at 6,000 characters while JSON remains the complete deterministic channel.

## Evidence

- Caller-facing graph tests prove typed edge evidence, ranking, visibility, actionable seeds, and
  SQLite/in-memory parity.
- MCP/CLI tests prove byte-equivalent revision-delta output through the shared core.
- Delta tests prove deleted paths do not seed traversal and remain distinct from unseeded paths.
- A 100-caller fixture reproduced output above 14 KB before the bound and proves the 6,000-character
  ceiling plus JSON recovery guidance after the change.
- Dangling exact-link fixtures prove the SQLite and in-memory adapters reject the same unknown endpoints.
- Equal-priority edge fixtures prove kind selection is stable across insertion order and storage adapters.
- Frontier-probe instrumentation proves evidence traversal stops querying once depth truncation is known.

## Independent review

A fresh Claude review found six defects; all six were reproduced and fixed:

1. filename-role matching covered only `ServiceTests`;
2. normal changed-path and diff results dropped seeded/unseeded path arrays;
3. SQLite admitted dangling test-linkage targets rejected by the in-memory graph;
4. evidence traversal queried every node on an already-proven depth frontier;
5. equal-priority edge kinds depended on row order;
6. heuristic candidate counts and truncation were not independently visible.

The first full gate also exposed stale minimal bridge and large-DB schemas after the new
`visibility` and confidence reads. Their synthesized v1 tables now include the pinned columns and
round-trip visibility.

The follow-up review confirmed the six repairs and found four smaller gaps. Normal changed-path and
diff requests with no seeds now use the same `not_run/no_seeds` disposition as revision deltas;
SQLite edge tie-breaking now uses ordinal comparison across cultures; and the traversal contract's
field count and delta example now include all fourteen frozen fields.

A fresh 2026-07-25 Claude pass found that post-rank truncation counts could invert, heuristic
displacement could still report `exhausted`, `Run` and revision-delta ranking had diverged, maximum
bounds were not clamped, the attempted full-graph optimization regressed the documented large-repo
architecture, SQLite omitted Blazor component edges, and no-seed diagnostics lacked recovery calls.
All findings were reproduced and accepted. Both Impact paths now share one bounded ranking core;
returned graph and heuristic counts are disjoint and non-negative; any omitted graph or heuristic
candidate prevents an exhausted result; depth and limit are clamped to 5 and 1,000; no-seed JSON
offers file search and refresh calls; and SQLite/fully loaded graphs have Blazor evidence parity.

The performance repair retained on-demand graph reads. SQLite now batches every BFS depth, depth
boundary proof, and centrality lookup instead of issuing neighbour, degree, and visibility queries
per node. See
[`2026-07-25-impact-readpath-performance.md`](2026-07-25-impact-readpath-performance.md).

The next structured Claude pass found nine additional issues in that batch path. All were verified:
unbounded frontier parameters and cache growth, nullable overlay confidence, direct/overlay confidence
drift, storage-dependent pre-window ordering, limit misattribution for unresolvable nodes, false
filename-scan truncation, an unreachable scalar edge implementation, missing diff-path recovery, and
duplicate supplemental-edge hydration. The repair caps SQL batches at 500, bounds and clears the
per-traversal evidence cache, short-circuits frontier proof by batch, normalizes both confidence arms,
uses storage-neutral pre-window keys, attributes limit truncation only to resolvable graph rows,
reports only observed heuristic truncation, removes the dead scalar implementation, derives recovery
paths from parsed diffs, and keeps supplemental edges in one batch path.

The review recommendation to add a second dedicated frontier-existence SQL implementation was
corrected: the frontier proof reuses the single normalized batch-edge implementation in bounded
chunks and returns after the first unseen neighbour. This preserves one edge truth policy while
avoiding both an unbounded query and another SQL copy that could drift.

The next follow-up confirmed all nine repairs and found five remaining gaps. Filename-role scanning
could still omit methods from a real test file after dense source-file rows consumed its row cap;
candidate-only omission could disagree with `truncated_by_limit`; frontier proof populated a cache
that could not be reused; batch SQL sorted rows whose order was normalized later; and the performance
evidence did not explicitly distinguish the CLI SQLite graph from the MCP resident graph. The accepted
repair enumerates matching file paths before hydrating and bounding genuine test candidates, keeps
`truncated_by_limit` authoritative for any result-cap omission, uses uncached 100-ID frontier-proof
chunks, removes the redundant SQL sort, adds end-to-end `ImpactTool.Run` coverage over the SQLite
graph, and pins every timing to the CLI surface that produced it.

The final follow-up found two residual issues. The obsolete 64-candidate safety ceiling could label a
complete result-cap window as `reason=limit`, and successful diff requests parsed the full diff a second
time solely to prepare an unused empty-result diagnostic. The ceiling is removed because the bounded
ranking input and public limit already constrain expansion; diff-path recovery now parses only when an
empty-result diagnostic is actually emitted.

That follow-up then exposed a wrapper-only classification bug: `impactedCount` intentionally excludes
tests, so a tests-only result was rendered with populated `tests[]` but attached an empty-result
diagnostic. The wrapper now tracks total returned rows separately from the non-test telemetry KPI and
emits the diagnostic only when both partitions are empty.

The next check found that mixed changed-path diagnostics guessed the first input path instead of using
the actual unseeded path, and that tests-only telemetry could pair `outcome=ok` with
`result_count=0`. The internal execution result now carries unseeded paths and both count axes;
diagnostics prefer the first proven-unseeded path, while telemetry `result_count` records all returned
rows. The public static `impactedCount` compatibility output remains the non-test partition.

The next review removed the final text-derived ambiguity. Empty diagnostics now consume a typed
execution reason, so a partially seeded input with an empty closure is `no_dependents`, not the false
claim `no_seed_symbols`. Revision-delta rendering returns the same execution evidence and preserves
real returned/visited counts for telemetry. Duplicate diff-note branches left by the earlier
normalization were removed.

The final actionability pass caught a vacuous mixed-path test, stale public traversal wording, and
telemetry buckets that no longer described the clamped bounds. Mixed seeded/unseeded empty closures now
offer file-search and refresh actions for the proven-unseeded path, the test asserts both calls
positively, the public core documents its 500–2,000-row candidate window before `limit`, and telemetry
distinguishes the reachable 101–250, 251–500, and 501–1,000 ranges.

The final truthfulness check removed the last input-derived recovery fallback. All-seeded empty
closures now emit no stale-path recovery; only execution-proven unseeded paths receive file-search and
refresh actions. The public `nodesVisited` documentation now states its exact graph-only,
pre-window `reached_count` meaning and explicitly excludes heuristic test candidates.

The final recovery check found that file-shaped Impact targets could recommend a Trace call that Trace
correctly refuses for files. Normal execution now carries an exact resolved seed symbol for diagnostic
trace actions; file targets trace that symbol id and still inspect the original file target.

The last normalization edge aligns action-target selection with the input router: explicitly blank or
whitespace `target` values now fall back to the execution-proven unseeded path, preserving both
file-search and refresh recovery.

The final Claude clean pass returned `verdict=clean` with an empty findings array after verifying
symbol, file, changed-path, diff, blank-target, and revision-delta recovery paths.

## Verification

- Final post-review focused set: 190 passed.
- Final fast suite: 4,891 passed, 2 environment skips.
- Final Scale suite: 91 passed, 3 configured sidecar/platform skips against the real pinned extractor.
- Final Release build: 0 warnings, 0 errors.
- Final `git diff --check`: clean.
- Focused Phase 6 and compatibility filter: 235 passed.
- Fast suite: 4,727 passed, 2 environment skips.
- Scale suite: 87 passed against the real pinned `julie-extract`.
- Release build: 0 warnings, 0 errors.
- Native AOT `osx-arm64` publish: passed.
- Plugin contracts: 48 passed.
- Agent-efficiency Python harness: 99 passed.
- Retrieval evaluator: 95 passed.
- `git diff --check`: clean.

## Bridge decision

No bridge/web reverse-impact edge was added. Miller already exposes deterministic bridge evidence
through `trace --mode bridge` and structural `patterns`; the visible Phase 6 evidence showed no
measured impact workflow miss requiring those edges. Adding them without a measured need would
inflate impact recall and confidence beyond the evidence.

## Remaining limits

Traversal is only complete for current indexed edges. Dynamic dispatch, reflection, configuration,
generated code, and missing extractor edges remain outside the claim. Exact test linkage remains
dormant until `julie-extract` emits labeled linkage metadata.
