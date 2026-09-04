---
name: miller-explore-area
description: Use when orienting on an unfamiliar code area with Miller, explaining a module, finding entry points, or gathering context before a change.
user-invocable: true
arguments: "<area, concept, module, file, or task>"
allowed-tools: mcp__miller__context, mcp__miller__search, mcp__miller__inspect, mcp__miller__trace, mcp__miller__workspace
---

# Miller Explore Area

Use Miller's indexed context before raw file reads. The goal is to identify the important symbols, files, and flows with a few targeted calls.

## Workspace targeting (required)

Every workspace-bound Miller MCP call names its target with `workspace_id`; Miller never infers it from the
launch directory, environment variables, MCP Roots, or a previous call. Get the ID from
`workspace(operation="list")`, or from `workspace(operation="open", path="/absolute/project")` when the repo is
absent. The examples below write it as `workspace_id="<id>"`. Only `workspace` `list`, `open`, `remove`,
`prune`, and `dashboard` run without one; `current` and `primary` are CLI-only. The full targeting rules live
in the `miller-orientation` skill.

## Workflow

1. If the workspace may be stale, run `workspace(operation="status")`; use `workspace(operation="refresh")` when needed.
   When you are starting fresh in an already-indexed repo, run `workspace(operation="onboarding")` first — it
   summarizes local telemetry into starter guidance for this repo.
2. For unfamiliar task-shaped work, start with:

```text
context(workspace_id="<id>", query="<task or concept>")
```

Use `failing_test`, `stack_trace`, or `entry_symbols` when the user gave those anchors.

3. If the user already named a symbol or file, use `inspect` first:

```text
inspect(workspace_id="<id>", target="<file-or-symbol>")
inspect(workspace_id="<id>", target="<symbol>", depth="overview")
```

Omitted `inspect` depth is `summary`. Use `depth=overview` for the first symbol read; escalate to
`depth=full` only when you need the complete body or complete relation lists.

4. Use `search` for missing anchors:

```text
search(workspace_id="<id>", query="<identifier or phrase>")
search(workspace_id="<id>", query="<docs/prose phrase>", mode="content")
search(workspace_id="<id>", query="<source-body literal>", mode="source")
search(workspace_id="<id>", query="<imported log or web phrase>", mode="external|web")
search(workspace_id="<id>", query="<comment or literal>", regions="comment|string_literal|doc_comment")
```

For exact-text audits across registered workspaces, use
`content(operation="search", workspace_id="all", query="<term>", content_kind="source|docs|config|external_file|web")`
and bounded `content(operation="read", ...)` windows (the `miller-text-audit` skill) before escalating to broader context.

`context` integration from content hits remains opt-in: use it only when the user asks for surrounding code context after an audit or text-search hit.

5. Use `trace` when the question is about flow:

```text
trace(workspace_id="<id>", target="<symbol>")
trace(workspace_id="<id>", target="<from>", mode="path", to="<to>")
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
