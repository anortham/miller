---
name: miller-large-file
description: Use when an agent needs to inspect, search, or quote a large text file such as a log, CI output, JSON dump, generated report, or other non-workspace text without reading the whole file into context.
---

# Miller Large File Workflow

Use Miller's `content` tool instead of `cat`, full-file reads, or broad shell output when a text file may be large enough to waste context.

## Workspace targeting (required)

Every workspace-bound Miller MCP call names its target with `workspace_id`; Miller never infers it from the
launch directory, environment variables, MCP Roots, or a previous call. Get the ID from
`workspace(operation="list")`, or from `workspace(operation="open", path="/absolute/project")` when the repo is
absent. The examples below write it as `workspace_id="<id>"`. Only `workspace` `list`, `open`, `remove`,
`prune`, and `dashboard` run without one; `current` and `primary` are CLI-only. The full targeting rules live
in the `miller-orientation` skill.

## Workflow

1. Import the file:
   - MCP: `content(workspace_id="<id>", operation="import", path="/absolute/path/to/file")`
   - CLI: `miller content import /absolute/path/to/file`
2. Search it:
   - MCP: `content(workspace_id="<id>", operation="search", query="error text")`
   - CLI: `miller content search "error text"`
3. Read only bounded windows:
   - MCP: `content(workspace_id="<id>", operation="read", source_id="...", line=120, context_lines=10)`
   - CLI: `miller content read --source-id ... --line 120 --context-lines 10`
4. List or remove imported sources when needed:
   - MCP: `content(workspace_id="<id>", operation="list")`
   - MCP: `content(workspace_id="<id>", operation="remove", source_id="...")`

## Rules

- Do not print or paste the full file content.
- Keep `context_lines` small and increase it only when the first bounded window is insufficient.
- Use `max_bytes` only when you intentionally want to import a file over the default cap.
- Prefer searching first, then reading the smallest line window that explains the hit.
- Remove temporary imports when they are no longer useful.
