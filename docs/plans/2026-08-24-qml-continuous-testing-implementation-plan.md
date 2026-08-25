# QML Continuous Testing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Add a production Qt Quick Test provider that discovers and runs CMake/CTest QML test targets through Miller continuous testing.

**Architecture:** Inventory identifies strict Qt Quick Test CMake projects and collapses nested build files to one topmost configure root. A dedicated provider reuses Miller’s process, generation, JUnit, diagnostics, selection, and whole-suite contracts while isolating CMake/CTest parsing under `Providers/Qml`.

**Tech Stack:** .NET 10, C#, CMake 3.21+, CTest JSON v1 and JUnit XML, Qt 6 Quick Test, xUnit v3, Miller continuous testing.

**Architecture Quality:** The existing `IContinuousTestProvider` seam absorbs Qt without a new daemon or public API. The main risk is honest external-tool orchestration across configure/build/discover/run and Windows multi-config paths. Architecture risk: high.

## Global Constraints

- Framework key is exactly `qt-quick-test`.
- Provider source is exactly `ct-provider:qml`.
- Project and test language are exactly `qml`.
- Initial support is CMake/CTest only; qmake is an explicit open gap.
- Initial discovery granularity is one Miller case per CTest test target; QML function-level discovery is an explicit open gap.
- Require CMake 3.21 or newer.
- Use `ctest --show-only=json-v1` for discovery and `--output-junit` for results.
- Treat zero discovered/run tests as an error with `--no-tests=error` semantics.
- Set `QT_QPA_PLATFORM=offscreen` only when the environment does not already define it.
- Coverage is unsupported; any non-none coverage request fails explicitly.
- Configure/build/result files live under `CtGenerationPaths`, never the source tree.
- Require complete standard output before parsing tool versions, configure/build results, or discovery JSON.
- Reuse `ITestProcessRunner`, `JunitTestResultParser`, diagnostics, cancellation, timeout, artifact, and whole-suite contracts.
- Use TDD, keep subprocess tests in Scale, support Windows, and preserve unrelated working-tree changes.

---

## Architecture Quality

**Affected modules:** CT inventory/factory, new QML provider, shared parsing/selection contracts, Scale fixtures, and support docs.

**Caller-facing interface:** unchanged `IContinuousTestProvider` and existing `tests` CLI/MCP surfaces.

**Depth/locality check:** QML/CMake concerns stay in `Providers/Qml`; inventory gets only evidence/root detection; selection gets only language/fallback wiring.

**Test surface:** provider interface, inventory interface, impact selector, whole-suite contract, and one real external-tool fixture.

**Seams/adapters:** `QtQuickTestProvider` adapts CTest targets/JUnit into existing provider models. A focused CTest JSON parser keeps external format handling out of orchestration.

**Rejected shortcuts:** source-tree builds, shell-string commands, human-output parsing, recursive project duplication, fake function attribution, silent coverage downgrade, and copying process/JUnit infrastructure.

**Architecture risk:** high.

## Verification Strategy

**Project source of truth:** `AGENTS.md`, `docs/contracts/tests-cli-v1.md`, `tests/Miller.Tests/Miller.Tests.csproj`, `scripts/test.sh`, and existing provider/whole-suite contracts.

**Worker red/green scope:** `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Debug --filter "FullyQualifiedName~<OwnedTestClass>"`; run the exact new test during RED and the whole owned class before handoff.

**Worker ceiling:** Focused fast classes and one explicitly assigned Qt Scale method after toolchain preflight. Workers do not run the full fast suite, full Scale suite, or Windows guest.

**Worker gate invariant:** Tests prove exact project collapse/evidence, command argv/environment, CMake floor, complete-output enforcement, discovery identities, selected/whole-suite JUnit mapping, and explicit unsupported behavior.

**Lead affected-change scope:** After each coherent batch, run all QML provider/inventory/selector classes, `WholeSuiteProviderContractTests`, `dotnet build Miller.slnx -c Release --no-restore`, and `scripts/test.sh`.

**Branch gate:** Run `scripts/test.sh all` once at the final HEAD. Record the Qt Scale fixture as executed/pass, executed/fail, or `NOT VERIFIED` with the missing executable/version; a skip is not evidence.

**Security scope:** `security-secrets` through `razorback:security-review`; `security-deps` only if package dependencies change. External executable resolution and argv construction receive explicit command-injection review.

**Replay/metric evidence:** Hard gates are no inventory false positives/duplicates, exact stable discovery ids, no shell command construction, successful JUnit attribution, explicit zero-test/coverage errors, and an actually executed Qt Scale case on each claimed platform. Configure/build/discovery/run timings and artifact sizes are report-only.

**Escalation triggers:** Public CT schema changes, shared runner changes, source-tree writes, command-length failures, stale build reuse, or tool-output truncation require lead review and broader affected tests. Process/path changes require clean-SHA Windows verification: `win-test sync miller` then `win-test run miller -- powershell -File scripts/test.ps1`.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp. For replay or metric evidence, also record hard-gate metrics and report-only metrics. If the same HEAD already has a passing ledger entry for the required scope, reuse that evidence instead of rerunning the same expensive gate.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Discover one Qt Quick Test project root | Batch A | `src/Miller.Testing/Daemon/ContinuousTestProjectInventory.cs`; `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProjectInventoryTests.cs` | No | None - safe parallel batch. |
| Task 2: Parse CMake and CTest machine contracts | Batch A | create `src/Miller.Testing/Providers/Qml/QtQuickTestTooling.cs`; create `src/Miller.Testing/Providers/Qml/CTestDiscoveryParser.cs`; create `tests/Miller.Tests/Testing/Providers/Qml/CTestDiscoveryParserTests.cs`; create QML provider fake/scripted runner test support | No | None - safe parallel batch. |
| Task 3: Implement Qt Quick Test discovery and runs | None - serial | create `src/Miller.Testing/Providers/Qml/QtQuickTestProvider.cs`; create `tests/Miller.Tests/Testing/Providers/Qml/QtQuickTestProviderTests.cs`; `src/Miller.Testing/Daemon/ContinuousTestProviderFactory.cs`; `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProviderFactoryTests.cs` | Yes | Requires Tasks 1-2 final project/tool contracts and registers the provider in the shared factory. |
| Task 4: Integrate selection and whole-suite contracts | None - serial | `src/Miller.Testing/Selection/ContinuousTestImpactSelector.cs`; `tests/Miller.Tests/Testing/Selection/ContinuousTestImpactSelectorTests.cs`; `tests/Miller.Tests/Testing/Providers/WholeSuiteProviderContractTests.cs`; provider contract support needed for QML | Yes | Requires Task 3 stable case ids, framework key, and whole-suite behavior. |
| Task 5: Add real Qt Scale and platform evidence | None - serial | create Qt Quick Test fixture under the existing Scale fixture convention; create `tests/Miller.Tests/Testing/Providers/Qml/QtQuickTestProviderScaleTests.cs`; testing/toolchain docs; verification ledger/findings document | Yes | Exercises the completed provider and selection path with real external tools on Linux and Windows. |

Commit mode: Tasks 1-2 use `parallel-lead-commit`; Tasks 3-5 use `serial-worker-commit` after lead inline review and assigned verification.

### Task 1: Discover one Qt Quick Test project root

**Files:**
- Modify: `src/Miller.Testing/Daemon/ContinuousTestProjectInventory.cs`
- Modify: `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProjectInventoryTests.cs`

**Interfaces:**
- Consumes: workspace root, CMake project files, QuickTest runner/link evidence, QML test evidence, and existing `ContinuousTestProject` identity rules.
- Produces: one project with framework `qt-quick-test`, language `qml`, topmost configure root, and distinct evidence root when applicable.

**Contract inputs:** strict QuickTest plus QML evidence; nested `CMakeLists.txt` collapse; generic QML negative control.

**File ownership:** `src/Miller.Testing/Daemon/ContinuousTestProjectInventory.cs`; `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProjectInventoryTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Extend inventory candidate discovery to recognize CMake Qt Quick Test projects without turning every QML directory into a test project. Collapse nested build files to the topmost `project(...)` root and retain the subtree evidence location for provider use.

**Approach:** Add tests for nested subdirectories, separate configure/evidence roots, multiple independent top-level projects, missing QuickTest, missing QML tests, and misleading strings in unrelated files. Reuse current path normalization and ignore policies.

**Acceptance criteria:**
- [x] One logical CMake project produces one stable inventory row regardless of nested `CMakeLists.txt` count.
- [x] QuickTest and QML evidence may live below the configure root.
- [x] Generic QML/CMake applications and text-only false positives are not registered.
- [x] Worker-scope verification passes and the change is handed to the lead per `parallel-lead-commit`.

### Task 2: Parse CMake and CTest machine contracts

**Files:**
- Create: `src/Miller.Testing/Providers/Qml/QtQuickTestTooling.cs`
- Create: `src/Miller.Testing/Providers/Qml/CTestDiscoveryParser.cs`
- Create: `tests/Miller.Tests/Testing/Providers/Qml/CTestDiscoveryParserTests.cs`
- Create: QML provider fake/scripted runner support under `tests/Miller.Tests/Testing/Providers/Qml/`

**Interfaces:**
- Consumes: complete `cmake --version` output and complete `ctest --show-only=json-v1` output.
- Produces: validated tool version/capability and immutable discovered target records with exact names, labels, and bounded metadata.

**Contract inputs:** CMake floor 3.21; CTest JSON schema version 1; stable identity from project id plus exact test name.

**File ownership:** create `src/Miller.Testing/Providers/Qml/QtQuickTestTooling.cs`; create `src/Miller.Testing/Providers/Qml/CTestDiscoveryParser.cs`; create `tests/Miller.Tests/Testing/Providers/Qml/CTestDiscoveryParserTests.cs`; create QML provider fake/scripted runner test support

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Add focused parsers/builders for external machine output and argv fragments. Reject incomplete, malformed, unsupported-version, duplicate-name, and empty discovery inputs with provider diagnostics.

**Approach:** Parse JSON with bounded existing .NET facilities and preserve exact Unicode test names. Build argv as argument arrays, never shell strings, and centralize regex escaping for exact CTest selection.

**Acceptance criteria:**
- [ ] Versions below 3.21 and incomplete version output fail clearly.
- [ ] JSON v1 targets produce deterministic stable records independent of array order.
- [ ] Malformed, truncated, duplicate, and zero-test discovery fail without partial cases.
- [ ] Exact target-name regex construction handles metacharacters safely.
- [ ] Worker-scope verification passes and the change is handed to the lead per `parallel-lead-commit`.

### Task 3: Implement Qt Quick Test discovery and runs

**Files:**
- Create: `src/Miller.Testing/Providers/Qml/QtQuickTestProvider.cs`
- Create: `tests/Miller.Tests/Testing/Providers/Qml/QtQuickTestProviderTests.cs`
- Modify: `src/Miller.Testing/Daemon/ContinuousTestProviderFactory.cs`
- Modify: `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProviderFactoryTests.cs`

**Interfaces:**
- Consumes: Task 1 project roots, Task 2 tool/discovery records, `ITestProcessRunner`, `CtGenerationPaths`, `JunitTestResultParser`, and `IContinuousTestProvider` requests.
- Produces: discover/run responses for source `ct-provider:qml`, including whole-suite/selected cases, JUnit artifact path, diagnostics, and explicit unsupported coverage.

**Contract inputs:** configure/build/discover/run command shapes from CMake/CTest docs; environment and no-test constraints in Global Constraints.

**File ownership:** create `src/Miller.Testing/Providers/Qml/QtQuickTestProvider.cs`; create `tests/Miller.Tests/Testing/Providers/Qml/QtQuickTestProviderTests.cs`; `src/Miller.Testing/Daemon/ContinuousTestProviderFactory.cs`; `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProviderFactoryTests.cs`

**Serialization required:** Yes

**Dependency reason:** Requires Tasks 1-2 final project/tool contracts and registers the provider in the shared factory.

**What to build:** Implement generation-scoped configure/build, target discovery, exact selected runs, whole-suite runs, JUnit parsing, diagnostics, cancellation, and timeout behavior. Register the provider in the default factory.

**Approach:** Follow existing provider lifecycle and shared runner patterns identified with Miller. Set `QT_QPA_PLATFORM` only if absent; reuse a valid build tree within the generation; fail before execution for coverage requests; require complete outputs before parsing.

**Acceptance criteria:**
- [x] Discovery configures/builds outside source and returns stable CTest target cases.
- [x] Whole-suite and exact selections use `--output-junit` and `--no-tests=error` and map results correctly.
- [x] Explicit Qt platform values are preserved; absent values default to `offscreen`.
- [x] Coverage, zero tests, missing tools, bad versions, truncation, cancellation, timeout, and nonzero exits have honest typed outcomes.
- [x] Default factory resolves `qt-quick-test` to the provider.
- [x] Worker-scope verification passes and the worker commits per `serial-worker-commit`.

### Task 4: Integrate selection and whole-suite contracts

**Files:**
- Modify: `src/Miller.Testing/Selection/ContinuousTestImpactSelector.cs`
- Modify: `tests/Miller.Tests/Testing/Selection/ContinuousTestImpactSelectorTests.cs`
- Modify: `tests/Miller.Tests/Testing/Providers/WholeSuiteProviderContractTests.cs`
- Modify: provider contract support required to instantiate the QML provider in the shared contract

**Interfaces:**
- Consumes: `.qml` changed paths, CMake/runner changes, Task 3 target cases, and existing selection fallback/whole-suite models.
- Produces: QML-language impact evidence, project/target-level selection, and shared whole-suite conformance.

**Contract inputs:** no function ownership claim; cases without source attribution use framework/project fallback; exact framework/source strings.

**File ownership:** `src/Miller.Testing/Selection/ContinuousTestImpactSelector.cs`; `tests/Miller.Tests/Testing/Selection/ContinuousTestImpactSelectorTests.cs`; `tests/Miller.Tests/Testing/Providers/WholeSuiteProviderContractTests.cs`; provider contract support needed for QML

**Serialization required:** Yes

**Dependency reason:** Requires Task 3 stable case ids, framework key, and whole-suite behavior.

**What to build:** Teach path language inference about `.qml`, select affected Qt Quick Test projects without invented function mappings, and add the provider to the generic whole-suite contract suite.

**Approach:** Prefer exact fact-based selection where available, then framework/project fallback for unattributed CTest targets. Assert unrelated QML applications do not enter a QuickTest project and empty selection preserves whole-suite semantics.

**Acceptance criteria:**
- [x] `.qml` paths are classified as language `qml`.
- [x] Relevant QML/CMake/runner changes select the expected QuickTest project or targets without false function precision.
- [x] The provider passes `WholeSuiteProviderContractTests`, including artifact and missing-case behavior.
- [x] Non-QML provider selection remains unchanged.
- [x] Worker-scope verification passes and the worker commits per `serial-worker-commit`.

### Task 5: Add real Qt Scale and platform evidence

**Files:**
- Create: minimal CMake Qt Quick Test fixture under the existing Scale fixture convention identified with Miller
- Create: `tests/Miller.Tests/Testing/Providers/Qml/QtQuickTestProviderScaleTests.cs`
- Modify: testing/toolchain support documentation located with Miller
- Create: `docs/findings/2026-08-24-qml-continuous-testing-verification.md`

**Interfaces:**
- Consumes: the completed provider/factory/inventory/selection path and installed CMake/Qt toolchains.
- Produces: real configure/build/discover/whole-suite/selected-run evidence on Linux and Windows plus an honest toolchain availability record.

**Contract inputs:** fixture includes at least two CTest targets or otherwise proves whole-suite versus exact selection; Windows path contains spaces; coverage remains unrequested.

**File ownership:** create Qt Quick Test fixture under the existing Scale fixture convention; create `tests/Miller.Tests/Testing/Providers/Qml/QtQuickTestProviderScaleTests.cs`; testing/toolchain docs; verification ledger/findings document

**Serialization required:** Yes

**Dependency reason:** Exercises the completed provider and selection path with real external tools on Linux and Windows.

**What to build:** Add the smallest real Qt Quick Test project that can prove discovery, one selected target, whole-suite execution, JUnit failures/passes, and generation isolation. Record actual executable versions and platform results.

**Approach:** Tag subprocess tests `Scale`; use established toolchain preflight and report missing prerequisites as `NOT VERIFIED`. Run Linux locally and Windows through clean-SHA `win-test` on NTFS, including a spaced project path and multi-config build flag behavior.

**Acceptance criteria:**
- [ ] The Scale fixture really configures, builds, discovers, and runs through the provider.
- [ ] Whole-suite and one exact selection produce correctly attributed JUnit results.
- [ ] Source tree remains unchanged by configure/build/run.
- [x] Linux and Windows claims are backed by executed logs at the same commit; missing toolchains are reported as `NOT VERIFIED`.
- [x] `scripts/test.sh all` and triggered Windows verification pass; the worker commits per `serial-worker-commit`.

## Execution Handoff

- The user reviews and approves this plan before implementation begins.
- QML extractor test-role evidence and Miller QML indexing support should land before final Scale acceptance, but Tasks 1-4 can use a self-contained provider fixture.
- Create or reuse a dedicated Miller task worktree after approval and execute with `razorback:subagent-driven-development`, Miller impact/trace checks, TDD, inline lead review, and the commit modes above.
