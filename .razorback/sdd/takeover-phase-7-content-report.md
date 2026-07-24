# Phase 7A Content Worker Report

## State

- Starting path: `/Users/murphy/source/miller/.worktrees/miller-julie-takeover`
- Starting branch: `codex/miller-julie-takeover`
- Starting HEAD: `7306f9c672ea91f90899f7c51080308ef1f4cac3`
- Starting dirty state: dirty with the Phase 7 content slice and parallel
  patterns/workspace/inspect slices already in progress
- Ending path: `/Users/murphy/source/miller/.worktrees/miller-julie-takeover`
- Ending branch: `codex/miller-julie-takeover`
- Ending HEAD: `7306f9c672ea91f90899f7c51080308ef1f4cac3`
- Ending dirty state: dirty by design; this worker did not stage or commit, and
  the shared worktree also contains parallel patterns/workspace/inspect changes

## Implemented

- MCP `content list` is a schema-v2 per-kind inventory with exact totals,
  returned/omitted counts, deterministic ordering, 20 rows per kind, and hard
  16,000-character compact / 48,000-character JSON budgets.
- MCP `content shape` provides source identity, exact line count, five head
  lines, five tail lines, and deterministic text-derived severity counts under
  an 8,000-character budget.
- MCP `content export` and `content_workspace_id` are hard-removed from the
  method, descriptions, operation parser, guidance, and tests.
- CLI list retains its v1 flat compact/JSON shape; its default remains
  external-only and explicit `--kind all` returns external plus web. CLI
  export retains its deterministic JSONL kind/workspace filters through
  `ContentCorpusExportReader`.
- Raised-cap imports use incremental BLAKE3 hashing, strict UTF-8 decoding,
  line normalization, overlapping chunk insertion, and FTS insertion without
  complete-file byte/text/normalized allocations.
- Raised-cap imports reject logical lines over 65,536 characters, cap raw
  chunks at 1,048,576 characters, and roll back streamed partial writes.
- JSON field truncation is escape-aware, exact alias ambiguity is capped at
  five candidates, and compact/JSON diagnostics are capped at 8,000
  characters.
- Compact and JSON reads cap each rendered line at 160 units, remain below
  48,000 characters, and report exact truncation facts.
- CLI compact and JSON content failures both write typed diagnostics to stderr
  and exit 3 through a structural execution result; diagnostic-looking
  successful content exits 0.
- Streaming and non-streaming chunk offsets share the normalized-LF UTF-8
  convention, including CRLF sources.
- Active contracts and Phase 7 evidence are documented.

## Public API shapes proved with Miller

- `ContentTool.Content` now has 13 parameters. Accepted MCP operations are
  `import`, `add_markdown`, `search`, `read`, `shape`, `list`, and `remove`.
- `ContentCorpusExternalStore.Inventory(string, string?, int)` returns
  `ExternalContentInventory` with per-kind totals and bounded source rows.
- `ContentCorpusExternalStore.Shape(string, string)` returns
  `ExternalContentShape`.
- `ContentCorpusExternalStore.ImportFileStreaming` is private and selected only
  when explicit `max_bytes` is above the configured default.
- `CliDispatch.Content` routes `list` and `export` directly to their preserved
  CLI contracts while sharing the content store and typed diagnostic renderer
  with `ContentTool`.

## RED/GREEN evidence

- Initial focused RED compiled and failed on the intended missing behaviors:
  list was the legacy JSON array, shape returned a diagnostic instead of a
  result, and MCP export still returned JSONL.
- Shape typed-failure RED: two cases expected `missing_source_id` and
  `source_not_found` but received `content_error`.
- Contract-version RED: list and shape expected schema version 2 but returned
  1.
- GREEN: 101 content/store/CLI/export-reader tests passed.
- GREEN: 153 content plus `AgentInstructionsTests` passed.
- Post-review GREEN: 110 focused store/tool/CLI tests and 404 content plus
  `AgentInstructionsTests` passed.
- Follow-up RED: seven failures reproduced the undersized rollback fixtures,
  unbounded compact/escape-heavy JSON reads, rendered-text status
  misclassification, incomplete `--kind all`, and missing JSON truncation
  fields.
- Follow-up GREEN: 301 direct store/tool/CLI tests and 413 content plus
  `AgentInstructionsTests` passed.
- The raised-cap fixture verifies exact BLAKE3 parity, multiple persisted
  chunks, first/middle/last bounded reads, and exact line count.
- The adversarial inventory fixture imports 40 sources with 5,000-character
  paths/URLs and proves the hard compact/JSON budgets.

## Claude review follow-up

- RED reproduced eight failures across escape-heavy JSON, unbounded long-line
  streaming, exact alias candidate count, diagnostic budgets, and CLI exit
  semantics.
- GREEN covers escape-heavy list and shape JSON, the 65,536-character line
  policy, the 1,048,576-character raw chunk cap, hash/read preservation,
  invalid-UTF-8 transactional rollback, exact alias capping, bounded read/shape
  diagnostics, shape `ambiguous_source`/`content_corpus_missing`, and compact
  plus JSON CLI exit 3 behavior.
- Lead follow-up added an internal stream-opening seam and a deterministic
  size-drift fixture. Invalid UTF-8 and changed-size failures now both prove
  transaction rollback after streamed chunk insertion. Both fixtures exceed
  the 16 KiB decoder buffer and 160-line chunk threshold before failure.
- A second follow-up bounded compact and escape-heavy JSON reads, corrected
  CLI `--kind all`, replaced rendered-text failure sniffing with a typed
  execution result, and pinned CRLF offset parity.
- A third fresh review found and fixed typed CLI handling for invalid
  `list`/`export` kinds, a same-length JSON truncation flag edge case, and
  fail-fast raised-cap growth checks during streaming. Seven focused
  regression cases and 307 direct store/tool/CLI tests passed.
- A fourth fresh review found and fixed the last two diagnostic issues:
  pathological operation names are now truncated inside the 8,000-character
  JSON budget, and invalid CLI export diagnostics retain `operation=export`.
  The shared renderer prevents CLI and MCP diagnostics from drifting.
- The final fresh Claude pass returned no findings and independently reran the
  4,775-test fast suite.

## Worker ceiling

- Focused tests: passed, 301 direct store/tool/CLI tests and 413 expanded
  content plus guidance tests, zero failures/skips.
- Fast suite: passed, 4,759 tests and two expected skips.
- Final fast suite: passed, 4,775 tests and two expected skips.
- Scale suite: the full parallel batch exposed one transient
  `julie-extract db_write_failed` WAL test and one context p95 timing failure;
  the two exact tests then passed together, 2/2, and the final full rerun
  passed 87/87. No content test failed.
- Release build: `dotnet build Miller.slnx -c Release --no-restore` passed with
  0 warnings and 0 errors.
- Whitespace gate: `git diff --check` passed.

## Architecture Quality

**Affected modules:** `Miller.Indexing` content store, server `ContentTool`, CLI
content dispatch, content contracts/tests.

**Caller-facing interface:** one existing MCP tool gains `shape` and bounded
list/read envelopes while losing bulk export; the CLI list JSON shape stays
v1 while `--kind all` now returns both imported kinds.

**Depth/locality check:** SQLite scans, exact counts, severity classification,
and streaming writes remain behind the store. Routing, diagnostics, rendering,
and budgets remain behind the tool. CLI compatibility remains behind CLI
dispatch.

**Test surface:** public `ContentTool.Content`, `CliDispatch.Run`, and real
SQLite-backed `ContentCorpusExternalStore` behavior.

**Seams/adapters:** the store's internal stream-opening seam makes size drift
deterministic in tests. The tool's internal execution result carries rendered
output plus error status so CLI control flow never parses user-visible text.

**Rejected shortcuts:** unbounded paging, MCP export continuation, in-memory
spillover state, whole-file raised-cap reads, changing the Eros CLI list/export
shapes.

**Architecture risk:** medium; the MCP list shape is intentionally breaking and
therefore versioned, while CLI compatibility and persistent schema remain
unchanged.

- Complexity stays local.
- The caller interface is smaller than the behavior it unlocks.
- Tests exercise the same public surfaces as callers.
- New records earn their keep by carrying exact totals and bounded shape facts.
- No speculative extensibility was added.
- The changes fix the unbounded query and allocation paths rather than hiding
  their output.

## Owned changed files

- `src/Miller.Indexing/ContentCorpusExternalStore.cs`
- `src/Miller.Server/Tools/ContentTool.cs`
- content-only portions of `src/Miller.Server/Cli/CliDispatch.cs`
- `tests/Miller.Tests/Indexing/ContentCorpusExternalStoreTests.cs`
- `tests/Miller.Tests/Server/ContentToolTests.cs`
- content-only portions of `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`
- `docs/contracts/content-mcp-v2.md`
- `docs/contracts/content-corpus-v1.md`
- `docs/contracts/cli-eros-v1.md`
- content-only portions of `docs/agent-guidance.md`
- content entries in `docs/README.md`
- `docs/findings/2026-07-23-phase7-content-bounds.md`
- this report

## Unresolved risks

No known correctness or contract risks remain in the Phase 7A scope. Final
branch-wide verification remains lead-owned because this is a shared worktree.
