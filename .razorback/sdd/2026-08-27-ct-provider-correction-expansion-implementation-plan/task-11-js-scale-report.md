# Task 11 — JavaScript Node result-attribution correction

## Outcome

Node test runs now isolate every known file-level selection to its own invocation and
JUnit artifact. This is required because Node 22's built-in JUnit reporter does not emit
source file attributes. `ParseNodeJunit` also refuses to copy an unattributed report
across multiple selected IDs. Jest and Vitest keep their existing batching and shared
argv-chunking behavior.

## TDD evidence

Red tests were added before the production change:

```text
dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter 'FullyQualifiedName~BuildRunCommands_splits_selected_node_test_files_into_one_invocation_each|FullyQualifiedName~BuildRunCommands_splits_a_whole_suite_node_test_selection_into_one_invocation_each|FullyQualifiedName~Run_refuses_an_unattributed_node_junit_report_for_multiple_ids' --no-restore
```

The first run failed as intended: selected and whole-suite Node requests each produced
one batched command, and the old parser copied the no-file JUnit aggregate instead of
throwing for multiple IDs.

The same three tests passed after the implementation (3/3). The focused JavaScript
provider class passed 55/55, including the existing Jest/Vitest chunking and result-merge
tests:

```text
dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter 'FullyQualifiedName~JavaScriptTestProviderTests' --no-restore
```

The discovery class passed 36/36:

```text
dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter 'FullyQualifiedName~JsFrameworkTestFileDiscoveryTests' --no-restore
```

The native Node regression passed 1/1:

```text
dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter 'FullyQualifiedName~JavaScriptProviderScaleTests.Node_smoke_marks_only_the_failing_file_red_in_a_partially_red_suite' --no-restore
```

The full Scale suite and bare fast suite were not run in this packet.

## Miller/API evidence

- `context` and `workspace onboarding` oriented the Node provider and ranked the failing
  Scale fixture.
- `search` located `BuildRunInvocations`, `BuildInvocation`, `MergeRuns`,
  `ParseNodeJunit`, the existing provider tests, and the Scale test.
- `inspect` (overview/full) established the call/data flow:
  `RunAsync` → `RunInGenerationAsync` → `MergeRuns` → `ParseResultArtifact` →
  `ParseNodeJunit` → `JunitTestResultParser.Parse`.
- `trace mode=refs` confirmed `BuildRunInvocations` is used by production runs and the
  command-preview seams, while `ParseNodeJunit` is called from `ParseResultArtifact`.
- `impact` after editing identified the provider run/preview methods and the JavaScript
  provider test class as the affected scope. No Jest/Vitest source path was changed.
- Miller edit previews were used before applying the provider body, parser guard, docs,
  and test updates. A local-name collision caught by the first green compile was fixed
  before rerunning the red tests.
- Official Node v22.23.2 evidence remains the diagnostic basis:
  [built-in JUnit reporter source](https://raw.githubusercontent.com/nodejs/node/v22.23.2/lib/internal/test_runner/reporter/junit.js)
  writes `name`, `time`, and `classname` but not `event.data.file`; the
  [test runner docs](https://nodejs.org/download/release/latest-jod/docs/api/test.html#event-testfail)
  expose file-aware events but warn that built-in reporter output is not a stable
  programmatic contract.

## Invariants preserved

- A Node request whose IDs decode to known files creates one invocation per distinct,
  normalized, sorted file. Each invocation carries only that file's known IDs, except
  unknown IDs retain their prior first-invocation placement so they cannot disappear
  silently.
- `WholeSuite=true` does not bypass this isolation when the request carries the full
  known file selection. A Node request with no known file IDs retains the existing single
  unfiltered invocation.
- A one-file Node invocation keeps the unsuffixed result artifact name; multi-file Node
  requests receive distinct `.part` artifacts. Runs remain sequential so artifacts and
  shared package caches cannot race.
- An unattributed Node JUnit report is aggregated only for one selected ID. Any
  multi-ID report containing unattributed rows throws an actionable provider exception
  instead of producing false per-file verdicts.
- A valid empty Node artifact remains unreported, allowing the coordinator's existing
  missing-case fail-safe to act. The synthetic selected-case failure fallback still
  applies to a genuinely missing artifact and remains unchanged for Jest/Vitest.
- Existing file-aware JUnit parsing remains supported for alternate reporters.

## Judgment calls

- The correction isolates Node invocations instead of parsing failure stack text or
  inventing a new reporter protocol. Stack text cannot identify green files, and Node
  explicitly treats reporter output as unstable.
- The empty-artifact fallback exception is scoped to `node-test`; this avoids changing
  Jest/Vitest failure semantics while preserving the existing “unreported file” test
  under one-file Node execution.
- The existing `Run_attributes_a_node_junit_failure_to_the_file_that_failed` fixture is
  now documented as a file-aware alternate-reporter fixture. The native Scale test
  remains the no-file Node reporter proof.

## Commit and worktree

- Commit: `10ed419f25f04d802145f1811a8b88fa6b1b4adf` (`fix(ct): isolate node test result attribution`).
- Path: `/home/murphy/source/miller`
- Branch: `feature/ct-provider-correction-expansion`
- Base preserved: `8671c578` (`test(ct): restore file evidence in engine scale fixture`)
- Final HEAD: `10ed419f25f04d802145f1811a8b88fa6b1b4adf`.
- Final status: tracked worktree clean; this report is ignored by the repository's
  `.gitignore` but remains at this path for the lead.
