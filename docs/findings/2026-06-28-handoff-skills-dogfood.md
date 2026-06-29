# Handoff Skills Dogfood Evidence

Date: 2026-06-28
Workspace: `/Users/murphy/source/miller/.worktrees/handoff-skills`
Branch: `codex/handoff-skills`
HEAD: `7bf576d`

## What Was Tested

Dogfooded the new Miller-provided `handoff-out` / `handoff-in` skill workflow without adding an MCP tool or CLI command.

The packet was written locally to:

```text
.miller/handoffs/2026-06-28T23-34-43Z-codex-to-cursor-codex-handoff-skills-7bf576d.md
.miller/handoffs/latest.md
```

The packet is intentionally not committed.

## Handoff Out Result

Collected:

- Miller `workspace(status)`: fresh revision 2 at packet creation, current `search_db`, current `content_db`, queue empty. The skill now records the JSON fields as `index_built_revision` and `index_latest_revision`.
- Miller `workspace(health)`: usable with warnings due to 21 parse diagnostics; telemetry errors were 0.
- Git root, branch, HEAD, status, diff stat, and changed path list.
- Miller `impact(git=true)` for the tracked working-tree diff.
- Miller `context(...)` with a handoff-skills implementation query and a 1,800-token budget.

The generated packet included all required stable sections:

- Resume Prompt
- Current State
- Changed Files
- Impact
- Context Bundle
- Session Notes
- Next Action
- Source Pointers
- Validation Checklist

## Handoff In Result

Validation against current workspace facts:

- Workspace root matched.
- Branch matched: `codex/handoff-skills`.
- HEAD matched: `7bf576d`.
- Dirty state matched: dirty.
- Miller remained fresh, but the index moved from built/latest revision 2 to built/latest revision 3 after packet creation.

Result classification: `drifted-but-resumable`.

The drift is expected because writing and then validating the local packet advanced the workspace index. The receiving workflow should keep the packet, note the revision drift, compare the changed-file lists, and rerun Miller checks when using impact/context.

## Observed Limitation

`impact(git=true)` reported tracked-diff impact only. At the time of dogfood, the new skill directories and plan file were still untracked, so they appeared in `git status --short` but not in `git diff --stat` or the tracked-diff impact result.

This is acceptable for this slice because `handoff-out` records both `git status --short` and `impact(git=true)`. Future tuning could mention explicitly that untracked files are represented by git status until staged or tracked.

## Verification Evidence

- `scripts/sync-plugin-skills.sh`: passed.
- `diff -qr .agents/skills skills`: no differences.
- `node --test tests/plugin/plugin-manifest.test.cjs`: passed, 10 tests.
- `.miller/handoffs/latest.md`: existed and was non-empty.
- `git check-ignore -q .miller`: passed, confirming the local packet directory is ignored.
