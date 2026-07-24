# Phase 7A Content Bounds And CLI Boundary Evidence

Date: 2026-07-23
Branch: `codex/miller-julie-takeover`

## Result

The `content` MCP surface now orients agents without an unbounded inventory or
bulk export:

- bare list reports exact `external_file` and `web` totals;
- list returns at most 20 rows per kind and reports returned/omitted counts;
- compact list is capped at 16,000 characters and JSON at 48,000;
- `shape` returns five head lines, five tail lines, exact line count, and a
  labeled text-derived severity summary within 8,000 characters;
- MCP `export` and its `content_workspace_id` parameter are hard-removed;
- `miller content export` remains the unchanged deterministic JSONL process
  contract;
- `miller content list --json` remains the v1 flat array used by Eros, while
  explicit `--kind all` now truthfully returns external and web imports;
- raised-cap imports hash, decode, chunk, tokenize, and insert incrementally
  without a complete-file byte array or complete normalized string;
- raised-cap imports reject logical lines over 65,536 characters, cap raw
  chunks at 1,048,576 characters, and roll back partial writes on invalid
  UTF-8, size drift, or an overlong line;
- compact and escape-heavy JSON reads bound very large default-cap lines,
  report exact truncation facts, and remain successful;
- JSON list/shape string bounds account for escape expansion rather than only
  raw input length;
- exact display-path ambiguity reports are capped at five candidates;
- compact and JSON read/shape diagnostics remain typed and within 8,000
  characters;
- compact and JSON CLI failures both write to stderr and exit 3 through a
  structural tool result rather than rendered-text inspection;
- chunk byte offsets consistently address LF-normalized UTF-8 text in both
  streaming and non-streaming imports, including CRLF inputs.

The active MCP contract is
[`content-mcp-v2.md`](../contracts/content-mcp-v2.md). The content corpus and
CLI export schemas remain in
[`content-corpus-v1.md`](../contracts/content-corpus-v1.md) and
[`cli-eros-v1.md`](../contracts/cli-eros-v1.md).

## API evidence

Miller inspection after refresh reports `ContentTool.Content` with 13
parameters. Its accepted operation switch contains `import`, `add_markdown`,
`search`, `read`, `shape`, `list`, and `remove`; neither `export` nor
`content_workspace_id` remains in the MCP method.

`ContentCorpusExternalStore.Inventory` performs a separate exact count query
before its deterministic limited source query. `Shape` streams ordered chunk
rows while retaining only bounded head/tail state and severity counters.
`ImportFileStreaming` is selected only when the caller explicitly raises
`max_bytes` above the configured default.

## Claude review disposition

The content-specific Claude review found five real gaps. All were reproduced
against the public store/tool/CLI surfaces and closed:

1. Escape-heavy list and shape JSON could cross the hard envelope and degrade
   into an error. JSON-aware truncation now caps the escaped contribution.
2. A huge single logical line could allocate the line and persist it as an
   unbounded chunk. Streaming imports now have explicit line and chunk caps.
3. Compact read failures and all JSON failures could return CLI exit 0.
   Typed content failures now consistently write stderr and exit 3.
4. Exact display-path ambiguity loaded every candidate, while diagnostics
   could echo unbounded input. Exact and suffix candidates now share the
   five-row cap, and diagnostics use bounded structured fields.
5. Streaming invalid-UTF-8 rollback and two shape diagnostic categories lacked
   direct coverage. Public regressions now cover rollback,
   `ambiguous_source`, and `content_corpus_missing`.

A follow-up Claude pass found five more accepted gaps. The rollback fixtures
now each contain more than 160 valid lines and 16 KiB before their injected
failure, so they prove that chunk insertion began before rollback. Default-cap
read rendering now bounds compact and escape-heavy JSON lines and reports
truncation. CLI `content list --kind all` returns both imported kinds without
changing the flat v1 shape. CLI exit status now uses a typed tool result, so
diagnostic-looking successful content exits 0. CRLF parity coverage pins
chunk offsets to normalized LF UTF-8 text for both import paths.

## TDD evidence

The initial focused RED compiled and failed on the missing inventory envelope,
missing shape operation, and still-active MCP export. Later focused RED cycles
proved:

- shape failures returned `content_error` instead of
  `missing_source_id`/`source_not_found`;
- the new list and shape JSON envelopes still reported schema version 1
  instead of 2.

After implementation:

- the initial slice passed 101 content/store/CLI/export-reader tests and 153
  content plus agent-instruction/description contract tests;
- the Claude-remediated slice passed 110 focused store/tool/CLI tests and 404
  content plus agent-instruction/description contract tests;
- the follow-up RED run reproduced seven failures: two rollback-fixture
  preconditions, two unbounded read formats, compact success misclassification,
  incomplete `--kind all`, and the additive JSON read contract;
- the follow-up GREEN passed 301 direct store/tool/CLI tests and 413 content
  plus agent-instruction tests;
- the complete fast suite passed 4,759 tests with two expected skips;
- the follow-up complete fast suite passed 4,768 tests with two expected
  skips;
- the scale suite was executed; its full parallel batch exposed two unrelated
  timing/contention failures, and both exact failing scale tests passed
  together on immediate rerun; the final full scale rerun passed all 87 tests;
- `dotnet build Miller.slnx -c Release --no-restore` passed with zero warnings
  and zero errors;
- `git diff --check` passed.

The fixtures cover exact totals independent from paging, 40-row maximum
inventory output with escape-heavy paths and URLs, deterministic severity
counts, typed shape failures, raised-cap streaming hash/chunk/read round-trips,
bounded chunks, rollback after partial streaming writes, normalized-LF byte
offset parity, hard MCP export removal, and the v1 CLI list/export shapes.

## Architecture quality

- Storage, aggregation, streaming, and severity classification remain in
  `Miller.Indexing`.
- MCP routing, typed diagnostics, budgets, and rendering remain in
  `ContentTool`.
- CLI list/export bypass the MCP renderer and continue through their existing
  process contracts.
- `Miller.Core` remains I/O-free.
- No new MCP tool or stateful spillover surface was added.
