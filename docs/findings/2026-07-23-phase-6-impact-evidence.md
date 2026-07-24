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

## Verification

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
