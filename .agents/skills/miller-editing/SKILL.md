---
name: miller-editing
description: Use before changing existing indexed files with Miller edit, especially symbol rewrites, text replacements, renames, or refactors.
allowed-tools: mcp__miller__edit, mcp__miller__inspect, mcp__miller__impact, mcp__miller__search, mcp__miller__workspace
---

# Miller Editing

Use Miller's index-aware `edit` tool for existing indexed files. It previews a diff by default and blocks stale targets unless you explicitly allow stale edits.

## When To Use

- Existing file text replacements
- Symbol body or signature rewrites
- Insert before or after a symbol
- Add docs to a symbol
- Workspace-wide symbol renames

Creating a brand-new file is outside this skill; use the normal file creation path.

## Workflow

1. Resolve the target:

```text
search(query="<symbol or file>")
inspect(target="<symbol-or-file>")
```

2. For refactors or public/shared symbols, run impact first:

```text
impact(target="<symbol-or-file>")
```

3. Preview the edit. Dry run is the default:

```text
edit(operation="replace_text", target="<file>", old_text="<old>", new_text="<new>")
edit(operation="replace_symbol_body", target="<symbol>", new_text="<body>")
edit(operation="rename_symbol", target="<symbol>", new_text="<new-name>")
```

4. Apply only after reviewing the preview:

```text
edit(operation="...", target="...", new_text="...", apply=true)
```

5. If Miller reports a stale target, run:

```text
workspace(operation="refresh")
```

Use `allow_stale=true` only when the user explicitly accepts the risk or the edit is purely mechanical and the current disk state was independently checked.

## Rules

- Prefer `replace_symbol_body` or `replace_symbol_signature` for code symbols.
- Prefer `rename_symbol` for symbol renames.
- Prefer `replace_text` for docs, config, and arbitrary text.
- Keep the preview in the reasoning loop; do not apply blind edits.
- After applying, run the tests or checks suggested by `impact`.
