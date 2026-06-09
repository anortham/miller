# Miller Quality Review Goal Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Execute the Miller quality review goal from `docs/plans/2026-06-09-miller-quality-review-goal-design.md`, fixing high-confidence defects as they are found and proving completion with checklists, evidence, tests, and durable artifacts.

**Architecture:** This is an audit-and-fix program, not one feature. The lead owns the shared ledger, commits, scope control, and final verification; workers may perform bounded audits or fixes against assigned files. Defect fixes must be local to the owning module unless an architecture-quality candidate justifies a wider structural change.

**Tech Stack:** .NET 10, xUnit v3, SQLite, MCP tools, Node test runner for plugin tests, Bash/PowerShell scripts, GitHub Actions release/CI workflows, Miller's checked-in search-quality runner.

**Architecture Quality:** Medium risk. The caller-facing interface for this plan is the review outcome: phase checklists, findings ledger, fixes, tests, perf evidence, Windows status, ADRs when needed, and final verification. Every non-trivial fix must run the architecture-quality checklist and test behavior through caller-facing surfaces.

---

## Source Documents

- Design spec: `docs/plans/2026-06-09-miller-quality-review-goal-design.md`
- Repo guidance: `CLAUDE.md` and generated mirror `AGENTS.md`
- Docs map: `docs/README.md`
- Search-quality runner: `docs/search-quality-runner.md`
- Active contracts: `docs/contracts/cli-eros-v1.md`, `docs/contracts/content-corpus-v1.md`
- Current release process: `docs/release-process.md`
- Test wrapper: `scripts/test.sh`, `scripts/test.ps1`
- Plugin test wrapper: `scripts/test-plugin.sh`
- CI/release workflows: `.github/workflows/ci.yml`, `.github/workflows/release.yml`

## Files And Ownership

Shared artifacts, lead-owned:

- Create: `docs/findings/2026-06-09-miller-quality-review.md`
- Modify as needed: `docs/plans/2026-06-09-miller-quality-review-goal-design.md`
- Modify as needed: `docs/plans/2026-06-09-miller-quality-review-goal-implementation-plan.md`
- Modify as needed: `docs/README.md`
- Modify as needed: `TODO.md`
- Create/modify as needed: `.memories/YYYY-MM-DD/*.md` through Goldfish checkpoints
- Create/modify as needed: `docs/adr/ADR-NNNN-*.md` if ADRs exist, otherwise `docs/findings/architecture-decision.md`

Likely fix surfaces by phase:

- CLI and server split: `src/Miller.Server/Program.cs`, `src/Miller.Server/Cli/CliDispatch.cs`, `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`, `tests/Miller.Tests/Server/Cli/CliBinarySubprocessTests.cs`
- MCP tools: `src/Miller.Server/Tools/*Tool.cs`, `tests/Miller.Tests/Server/*ToolTests.cs`, `tests/Miller.Tests/Tools/TraceToolTests.cs`
- Workspace/read providers: `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`, `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs`, `tests/Miller.Tests/ReadToolRoutingTestSupport.cs`
- Search/read projections: `src/Miller.Indexing/*Search*`, `src/Miller.Indexing/*Sidecar*`, `tests/Miller.Tests/Indexing/*Search*Tests.cs`, `tests/Miller.Tests/Indexing/*SidecarTests.cs`
- Content corpus: `src/Miller.Indexing/Content*`, `src/Miller.Server/Tools/ContentTool.cs`, `tests/Miller.Tests/Indexing/Content*Tests.cs`, `tests/Miller.Tests/Server/ContentToolTests.cs`
- Workspace lifecycle and safety: `src/Miller.Server/Tools/WorkspaceTool.cs`, `src/Miller.Server/Tools/WorkspaceRootSafety.cs`, `tests/Miller.Tests/Server/WorkspaceToolTests.cs`, `tests/Miller.Tests/Server/WorkspaceRootSafetyTests.cs`, `tests/Miller.Tests/Server/WorkspaceSafetyTests.cs`
- Host lifecycle: `src/Miller.Server/Hosting/MillerServiceRegistration.cs`, `src/Miller.Server/Hosting/*`, `tests/Miller.Tests/Server/HostStartupRegistrationTests.cs`, `tests/Miller.Tests/Server/IndexerCoreTests.cs`
- Path/process/Windows: `src/Miller.Indexing/PathCanonicalizer.cs`, `src/Miller.Indexing/JulieExtractRunner.cs`, `src/Miller.Indexing/SingleWriterLock.cs`, `tests/Miller.Tests/Indexing/PathCanonicalizerTests.cs`, `tests/Miller.Tests/Indexing/JulieExtractRunnerTests.cs`, `tests/Miller.Tests/Indexing/JulieExtractBinaryNameTests.cs`, `tests/Miller.Tests/Indexing/SingleWriterLockTests.cs`
- Test discipline: `tests/Miller.Tests/Miller.Tests.csproj`, `tests/Miller.Tests/Conventions/ScaleTraitConventionTests.cs`, `tests/Miller.Tests/Conventions/ScriptPlatformConventionTests.cs`
- Plugin/package/release: `.claude-plugin/plugin.json`, `.cursor-plugin/plugin.json`, `.codex-plugin/plugin.json`, `.mcp.json`, `miller-plugin.json`, `bin/miller-plugin-launcher.cjs`, `tests/plugin/*.test.cjs`, `.github/workflows/release.yml`, `scripts/julie-pins.json`
- Public docs/instructions: `README.md`, `docs/README.md`, `docs/contracts/*.md`, `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`, `tests/Miller.Tests/Server/AgentInstructionsTests.cs`, `CLAUDE.md`, `AGENTS.md`

Workers must not edit shared artifacts concurrently. Worker agents return structured reports or patch scoped files; the lead updates the ledger and commits.

## Model Routing

**Project source of truth:** No repo-local `RAZORBACK.md` exists. Use harness defaults unless the active harness exposes explicit per-agent model controls.

**Strategy tier:** planning, architecture, decomposition, lead review, finding triage
- Harness mapping: inherit

**Implementation tier:** bounded fixes from this plan after a finding has evidence and a test surface
- Harness mapping: inherit

**Mechanical tier:** docs-map edits, checklist status updates, command ledger formatting, manifest version checks with no gate interpretation
- Harness mapping: inherit

**Gate-interpretation reviewer:** reading failing test output, performance deltas, search-quality metrics, Windows CI/workflow evidence, or release package evidence
- Harness mapping: inherit

**Escalation tier:** security, destructive workspace behavior, release/publish risk, repeated verification failure, architecture refactor candidates, weak tests around broad module boundaries
- Harness mapping: inherit

**Worker eligibility:** A worker may run when the task has a bounded file set, clear acceptance criteria, and no need to publish, push, release, delete user data, or rewrite history.

**Escalation triggers:** Any fix that changes public JSON contracts, release packaging semantics, workspace deletion behavior, root-safety behavior, test-suite category rules, or cross-language extractor expectations must be reviewed by the lead before commit.

**Mechanical exclusion:** Mechanical workers cannot own failing tests, replay evidence, metrics, or acceptance gates. Split mechanical docs updates from evidence interpretation.

**Unsupported harness behavior:** If the harness cannot choose models per worker, use `inherit` and continue.

## Verification Strategy

**Project source of truth:** `CLAUDE.md`, `AGENTS.md`, `scripts/test.sh`, `scripts/test.ps1`, `scripts/test-plugin.sh`, `.github/workflows/ci.yml`, `.github/workflows/release.yml`, and `docs/search-quality-runner.md`.

**Worker red/green scope:** Workers run the narrowest test that proves their finding or fix. Use focused `dotnet test` filters for .NET tests and `node --test tests/plugin/<file>.test.cjs` for plugin tests.

Example focused fast test command:

```bash
dotnet test Miller.slnx -c Release --filter "FullyQualifiedName~CliDispatchTests&Category!=Scale"
```

Example focused Scale test command:

```bash
dotnet test Miller.slnx -c Release --filter "FullyQualifiedName~LiveExtractIndexTests&Category=Scale"
```

Example plugin test command:

```bash
node --test tests/plugin/miller-plugin-launcher.test.cjs
```

**Worker ceiling:** Workers may run focused tests, `dotnet build Miller.slnx -c Release`, `scripts/test.sh`, `scripts/test-plugin.sh`, and targeted search-quality slices. Workers do not own final branch acceptance.

**Worker gate invariant:** The assigned gate must prove the caller-facing behavior affected by the fix. A passing private helper test is not enough when a CLI/MCP/documented behavior is affected.

**Lead affected-change scope:** After a coherent phase or fix batch, run `scripts/test.sh`, `scripts/test-plugin.sh` if plugin/package files changed, and any focused Scale/search-quality gate triggered by touched areas.

**Branch gate:** Before closeout, run:

```bash
dotnet build Miller.slnx -c Release
scripts/test.sh
scripts/test.sh scale
scripts/test-plugin.sh
git diff --check
```

If `.tools/julie-extract` is absent, run `scripts/restore-julie-extract.sh` first or record why Scale could not be exercised.

**Replay/metric evidence:** Search-quality and performance runs are evidence gates only when their affected surface is part of the finding. Hard gates are top1/top3/top5 expectations in the selected search-quality slice and no regression versus the recorded baseline for the touched query class. Report-only metrics include full-suite MRR movement for providers not changed by Miller.

Search-quality commands:

```bash
dotnet run -c Release --project tools/Miller.SearchQuality -- init
dotnet run -c Release --project tools/Miller.SearchQuality -- run --providers miller --repo miller --limit 5
```

Use narrower slices with `--case`, `--tag`, or `--repo` when the finding is scoped.

**Escalation triggers:** Broaden to `scripts/test.sh scale` when touching extraction/indexing, live workspace, sidecar build/read, file watcher, or package restore paths. Broaden to Windows CI evidence when touching PowerShell scripts, path canonicalization, lock/delete behavior, plugin launchers, release archives, or executable names.

**Assigned verification failure:** Workers stop and report when assigned verification fails unless the failing gate is the expected red test for their own fix.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, timestamp, and elapsed time in `docs/findings/2026-06-09-miller-quality-review.md`. For metrics, also record hard-gate metrics and report-only metrics. If the same HEAD already has a passing ledger entry for the required scope, reuse that evidence instead of rerunning the same expensive gate.

## Defect Fix Protocol

Every fix found during the review follows this sequence:

1. Record the finding in the ledger with phase, severity, evidence, suspected root cause, and intended caller-facing test surface.
2. Run @razorback:architecture-quality Gate Mode for non-mechanical fixes. For local mechanical fixes, record `No Architecture Impact` in the finding.
3. Choose the regression surface:
   - CLI behavior: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` or `tests/Miller.Tests/Server/Cli/CliBinarySubprocessTests.cs`
   - MCP tool behavior: matching `tests/Miller.Tests/Server/*ToolTests.cs` or `tests/Miller.Tests/Tools/TraceToolTests.cs`
   - Workspace/provider behavior: `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs`, `tests/Miller.Tests/Server/WorkspaceToolTests.cs`, or `tests/Miller.Tests/ReadToolRoutingTestSupport.cs`
   - Search/content/sidecar behavior: matching `tests/Miller.Tests/Indexing/*Tests.cs` plus `tests/Miller.Tests/Server/SearchToolTests.cs` or `ContentToolTests.cs` where public rendering changes
   - Windows/path/process behavior: `PathCanonicalizerTests.cs`, `WorkspaceRootSafetyTests.cs`, `JulieExtractRunnerTests.cs`, `JulieExtractBinaryNameTests.cs`, `SingleWriterLockTests.cs`, or plugin launcher tests
   - Test-suite discipline: `ScaleTraitConventionTests.cs`, `ScriptPlatformConventionTests.cs`, or `Miller.Tests.csproj`
   - Agent instructions/docs: `AgentInstructionsTests.cs`, `scripts/sync-agents.sh`, and `cmp -s CLAUDE.md AGENTS.md`
4. Write the failing test first when the defect is reproducible in tests.
5. Verify the focused test fails for the expected reason.
6. Implement the smallest fix that addresses the root cause.
7. Verify the focused test passes.
8. Run the worker red/green scope and any triggered broader gate.
9. Update the ledger with before/after evidence, final status, and residual risk.
10. Commit the coherent fix batch. Include only files belonging to that finding or phase.

## Task 0: Start The Goal Run

**Files:**
- Read: `docs/plans/2026-06-09-miller-quality-review-goal-design.md`
- Read: `docs/plans/2026-06-09-miller-quality-review-goal-implementation-plan.md`
- Read: `CLAUDE.md`
- Create: `docs/findings/2026-06-09-miller-quality-review.md`

**What to do:** Prepare the branch/run state, create the ledger, and record the exact starting point.

**Steps:**

1. Confirm the user has approved this implementation plan.
2. Confirm no unrelated dirty work is present with `git status --short`.
3. If a new branch is needed, create it with the repo prefix, for example `codex/miller-quality-review-goal`.
4. Create the findings ledger with sections for baseline, phase findings, verification ledger, deferred risks, and final summary.
5. Record HEAD, branch, current date, current Miller version, and the approved design/implementation plan links.
6. Save a Goldfish checkpoint describing goal start and scope.

**Acceptance criteria:**

- [ ] Ledger file exists and names the approved design and implementation plan.
- [ ] Ledger records branch, HEAD, date, and starting git state.
- [ ] No source code changes have been made before baseline evidence is captured.

## Task 1: Baseline And Inventory

**Files:**
- Modify: `docs/findings/2026-06-09-miller-quality-review.md`
- Read: `Directory.Build.props`
- Read: `miller-plugin.json`
- Read: `.claude-plugin/plugin.json`
- Read: `.cursor-plugin/plugin.json`
- Read: `.codex-plugin/plugin.json`
- Read: `.mcp.json`
- Read: `.miller/eval/search-quality/runs/*`

**What to do:** Establish the current state before any fixes. This becomes the comparison point for performance and test-suite claims.

**Commands:**

```bash
git status --short
git log --oneline -10
dotnet build Miller.slnx -c Release
scripts/test.sh
scripts/test.sh scale
scripts/test-plugin.sh
git diff --check
dotnet run -c Release --project src/Miller.Server/Miller.Server.csproj -- version
dotnet run -c Release --project src/Miller.Server/Miller.Server.csproj -- workspace status
dotnet run -c Release --project src/Miller.Server/Miller.Server.csproj -- search WorkspaceIndexProvider --limit 5
dotnet run -c Release --project src/Miller.Server/Miller.Server.csproj -- inspect SearchTool --depth summary
dotnet run -c Release --project src/Miller.Server/Miller.Server.csproj -- inspect SearchTool --depth full
dotnet run -c Release --project tools/Miller.SearchQuality -- run --providers miller --repo miller --limit 5
```

If Scale cannot run because `.tools/julie-extract` is missing, run:

```bash
scripts/restore-julie-extract.sh
scripts/test.sh scale
```

**Acceptance criteria:**

- [ ] Ledger records every command, result, elapsed time when relevant, and commit SHA.
- [ ] Baseline failures are entered as active findings.
- [ ] Baseline search/inspect timings are specific enough to compare after fixes.
- [ ] No fix work begins until the baseline is recorded.

## Task 2: Missing Or Shortcut Implementation Audit

**Files:**
- Modify: `docs/findings/2026-06-09-miller-quality-review.md`
- Modify as findings require: source/tests/docs listed in the finding
- Read: `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`
- Read: `README.md`
- Read: `docs/contracts/*.md`
- Read: `TODO.md`

**What to do:** Find code, docs, or tests that look complete but are missing, shallow, or misleading.

**Audit commands:**

```bash
rg -n "NotImplemented|TODO|FIXME|HACK|throw new|Unsupported|temporary|stub|placeholder|future|not yet|best effort" src tests docs scripts .github README.md TODO.md .claude-plugin .cursor-plugin .codex-plugin .mcp.json miller-plugin.json
rg -n "catch \\(|catch\\{|return \\[\\]|return Array.Empty|return null|return string.Empty|ignore|best effort|fallback" src tests
rg -n "MILLER_[A-Z0-9_]+|Environment.GetEnvironmentVariable" src tests docs scripts
rg -n "\\[McpServerTool|Name = \\\"" src/Miller.Server src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md tests/Miller.Tests/Server/AgentInstructionsTests.cs
```

**Review checklist:**

- [ ] Classify every active placeholder/stub result as fixed, deferred, accepted, or not a bug.
- [ ] Compare every MCP tool documented in `MILLER_AGENT_INSTRUCTIONS.md` to implementation and tests.
- [ ] Compare CLI help text in `CliDispatch.cs` to README examples and contract docs.
- [ ] Compare `TODO.md` open items to current code before editing the file.
- [ ] Confirm any fallback that preserves user-visible correctness has explicit tests.

**Fix rules:**

- Fix high-confidence defects immediately using the Defect Fix Protocol.
- Do not delete historical docs only because they mention old behavior; add a historical banner or update `docs/README.md` if the current/historical boundary is unclear.
- Do not broaden public contracts without a concrete caller need.

**Acceptance criteria:**

- [ ] Ledger has a classification table for audit hits.
- [ ] Every fixed shortcut has a regression test through a caller-facing surface.
- [ ] Remaining accepted/deferred items have reason and risk.
- [ ] `TODO.md` is updated only for proven active remaining work.

## Task 3: Architecture-Quality Review

**Files:**
- Modify: `docs/findings/2026-06-09-miller-quality-review.md`
- Create/modify as needed: `docs/adr/ADR-NNNN-*.md` or `docs/findings/architecture-decision.md`
- Likely inspect: `src/Miller.Core/**`
- Likely inspect: `src/Miller.Indexing/**`
- Likely inspect: `src/Miller.Server/Tools/**`
- Likely inspect: `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`
- Likely inspect: `src/Miller.Server/Cli/CliDispatch.cs`
- Likely inspect: `src/Miller.Server/Hosting/MillerServiceRegistration.cs`

**What to do:** Apply @razorback:architecture-quality to the main module boundaries and record local fixes or candidates.

**Miller orientation commands:**

Use Miller before broad file reads:

```text
miller.context({"query":"architecture review Miller Core Indexing Server tools workspace provider CLI dispatch sidecars test surfaces","workspace_id":"current","token_budget":8000})
miller.inspect({"target":"CliDispatch","workspace_id":"current","depth":"full","scope":"src/Miller.Server/Cli/CliDispatch.cs"})
miller.inspect({"target":"WorkspaceIndexProvider","workspace_id":"current","depth":"full","scope":"src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs"})
miller.inspect({"target":"SearchTool","workspace_id":"current","depth":"full","scope":"src/Miller.Server/Tools/SearchTool.cs"})
miller.inspect({"target":"InspectTool","workspace_id":"current","depth":"full","scope":"src/Miller.Server/Tools/InspectTool.cs"})
miller.inspect({"target":"MillerServiceRegistration","workspace_id":"current","depth":"full","scope":"src/Miller.Server/Hosting/MillerServiceRegistration.cs"})
```

**Checklist:**

- [ ] Review `Miller.Core` for I/O leaks and misplaced adapter logic.
- [ ] Review `Miller.Indexing` for storage policy leaking into tools.
- [ ] Review tool classes for duplicated parsing, rendering, workspace resolution, JSON shape, and error policy.
- [ ] Review CLI dispatch for accidental host startup, stdout/stderr ownership drift, and duplicated tool behavior.
- [ ] Review provider/projection seams for cheap-path/full-path separation.
- [ ] Review dashboard paths for full-index hydration risk.
- [ ] Review tests for private plumbing assertions where caller-facing tests are practical.
- [ ] Record every structural candidate with deletion test, proposed interface, test surface, risk, and recommendation.

**Acceptance criteria:**

- [ ] Findings ledger has one architecture-quality note per major module boundary.
- [ ] Every accepted candidate has a test plan and either an implemented fix or a deferred/approval note.
- [ ] Any accepted structural decision with future-agent value has an ADR or architecture-decision entry.

## Task 4: Performance Review And Fixes

**Files:**
- Modify: `docs/findings/2026-06-09-miller-quality-review.md`
- Likely modify: `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`
- Likely modify: `src/Miller.Indexing/SymbolSearchSidecar.cs`
- Likely modify: `src/Miller.Indexing/ContentCorpusSidecar.cs`
- Likely modify: `src/Miller.Indexing/FtsSymbolSearchIndex.cs`
- Likely modify: `src/Miller.Indexing/FtsTextContentSearchIndex.cs`
- Likely modify: matching tests in `tests/Miller.Tests/Server/**` and `tests/Miller.Tests/Indexing/**`

**What to do:** Measure first, then fix unnecessary hydration, repeated loads, stale cache behavior, or slow fast-suite tests.

**Baseline commands:**

```bash
/usr/bin/time -l dotnet run -c Release --project src/Miller.Server/Miller.Server.csproj -- search WorkspaceIndexProvider --limit 5
/usr/bin/time -l dotnet run -c Release --project src/Miller.Server/Miller.Server.csproj -- inspect SearchTool --depth summary
/usr/bin/time -l dotnet run -c Release --project src/Miller.Server/Miller.Server.csproj -- inspect SearchTool --depth full
/usr/bin/time -l dotnet run -c Release --project src/Miller.Server/Miller.Server.csproj -- workspace status
scripts/test.sh
```

If `/usr/bin/time -l` is unavailable in the active environment, use the platform's equivalent and record the command used.

**Review checklist:**

- [ ] Confirm `search` and summary `inspect` use projection/sidecar paths, not `RepositoryIndexLoader.Load`.
- [ ] Confirm full `inspect`, graph `context`, `impact`, and `trace` pay full-load costs only when their behavior needs it.
- [ ] Confirm `workspace status`, `workspace list`, and dashboard list/detail do not hydrate full indexes.
- [ ] Confirm `search.db` and `content.db` open paths avoid eager full-table materialization.
- [ ] Confirm caches are keyed by workspace, db path, and revision, and evict stale entries.
- [ ] Confirm sidecar stale/corrupt behavior matches active design and tests.
- [ ] Confirm fast-suite wall time stays under the wrapper ceiling and local target.

**Fix rules:**

- A performance fix must include before/after evidence.
- A performance regression test must be deterministic. If timing is inherently noisy, assert behavior that prevents the regression, such as "full loader was not called."
- Do not widen symbol ranking fields unless search-quality evidence shows the explicit content/source modes fail the task.

**Acceptance criteria:**

- [ ] Ledger records baseline and final timings.
- [ ] Every performance fix has before/after evidence.
- [ ] Fast-suite runtime remains within `scripts/test.sh` budget.
- [ ] Any remaining performance risk is ranked and linked to a concrete follow-up.

## Task 5: Windows Portability Review And Fixes

**Files:**
- Modify as needed: `src/Miller.Indexing/PathCanonicalizer.cs`
- Modify as needed: `src/Miller.Indexing/JulieExtractRunner.cs`
- Modify as needed: `src/Miller.Indexing/SingleWriterLock.cs`
- Modify as needed: `src/Miller.Server/Tools/WorkspaceRootSafety.cs`
- Modify as needed: `src/Miller.Server/Cli/CliDispatch.cs`
- Modify as needed: `bin/miller-plugin-launcher.cjs`
- Modify as needed: `scripts/*.ps1`, `scripts/*.sh`
- Modify as needed: `.github/workflows/ci.yml`, `.github/workflows/release.yml`
- Test: `tests/Miller.Tests/Indexing/PathCanonicalizerTests.cs`
- Test: `tests/Miller.Tests/Indexing/JulieExtractRunnerTests.cs`
- Test: `tests/Miller.Tests/Indexing/JulieExtractBinaryNameTests.cs`
- Test: `tests/Miller.Tests/Indexing/SingleWriterLockTests.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceRootSafetyTests.cs`
- Test: `tests/Miller.Tests/Conventions/ScriptPlatformConventionTests.cs`
- Test: `tests/plugin/miller-plugin-launcher.test.cjs`

**What to do:** Find Unix assumptions and fix them with tests that run locally where possible.

**Audit commands:**

```bash
rg -n "#!/usr/bin/env bash|chmod|/tmp/|/bin/|bash |sh |powershell|pwsh|\\.exe|Path\\.DirectorySeparatorChar|Path\\.AltDirectorySeparatorChar|Replace\\('\\/',|Replace\\('\\\\\\\\'|UseShellExecute|ProcessStartInfo|FileShare|Directory\\.Delete|ZipFile|tar|sha256|shasum" src tests scripts bin .github .claude-plugin .cursor-plugin .codex-plugin .mcp.json miller-plugin.json
rg -n "\\$\\{CLAUDE_PLUGIN_ROOT\\}|\\$\\{CURSOR_PLUGIN_ROOT\\}|workspaceFolder|userHome" .claude-plugin .cursor-plugin .codex-plugin .mcp.json bin tests/plugin
```

**Checklist:**

- [ ] Path comparisons handle separators, casing, rooted paths, drive roots, and symlink/canonical behavior intentionally.
- [ ] File locks and delete paths respect Windows handle semantics.
- [ ] Process launch code avoids shell quoting traps and executable-name assumptions.
- [ ] Plugin launcher maps Windows x64 to the correct archive and binary names.
- [ ] Release workflow packages and smokes Windows artifacts.
- [ ] PowerShell wrappers cover critical restore/test/sync/hook flows.

**Acceptance criteria:**

- [ ] Ledger separates Windows-verified, locally-tested, and unverified items.
- [ ] Windows-sensitive fixes have local tests when possible.
- [ ] Windows CI/workflow evidence is cited when touched areas depend on Windows runners.

## Task 6: Test Suite Discipline Review And Fixes

**Files:**
- Modify as needed: `tests/Miller.Tests/Miller.Tests.csproj`
- Modify as needed: `tests/Miller.Tests/Conventions/ScaleTraitConventionTests.cs`
- Modify as needed: `tests/Miller.Tests/Conventions/ScriptPlatformConventionTests.cs`
- Modify as needed: any test file found to be miscategorized
- Read: `scripts/test.sh`
- Read: `scripts/test.ps1`
- Read: `.github/workflows/ci.yml`

**What to do:** Keep the fast suite cheap and make Scale boundaries explicit.

**Audit commands:**

```bash
rg -n "RequireJulieServer|LocateJulieServer|ProcessStartInfo|dotnet run|FileSystemWatcher|Thread.Sleep|Task.Delay|\\[Trait\\(\"Category\",\\s*\"Scale\"\\)\\]" tests/Miller.Tests
rg -n "VSTestTestCaseFilter|Category!=Scale|Category=Scale|FAST_BUDGET_SECONDS|windows-fast" tests/Miller.Tests/Miller.Tests.csproj scripts/test.sh scripts/test.ps1 .github/workflows/ci.yml
scripts/test.sh
```

**Checklist:**

- [ ] Bare `dotnet test` defaults to `Category!=Scale`.
- [ ] `scripts/test.sh` preserves the fast-suite budget tripwire.
- [ ] CI preserves the fast-suite budget tripwire.
- [ ] Every julie-spawning test uses `ScaleTestSupport.RequireJulieServer()` and is class-tagged Scale.
- [ ] Scale tests skip when `.tools/julie-extract` is absent.
- [ ] Slow pure tests are sped up or moved to Scale with a reason.
- [ ] xUnit analyzer warnings are fixed, not suppressed.

**Acceptance criteria:**

- [ ] Fast-suite time is recorded before and after changes.
- [ ] No subprocess-heavy test remains in the default suite.
- [ ] `ScaleTraitConventionTests` and script convention tests pass.

## Task 7: Package, Plugin, Release, And Docs Integrity

**Files:**
- Modify as needed: `.github/workflows/release.yml`
- Modify as needed: `scripts/release-promote.sh`
- Modify as needed: `scripts/julie-pins.json`
- Modify as needed: `miller-plugin.json`
- Modify as needed: `.claude-plugin/plugin.json`
- Modify as needed: `.cursor-plugin/plugin.json`
- Modify as needed: `.codex-plugin/plugin.json`
- Modify as needed: `.mcp.json`
- Modify as needed: `bin/miller-plugin-launcher.cjs`
- Modify as needed: `README.md`
- Modify as needed: `docs/README.md`
- Modify as needed: `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`
- Test: `tests/plugin/plugin-manifest.test.cjs`
- Test: `tests/plugin/miller-plugin-launcher.test.cjs`
- Test: `tests/Miller.Tests/Indexing/MillerExtractContractTests.cs`
- Test: `tests/Miller.Tests/Server/AgentInstructionsTests.cs`

**What to do:** Ensure install, plugin, package, and docs surfaces describe and test reality.

**Audit commands:**

```bash
scripts/test-plugin.sh
dotnet test Miller.slnx -c Release --filter "FullyQualifiedName~MillerExtractContractTests&Category!=Scale"
dotnet test Miller.slnx -c Release --filter "FullyQualifiedName~AgentInstructionsTests&Category!=Scale"
rg -n "<Version>|version|miller-.*tar|miller-.*zip|sha256|promote_run_id|pipefail|grep -q|CLAUDE_PLUGIN_ROOT|CURSOR_PLUGIN_ROOT" Directory.Build.props miller-plugin.json .claude-plugin .cursor-plugin .codex-plugin .mcp.json bin scripts .github docs README.md
```

**Checklist:**

- [ ] Version surfaces are aligned.
- [ ] Release workflow matrix matches `scripts/julie-pins.json`.
- [ ] Package contents documented in README match workflow/package reality.
- [ ] Plugin manifests use the correct launcher and root variables for Claude, Cursor, and Codex.
- [ ] Release smoke checks avoid known brittle `echo | grep -q` under `pipefail` patterns.
- [ ] No release is published or altered during this review without explicit approval.
- [ ] Agent instructions document every MCP tool.
- [ ] `CLAUDE.md` and `AGENTS.md` are synced if guidance changes.

**Acceptance criteria:**

- [ ] Plugin tests pass.
- [ ] Contract/instruction tests pass.
- [ ] Any docs changes are backed by live facts or clearly scoped as current source-checkout behavior.

## Task 8: Security, Safety, And Failure-Mode Review

**Files:**
- Modify as needed: `src/Miller.Server/Tools/WorkspaceRootSafety.cs`
- Modify as needed: `src/Miller.Server/Tools/WorkspaceTool.cs`
- Modify as needed: `src/Miller.Server/Tools/EditTool.cs`
- Modify as needed: `src/Miller.Server/Hosting/EditWriteLock.cs`
- Modify as needed: `src/Miller.Indexing/ContentCorpusExternalStore.cs`
- Modify as needed: `src/Miller.Indexing/ContentCorpusExportReader.cs`
- Modify as needed: `src/Miller.Server/Telemetry/*`
- Modify as needed: `bin/miller-plugin-launcher.cjs`
- Test: `tests/Miller.Tests/Server/WorkspaceRootSafetyTests.cs`
- Test: `tests/Miller.Tests/Server/EditToolTests.cs`
- Test: `tests/Miller.Tests/Server/EditWriteLockTests.cs`
- Test: `tests/Miller.Tests/Indexing/ContentCorpusExternalStoreTests.cs`
- Test: `tests/Miller.Tests/Indexing/ContentCorpusExportReaderTests.cs`
- Test: `tests/plugin/miller-plugin-launcher.test.cjs`

**What to do:** Catch behavior that could damage user work, hide broken state, or expose sensitive data.

**Audit commands:**

```bash
rg -n "Directory\\.Delete|File\\.Delete|File\\.Write|WriteAllText|Move\\(|Copy\\(|ZipFile|Extract|workspace remove|root safety|sensitive|secret|token|password|api[_-]?key|Environment|Telemetry|catch \\(" src tests bin docs README.md
```

**Checklist:**

- [ ] Root safety refuses home, filesystem roots, drive roots, and system dirs.
- [ ] Workspace remove/edit/apply paths refuse unsafe live or stale states.
- [ ] Archive extraction and plugin download paths resist path traversal.
- [ ] Content import/export enforces size, source, and path boundaries.
- [ ] Logs and telemetry do not capture secrets-like values beyond intended paths and tool facts.
- [ ] Correctness-affecting failures surface loudly enough for agents to act.

**Acceptance criteria:**

- [ ] High-confidence safety defects are fixed with caller-facing tests.
- [ ] Accepted risks are documented with rationale.
- [ ] No destructive behavior is broadened without explicit user approval.

## Task 9: Final Closeout

**Files:**
- Modify: `docs/findings/2026-06-09-miller-quality-review.md`
- Modify: `docs/plans/2026-06-09-miller-quality-review-goal-design.md`
- Modify as needed: `TODO.md`
- Modify as needed: `docs/README.md`
- Create/modify as needed: Goldfish checkpoint file through `mcp__goldfish__checkpoint`

**What to do:** Prove the goal is complete and leave durable state.

**Final commands:**

```bash
dotnet build Miller.slnx -c Release
scripts/test.sh
scripts/test.sh scale
scripts/test-plugin.sh
git diff --check
dotnet run -c Release --project tools/Miller.SearchQuality -- run --providers miller --repo miller --limit 5
dotnet run -c Release --project src/Miller.Server/Miller.Server.csproj -- version
dotnet run -c Release --project src/Miller.Server/Miller.Server.csproj -- workspace status
dotnet run -c Release --project src/Miller.Server/Miller.Server.csproj -- search WorkspaceIndexProvider --limit 5
dotnet run -c Release --project src/Miller.Server/Miller.Server.csproj -- inspect SearchTool --depth summary
```

If relevant files changed, also run:

```bash
scripts/sync-agents.sh
cmp -s CLAUDE.md AGENTS.md
```

**Checklist:**

- [ ] Every design checklist item is checked, deferred, accepted, or marked not applicable with evidence.
- [ ] Every finding has final status.
- [ ] High/blocker findings are fixed or explicitly deferred with user-visible rationale.
- [ ] Performance findings include before/after evidence.
- [ ] Windows-sensitive surfaces are verified, CI-backed, or listed as unverified risk.
- [ ] `TODO.md` contains only active remaining work from this goal.
- [ ] Final Goldfish checkpoint records what changed, why, how verified, and what remains.
- [ ] Final commit history is coherent and scoped.

**Acceptance criteria:**

- [ ] Branch gate passes or failures are documented as unresolved blockers.
- [ ] Findings ledger can stand alone without chat context.
- [ ] Final response summarizes fixes, verification, performance deltas, Windows status, and unresolved risks.

## Commit Cadence

- Commit the initial ledger after Task 1 if baseline evidence is substantial.
- Commit each coherent fix batch after focused verification passes.
- Commit docs/ledger updates after each phase if they represent meaningful progress.
- Save a Goldfish checkpoint before any commit that should survive handoff.
- Never mix unrelated phase fixes in one commit unless one defect spans both surfaces.

## Stop Conditions

Stop and ask the user only when:

- A fix would publish, push, retag, delete, or overwrite a release.
- A fix would rewrite git history or delete unrelated user work.
- A finding requires a product decision, not a quality decision.
- A structural candidate is useful but not required to fix the current defect.
- Required credentials or external access are missing.
- The same blocker has been hit repeatedly and no plan-consistent path remains.

Otherwise, keep moving through the plan until the goal is complete.

