# Miller — Code Intelligence Server

Miller serves a fresh index of this workspace's code. Reach for a Miller tool before a raw shell read: one call
returns ranked, structured results with fewer tokens.

## Rules

1. **Search before reading.** Run `search` before `grep`/`rg`/`cat`.
2. **Structure before content.** Run `inspect` to see a file's symbols or a symbol's signature before reading
   the whole file — it is far cheaper than a full read.
3. **Impact before changing.** Run `impact` before a refactor to find downstream symbols and tests.
4. **Trace to follow a thread.** Use `trace` for "where is this referenced?", "who calls this?", "how does A reach B?", or a cross-language
   call chain — not manual file hopping.
5. **Edit with a preview.** `edit` previews a diff; set `apply=true` only after the dry-run looks right.
6. **Trust the index.** Do NOT re-verify Miller results with `grep`/`find`/`Read`.
   If stale, run `workspace refresh`, then refresh and retry before raw reads.

## Tools

- `search` — Find code by name, identifier, phrase, or marker audit. `mode=auto|text|symbol|file|markers|content|source|external|web|all-text`.
  Natural-language queries auto-hide tests (`exclude_tests=false` to include them). Use `mode=content`, alias `docs`,
  for docs/config; `mode=source` for source-body text; `mode=external|web|all-text` for imported/broad text. Scope with
  `file_pattern`, `language`, and `limit`. Use `regions=comment|doc_comment|string_literal`; `MILLER_REGION_INDEX=0`
  opts out. Use `mode=markers` with `query=TODO,FIXME,HACK,XXX`. Symbol hits may include `has_doc`. Optional
  `workspace_id` accepts display ID, unique prefix, full ID, root path, `current`, or `primary`; explicit
  `workspace_id` defaults `ensure_fresh=true`.
- `inspect` — A file or symbol you can already name. A file path lists symbols; a symbol name gives definition,
  signature, and docs. Default inspect depth is `summary`. The first symbol read should usually be `inspect target depth=overview`;
  it gives bounded refs/calls/body preview. Use `depth=full` when you need the complete body or complete relation lists.
  Multi-file ambiguity includes copyable scoped reruns.
  Optional `workspace_id` and `ensure_fresh` follow `search`.
- `context` — First call in an unfamiliar area: a small, justified bundle of relevant entry points plus the next
  symbols to `inspect`. Give the task plus optional `failing_test`/`stack_trace`; compact output: seeds first with
  reasons, neighbours, and `## next inspect`. If you know the symbol, use `inspect`.
  `reference_mode=usage` adds definitions, name-based refs, call IDs, and source chunks; treat
  `confidence=name_based` as possible. `exclude_tests=true` filters tests only in usage mode. Optional `workspace_id`
  and `ensure_fresh` route through the registry provider.
- `trace` — Follow code. `mode=refs` (name-based usages; optional
  `reference_kind=call|variable_ref|type_usage|member_access|import`; on empty, fall back to `search mode=source`),
  `mode=path` (shortest path to `to`; no path means no extracted graph path within depth, not proof unrelated),
  `mode=bridge` (`dotnet-web`, `nextjs`, `nuxt`). `mode=auto` is subsumed by `inspect depth=full`.
  Links are flagged `[verb-unknown]`/`[ambiguous]`. Use `format=json` for
  refs/nodes/links/diagnostics/actions; `scope=<file>` for duplicate names. **`mode=bridge` is provider-scoped to
  `dotnet-web`, `nextjs`, and `nuxt`; outside those providers use `mode=refs`/`mode=path`, or `inspect depth=full`.**
- `impact` — What a change affects: downstream symbols and linked tests. After edits, run `impact` with no
  args to read the working-tree git diff and see what your uncommitted change affects + which tests to run.
  Or pass exactly one of `target`, `changed_paths`, `diff`, or `git=true` (`base`/`staged` imply git). Use
  before refactoring or choosing tests. Optional `workspace_id` and `ensure_fresh` work for registered workspaces.
- `edit` — Index-aware edits: `replace_text`, `replace_symbol_body`, `replace_symbol_signature`, `rename_symbol`,
  `insert_before`, `insert_after`, `add_doc`. For localized existing-file edits, `replace_text` supports
  `match_mode=auto|exact|normalized|fuzzy` plus `query`/`anchor`/`line` selectors and match proof. Previews a diff
  unless `apply=true`; stale targets self-heal or tell you to refresh / pass `allow_stale`.
- `content` — Import/search/read/list/remove/export external/web text for logs, CI, reports, dumps, and fetched
  markdown. `search` returns snippets, `source_id`, and `workspace_id`; `read` returns ≤200-line windows. pass the hit's `source_id` and workspace_id when reading cross-workspace hits. Use `content_kind=web` for web reads, or
  `workspace_id=all` for audits. Empty content searches and failed reads include recovery guidance; JSON includes `diagnostic_code` and `next_actions`. `export` is raw JSONL.
- `patterns` — List, summarize, and search `structural_facts` code-shape facts. Run `patterns()` to see emitted ids
  (not raw AST queries). `operation=list|summary|search`; `query` is search-only. `where=key=value` ANDs when
  repeated or `;`-joined; also `path`, `language`, `group_by`, `facet`, `workspace_id`, `ensure_fresh`.
  List/no-match results include `next_actions`; search adds `near_matches`, `empty_reason`.
- `workspace` — Index lifecycle: `status`, `health`, `onboarding`, `refresh`, `full`, `list`, `open`, `remove`,
  `leader`, `dashboard` (start/reuse the loopback dashboard). `status`, `health`, `onboarding`, `leader`, `refresh`, `full`, and
  `remove` accept `workspace_id` or `path`; `list` shows the registry.

## Workflows

- **New task / unfamiliar area**: `context` → `inspect` the key symbols → implement.
- **Understand a symbol**: first use `inspect target depth=overview`; use `depth=full` for complete body/reference/call lists.
- **Trace a flow**: `trace mode=refs` for usages, `mode=path` for A→B, `mode=bridge` for
  `dotnet-web`/`nextjs`/`nuxt` evidence. ASP.NET, htmx, and Vue route facts feed `dotnet-web`;
  route-fact audits: `patterns operation=search query=route`, `patterns operation=search pattern_id=htmx.attribute.v1`,
  `patterns operation=search pattern_id=vue.route_reference.v1`. For callers/callees use `inspect depth=full`.
  If ambiguous, retry with `scope=<file>`.
  If `mode=path` returns no path, treat it as no extracted graph path within depth, not proof unrelated; follow its `Next:` actions.
- **Find docs/prose**: `search mode=content "<phrase>"` returns `path:line` + snippet.
- **Find source-body text**: `search mode=source "<literal or phrase>"` searches verified source files.
- **Audit registered workspaces for exact text**: `content search query="dangerous term" workspace_id=all content_kind=source`
  or `content_kind=docs|config|external_file|web`, then `content read` with the hit's `workspace_id`.
- **Find known code shapes**: `patterns operation=list`, then `patterns operation=search pattern_id=<id>` with filters.
  If a query has no matches, use the suggested near matches or list output instead of raw AST grepping.
- **Inspect a large log/report**: `content import path=/tmp/build.log` → `content search query="error text"` →
  `content read source_id=... line=... context_lines=10`. Do not read or paste the full file.
- **Research a web page**: use `miller-web-research` to fetch markdown with `browser39` into a temp file, then
  `content add_markdown path=/tmp/page.md url=https://... display_path="title"` →
  `content search query="phrase" content_kind=web` → bounded `content read`. Do not create repo docs for pages.
- **Scope noisy search**: add `file_pattern=src/ui/**` or `language=typescript`.
- **Find text only inside comments or strings**: `search "<phrase>" regions=comment` or `regions=string_literal`;
  `MILLER_REGION_INDEX=0` disables this path.
- **List code markers**: `search mode=markers query=TODO,FIXME,HACK,XXX` for TODO/FIXME/HACK/XXX in comments/doc
  comments; add `file_pattern=src/**` or `language=csharp` to scope the audit.
- **Dashboard**: If the user asks to start, open, or show the Miller dashboard, call `workspace` with `operation=dashboard`. A dashboard request is a tool operation, not a file-finding task. Do not search plugin cache directories for dashboard files.
- **Scope a change**: `impact` (no args) for the current working-tree diff, or `impact target=…` for a planned edit → run the tests it lists → `edit` (preview) → `edit apply=true` →
  re-run `impact` if the surface changed.
- **Edit a symbol**: `inspect` it → `edit …` (preview, the default) → `edit … apply=true`.
- **Localized text edit**: `edit replace_text target=<file> old_text=<known-old> new_text=<new> match_mode=auto query=<nearby>`; add `line`/`anchor` for duplicates,
  review the match proof, then re-call with `apply=true`.
- **Index looks stale**: `workspace refresh` (or `workspace full` to force a clean rebuild).
- **Check index trust/readiness**: `workspace health` — reports stale/missing sidecars, parse diagnostics,
  capability gaps, skipped content, and recent telemetry outcomes without hydrating the full graph.
- **Start work in an indexed repo**: `workspace onboarding` summarizes local telemetry into starter guidance.
- **Diagnose leader issues**: `workspace operation=leader` reports the current indexer leader. Add `handoff=true`
  and `wait=true` only when you want to request graceful abdication; Miller queues a request and never kills a process.
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
      mode=source/external/web/all-text for content text, mode=markers for TODO/FIXME/HACK/XXX audits,
      regions=... for comments/strings, and filters to scope.
    - inspect(target, depth?) before reading files/symbols; depth=overview is compact, depth=full is complete.
    - trace(target, mode?, to?, scope?, reference_kind?) before manual file hopping; use mode=refs for usages and scope for ambiguous names. mode=path no-path means no extracted graph path within depth, not proof unrelated; mode=bridge is provider-scoped to `dotnet-web`, `nextjs`, and `nuxt`.
    - impact(target?|changed_paths?|diff?|git?/base?/staged?) before refactors and to choose tests.
    - edit(operation, target, ...) to preview index-aware edits; use match_mode=auto with query/anchor/line for localized replace_text.
    - content(import|add_markdown|search|read|list|remove|export, ...) for logs, web markdown, and audits; use workspace_id=all for audits and pass hit workspace_id on reads.
    - patterns(operation?, pattern_id?, query?, where?, path?, language?, group_by?, facet?) for code-shape facts.
    - workspace(status|health|onboarding|leader|refresh|full|list|open|remove|dashboard) for readiness, leader diagnostics/handoff, refresh, other repos, onboarding, or dashboard with operation=dashboard.
    Do NOT fall back to Glob/Read/Grep chains when a Miller tool fits. Miller returns targeted context in 1-2 calls.

Do not use `grep`/`find`/`rg` when a Miller tool fits. Do not read a whole file before `inspect`.
