---
name: miller-editing
description: Use before changing existing indexed files with Miller edit, especially symbol rewrites, text replacements, renames, or refactors.
allowed-tools: mcp__miller__edit, mcp__miller__inspect, mcp__miller__impact, mcp__miller__search, mcp__miller__workspace
---

# Miller Editing

Use Miller's index-aware `edit` tool for existing indexed files. It previews a diff by default, blocks stale
targets unless you explicitly allow stale edits, and can make localized text edits without reading a whole file
when you provide a small selector.

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
search(query="<symbol or file>")
inspect(target="<symbol-or-file>")
```

For symbols, start with `inspect(target="<symbol>", depth="overview")` to choose the edit target and understand
nearby refs/calls without dumping the full body. Use `inspect(target="<symbol>", depth="full")` before rewriting a
body or auditing complete relation lists.

For broad multi-hunk edits or when you need to handcraft a large replacement, use the normal patch/edit path. For
small file-level text edits where you know the old value and a nearby selector, use `replace_text` directly and let
Miller prove the match in preview:

```text
edit(operation="replace_text", target="<file>", old_text="<known-old>", new_text="<new>", match_mode="auto", query="<nearby text>")
```

2. For refactors or public/shared symbols, run impact first:

```text
impact(target="<symbol-or-file>")
```

3. Preview the edit. Dry run is the default:

```text
edit(operation="replace_text", target="<file>", old_text="<old>", new_text="<new>")
edit(operation="replace_text", target="<file>", old_text="<old>", new_text="<new>", match_mode="auto", line=42)
edit(operation="replace_text", target="<file>", old_text="<old>", new_text="<new>", match_mode="auto", anchor="<nearby text>")
edit(operation="replace_symbol_body", target="<symbol>", new_text="<body>")
edit(operation="rename_symbol", target="<symbol>", new_text="<new-name>")
```

The preview should show match mode, match source, line range, occurrence, disk verification, and a concise diff.
If it is the intended edit, re-run the same call with `apply=true`.

4. Apply only after reviewing the preview:

```text
edit(operation="...", target="...", new_text="...", apply=true)
```

5. If Miller reports a stale target, run:

```text
workspace(operation="refresh")
```

Use `allow_stale=true` only when the user explicitly accepts the risk or the edit is purely mechanical and the current disk state was independently checked.

## Match Recovery

`replace_text` still requires a known `old_text`, but `match_mode="auto"` can accept exact, normalized, or bounded
fuzzy matches after verifying the span against current disk text. If preview is ambiguous or text is not found:

1. Add `line=<line>` when you know the target line.
2. Add `anchor="<nearby text>"` when the old text appears in multiple places.
3. Add `query="<nearby text>"` to use indexed content as a candidate finder.
4. Use `match_mode="exact"` when whitespace-tolerant or fuzzy matching would be unsafe.
5. Use `occurrence="all"` only when every match should change.

## Rules

- Prefer `replace_symbol_body` or `replace_symbol_signature` for code symbols.
- Prefer `rename_symbol` for symbol renames.
- Prefer `replace_text` for docs, config, and arbitrary text.
- Keep the preview in the reasoning loop; do not apply blind edits.
- Do not use raw selector text in telemetry or reports; describe selector shape instead.
- After applying, run the tests or checks suggested by `impact`.
