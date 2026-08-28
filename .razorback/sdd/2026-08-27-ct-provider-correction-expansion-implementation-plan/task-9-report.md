# Task 9 report: documentation and Julie audit handoff

## Status

The documentation packet is implemented in the Miller worktree. The copied Julie finding is
byte-for-byte identical to its source. No Julie file was modified and no worktree was removed.
The owned-file commit is `e2a3d5fde42df9aba4ff294a85006001b207adb8`
(`docs(ct): publish provider expansion and Julie handoff`).

## Changed files

- `README.md` — public CT matrix, project discovery summary, and exact Go/QML/.NET limits.
- `docs/README.md` — pointers to the current matrix, audit, backlog, and implementation plan.
- `docs/continuous-testing.md` — authoritative provider matrix, discovery rules, evidence, and
  known limits.
- `docs/findings/2026-08-27-continuous-testing-language-readiness-audit.md` — exact copy of the
  Julie audit finding.
- `docs/findings/2026-08-27-continuous-testing-extractor-backlog.md` — dated, non-executable Julie
  backlog note.
- `docs/plans/2026-08-27-ct-provider-correction-expansion-implementation-plan.md` — MTP delimiter
  correction and Task 7/8 evidence checkboxes.
- `.razorback/sdd/2026-08-27-ct-provider-correction-expansion-implementation-plan/task-9-report.md`
  — this report.

## Final support matrix and limits

| Ecosystem | Framework | Evidence and boundary |
|---|---|---|
| .NET | `dotnet`, `xunit`, `nunit`, `mstest` | C#, VB.NET, and Razor family mapping; VSTest uses `dotnet test`, xUnit v3 uses its self-executing assembly, and MTP uses direct `dotnet exec`. xUnit v2 is detected and refused. |
| Rust | `cargo` | Cargo package execution and exact libtest names. |
| Python | `pytest` | Pytest project discovery and node IDs. |
| JavaScript/TypeScript | `vitest`, `jest`, `node-test` | JavaScript, TypeScript, JSX, and TSX dialect/path mapping; bounded literal config only; unsafe or unknown config/version evidence refuses. |
| QML/Qt | `qt-quick-test` | CMake/CTest with static registration and qmake/QTest with a generated `check` target; target-level qmake selection only. |
| Go | `go` | Go 1.24+, one project per `go.mod`, in-root `go.work` context, top-level `TestXxx` cases grouped by package. |

Go child `t.Run` identity, benchmarks, fuzz targets, examples, and function-level source paths
remain outside the V1 contract. MTP requires version evidence at or above 1.7, a proven framework
filter for selected runs, and a TRX report extension. CMake requires static Qt Quick Test and CTest
registration; qmake requires a generated `check` target. F#, Ruby, Java, PHP, and other unlisted
toolchains remain unsupported.

## Julie evidence and state

The source audit is:

`/home/murphy/source/julie-extractors/.claude/worktrees/ct-language-audit-plan/docs/findings/2026-08-27-continuous-testing-language-readiness-audit.md`

Source and destination checksum command:

`sha256sum docs/findings/2026-08-27-continuous-testing-language-readiness-audit.md /home/murphy/source/julie-extractors/.claude/worktrees/ct-language-audit-plan/docs/findings/2026-08-27-continuous-testing-language-readiness-audit.md`

Result for both paths: `9681e8a196b4a835534fb285846eedf6bc49f33ee3eb0f297ecba01ca02a6588`.
`cmp -s` returned exit 0. The audit source remains untracked in Julie because it is preserved
evidence; the Miller copy is an owned handoff artifact.

Julie audit worktree:

- Path: `/home/murphy/source/julie-extractors/.claude/worktrees/ct-language-audit-plan`
- Branch: `worktree-ct-language-audit-plan`
- HEAD: `2ea9b0daa2e736f9248d8caf4c475e47dea0d522`
- Status: untracked source audit and stale source plan only.

Julie main:

- Path: `/home/murphy/source/julie-extractors`
- Branch: `main`
- Status: exactly two unrelated untracked Goldfish files,
  `.memories/2026-08-27/194443_9d10.md` and `.memories/2026-08-27/222024_6a24.md`; neither was
  modified, staged, or removed.

The stale Julie plan is not executable as written. The Miller backlog note at
`docs/findings/2026-08-27-continuous-testing-extractor-backlog.md:5-16` records the stale
contract/helper warning and points to live Miller contracts. Its only verified remaining Julie
items at `:18-26` are Go `t.Run` child identity, F# extractor/capability evidence, Scala
parameterized/teardown evidence, and R testthat lifecycle evidence. No Julie worktree was removed.

## Miller calls and contract evidence

Local Miller calls: `workspace onboarding`, `workspace health`, `workspace status`,
`context`, `search` in content/source/symbol/file modes, `inspect` for README/docs and provider
symbols, `impact` before and after the documentation diff, and `workspace refresh` status checks.
The final impact call seeded the five changed documentation/plan paths and returned no dependent
symbols or test candidates because the edits are documentation-only.

Cross-workspace Miller calls used selector `ct-language-audit-plan-4be9df540587`:
`workspace status` and content searches for the audit, stale plan, Go `t.Run`, Scala, and R
evidence. The cross-workspace index proved the source paths and returned the finding at
`docs/findings/2026-08-27-continuous-testing-language-readiness-audit.md:154` and the stale plan.
The source file was then bounded-read for checksum-preserving application.

Provider contract evidence used for the matrix came from committed task reports and current
indexed symbols:

- `ContinuousTestProviderFactory` registers the .NET, Rust, JavaScript, Python, QML, and Go
  framework groups (factory source `src/Miller.Testing/Daemon/ContinuousTestProviderFactory.cs:44-105`).
- `ContinuousTestProjectInventory.Discover` is the caller-facing project inventory
  (`src/Miller.Testing/Daemon/ContinuousTestProjectInventory.cs:137-196`); the Task 5 report
  proves qmake `qmltestcase`/`qmltest + testcase` and generated `check` gating, and the Task 8
  report proves one project per module plus in-root `go.work` context.
- `JavaScriptTestProvider` and its bounded config/discovery helpers prove Jest/Vitest/Node
  behavior and fail-closed config/version handling (`src/Miller.Testing/Providers/Node`).
- `QtQuickTestProvider` delegates to CMake/CTest and qmake backends
  (`src/Miller.Testing/Providers/Qml/QtQuickTestProvider.cs:6-40`).
- `DotnetTestProvider`, `DotnetTestBackend`, and `MtpDotnetTestBackend` prove backend evidence,
  direct MTP invocation, filter capability, and TRX-only result handling
  (`src/Miller.Testing/Providers/Dotnet`).
- `GoTestProvider`, `GoTestTooling`, `GoTestListParser`, and `GoTestJsonParser` prove Go 1.24+,
  module/package grouping, top-level names, explicit environment isolation, and complete mixed
  JSON verdicts (`src/Miller.Testing/Providers/Go`).

The plan wording at
`docs/plans/2026-08-27-ct-provider-correction-expansion-implementation-plan.md:260` now says
native MTP filters are passed as direct application arguments to `dotnet exec <TargetPath>`;
the `--` delimiter belongs only to the `dotnet test` driver. Task 7 and Task 8 acceptance
checkboxes are marked at `:264-299` from their reports. Task 9's first four acceptance items are
marked at `:322-326`; its worker-verification checkbox stays open until the lead reconciles the
two stale CLI tests described below.

## Judgments

- `README.md:226` calls the third column “Verification evidence” because Go and qmake have guarded
  fixture proof rather than a live external-repository claim.
- `README.md:232-233` adds qmake/QTest and Go without claiming an unavailable Qt host or an
  unguarded qmake run.
- `README.md:240-259` names .NET project signals, Go module boundaries, MTP evidence gates, qmake
  target-level selection, and CMake static registration because those are the implemented
  caller-facing limits.
- `docs/continuous-testing.md:21-31` is the authoritative matrix. MTP is documented as a
  backend under existing .NET framework values, not as a new public framework key.
- `docs/continuous-testing.md:87-107` separates CMake and qmake QML recognition and states that
  external `GOWORK` is not inherited. This reflects inventory and tooling tests.
- `docs/continuous-testing.md:121-139` removes component extensions from JavaScript defaults and
  states that unsupported/truncated/malformed/interpolated config refuses; this prevents the old
  silent fallback contract from returning.
- `docs/continuous-testing.md:239-252` records the QML, MTP, and Go boundaries and preserves the
  direct-invocation argument distinction verified by the MTP Scale fixture and official docs.
- `docs/README.md:18-20,33` makes the audit, backlog, plan, and current operating doc discoverable.
- `docs/findings/2026-08-27-continuous-testing-extractor-backlog.md:5-31` is Miller-owned and
  deliberately non-executable; it does not copy stale Julie helpers or contract shapes.

## Verification

- `git diff --check` — passed.
- `sha256sum` and `cmp -s` — both hashes equal
  `9681e8a196b4a835534fb285846eedf6bc49f33ee3eb0f297ecba01ca02a6588`; compare exit 0.
- `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~AgentInstructionsTests" --no-restore`
  — 60 passed, 0 failed, 0 skipped. This is the documentation/guidance contract scope selected
  after Miller impact found no documentation dependents.
- The broader exploratory command
  `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~AgentInstructionsTests|FullyQualifiedName~TestsCliTests" --no-restore`
  — 108 passed, 2 failed, 0 skipped. Both failures are stale expectations in
  `tests/Miller.Tests/Server/Cli/TestsCliTests.cs:152-176`: the tests create a Go 1.23 `go.mod`
  and still expect refusal, while the implemented Go provider correctly refuses only below Go
  1.24 and the test's fixture now returns the observed non-refusal path. The test files are
  outside this packet's ownership; the lead should dispatch a bounded correction before the
  final branch gate.

## Worktree state

- Miller path: `/home/murphy/source/miller`
- Branch: `feature/ct-provider-correction-expansion`
- HEAD for the owned-file commit: `e2a3d5fde42df9aba4ff294a85006001b207adb8`
- The report finalization is a follow-up commit because the report records the owned-file commit
  SHA after Git created it.
- Worktree list before report finalization: `/home/murphy/source/miller e2a3d5fd [feature/ct-provider-correction-expansion]`.
- Finalization commit SHA: recorded in the handoff message after this report update.
