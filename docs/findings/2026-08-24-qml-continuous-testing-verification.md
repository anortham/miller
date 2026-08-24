# QML continuous-testing verification

Date: 2026-08-24

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

## Windows evidence

Windows NTFS `win-test` verification is owned by the lead and was not executed in this Task 5
worker packet. Windows real evidence is **NOT VERIFIED** here. The fixture and provider include
the spaced-path and multi-config behavior that the clean-SHA Windows run must exercise.

## External contracts

- [Qt Quick Test](https://doc.qt.io/qt-6/qtquicktest-index.html)
- [CTest command line](https://cmake.org/cmake/help/latest/manual/ctest.1.html)
- [Qt QML CMake integration](https://doc.qt.io/qt-6/qtqml-modules-cmake-integration.html)
