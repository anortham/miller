# julie-extract 2.15.0 Adoption Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Upgrade Miller to julie-extract 2.15.0 and consume its new backend-route and diagnostic output.

**Architecture:** Keep the stable schema-v4 artifact contract and existing generic consumers. Admit Symfony/Ktor through the current structural-fact seam, generalize the current pure report-warning helper, and add live compatibility proof for new extractor-owned Kotlin roles.

**Tech Stack:** .NET 10, xUnit, SQLite, julie-extract 2.15.0, shell restore/sync scripts.

**Architecture Quality:** Affected modules are `BridgeStructuralPatterns` and the extraction-report logging helper. The caller-facing interfaces remain bridge graph/trace output and one pure warning-description method; complexity stays local, tests exercise the same adapters and refresh results callers use, and architecture risk is low. Rejected shortcuts: pin-only adoption, family-specific route-provider branches, schema migration, and a new MCP tool.

## Global Constraints

- Artifact schema version remains 4, SQLite schema version remains 4, extract contract version remains 3, and report schema version remains 3.
- `symfony.route.v1` and `ktor.route.v1` use the existing normalized/effective route-template plus optional uppercase verb contract.
- Current pattern guidance says 194 pattern IDs across 36 languages; historical release notes and findings retain their historical counts.
- Miller adds no parser recognition, semantic feature, migration, or MCP tool.
- Configuration/docs compatibility work and upstream-output characterization are explicit TDD exceptions; behavior changes use red-green-refactor.

---

## Verification Strategy

**Project source of truth:** `AGENTS.md`, especially the fast/Scale split, build guard, pin restore, generated mirror, and language-parity rules.

**Worker red/green scope:** Focused `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter "FullyQualifiedName~<affected tests>"`; bridge workers also run their specific Scale test after the lead restores 2.15.0.

**Worker ceiling:** Focused test classes only. Workers do not run the full fast suite, full Scale suite, or repository build gate.

**Worker gate invariant:** Pin tests prove the published version/digest contract; bridge tests prove admission and end-to-end matching; warning tests prove successful diagnostics and unchanged partial behavior; Kotlin Scale tests prove live extractor-owned role evidence survives existing consumers.

**Lead affected-change scope:** `mcp__miller.impact` over the working diff, generated-mirror checks, focused tests for every touched subsystem, and `dotnet build Miller.slnx -c Release`.

**Branch gate:** `scripts/test.sh all`, `scripts/sync-agents.sh` plus `cmp -s CLAUDE.md AGENTS.md`, `scripts/sync-plugin-skills.sh` plus `diff -qr .agents/skills skills`, and `node --test tests/plugin/plugin-manifest.test.cjs`.

**Replay/metric evidence:** Hard gates are the 2.15.0 version probe, 30-family live parity assertion, Symfony/Ktor trace assertions, Kotlin role assertions, and zero test/build failures. Catalog row counts are compatibility evidence, not a performance metric.

**Escalation triggers:** Any contract-version drift, missing family in live extraction, language-parity regression, or fast/Scale gate failure requires investigation before completion.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp. For replay or metric evidence, also record hard-gate metrics and report-only metrics. If the same HEAD already has a passing ledger entry for the required scope, reuse that evidence instead of rerunning the same expensive gate.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Pin and current catalog guidance | None - serial | `scripts/julie-pins.json`, `src/Miller.Indexing/MillerExtractContract.cs`, `src/Miller.Server/Tools/PatternsTool.cs`, `tests/Miller.Tests/Indexing/MillerExtractContractTests.cs`, `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`, `README.md`, `CLAUDE.md`, `.agents/skills/miller-patterns-audit/SKILL.md` | Yes | The lead must restore the newly pinned 2.15.0 binary before live red/green work can run. |
| Task 2: Symfony/Ktor backend bridges | Batch A | `src/Miller.Core/Graph/BridgeStructuralPatterns.cs`, `src/Miller.Core/Graph/BackendHttpBridgeProvider.cs`, `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs`, `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs`, `tests/Miller.Tests/Indexing/SqliteBridgeReaderTests.cs`, `tests/Miller.Tests/Indexing/LiveBridgeTraceTests.cs`, `docs/contracts/trace-json-v1.md`, `.agents/skills/miller-bridge-trace/SKILL.md` | No | None - safe parallel batch. |
| Task 3: Successful extractor warning visibility | Batch A | `src/Miller.Server/Logging/PartialExtractLog.cs`, `src/Miller.Server/Hosting/IndexBootstrapService.cs`, `src/Miller.Server/Hosting/IndexerCore.cs`, `src/Miller.Server/Hosting/IndexerService.cs`, `src/Miller.Server/Tools/WorkspaceTool.cs`, `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`, `tests/Miller.Tests/Logging/PartialExtractLogTests.cs`, `tests/Miller.Tests/Server/CrossWorkspaceRefreshServiceTests.cs`, `tests/Miller.Tests/Server/WorkspaceToolTests.cs` | No | None - safe parallel batch. |
| Task 4: Kotlin test-role compatibility proof | Batch A | `tests/Miller.Tests/Indexing/LiveTestRoleEvidenceScaleTests.cs` | No | None - safe parallel batch. |

### Task 1: Pin and current catalog guidance

**Files:**
- Modify: `scripts/julie-pins.json`
- Modify: `src/Miller.Indexing/MillerExtractContract.cs`
- Modify: `src/Miller.Server/Tools/PatternsTool.cs`
- Modify: `tests/Miller.Tests/Indexing/MillerExtractContractTests.cs`
- Modify: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`
- Modify: `README.md`
- Modify: `CLAUDE.md`
- Modify: `.agents/skills/miller-patterns-audit/SKILL.md`

**Interfaces:**
- Consumes: Published julie-extract 2.15.0 target archives and verified SHA-256 digests.
- Produces: `PinnedJulieExtractVersion == "2.15.0"` and four restore-script digests; current guidance names 194 IDs across 36 languages.

**Contract inputs:** aarch64 macOS `62ceba5817c57228b51d0c5525ac7c07224f043606758ef1137c2f0e3af184b9`; x86_64 macOS `fcee9a5ddd284d4cf4d7b826e3b0e4a89ccbd15278d0621dadc2d947d8d747d0`; Linux x86_64 `ef80a6534b9afea7e531799d72fa1427f6970fdaea729903d79b3b867126e466`; Windows x86_64 `a22d53137317c43af6d198757baf9dbb6f1326b45fec39840789b37ce87a9316`.

**File ownership:** `scripts/julie-pins.json`, `src/Miller.Indexing/MillerExtractContract.cs`, `src/Miller.Server/Tools/PatternsTool.cs`, `tests/Miller.Tests/Indexing/MillerExtractContractTests.cs`, `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`, `README.md`, `CLAUDE.md`, `.agents/skills/miller-patterns-audit/SKILL.md`

**Serialization required:** Yes

**Dependency reason:** The lead must restore the newly pinned 2.15.0 binary before live red/green work can run.

**What to build:** Update the dependency pin and exact archive digests, then update only current catalog-count surfaces and assertions. Do not edit generated `AGENTS.md`/`skills` mirrors or historical release evidence; the lead owns sync.

**Approach:** Update assertions first where applicable, then the pin and current guidance. Configuration/docs changes are the approved TDD exception.

**Acceptance criteria:**
- [ ] Pin JSON and contract constant agree on 2.15.0 and all four verified digests.
- [ ] Current public/tool/agent guidance says 194 IDs across 36 languages.
- [ ] Contract versions remain 4/4/3/3 and focused pin/CLI tests pass.
- [ ] Worker-scope verification passes and the change is committed by the worker.

### Task 2: Symfony/Ktor backend bridges

**Files:**
- Modify: `src/Miller.Core/Graph/BridgeStructuralPatterns.cs`
- Modify: `src/Miller.Core/Graph/BackendHttpBridgeProvider.cs`
- Modify: `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs`
- Modify: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs`
- Modify: `tests/Miller.Tests/Indexing/SqliteBridgeReaderTests.cs`
- Modify: `tests/Miller.Tests/Indexing/LiveBridgeTraceTests.cs`
- Modify: `docs/contracts/trace-json-v1.md`
- Modify: `.agents/skills/miller-bridge-trace/SKILL.md`

**Interfaces:**
- Consumes: `symfony.route.v1` and `ktor.route.v1` facts with effective/normalized route templates, nullable uppercase verbs, and test flags.
- Produces: Both IDs in `BridgeFactPatternIds` and `BackendRoutePatternIds`; generic backend-http candidates and live parity count 30.

**Contract inputs:** Existing `StructuralRouteFactAdapter.TryReadBackendRoute` and `BackendHttpBridgeProvider.BuildCandidates`; no family-specific branch.

**File ownership:** `src/Miller.Core/Graph/BridgeStructuralPatterns.cs`, `src/Miller.Core/Graph/BackendHttpBridgeProvider.cs`, `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs`, `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs`, `tests/Miller.Tests/Indexing/SqliteBridgeReaderTests.cs`, `tests/Miller.Tests/Indexing/LiveBridgeTraceTests.cs`, `docs/contracts/trace-json-v1.md`, `.agents/skills/miller-bridge-trace/SKILL.md`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Add the two route-family constants and whitelist entries, update 28/16 counts to 30/18, and prove unit, SQLite-load, provider, and live extractor behavior.

**Approach:** Write failing whitelist/provider/live tests first against the restored 2.15.0 binary. Use the generic route adapter; Symfony/Ktor should need constants and whitelist membership only. Add high-confidence verb matches and at least one honest medium-confidence verbless case if the emitted fixture supports it.

**Acceptance criteria:**
- [ ] Unit lists contain all 30 backend families and exactly 18 plain route families.
- [ ] SQLite reader admits Symfony/Ktor facts and backend-http produces expected edges.
- [ ] Live 2.15.0 extraction emits both families and the 30-family parity gate passes.
- [ ] Current trace contract/skill guidance names Symfony and Ktor.
- [ ] Worker-scope verification passes and the change is handed to the lead per parallel-lead-commit.

### Task 3: Successful extractor warning visibility

**Files:**
- Rename/modify: `src/Miller.Server/Logging/PartialExtractLog.cs`
- Modify: `src/Miller.Server/Hosting/IndexBootstrapService.cs`
- Modify: `src/Miller.Server/Hosting/IndexerCore.cs`
- Modify: `src/Miller.Server/Hosting/IndexerService.cs`
- Modify: `src/Miller.Server/Tools/WorkspaceTool.cs`
- Modify: `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`
- Rename/modify: `tests/Miller.Tests/Logging/PartialExtractLogTests.cs`
- Modify: `tests/Miller.Tests/Server/CrossWorkspaceRefreshServiceTests.cs`
- Modify: `tests/Miller.Tests/Server/WorkspaceToolTests.cs`

**Interfaces:**
- Consumes: `ExtractReport.IsPartial`, `Errors`, and `Warnings` (`ReportDiagnostic` code/message/path/root-relative path).
- Produces: A pure `ExtractReportLog.DescribeWarning(ExtractReport)` result used by every current scan caller.

**Contract inputs:** Partial warnings preserve current failed-count/code/path semantics; healthy reports without warnings return null; successful/no-change reports with warnings name codes and affected paths.

**File ownership:** `src/Miller.Server/Logging/PartialExtractLog.cs`, `src/Miller.Server/Hosting/IndexBootstrapService.cs`, `src/Miller.Server/Hosting/IndexerCore.cs`, `src/Miller.Server/Hosting/IndexerService.cs`, `src/Miller.Server/Tools/WorkspaceTool.cs`, `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`, `tests/Miller.Tests/Logging/PartialExtractLogTests.cs`, `tests/Miller.Tests/Server/CrossWorkspaceRefreshServiceTests.cs`, `tests/Miller.Tests/Server/WorkspaceToolTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Rename/generalize the misleading partial-only helper and update all nine scan call sites. Surface `slow_file_skipped` on successful reports through logs and refresh `WarningText` without turning the scan into a failure.

**Approach:** First add failing pure-helper and refresh-result tests. Preserve the existing partial string contract, render successful warnings compactly with code/path evidence, and prove workspace refresh/open surfaces the warning through existing output.

**Acceptance criteria:**
- [ ] Healthy warning-free reports return null and partial report text remains stable.
- [ ] Successful `slow_file_skipped` reports return operator-visible code/path text.
- [ ] All scan paths call the generalized helper; refresh/workspace results surface the warning without failure.
- [ ] Worker-scope verification passes and the change is handed to the lead per parallel-lead-commit.

### Task 4: Kotlin test-role compatibility proof

**Files:**
- Modify: `tests/Miller.Tests/Indexing/LiveTestRoleEvidenceScaleTests.cs`

**Interfaces:**
- Consumes: julie-extract 2.15.0 Kotlin JUnit `test_case`, `test_container`, and `test_lifecycle` flags.
- Produces: Live round-trip assertions through `SqliteSymbolReader`, JSONL export, and existing test-evidence consumers.

**Contract inputs:** This is an approved external-contract characterization exception to red/green TDD because generic Miller consumers already support the additive flags.

**File ownership:** `tests/Miller.Tests/Indexing/LiveTestRoleEvidenceScaleTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Extend the live role fixture with Kotlin JUnit 5 container, lifecycle, and case methods plus an ordinary control. Assert typed role flags, aggregate counts, export parity, and downstream test-evidence behavior where applicable.

**Approach:** Keep the test Scale-tagged and use `ScaleTestSupport.RequireJulieServer`. Test the real 2.15.0 binary and existing public readers rather than adding Kotlin-specific Miller code.

**Acceptance criteria:**
- [ ] Live Kotlin JUnit case/container/lifecycle roles are present and ordinary controls remain non-test.
- [ ] Kotlin role counts and JSONL export match the SQLite reader.
- [ ] No Kotlin-specific production branch is added.
- [ ] Worker-scope verification passes and the change is handed to the lead per parallel-lead-commit.
