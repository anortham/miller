# Miller agent guidance (long-form reference)

> **Why this doc exists.** Miller's embedded `ServerInstructions` core (`src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`)
> is kept to **≤1,900 characters by design**: Claude Code merges every MCP server's instructions into one shared
> block and truncates at roughly 2KB, so anything past the first ~2,000 characters is silently dropped and never
> reaches the agent. The core therefore carries only the discovery essentials — the six rules and a one-line "when
> to reach for each tool" list. This document and the Miller plugin skills carry the depth that no longer fits.
> It is a reference, not server instructions: read it when you want the full workflow catalog, the subagent-dispatch
> primer, or the per-tool parameter detail. The per-tool "what it does" also lives in each tool's own MCP
> description; this doc adds the residual flags and selectors those descriptions keep short.

## How the guidance is delivered

- **Embedded core** — the ≤1,900-char discovery core, always delivered to Claude Code (and other MCP clients) as
  `ServerInstructions`. Six rules + a one-line tool list. Never let it grow past ~1,900 chars.
- **Success-path nudges** — each read tool appends a one-line `next: <call>` hint on success so the next step is
  discoverable without reading this doc.
- **Plugin skills** — `miller-orientation`, `miller-explore-area`, `miller-impact-analysis`, `miller-editing`,
  `miller-search-debug`, `miller-text-audit`, `miller-cross-workspace`, `miller-bridge-trace`, `miller-large-file`,
  and `miller-web-research` carry per-workflow depth for plugin users.
- **This reference** — the complete workflow catalog, the subagent primer, and per-tool parameter detail, for
  everyone via the repo.

## Per-tool detail

The one-line "what each tool is for" lives in the embedded core and in each tool's MCP description. The residual
parameters and selectors below are what those short forms omit.

- **`search`** — `mode=auto|text|symbol|file|markers|content|source|external|web|all-text`. Natural-language
  queries auto-hide tests (`exclude_tests=false` to include them). `mode=content` has the alias `docs`. Scope with
  `file_pattern`, `language`, and `limit`. `regions=comment|doc_comment|string_literal` restricts to source
  regions; `MILLER_REGION_INDEX=0` opts the region index out. Multi-term symbol searches use AND first and expose
  `relaxed=or` when OR fallback fills the page. Auto mode can return typed symbol and file arms for mixed queries.
  Identifier-shaped queries with only fuzzy near matches state that no exact symbol exists and omit the inspect
  nudge; JSON marks those rows with `exact_match=false`.
  `retrieval=auto|lexical|hybrid|semantic` selects the per-call symbol policy; lexical does no vector work and the
  global semantic off switch remains authoritative. `mode=markers` with `query=TODO,FIXME,HACK,XXX` runs
  a marker audit. Symbol hits may include `has_doc`. Optional `workspace_id` accepts a display ID, unique prefix,
  full ID, root path, `current`, or `primary`; an explicit `workspace_id` defaults `ensure_fresh=true`.
- **`inspect`** — a file path lists symbols; a symbol name gives definition, signature, and docs. Default depth is
  `summary`. The first symbol read should usually be `inspect target depth=overview` (bounded refs/calls/body
  preview); use `depth=full` for the complete body and complete relation lists. Optional `workspace_id` and
  `ensure_fresh` follow `search`.
- **`context`** — give the task plus optional `failing_test`/`stack_trace`; compact output leads with seeds and
  reasons, then neighbours, then `## next inspect`. `reference_mode=usage` adds definitions, name-based refs, call
  IDs, and source chunks; treat `confidence=name_based` as possible, not proven. `exclude_tests=true` filters tests
  only in usage mode. Optional `workspace_id` and `ensure_fresh` route through the registry provider.
- **`trace`** — `mode=refs` (name-based usages; optional `reference_kind=call|variable_ref|type_usage|member_access|import`;
  on empty, fall back to `search mode=source`), `mode=path` (shortest path to `to`; no path means no extracted
  graph path within depth, **not** proof the code is unrelated), `mode=bridge` (provider-scoped). Links are flagged
  `[verb-unknown]`/`[ambiguous]`. Use `format=json` for
  refs/nodes/links/diagnostics/actions; `scope=<file>` for duplicate names. `mode=bridge` is provider-scoped to
  `dotnet-web`, `nextjs`, `nextjs-api`, `nuxt`, `nuxt-api`, `vue`, `react`, and `backend-http`; on any other stack
  use `mode=refs`/`mode=path` or `inspect depth=full`.
- **`impact`** — with **no args** it reads the working-tree git diff and maps changed ranges to impacted symbols
  plus linked tests — run it after edits, before committing. Or pass exactly one of `target`, `changed_paths`,
  `diff`, or `git=true` (`base`/`staged` imply git). Optional `workspace_id` and `ensure_fresh` work for
  registered workspaces.
- **`edit`** — index-aware edits: `replace_text`, `replace_symbol_body`, `replace_symbol_signature`,
  `rename_symbol`, `insert_before`, `insert_after`, `add_doc`. For localized existing-file edits, `replace_text`
  supports `match_mode=auto|exact|normalized|fuzzy` plus `query`/`anchor`/`line` selectors and match proof.
  Previews a diff unless `apply=true`; stale targets self-heal or tell you to refresh / pass `allow_stale`.
- **`content`** — import/search/read/list/remove/export external/web text for logs, CI output, reports, dumps, and
  fetched markdown. `search` returns snippets, `source_id`, and `workspace_id`; `read` returns ≤200-line windows —
  pass the hit's `source_id` and `workspace_id` when reading cross-workspace hits. Use `content_kind=web` for web
  reads, or `workspace_id=all` for audits. Empty searches and failed reads include recovery guidance; JSON includes
  `diagnostic_code` and `next_actions`. `export` is raw JSONL.
- **`patterns`** — list, summarize, and search `structural_facts` code-shape facts. Run `patterns()` to see emitted
  ids (not raw AST queries). `operation=list|summary|search`; `query` is search-only. `where=key=value` ANDs when
  repeated or `;`-joined; also `path`, `language`, `group_by`, `facet`, `workspace_id`, `ensure_fresh`. List and
  no-match results include `next_actions`; search adds `near_matches` and `empty_reason`.
- **`workspace`** — index lifecycle: `status`, `health`, `onboarding`, `refresh`, `full`, `list`, `open`, `remove`,
  `leader`, `dashboard` (start/reuse the loopback dashboard). `status`, `health`, `onboarding`, `leader`, `refresh`,
  `full`, and `remove` accept `workspace_id` or `path`; `list` shows the registry (`filter`/`limit`).

## Workflows

- **New task / unfamiliar area**: `context` → `inspect` the key symbols → implement.
- **Understand a symbol**: first use `inspect target depth=overview`; use `depth=full` for complete
  body/reference/call lists.
- **Trace a flow**: `trace mode=refs` for usages, `mode=path` for A→B, `mode=bridge` for
  `dotnet-web`/`nextjs`/`nextjs-api`/`nuxt`/`nuxt-api`/`vue`/`react`/`backend-http` evidence. ASP.NET, htmx, and
  frontend route-reference facts feed `dotnet-web`; client fetch/axios `http.client_request.v1` facts feed
  `dotnet-web` and the `*-api` providers, plus `backend-http` for python/go/java/ruby and vue client requests
  beyond js/ts. Route-fact audits: `patterns operation=search query=route`,
  `patterns operation=search pattern_id=htmx.attribute.v1`,
  `patterns operation=search pattern_id=http.client_request.v1`,
  `patterns operation=search pattern_id=vue.route_reference.v1`. For callers/callees use `inspect depth=full`. If
  ambiguous, retry with `scope=<file>`. A `mode=path` no-path result is not proof the code is unrelated; follow its
  `Next:` actions.
- **Find docs/prose**: `search mode=content "<phrase>"` returns `path:line` + snippet.
- **Find source-body text**: `search mode=source "<literal or phrase>"` searches verified source files.
- **Audit registered workspaces for exact text**:
  `content search query="dangerous term" workspace_id=all content_kind=source` (or
  `content_kind=docs|config|external_file|web`), then `content read` with the hit's `workspace_id`.
- **Find known code shapes**: `patterns operation=list`, then `patterns operation=search pattern_id=<id>` with
  filters. If a query has no matches, use the suggested near matches or list output instead of raw AST grepping.
- **Inspect a large log/report**: `content import path=/tmp/build.log` → `content search query="error text"` →
  `content read source_id=... line=... context_lines=10`. Do not read or paste the full file.
- **Research a web page**: use `miller-web-research` — fetch markdown with `browser39`, `content add_markdown`,
  then `content search query="phrase" content_kind=web` → bounded `content read`. Do not create repo docs for pages.
- **Scope noisy search**: add `file_pattern=src/ui/**` or `language=typescript`.
- **Find text only inside comments or strings**: `search "<phrase>" regions=comment` or `regions=string_literal`;
  `MILLER_REGION_INDEX=0` disables this path.
- **List code markers**: `search mode=markers query=TODO,FIXME,HACK,XXX` for TODO/FIXME/HACK/XXX in comments/doc
  comments; add `file_pattern=src/**` or `language=csharp` to scope the audit.
- **Dashboard**: if the user asks to start, open, or show the Miller dashboard, call `workspace` with
  `operation=dashboard`. A dashboard request is a tool operation, not a file-finding task. Do not search plugin
  cache directories for dashboard files.
- **Scope a change**: `impact` (no args) for the current working-tree diff, or `impact target=…` for a planned
  edit → run the tests it lists → `edit` (preview) → `edit apply=true` → re-run `impact` if the surface changed.
- **Edit a symbol**: `inspect` it → `edit …` (preview, the default) → `edit … apply=true`.
- **Localized text edit**:
  `edit replace_text target=<file> old_text=<known-old> new_text=<new> match_mode=auto query=<nearby>`; add
  `line`/`anchor` for duplicates, review the match proof, then re-call with `apply=true`.
- **Index looks stale**: `workspace refresh` (or `workspace full` to force a clean rebuild).
- **Check index trust/readiness**: `workspace health` — stale/missing sidecars, parse diagnostics, capability
  gaps, skipped content, recent telemetry outcomes.
- **Start work in an indexed repo**: `workspace onboarding` summarizes local telemetry into starter guidance.
- **Diagnose leader issues**: `workspace operation=leader`; add `handoff=true wait=true` to request graceful
  abdication (queued; Miller never kills a process).
- **Need another repo**: `workspace list`; if registered, pass the display ID, unique prefix, full ID, or root path
  as `workspace_id` to code tools. If absent, run `workspace operation=open path=/absolute/repo`, then retry.
  `workspace_id=all` is only for `content search` text audits, not code read tools.

## Subagent dispatching

Subagents do not always inherit Miller's server instructions. When dispatching a subagent that will explore or
modify code, paste this block into its prompt so it reaches for Miller before raw shell/file exploration:

    ## Code Intelligence Tools (use instead of Grep/Glob/Read)
    You have Miller MCP tools. Use them before raw shell/file exploration:
    - context(query, ...) for unfamiliar task-shaped orientation.
    - search(query, mode?, regions?, file_pattern?, language?) before rg/grep/find; use mode=content for docs,
      mode=source/external/web/all-text for content text, mode=markers for TODO/FIXME/HACK/XXX audits,
      regions=... for comments/strings, and filters to scope.
    - inspect(target, depth?) before reading files/symbols; depth=overview is compact, depth=full is complete.
    - trace(target, mode?, to?, scope?, reference_kind?) before manual file hopping; use refs for usages and scope for ambiguous names. mode=path no-path means not proven unrelated; mode=bridge is provider-scoped to `dotnet-web`, `nextjs`, `nextjs-api`, `nuxt`, `nuxt-api`, `vue`, `react`, and `backend-http`.
    - impact(target?|changed_paths?|diff?|git?/base?/staged?) before refactors and to choose tests.
    - edit(operation, target, ...) to preview index-aware edits; use match_mode=auto with query/anchor/line for localized replace_text.
    - content(import|add_markdown|search|read|list|remove|export, ...) for logs, web markdown, and audits; use workspace_id=all for audits and pass hit workspace_id on reads.
    - patterns(operation?, pattern_id?, query?, where?, path?, language?, group_by?, facet?) for code-shape facts.
    - workspace(status|health|onboarding|leader|refresh|full|list|open|remove|dashboard) for readiness, leader diagnostics/handoff, refresh, other repos, onboarding, or dashboard with operation=dashboard.
    Do NOT fall back to Glob/Read/Grep chains when a Miller tool fits. Miller returns targeted context in 1-2 calls.

Do not use `grep`/`find`/`rg` when a Miller tool fits. Do not read a whole file before `inspect`.
