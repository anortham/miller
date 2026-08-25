# QML continuous-testing verification

Date: 2026-08-24

Final gate update: 2026-08-25

## Scope

Task 5 adds a real Qt Quick Test Scale fixture under
`tests/Miller.Tests/Fixtures/QtQuickTestScale/` and the live provider smoke at
`tests/Miller.Tests/Testing/Providers/Qml/QtQuickTestProviderScaleTests.cs`.
The fixture registers two exact CTest targets (`qml/basic` and `qml/second`) against a
small `QUICK_TEST_MAIN` harness. The Scale test copies it into a path containing spaces,
keeps build and JUnit output under `CtGenerationPaths`, runs one selected target and the
whole suite, checks JUnit attribution, proves generation isolation, and hashes both the
repository fixture and the copied source before and after execution.

## Toolchain support

`CtProviderTestSupport` now centralizes CMake, CTest, and Qt Quick Test development-package
preflight. `CtScaleTraitConventionTests` treats those launch signals as Scale-only, alongside
the existing dotnet, cargo, node, and Python signals.

`QtQuickTestProvider` configures with `-DBUILD_TESTING=ON`. An optional existing
`ContinuousTestWorkspace.Metadata["configuration"]` value is applied to
`-DCMAKE_BUILD_TYPE`, `cmake --build --config`, and CTest `-C`. Windows defaults to `Release`
when the metadata does not provide a configuration, so multi-config builds use one matching
configuration throughout.

## Linux evidence

Executed in the Task 5 worktree:

```text
cmake --version       cmake version 4.3.0
ctest --version       ctest version 4.3.0
qtpaths6 --qt-version 6.11.1
pkg-config Qt6QuickTest: not found
```

The Qt runtime libraries are installed, but the Qt development CMake package
`Qt6QuickTestConfig.cmake` is absent. The focused verification command was:

```text
dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Debug --no-restore \
  --filter 'FullyQualifiedName~QtQuickTestProviderScaleTests|FullyQualifiedName~CtScaleTraitConventionTests|FullyQualifiedName~CTestDiscoveryParserTests|FullyQualifiedName~QtQuickTestProviderTests'
```

Result: 34 passed, 1 skipped, 0 failed. The real Qt Scale test skipped during the central
Qt development-package preflight. Linux real configure/build/discovery/run evidence is
therefore **NOT VERIFIED** on this machine.

The first two pre-fix Linux fast-suite attempts failed only an existing Julie-adoption test
under full-suite load. The focused test and stress replay passed; the collection was made
nonparallel at `a7e04ecb`, and the final fast gate is green.

The final branch-level Linux gates were recorded separately from the real-Qt fixture
preflight:

- At `a7e04ecb`, `scripts/test.sh all` built Release with 0 warnings and 0 errors; the fast
  suite reported 8,462 passed, 9 skipped, and 0 failed, and Scale reported 196 passed,
  17 skipped, and 0 failed.

## Windows evidence

The clean NTFS guest ran exact `a7e04ecb` through the PowerShell wrapper:

- The Release build reported 0 warnings and 0 errors.
- The fast suite reported 8,446 passed, 25 skipped, and 0 failed.
- Scale reported 199 passed, 14 skipped, and 0 failed after the publication-grace test
  correction.

The original Windows Scale failure was diagnosed as a suite-load race: the launcher returned
the accepted `not_published_within_grace` result during its fixed probe even though the daemon
obtained the lease immediately afterward. The correction accepts that documented result while
retaining the live-daemon, PID, stop, and log-growth assertions. It changed tests only; no
production grace, sleep, or launcher behavior changed.

The real Qt fixture itself remains **NOT VERIFIED** on the golden Windows guest because CMake,
CTest, and `qtpaths6` are absent. The fixture still carries the spaced-path and multi-config
coverage for a guest with the required Qt development package.

## Final gates and security

- Linux and Windows claims above are backed by the final fast/Scale logs at `a7e04ecb`;
  missing Qt development toolchains are explicitly **NOT VERIFIED**, not counted as fixture
  execution.
- `scripts/test.sh all` and `scripts/test.ps1 all` both passed on the exact final tree with
  0 warnings and 0 errors.
- `gitleaks detect --source . --no-banner --redact --verbose` at `92ed4333` exited 0 after the
  narrow public model-ID allowlist in `.gitleaks.toml`; it scanned 1,895 commits and found no
  secrets. The allowlist is limited to the exact public identifiers and does not disable the
  default rules globally.
- `dotnet list Miller.slnx package --vulnerable --include-transitive` reported no vulnerable
  packages.
- The QML CMake/CTest command-injection review found argument-array construction through
  `TestProcessCommand`, an anchored escaped test-name regex passed as one argv item, no shell
  command construction, and typed launch exceptions.

## External contracts

- [Qt Quick Test](https://doc.qt.io/qt-6/qtquicktest-index.html)
- [CTest command line](https://cmake.org/cmake/help/latest/manual/ctest.1.html)
- [Qt QML CMake integration](https://doc.qt.io/qt-6/qtqml-modules-cmake-integration.html)
