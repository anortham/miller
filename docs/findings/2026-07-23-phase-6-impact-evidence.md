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
The current Miller artifact contains zero symbols with either exact-link metadata key, so that
tier is implemented and fixture-proven but dormant.

MCP `from_index_revision` and optional `from_artifact_id` now use the same preparation and
rendering core as the CLI. Delta traversal separates deleted paths from current unseeded paths,
and both normal and delta results expose explicit completion/truncation evidence. Compact output
is capped at 6,000 characters while JSON remains the complete deterministic channel.

## Evidence

- Caller-facing graph tests prove typed edge evidence, ranking, visibility, actionable seeds, and
  SQLite/in-memory parity.
- MCP/CLI tests prove byte-equivalent revision-delta output through the shared core.
- Delta tests prove deleted paths do not seed traversal and remain distinct from unseeded paths.
- A 100-caller fixture reproduced output above 14 KB before the bound and proves the 6,000-character
  ceiling plus JSON recovery guidance after the change.
- The focused Phase 6 suite passed 116 tests; the Release build completed with zero warnings/errors.

## Bridge decision

No bridge/web reverse-impact edge was added. Miller already exposes deterministic bridge evidence
through `trace --mode bridge` and structural `patterns`; the visible Phase 6 evidence showed no
measured impact workflow miss requiring those edges. Adding them without a measured need would
inflate impact recall and confidence beyond the evidence.

## Remaining limits

Traversal is only complete for current indexed edges. Dynamic dispatch, reflection, configuration,
generated code, and missing extractor edges remain outside the claim. Exact test linkage remains
dormant until `julie-extract` emits labeled linkage metadata.
