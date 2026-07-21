## Task 3: Exclude nested worktrees from full scan and incremental watching

**Owns:**

- `.julieignore`
- `src/Miller.Indexing/WatchPathFilter.cs`
- `tests/Miller.Tests/Indexing/WatchPathFilterTests.cs`
- one existing scale test file if required to prove extractor scope

**Red tests:**

1. `.claude/worktrees/example/src/A.cs` is rejected by the watcher.
2. Similar non-worktree `.claude` paths remain eligible.
3. Both slash styles and mixed case behave according to existing platform normalization rules.
4. A real full extract of this repository contains no `.claude/worktrees/**` rows.

**Implementation:**

- Add the precise nested-worktree path to `.julieignore`.
- Add segment-pair filtering for `.claude/worktrees` without ignoring all `.claude` content.
- Do not change `julie-extractors` in this task.

**Worker verification:** focused watcher tests plus the narrowest scale extract assertion available.

