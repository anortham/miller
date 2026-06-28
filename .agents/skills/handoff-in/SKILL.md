---
name: handoff-in
description: Use when resuming Miller-backed work from a handoff packet or validating a packet before continuing in another harness, model, or session.
user-invocable: true
arguments: "[packet path]"
allowed-tools: mcp__miller__workspace, mcp__miller__impact, mcp__miller__context, mcp__miller__search, mcp__miller__inspect, mcp__miller__trace, Bash
---

# Handoff In

Read a Miller handoff packet, validate it against the current workspace, and produce a compact resume summary. Do not blindly trust stale packet context.

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
- `index_revision`

2. Collect current facts:

```text
workspace(operation="status")
workspace(operation="health")
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

| Status | Meaning | Action |
|---|---|---|
| `safe-to-resume` | Same workspace root, same branch, same HEAD, matching dirty state, and Miller health is fresh enough. | Use packet impact/context and continue from `Next Action`. |
| `drifted-but-resumable` | Same workspace root, but branch, same HEAD check, dirty files, or index revision drifted in an explainable way. | Say what drifted, rerun Miller checks, then continue with updated facts. |
| `blocked` | Packet is missing, root points at a different repo, Miller health is unusable, or drift changes the product intent. | Stop and ask for the smallest needed decision. |

When there is a current dirty diff, rerun:

```text
impact(git=true)
```

When packet context is stale or the next action is vague, rerun:

```text
context(query="<packet goal or next action>", token_budget=<budget>)
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
- Treat branch or same HEAD drift as a reason to re-check impact/context, not as proof the packet is useless.
- Do not call Goldfish automatically.
- Do not include secrets from the packet in the chat transcript.
- If the packet points at `.miller/handoffs/latest.md` but it is missing, report `blocked` and ask for the packet path.
