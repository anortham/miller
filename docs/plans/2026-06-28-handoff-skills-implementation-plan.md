# Handoff Skills Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Add Miller-provided `handoff-out` and `handoff-in` skills that let agents move work between harnesses/models using existing Miller workspace facts, code context, impact analysis, and explicit session notes.

**Architecture:** Implement this as skill-layer orchestration, not a new MCP tool and not a CLI command. `handoff-out` assembles a markdown packet from git state, Miller `workspace`/`impact`/`context` output, and agent-supplied session notes; `handoff-in` reads that packet, validates drift against the current workspace, and gives the receiving agent a concise resume brief.

**Tech Stack:** Miller skills under `.agents/skills/`, generated `skills/` mirror, existing Miller MCP tools (`workspace`, `impact`, `context`, `search`, `inspect`, `trace`), shell/git for local workspace facts, markdown packet files under `.miller/handoffs/`, Node plugin/skill tests, README/GitHub Pages docs, and `scripts/test.sh`.

**Architecture Quality:** Affected modules are the Miller skill package and documentation, not Miller server code. Caller-facing interface is two user-invocable skills, `handoff-out` and `handoff-in`, with a stable markdown packet convention under `.miller/handoffs/`. Architecture risk is low-medium: the main risk is over-trusting stale packet data, mitigated by `handoff-in` drift validation and by making session notes explicit agent-authored context rather than inferred durable memory.

## Global Constraints

- Do not add a new MCP tool.
- Do not add a new CLI command.
- Do not require Goldfish.
- Do not call Goldfish automatically from either skill.
- Skill names are exactly `handoff-out` and `handoff-in`; do not prefix them with `miller-`.
- Store handoff packets under `.miller/handoffs/`; `.miller/` is gitignored and workspace-local.
- Write a timestamped packet and update `.miller/handoffs/latest.md`.
- Packet format is markdown with a small frontmatter block plus stable sections.
- The packet may include sensitive session notes or diff/context snippets; the skills must warn agents not to include secrets.
- `handoff-out` may use only existing Miller facts plus local git/session context supplied by the active agent.
- `handoff-in` must validate packet drift before giving continuation guidance.
- Existing Miller skills remain available and unchanged except for optional orientation/docs pointers to the new handoff skills.
- Keep `.agents/skills/` canonical and regenerate `skills/` with `scripts/sync-plugin-skills.sh`.
- If `CLAUDE.md` changes, regenerate `AGENTS.md` with `scripts/sync-agents.sh` and verify byte-for-byte sync. This plan should not need `CLAUDE.md`.
- No source code should be changed unless tests/docs reveal a plugin packaging or skill metadata gap.

---

## Interface Decision

Chosen lane: Miller skills.

`handoff-out` is the producing skill. It creates a packet for another harness/model to resume work.

Arguments:

```text
handoff-out <target-harness> [--goal "<goal>"] [--next "<next action>"] [--notes "<session notes>"] [--token-budget 4000]
```

The arguments are skill guidance, not a parsed CLI contract. Agents should interpret natural-language invocations such as "handoff to Cursor" or "prepare a handoff packet for Claude" equivalently.

`handoff-in` is the consuming skill. It validates and summarizes a packet.

Arguments:

```text
handoff-in [<packet-path>]
```

If no path is provided, it reads `.miller/handoffs/latest.md`.

Rejected lanes:

- **MCP tool:** rejected because no new code facts are needed. Existing Miller tools already expose the valuable data.
- **CLI command:** rejected for the first slice because the target user is an agent in an MCP session, and a skill can orchestrate existing tools without adding process/API contract surface.
- **Goldfish-backed workflow:** rejected for this slice because checkpoints/briefs are session-memory products with their own boundaries. The handoff packet should be self-contained and explicitly agent-authored where session context is needed.
- **Razorback-owned skills:** rejected because this should benefit all Miller plugin users, not only the user's personal workflow stack.

## Packet Format

Packets are markdown files saved under `.miller/handoffs/`.

Filename:

```text
.miller/handoffs/YYYY-MM-DDTHH-MM-SSZ-<source>-to-<target>-<branch>-<short-head>.md
.miller/handoffs/latest.md
```

Use UTC timestamps. Sanitize branch and harness names for filenames.

Packet template:

```markdown
---
packet_format: miller-handoff-v1
workspace_root: /absolute/path
workspace_id: <miller workspace id or display id>
created_at_utc: 2026-06-28T00:00:00Z
source_harness: codex
target_harness: cursor
branch: main
head: 7bf576d
dirty_state: dirty|clean
index_built_revision: 1908
index_latest_revision: 1908
---

## Resume Prompt

<Target-harness-specific prompt the receiving agent can start with.>

## Current State

<Workspace status, branch, HEAD, dirty files, and concise state summary.>

## Changed Files

<git status --short, git diff --stat, and changed path list.>

## Impact

<Miller impact output for the working-tree diff, or explicit "no local diff".>

## Context Bundle

<Miller context output within the requested token budget.>

## Session Notes

<Agent-authored notes: what was tried, what failed, decisions made, constraints, and current intent.>

## Next Action

<The single most useful next action.>

## Source Pointers

<Files, plans, tests, commands, or Miller calls worth opening first.>

## Validation Checklist

- Same workspace root?
- Same branch?
- Same HEAD or acceptable drift?
- Dirty files match packet?
- Miller workspace fresh?
- Re-run impact if drifted?
```

## File Structure

- Create: `.agents/skills/handoff-out/SKILL.md` - producer workflow, packet template, required Miller calls, git capture, writing rules, target prompt guidance, and safety caveats.
- Create: `.agents/skills/handoff-in/SKILL.md` - consumer workflow, packet lookup, drift validation, fresh Miller checks, and resume summary format.
- Modify: `.agents/skills/miller-orientation/SKILL.md` - add a first-call row for switching harnesses/models or preparing/resuming a handoff.
- Generated: `skills/` - regenerated mirror from `.agents/skills/`.
- Modify: `tests/plugin/plugin-manifest.test.cjs` - add assertions that `handoff-out` and `handoff-in` are present, user-invocable, tool-scoped, and document `.miller/handoffs/`.
- Modify: `README.md` - document handoff skills in the agent/plugin workflow without implying a new MCP tool.
- Modify: `docs/site/index.html` - public site copy for handoff skills if the skills/features section lists workflow capabilities.
- Modify: `docs/README.md` - add the plan to the docs map only if this repo's current docs map expects active plans to be listed.

## Task 1: Add `handoff-out` Skill

**Files:**
- Create: `.agents/skills/handoff-out/SKILL.md`
- Test: `tests/plugin/plugin-manifest.test.cjs`

**Interfaces:**
- Consumes: existing Miller MCP tools `workspace`, `impact`, `context`, optional `search`/`inspect`/`trace`, plus local git state from shell.
- Produces: a markdown packet at `.miller/handoffs/<timestamp>-<source>-to-<target>-<branch>-<short-head>.md` and `.miller/handoffs/latest.md`.

**What to build:** A user-invocable skill that creates a portable handoff packet for another harness/model. It should gather current workspace facts, local git state, impact analysis for the current diff, a bounded context bundle, and explicit session notes from the active agent.

**Approach:** The skill should run `workspace(status)` and `workspace(health)` first, then capture git root/branch/HEAD/status/diff stat/changed files. If the worktree is dirty, run `impact(git=true)`; otherwise state that there is no local diff and use the supplied goal/session notes to drive `context(query=..., token_budget=...)`. The skill should ask the active agent to include concise session notes from the current conversation, but must not call Goldfish or claim to recover durable memory.

**Acceptance criteria:**
- [x] Skill metadata name is exactly `handoff-out`.
- [x] Skill is user-invocable.
- [x] Skill allowed-tools include Miller workspace, impact, context, search, inspect, trace, and shell/git access.
- [x] Workflow writes a timestamped packet under `.miller/handoffs/`.
- [x] Workflow updates `.miller/handoffs/latest.md`.
- [x] Packet contains stable sections for resume prompt, state, changed files, impact, context, session notes, next action, source pointers, and validation checklist.
- [x] Skill explicitly warns not to include secrets in session notes.
- [x] Skill explicitly says Goldfish is not required and is not called automatically.
- [x] Worker-scope verification passes, committed.

## Task 2: Add `handoff-in` Skill

**Files:**
- Create: `.agents/skills/handoff-in/SKILL.md`
- Test: `tests/plugin/plugin-manifest.test.cjs`

**Interfaces:**
- Consumes: packet markdown from `.miller/handoffs/latest.md` or an explicit path, current git state, current Miller workspace status/health, and optional fresh Miller `impact`.
- Produces: a concise receiving-agent resume summary with drift status and next actions.

**What to build:** A user-invocable skill that reads a handoff packet, validates whether the current workspace still matches it, and tells the receiving agent how to continue safely.

**Approach:** The skill should default to `.miller/handoffs/latest.md`, parse the frontmatter enough to compare workspace root, branch, HEAD, and dirty state, then run fresh `workspace(status)`/`workspace(health)`. If `HEAD`, branch, or dirty files drift, the skill should say so plainly and rerun `impact(git=true)` when there is a current diff. It should not blindly trust stale packet context.

**Acceptance criteria:**
- [x] Skill metadata name is exactly `handoff-in`.
- [x] Skill is user-invocable.
- [x] Skill defaults to `.miller/handoffs/latest.md` when no path is supplied.
- [x] Skill validates workspace root, branch, HEAD, dirty files, and Miller freshness.
- [x] Skill distinguishes safe-to-resume, drifted-but-resumable, and blocked states.
- [x] Skill reruns Miller checks when packet drift makes old impact/context stale.
- [x] Skill outputs a compact receiving-agent summary, not a dump of the whole packet.
- [x] Worker-scope verification passes, committed.

## Task 3: Wire Skill Discovery, Mirrors, And Plugin Tests

**Files:**
- Modify: `.agents/skills/miller-orientation/SKILL.md`
- Generated: `skills/`
- Modify: `tests/plugin/plugin-manifest.test.cjs`

**Interfaces:**
- Consumes: Task 1 and Task 2 skill files.
- Produces: mirrored plugin skills and regression tests that keep the handoff skills packaged.

**What to build:** Make the new skills discoverable in the Miller plugin and keep the generated `skills/` mirror byte-for-byte aligned.

**Approach:** Add one `miller-orientation` row for "handoff to another harness/model" pointing to `handoff-out`, and one row for "resume from handoff packet" pointing to `handoff-in`. Run `scripts/sync-plugin-skills.sh`. Add plugin tests that assert both skill files exist in `.agents/skills/` and `skills/`, have `user-invocable: true`, include `.miller/handoffs/`, and do not use the `miller-` prefix.

**Acceptance criteria:**
- [x] `scripts/sync-plugin-skills.sh` regenerates `skills/`.
- [x] `diff -qr .agents/skills skills` reports no differences.
- [x] Plugin tests fail if either handoff skill is missing from the mirror.
- [x] Plugin tests fail if either handoff skill is named with a `miller-` prefix.
- [x] `miller-orientation` points harness-switching users to the handoff skills.
- [x] Worker-scope verification passes, committed.

## Task 4: Update Public Guidance

**Files:**
- Modify: `README.md`
- Modify: `docs/site/index.html`
- Modify: `docs/README.md` only if needed by the docs map convention

**Interfaces:**
- Consumes: final skill names and packet location from Tasks 1-3.
- Produces: public guidance that tells Miller plugin users the feature exists without implying a new server tool.

**What to build:** Add concise documentation for handoff skills as part of Miller's agent workflow.

**Approach:** Mention that Miller ships handoff skills for moving between Codex, Cursor, Claude, and other harnesses. Explain that packets are markdown under `.miller/handoffs/`, use existing Miller tools, and are not committed by default. Do not add a README tool list entry that looks like an MCP tool.

**Acceptance criteria:**
- [x] README mentions `handoff-out` and `handoff-in`.
- [x] README says packets live in `.miller/handoffs/`.
- [x] README does not describe handoff as an MCP tool or CLI command.
- [x] GitHub Pages copy matches the same product boundary.
- [x] Docs map is updated if the new plan needs listing.
- [x] Worker-scope verification passes, committed.

## Task 5: Dogfood And Capture Evidence

**Files:**
- Create: `docs/findings/2026-06-28-handoff-skills-dogfood.md`

**Interfaces:**
- Consumes: implemented skills and current Miller MCP session.
- Produces: dogfood evidence from creating and consuming a handoff packet.

**What to build:** Prove the workflow with a real local packet in `.miller/handoffs/`, but do not commit that packet. Commit only a short findings document with the evidence and any limitations.

**Approach:** Run `handoff-out` in the Miller repo with a target such as Cursor and a small session note. Confirm `.miller/handoffs/latest.md` exists locally and includes the expected sections. Run `handoff-in` against it and record whether it correctly validates the current workspace. Since `.miller/` is ignored, do not force-add the packet.

**Acceptance criteria:**
- [x] `handoff-out` creates a local packet under `.miller/handoffs/`.
- [x] `handoff-out` updates `.miller/handoffs/latest.md`.
- [x] Packet includes Miller impact and context sections or explicit no-diff/no-context reasons.
- [x] `handoff-in` reads the packet and reports validation state.
- [x] Findings doc records what was tested and what remains future work.
- [x] No `.miller/handoffs/` packet is committed.
- [x] Worker-scope verification passes, committed.

## Verification Strategy

**Project source of truth:** `AGENTS.md`, `CLAUDE.md`, `tests/plugin/plugin-manifest.test.cjs`, `scripts/sync-plugin-skills.sh`, and `scripts/test.sh`.

**Worker red/green scope:** For skill/package changes, run:

```bash
scripts/sync-plugin-skills.sh
diff -qr .agents/skills skills
node --test tests/plugin/plugin-manifest.test.cjs
git diff --check
```

**Worker ceiling:** Workers may also run `scripts/test.sh` for confidence. Workers should not run Scale tests because this plan does not touch extractor, indexing subprocess, or server runtime behavior.

**Worker gate invariant:** The worker gate proves the skills are packaged, mirrored, named correctly, documented with the `.miller/handoffs/` packet location, and free of markdown/trailing-whitespace mistakes.

**Lead affected-change scope:** After the skill/docs batch, run:

```bash
scripts/sync-plugin-skills.sh
diff -qr .agents/skills skills
node --test tests/plugin/plugin-manifest.test.cjs
git diff --check
```

Then run the dogfood `handoff-out`/`handoff-in` flow and inspect the generated local packet.

**Branch gate:** Before merge or handoff, run:

```bash
scripts/test.sh
```

**Replay/metric evidence:** The hard gate is successful skill packaging and dogfood packet validation. The exact packet content length, target harness wording, and context usefulness are report-only observations for future tuning.

**Escalation triggers:** If dogfood shows that existing Miller `impact` or `context` cannot provide useful packet data, stop and report a product gap instead of adding a new MCP/CLI surface inside this plan. If packet validation needs structured parsing beyond markdown/frontmatter conventions, split that into a future design.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp in the task summary or checkpoint. If the same HEAD already has a passing ledger entry for the required scope, reuse that evidence instead of rerunning the same expensive gate.

## Model Routing

**Project source of truth:** No repo-root `RAZORBACK.md` exists. Use inherited harness defaults unless the user specifies a reviewer or model.

**Strategy tier:** Planning, architecture, decomposition, lead review, finding triage.
- Harness mapping: inherit.

**Implementation tier:** Bounded worker tasks from a clear plan.
- Harness mapping: inherit.

**Mechanical tier:** Docs, skill text, fixtures, manifests, formatting, and mirror sync with no test/replay interpretation.
- Harness mapping: inherit.

**Gate-interpretation reviewer:** Reading the plan, failing test, dogfood output, and diff to decide whether skill text or test expectations are wrong.
- Harness mapping: inherit.

**Escalation tier:** Security-sensitive packet content, repeated dogfood failures, ambiguous product boundary, or requests to add MCP/CLI surfaces.
- Harness mapping: inherit.

**Worker eligibility:** Implementation-tier workers may edit skill docs, plugin tests, README/site copy, and findings docs when the plan's file ownership is clear.

**Escalation triggers:** Escalate if an implementation requires Miller server code, a new MCP tool, a new CLI command, Goldfish integration, or committed packet artifacts.

**Mechanical exclusion:** Mechanical workers cannot own dogfood evidence interpretation or decide to change the product boundary.

**Unsupported harness behavior:** If the harness cannot choose models per agent, use `inherit`, note it in the run summary, and continue.
