# Task 7 — Tail relocation with loss accounting

**Status:** complete
**Branch / worktree:** `guidance-delivery` @ `/Users/murphy/source/miller/.worktrees/guidance-delivery`
**Build:** `dotnet build Miller.slnx -c Release` → Build succeeded, 0 Warning(s) / 0 Error(s). No source touched (docs + skills only).

## What changed

- **Created** `docs/agent-guidance.md` — the long-form home for the tail deleted from the embedded
  `MILLER_AGENT_INSTRUCTIONS.md` in Task 5 (commit `95ec42c`). Headed with the design note: the embedded
  `ServerInstructions` core is ≤1,900 chars by design because Claude Code truncates merged server instructions at
  ~2KB; this doc and the plugin skills carry the depth. Contains: a "how guidance is delivered" map, **per-tool
  detail** (the residual flags/selectors the short golden descriptions omit), the **full 21-item workflow
  catalog**, and the **subagent-dispatch primer** (the pasteable block, verbatim).
- **Modified** `.agents/skills/miller-impact-analysis/SKILL.md` (source of truth) — additively added the no-arg
  `impact()` working-tree git-diff form and the `git=true` scoping note. The skill previously showed only
  `target`/`changed_paths`/`diff` seeds and lacked the no-arg form the old workflow featured.
- **Modified** `.agents/skills/miller-explore-area/SKILL.md` (source of truth) — additively added a
  `workspace(operation="onboarding")` session-start note. The skill's freshness step previously covered only
  status/refresh.
- **Regenerated** `skills/miller-impact-analysis/SKILL.md` and `skills/miller-explore-area/SKILL.md` via
  `scripts/sync-plugin-skills.sh`. **Important:** `skills/` is `rm -rf`'d and recopied from `.agents/skills/` by
  that script, so editing `skills/` alone would be silently wiped on the next sync. I edited the source
  (`.agents/skills/`) and re-ran sync; both trees are byte-identical. This is a deliberate deviation from the
  literal "modify `skills/…`" file list to avoid exactly the silent loss this task exists to prevent — the other
  six named skills needed no change (see ledger), so only these two source+generated pairs moved.
- **Modified** `README.md` — added a paragraph after "Common Agent Workflow" pointing agent-facing readers to
  `docs/agent-guidance.md` and explaining the ≤1,900-char core rationale.
- **Modified** `docs/README.md` — added an `agent-guidance.md` entry under **Current docs**.

## Miller-first orientation (tool calls)

- Used `git show 95ec42c^:…` to recover the pre-Task-5 doc and `grep`/`sed`/`awk` over the plan for the Golden
  Content section. The Miller MCP tools were listed as deferred (`mcp__miller__*` require ToolSearch load) and the
  worktree's own index would need a refresh for this uncommitted branch; for a small, known set of docs files the
  targeted shell reads (recover-old-content, section-boundary greps, `diff -q` skill comparison) were the faster
  path and are consistent with "use Miller where useful". No Miller `edit` was used; all file edits were
  Read/Write/Edit on worktree paths only.

## Loss ledger — every old section → destination

Recovered doc: `git show 95ec42c^:src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` (11,982 chars). "New core" =
`src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` at HEAD. "golden" = the per-tool final text in
`docs/plans/2026-07-02-guidance-delivery-implementation.md` § Golden Content (Task 6). Zero unaccounted paragraphs.

### Header + Rules

| Old section | Destination |
|---|---|
| Title + intro ("Miller serves a fresh index…") | **Retained in new core** (core header + intro paragraph) |
| `## Rules` — 6 numbered rules | **Retained in new core** (core `## Rules`, 6 rules, condensed) |

### `## Tools` — 9 bullets (each superseded by its golden description; residual params relocated)

| Old bullet | Destination |
|---|---|
| `search` | **Superseded by search golden [Description]** (Task 6); residual (`mode` list, `MILLER_REGION_INDEX=0`, `has_doc`, `workspace_id` selectors) → `docs/agent-guidance.md` § Per-tool detail |
| `inspect` | **Superseded by inspect golden [Description]**; residual (`workspace_id`/`ensure_fresh`) → `docs/agent-guidance.md` § Per-tool detail |
| `context` | **Superseded by context golden [Description]**; residual (`reference_mode=usage`, `confidence=name_based`, `exclude_tests`) → `docs/agent-guidance.md` § Per-tool detail |
| `trace` | **Superseded by trace golden [Description]**; residual (`reference_kind` values, `scope=<file>`, `format=json` fields) → `docs/agent-guidance.md` § Per-tool detail |
| `impact` | **Superseded by impact golden [Description]**; residual (`workspace_id`/`ensure_fresh`) → `docs/agent-guidance.md` § Per-tool detail |
| `edit` | **Superseded by edit golden [Description]**; full param detail retained → `docs/agent-guidance.md` § Per-tool detail |
| `content` | **Superseded by content golden [Description]**; residual (`content_kind=web`, `diagnostic_code`/`next_actions`, `export` JSONL) → `docs/agent-guidance.md` § Per-tool detail |
| `patterns` | **Superseded by patterns golden [Description]**; residual (`where`/`group_by`/`facet`, `near_matches`/`empty_reason`) → `docs/agent-guidance.md` § Per-tool detail |
| `workspace` | **Superseded by workspace golden [Description]**; full lifecycle/selector detail → `docs/agent-guidance.md` § Per-tool detail |

### `## Workflows` — 21 bullets (all → `docs/agent-guidance.md` § Workflows; skill homes noted)

| # | Old workflow | Skill destination (in addition to `docs/agent-guidance.md` § Workflows) |
|---|---|---|
| 1 | New task / unfamiliar area | already in `miller-explore-area` (context→inspect) |
| 2 | Understand a symbol | already in `miller-explore-area` / `miller-orientation` |
| 3 | Trace a flow | already in `miller-bridge-trace` / `miller-explore-area` |
| 4 | Find docs/prose | already in `miller-search-debug` / `miller-orientation` |
| 5 | Find source-body text | already in `miller-search-debug` / `miller-orientation` |
| 6 | Audit registered workspaces for exact text | already in `miller-text-audit` |
| 7 | Find known code shapes | already in `miller-explore-area` / `miller-orientation` |
| 8 | Inspect a large log/report | already in `miller-large-file` (+ `miller-orientation` row) |
| 9 | Research a web page | already in `miller-web-research` |
| 10 | Scope noisy search | already in `miller-search-debug` / `miller-orientation` |
| 11 | Find text only inside comments/strings | already in `miller-search-debug` / `miller-orientation` |
| 12 | List code markers | already in `miller-search-debug` / `miller-orientation` |
| 13 | Dashboard | **`docs/agent-guidance.md` only** — no skill home (dashboard-launch guidance also lives in CLAUDE.md) |
| 14 | Scope a change | **ADDED no-arg `impact()` form to `miller-impact-analysis`**; `miller-editing` covers the edit tail |
| 15 | Edit a symbol | already in `miller-editing` |
| 16 | Localized text edit | already in `miller-editing` / `miller-orientation` |
| 17 | Index looks stale | already in `miller-orientation` (freshness first) |
| 18 | Check index trust/readiness | already in `miller-orientation` (health) |
| 19 | Start work in an indexed repo (`workspace onboarding`) | **ADDED onboarding note to `miller-explore-area`** (+ retained in new core's onboarding line) |
| 20 | Diagnose leader issues | **`docs/agent-guidance.md` only** — no skill home |
| 21 | Need another repo | already in `miller-cross-workspace` |

### `## Subagent Dispatching` + closing

| Old section | Destination |
|---|---|
| `## Subagent Dispatching` intro + pasteable code block | **`docs/agent-guidance.md` § Subagent dispatching** (block relocated verbatim) |
| Closing line ("Do not use grep/find/rg… Do not read a whole file before inspect") | **Retained in new core** (Rules 1/2/6) + echoed at end of `docs/agent-guidance.md` § Subagent dispatching |

**Ledger summary:** 6 sections → 2 retained in core (Header, Rules), 9 tool bullets superseded by golden
descriptions with residual detail in `agent-guidance.md`, 21 workflow bullets → `agent-guidance.md` (2 additively
merged into skills, 17 already covered by existing skills, 2 doc-only), 1 subagent block → `agent-guidance.md`, 1
closing line retained in core. **Zero unaccounted paragraphs.**

## Concerns

- **Source-of-truth deviation (surfaced, not silent):** the task file list named `skills/…/SKILL.md`, but those are
  generated. I edited `.agents/skills/…` and re-ran `scripts/sync-plugin-skills.sh` so the changes survive the next
  sync; both trees are byte-identical. If the lead intended only the generated copies, this is safe to keep — the
  generated copies match.
- I intentionally did **not** add dashboard/leader rows to `miller-orientation` or a leader skill; those two
  workflows have no natural skill home and are fully carried by `docs/agent-guidance.md`. Flagging in case the lead
  wants a skill surface for them later (would require a new/expanded skill, outside this task's owned files).
