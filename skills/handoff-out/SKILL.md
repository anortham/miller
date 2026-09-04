---
name: handoff-out
description: Use when preparing to move active Miller-backed work to another harness, model, or session and the receiving agent needs a self-contained resume packet.
user-invocable: true
arguments: "<target harness/model> [goal, next action, session notes, token budget]"
allowed-tools: mcp__miller__workspace, mcp__miller__impact, mcp__miller__context, mcp__miller__search, mcp__miller__inspect, mcp__miller__trace, Bash
---

# Handoff Out

Create a local markdown handoff packet from current workspace facts, Miller context, impact analysis, and explicit session notes. This is a skill workflow, not a Miller MCP tool or CLI command.

## Workspace targeting (required)

Every workspace-bound Miller MCP call names its target with `workspace_id`; Miller never infers it from the
launch directory, environment variables, MCP Roots, or a previous call. Get the ID from
`workspace(operation="list")`, or from `workspace(operation="open", path="/absolute/project")` when the repo is
absent. The examples below write it as `workspace_id="<id>"`. Only `workspace` `list`, `open`, `remove`,
`prune`, and `dashboard` run without one; `current` and `primary` are CLI-only. The full targeting rules live
in the `miller-orientation` skill.

## Rules

- Store packets under `.miller/handoffs/`; write both a timestamped packet and `.miller/handoffs/latest.md`.
- Goldfish is not required and is not called automatically. Include only session context the active agent can state directly.
- Do not include secrets, credentials, tokens, private keys, customer data, or raw environment dumps in session notes or copied output.
- Keep the packet actionable: current state, changed files, impact, context, next action, and validation checklist.
- Do not commit `.miller/handoffs/` packets.

## Workflow

1. Confirm Miller freshness:

```text
workspace(workspace_id="<id>", operation="status", format="json")
workspace(workspace_id="<id>", operation="health")
```

If `health` reports stale, missing, or corrupt sidecars, run `workspace(operation="refresh")` before collecting packet evidence. From JSON status, record `index.built_revision` and `index.latest_revision` as `index_built_revision` and `index_latest_revision`; do not invent a generic revision field.

2. Capture local git facts with shell/git:

```bash
git rev-parse --show-toplevel
git branch --show-current
git rev-parse --short HEAD
git status --short
git diff --stat
git diff --name-only
```

3. Capture Miller impact and context:

```text
impact(workspace_id="<id>", git=true)
context(workspace_id="<id>", query="<goal, changed area, or next action>", token_budget=<budget>)
```

If the worktree is clean, write `no local diff` in the Impact section and use the goal/session notes to drive `context(...)`.

4. Write the packet under `.miller/handoffs/`.

Filename shape:

```text
.miller/handoffs/YYYY-MM-DDTHH-MM-SSZ-<source>-to-<target>-<branch>-<short-head>.md
.miller/handoffs/latest.md
```

Use UTC time. Sanitize harness and branch names for filenames.

## Packet Template

```markdown
---
packet_format: miller-handoff-v1
workspace_root: /absolute/path
workspace_id: <miller workspace id or display id>
created_at_utc: 2026-06-28T00:00:00Z
source_harness: <source harness/model>
target_harness: <target harness/model>
branch: <branch>
head: <short head>
dirty_state: dirty|clean
index_built_revision: <workspace status index.built_revision>
index_latest_revision: <workspace status index.latest_revision>
---

## Resume Prompt

<Prompt the receiving agent can start with.>

## Current State

<Workspace status, branch, HEAD, dirty files, and concise state summary.>

## Changed Files

### git status --short

```text
<exact git status --short output, or "clean">
```

### git diff --name-only

```text
<exact git diff --name-only output, or "none">
```

### git diff --stat

```text
<exact git diff --stat output, or "none">
```

## Impact

<Miller impact output for the working-tree diff, or explicit "no local diff".>

## Context Bundle

<Miller context output within the requested token budget.>

## Session Notes

<Agent-authored notes: what was tried, what failed, decisions made, constraints, and current intent. Do not include secrets.>

## Next Action

<The single most useful next action.>

## Source Pointers

<Files, plans, tests, commands, or Miller calls worth opening first.>

## Validation Checklist

- Same workspace root?
- Same branch?
- Same HEAD or acceptable drift?
- Dirty state and changed-file list match packet?
- Miller workspace fresh?
- Re-run impact if drifted?
```

## Final Check

Before reporting success:

```bash
test -s .miller/handoffs/latest.md
git check-ignore -q .miller
```

Report the timestamped packet path, whether the packet is safe for the target harness to read, and the next action. Do not paste the full packet unless the user asks.
