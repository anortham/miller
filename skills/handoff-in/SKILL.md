---
name: handoff-in
description: Use when resuming Miller-backed work from a handoff packet or validating a packet before continuing in another harness, model, or session.
user-invocable: true
arguments: "[packet path]"
allowed-tools: mcp__miller__workspace, mcp__miller__impact, mcp__miller__context, mcp__miller__search, mcp__miller__inspect, mcp__miller__trace, Bash
---

# Handoff In

Read a Miller handoff packet, validate it against the current workspace, and produce a compact resume summary. Do not blindly trust stale packet context.

## Workspace targeting (required)

Every workspace-bound Miller MCP call names its target with `workspace_id`; Miller never infers it from the
launch directory, environment variables, MCP Roots, or a previous call. Get the ID from
`workspace(operation="list")`, or from `workspace(operation="open", path="/absolute/project")` when the repo is
absent. The examples below write it as `workspace_id="<id>"`. Only `workspace` `list`, `open`, `remove`,
`prune`, and `dashboard` run without one; `current` and `primary` are CLI-only. The full targeting rules live
in the `miller-orientation` skill.

## Inputs

If no packet path is supplied, read:

```text
.miller/handoffs/latest.md
```

Packets should use `packet_format: miller-handoff-v1` and live under `.miller/handoffs/`.

## Workflow

1. Read the packet path and frontmatter. Extract at least:

- `workspace_root`
- `workspace_id`
- `branch`
- `head`
- `dirty_state`
- `index_built_revision`
- `index_latest_revision`

Also extract the packet's changed-file list from the `Changed Files` section, especially the `git status --short` and `git diff --name-only` blocks.

2. Collect current facts:

```text
workspace(workspace_id="<id>", operation="status", format="json")
workspace(workspace_id="<id>", operation="health")
```

```bash
git rev-parse --show-toplevel
git branch --show-current
git rev-parse --short HEAD
git status --short
git diff --stat
git diff --name-only
```

3. Compare packet facts to current facts:

- Workspace root
- Branch
- HEAD
- Dirty state
- Exact changed-file list from `git status --short`
- Exact tracked changed-file list from `git diff --name-only`
- Miller `index_built_revision` / `index_latest_revision` from `index.built_revision` and `index.latest_revision`

| Status | Meaning | Action |
|---|---|---|
| `safe-to-resume` | Same workspace root, same branch, same HEAD, matching dirty state, matching changed-file list, and Miller health is fresh enough. | Use packet impact/context and continue from `Next Action`. |
| `drifted-but-resumable` | Same workspace root, but branch, HEAD, dirty state, changed-file list, or Miller index revisions drifted in an explainable way. | Say what drifted, rerun Miller checks, then continue with updated facts. |
| `blocked` | Packet is missing, root points at a different repo, Miller health is unusable, or drift changes the product intent. | Stop and ask for the smallest needed decision. |

When there is a current dirty diff, rerun:

```text
impact(workspace_id="<id>", git=true)
```

When packet context is stale or the next action is vague, rerun:

```text
context(workspace_id="<id>", query="<packet goal or next action>", token_budget=<budget>)
```

## Resume Summary

Output a compact summary, not a dump of the whole packet:

```markdown
## Handoff Intake

status: safe-to-resume | drifted-but-resumable | blocked
packet: <path>
workspace: <current root>
branch/head: <branch> <head>
drift: <none or concrete differences>
miller: <fresh/needs refresh/problem>

## Continue From

<One-paragraph session note summary.>

## Next Action

<The next command, file, test, or Miller call to run.>
```

## Rules

- Validate workspace root before using packet instructions.
- Treat branch, HEAD, dirty-state, changed-file list, or Miller revision drift as a reason to re-check impact/context, not as proof the packet is useless.
- Do not call Goldfish automatically.
- Do not include secrets from the packet in the chat transcript.
- If the packet points at `.miller/handoffs/latest.md` but it is missing, report `blocked` and ask for the packet path.
