# Phase 3 exact consumers and rename safety — 2026-07-23

## Result

Miller's agent-facing reference workflows now use exact target-resolved evidence keyed by symbol ID. `trace`,
`inspect`, `context`, CLI equivalents, and `rename_symbol` share the same normalized exact/fallback contract.
No agent-facing path calls the legacy name-only `ExtractReader.ReadReferences(dbPath, name)` reader.

`trace auto` was removed from the MCP tool, CLI, guidance, contracts, implementation, and tests. `trace` now
defaults to exact `refs`; callers and callees belong to `inspect depth=full`.

## Exact reference behavior

- inbound evidence merges direct identifiers, identifier resolutions, relationships, and resolved pending rows;
- outgoing evidence carries exact target symbol IDs and keeps unresolved target names in a separate fallback tier;
- canonical site deduplication prevents direct, overlay, and relationship evidence from double-counting one site;
- kind filters, exact offsets, and fallback offsets are applied independently;
- missing optional relationship projection data does not discard available exact identifier evidence;
- ambiguous same-name definitions suppress unresolved fallback rather than attributing it to the chosen homonym.

Inspect derives `callers` only from exact call and instantiation evidence. Other exact inbound evidence is rendered
as `referenced_by`. Exact outgoing call evidence becomes `callees`; unresolved calls become `callee_fallback`.

Context usage bundles label exact inbound rows `reference`, exact outgoing calls `callee`, and other exact outgoing
rows `dependency`. Unresolved inbound rows use `possible_reference`; unresolved outgoing rows use
`unresolved_callee` or `unresolved_dependency`. JSON carries target ID, resolution status, provenance, and numeric
evidence confidence.

## Reference continuation

`trace mode=refs` reuses the Phase 2 stateless continuation foundation. The checksum-bound token binds:

- workspace and exact target symbol IDs;
- artifact ID and extraction revision;
- reference-kind filter and include-definition flag;
- requested total limit;
- independent exact and fallback offsets.

The renderer measures serialized UTF-8 output and reduces the page until it is at most 16 KiB. A row that cannot
fit alone is refused, and missing, malformed, stale, cross-artifact, or filter-mismatched cursors cannot resume.

## Rename safety

`rename_symbol` defaults to `rename_mode=exact`. Exact mode:

- validates the requested new identifier before reading evidence;
- requires exact target-proven reference spans and a proven definition token;
- excludes sites resolved to another same-name symbol;
- refuses unresolved fallback candidates, unusable spans, or missing exact files;
- reports exact and fallback sites separately plus language, kind, and resolution coverage.

`rename_mode=include_fallback` is an explicit homonym-risk opt-in. Selected fallback sites remain labeled in compact
and JSON output. Multi-file apply still uses the existing atomic apply/rollback path. Successful compact apply
ends with an exact-symbol `impact` command and a test reminder.

`Type::member` and `Type.member` resolve as qualified members before the symbol-ID-shape heuristic.

## Contracts

- [Exact Reference Consumers v1](../contracts/exact-reference-consumers-v1.md)
- [Miller trace JSON v1](../contracts/trace-json-v1.md)
- [Miller edit JSON v1](../contracts/edit-json-v1.md)
- [Tool Continuation Contract v1](../contracts/tool-continuation-v1.md)

## Safety evidence

Focused fixtures prove:

- same-name targets have disjoint exact reference sets;
- callers exclude non-call evidence;
- exact callees use target IDs while unresolved callees remain fallback;
- exact context items cannot be confused with fallback items;
- exact rename refuses incomplete coverage and excludes a resolved homonym;
- explicit fallback rename excludes sites exactly resolved to another homonym while retaining unresolved sites;
- declaration-token selection distinguishes a symbol name from a same-named return or property type;
- rename JSON lists a definition/reference byte span once even when extraction emitted both facts;
- multiple exact facts for one byte span produce one rename edit rather than a false incomplete-coverage refusal;
- explicit fallback remains visible in compact and JSON;
- fully qualified member resolution checks the complete ancestor chain instead of only the immediate parent;
- import evidence, deterministic inbound ordering, canonical cursors, orphan relationship rows, and final
  output-budget diagnostics have focused regressions;
- full-set caller and `referenced_by` membership remains exact when the displayed reference page is truncated;
- the real-extractor cross-file rename scale case must select `include_fallback` for its unresolved call site, and
  the combined two-file apply still converges atomically;
- long reference pages remain within 16 KiB and resume without server state;
- relationship-projection absence preserves exact identifier evidence;
- atomic rollback and freshness recovery remain covered by the edit suite.

The repository's sealed-task protocol keeps prompts, checks, trajectories, task IDs, and per-task outcomes outside
implementation sessions. The user-controlled sealed replay is therefore a separate acceptance event; this phase
does not inspect or manufacture sealed rows.

## Verification

- fast suite: 4,650 passed, 2 platform skips, 0 failed;
- scale suite: 87 passed, 0 failed;
- Release build: 0 warnings, 0 errors;
- macOS arm64 Native AOT publish passed;
- focused benchmark scorer: 4 passed;
- the fallback-label and two distinct empty-reference hard benchmark rows passed their gate;
- three focused Claude review passes covered reference reading/trace, inspect/context, and edit/resolution/benchmark
  behavior. All accepted findings were fixed with regressions; rejected findings were checked against live
  contracts. A closure-only retry reached its bounded budget without emitting another result.
