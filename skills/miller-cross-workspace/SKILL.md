---
name: miller-cross-workspace
description: Use when querying another registered workspace with Miller, comparing repos, opening a workspace, or routing search/inspect/context/impact/trace through workspace_id.
user-invocable: true
arguments: "<workspace path, display id, or cross-repo task>"
allowed-tools: mcp__miller__workspace, mcp__miller__search, mcp__miller__inspect, mcp__miller__context, mcp__miller__impact, mcp__miller__trace
---

# Miller Cross Workspace

Miller can read registered workspaces without changing directories. Use `workspace_id` on read tools instead of shelling into another repo.

## Workspace targeting (required)

Every workspace-bound Miller MCP call names its target with `workspace_id`; Miller never infers it from the
launch directory, environment variables, MCP Roots, or a previous call. Get the ID from
`workspace(operation="list")`, or from `workspace(operation="open", path="/absolute/project")` when the repo is
absent. The examples below write it as `workspace_id="<id>"`. Only `workspace` `list`, `open`, `remove`,
`prune`, and `dashboard` run without one; `current` and `primary` are CLI-only. The full targeting rules live
in the `miller-orientation` skill.

## Select A Workspace

List known workspaces:

```text
workspace(operation="list")
```

Valid `workspace_id` selectors:

- display ID
- unique prefix
- full workspace ID
- registered absolute root path

`current` and `primary` are CLI-only selectors; MCP refuses them.

Prime a repo that is not registered yet:

```text
workspace(operation="open", path="<absolute-root>")
```

`workspace open` primes the index; it does not switch the live MCP server workspace.

## Read Another Workspace

Pass the selector to the read tool:

```text
search(workspace_id="<selector>", query="<query>")
inspect(workspace_id="<selector>", target="<symbol-or-file>")
context(workspace_id="<selector>", query="<task>")
impact(workspace_id="<selector>", target="<symbol-or-file>")
trace(workspace_id="<selector>", target="<symbol>")
```

Explicit `workspace_id` serves the pinned index immediately and refreshes in the background, so the read is fast and the NEXT call sees fresher data. Such a read says so: `freshness: refresh_pending` plus the served `revision`. Set `ensure_fresh=true` when you must wait for a current index; set `ensure_fresh=false` for zero refresh work.

## Report

Mention:

- Workspace selector used
- Whether the index was refreshed or best-effort stale
- Any first-read latency caveat on large workspaces
- The concrete repo path if the selector was ambiguous

Do not manually `cd` and repeat shell searches when a Miller read tool can route to the workspace.
