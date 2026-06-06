---
name: miller-search-debug
description: Use when Miller search misses expected results, ranks surprising hits, returns noisy output, or needs mode/content/region troubleshooting.
user-invocable: true
arguments: "<query and expected result>"
allowed-tools: mcp__miller__search, mcp__miller__inspect, mcp__miller__context, mcp__miller__trace, mcp__miller__workspace
---

# Miller Search Debug

Diagnose search behavior by checking the query mode, index freshness, projection limits, and whether the expected result exists.

## Workflow

1. Reproduce the exact query:

```text
search(query="<original>", limit=20)
```

2. Compare modes when the query intent is unclear:

```text
search(query="<symbol-ish>", mode="symbol")
search(query="<path-ish>", mode="file")
search(query="<docs or prose>", mode="content")
search(query="<comment or literal>", regions="comment|string_literal|doc_comment")
search(query="<known area>", file_pattern="src/ui/**", language="typescript")
```

3. Check common beta gotchas:

- Natural-language search hides test code by default; use `exclude_tests=false` when tests are expected.
- Symbol search ranks `name + signature`; docs/prose belong in `mode=content`.
- Comment, doc-comment, and string-literal searches require region indexing and a fresh sidecar.
- File/path queries should use `mode=file` when auto mode looks noisy.
- Scoped workflows should use `file_pattern` and `language` before raising `limit`.
- Stale indexes should be refreshed with `workspace(operation="refresh")`.

4. Verify the expected item exists:

```text
search(query="<exact symbol or file>", mode="auto", exclude_tests=false)
inspect(target="<expected-symbol-or-file>")
```

5. If the expected result is connected by calls rather than text, use:

```text
trace(target="<nearby-symbol>")
context(query="<workflow around expected result>")
```

## Report

Use this shape:

```text
Search Debug: "<query>"
Expected: <symbol/file/text>

Observed:
- <top results or failure>

Diagnosis:
- <wrong mode, stale index, hidden tests, projection limit, region disabled, or true miss>

Next:
- <specific query, refresh, or implementation follow-up>
```

Do not widen Miller's symbol ranking as a first response. First prove that `mode=content`, `regions=...`, file mode, or refresh does not solve the workflow.
