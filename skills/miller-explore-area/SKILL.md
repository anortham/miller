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
   When you are starting fresh in an already-indexed repo, run `workspace(operation="onboarding")` first — it
   summarizes local telemetry into starter guidance for this repo.
2. For unfamiliar task-shaped work, start with:

```text
context(query="<task or concept>")
```

Use `failing_test`, `stack_trace`, or `entry_symbols` when the user gave those anchors.

3. If the user already named a symbol or file, use `inspect` first:

```text
inspect(target="<file-or-symbol>")
inspect(target="<symbol>", depth="overview")
```

Omitted `inspect` depth is `summary`. Use `depth=overview` for the first symbol read; escalate to
`depth=full` only when you need the complete body or complete relation lists.

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

If `trace` returns no refs, no neighbours, no path, or an unsupported bridge, follow its `Next:` actions first.
Typical recovery is `trace(mode="refs")`, `search(mode="source")`, a scoped `inspect(depth="overview")`, or a
bounded depth bump; a missing extracted path is not proof the code is unrelated.

Use `patterns(operation="list")` before raw route, HTML, SQL, JSON, YAML, TOML, or Markdown structure hunting.
Then search a shown `pattern_id` or follow the list output's `Next:` actions.

## Report

Keep the answer compact:

- Key entry points
- Core files and symbols
- Important caller/callee or data flow
- Suggested first file or symbol to inspect next
- Any stale-index or missing-index caveat

Do not read whole files until `context`, `search`, or `inspect` has narrowed the target.
