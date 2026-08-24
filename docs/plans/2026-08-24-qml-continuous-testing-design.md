# QML Continuous Testing Design

## Outcome

Miller continuous testing will discover, build, list, select, and run CMake-based Qt Quick Test projects as a first-class provider using CTest’s stable machine-readable interfaces.

## Scope

- Initial project system: CMake projects that register Qt Quick Test executables with CTest.
- Initial test granularity: one Miller test case per CTest test target.
- Framework key: `qt-quick-test`.
- Provider source: `ct-provider:qml`.
- Project/test language: `qml`.
- Headless default: `QT_QPA_PLATFORM=offscreen` only when the environment does not already set it.
- Coverage mode: none. Any requested coverage fails with an explicit unsupported diagnostic.
- Deferred with named gaps: qmake project discovery, QML function-level discovery/selection, and native QML coverage integration.

## Architecture Quality

**Affected modules:** project inventory, provider factory, a new QML provider, shared process/generation paths, impact selection, whole-suite contracts, Scale fixtures, and tests documentation.

**Caller-facing interface:** existing `IContinuousTestProvider` methods and existing `tests` CLI/MCP operations. No new test command or public result schema is required.

**Depth/locality check:** CMake/CTest command construction, JSON parsing, version validation, and Qt environment handling live under `Providers/Qml`. Generic inventory only recognizes projects; generic selection only learns `.qml` as a language.

**Test surface:** provider contract tests, fake-runner command/result tests, inventory collapse tests, selection tests, and a real Scale fixture using Qt/CMake when available.

**Seams/adapters:** the provider reuses `ITestProcessRunner`, `CtGenerationPaths`, `JunitTestResultParser`, diagnostics, cancellation, timeouts, artifact transport, and whole-suite semantics. It does not introduce a Qt-specific daemon.

**Rejected shortcuts:** recursively treating every `tst_*.qml` as a project, one project per nested `CMakeLists.txt`, regex-parsing human CTest output, assuming configure and evidence roots are identical, claiming QML function-level selection from CTest target discovery, silently skipping no-test runs, and synthesizing coverage.

**Architecture risk:** high. The provider coordinates external tool versions, configure/build state, test discovery, selection, result artifacts, cross-platform paths, and headless execution.

## Project Discovery

A directory is a Qt Quick Test project when:

1. A topmost applicable `CMakeLists.txt` contains a CMake `project(...)` declaration.
2. Its subtree contains strict Quick Test evidence: `Qt6::QuickTest`, `QUICK_TEST_MAIN`, `QUICK_TEST_OPENGL_MAIN`, a Qt Quick Test runner source, or an equivalent Qt CMake target that links QuickTest.
3. Its subtree contains QML test evidence such as `tst_*.qml` or `TestCase`.

Inventory collapses nested `CMakeLists.txt` files to the topmost project root satisfying those conditions. The root containing Quick Test evidence may differ from the configure root; the project records both. A generic QML application without QuickTest evidence is not registered.

`FrameworkFallback` recognizes the CMake/QML combination only after strict project evidence has selected `qt-quick-test`; it does not classify arbitrary `.qml` directories by filename alone.

## Toolchain and Generation

- Probe `cmake --version`; require CMake 3.21 or newer because the provider needs both CTest JSON discovery and `--output-junit`.
- Allocate configure/build/result locations through `CtGenerationPaths`; never build into the source tree.
- Configure once per generation with the project’s configure root, `BUILD_TESTING=ON`, and a generator/configuration compatible with the host.
- Build before discovery/run as needed and reuse the generation-scoped build tree rather than reconfiguring for each selected target.
- Invoke CTest through `cmake`/`ctest` paths discovered by the existing tooling policy and process runner.
- Require complete standard output for version, configure, build, and discovery parsing; truncated output is a provider error.

## Discovery and Stable Identities

- Run `ctest --test-dir <build> --show-only=json-v1` after configure/build.
- Parse JSON version 1 and create one `ProviderTestCase` per CTest test object.
- Stable identity derives from project identity plus the exact CTest test name, not enumeration order.
- Preserve labels and command metadata only in bounded provider metadata fields allowed by existing contracts.
- Do not claim a QML source function or line when CTest does not provide one. Cases without source attribution use framework/project fallback during impact selection.
- A project with zero discovered tests is an error equivalent to `--no-tests=error`, not a successful empty project.

## Running Tests

- Whole suite: `ctest --test-dir <build> --output-junit <artifact> --no-tests=error`, plus the host configuration flag when required.
- Selection: add one anchored escaped `-R` expression covering the exact selected CTest names; chunk only if shared argv limits require it.
- Add `--output-on-failure` for useful diagnostics while the JUnit artifact remains the result source of truth.
- Parse results through `JunitTestResultParser`; attribute missing selected cases and nonzero exits using existing provider contract semantics.
- Set `QT_QPA_PLATFORM=offscreen` only when absent so user-selected platforms remain authoritative.
- Implement cancellation, timeouts, complete-output checks, and artifact paths through existing shared facilities.

## Selection and Continuous Runs

- `ContinuousTestImpactSelector.LanguageFromPath` recognizes `.qml` as `qml`.
- Initially, any relevant QML/CMake/Qt runner change selects the affected Qt Quick Test project at target or whole-project granularity because CTest does not expose QML function ownership.
- The provider’s whole-suite flag and coverage of known cases obey `WholeSuiteProviderContractTests`.
- Watch/inventory refresh uses the same project identity and does not create duplicate projects for subdirectories.

## Testing and Platform Support

- Fast tests use a scripted/fake process runner to assert exact commands, environment, version floors, JSON parsing, selections, zero-test behavior, truncation errors, and JUnit mapping.
- Inventory tests cover nested CMake roots, separate evidence/configure roots, generic-QML negative controls, and multiple independent top-level projects.
- A Scale fixture builds a minimal Qt Quick Test project, discovers targets, runs whole-suite and one selection, and verifies JUnit evidence.
- Scale tests report `NOT VERIFIED` when Qt/CMake is absent; a skip is not acceptance evidence.
- Windows runs use the NTFS-backed `win-test` guest from a clean synchronized commit and prove paths containing spaces plus multi-config CMake behavior.

## Acceptance Criteria

- [ ] Inventory discovers one project per topmost CMake QuickTest root and no generic QML false positives.
- [ ] CMake versions below 3.21 fail with an actionable provider diagnostic.
- [ ] Discovery uses CTest JSON v1 and stable target identities.
- [ ] Whole-suite and exact selected runs produce parsed JUnit results through shared contracts.
- [ ] Headless defaults respect an explicitly configured Qt platform.
- [ ] Coverage requests fail explicitly; qmake and function-level support remain documented gaps.
- [ ] QML changes enter impact selection without fabricated function ownership.
- [ ] Fast, whole-suite contract, real Scale, Linux, and Windows verification pass or report missing toolchains honestly.

## Primary References

- Qt Quick Test: <https://doc.qt.io/qt-6/qtquicktest-index.html>
- Qt Quick Test `TestCase`: <https://doc.qt.io/qt-6/qml-qttest-testcase.html>
- CTest command line: <https://cmake.org/cmake/help/latest/manual/ctest.1.html>
- Qt QML CMake integration: <https://doc.qt.io/qt-6/qtqml-modules-cmake-integration.html>
