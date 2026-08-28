# Task 13: Windows path portability

## Outcome

Updated the five synthetic test expectations reported by the Windows fast-suite run. Production code was unchanged.

## Failure mapping

- `MtpDotnetTestBackendTests.BuildRunCommand_keeps_whole_suite_unfiltered_and_trx_generation_scoped`: derives the expected results directory from the public result-artifact input with `Path.GetDirectoryName`.
- `MtpTestToolingTests.BuildRunArguments_places_framework_filter_in_app_arguments_and_keeps_results_inside_generation`: derives the expected results directory and report filename from the same public result-artifact input.
- `QmakeQuickTestToolingTests.BuildConfigureArguments_keep_the_project_and_makefile_in_generation_output`: derives the Makefile path with `Path.Combine` and the project path with `Path.GetFullPath`.
- `QmakeQuickTestToolingTests.BuildMakeArguments_probe_build_and_check_without_shell_joining`: derives the normalized QTest result path with `Path.GetFullPath`; the `TestResults` containment contract remains asserted.
- `QmakeQtQuickTestBackendTests.Discover_returns_one_stable_qmake_target_case`: expects the platform-native executable identity, including `.exe` on Windows.

## Evidence

- Miller indexed `MtpDotnetTestBackend.BuildRunCommand` and `MtpTestTooling.BuildRunArguments`; the production path is passed through to `Path.GetDirectoryName`/`Path.GetFileName` while argument order, TRX reporting, and filter placement are unchanged.
- Miller indexed `QmakeQuickTestTooling.BuildConfigureArguments` and `BuildCheckArguments`; production uses `Path.Combine`/`Path.GetFullPath` and rejects results outside a `TestResults` directory.
- Miller indexed `QmakeQtQuickTestBackend.DiscoverAsync`; production appends `.exe` on Windows before combining the generated output directory.
- Linux focused command: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~MtpDotnetTestBackendTests|FullyQualifiedName~MtpTestToolingTests|FullyQualifiedName~QmakeQuickTestToolingTests|FullyQualifiedName~QmakeQtQuickTestBackendTests"`
- Linux result: passed, 39 passed, 0 failed, 0 skipped.
- `git diff --check`: passed.

## Judgment calls

- Kept the synthetic path inputs and derived expectations with standard platform path APIs instead of adding OS-specific skips or duplicating private production helpers.
- Kept the executable assertion tied to the known qmake target name and platform executable suffix.

## Worktree

- Path: `/home/murphy/source/miller`
- Branch: `feature/ct-provider-correction-expansion`
- Starting HEAD: `b4c888bb`
- Implementation commit SHA: `ac4ae3ec`
- Report provenance commit SHA: `583f93ac`
- Production files changed: none
