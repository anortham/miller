# Patterns Tool Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Build a read-only `patterns` MCP/CLI surface over `julie-extractors` `structural_facts`, so agents and Eros can list and search known code-shape facts without raw AST queries.

**Architecture:** Add a narrow structural-fact reader in `Miller.Indexing`, a cheap workspace-artifact resolver for read tools that do not need hydrated symbol indexes, and a `PatternsTool` renderer in `Miller.Server`. Keep parser recognition in `julie-extractors`; Miller only reads, filters, groups, and renders generic `pattern_id` facts.

**Tech Stack:** .NET 10, Microsoft.Data.Sqlite read-only access, Utf8JsonWriter/JsonDocument, MCP tool attributes, xUnit fast tests, existing Miller CLI dispatch and telemetry conventions.

**Architecture Quality:** Medium risk public contract. Affected modules are `Miller.Indexing` readers, `Miller.Server.Workspaces` routing, `Miller.Server.Tools`, CLI dispatch/capabilities, docs/contracts, and fast tests. Caller-facing interfaces are new MCP `patterns`, CLI `miller patterns`, and `patterns-json-v1`. Keep complexity local, avoid raw AST query execution, avoid per-pattern switches, and test through MCP/CLI JSON surfaces.

---

## Source Spec

Implement from [docs/plans/2026-06-09-patterns-tool-design.md](2026-06-09-patterns-tool-design.md:1).

Small design clarification already captured there: `patterns` is a normal code-read tool and should accept `workspace_id` / `--workspace-id`, plus CLI `--workspace DIR`, like `search`, `inspect`, `context`, `impact`, and `trace`.

## Current Orientation Notes

- MCP tools are explicitly registered in [Program.cs](../../src/Miller.Server/Program.cs:89).
- CLI verb dispatch lives in [CliDispatch.cs](../../src/Miller.Server/Cli/CliDispatch.cs:61).
- Capabilities JSON commands/contracts live in [CliCapabilities.cs](../../src/Miller.Server/Cli/CliCapabilities.cs:12).
- Structural-fact aggregate reading already exists in [WorkspaceHealthReader.cs](../../src/Miller.Indexing/WorkspaceHealthReader.cs:149).
- The fixture schema already has `structural_facts` in [JulieDbFixture.cs](../../tests/Miller.Tests/Indexing/JulieDbFixture.cs:953).
- Existing read-workspace routing helpers are in [ReadToolWorkspaceRouting.cs](../../src/Miller.Server/Tools/ReadToolWorkspaceRouting.cs:7).
- Search path/language glob filtering currently lives as private nested code in [SearchTool.cs](../../src/Miller.Server/Tools/SearchTool.cs:1396).

Miller MCP orientation was attempted but the current running MCP server is still on a pinned `julie-extract` v2.1.3 schema gate and refused this worktree's v2.2.0 artifact. Use Miller again during implementation after the server is aligned; this plan uses direct repo inspection as fallback.

## File Structure

Create:

- `src/Miller.Indexing/PatternFactsReader.cs` - read-only structural-fact list/summary/search reader and result records.
- `src/Miller.Server/Workspaces/IWorkspaceArtifactProvider.cs` - cheap workspace selector interface for DB-path-only read tools.
- `src/Miller.Server/Workspaces/WorkspaceArtifactContext.cs` - artifact path/freshness/workspace identity record.
- `src/Miller.Server/Tools/ToolSearchFilters.cs` - shared path/language filter extracted from `SearchTool`.
- `src/Miller.Server/Tools/PatternsTool.cs` - MCP tool, shared render core, telemetry shell.
- `docs/contracts/patterns-json-v1.md` - stable JSON contract.
- `tests/Miller.Tests/Indexing/PatternFactsReaderTests.cs` - reader behavior.
- `tests/Miller.Tests/Server/PatternsToolTests.cs` - MCP/tool rendering behavior.

Modify:

- `src/Miller.Server/Program.cs:89-96` - register `.WithTools<PatternsTool>()`.
- `src/Miller.Server/Hosting/MillerServiceRegistration.cs:112-117` - register `PatternFactsReader` and `IWorkspaceArtifactProvider`.
- `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs:7-145` and registered/current resolver sections - implement cheap artifact resolution without loading a full repository index.
- `src/Miller.Server/Tools/ReadToolWorkspaceRouting.cs:12-124` - add `WorkspaceArtifactContext` compact-banner and telemetry overloads.
- `src/Miller.Server/Tools/SearchTool.cs:393` and `1396-1520` - use shared `ToolSearchFilters` and remove private duplicate classes.
- `src/Miller.Server/Cli/CliDispatch.cs:61-90`, `155-360`, and `1287-1327` - add `patterns` verb, parser, usage/help text.
- `src/Miller.Server/Cli/CliCapabilities.cs:12-52` - advertise `patterns --json` and `patterns-json-v1`.
- `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md:21-79` and subagent primer - document `patterns`.
- `tests/Miller.Tests/Server/AgentInstructionsTests.cs:47-56` and `134-146` - include `patterns`.
- `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs:207-269` - assert capabilities command/contract and add CLI behavior tests.
- `tests/Miller.Tests/Server/SearchToolTests.cs` - rely on existing file-pattern tests after filter extraction.
- `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs` - prove artifact resolution does not hydrate full indexes.
- `docs/contracts/cli-eros-v1.md:39-62` - list `patterns --json` and link contract.
- `docs/README.md:9-18` - list `patterns-json-v1` and this implementation plan.

## Task 1: Structural-Fact Reader

**Files:**
- Create: `src/Miller.Indexing/PatternFactsReader.cs`
- Test: `tests/Miller.Tests/Indexing/PatternFactsReaderTests.cs`

**What to build:** A read-only reader over `structural_facts` that can list observed patterns, summarize grouped facts, and search one `pattern_id`. It must use `SqliteReadOnlyAccess.Open` plus `JulieSchemaGate.Verify`, and it must fail cleanly when `structural_facts` is missing.

**Approach:**
- Model rows explicitly:
  - `PatternListRow(pattern_id, label, languages, captures, count, catalog)`
  - `PatternSummaryRow(language, pattern_id, capture_name, count)`
  - `PatternMatchRow(fact_id, pattern_id, language, path, capture_name, node_kind, containing_symbol_id, span, confidence, metadata_json, metadata_error)`
  - `PatternSpan(start_line, start_column, end_line, end_column, start_byte, end_byte)`
- `label` should default to `pattern_id`; `catalog` should be `"observed"` for first slice.
- `List` should group by `pattern_id`, aggregate distinct languages/captures in deterministic order, and count rows.
- `Summary` should group by `language`, `pattern_id`, `capture_name`.
- `Search` must require a non-empty `pattern_id`, sort by `path`, `start_byte`, `structural_fact_id`, and cap results to caller-supplied `limit`.
- Metadata parsing can live in this reader or a small internal helper. Valid object metadata should be available for rendering and filtering. Malformed metadata should set `metadata_error`; it must not throw during normal unfiltered search.
- Metadata filters are exact string comparisons against top-level JSON properties. For this slice, support exactly one `where` filter from CLI and MCP, represented as a string in `key=value` form.

**Acceptance criteria:**
- [ ] Reader lists an unknown future `pattern_id` without a catalog entry.
- [ ] Reader summarizes by language/pattern/capture.
- [ ] Reader searches by `pattern_id` and returns stable ordered rows with span and confidence.
- [ ] Reader applies a metadata filter such as `name=hx-get`.
- [ ] Malformed metadata is reported on unfiltered rows and skipped by metadata-filtered search.
- [ ] Missing `structural_facts` returns a clean unavailable/invalid-operation result, not a raw SQLite stack.
- [ ] Focused reader tests fail before implementation and pass after implementation.

## Task 2: Cheap Workspace Artifact Routing

**Files:**
- Create: `src/Miller.Server/Workspaces/IWorkspaceArtifactProvider.cs`
- Create: `src/Miller.Server/Workspaces/WorkspaceArtifactContext.cs`
- Modify: `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs:7-145`
- Modify: `src/Miller.Server/Tools/ReadToolWorkspaceRouting.cs:12-124`
- Modify: `src/Miller.Server/Hosting/MillerServiceRegistration.cs:112-117`
- Test: `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs`

**What to build:** A cheap read-workspace resolver that returns DB path, workspace id/root, revision, freshness, warning, and display id without loading a `MillerRepositoryIndex`.

**Approach:**
- Add `IWorkspaceArtifactProvider.ResolveArtifact(string? workspaceId, bool ensureFresh)`.
- Implement it on `WorkspaceIndexProvider`.
- Current workspace artifact resolution can use `_holder.Snapshot()` only to read the current revision and `_currentWorkspace.CanonicalExtractDbPath`.
- Registered workspace artifact resolution should reuse `ResolveRegisteredState(workspaceId, ensureFresh)`, then return row facts directly. Do not call `GetOrLoad`, `_loadIndex`, `_loadSymbolSearch`, or content/region loaders.
- Add `ReadToolWorkspaceRouting.CompactBanner(WorkspaceArtifactContext, ...)` and `ApplyTelemetry(..., WorkspaceArtifactContext)`.
- Register `IWorkspaceArtifactProvider` to the existing singleton `WorkspaceIndexProvider`.

**Acceptance criteria:**
- [ ] Current workspace artifact resolution returns canonical DB path/root and current revision.
- [ ] Registered workspace artifact resolution honors `ensureFresh` behavior and selector errors.
- [ ] A unit test proves registered artifact resolution does not call the full-index loader.
- [ ] Existing `WorkspaceIndexProviderTests` pass.
- [ ] No hosted-service constructor starts reading bootstrap getters because the new provider is only resolved by per-call tools.

## Task 3: Shared Path/Language Filters

**Files:**
- Create: `src/Miller.Server/Tools/ToolSearchFilters.cs`
- Modify: `src/Miller.Server/Tools/SearchTool.cs:393` and `1396-1520`
- Test: `tests/Miller.Tests/Server/SearchToolTests.cs`

**What to build:** Extract the private search path/language filter into an internal shared helper so `PatternsTool` can use the same glob semantics as search.

**Approach:**
- Move the current private `SearchFilters` and `GlobMatcher` behavior into an internal type such as `ToolSearchFilters`.
- Preserve comma-separated file patterns and languages.
- Preserve basename matching when the glob has no slash.
- Update `SearchTool` to use the shared helper without changing output.
- Keep `ScopeDescription`, `HasAny`, and `Allows(path, language)` behavior.

**Acceptance criteria:**
- [ ] Existing search file-pattern/language tests pass unchanged.
- [ ] Patterns tool can call the shared helper.
- [ ] There is one implementation of this glob/language filtering behavior in `Miller.Server.Tools`.

## Task 4: Patterns MCP Tool And Rendering

**Files:**
- Create: `src/Miller.Server/Tools/PatternsTool.cs`
- Modify: `src/Miller.Server/Program.cs:89-96`
- Test: `tests/Miller.Tests/Server/PatternsToolTests.cs`
- Test: `tests/Miller.Tests/Server/AgentInstructionsTests.cs`

**What to build:** A standalone MCP tool named `patterns` with `list`, `summary`, and `search` operations.

**Approach:**
- Follow `ContentTool`'s shape: `[McpServerToolType]`, `[McpServerTool(Name = "patterns")]`, concise descriptions, try/catch returning `"patterns failed: {message}"`.
- Constructor dependencies should be `IWorkspaceArtifactProvider` and `PatternFactsReader`.
- Parameters:
  - `operation = "list"`
  - `pattern_id = null`
  - `language = null`
  - `path = null`
  - `where = null`
  - `workspace_id = null`
  - `ensure_fresh = null`
  - `limit = 50`
  - `format = "compact"`
- `workspace_id` should default `ensure_fresh` through `ReadToolWorkspaceRouting.ResolveEnsureFresh`.
- `list` and `summary` may omit `pattern_id`; `search` must require it.
- Clamp `limit` to `1..500`.
- Compact output should be short:
  - `# patterns`
  - `pattern_id  count  languages  captures`
  - Search rows grouped by path, with line/capture/pattern and a short metadata summary.
- JSON output must match the design's `schema_version: 1` shapes.
- JSON metadata should write a nested `metadata` object when metadata is valid, and `metadata_error` when malformed.
- Telemetry should set target to operation plus pattern id when available, result count, workspace/freshness, and `ErrorKind` on failure.

**Acceptance criteria:**
- [ ] MCP/tool `patterns list --json` equivalent returns `schema_version`, `operation`, and `patterns`.
- [ ] MCP/tool `patterns summary --json` returns grouped rows.
- [ ] MCP/tool `patterns search --pattern <id> --json` returns match objects with span, confidence, and metadata.
- [ ] Unknown future pattern ids work without catalog entries.
- [ ] `where=name=hx-get` filters htmx-like fixture rows without an htmx-specific branch.
- [ ] `search` without `pattern_id` returns a clean failure.
- [ ] JSON mode does not include compact workspace banners; compact mode does.
- [ ] `Program.cs` registers `.WithTools<PatternsTool>()`.
- [ ] `AgentInstructionsTests.ToolMethods` includes `PatternsTool.Patterns`.

## Task 5: CLI Command

**Files:**
- Modify: `src/Miller.Server/Cli/CliDispatch.cs:61-90`
- Modify: `src/Miller.Server/Cli/CliDispatch.cs:155-360`
- Modify: `src/Miller.Server/Cli/CliDispatch.cs:1287-1327`
- Test: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`

**What to build:** `miller patterns` CLI with `list`, `summary`, and `search` subcommands.

**Approach:**
- Add `case "patterns": return Patterns(rest, context, stdout, stderr);`.
- Parse:
  - `miller patterns list [--workspace-id SELECTOR] [--workspace DIR] [--language LANG] [--json]`
  - `miller patterns summary [--workspace-id SELECTOR] [--workspace DIR] [--pattern ID] [--language LANG] [--path GLOB] [--json]`
  - `miller patterns search --pattern ID [--workspace-id SELECTOR] [--workspace DIR] [--language LANG] [--path GLOB] [--where key=value] [--limit N] [--json]`
- Use `TryResolveReadContext` for CLI workspace selection, then call the same `PatternsTool` static/core renderer against `ctx.ExtractDbPath`.
- Because `CliOptions` currently stores one value per flag, support one `--where key=value` in the first CLI slice. Do not add repeated metadata filters unless the implementation deliberately extends `CliOptions` with tests.
- Convert `"patterns failed:"` output into exit code `3`, matching `content`.
- Usage errors return `2`.
- Help text should include `patterns`.

**Acceptance criteria:**
- [ ] `miller patterns list --json` exits `0` and returns valid contract JSON.
- [ ] `miller patterns search --pattern htmx.attribute.v1 --where name=hx-get --json` filters fixture rows.
- [ ] `miller patterns search --json` exits `2` with usage.
- [ ] `miller patterns list --workspace-id <selector> --json` routes through the selected workspace context.
- [ ] Help text lists the new command.

## Task 6: Contracts, Capabilities, And Agent Guidance

**Files:**
- Create: `docs/contracts/patterns-json-v1.md`
- Modify: `docs/contracts/cli-eros-v1.md:39-62`
- Modify: `docs/README.md:9-18`
- Modify: `src/Miller.Server/Cli/CliCapabilities.cs:12-52`
- Modify: `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md:21-79`
- Modify: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs:207-269`
- Modify: `tests/Miller.Tests/Server/AgentInstructionsTests.cs:47-56`

**What to build:** Public documentation and discoverability for the new contract.

**Approach:**
- `patterns-json-v1.md` should define `list`, `summary`, and `search` JSON fields, exit-code expectations, metadata handling, and the rule that unknown `pattern_id` values are valid observed facts.
- `cli-eros-v1.md` should add `patterns --json` to stable JSON commands and state that Eros should consume this public surface instead of private SQLite.
- `CliCapabilities.JsonCommands` should include `patterns --json`.
- `CliCapabilities.JsonContracts` should include `("patterns", "patterns --json", 1, "docs/contracts/patterns-json-v1.md")`.
- Agent instructions should explain in plain language: `patterns` finds known extractor-recognized code shapes. It is not raw AST query execution.
- Keep server instructions under the existing character budget.

**Acceptance criteria:**
- [ ] `capabilities --json` lists `patterns --json`.
- [ ] `capabilities --json` lists the `patterns` contract with schema version `1`.
- [ ] Agent instructions mention `` `patterns` `` and pass budget tests.
- [ ] Docs map points to the active contract and plan.

## Task 7: Integration Verification And Cleanup

**Files:**
- Any files touched above.

**What to build:** Final coherence pass.

**Approach:**
- Run focused tests after each task.
- After the feature slice is coherent, run the repo fast suite and build.
- Review public JSON manually using a fixture-backed CLI call where practical.
- Keep commits tight:
  - reader/routing/filter extraction,
  - tool/CLI,
  - docs/capabilities if not naturally bundled.

**Acceptance criteria:**
- [ ] `git diff --check` passes.
- [ ] `scripts/test.sh` passes.
- [ ] `dotnet build Miller.slnx -c Release` passes with 0 warnings.
- [ ] No Scale test is required unless implementation touches extraction/indexing subprocess paths beyond fixture-backed reads.

## Verification Strategy

**Project source of truth:** [AGENTS.md](../../AGENTS.md:1), [CLAUDE.md](../../CLAUDE.md:1), `scripts/test.sh`, `tests/Miller.Tests/Miller.Tests.csproj`, and `docs/contracts/`.

**Worker red/green scope:** Focused xUnit filters for the touched task, for example:

```bash
dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~PatternFactsReaderTests
dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~PatternsToolTests
dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~CliDispatchTests
```

Workers must write the failing test first, verify it fails for the expected reason, then implement.

**Worker ceiling:** Workers may run focused tests, `scripts/test.sh`, `dotnet build Miller.slnx -c Release`, and `git diff --check`. Workers should not run Scale tests unless their changes unexpectedly touch live `julie-extract` subprocess/indexing behavior.

**Worker gate invariant:** Each focused test must prove caller-visible behavior: reader rows, MCP JSON shape, compact output, CLI exit code, capabilities advertisement, or routing without full-index hydration.

**Lead affected-change scope:** After coherent implementation, run:

```bash
git diff --check
scripts/test.sh
dotnet build Miller.slnx -c Release
```

**Branch gate:** Same as affected-change scope for this slice. Add `scripts/test.sh scale` only if the implementation changes extraction, scan, schema generation, or live `julie-extract` invocation paths.

**Replay/metric evidence:** No replay or metric gate is required. Report-only evidence can include a fixture-backed `miller patterns list --json` sample if available after build.

**Escalation triggers:** Escalate if MCP tool model binding cannot represent `where`, if JSON metadata rendering requires AOT-unsafe reflection serialization, if `WorkspaceIndexProvider` artifact routing risks hosted-service lifecycle behavior, or if path filtering extraction changes existing search output.

**Assigned verification failure:** Workers stop and report when assigned verification fails unless the failure is clearly caused by their incomplete red test before implementation.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp in the worker completion note. Reuse a passing ledger entry for the same HEAD and scope instead of rerunning an expensive gate.

## Model Routing

**Project source of truth:** No `RAZORBACK.md` exists in this worktree. Use the current harness default model unless the lead explicitly dispatches with a model override.

**Strategy tier:** planning, architecture, decomposition, lead review, finding triage.
- Harness mapping: inherit.

**Implementation tier:** bounded worker tasks from this plan.
- Harness mapping: inherit.

**Mechanical tier:** docs, fixtures, rote edits, formatting, manifests with no test, replay, metric, or acceptance-gate ownership.
- Harness mapping: inherit.

**Gate-interpretation reviewer:** reading the plan, failing test, or diff to decide whether the test or implementation is wrong.
- Harness mapping: inherit.

**Escalation tier:** subtle public-contract correctness, weak tests, repeated verification failures, lifecycle/provider concerns, AOT serialization risk.
- Harness mapping: inherit.

**Worker eligibility:** Workers are eligible when assigned exactly one task area with the listed files and focused tests.

**Escalation triggers:** Same as the verification escalation triggers.

**Mechanical exclusion:** Mechanical workers cannot own failing tests, replay evidence, metrics, or acceptance gates. Split docs-only updates from evidence interpretation.

**Unsupported harness behavior:** If the harness cannot choose models per agent, use inherit and continue.

## Architecture Quality

**Affected modules:** `Miller.Indexing`, `Miller.Server.Workspaces`, `Miller.Server.Tools`, `Miller.Server.Cli`, MCP registration, docs/contracts, fast tests.

**Caller-facing interface:** MCP `patterns`, CLI `miller patterns`, `capabilities --json`, and `patterns-json-v1`.

**Depth/locality check:** The feature is read-only. Recognition of patterns remains in `julie-extractors`; Miller reads `structural_facts`, applies generic filters, and renders results. The added workspace artifact provider avoids paying full-index hydration for DB-path-only facts.

**Test surface:** Reader tests for data behavior, tool/CLI tests for public output and errors, capability/doc tests for discoverability, and existing SearchTool tests for filter extraction safety.

**Seams/adapters:** `PatternFactsReader` isolates SQLite reads. `IWorkspaceArtifactProvider` isolates workspace selection/freshness. `ToolSearchFilters` isolates shared path/language filtering.

**Rejected shortcuts:** Raw AST query execution, folding patterns into default search, per-pattern switches, catalog-required patterns, Eros/private SQLite coupling, and using `IWorkspaceIndexProvider.Resolve` when only a DB path is needed.

**Architecture risk:** medium because this adds a public contract and a new workspace routing seam.
