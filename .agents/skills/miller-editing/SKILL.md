---
name: miller-editing
description: Use before changing existing indexed files with Miller edit, especially symbol rewrites, text replacements, renames, or refactors.
allowed-tools: mcp__miller__edit, mcp__miller__inspect, mcp__miller__impact, mcp__miller__search, mcp__miller__workspace, mcp__miller__tests
---

# Miller Editing

Use Miller's index-aware `edit` tool for existing indexed files. It previews a diff by default and blocks stale
indexed spans. Only `replace_text` can explicitly allow a stale index because it proves its match against current
disk text.

## Workspace targeting (required)

Every workspace-bound Miller MCP call names its target with `workspace_id`; Miller never infers it from the
launch directory, environment variables, MCP Roots, or a previous call. Get the ID from
`workspace(operation="list")`, or from `workspace(operation="open", path="/absolute/project")` when the repo is
absent. The examples below write it as `workspace_id="<id>"`. Only `workspace` `list`, `open`, `remove`,
`prune`, and `dashboard` run without one; `current` and `primary` are CLI-only. The full targeting rules live
in the `miller-orientation` skill.

## When To Use

- Localized existing-file text replacements, especially when a full-file Read would be wasteful
- Symbol body or signature rewrites
- Insert before or after a symbol
- Add docs to a symbol
- Workspace-wide symbol renames

Creating a brand-new file is outside this skill; use the normal file creation path.

## Workflow

1. Resolve the target:

```text
search(workspace_id="<id>", query="<symbol or file>")
inspect(workspace_id="<id>", target="<symbol-or-file>")
```

For symbols, start with `inspect(target="<symbol>", depth="overview")` to choose the edit target and understand
nearby refs/calls without dumping the full body. Use `inspect(target="<symbol>", depth="full")` before rewriting a
body or auditing complete relation lists.

For broad multi-hunk edits or when you need to handcraft a large replacement, use the normal patch/edit path. For
small file-level text edits where you know the old value and a nearby selector, use `replace_text` directly and let
Miller prove the match in preview:

```text
edit(workspace_id="<id>", operation="replace_text", target="<file>", old_text="<known-old>", new_text="<new>", match_mode="auto", query="<nearby text>")
```

2. For refactors or public/shared symbols, run impact first:

```text
impact(workspace_id="<id>", target="<symbol-or-file>")
```

3. Preview the edit. Dry run is the default:

```text
edit(workspace_id="<id>", operation="replace_text", target="<file>", old_text="<old>", new_text="<new>")
edit(workspace_id="<id>", operation="replace_text", target="<file>", old_text="<old>", new_text="<new>", match_mode="auto", line=42)
edit(workspace_id="<id>", operation="replace_text", target="<file>", old_text="<old>", new_text="<new>", match_mode="auto", anchor="<nearby text>")
edit(workspace_id="<id>", operation="replace_symbol_body", target="<symbol>", new_text="<body>")
edit(workspace_id="<id>", operation="rename_symbol", target="<symbol>", new_text="<new-name>")
```

The preview should show match mode, match source, line range, occurrence, disk verification, and a concise diff.
If it is the intended edit, re-run the same call with `apply=true`.

4. Apply only after reviewing the preview:

```text
edit(workspace_id="<id>", operation="...", target="...", new_text="...", apply=true)
```

5. If Miller reports a stale target, run:

```text
workspace(workspace_id="<id>", operation="refresh")
```

Use `allow_stale=true` only for `replace_text` after independently checking current disk state. Symbol-body,
signature, insert, doc, and rename operations require fresh indexed spans even when the user accepts the risk.

## Match Recovery

`replace_text` still requires a known `old_text`, but `match_mode="auto"` can accept exact, normalized, or bounded
fuzzy matches after verifying the span against current disk text. If preview is ambiguous or text is not found:

1. Add `line=<line>` when you know the target line.
2. Add `anchor="<nearby text>"` when the old text appears in multiple places.
3. Add `query="<nearby text>"` to use indexed content as a candidate finder.
4. Use `match_mode="exact"` when whitespace-tolerant or fuzzy matching would be unsafe.
5. Use `occurrence="all"` only when every whole-file match should change; do not combine it with `line`, `anchor`,
   or `query`.

## Rules

- Prefer `replace_symbol_body` or `replace_symbol_signature` for code symbols.
- Prefer `rename_symbol` for symbol renames.
- Prefer `replace_text` for docs, config, and arbitrary text.
- Keep the preview in the reasoning loop; do not apply blind edits.
- Do not use raw selector text in telemetry or reports; describe selector shape instead.
- After applying, check `tests(operation="status")`: when CT is enabled it lists the cases the edit staled, and
  `tests(operation="run", wait=true)` executes only those. When CT is off, run the tests `impact` suggested with
  the project's test runner.
