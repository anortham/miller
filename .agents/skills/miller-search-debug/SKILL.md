---
name: miller-search-debug
description: Use when Miller search misses expected results, ranks surprising hits, returns noisy output, or needs mode/content/region troubleshooting.
user-invocable: true
arguments: "<query and expected result>"
allowed-tools: mcp__miller__search, mcp__miller__inspect, mcp__miller__context, mcp__miller__trace, mcp__miller__workspace
---

# Miller Search Debug

Diagnose search behavior by checking the query mode, index freshness, projection limits, and whether the expected result exists.

## Workspace targeting (required)

Every workspace-bound Miller MCP call must name its target with `workspace_id`. Miller does not infer the
workspace from the launch directory, environment variables, MCP Roots, or a previous call.

```text
workspace(operation="list")
workspace(operation="open", path="/absolute/project")
```

Use the ID those return on every `search`, `inspect`, `context`, `trace`, `impact`, `edit`, `patterns`,
`content`, and `tests` call, and on every scoped `workspace` operation (`status`, `health`, `onboarding`,
`refresh`, `full`, `leader`). The examples below write it as `workspace_id="<id>"`.

`workspace` `list`, `open`, `remove`, `prune`, and `dashboard` need no ID.
`content(operation="search", workspace_id="all")` stays the read-only cross-workspace text audit.
`current` and `primary` are CLI-only selectors; MCP refuses them.

## Workflow

1. Reproduce the exact query:

```text
search(workspace_id="<id>", query="<original>", limit=20)
```

2. Compare modes when the query intent is unclear:

```text
search(workspace_id="<id>", query="<symbol-ish>", mode="symbol")
search(workspace_id="<id>", query="<path-ish>", mode="file")
search(workspace_id="<id>", query="<docs or prose>", mode="content")
search(workspace_id="<id>", query="<source body text>", mode="source")
search(workspace_id="<id>", query="<imported log text>", mode="external")
search(workspace_id="<id>", query="<imported web text>", mode="web")
search(workspace_id="<id>", query="<broad text>", mode="all-text")
search(workspace_id="<id>", query="<comment or literal>", regions="comment|string_literal|doc_comment")
search(workspace_id="<id>", query="<known area>", file_pattern="src/ui/**", language="typescript")
```

3. Check common beta gotchas:

- Natural-language search hides test code by default; use `exclude_tests=false` when tests are expected.
- Symbol search ranks `name + signature`; docs/prose belong in `mode=content`.
- Source bodies belong in `mode=source`; imported logs/reports and web markdown belong in `mode=external` or `mode=web`.
- Cross-workspace exact-text audits should use `content search "<term>" --workspace-id all --kind source|docs|config|external_file|web`.
- Comment, doc-comment, and string-literal searches require region indexing and a fresh sidecar.
- File/path queries should use `mode=file` when auto mode looks noisy.
- Scoped workflows should use `file_pattern` and `language` before raising `limit`.
- Stale indexes should be refreshed with `workspace(operation="refresh")`.

4. Verify the expected item exists:

```text
search(workspace_id="<id>", query="<exact symbol or file>", mode="auto", exclude_tests=false)
inspect(workspace_id="<id>", target="<expected-symbol-or-file>")
```

5. If the expected result is connected by calls rather than text, use:

```text
trace(workspace_id="<id>", target="<nearby-symbol>")
context(workspace_id="<id>", query="<workflow around expected result>")
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
