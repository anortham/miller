# Task 7 brief — JVM sbt backend

## Objective

Implement the serialized Task 7 slice from the approved plan: add an sbt 1.x backend behind the
existing JVM provider seam, register framework key `sbt`, and prove class-level discovery and runs
without writing build output into the source tree.

## Ownership

- Create `src/Miller.Testing/Providers/Jvm/SbtTestBackend.cs`.
- Modify only the sbt/JVM registration seams in
  `src/Miller.Testing/Providers/Jvm/JvmTestProvider.cs` and
  `src/Miller.Testing/Daemon/ContinuousTestProviderFactory.cs`.
- Modify `tests/Miller.Tests/Testing/CtProviderTestSupport.cs` only for sbt Scale prerequisites.
- Create `tests/Miller.Tests/Testing/Providers/Jvm/SbtTestBackendTests.cs` and extend the existing
  JVM Scale smoke file with the sbt case.
- Append `task-7-report.md`; do not edit plan acceptance or the progress ledger.

## Dependencies and fixed decisions

- Reuse `IJvmTestBackend`, `JvmTestProvider`, `JvmTestTooling`, and `JUnitXmlResultParser`; do not
  create another provider shell or parser.
- Product support floor is sbt 1.x.
- Prefer the documented `show Test/definedTestNames` class-name listing. If official evidence or
  a real probe proves that output cannot be parsed fail-closed, use compile plus generation-local
  test-class scanning and record the plan-consistent substitution in the task report and findings.
- Focused selection is one sbt process using `testOnly` with whitespace-separated fully-qualified
  class names. Discovery and each run are one process, never one process per class.
- Cases are class-level and use the shared Maven class sentinel convention.
- Parse only JUnit XML reports contained by the generation-owned sbt report directory. Aggregate
  method rows into one class verdict; missing selected classes are failures, and unexpected rows
  are rejected on partial runs.
- Wrapper preference is project/workspace `sbt` launcher before PATH `sbt` when a conventional
  wrapper is present; prove the actual existing repository pattern rather than inventing one.
- All sbt target, boot, dependency-cache, report, and temporary output must be generation-local.
  The Scale test hashes the source tree and rejects `target`, `project/target`, `bin`, and `obj`.
  Do not overwrite `SBT_OPTS`, `JAVA_OPTS`, or project test configuration.
- Do not add an MCP tool or change public CT contracts.

## Acceptance

- Fast tests cover listing parsing or the documented fallback scan, wrapper/PATH resolution,
  generation-local command construction, `testOnly` selection, chunk/batch behavior, report
  aggregation, missing/unexpected results, and malformed input.
- A Scale smoke builds two ScalaTest or munit classes when both sbt and a JDK compiler are present;
  otherwise it skips with the shared support guards.
- Focused Task 7 tests pass, the exact sbt Scale smoke passes or honestly skips, Release build has
  zero warnings/errors, `git diff --check` is clean, and Miller post-edit impact is recorded.
- Commit implementation and task report. Report the worktree path, branch, commit(s), dirty state,
  exact verification, runtime/tool versions, and any remaining runtime uncertainty.

## Worker rules

- You are not alone in the codebase. Preserve all prior Task 1–6 commits and do not revert others.
- Use Miller for all exploration, impact before edits, TDD red-green, official current sbt docs for
  external behavior, focused tests only, and no full suite.
- Do not broaden scope, make product/architecture decisions outside this brief, or spawn agents.
