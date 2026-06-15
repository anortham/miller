# Miller — Code Intelligence Server

Miller serves an always-fresh index of this workspace's code. Reach for a Miller tool before a raw shell
`rg`/`grep`/`find` or reading a whole file: one call returns ranked, structured results with fewer tokens.

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
   If the index is stale or `inspect <file>` misses expected symbols, run `workspace refresh`, then refresh and retry before raw reads.

## Tools

- `search` — Find code by name, identifier, or phrase. `mode=auto|text|symbol|file|content|source|external|web|all-text`.
  Natural-language queries auto-hide tests (`exclude_tests=false` to include them). Use `mode=content`, alias `docs`,
  for docs/config; `mode=source` for source-body text; `mode=external|web|all-text` for imported/broad text. Scope with
  `file_pattern`, `language`, and `limit`. Use `regions=comment|doc_comment|string_literal` for comments/strings; it
  requires `MILLER_REGION_INDEX=1` and a refreshed `search.db`. Symbol hits may include `has_doc`. Optional
  `workspace_id` accepts display ID, unique prefix, full ID, root path, `current`, or `primary`; explicit
  `workspace_id` defaults `ensure_fresh=true`.
- `inspect` — A file or symbol you can already name. A file path lists symbols; a symbol name gives definition,
  signature, and docs. `depth=full` adds refs, callers/callees, body, and recorded complexity facts. Use before
  reading an entire file. Optional `workspace_id` and `ensure_fresh` follow `search`.
- `context` — A token-budgeted bundle for a task/question. Give the task plus optional `failing_test` or
  `stack_trace`; get bounded, provenance-tagged symbols. Use for UNFAMILIAR areas; if you know the symbol, use
  `inspect`. `reference_mode=usage` adds confidence-labeled definitions, possible name-based refs, call IDs, and
  source chunks; treat `confidence=name_based` as possible, not exact. `exclude_tests=true` filters tests only in
  usage mode. Optional `workspace_id` and `ensure_fresh` route through the registry provider.
- `trace` — Follow code. `mode=auto` (callers/callees), `mode=path` (shortest path), `mode=bridge` (TS call →
  endpoint → DTO → entity → table). Reduced-confidence links are flagged `[verb-unknown]`/`[ambiguous]`. Use
  `format=json` for structured nodes/links/diagnostics. Use `scope=<file>` to disambiguate duplicate names. Optional
  `workspace_id` and `ensure_fresh` work cross-workspace. **`mode=bridge` is provider-scoped to `dotnet-web`; on
  another stack use `mode=auto`/`mode=path`.**
- `impact` — What a change affects: downstream symbols and linked tests. Pass exactly one of `target`,
  `changed_paths`, or `diff`. Use before refactoring or choosing tests. Optional `workspace_id` and `ensure_fresh`
  work for registered workspaces.
- `edit` — Index-aware edits: `replace_text`, `replace_symbol_body`, `replace_symbol_signature`, `rename_symbol`,
  `insert_before`, `insert_after`, `add_doc`. Previews a diff unless `apply=true`. A stale target self-heals by
  converging that file; if it fails, run `workspace refresh` first or pass `allow_stale=true` if you accept risk.
- `content` — Import, search, read, list, remove, and export external/web text. Use for logs, CI output, reports,
  large dumps, and browser-fetched markdown. `import`/`add_markdown` report metadata; `search` returns snippets;
  `read` returns bounded windows. Use `content_kind=web` for web-only reads, or `workspace_id=all` on `search` for
  audits. `export` writes raw JSONL for integration, not interactive reading.
- `patterns` — List, summarize, and search extractor-recognized code shapes from `structural_facts` across many
  languages: framework facts, language facts, SQL DDL, and JSON/YAML/TOML/Markdown structure. Run `patterns()` first
  to see emitted ids. This is not raw AST query execution. Use `operation=list|summary|search`, `pattern_id`,
  `where=key=value`, `path`, and `language`. Optional `workspace_id` and `ensure_fresh` work for registered workspaces.
- `workspace` — Index lifecycle: `status`, `health`, `refresh`, `full`, `list`, `open`, `remove`, `dashboard`
  (start/reuse the loopback dashboard). `status`, `health`, `refresh`, `full`, and `remove` accept `workspace_id`
  or `path`; `list` shows the registry.

## Workflows

- **New task / unfamiliar area**: `context` → `inspect` the key symbols → implement.
- **Understand a symbol**: `inspect target depth=full` (definition + refs + callers/callees + body in one call).
- **Trace a flow**: `trace mode=auto` to fan out, `mode=path` for A→B, `mode=bridge` for `dotnet-web`
  cross-language chains. If a name is ambiguous, retry with `scope=<file>`.
- **Find something in docs/prose**: `search mode=content "<phrase>"` — searches markdown/config/text content and
  returns `path:line` + snippet, where symbol search would find nothing.
- **Find source-body text**: `search mode=source "<literal or phrase>"` — searches verified source files and
  returns `path:line`, kind, containing symbol when known, and snippet.
- **Audit registered workspaces for exact text**: `content search query="dangerous term" workspace_id=all content_kind=source`
  or `content_kind=docs|config|external_file|web`, then bounded `content read`.
- **Find known code shapes**: `patterns operation=list` to discover ids, then
  `patterns operation=search pattern_id=<id> where=attribute_name=hx-get path=Views/**`.
- **Inspect a large log/report**: `content import path=/tmp/build.log` → `content search query="error text"` →
  `content read source_id=... line=... context_lines=10`. Do not read or paste the full file.
- **Research a web page**: use `miller-web-research` to fetch markdown with `browser39` into a temp file, then
  `content add_markdown path=/tmp/page.md url=https://... display_path="title"` →
  `content search query="phrase" content_kind=web` → bounded `content read`. Do not create repo docs for pages.
- **Scope noisy search**: add `file_pattern=src/ui/**` or `language=typescript` when you know the likely area.
- **Find text only inside comments or strings**: `search "<phrase>" regions=comment` or `regions=string_literal` —
  requires `MILLER_REGION_INDEX=1` and a refreshed workspace.
- **Dashboard**: If the user asks to start, open, or show the Miller dashboard, call `workspace` with `operation=dashboard`. A dashboard request is a tool operation, not a file-finding task. Do not search plugin cache directories for dashboard files.
- **Scope a change**: `impact target=…` → run the tests it lists → `edit` (preview) → `edit apply=true` →
  re-run `impact` if the surface changed.
- **Edit a symbol**: `inspect` it → `edit …` (preview, the default) → `edit … apply=true`.
- **Index looks stale**: `workspace refresh` (or `workspace full` to force a clean rebuild).
- **Check index trust/readiness**: `workspace health` — reports stale/missing sidecars, parse diagnostics,
  capability gaps, skipped content, and recent telemetry outcomes without hydrating the full graph.
- **Need another repo**: `workspace list`; if registered, pass display ID, unique prefix, full ID, or root path as
  `workspace_id` to code tools. If absent, run `workspace operation=open path=/absolute/repo`, then retry.
  `workspace_id=all` is only for `content search` text audits, not code read tools.

## Subagent Dispatching

Subagents may not receive Miller's server instructions. When dispatching subagents that will explore or modify
code, paste this block into the prompt:

    ## Code Intelligence Tools (use instead of Grep/Glob/Read)
    You have Miller MCP tools. Use them before raw shell/file exploration:
    - context(query, ...) for unfamiliar task-shaped orientation.
    - search(query, mode?, regions?, file_pattern?, language?) before rg/grep/find; use mode=content for docs,
      mode=source/external/web/all-text for content text, regions=... for comments/strings, and filters to scope.
    - inspect(target, depth?) before reading files/symbols; depth=full adds refs/callers/callees/body.
    - trace(target, mode?, to?, scope?) before manual caller/callee file hopping; use scope for ambiguous names.
    - impact(target?|changed_paths?|diff?) before refactors and to choose tests.
    - edit(operation, target, ...) to preview index-aware edits (apply=true to commit).
    - content(import|add_markdown|search|read|list|remove|export, ...) for logs, CI, web markdown, external text, and audits; use workspace_id=all for registered-workspace text audits and bounded reads only.
    - patterns(operation?, pattern_id?, where?, path?, language?) for extractor-recognized code-shape facts.
    - workspace(status|health|refresh|full|list|open|remove|dashboard) for readiness, refresh, other repos, or dashboard with operation=dashboard.
    Do NOT fall back to Glob/Read/Grep chains when a Miller tool fits. Miller returns targeted context in 1-2 calls.

Do not use `grep`/`find`/`rg` when a Miller tool fits. Do not read a whole file before `inspect`. Do not chain
several lookups when one `inspect depth=full` or `context` call answers the question.
