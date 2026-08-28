# CT Provider Correction and Expansion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Correct JavaScript CT discovery, complete Qt Quick Test CMake/qmake coverage, add first-class VB.NET and Microsoft.Testing.Platform behavior, and add a first-class Go provider without false `KnownEmpty` or green verdicts.

**Architecture:** Shared fact completeness, role, language-family, and provider-identity contracts land first. Public framework names and `IContinuousTestProvider` stay stable while JavaScript uses a typed literal matcher, QML delegates to CTest/qmake adapters, .NET delegates to VSTest/xUnit-v3/MTP adapters, and Go owns module discovery plus complete JSON verdict parsing.

**Tech Stack:** .NET 10, C#, SQLite, xUnit v3, CMake/CTest, qmake/QTestLib, Microsoft.Testing.Platform, Go 1.24+, Jest 29-30, Vitest 0.34-4.

**Architecture Quality:** High-risk multi-provider change. Keep backend complexity behind existing provider boundaries; add only one typed file-completeness seam and trailing optional fact/identity fields. Tests prove behavior through inventory, `DiscoverAsync`, `RunAsync`, and selector results.

## Global Constraints

- Preserve the uncommitted JavaScript starting work and all Goldfish files already present on `feature/ct-provider-correction-expansion`.
- Follow `docs/plans/2026-08-27-ct-provider-correction-expansion-design.md` as the approved specification.
- Every production behavior follows red-green-refactor. A worker must show the intended test failure before implementation and the same scope passing afterward.
- Use Miller `context`, `inspect`, `trace`, and `impact` before code reads or edits. Do not infer symbols, signatures, paths, command flags, or public contracts.
- Tests contain no comments. Production comments are limited to public API documentation or non-expressible external constraints.
- Keep `Miller.Core` free of I/O dependencies.
- Add no MCP tools.
- Keep CT opt-in, supervised, generation-scoped, and write-free outside Miller-owned paths.
- Preserve raw Julie labels and evidence. Exact labels for this plan are `csharp`, `razor`, `vbnet`, `javascript`, `jsx`, `typescript`, `tsx`, `qml`, and `go`.
- Missing, stale, unavailable, ambiguous, malformed, truncated, unsupported, or version-unknown evidence fails closed. It never becomes `KnownEmpty` or green.
- Never persist a Julie symbol ID without its matching index identity. Resolve stable provider name/path identity against the current snapshot.
- JavaScript support is Jest 29-30 and Vitest 0.34-4. Unknown majors are refused with the detected version.
- Go support requires Go 1.24 or newer and covers top-level `TestXxx` cases only. Examples, child `t.Run`, benchmarks, and fuzzing are excluded.
- QML support covers CMake/CTest and qmake Qt Quick Test. Qbs, PySide6, native Qt Test, device runners, function-level selection, and QML coverage remain unsupported.
- Real `julie-extract`, Qt, qmake, .NET provider, or Go processes require class-level `[Trait("Category","Scale")]` and the repository's shared `Require*` launch signals.
- Do not remove the Julie audit worktree. Preservation is implemented first; removal remains a separate destructive approval.
- No push, release, publish, or deployment is authorized.

---

## Verification Strategy

**Project source of truth:** `AGENTS.md` testing/build rules, `docs/continuous-testing.md`, `tests/Miller.Tests/Miller.Tests.csproj`, and `docs/release-process.md`.

**Worker red/green scope:** `dotnet test --filter "FullyQualifiedName~<AssignedTestClass>"`, combining only the focused classes assigned to that task.

**Worker ceiling:** Focused fast test classes only. Workers do not run bare `dotnet test`, Scale, Release build, Windows, or security scopes.

**Worker gate invariant:** The assigned scope proves the task's exact external contract and fail-closed behavior through the caller-facing interface. A passing parser test without provider or selector coverage is insufficient when the task changes those interfaces.

**Lead affected-change scope:** After each coherent phase, run the union of focused classes returned by Miller `impact` for that phase. Do not rerun a passing scope on an unchanged tree.

**Branch gate:** Run once on the final unchanged tree: `dotnet test`; `scripts/test.sh scale`; `dotnet build Miller.slnx -c Release`; `win-test sync miller`; `win-test run miller -- powershell -Command "dotnet test --filter 'Category!=Scale'"`.

**Security scope:** `security-secrets`: `gitleaks detect --no-banner --redact`. `security-deps`: `dotnet package list --project Miller.slnx --vulnerable --include-transitive --no-restore`. Any secrets finding or critical/high dependency finding blocks completion.

**Replay/metric evidence:** Hard gates are zero fast-test failures, zero required Scale failures when toolchains are present, zero Release warnings/errors, zero Windows fast failures, and language-role parity on a real extractor fixture. Skipped Scale cases due absent guarded toolchains are report-only and must name the missing toolchain.

**Escalation triggers:** Changes to provider process launch, inventory, extractor fact reading, or CT selection require Scale. .NET/MTP or platform command changes require Windows. Changes to `ICtFactSource` or `ProviderTestCase` require all compiler-reported implementations/fakes plus selector and store-applier coverage.

**Assigned verification failure:** Workers diagnose and repair failures inside owned scope. A failure outside ownership is reported with evidence; the lead defines a bounded correction packet.

**Verification ledger:** Record invariant, exact command, scope label, commit SHA, result, and UTC timestamp in `.razorback/sdd/<plan>/progress.md`. Reuse a passing entry for the same HEAD and scope.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Typed CT fact completeness and role evidence | None - serial | `src/Miller.Indexing/Testing/ICtFactSource.cs`, `src/Miller.Indexing/Testing/CtFactAdapter.cs`, `src/Miller.Testing/Selection/IMillerFactSource.cs`, `src/Miller.Testing/Daemon/ContinuousTestRevisionPoller.cs`, `src/Miller.Server/Tools/TestsCore.cs`, `tests/Miller.Tests/Testing/FactAdapter/CtFactAdapterTests.cs`, `tests/Miller.Tests/Testing/Selection/FakeMillerFactSource.cs`, `tests/Miller.Tests/Server/TestsRunExecutionBudgetTests.cs` | Yes | Contract-first dependency for selection and every provider slice. |
| Task 2: Language families and provider identity fail-closed selection | None - serial | `src/Miller.Testing/Selection/ContinuousTestLanguageFamily.cs`, `src/Miller.Testing/Selection/ContinuousTestImpactSelector.cs`, `src/Miller.Testing/Contracts/ProviderContracts.cs`, `src/Miller.Testing/ContinuousTestStoreApplier.cs`, `tests/Miller.Tests/Testing/Selection/ContinuousTestImpactSelectorTests.cs`, `tests/Miller.Tests/Testing/Analysis/ContinuousTestStoreApplierTests.cs` | Yes | Depends on Task 1 typed evidence and defines contracts consumed by provider tasks. |
| Task 3: JavaScript discovery correction | Batch A | `src/Miller.Testing/Providers/Node/JavaScriptTestProvider.cs`, `src/Miller.Testing/Providers/Node/JsFrameworkTestFileDiscovery.cs`, `src/Miller.Testing/Providers/Node/JsTestConfigPatterns.cs`, `src/Miller.Testing/Providers/Node/JsTestGlob.cs`, `tests/Miller.Tests/Testing/Providers/Node/JavaScriptTestProviderTests.cs`, `tests/Miller.Tests/Testing/Providers/Node/JsFrameworkTestFileDiscoveryTests.cs` | No | None - safe parallel batch. |
| Task 4: Correct QML CMake/CTest and establish backend seam | Batch A | `src/Miller.Testing/Daemon/ContinuousTestProjectInventory.cs`, `src/Miller.Testing/Providers/Qml/QtQuickTestProvider.cs`, `src/Miller.Testing/Providers/Qml/IQtQuickTestBackend.cs`, `src/Miller.Testing/Providers/Qml/CMakeQtQuickTestBackend.cs`, `src/Miller.Testing/Providers/Qml/QtQuickTestTooling.cs`, `src/Miller.Testing/Providers/Qml/CTestDiscoveryParser.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProjectInventoryTests.cs`, `tests/Miller.Tests/Testing/Providers/Qml/QtQuickTestProviderTests.cs`, `tests/Miller.Tests/Testing/Providers/Qml/CTestDiscoveryParserTests.cs` | No | None - safe parallel batch. |
| Task 5: Add qmake Qt Quick Test backend | None - serial | `src/Miller.Testing/Daemon/ContinuousTestProjectInventory.cs`, `src/Miller.Testing/Providers/Qml/QtQuickTestProvider.cs`, `src/Miller.Testing/Providers/Qml/IQtQuickTestBackend.cs`, `src/Miller.Testing/Providers/Qml/QmakeQtQuickTestBackend.cs`, `src/Miller.Testing/Providers/Qml/QmakeQuickTestTooling.cs`, `src/Miller.Testing/Providers/Qml/QTestResultParser.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProjectInventoryTests.cs`, `tests/Miller.Tests/Testing/Providers/Qml/QtQuickTestProviderTests.cs`, `tests/Miller.Tests/Testing/Providers/Qml/QmakeQtQuickTestBackendTests.cs`, `tests/Miller.Tests/Testing/Providers/Qml/QmakeQuickTestToolingTests.cs`, `tests/Miller.Tests/Testing/Providers/Qml/QTestResultParserTests.cs`, `tests/Miller.Tests/Testing/CtProviderTestSupport.cs`, `tests/Miller.Tests/Conventions/CtScaleTraitConventionTests.cs`, `tests/Miller.Tests/Testing/Providers/Qml/QtQuickTestProviderScaleTests.cs`, `tests/Miller.Tests/Fixtures/QtQuickTestQmakeScale/quicktest.pro`, `tests/Miller.Tests/Fixtures/QtQuickTestQmakeScale/runner.cpp`, `tests/Miller.Tests/Fixtures/QtQuickTestQmakeScale/tst_smoke.qml` | Yes | Depends on Task 4 backend seam and reuses inventory/provider files. |
| Task 6: First-class VB.NET selection and .NET backend evidence | None - serial | `src/Miller.Testing/Daemon/ContinuousTestProjectInventory.cs`, `src/Miller.Testing/Providers/Dotnet/DotnetTestProvider.cs`, `src/Miller.Testing/Providers/Dotnet/DotnetTestBackend.cs`, `src/Miller.Testing/Selection/ContinuousTestImpactSelector.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProjectInventoryTests.cs`, `tests/Miller.Tests/Testing/Providers/Dotnet/DotnetTestProviderTests.cs`, `tests/Miller.Tests/Testing/Selection/ContinuousTestImpactSelectorTests.cs`, `tests/Miller.Tests/Testing/Providers/Dotnet/DotnetProviderScaleTests.cs`, `tests/Miller.Tests/Fixtures/VbDotnetScale/VbDotnetScale.vbproj`, `tests/Miller.Tests/Fixtures/VbDotnetScale/UnitTests.vb` | Yes | Follows QML inventory changes and consumes Task 2 language/identity contracts. |
| Task 7: Add Microsoft.Testing.Platform backend | None - serial | `src/Miller.Testing/Providers/Dotnet/DotnetTestProvider.cs`, `src/Miller.Testing/Providers/Dotnet/DotnetTestBackend.cs`, `src/Miller.Testing/Providers/Dotnet/MtpDotnetTestBackend.cs`, `src/Miller.Testing/Providers/Dotnet/MtpTestTooling.cs`, `src/Miller.Testing/Providers/Dotnet/MtpTestListParser.cs`, `tests/Miller.Tests/Testing/Providers/Dotnet/DotnetTestProviderTests.cs`, `tests/Miller.Tests/Testing/Providers/Dotnet/MtpDotnetTestBackendTests.cs`, `tests/Miller.Tests/Testing/Providers/Dotnet/MtpTestToolingTests.cs`, `tests/Miller.Tests/Testing/Providers/Dotnet/MtpTestListParserTests.cs`, `tests/Miller.Tests/Testing/Providers/Dotnet/DotnetProviderScaleTests.cs`, `tests/Miller.Tests/Fixtures/VbMtpScale/VbMtpScale.vbproj`, `tests/Miller.Tests/Fixtures/VbMtpScale/UnitTests.vb`, `tests/Miller.Tests/Fixtures/VbMtpScale/global.json` | Yes | Depends on Task 6 backend discriminator and stable VSTest/xUnit lanes. |
| Task 8: Add Go provider | None - serial | `src/Miller.Testing/Daemon/ContinuousTestProjectInventory.cs`, `src/Miller.Testing/Daemon/ContinuousTestProviderFactory.cs`, `src/Miller.Testing/Providers/Go/GoTestProvider.cs`, `src/Miller.Testing/Providers/Go/GoTestTooling.cs`, `src/Miller.Testing/Providers/Go/GoTestListParser.cs`, `src/Miller.Testing/Providers/Go/GoTestJsonParser.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProjectInventoryTests.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProviderFactoryTests.cs`, `tests/Miller.Tests/Testing/Providers/Go/GoTestProviderTests.cs`, `tests/Miller.Tests/Testing/Providers/Go/GoTestToolingTests.cs`, `tests/Miller.Tests/Testing/Providers/Go/GoTestListParserTests.cs`, `tests/Miller.Tests/Testing/Providers/Go/GoTestJsonParserTests.cs`, `tests/Miller.Tests/Testing/CtProviderTestSupport.cs`, `tests/Miller.Tests/Conventions/CtScaleTraitConventionTests.cs`, `tests/Miller.Tests/Testing/Providers/Go/GoTestProviderScaleTests.cs`, `tests/Miller.Tests/Fixtures/GoCtScale/go.mod`, `tests/Miller.Tests/Fixtures/GoCtScale/math_test.go`, `tests/Miller.Tests/Fixtures/GoCtWorkspaceScale/go.work`, `tests/Miller.Tests/Fixtures/GoCtWorkspaceScale/first/go.mod`, `tests/Miller.Tests/Fixtures/GoCtWorkspaceScale/first/first_test.go`, `tests/Miller.Tests/Fixtures/GoCtWorkspaceScale/second/go.mod`, `tests/Miller.Tests/Fixtures/GoCtWorkspaceScale/second/second_test.go` | Yes | Depends on shared contracts and follows prior inventory/factory edits. |
| Task 9: Documentation and Julie audit handoff | None - serial | `README.md`, `docs/README.md`, `docs/continuous-testing.md`, `docs/findings/2026-08-27-continuous-testing-language-readiness-audit.md`, `docs/findings/2026-08-27-continuous-testing-extractor-backlog.md`, `/home/murphy/source/julie-extractors/.claude/worktrees/ct-language-audit-plan/docs/findings/2026-08-27-continuous-testing-language-readiness-audit.md`, `/home/murphy/source/julie-extractors/.claude/worktrees/ct-language-audit-plan/docs/plans/2026-08-27-continuous-testing-extractor-evidence.md`, `docs/plans/2026-08-27-ct-provider-correction-expansion-implementation-plan.md` | Yes | Must describe the implemented final matrix and preserve cross-workspace evidence after provider behavior stabilizes. |

Parallel Batch A uses `parallel-lead-commit`. Every other task uses `serial-worker-commit`.

### Task 1: Typed CT fact completeness and role evidence

**Files:**
- Modify the Task 1 ownership files from the parallel contract.
- Test: `tests/Miller.Tests/Testing/FactAdapter/CtFactAdapterTests.cs` and affected fact-source fake tests.

**Interfaces:**
- Consumes: `IndexedSymbol.TestEvidence`, current file status/content hash/parse diagnostics, `ICtFactSource`.
- Produces: trailing optional detailed evidence on `CtSymbolFact`/`CtImpactedSymbol` and a typed `CtFileFact` completeness read.

**Contract inputs:** Design sections “Detailed test evidence” and “File completeness and KnownEmpty”; pinned Julie `apply_test_role` semantics.

**File ownership:** `src/Miller.Indexing/Testing/ICtFactSource.cs`, `src/Miller.Indexing/Testing/CtFactAdapter.cs`, `src/Miller.Testing/Selection/IMillerFactSource.cs`, `src/Miller.Testing/Daemon/ContinuousTestRevisionPoller.cs`, `src/Miller.Server/Tools/TestsCore.cs`, `tests/Miller.Tests/Testing/FactAdapter/CtFactAdapterTests.cs`, `tests/Miller.Tests/Testing/Selection/FakeMillerFactSource.cs`, `tests/Miller.Tests/Server/TestsRunExecutionBudgetTests.cs`

**Serialization required:** Yes.

**Dependency reason:** Contract-first dependency for selection and every provider slice.

**What to build:** Preserve case/container/lifecycle/status/reason evidence and expose current file completeness without widening graph nodes. Update every implementation and fake so absence means unknown, never empty.

**Approach:** Use trailing optional fields for compatibility. Keep legacy `IsTest`. Prove role precedence and file status/diagnostic combinations with focused tests; add a Scale parity test over a real multi-language extract if no existing guarded test proves the full carrier.

**Acceptance criteria:**
- [x] Case, container, lifecycle, current, diagnostic, non-indexed, and unavailable evidence round-trip through `ICtFactSource`.
- [x] Old artifacts remain readable and incomplete evidence is typed unknown.
- [x] Real extractor role rows remain generic across every observed language and role.
- [x] Worker-scope verification passes and the worker creates the owned-file commit.

### Task 2: Language families and provider identity fail-closed selection

**Files:**
- Create: `src/Miller.Testing/Selection/ContinuousTestLanguageFamily.cs`.
- Modify/Test the Task 2 ownership files from the parallel contract.

**Interfaces:**
- Consumes: Task 1 detailed evidence and `CtFileFact`.
- Produces: centralized exact language families; trailing `ProviderTestCase.SymbolName`/`SymbolPath`; current-snapshot joins; fail-closed legacy/ambiguous selection.

**Contract inputs:** Exact label list, role precedence table, `KnownEmpty` matrix, and legacy `SymbolName` migration rule from the design.

**File ownership:** `src/Miller.Testing/Selection/ContinuousTestLanguageFamily.cs`, `src/Miller.Testing/Selection/ContinuousTestImpactSelector.cs`, `src/Miller.Testing/Contracts/ProviderContracts.cs`, `src/Miller.Testing/ContinuousTestStoreApplier.cs`, `tests/Miller.Tests/Testing/Selection/ContinuousTestImpactSelectorTests.cs`, `tests/Miller.Tests/Testing/Analysis/ContinuousTestStoreApplierTests.cs`

**Serialization required:** Yes.

**Dependency reason:** Depends on Task 1 typed evidence and defines contracts consumed by provider tasks.

**What to build:** Replace private partial path mapping and fileless-C# guesses with one family mapper and stable provider identity. A reachable project test that cannot resolve exactly forces `Unknown`; current accounted empty files alone can produce `KnownEmpty`.

**Approach:** Preserve raw labels. Treat legacy stored symbol IDs as opaque until rediscovery. Add exact/ambiguous/stale joins, mixed-language exclusions, extension dialects, and watermark regressions through `Select` and `ApplyDiscovery`.

**Acceptance criteria:**
- [x] All exact labels/extensions in Global Constraints map to the approved family and unknown labels stay incompatible.
- [x] Detailed containers/lifecycle symbols never become runnable cases; legacy `IsTest` behavior remains compatible.
- [x] Missing/diagnostic/stale file facts and unresolved reachable provider cases return `Unknown`, not green or `KnownEmpty`.
- [x] Provider name/path identity round-trips without a durable unversioned Julie ID.
- [x] Worker-scope verification passes and the worker creates the owned-file commit.

### Task 3: JavaScript discovery correction

**Files:**
- Modify/Create/Test the Task 3 ownership files from the parallel contract.

**Interfaces:**
- Consumes: `JavaScriptTestProvider.DiscoverAsync`, `NodeTestFileDiscovery` matching behavior, package metadata.
- Produces: internal typed pattern set and `IsMatch` contract with runner-specific defaults, config diagnostics, and exclusions.

**Contract inputs:** Jest 29-30 configuration/testMatch contract and Vitest 0.34-4 config/include contract linked in the design; existing uncommitted JavaScript work is the starting point.

**File ownership:** `src/Miller.Testing/Providers/Node/JavaScriptTestProvider.cs`, `src/Miller.Testing/Providers/Node/JsFrameworkTestFileDiscovery.cs`, `src/Miller.Testing/Providers/Node/JsTestConfigPatterns.cs`, `src/Miller.Testing/Providers/Node/JsTestGlob.cs`, `tests/Miller.Tests/Testing/Providers/Node/JavaScriptTestProviderTests.cs`, `tests/Miller.Tests/Testing/Providers/Node/JsFrameworkTestFileDiscoveryTests.cs`

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**What to build:** Replace the raw recursive scanner with a bounded typed literal-config reader. Correct defaults, Vite fallback, `.cts`, referenced Jest JSON, `<rootDir>`, runner-specific negatives, operational exclusions, `testRegex`, interpolation, truncation, and unknown versions.

**Approach:** Config is read, never executed. Defaults apply only when no discovery property is declared. Declared unsupported config throws an actionable provider diagnostic. Test all behavior through `DiscoverAsync`; remove comments from changed tests.

**Acceptance criteria:**
- [x] Jest/Vitest defaults contain only documented JS/TS cases and explicit config may include runner-owned directories/component extensions.
- [x] Supported config shapes match exactly; unsupported/truncated/testRegex/unknown-version shapes refuse instead of silently changing suites.
- [x] Positive/negative semantics differ correctly between Jest and Vitest.
- [x] Existing command/run behavior remains green.
- [x] Worker-scope verification passes; worker does not commit in Batch A and reports the verified diff.

### Task 4: Correct QML CMake/CTest and establish backend seam

**Files:**
- Create/Modify/Test the Task 4 ownership files from the parallel contract.

**Interfaces:**
- Consumes: current `QtQuickTestProvider`, CMake/CTest machine contracts, project metadata.
- Produces: stable `qt-quick-test` provider with internal backend discriminator and CMake adapter.

**Contract inputs:** Qt Quick Test and CTest official links in the design; supported macro family and CTest capability rules.

**File ownership:** `src/Miller.Testing/Daemon/ContinuousTestProjectInventory.cs`, `src/Miller.Testing/Providers/Qml/QtQuickTestProvider.cs`, `src/Miller.Testing/Providers/Qml/IQtQuickTestBackend.cs`, `src/Miller.Testing/Providers/Qml/CMakeQtQuickTestBackend.cs`, `src/Miller.Testing/Providers/Qml/QtQuickTestTooling.cs`, `src/Miller.Testing/Providers/Qml/CTestDiscoveryParser.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProjectInventoryTests.cs`, `tests/Miller.Tests/Testing/Providers/Qml/QtQuickTestProviderTests.cs`, `tests/Miller.Tests/Testing/Providers/Qml/CTestDiscoveryParserTests.cs`

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**What to build:** Extract the current CMake behavior behind an internal backend interface, recognize the full macro family, require usable CTest registration before state writes, preserve independent nested projects, and make bounded reads complete to EOF/bound.

**Approach:** Keep framework/status contracts unchanged. Use static evidence when conclusive and a zero-write capability probe when required. Preserve exact selection, offscreen defaulting, source immutability, JUnit parsing, and coverage refusal.

**Acceptance criteria:**
- [x] `QUICK_TEST_MAIN`, `QUICK_TEST_MAIN_WITH_SETUP`, and `QUICK_TEST_OPENGL_MAIN` inventory paths work.
- [x] No-CTest projects are refused before CT state writes.
- [x] Independent nested CMake projects remain separate.
- [x] Existing QML provider behavior passes through the CMake adapter unchanged.
- [x] Worker-scope verification passes; worker does not commit in Batch A and reports the verified diff.

### Task 5: Add qmake Qt Quick Test backend

**Files:**
- Create/Modify/Test the Task 5 ownership files from the parallel contract.

**Interfaces:**
- Consumes: Task 4 `IQtQuickTestBackend`, project backend metadata, shared process/generation contracts.
- Produces: qmake discovery/run adapter and guarded real-toolchain support under framework `qt-quick-test`.

**Contract inputs:** Qt qmake `qmltestcase`, `make check`, `TESTARGS`, Qt 5 `xunitxml`, and Qt 6 `junitxml` contracts linked in the design.

**File ownership:** `src/Miller.Testing/Daemon/ContinuousTestProjectInventory.cs`, `src/Miller.Testing/Providers/Qml/QtQuickTestProvider.cs`, `src/Miller.Testing/Providers/Qml/IQtQuickTestBackend.cs`, `src/Miller.Testing/Providers/Qml/QmakeQtQuickTestBackend.cs`, `src/Miller.Testing/Providers/Qml/QmakeQuickTestTooling.cs`, `src/Miller.Testing/Providers/Qml/QTestResultParser.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProjectInventoryTests.cs`, `tests/Miller.Tests/Testing/Providers/Qml/QtQuickTestProviderTests.cs`, `tests/Miller.Tests/Testing/Providers/Qml/QmakeQtQuickTestBackendTests.cs`, `tests/Miller.Tests/Testing/Providers/Qml/QmakeQuickTestToolingTests.cs`, `tests/Miller.Tests/Testing/Providers/Qml/QTestResultParserTests.cs`, `tests/Miller.Tests/Testing/CtProviderTestSupport.cs`, `tests/Miller.Tests/Conventions/CtScaleTraitConventionTests.cs`, `tests/Miller.Tests/Testing/Providers/Qml/QtQuickTestProviderScaleTests.cs`, `tests/Miller.Tests/Fixtures/QtQuickTestQmakeScale/quicktest.pro`, `tests/Miller.Tests/Fixtures/QtQuickTestQmakeScale/runner.cpp`, `tests/Miller.Tests/Fixtures/QtQuickTestQmakeScale/tst_smoke.qml`

**Serialization required:** Yes.

**Dependency reason:** Depends on Task 4 backend seam and reuses inventory/provider files.

**What to build:** Detect qmake Quick Test projects only when a `check` target is guaranteed, build outside source, discover stable target-level cases, run through `make check`, and parse version-correct XML from generation results.

**Approach:** Probe qmake/Qt/make tooling explicitly. Keep qmake command, import-path, result, cancellation, and diagnostic rules inside the adapter. Add new shared `RequireQmakeQuickTest*` Scale signals and convention coverage.

**Acceptance criteria:**
- [x] `CONFIG += qmltestcase` and proven `qmltest + testcase` projects enable; `QT += qmltest` alone refuses.
- [x] Qt 5/6 logger names, offscreen environment, result paths, nonzero exits, missing reports, and malformed XML are correct.
- [x] Real qmake fixture passes when guarded tooling exists and writes nothing to source.
- [x] Worker-scope verification passes and the worker creates the owned-file commit.

### Task 6: First-class VB.NET selection and .NET backend evidence

**Files:**
- Create/Modify/Test the Task 6 ownership files from the parallel contract.

**Interfaces:**
- Consumes: Task 2 `vbnet` family/identity, `.vbproj` inventory, existing .NET provider lanes.
- Produces: typed .NET backend evidence and VB discovery/selection through existing provider contracts.

**Contract inputs:** .NET 10 runner selection, MSTest/NUnit/xUnit package signals, and pinned Julie `vbnet` support.

**File ownership:** `src/Miller.Testing/Daemon/ContinuousTestProjectInventory.cs`, `src/Miller.Testing/Providers/Dotnet/DotnetTestProvider.cs`, `src/Miller.Testing/Providers/Dotnet/DotnetTestBackend.cs`, `src/Miller.Testing/Selection/ContinuousTestImpactSelector.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProjectInventoryTests.cs`, `tests/Miller.Tests/Testing/Providers/Dotnet/DotnetTestProviderTests.cs`, `tests/Miller.Tests/Testing/Selection/ContinuousTestImpactSelectorTests.cs`, `tests/Miller.Tests/Testing/Providers/Dotnet/DotnetProviderScaleTests.cs`, `tests/Miller.Tests/Fixtures/VbDotnetScale/VbDotnetScale.vbproj`, `tests/Miller.Tests/Fixtures/VbDotnetScale/UnitTests.vb`

**Serialization required:** Yes.

**Dependency reason:** Follows QML inventory changes and consumes Task 2 language/identity contracts.

**What to build:** Prove `.vbproj` frameworks, remove the fileless-C# fallback, enrich VB cases with current name/path identity, and record static/evaluated backend evidence without changing VSTest or xUnit-v3 behavior.

**Approach:** Use one typed backend discriminator separate from framework identity. Add a bounded no-build property probe for inherited MSBuild settings. Test VB qualified/parameterized names through public provider and selector interfaces.

**Acceptance criteria:**
- [x] MSTest, NUnit, xUnit v3/v2, Test SDK, MSTest.Sdk, and generic `.vbproj` inventory is honest.
- [x] `.vb` changes select exact `vbnet` cases and exclude same-stem C# cases without project-language guessing.
- [x] Existing VSTest/xUnit commands and parsing remain stable.
- [x] A guarded real VB fixture proves discovery, selected run, result parsing, and Julie-backed selection.
- [x] Worker-scope verification passes and the worker creates the owned-file commit.

### Task 7: Add Microsoft.Testing.Platform backend

**Files:**
- Create/Modify/Test the Task 7 ownership files from the parallel contract.

**Interfaces:**
- Consumes: Task 6 backend discriminator and existing .NET provider generation/process contracts.
- Produces: internal MTP discovery/run/result adapter under the existing .NET provider/framework identities.

**Contract inputs:** Nearest `global.json` `test.runner`; evaluated `UseVSTest`, `EnableMSTestRunner`, `EnableNUnitRunner`, `UseMicrosoftTestingPlatformRunner`, `TestingPlatformDotnetTestSupport`; MTP 1.7+ CLI, 2.3+ JSON list/report behavior.

**File ownership:** `src/Miller.Testing/Providers/Dotnet/DotnetTestProvider.cs`, `src/Miller.Testing/Providers/Dotnet/DotnetTestBackend.cs`, `src/Miller.Testing/Providers/Dotnet/MtpDotnetTestBackend.cs`, `src/Miller.Testing/Providers/Dotnet/MtpTestTooling.cs`, `src/Miller.Testing/Providers/Dotnet/MtpTestListParser.cs`, `tests/Miller.Tests/Testing/Providers/Dotnet/DotnetTestProviderTests.cs`, `tests/Miller.Tests/Testing/Providers/Dotnet/MtpDotnetTestBackendTests.cs`, `tests/Miller.Tests/Testing/Providers/Dotnet/MtpTestToolingTests.cs`, `tests/Miller.Tests/Testing/Providers/Dotnet/MtpTestListParserTests.cs`, `tests/Miller.Tests/Testing/Providers/Dotnet/DotnetProviderScaleTests.cs`, `tests/Miller.Tests/Fixtures/VbMtpScale/VbMtpScale.vbproj`, `tests/Miller.Tests/Fixtures/VbMtpScale/UnitTests.vb`, `tests/Miller.Tests/Fixtures/VbMtpScale/global.json`

**Serialization required:** Yes.

**Dependency reason:** Depends on Task 6 backend discriminator and stable VSTest/xUnit lanes.

**What to build:** Resolve effective MTP/VSTest selection, list tests with version-correct MTP contracts, run framework-appropriate filters after `--`, and produce generation-scoped TRX only when the extension exists.

**Approach:** Refuse ambiguous runner selection, unsupported filter providers, unknown versions, and missing report extensions with actionable diagnostics. Preserve current framework keys and factory behavior.

**Acceptance criteria:**
- [ ] .NET 10 global runner and inherited project properties resolve with documented precedence.
- [ ] MTP text/JSON listing, filters, result directories, report extension detection, and malformed/incomplete output are fail-closed.
- [ ] MSTest.Sdk defaults to MTP unless `UseVSTest=true`.
- [ ] Existing VSTest and xUnit-v3 focused tests remain byte-stable outside backend routing.
- [ ] Guarded real MTP VB/.NET fixture passes when supported tooling/packages exist.
- [ ] Worker-scope verification passes and the worker creates the owned-file commit.

### Task 8: Add Go provider

**Files:**
- Create/Modify/Test the Task 8 ownership files from the parallel contract.

**Interfaces:**
- Consumes: Task 2 Go family/identity and shared process/generation contracts.
- Produces: framework `go`, per-`go.mod` inventory, top-level test discovery, package-grouped selection, and complete JSON verdicts.

**Contract inputs:** Go 1.24+ modules/workspaces, `go list -json`, `go test -list`, `go test -json`, `test2json`, and `-run` contracts linked in the design.

**File ownership:** `src/Miller.Testing/Daemon/ContinuousTestProjectInventory.cs`, `src/Miller.Testing/Daemon/ContinuousTestProviderFactory.cs`, `src/Miller.Testing/Providers/Go/GoTestProvider.cs`, `src/Miller.Testing/Providers/Go/GoTestTooling.cs`, `src/Miller.Testing/Providers/Go/GoTestListParser.cs`, `src/Miller.Testing/Providers/Go/GoTestJsonParser.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProjectInventoryTests.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProviderFactoryTests.cs`, `tests/Miller.Tests/Testing/Providers/Go/GoTestProviderTests.cs`, `tests/Miller.Tests/Testing/Providers/Go/GoTestToolingTests.cs`, `tests/Miller.Tests/Testing/Providers/Go/GoTestListParserTests.cs`, `tests/Miller.Tests/Testing/Providers/Go/GoTestJsonParserTests.cs`, `tests/Miller.Tests/Testing/CtProviderTestSupport.cs`, `tests/Miller.Tests/Conventions/CtScaleTraitConventionTests.cs`, `tests/Miller.Tests/Testing/Providers/Go/GoTestProviderScaleTests.cs`, `tests/Miller.Tests/Fixtures/GoCtScale/go.mod`, `tests/Miller.Tests/Fixtures/GoCtScale/math_test.go`, `tests/Miller.Tests/Fixtures/GoCtWorkspaceScale/go.work`, `tests/Miller.Tests/Fixtures/GoCtWorkspaceScale/first/go.mod`, `tests/Miller.Tests/Fixtures/GoCtWorkspaceScale/first/first_test.go`, `tests/Miller.Tests/Fixtures/GoCtWorkspaceScale/second/go.mod`, `tests/Miller.Tests/Fixtures/GoCtWorkspaceScale/second/second_test.go`

**Serialization required:** Yes.

**Dependency reason:** Depends on shared contracts and follows prior inventory/factory edits.

**What to build:** Discover one project per module, use in-root `go.work` context, enumerate top-level tests, enrich exact Julie identities, group selected cases by package, and parse complete interleaved test/build JSON.

**Approach:** Require Go 1.24. Use stable project cache plus generation temp/results. Set explicit `GOWORK`, sanitize selection-affecting `GOFLAGS`, use anchored escaped `-run` plus `-count=1`, roll subtests to parents, and exclude examples/benchmarks/fuzzing.

**Acceptance criteria:**
- [ ] Nested modules remain separate; `go.work` is context only and external `GOWORK` is not inherited.
- [ ] Stable IDs include module/package/kind/name and exact Julie name/path identity when available.
- [ ] Pass/fail/skip, interleaved packages, package build failures, unknown actions, malformed lines, truncation, missing terminal events, and nonzero exits are honest.
- [ ] Cache/temp/result paths stay Miller-owned and source remains unchanged.
- [ ] Guarded real single-module and multi-module workspace Scale fixtures pass.
- [ ] Worker-scope verification passes and the worker creates the owned-file commit.

### Task 9: Documentation and Julie audit handoff

**Files:**
- Modify/Create the Task 9 ownership files from the parallel contract.

**Interfaces:**
- Consumes: final provider matrix, exact limits, all prior verification evidence.
- Produces: public install/use guidance, authoritative Miller ownership audit, bounded Julie backlog note, completed plan checkboxes.

**Contract inputs:** Approved design, Julie worktree commit `2ea9b0daa2e736f9248d8caf4c475e47dea0d522`, final code/test evidence.

**File ownership:** `README.md`, `docs/README.md`, `docs/continuous-testing.md`, `docs/findings/2026-08-27-continuous-testing-language-readiness-audit.md`, `docs/findings/2026-08-27-continuous-testing-extractor-backlog.md`, `/home/murphy/source/julie-extractors/.claude/worktrees/ct-language-audit-plan/docs/findings/2026-08-27-continuous-testing-language-readiness-audit.md`, `/home/murphy/source/julie-extractors/.claude/worktrees/ct-language-audit-plan/docs/plans/2026-08-27-continuous-testing-extractor-evidence.md`, `docs/plans/2026-08-27-ct-provider-correction-expansion-implementation-plan.md`

**Serialization required:** Yes.

**Dependency reason:** Must describe the implemented final matrix and preserve cross-workspace evidence after provider behavior stabilizes.

**What to build:** Publish the exact JavaScript/QML/VB/.NET/Go support matrix and limits. Copy the Julie finding byte-for-byte into Miller, replace the stale Julie plan with a dated non-executable backlog note using current contract shapes/helpers, and inventory the two unrelated Julie-main Goldfish checkpoints without modifying them.

**Approach:** Verify copied documents by checksum and bounded Miller reads. Update `docs/README.md` pointers. Do not remove any worktree. Run `git diff --check` and focused documentation contract tests returned by Miller impact.

**Acceptance criteria:**
- [ ] README and CT docs match implemented behavior without runner/language overclaims.
- [ ] Miller owns the preserved readiness audit and bounded follow-up backlog.
- [ ] The Julie plan is clearly marked non-executable as originally written and retains only verified backlog items.
- [ ] Julie-main Goldfish files remain untouched and are reported in final state.
- [ ] Worker-scope verification passes and the worker creates the owned-file commit.
