# Guidance Delivery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Deliver Miller's guidance through channels that verifiably reach agents (per `docs/plans/2026-07-02-guidance-delivery-design.md`): a ≤1,900-char ServerInstructions discovery core, nine golden-text tool descriptions as usage contracts, and one-line success-path nudges — replacing the fictional 12k budget.

**Architecture:** Content changes (embedded doc, `[Description]` attributes, skills, ADR) plus one new static formatter (`NextStepHint`) consumed by three tool renderers. Test gates rewritten in `AgentInstructionsTests`. No new MCP tools, no schema changes, JSON untouched.

**Tech Stack:** .NET 10, xUnit fast suite.

**Architecture Quality:** Approved shape (design §1–§3, Codex-reviewed): ServerInstructions = discovery contract; descriptions = post-discovery usage contracts; nudges = decision-time routing, hint *decision* local to each tool, hint *format* in one shared static `NextStepHint`. Risk: low. If code reality contradicts this shape, report a plan mismatch — do not redesign locally.

## Global Constraints

- Warnings are errors; build must stay 0/0 (`Directory.Build.props`).
- Fast suite for red/green (`scripts/test.sh`); no test may spawn `julie-extract`.
- **Golden content in this plan is the contract.** Tasks 5–6 ship the exact texts below. Whitespace-only adjustments to meet a char budget are worker judgment; any WORDING change requires a plan-mismatch report to the lead, not silent editing.
- ServerInstructions core ≤**1,900** chars after `\r\n` normalization. Description budgets: ≤900 default, `trace` ≤1,500, `search` ≤1,100. Param descriptions ≤250 (existing). Total description+param text ≤**9,000** chars.
- Nudges: compact output only, max ONE hint line per response, real copyable targets, suppression rules exactly as specified. JSON byte-identical everywhere.
- No new MCP tools or operations. No edits to `mcp-config.json` or plugin manifests.
- Do not push, tag, or release. Commit per task on the feature branch.
- Tail relocation must be loss-accounted: every removed paragraph names its destination (skill/docs path) or the description that supersedes it, in the Task 7 report.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` (Testing section) + `scripts/test.sh`.
**Worker red/green scope:** targeted filter for the touched test class, e.g. `dotnet test tests/Miller.Tests --filter "FullyQualifiedName~AgentInstructionsTests"`.
**Worker ceiling:** the targeted filter(s) named in the task. Workers do not run the full fast suite; the lead owns it.
**Worker gate invariant:** stated per task below.
**Lead affected-change scope:** `scripts/test.sh` (full fast suite) after each wave.
**Branch gate:** `dotnet build Miller.slnx -c Release` (0/0) + `scripts/test.sh all` before finishing.
**Replay/metric evidence:** none hard; before/after schema char counts and a live truncation-window render are report-only evidence in the run report.
**Escalation triggers:** any JSON contract test failing; `AgentInstructionsTests` gates conflicting with golden content (plan mismatch — stop, report).
**Assigned verification failure:** stop and report; do not weaken a gate.
**Verification ledger:** invariant, command, scope, SHA, result, time per task in `.razorback/sdd/progress.md`.

## Model Routing

**Project source of truth:** none (`RAZORBACK.md` absent) → `inherit` for all tiers, all workers. Strategy/gate-review/escalation = lead (this session). Implementation tier for all tasks (each owns tests; none mechanical). Escalate on two failed attempts or gate conflicts.

## Execution Notes

- Dedicated worktree: `git worktree add .worktrees/guidance-delivery -b guidance-delivery` from repo root.
- Waves: **W1:** Tasks 1, 5 (parallel — disjoint files). **W2:** Tasks 2, 3, 4 in parallel (disjoint tool files) then Task 6 (touches all tool files' attributes — run AFTER 2–4 land to avoid collisions). **W3:** Tasks 7, 8 (parallel).
- Workers `git add` only owned files by explicit path; retry on index.lock; Miller MCP reads serve the main checkout — read worktree files for exact current text; never use Miller `edit`.

---

## Golden Content (single source of truth for Tasks 5–6)

### ServerInstructions core (Task 5) — target ≤1,900 chars

```markdown
# Miller — Code Intelligence Server

Fresh index of this workspace's code. One Miller call beats shell greps and full-file reads: ranked, structured, fewer tokens.

## Rules

1. Search before reading: run `search` before grep/rg/cat or opening whole files.
2. Structure before content: `inspect` a file's symbols or a symbol's signature before reading it whole.
3. Impact before changing: run `impact` to see blast radius and which tests to run.
4. Trace to follow a thread: `trace` answers "who references this?" and "how does A reach B?" — not manual file hopping.
5. Edit with a preview: `edit` dry-runs a diff; set apply=true only after it looks right.
6. Trust the index: do NOT re-verify Miller results with grep/find. If stale, run `workspace refresh` and retry.

## When to reach for each tool

- search — find code or text by symbol name, identifier, phrase, marker (TODO/FIXME audit), docs/config prose, or source-body text.
- inspect — a file or symbol you can already NAME: definition, signature, docs, refs, callers, body.
- context — FIRST call in an unfamiliar area: a token-budgeted bundle of entry-point symbols for a task, with reasons.
- trace — follow references, shortest dependency paths, or cross-language route chains (frontend→backend).
- impact — before a refactor or after edits: impacted symbols plus likely tests, from a symbol, file, or git diff.
- edit — index-aware replace/rename/body-rewrite with a diff preview and match proof.
- patterns — pre-extracted code-shape facts (routes, config keys, doc structure) across ~36 languages.
- content — import then search/read logs, CI output, web markdown, and other large text without full-file reads.
- workspace — index lifecycle: status, refresh, health, list (filter/limit), onboarding, dashboard.

Run `workspace onboarding` early for telemetry-derived guidance about THIS repo.
```

### Tool descriptions (Task 6) — final text per tool

**search (≤1,100):**
> Search indexed code and return ranked results — use this before shell rg/grep/cat or reading whole files. Pass a symbol name, identifier, or natural-language phrase; test code is auto-hidden for phrase queries unless exclude_tests=false. Modes: mode=markers audits TODO/FIXME/HACK/XXX in comments; mode=content (alias docs) searches docs/config prose; mode=source searches source-body text; mode=external/web/all-text search imported corpus text. regions=comment,doc_comment,string_literal restricts to those source regions. Scope with file_pattern/language/limit. NOT for: a symbol you can already name exactly (inspect it), orienting on an unfamiliar area (use context), or finding who references a symbol (use trace). Example: search query="promote rebuild" mode=source. Compact by default; format=json to chain.

**inspect (≤900):**
> Inspect a file or symbol you can already name. A file path lists its symbols; a symbol name gives definition, signature, docs — depth=overview adds bounded refs/callers/callees and a body preview (the right first symbol read); depth=full adds the complete body and relation lists. Use before reading any entire file. NOT for: discovering which symbol matters in an unfamiliar area (use context) or full reference lists across the repo (use trace mode=refs). Example: inspect target=FullRebuildPromotion depth=overview.

**context (≤900):**
> First call in an UNFAMILIAR code area: give a task or question (optionally a failing test or stack trace) and get a small justified bundle — the most relevant entry-point symbols with one-line reasons, capped relevance-ranked neighbours, and copyable next-inspect calls, all within token_budget. NOT for: a symbol you can already name (inspect it) or text lookups (search). Example: context query="how does workspace refresh converge the search sidecar". Compact by default; format=json to chain.

**trace (≤1,500):**
> Follow a thread of code. mode=refs lists name-based identifier references (usages) with the enclosing symbol per hit; mode=path finds the shortest dependency path from target to 'to'; mode=bridge follows provider-scoped cross-language chains (dotnet-web, nextjs, nextjs-api, nuxt, nuxt-api, vue, react, backend-http) with a confidence band. mode=auto (callers/callees) is subsumed by inspect depth=full — prefer inspect for that. refs is name-based and may be empty for some languages; on empty, fall back to search mode=source for text occurrences. Reduced-confidence links are flagged [verb-unknown]/[ambiguous] — never trust an unflagged link more than a flagged one. NOT for: a symbol's own definition/signature (inspect), or ranking which tests to run before a change (impact). Example: trace target=FreshnessService mode=refs. format=json for structure; format=full adds per-link signals in bridge output. Empty results include next actions.

**impact (≤900):**
> Blast-radius analysis: what a change affects and which tests to run. With NO args it reads the working-tree git diff and maps changed ranges to impacted symbols + likely tests — run it after edits, before committing. Or pass exactly one of: target (symbol or file), changed_paths, diff (unified), or git/base/staged for a specific git diff. Use BEFORE a refactor and AFTER edits; prefer it over grepping for usages when the question is "what breaks and what do I test". NOT for: plain reference lists (trace mode=refs). Example: impact target=SymbolSearchSidecar. Compact by default; format=json to chain.

**edit (≤900):**
> Edit indexed code with proof: previews a diff and writes NOTHING by default; set apply=true to commit the change. Operations: replace_text (match_mode + query/anchor/line selectors avoid full-file reads; returns match proof), replace_symbol_body, replace_symbol_signature, rename_symbol (workspace-wide), insert_before/insert_after, add_doc. If the index is stale for the target file Miller converges it first; refused only if that fails (re-index or pass allow_stale). NOT for: creating new files (use your file tools) or bulk text audits (search mode=markers first). Example: edit operation=replace_text target=src/App.cs old_text="retries: 3" new_text="retries: 5".

**patterns (≤900):**
> Query code-shape facts pre-extracted by julie-extractors (~130 pattern ids, ~36 languages: HTTP routes, HTML/htmx/Alpine, SQL DDL, async/await, JSON/YAML/TOML/Markdown structure). Call with no args to list observed pattern_id values; then operation=search with pattern_id (plus path/language/where filters) or a free-text query that matches pattern ids. Use INSTEAD of raw-grepping routes, config keys, or document structure. NOT for: raw AST queries or arbitrary text (search). Examples: patterns operation=search pattern_id=aspnet.minimal_api.route.v1; patterns operation=search query=route.

**content (≤900):**
> Import, search, read, list, remove, and export text in Miller's content corpus: logs, CI output, web markdown, reports, large text files, JSONL feeds. Search hits carry a source_id; pass it to read for a bounded line window instead of loading the whole file. Use for any big non-workspace text you'd otherwise cat into context. NOT for: workspace source/docs text (search mode=source or mode=content) or code symbols (search/inspect). Example: content operation=import path=/tmp/ci.log then content operation=search query="first failing test".

**workspace (≤900):**
> Manage the workspace index. Defaults to status (freshness, revision, leader). refresh updates stale files; full forces a rebuild; health reports readiness + extraction quality; onboarding gives telemetry-derived guidance for this repo; list shows registered workspaces (filter/limit, recency-ordered); open registers another repo for cross-workspace reads; leader diagnoses/hands off the indexer lock; dashboard starts/opens the local dashboard. Use when results look stale, before cross-workspace queries, or at session start (onboarding). NOT for: reading code (search/inspect). Example: workspace operation=list filter=eros limit=10.

---

### Task 1: `NextStepHint` shared formatter

**Files:**
- Create: `src/Miller.Server/Tools/NextStepHint.cs`
- Test: `tests/Miller.Tests/Server/NextStepHintTests.cs`

**Interfaces:**
- Produces: `internal static class NextStepHint` with `internal static string Render(string toolCall, string? reason = null)` returning exactly `next: <toolCall>` or `next: <toolCall> — <reason>` (single line, no trailing newline; throws on multi-line/blank toolCall).

**What to build:** The single format seam for all success-path nudges so nine renderers cannot drift.

**Approach:** Pure static, no I/O. Reject strings containing `\n`. Tests: format shape, reason optional, guard throws.

**Acceptance criteria:**
- [x] Format exactly `next: …` / `next: … — …`; guards proven by tests.
- [x] Worker scope green (`--filter "FullyQualifiedName~NextStepHintTests"`), committed.

### Task 2: search success nudge

**Files:**
- Modify: `src/Miller.Server/Tools/SearchTool.cs` (compact symbol-results renderer — locate the definition-found/symbol-hit path)
- Test: `tests/Miller.Tests/Server/SearchToolTests.cs`

**Interfaces:**
- Consumes: `NextStepHint.Render` (Task 1).
- Produces: symbol-mode compact output may end with one `next: inspect target="<top-hit name>" depth=overview` line.

**What to build:** When compact symbol search returns hits and the top hit is a symbol (not a file/content hit), append the hint naming the top hit.

**Approach:** Fire only in symbol/auto modes with ≥1 symbol hit; suppress when the top hit is a file match, any text/content mode, markers mode, or empty results (failure path owns those). Escape the name as `context`'s `NextInspectLine` does. One line max — if a banner/rescue section already closes the output, hint goes last.

**Acceptance criteria:**
- [x] Hint appears with real top-hit name in symbol/auto success; absent in content/source/markers/file-match/empty cases. JSON unchanged.
- [x] Worker scope green (`--filter "FullyQualifiedName~SearchToolTests"`), committed.

### Task 3: inspect impact nudge

**Files:**
- Modify: `src/Miller.Server/Tools/InspectTool.cs` (`RenderSymbolCompact` — after body section)
- Test: `tests/Miller.Tests/Server/InspectToolTests.cs`

**Interfaces:**
- Consumes: `NextStepHint.Render`; ref count already computed in `RenderSymbolCompact` (`refs.Count`); `IndexedSymbol.IsTest`.
- Produces: overview/full symbol output may end with `next: impact target="<name>" — <N> dependents` line.

**What to build:** Nudge toward `impact` when inspecting a symbol that many places depend on.

**Approach:** Fire when depth is overview or full AND `refs.Count >= 4` AND `!sym.IsTest`. Suppress at depth=summary and for file listings. N = `refs.Count`.

**Acceptance criteria:**
- [x] Hint fires at ≥4 non-test refs (overview + full), absent at ≤3 refs / test symbols / summary / file listings. JSON unchanged.
- [x] Worker scope green (`--filter "FullyQualifiedName~InspectToolTests"`), committed.

### Task 4: trace refs nudge

**Files:**
- Modify: `src/Miller.Server/Tools/TraceTool.cs` (`RunRefs` compact success path)
- Test: `tests/Miller.Tests/Tools/TraceToolTests.cs`

**Interfaces:**
- Consumes: `NextStepHint.Render`; `targetSymbol.IsTest`.
- Produces: non-empty compact refs output may end with `next: impact target="<name>" before editing`.

**What to build:** After an agent sees a symbol's usages, route it to impact before it edits.

**Approach:** Fire when `shown.Length > 0 && !targetSymbol.IsTest`; the hint renders after the references block (and after any truncation note). Empty-refs path keeps its existing next_actions untouched.

**Acceptance criteria:**
- [x] Hint present on non-empty refs for non-test targets; absent for test targets and empty results. JSON unchanged.
- [x] Worker scope green (`--filter "FullyQualifiedName~TraceToolTests"`), committed.

### Task 5: ServerInstructions discovery core + core gates

**Files:**
- Modify: `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` (replace ENTIRE content with the golden core above)
- Modify: `tests/Miller.Tests/Server/AgentInstructionsTests.cs` (core gates)
- Test: same file

**Interfaces:**
- Consumes: golden core text (this plan).
- Produces: `AgentInstructions.Load()` ≤1,900 chars normalized; the constant/test-name for the 12k fiction removed.

**What to build:** Replace the 11,856-char doc with the golden core. Rewrite core gates: (1) `Load_CoreFitsClaudeCodeDeliveryWindow` — length ≤1,900 after `ReplaceLineEndings("\r\n")`; (2) `Load_RoutingTableNamesEveryTool` — for every `[McpServerTool]` name (reflection, as existing tests do), the core contains `- <name> — ` routing line; (3) keep the lead-rule content check (`Search before reading`); (4) delete `MaxServerInstructionsChars = 12_000` and `Load_StaysUnderClaudeCodeInstructionBudget`.

**Approach:** Golden text verbatim; if >1,900 chars normalized, trim whitespace only, else plan-mismatch. Keep the embedded-resource loading path untouched. Other AgentInstructionsTests members that assert against tool-list phrases in the old doc must be updated to the new core's phrasing — enumerate them first (`--filter AgentInstructionsTests` red run).

**Acceptance criteria:**
- [x] `Load()` ≤1,900 normalized; every tool named in a routing line; 12k constant + fiction test gone.
- [x] Worker scope green (`--filter "FullyQualifiedName~AgentInstructionsTests"`), committed.

### Task 6: nine golden descriptions + description gates

**Files:**
- Modify: `src/Miller.Server/Tools/{SearchTool,InspectTool,ContextTool,TraceTool,ImpactTool,EditTool,PatternsTool,ContentTool,WorkspaceTool}.cs` (the `[Description]` attribute on each `[McpServerTool]` method ONLY — locate via `[McpServerTool(Name = "...")]`)
- Modify: `tests/Miller.Tests/Server/AgentInstructionsTests.cs` (description gates)
- Modify: `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` — NO (already final from Task 5; do not touch)

**Interfaces:**
- Consumes: golden description texts (this plan); per-tool budgets (900 default, trace 1,500, search 1,100).
- Produces: description gates — per-tool budget table in the test; golden-clause assertions; total ≤9,000.

**What to build:** Replace each tool's `[Description]` with its golden text. Add gates: (1) per-tool budget (table-driven, documented overrides); (2) golden-clause assertions — each description contains `NOT for:` and `Example:`; the seven cut tools' (context, trace, impact, edit, patterns, content, workspace) `NOT for:` clause names at least one other tool; (3) total description+param chars ≤9,000, with the measured total logged in the test failure message; (4) params ≤250 unchanged.

**Approach:** Attribute strings use the same concatenated-literal style as today. Record before (4,522) / after totals in the report. EditTool/ImpactTool/PatternsTool method locations: find via `[McpServerTool(Name = "edit"|"impact"|"patterns")]` — do not guess line numbers.

**Acceptance criteria:**
- [ ] All nine descriptions are the golden texts; budgets and clause gates pass; total ≤9,000 measured and reported.
- [ ] Worker scope green (`--filter "FullyQualifiedName~AgentInstructionsTests"`), committed.

### Task 7: tail relocation with loss accounting

**Files:**
- Modify: `skills/miller-explore-area/SKILL.md`, `skills/miller-impact-analysis/SKILL.md`, `skills/miller-editing/SKILL.md`, `skills/miller-search-debug/SKILL.md`, `skills/miller-text-audit/SKILL.md`, `skills/miller-cross-workspace/SKILL.md`, `skills/miller-bridge-trace/SKILL.md`, `skills/miller-orientation/SKILL.md` (only those a tail paragraph maps to)
- Create: `docs/agent-guidance.md` (long-form home of the old Workflows + Subagent Dispatching content)
- Modify: `README.md` (link `docs/agent-guidance.md`), `docs/README.md` (docs map entry)

**Interfaces:**
- Consumes: the pre-Task-5 `MILLER_AGENT_INSTRUCTIONS.md` content — recover it from git (`git show <pre-task-5-sha>:src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`).
- Produces: loss ledger in the task report: every tail paragraph → destination (skill path / docs/agent-guidance.md / "superseded by <tool> description").

**What to build:** Relocate the deleted ~10k tail per design §2 so plugin users get it via skills and everyone can reach it via docs. Merge workflow guidance into the matching skills without duplicating what the skill already says; put the complete long-form text in `docs/agent-guidance.md`.

**Approach:** Ledger first (map each paragraph), then edit. Skill edits are additive/merging — do not rewrite unrelated skill content. No new skills. Verify each named skill exists before editing.

**Acceptance criteria:**
- [ ] `docs/agent-guidance.md` exists, linked from README + docs map; skill merges done; loss ledger complete with zero unaccounted paragraphs.
- [ ] No test scope (docs task) — build still green (`dotnet build Miller.slnx -c Release`), committed.

### Task 8: ADR, CLAUDE.md, evidence loop

**Files:**
- Create: `docs/adr/ADR-0001-guidance-delivery-channels.md`
- Modify: `CLAUDE.md` (one bullet in "Server host & startup" pointing at the ADR: descriptions are the usage contract, core ≤1,900 chars is the discovery contract, 2KB/shared-4KB Claude Code limits, do not grow either without reading the ADR)
- Modify: `docs/plans/2026-07-02-guidance-delivery-design.md` (§5: add "follow-up checkpoint due ~2026-07-23" line with the exact re-read command `miller workspace onboarding --json`)
- Run: `scripts/sync-agents.sh`; verify `cmp -s CLAUDE.md AGENTS.md`

**Interfaces:**
- Consumes: design doc §7 ADR shape; measured facts (2,047-char cut, issue #43474, baseline table).

**What to build:** The durable decision record so no future session re-invents a 12k budget, per the ADR template in razorback architecture-quality (Context / Decision / Consequences / Applies To / Future Agents).

**Acceptance criteria:**
- [ ] ADR-0001 committed with the 2026-07-02 measurement; CLAUDE.md bullet added; AGENTS.md re-synced byte-identical; design doc checkpoint line added.
- [ ] Build green, committed.

---

## Run Report (fill during execution)

Ledger + before/after: schema char totals (4,522 → measured), core chars (11,856 → measured ≤1,900), live `trace refs`/`inspect`/`search` renders showing nudges, truncation-window render of the new core.
