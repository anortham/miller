# Miller — Code Intelligence Server

Miller serves a pre-built, always-fresh index of this workspace's code. Reach for a Miller tool before a raw
shell `rg`/`grep`/`find` or reading a whole file: one call returns ranked, structured results in a fraction of
the tokens.

## Rules

1. **Search before reading.** Run `search` before `grep`/`rg`/`cat` or opening files by hand.
2. **Structure before content.** Run `inspect` to see a file's symbols or a symbol's signature before reading
   the whole file — it is far cheaper than a full read.
3. **Impact before changing.** Run `impact` before a refactor to find the downstream symbols AND the tests to
   run. Prefer it over grepping for usages.
4. **Trace to follow a thread.** Use `trace` for "who calls this?", "how does A reach B?", or a cross-language
   call chain — not manual file hopping.
5. **Edit with a preview.** `edit` previews a diff and writes nothing by default; set `apply=true` only after
   the dry-run looks right.
6. **Trust the index.** Results are extracted and kept fresh — do NOT re-verify them with `grep`/`find`/`Read`.
   If the index is stale for a file, run `workspace refresh` rather than working around it.

## Tools

- `search` — Find code by name, identifier, or natural-language phrase. `mode=auto|text|symbol|file|content`. Test
  code is auto-hidden for natural-language queries (`exclude_tests=false` to force them in). The first move for
  "where is…?". Use `mode=content` (alias `docs`) to search docs/prose file CONTENT instead of symbols — it
  returns `path:line` + a snippet window, for files symbol search can't see (markdown, config, plain text).
  Optional `workspace_id` accepts a display ID, unique prefix, full ID, `current`, or `primary`; explicit
  `workspace_id` defaults `ensure_fresh=true`.
- `inspect` — A file or symbol you can already name. A file path lists its symbols; a symbol name gives its
  definition, signature, and docs. `depth=full` adds references, callers/callees, and the body. Use before
  reading an entire file. Optional `workspace_id` and `ensure_fresh` follow the same rules as `search`.
- `context` — A token-budgeted bundle of the most relevant code for a task or question. Give a description of
  what you're working on (optionally a `failing_test` or `stack_trace`) and get a bounded, provenance-tagged set
  of symbols. Use for orientation in an UNFAMILIAR area; if you already know the symbol, use `inspect`.
  Optional `workspace_id` and `ensure_fresh` route the bundle through the registry-backed provider.
- `trace` — Follow a thread of code. `mode=auto` (callers + callees), `mode=path` (shortest path from `target`
  to `to`), `mode=bridge` (cross-language chain: TS call → endpoint → DTO → entity → table). Reduced-confidence
  links are flagged `[verb-unknown]`/`[ambiguous]` — never trust an unflagged link less than a flagged one.
  Optional `workspace_id` and `ensure_fresh` work for cross-workspace traces.
- `impact` — What a change would affect: downstream symbols and linked tests. Pass exactly one of `target` (a
  symbol or file), `changed_paths` (a set of files), or `diff` (a unified diff). Use before refactoring or to
  pick which tests to run. Optional `workspace_id` and `ensure_fresh` work for registered workspaces.
- `edit` — Index-aware edits. Operations: `replace_text`, `replace_symbol_body`, `replace_symbol_signature`,
  `rename_symbol`, `insert_before`, `insert_after`, `add_doc`. Previews a diff and writes nothing unless
  `apply=true`. Blocked when the index is stale for the target file — `workspace refresh` first (or
  `allow_stale=true` if you accept the risk).
- `workspace` — Index lifecycle. `status` (default), `refresh` (reconcile stale files), `full` (rebuild from
  scratch), `list`, `open` (prime a different path's index), `remove`. `status`, `refresh`, `full`, and
  `remove` accept `workspace_id` or `path`; `list` shows the central registry.

## Workflows

- **New task / unfamiliar area**: `context` → `inspect` the key symbols → implement.
- **Understand a symbol**: `inspect target depth=full` (definition + refs + callers/callees + body in one call).
- **Trace a flow**: `trace mode=auto` to fan out, `mode=path` for a specific A→B chain, `mode=bridge` to cross a
  language boundary.
- **Find something in docs/prose**: `search mode=content "<phrase>"` — searches markdown/config/text content and
  returns `path:line` + snippet, where symbol search would find nothing.
- **Scope a change**: `impact target=…` → run the tests it lists → `edit` (dry-run) → `edit apply=true` →
  re-run `impact` if the surface changed.
- **Edit a symbol**: `inspect` it → `edit … dry_run` (the default) → `edit … apply=true`.
- **Index looks stale**: `workspace refresh` (or `workspace full` to force a clean rebuild).
- **Need another repo**: `workspace list` → pass the displayed ID (or a unique prefix) as `workspace_id` to
  `search`/`inspect`/`context`/`impact`/`trace`. Use `ensure_fresh=false` only when a fast best-effort stale
  read is acceptable.

Do not use `grep`/`find`/`rg` when a Miller tool fits. Do not read a whole file before `inspect`. Do not chain
several lookups when one `inspect depth=full` or `context` call answers the question.
