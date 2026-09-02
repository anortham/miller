# Godot/GUT Continuous Testing Project-Shadow Implementation Plan

- Status: approved
- Approved: 2026-09-02

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Ship the GDScript/GUT continuous-testing provider without allowing Godot, GUT, project code, imports, reports, user data, or temporary files to write outside supervised CT paths.

**Architecture:** Extract the accepted sbt filesystem reconciler into a closed internal `CtWorkspaceMirror`, retaining a thin behavior-compatible sbt adapter and adding a Godot policy adapter. The Godot provider discovers exact script cases from a mirrored GUT config, prepares a persistent import cache with an isolated home, selects scripts through a derived config file, and accepts only contained copied JUnit evidence.

**Tech Stack:** .NET 10, xUnit v3, `ITestProcessRunner`, `System.Text.Json`, `JUnitXmlResultParser`, Godot 4, GUT 9.

**Architecture Quality:** Shared internal mirror with two closed production policies; public `IContinuousTestProvider`, MCP, CT database, and case contracts remain unchanged. Risk is high because security-sensitive filesystem code moves and the new provider executes project code.

## Global Constraints

- Run every Godot and GUT process from a project-stable mirror under `CtGenerationPaths.CacheDirectory(workspace, "godot-workspace")`; never run from the user project.
- Redirect `HOME`, XDG data/config/cache, Windows profile/app-data, and all temp variables into `CtGenerationPaths.CacheDirectory(workspace, "godot-home")` as specified by the approved design.
- Source scope is only the directory containing the selected `project.godot`; exclude `.git`, `.miller`, `.godot`, and `.miller-gut-results` at every depth.
- The mirror owns `.git`, `.godot`, and `.miller-gut-results` at every depth; committed `.import` files remain source-owned.
- sbt keeps `sbt-workspace` plus `sbt-deps`, strict hashing, all 31 accepted shadow scenarios, ordinary canonical child-process paths, and the 260-character refusal bound.
- Godot uses `MetadataFastPath`; an unchanged warm sync copies and hashes zero bytes and does not import.
- Import when `.godot` is absent or the atomic import stamp's source digest differs; publish the stamp only after a successful `godot --headless --path <mirror> --import`.
- Project-cache overage writes `godot-workspace.over-budget.json` outside the reaped candidate and refuses the same source digest before another copy/import attempt.
- Inventory requires `project.godot` plus `addons/gut/plugin.cfg` for `gut`; `addons/gdUnit4/plugin.cfg` produces `gdunit4`; a bare project is ignored.
- Godot 4/GUT 9 is the floor. Project/plugin evidence below or outside the floor produces `gut-unsupported`, reason `Godot 4 with GUT 9 was not detected`, remedy `Upgrade or configure Godot 4 with GUT 9, or run GUT directly`.
- `gdunit4` remains recognized and refused with reason `gdUnit4 is detected; Miller CT does not yet support its runner` and remedy `run it with its own runner; CT support is planned`.
- Case ID is `gut:res://<project-relative-script>.gd`; case name and selector are the normalized `res://` path; source-path metadata is the original workspace-relative `.gd` path.
- Parse only `.gutconfig.json` fields `dirs`, `tests`, `include_subdirs`, `prefix`, and `suffix`; Miller defaults are empty lists, false recursion, `test_`, and `.gd`.
- Both focused and whole-suite runs put exact selected `res://` scripts in the derived config's `tests` array; never use GUT substring selectors or put an unbounded selection in argv.
- Use `-s`, not `--script`, with GUT 9.3.x. JUnit output is a contained `res://.miller-gut-results/*.xml` file copied atomically into the immutable generation results directory before parsing.
- Accept GUT exit 0 only with valid non-failing JUnit evidence and exit 1 only with valid failing evidence; all other exit/report combinations fail closed.
- No new MCP tool, public extension point, dependency, CT schema, retry timer, or kill policy.
- TDD is mandatory: every production behavior starts with a focused failing test that is observed failing for the expected reason.
- Tests contain no comments; production comments are limited to non-obvious external constraints.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` Testing, Build, Release, and Continuous testing sections; `docs/continuous-testing.md`; the approved Godot design; official Godot 4.4 command/data/import docs; GUT 9.3.1 command docs.

**Worker red/green scope:** Run `dotnet test --filter "FullyQualifiedName~<assigned test class>"` for each assigned class. Task 1 uses `CtWorkspaceMirrorTests`, `SbtWorkspaceShadowTests`, and `GodotProjectShadowTests`; Task 2 uses `GutToolingTests` and `GodotTestProviderTests`; Task 3 uses the inventory, factory, framework/language, whole-suite, convention, and Godot Scale classes named below.

**Worker ceiling:** Focused `dotnet test --filter` commands only. Workers do not run a bare fast suite, the complete Scale suite, Windows, security, replay, packaging, or release commands.

**Worker gate invariant:** Focused tests prove the assigned caller-facing contract, every production test was first observed red, and the task leaves the solution compilable through the test build.

**Lead affected-change scope:** After each committed task, inspect the exact diff and impacted symbols with Miller. Run the task's focused classes plus `dotnet build Miller.slnx -c Release` after Task 1 and Task 3. After all Task 8 work, run one bare `dotnet test` and the exact Godot Scale test on a real Godot 4/GUT 9 setup.

**Branch gate:** On the final unchanged release candidate run `dotnet build Miller.slnx -c Release`; one bare `dotnet test`; `scripts/test.sh scale`; `python3 scripts/tests/test_perf_recovery.py`; `scripts/test-plugin.sh`; `win-test sync miller`; `win-test run miller -- powershell -Command "dotnet test --filter 'Category!=Scale'"`; `git diff --check`; and `cmp -s CLAUDE.md AGENTS.md`. The real Godot/GUT Scale test must execute rather than skip.

**Security scope:** `security-secrets`: `gitleaks detect --source . --no-banner --log-opts=HEAD`. `security-deps`: `dotnet list Miller.slnx package --vulnerable --include-transitive`. Any secret or critical/high dependency finding blocks push and release.

**Replay/metric evidence:** Hard gates are zero copied and hashed bytes on unchanged Godot sync, skipped warm import, contained global-write roots, no source-tree mutation, real exact-script and whole-suite GUT execution, and no material regression in the established performance replay plus Godot cold/warm measures. Cold copy/import duration, candidate bytes, process time, and report-copy time are recorded; thresholds must be compared to the branch baseline before release.

**Escalation triggers:** Any shared-mirror change requires the complete sbt shadow suite and Release build. Provider-process changes require Scale. Path behavior requires Windows fast. A performance counter or timing regression requires diagnosis before broader verification continues.

**Assigned verification failure:** Workers investigate and fix assigned failures inside their packet. They stop only for a plan contradiction, an ownership conflict, or an unavailable prerequisite explicitly required by that packet.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp in the SDD ledger. Record hard-gate and report-only metrics for Godot and final replay evidence. Reuse a green scope only when HEAD and the relevant tree are unchanged.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Shared mirror and Godot shadow | None - serial | Create `src/Miller.Testing/Providers/Shared/CtWorkspaceMirror.cs`, `src/Miller.Testing/Providers/Godot/GodotProjectShadow.cs`, `tests/Miller.Tests/Testing/Providers/Shared/CtWorkspaceMirrorTests.cs`, `tests/Miller.Tests/Testing/Providers/Godot/GodotProjectShadowTests.cs`, `docs/adr/ADR-0008-ct-workspace-mirror.md`; modify `src/Miller.Testing/Providers/Jvm/SbtWorkspaceShadow.cs`, `tests/Miller.Tests/Testing/Providers/Jvm/SbtWorkspaceShadowTests.cs` | Yes | Risk-first filesystem seam must be green and reviewed before the provider consumes it. |
| Task 2: GUT tooling and provider | None - serial | Create `src/Miller.Testing/Providers/Godot/GutConfiguration.cs`, `src/Miller.Testing/Providers/Godot/GutTooling.cs`, `src/Miller.Testing/Providers/Godot/GodotTestProvider.cs`, `tests/Miller.Tests/Testing/Providers/Godot/GutToolingTests.cs`, `tests/Miller.Tests/Testing/Providers/Godot/GodotTestProviderTests.cs`; use existing `tests/Miller.Tests/Testing/Providers/Fixtures/gut-junit.xml` | Yes | Depends on Task 1's committed mirror and Godot shadow contracts. |
| Task 3: Inventory, registration, real-tool evidence, and plan reconciliation | None - serial | Create `tests/Miller.Tests/Testing/Providers/Godot/GodotTestProviderScaleTests.cs`; modify `src/Miller.Testing/Daemon/ContinuousTestProviderFactory.cs`, `src/Miller.Testing/Daemon/ContinuousTestProjectInventory.cs`, `src/Miller.Testing/ContinuousTestFrameworkSupport.cs`, `src/Miller.Testing/Selection/ContinuousTestLanguageFamily.cs`, `tests/Miller.Tests/Testing/CtProviderTestSupport.cs`, `tests/Miller.Tests/Conventions/CtScaleTraitConventionTests.cs`, `tests/Miller.Tests/Testing/Providers/WholeSuiteProviderContractTests.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProviderFactoryTests.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProjectInventoryTests.cs`, `tests/Miller.Tests/Testing/Selection/ContinuousTestImpactSelectorTests.cs`, `docs/findings/2026-09-ct-runner-surfaces.md`, `docs/plans/2026-09-01-ct-providers-jvm-ruby-php-gdscript-implementation-plan.md` | Yes | Depends on Task 2's provider; shared inventory and factory files require one serial owner. |

### Task 1: Shared mirror and Godot shadow

**Files:**
- Create: `src/Miller.Testing/Providers/Shared/CtWorkspaceMirror.cs`
- Create: `src/Miller.Testing/Providers/Godot/GodotProjectShadow.cs`
- Create: `tests/Miller.Tests/Testing/Providers/Shared/CtWorkspaceMirrorTests.cs`
- Create: `tests/Miller.Tests/Testing/Providers/Godot/GodotProjectShadowTests.cs`
- Create: `docs/adr/ADR-0008-ct-workspace-mirror.md`
- Modify: `src/Miller.Testing/Providers/Jvm/SbtWorkspaceShadow.cs`
- Modify: `tests/Miller.Tests/Testing/Providers/Jvm/SbtWorkspaceShadowTests.cs`

**Interfaces:**
- Consumes: `ContinuousTestWorkspace`, `CtGenerationPaths.CacheDirectory`, `ContinuousTestProjectInventory.WindowsPathBudget`, `ContinuousTestCoordinatorOptions.DefaultBuildCacheBudgetBytes`, and the accepted `SbtWorkspaceShadow.Sync` contract.
- Produces: internal `CtWorkspaceMirror.Sync`, `CtWorkspaceMirror.MeasureCandidateBytes`, `CtWorkspaceMirrorPolicy`, `CtWorkspaceMirrorIntegrity`, `CtWorkspaceMirrorResult`, and `GodotProjectShadow.Sync`; preserves `SbtWorkspaceShadowResult` and existing sbt callers unchanged.

**Contract inputs:** The approved design's common path/link/special-file/manifest/metadata rules, `StrictHash` for sbt, `MetadataFastPath` for Godot, Godot project/home candidate names, `.gdignore`, import-stamp path, and over-budget marker behavior.

**File ownership:** Create `src/Miller.Testing/Providers/Shared/CtWorkspaceMirror.cs`, `src/Miller.Testing/Providers/Godot/GodotProjectShadow.cs`, `tests/Miller.Tests/Testing/Providers/Shared/CtWorkspaceMirrorTests.cs`, `tests/Miller.Tests/Testing/Providers/Godot/GodotProjectShadowTests.cs`, `docs/adr/ADR-0008-ct-workspace-mirror.md`; modify `src/Miller.Testing/Providers/Jvm/SbtWorkspaceShadow.cs`, `tests/Miller.Tests/Testing/Providers/Jvm/SbtWorkspaceShadowTests.cs`

**Serialization required:** Yes

**Dependency reason:** Risk-first filesystem seam must be green and reviewed before the provider consumes it.

**What to build:** Move the filesystem algorithm, not sbt policy, into the shared module. Keep one closed data policy, two integrity modes, deterministic copy/hash metrics, metadata source digest, no-follow size measurement, atomic ownership manifest, and serialized sync per candidate. Make the sbt class a thin adapter and add the Godot shadow adapter with project/home candidates, containment mapping, `.gdignore`, import state, activity markers, and durable over-budget refusal.

**Approach:** Begin with direct shared-interface and adapter tests that fail because the new types do not exist. Move existing code without weakening any sbt scenario, then add Godot-specific red/green cases for warm metadata fast path, build-owned `.godot`, committed `.import`, global candidates, stale/import digests, marker recovery, budget refusal without recopy, and source immutability. Keep real-filesystem tests; do not introduce `IFileSystem`.

**Acceptance criteria:**
- [x] Every existing `SbtWorkspaceShadowTests` case passes with unchanged sbt paths, strict-hash repair, metrics, and dependency-cache ordering.
- [x] Shared tests cover collision, traversal, links, special files, manifest ownership/recovery, cancellation, concurrency, metadata, budget, path length, no-follow measurement, and both integrity modes.
- [x] Godot shadow tests prove exact project-root scope, `.godot` preservation, `.import` ownership, `.gdignore`, import/over-budget state, two candidate roots, zero-copy/zero-hash warm sync, and no source mutation.
- [x] ADR-0008 records the earned two-policy seam and rejected duplicate/cold/in-place alternatives.
- [x] Worker-scope verification passes and the serial worker commits only the owned files plus its checkpoint.

### Task 2: GUT tooling and provider

**Files:**
- Create: `src/Miller.Testing/Providers/Godot/GutConfiguration.cs`
- Create: `src/Miller.Testing/Providers/Godot/GutTooling.cs`
- Create: `src/Miller.Testing/Providers/Godot/GodotTestProvider.cs`
- Create: `tests/Miller.Tests/Testing/Providers/Godot/GutToolingTests.cs`
- Create: `tests/Miller.Tests/Testing/Providers/Godot/GodotTestProviderTests.cs`
- Use: `tests/Miller.Tests/Testing/Providers/Fixtures/gut-junit.xml`

**Interfaces:**
- Consumes: Task 1 `GodotProjectShadow`, `ITestProcessRunner`, `JUnitXmlResultParser`, `CtGenerationPaths`, and provider contracts in `ProviderContracts.cs`.
- Produces: `GodotTestProvider : IContinuousTestProvider`, `GodotTestProvider.IsGodotProjectFile`, case IDs `gut:res://...`, config discovery/derivation, Godot resolution/version/environment commands, import execution, GUT execution, and contained result attribution.

**Contract inputs:** Godot `GODOT` then deterministic PATH resolution; Godot major 4; GUT major 9 project evidence; Miller config defaults; `-s` GUT invocation; derived `tests` array; isolated environment; import-stamp digest; JUnit/exit agreement; empty-selection and whole-suite contracts.

**File ownership:** Create `src/Miller.Testing/Providers/Godot/GutConfiguration.cs`, `src/Miller.Testing/Providers/Godot/GutTooling.cs`, `src/Miller.Testing/Providers/Godot/GodotTestProvider.cs`, `tests/Miller.Tests/Testing/Providers/Godot/GutToolingTests.cs`, `tests/Miller.Tests/Testing/Providers/Godot/GodotTestProviderTests.cs`; use existing `tests/Miller.Tests/Testing/Providers/Fixtures/gut-junit.xml`

**Serialization required:** Yes

**Dependency reason:** Depends on Task 1's committed mirror and Godot shadow contracts.

**What to build:** Implement config parsing and exact script discovery without starting Godot. Build isolated import and GUT process requests, copy/parse only contained JUnit, aggregate method rows to script verdicts, and fail closed on every missing, unexpected, duplicate, malformed, symlinked, or exit-inconsistent result. Keep source paths out of writable destinations.

**Approach:** Drive each behavior through a failing provider/tooling test using `FakeTestProcessRunner` and real temporary files. Preserve unknown user config keys in the derived config while overriding inventory, selection, exit, colors, and report keys. Use the import digest rather than the last sync's changed flag so discovery cannot suppress a required import. Whole-suite selection lives in the derived file, satisfying the existing no-selection-on-argv contract.

**Acceptance criteria:**
- [ ] Config discovery covers explicit tests, directories, recursion, prefix/suffix, defaults, trailing commas, duplicates, escapes, case collisions, missing paths, and malformed types.
- [ ] Tooling tests cover `GODOT` precedence, PATH variants, version refusal, every isolated environment key, import command, GUT `-s` command, and no unbounded script argv.
- [ ] Provider tests cover discovery with no process, conditional import and stamp publication, warm import skip, focused/whole-suite derived configs, empty selection, source immutability, report containment/copy, green/red/skipped aggregation, inner-class shape, and every failure mode in the design.
- [ ] `GodotTestProvider` exposes no public surface beyond the provider contract and the inventory predicate.
- [ ] Worker-scope verification passes and the serial worker commits only the owned files plus its checkpoint.

### Task 3: Inventory, registration, real-tool evidence, and plan reconciliation

**Files:**
- Create: `tests/Miller.Tests/Testing/Providers/Godot/GodotTestProviderScaleTests.cs`
- Modify: `src/Miller.Testing/Daemon/ContinuousTestProviderFactory.cs`
- Modify: `src/Miller.Testing/Daemon/ContinuousTestProjectInventory.cs`
- Modify: `src/Miller.Testing/ContinuousTestFrameworkSupport.cs`
- Modify: `src/Miller.Testing/Selection/ContinuousTestLanguageFamily.cs`
- Modify: `tests/Miller.Tests/Testing/CtProviderTestSupport.cs`
- Modify: `tests/Miller.Tests/Conventions/CtScaleTraitConventionTests.cs`
- Modify: `tests/Miller.Tests/Testing/Providers/WholeSuiteProviderContractTests.cs`
- Modify: `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProviderFactoryTests.cs`
- Modify: `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProjectInventoryTests.cs`
- Modify: `tests/Miller.Tests/Testing/Selection/ContinuousTestImpactSelectorTests.cs`
- Modify: `docs/findings/2026-09-ct-runner-surfaces.md`
- Modify: `docs/plans/2026-09-01-ct-providers-jvm-ruby-php-gdscript-implementation-plan.md`

**Interfaces:**
- Consumes: Task 2 `GodotTestProvider`, project/plugin version predicates, case/result contracts, and the existing factory/inventory/framework/language conventions.
- Produces: factory key `"gut" -> (GodotTestProvider, "ct-provider:godot")`; inventory classifications `gut`, `gdunit4`, and `gut-unsupported`; framework refusal constants; `ContinuousTestLanguageFamily.Gdscript`; Godot/GUT Scale launch signals and runtime evidence.

**Contract inputs:** Exact framework reasons/remedies and case IDs from Global Constraints; `project.godot` candidate detection; both-addon behavior from the design; Scale class-level trait and `CtProviderTestSupport.RequireGodot`/`RequireGut` signals; release requires actual execution. `RequireGodot` uses `GODOT` then the production PATH order. `RequireGut` uses `MILLER_GUT_ROOT` then `<repo>/.tools/gut`, and requires `addons/gut/plugin.cfg` beneath the selected root.

**File ownership:** Create `tests/Miller.Tests/Testing/Providers/Godot/GodotTestProviderScaleTests.cs`; modify `src/Miller.Testing/Daemon/ContinuousTestProviderFactory.cs`, `src/Miller.Testing/Daemon/ContinuousTestProjectInventory.cs`, `src/Miller.Testing/ContinuousTestFrameworkSupport.cs`, `src/Miller.Testing/Selection/ContinuousTestLanguageFamily.cs`, `tests/Miller.Tests/Testing/CtProviderTestSupport.cs`, `tests/Miller.Tests/Conventions/CtScaleTraitConventionTests.cs`, `tests/Miller.Tests/Testing/Providers/WholeSuiteProviderContractTests.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProviderFactoryTests.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProjectInventoryTests.cs`, `tests/Miller.Tests/Testing/Selection/ContinuousTestImpactSelectorTests.cs`, `docs/findings/2026-09-ct-runner-surfaces.md`, `docs/plans/2026-09-01-ct-providers-jvm-ruby-php-gdscript-implementation-plan.md`

**Serialization required:** Yes

**Dependency reason:** Depends on Task 2's provider; shared inventory and factory files require one serial owner.

**What to build:** Wire GUT into the default factory and project inventory, add `gdunit4` and unsupported-GUT refusal evidence, and map `.gd`/`gdscript` as its own language family. Add Scale support and a real fixture containing two scripts, an inner class, a `class_name` dependency, and an imported asset. Replace the parent plan's false import-suppression statement with the approved shadow/import contract and record actual runtime facts in the findings document.

**Approach:** Start with failing inventory/factory/framework/language/convention/whole-suite tests, then make the smallest shared-file changes. Build the Scale fixture at runtime from committed test source plus a real GUT 9 addon found through `MILLER_GUT_ROOT` or `<repo>/.tools/gut`; `RequireGodot` and `RequireGut` remain the only launch signals. The lead must install or locate a real Godot 4/GUT 9 setup and run the exact Scale case before accepting the task for release.

**Acceptance criteria:**
- [ ] Inventory proves GUT runnable, gdUnit4 refused with its exact remedy, unsupported project/plugin versions refused, both addons represented, and bare Godot ignored.
- [ ] Factory shares the same process runner and registers `gut` with source `ct-provider:godot`.
- [ ] `.gd`, `gdscript`, and GUT case/source paths map only to `ContinuousTestLanguageFamily.Gdscript` and participate in impact selection.
- [ ] Convention and whole-suite contract tests include Godot without weakening existing providers.
- [ ] The real Godot 4/GUT 9 Scale smoke executes and proves import isolation, exact focused and whole-suite runs, inner-class JUnit attribution, source/global-path immutability, warm zero-copy/zero-hash/no-import behavior, and recorded cold/warm metrics.
- [ ] Findings and parent plan state the runtime-proven command/report behavior and the approved mirror adaptation.
- [ ] Worker-scope verification passes and the serial worker commits only the owned files plus its checkpoint.

## Completion

- [ ] Lead inline review accepts every task with no open finding.
- [ ] Miller post-edit impact matches the planned files and focused scopes.
- [ ] Task 8 focused tests, Release build, bare fast suite, real Godot/GUT Scale smoke, and deterministic performance checks are green.
- [ ] Parent plan Task 8 is checked complete and the SDD ledger contains commit-bound evidence.
- [ ] Worktree state is clean and reconciled before Task 9 documentation and final release gates begin.
