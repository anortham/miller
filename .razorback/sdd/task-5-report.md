# Task 5 Report: Docs, Agent Guidance, And Contract Text

## Status

Task 5 implementation is complete in the working tree. I did not commit, per the task instruction for this worker.

## Files Changed

- `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`
- `docs/contracts/trace-json-v1.md`
- `README.md`
- `skills/miller-bridge-trace/SKILL.md`
- `.agents/skills/miller-bridge-trace/SKILL.md`
- `tests/Miller.Tests/Server/AgentInstructionsTests.cs`
- `.razorback/sdd/task-5-report.md`

## Miller Calls Used

- `workspace status`: confirmed `/Users/murphy/source/miller` was fresh before and after edits; final status was revision 40 with current `search_db` and `content_db`.
- `context(query="Task 5 docs and agent guidance ...")`: found the approved Task 5 plan entry and the relevant contract/test surfaces.
- `search(mode=file, query="RAZORBACK.md")`: found no indexed `RAZORBACK.md`; repo-specific policy came from the task brief and AGENTS guidance.
- `search(mode=file, query=<plan/brief filenames>)`: exact file-mode lookup did not find the hidden/new plan artifacts, so I used the explicit task paths for bounded shell reads.
- `inspect(target="Task 5: Docs, Agent Guidance, And Contract Text", scope=...)`: confirmed allowed files, interfaces, and acceptance criteria from the plan.
- `inspect(target="AgentInstructionsTests", depth="full")`: confirmed the budget/recovery guidance assertions and the old dotnet-web-only assertion to update.
- `inspect` on the target markdown files: confirmed these are prompt/docs surfaces without code symbols before bounded text reads.
- `search(mode=content, query="dotnet-web")`: located old dotnet-web-only wording in server instructions, README, and both bridge-trace skills.
- `impact(changed_paths=[...])`: scoped the requested docs/test files before editing.
- `impact()` after editing: mapped the full dirty worktree; it included pre-existing Tasks 1-4 changes, with `AgentInstructionsTests` among likely tests for this worker slice.
- `search(mode=content, query="navigates_to")` and `search(mode=content, query="next_route")`: confirmed the trace JSON contract now documents both stable values.
- `search(mode=content, query="nextjs.route_reference.v1")`: confirmed the bridge-trace skill now tells agents to check `patterns` when Next route facts are missing.

## Verification

- Command: `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter "FullyQualifiedName~AgentInstructionsTests"`
  - Result: passed.
  - Evidence: 41 passed, 0 failed, 0 skipped.
  - Invariant proved: embedded server instructions remain non-empty, documented, current with the pinned trace guidance, and within instruction/tool description budgets.
- Command: `git diff --check`
  - Result: passed.
  - Invariant proved: no whitespace errors in the combined dirty diff.

## Acceptance Criteria Checklist

- [x] Agent instructions no longer say bridge mode is only a `dotnet-web` chain.
- [x] README describes `nextjs` support without implying all-framework bridge coverage.
- [x] JSON contract documents `navigates_to` and `next_route`.
- [x] Bridge trace skill tells agents to use `patterns` when Next route facts are missing.
- [x] Agent instruction tests pass.
- [x] Worker-scope verification passes.
- [x] No commit was made, per this worker instruction.

## Concerns / Plan Mismatch

- The plan acceptance text says changes are committed, but this worker brief explicitly says `DO NOT commit`; I followed the worker instruction.
- Miller content search still finds older dotnet-web-only wording in historical release notes/plans, `docs/site/index.html`, and the separate `miller-orientation` skill copies. Historical release notes/plans were explicitly out of scope, and `docs/site/index.html` plus `miller-orientation` were not in the allowed Task 5 file list, so I did not modify them.

## Lead Follow-Up

- Updated current-facing stragglers outside the original worker write list: `docs/site/index.html`, `skills/miller-orientation/SKILL.md`, and `.agents/skills/miller-orientation/SKILL.md`.
- Shortened `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` after the first lead run exceeded the 12,000-character budget by 8 characters.
- Re-ran `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter "FullyQualifiedName~AgentInstructionsTests"`: 41 passed, 0 failed.
- Re-ran `git diff --check`: passed.
