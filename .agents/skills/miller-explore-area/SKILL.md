---
name: miller-explore-area
description: Use when orienting on an unfamiliar code area with Miller, explaining a module, finding entry points, or gathering context before a change.
user-invocable: true
arguments: "<area, concept, module, file, or task>"
allowed-tools: mcp__miller__context, mcp__miller__search, mcp__miller__inspect, mcp__miller__trace, mcp__miller__workspace
---

# Miller Explore Area

Use Miller's indexed context before raw file reads. The goal is to identify the important symbols, files, and flows with a few targeted calls.

## Workflow

1. If the workspace may be stale, run `workspace(operation="status")`; use `workspace(operation="refresh")` when needed.
2. For unfamiliar task-shaped work, start with:

```text
context(query="<task or concept>")
```

Use `failing_test`, `stack_trace`, or `entry_symbols` when the user gave those anchors.

3. If the user already named a symbol or file, use `inspect` first:

```text
inspect(target="<file-or-symbol>")
inspect(target="<symbol>", depth="full")
```

4. Use `search` for missing anchors:

```text
search(query="<identifier or phrase>")
search(query="<docs/prose phrase>", mode="content")
search(query="<source-body literal>", mode="source")
search(query="<imported log or web phrase>", mode="external|web")
search(query="<comment or literal>", regions="comment|string_literal|doc_comment")
```

For audits across registered workspaces, use `content search "<term>" --workspace-id all --kind source|docs|config|external_file|web` and bounded `content read` windows before escalating to broader context.

`context` integration from content hits remains opt-in: use it only when the user asks for surrounding code context after an audit or text-search hit.

5. Use `trace` when the question is about flow:

```text
trace(target="<symbol>")
trace(target="<from>", mode="path", to="<to>")
```

## Report

Keep the answer compact:

- Key entry points
- Core files and symbols
- Important caller/callee or data flow
- Suggested first file or symbol to inspect next
- Any stale-index or missing-index caveat

Do not read whole files until `context`, `search`, or `inspect` has narrowed the target.
