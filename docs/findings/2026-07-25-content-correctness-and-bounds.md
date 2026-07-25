# Content Correctness And Bounds Evidence

**Date:** 2026-07-25  
**Status:** implementation complete; final Claude re-review clean

## Outcome

Miller's Content workflow remains a decisive advantage over Julie's transient
spillover cache. The implementation now makes that advantage safe for agent
context: persistent imported and workspace text can be searched and read without
stale claims, all-workspace fail-fast behavior, whole-corpus raw-text allocation,
or an unbounded MCP response.

## Accepted audit findings

| Finding | Disposition |
|---|---|
| Search fields and `limit` could create unbounded output | 12 KiB serialized ceiling, bounded fields, `limit` 1–100 |
| Implicit current-workspace search bypassed revision checks | Workspace kinds open against the live `symbols.db` revision |
| One stale registered workspace aborted an all-workspace search | `all` and `registered` isolate and report failures |
| FTS open materialized all raw chunk text | Open reads metadata; search batches raw-text reads for candidate IDs |
| Raw BM25 scores were merged across different corpora | Results merge by local rank and stable workspace/source keys |
| Read could hide a clamp at the source boundary | Compact and JSON expose store clamping and omissions |
| JSON read lacked continuation and clamp facts | Schema v3 carries counts, omissions, truncation, and callable continuation |
| Every failure received read-specific recovery actions | Recovery is selected by operation and diagnostic |
| Import/list/remove diagnostics were generic | Stable operation-specific codes cover common caller failures |
| Search JSON changed type between hit and empty results | One schema-v3 object contains `results` in both cases |
| Search-to-read source drift was invisible | `content_hash` spans import, search, read, shape, and list |
| Import/remove echoed unbounded caller-controlled fields | Inputs and rendered metadata are byte-bounded |
| CLI export materialized the corpus twice | SQLite rows stream directly as deterministic JSONL |

## Additional corrections

- Explicit `content_kind=all` searches all five searchable content kinds.
- Healthy results survive a stale peer workspace and retain a degradation warning.
- Search distinguishes bounded result probing from serialized-output omission and
  reports `more_may_exist` when the probe cannot prove exhaustion.
- Inventory count and page queries share one SQLite read transaction.
- Source metadata and chunk reads for shape/read share one SQLite read transaction.
- Forward compact continuation terminates at the source boundary.
- The MCP `export` operation remains removed; no deprecated compatibility path exists.
- Candidate coverage is explicitly probe-bounded rather than presented as an
  exhaustive count.
- All-degraded searches report `workspace_search_incomplete` in MCP and CLI;
  they never claim a proven lexical miss.
- Degraded-workspace detail is capped at three rows with exact omitted counts.
- CLI search retains limits above the MCP cap without limit-sized preallocation.
- JSONL row terminators remain literal LF on Windows.

## Claude review loop

The first fresh pass found thirteen implementation defects and five suggestions.
After remediation, the first follow-up found eight remaining defects. The next
targeted pass found four truthfulness/resource defects, and the following pass
found two renderer gaps. Every confirmed defect was reproduced or inspected
locally and repaired. The final targeted review returned `verdict=clean`.

## Verification

- Focused test gate:
  `ContentToolTests|FtsTextContentSearchIndexTests|ContentCorpusExternalStoreTests|ContentCorpusExportReaderTests|CliDispatchTests`
- Result: 340 passed, 0 failed, 0 skipped.
- Release build: 0 warnings, 0 errors before documentation finalization.
- Contract: [`content-mcp-v3.md`](../contracts/content-mcp-v3.md).
