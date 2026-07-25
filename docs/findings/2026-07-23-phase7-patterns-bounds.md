# Phase 7 Patterns Bounds Evidence

## Outcome

Phase 7B makes free-text pattern fan-out visible, restores `directory` to its literal full-parent meaning,
adds an explicit `top_directory` rollup, and removes mutable catalog counts from active guidance.

The 2026-07-25 takeover re-audit also made every successful MCP operation byte-bounded and made its
population coverage exact. Exact and free-text search now report the complete matching fact population even
when the requested row limit or MCP byte budget retains only a prefix. List and summary report the same
total/returned/omitted/truncated coverage over patterns and groups.

## Implementation Evidence

- Query search examines the complete observed pattern-ID population, reports considered/matched/returned/omitted
  counts, and marks truncation when the 25-ID retrieval bound omits matches.
- JSON and compact output expose the same fan-out facts.
- `PatternFactsReader` returns the full normalized parent for `directory`; `top_directory` returns only the first
  parent segment.
- A synthetic SQLite fixture aggregates 10,005 structural facts into one exact directory count.
- The MCP description, public README, agent working notes, and both mirrored patterns-audit skills use runtime
  `patterns operation=list` output as the authoritative catalog instead of freezing counts.
- Exact searches obtain total population, retained rows, global pattern existence, and language-scoped
  suggestions from one read transaction, so empty classification cannot race a concurrent artifact update.
- The exact-search statement windows only identity/order columns before joining retained payload rows, so exact
  coverage does not force SQLite to sort every matching `metadata_json` value.
- Path-glob fallback compiles its matcher once, scans lightweight fields once, counts every matching row, and
  deserializes metadata only for retained rows.
- List and summary use SQLite aggregation where filters can be pushed down and lightweight streaming aggregation
  otherwise; neither materializes the full fact population as `PatternMatchRow` objects.
- Free-text search ranks all matching pattern IDs as one population. When more than 25 IDs match, filtered counts
  determine the retained IDs so a globally rare but filter-relevant pattern cannot disappear.
- Free-text observed IDs, filtered ranking, retained IDs, exact match totals, and retained rows share one
  read-only transaction, so a rebuild promote cannot mix snapshots in one fan-out response.
- Metadata equality predicates evaluate JSON type and value at most once per row while preserving string,
  boolean, null, numeric, object, and array equality semantics.
- MCP list and summary order their retained prefix by population count before applying the shared 12 KiB output
  budget. CLI JSON remains exhaustive and deterministically key-sorted.
- List fact aggregation and optional catalog overlay share one read transaction, preventing mixed-revision
  counts and labels during rebuild promotion.
- MCP list and summary pre-cap byte-budget render candidates from conservative minimum serialized row sizes, so
  a large exact group population cannot trigger repeated rendering of rows that cannot fit in 12 KiB.
- Invalid facet keys and oversized encoded inputs are typed refusals in MCP and CLI instead of internal failures.
- Metadata filters are capped at 16, query-no-match output uses the same 12 KiB budget, and irreducible metadata
  overflow is a typed `output_metadata_too_large` refusal.
- Compact output preserves every active filter and prioritizes each filtered key in retained-row metadata;
  list and summary support target-free metadata discovery and echo filters on populated and empty results.
- Empty summary output keeps `group_by` and `facet`, and truncation recovery actions keep the caller's active
  `language`, `path`, and combined `where` population while reducing only `limit`; JSON emits numeric `limit`.
- Exact-ID near matches include concrete copyable search and summary recovery actions in compact and JSON.
- Query-no-match suggestions remain inside an active language filter while the exact considered-ID count stays
  global, and both facts are read from the same transaction.
- MCP rendering reserves diagnostic headroom, checks the final attached response against 12 KiB, and uses the
  relaxed JSON encoder throughout so attachment cannot expand safe source characters past the budget.
- The duplicate `patterns-v1.md` contract was removed. Compact, JSON, directory, fan-out, coverage, ordering, and
  budget semantics now live in the canonical `patterns-json-v1.md` contract.

## Live Dogfood Evidence

The Release CLI against Miller's current artifact reported:

- exact `json.property.v1`: 38,060 total matches, 2 returned, 38,058 omitted;
- free-text `json`: 41 IDs considered, 5 matched and retained, 45,310 total facts, 2 returned;
- file summary: 1,343 groups over 52,240 facts, exhaustively returned by CLI in 0.28 seconds;
- path-glob exact search `**/*.json`: 29,812 total matches and 2 returned in 0.30 seconds;
- path-glob query search `json` with `**/*.json`: 34,584 total matches and 2 returned in 0.22 seconds;
- non-SQL glob exact search `docs/*/*/*/aggregate.json`: 72 total and returned matches in 0.12 seconds;
- target-free summary `where=query_family=framework`: one exact group over the full 52,240-fact population in
  0.10–0.11 seconds warm.

The first fallback implementation recompiled its regular expression per fact and took 29.68 seconds. Compiling
once and using one lightweight scan removed that regression without weakening the exact population count.

Miller remains materially stronger than Julie for this workflow. Julie applies its global result limit before
summary aggregation and caps list observation at 10,000 facts. Miller now preserves full-population aggregation,
catalog metadata, filters, diagnostics, stable JSON, exact coverage, and bounded MCP delivery.

## Ownership

SQLite aggregation and normalization remain in `PatternFactsReader`. Request parsing, typed diagnostics,
fan-out selection, and bounded rendering remain in `PatternsTool`. Parser recognition remains in
`julie-extractors`; `Miller.Core` is unchanged.

## Verification

- RED: six focused failures proved missing fan-out fields, stale two-segment directory grouping, the absent
  explicit top-directory mode, and hardcoded description counts.
- GREEN: 37 focused `PatternsToolTests` and `PatternFactsReaderTests` passed.
- Related pattern SQL, CLI, diagnostic, and guidance-budget scope: 117 passed.
- `dotnet build Miller.slnx -c Release --no-restore`: zero warnings and zero errors.
- `git diff --check`, `CLAUDE.md`/`AGENTS.md` mirroring, and patterns-skill mirroring passed.
- 2026-07-25 final focused re-audit: 117 pattern-related/budget-helper tests passed.
- 2026-07-25 final fast gate: 4,923 passed, 2 expected environment skips, zero failed, 25 seconds wall time.
- 2026-07-25 Scale gate: 91 passed, 3 configured sidecar/platform skips, zero failed.

## Claude Re-audit

Thirteen completed Opus/high review passes were evaluated against the live code. Two additional clean-gate
attempts reached their turn cap without a verdict and were retried rather than counted as approval.

- Round one exposed fallback repeated parsing/regex compilation, misleading bounded prefixes, a duplicate
  contract, and separate exact count/row snapshots.
- Round two exposed full filtered-population materialization, alphabetical summary prefixes, and an unnecessary
  second filtered scan for small fan-out populations.
- Round three exposed default summary over-grouping, missing SQL facet parity coverage, invalid truncation
  guidance, and the dead one-shot glob helper.
- Round four approved the corrected behavior and identified four remaining low-severity opportunities. The
  exact-search CTE now sorts only identity/order columns, unreachable metadata fallback code is removed,
  summary accepts its documented target-free `where`, and query fan-out now uses one read transaction.
- Rounds five through seven removed CLI/contract drift, pre-capped large collection rendering, typed invalid
  facets and oversized inputs, optimized metadata SQL, and documented the intentional full aggregate scan.
- Rounds eight through ten capped metadata filters, bounded query-no-match metadata, preserved all compact
  filters, kept language-scoped suggestions honest, and aligned target-free list and summary behavior.
- Round eleven found final-envelope budgeting, filter echo, exact-search snapshot, recovery-action, CLI-help,
  and multi-filter metadata gaps. Every accepted finding received a focused regression and repair.
- Round twelve found the remaining exact-ID recovery, list/catalog snapshot, and typed numeric action gaps.
- Round thirteen returned `verdict=clean`.

The full worker record is `.razorback/sdd/takeover-phase-7-patterns-report.md`.
