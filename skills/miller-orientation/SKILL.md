---
name: miller-orientation
description: Use when starting a Miller task, choosing a Miller tool, or choosing a Miller search mode.
user-invocable: true
arguments: "<what you want to find or do>"
allowed-tools: mcp__miller__search, mcp__miller__inspect, mcp__miller__context, mcp__miller__trace, mcp__miller__impact, mcp__miller__workspace, mcp__miller__patterns, mcp__miller__content, mcp__miller__edit
---

# Miller Orientation

Pick the right Miller tool and mode on the first call instead of guessing or falling back to Grep/Read. One call from the table below answers most tasks.

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

## Freshness first

If you are unsure whether the index is current, start here:

```text
workspace(workspace_id="<id>", operation="status")
workspace(workspace_id="<id>", operation="health")
```

Refresh only if stale or `health` reports a missing/corrupt sidecar:

```text
workspace(workspace_id="<id>", operation="refresh")
```

## Intent to first call

| If the intent is | First call |
|---|---|
| Find a symbol/identifier | `search(query="...")` (mode=auto) |
| Find docs/config prose | `search(query="...", mode="content")` |
| Find source-body text | `search(query="...", mode="source")` |
| Find text in comments/strings | `search(query="...", regions="comment,doc_comment,string_literal")` |
| Audit TODO/FIXME/HACK/XXX/RAZORBACK | `search(query="TODO,FIXME,HACK,XXX,RAZORBACK", mode="markers")` |
| Read a file you can name | `inspect(target="<file>")` |
| Understand a symbol first | `inspect(target="<symbol>", depth="overview")` |
| Need complete body or complete relations | `inspect(target="<symbol>", depth="full")` |
| Orient on an unfamiliar area/task | `context(query="<task>")` |
| Who calls / what does this reach | `trace(target="<symbol>")` |
| How does A reach B | `trace(target="A", mode="path", to="B")` |
| Where is this name referenced | `trace(target="<symbol>", mode="refs")` |
| Scope a refactor / choose tests | `impact(target="...")` or `impact(git=true)` |
| Make a localized existing-file text edit | `edit(operation="replace_text", target="<file>", old_text="<known-old>", new_text="<new>", match_mode="auto", query="<nearby text>")` |
| Prepare a handoff to another harness/model | Use the `handoff-out` skill |
| Resume from a handoff packet | Use the `handoff-in` skill |
| List known code shapes | `patterns(operation="list")`, then follow its `Next:` actions or search a shown `pattern_id` |
| Read a large log/report | `content(operation="import", path=...)` -> `content(operation="search", query="...")` -> bounded `content(operation="read", source_id="...", line=...)` |
| Work in another registered repo | `workspace(operation="list")` then pass `workspace_id="<selector>"` |

## Gotchas

- Natural-language `search` hides tests by default; set `exclude_tests=false` when tests are expected.
- Omitted `inspect` depth is `summary`; use `depth=overview` for the first symbol read, then `depth=full` only when you need the complete body or complete relation lists.
- `workspace_id=all` is for `content(operation="search")` text audits only, not symbol/code read tools.
- `trace` empty results include `Next:` / JSON `next_actions`; follow them before treating a missing path or ref as proof.
- `content(operation="read")` should use the `source_id` from `content(operation="search")` or `content(operation="list")`; pass the hit's `workspace_id` for cross-workspace reads.
- `patterns` list and no-match results include `Next:` / JSON `next_actions`; run `patterns(operation="list")` before raw route/HTML/JSON/YAML/Markdown greps.
- `trace mode=bridge` is provider-scoped to `dotnet-web`, `nextjs`, `nextjs-api`, `nuxt`, `nuxt-api`, `vue`, `react`, and `backend-http`; on another stack use `mode=refs`/`path`, and use `inspect depth=full` for callers and callees.
- Symbol search ranks `name + signature` only; docs/literals/broad source text need the row above.
- Use `edit` for localized text changes when `query`, `anchor`, or `line` can avoid a full-file read; use normal patching for broad multi-hunk edits.

## Report

Keep it compact:

- Tool chosen and why
- The exact call issued
- One-line next step (deeper `inspect`, a follow-up `trace`, `impact` before a change, or `edit` preview for a localized change)

Do not reach for Grep/Read/find when a row above fits. Do not read a whole file before `inspect`.
