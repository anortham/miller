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

- `search` — Find code by name, identifier, or natural-language phrase. `mode=auto|text|symbol|file|content|source|external|web|all-text`. Test
  code is auto-hidden for natural-language queries (`exclude_tests=false` to force them in). The first move for
  "where is…?". Use `mode=content` (alias `docs`) to search docs/prose file CONTENT instead of symbols — it
  returns `path:line` + a snippet window, for files symbol search can't see (markdown, config, plain text).
  Use `mode=source` to search verified source-file body text for error strings, route literals, assertion text,
  and implementation phrases without blending those hits into default symbol search. Use `mode=external` for
  imported logs/reports, `mode=web` for imported web markdown, and `mode=all-text` only when an explicit
  one-workspace union is useful.
  Use `file_pattern=<glob>` and `language=<lang>` to keep result sets small when you know the area or language.
  The default page is 6 results; each compact symbol hit includes name, kind, file, line, and signature when
  available. Raise `limit` for a wider page.
  Use `regions=comment|doc_comment|string_literal` (comma lists accepted; `docstring` aliases `doc_comment`) to
  search only inside comments, doc-comments, or string literals. Region search requires `MILLER_REGION_INDEX=1`
  and a refreshed `search.db` sidecar; set `MILLER_REGION_MAX_BYTES=<n>` before refresh to tune the per-region cap.
  If unavailable, region search fails closed instead of returning symbol results.
  Symbol hits may include `has_doc` when the symbol has `symbols.doc_comment`. Optional `workspace_id` accepts a
  display ID, unique prefix, full ID, registered root path, `current`, or `primary`; explicit `workspace_id` defaults
  `ensure_fresh=true`.
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
  Pass `scope=<file>` to disambiguate duplicate symbol names before falling back to symbol IDs. Optional
  `workspace_id` and `ensure_fresh` work for cross-workspace traces. **`mode=bridge` is provider-scoped, not a
  general all-language feature: it currently covers the `dotnet-web` stack (ASP.NET controllers ↔
  TypeScript/JS client URL calls ↔ AutoMapper ↔ Entity Framework). On another stack, do not expect
  cross-language bridge results — use `mode=auto`/`mode=path` instead.**
- `impact` — What a change would affect: downstream symbols and linked tests. Pass exactly one of `target` (a
  symbol or file), `changed_paths` (a set of files), or `diff` (a unified diff). Use before refactoring or to
  pick which tests to run. Optional `workspace_id` and `ensure_fresh` work for registered workspaces.
- `edit` — Index-aware edits. Operations: `replace_text`, `replace_symbol_body`, `replace_symbol_signature`,
  `rename_symbol`, `insert_before`, `insert_after`, `add_doc`. Previews a diff and writes nothing unless
  `apply=true`. Blocked when the index is stale for the target file — `workspace refresh` first (or
  `allow_stale=true` if you accept the risk).
- `content` — Import, search, read, list, remove, and export external/web text in Miller's content corpus. Use it for
  logs, CI output, generated reports, large JSON/text dumps, browser-fetched markdown, and other non-workspace
  text that would waste context if read in full. `import` and `add_markdown` report metadata only; `search`
  returns snippets; `read` returns a bounded line window by `source_id` and `line`; `remove` deletes an imported
  source. Use `content_kind=web` for web-only `search`/`list`, or `workspace_id=all` on `search` to audit all
  registered workspaces with workspace/display IDs on every hit. `export` writes deterministic JSONL chunks for
  Eros/local integration; it includes raw chunk text, so do not use it as an interactive reading shortcut.
- `workspace` — Index lifecycle. `status` (default), `refresh` (reconcile stale files), `full` (rebuild from
  scratch), `list`, `open` (prime a different path's index), `remove`, `dashboard` (start/reuse the local
  loopback dashboard). `status`, `refresh`, `full`, and `remove` accept `workspace_id` or `path`; `list` shows
  the central registry.

## Workflows

- **New task / unfamiliar area**: `context` → `inspect` the key symbols → implement.
- **Understand a symbol**: `inspect target depth=full` (definition + refs + callers/callees + body in one call).
- **Trace a flow**: `trace mode=auto` to fan out, `mode=path` for a specific A→B chain, `mode=bridge` to cross a
  language boundary (provider-scoped to the `dotnet-web` stack for now; on another stack stay with
  `mode=auto`/`mode=path`). If a name is ambiguous, retry with `scope=<file>`.
- **Find something in docs/prose**: `search mode=content "<phrase>"` — searches markdown/config/text content and
  returns `path:line` + snippet, where symbol search would find nothing.
- **Find source-body text**: `search mode=source "<literal or phrase>"` — searches verified source files and
  returns `path:line`, content kind, containing symbol when known, and a snippet.
- **Audit registered workspaces for exact text**: `content search query="dangerous term" workspace_id=all content_kind=source`
  or `content_kind=docs|config|external_file|web`, then bounded `content read`.
- **Inspect a large log/report**: `content import path=/tmp/build.log` → `content search query="error text"` →
  `content read source_id=... line=... context_lines=10`. Do not read or paste the full file.
- **Research a web page**: use the `miller-web-research` skill to fetch markdown with `browser39` into a temp
  file, then `content add_markdown path=/tmp/page.md url=https://... display_path="title"` →
  `content search query="phrase" content_kind=web` → bounded `content read`. Do not create repo docs for pages.
- **Scope noisy search**: add `file_pattern=src/ui/**` or `language=typescript` when you know the likely area.
- **Find text only inside comments or strings**: `search "<phrase>" regions=comment` or
  `search "<phrase>" regions=string_literal` — requires `MILLER_REGION_INDEX=1` and a refreshed workspace.
  Use `MILLER_REGION_MAX_BYTES=<n>` to tune oversized region indexing.
- **Scope a change**: `impact target=…` → run the tests it lists → `edit` (preview) → `edit apply=true` →
  re-run `impact` if the surface changed.
- **Edit a symbol**: `inspect` it → `edit …` (preview, the default) → `edit … apply=true`.
- **Index looks stale**: `workspace refresh` (or `workspace full` to force a clean rebuild).
- **Need another repo**: `workspace list` → pass the displayed ID, a unique prefix, or the root path as
  `workspace_id` to `search`/`inspect`/`context`/`impact`/`trace`. Use `ensure_fresh=false` only when a fast best-effort stale
  read is acceptable.

## Subagent Dispatching

Subagents may not receive Miller's server instructions. When dispatching subagents that will explore or modify
code, paste this block into the prompt:

    ## Code Intelligence Tools (use instead of Grep/Glob/Read)
    You have Miller MCP tools. Use them before raw shell/file exploration:
    - context(query, ...) for unfamiliar task-shaped orientation.
    - search(query, mode?, regions?, file_pattern?, language?) before rg/grep/find, including mode=content for
      docs/prose, mode=source/external/web/all-text for content corpus text, regions=... for comments/strings, and scoped filters for smaller result sets.
    - inspect(target, depth?) before reading a whole file or symbol body; use depth=full for refs/callers/callees/body.
    - trace(target, mode?, to?, scope?) before manual caller/callee file hopping; use scope for ambiguous names.
    - impact(target?|changed_paths?|diff?) before refactors and to choose tests.
    - edit(operation, target, ...) to preview index-aware edits (apply=true to commit).
    - content(import|add_markdown|search|read|list|remove|export, ...) for large logs, CI output, web markdown, external text, and workspace audits; use workspace_id=all for registered-workspace text audits and read bounded windows only.
    - workspace(status|refresh|full|list|open|remove|dashboard) to refresh stale indexes, open another repo, or
      start the local dashboard from the session.
    Do NOT fall back to Glob/Read/Grep chains when a Miller tool fits. Miller returns targeted context in 1-2 calls.

Do not use `grep`/`find`/`rg` when a Miller tool fits. Do not read a whole file before `inspect`. Do not chain
several lookups when one `inspect depth=full` or `context` call answers the question.
