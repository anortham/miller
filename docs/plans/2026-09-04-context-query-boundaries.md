# Context Query Boundaries Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `razorback:subagent-driven-development` when subagent delegation is available. Fall back to `razorback:executing-plans` for tightly-sequential or no-delegation runs.

**Goal:** Extract ContextTool's query orchestration, bundle construction, and rendering into three cohesive internal components while preserving every existing MCP, CLI, evaluation, freshness, semantic, telemetry, cancellation, and output behavior.

**Architecture:** `ContextTool` remains the public MCP adapter and owns its public constructors and `Context`/`ContextWithCancellation` metadata. `ContextQueryService` resolves the routed workspace read context and coordinates one request; `ContextBundleBuilder` owns pivot selection, rescue, graph, body, reference, and sufficiency logic; `ContextBundleRenderer` owns compact/JSON rendering and output budgets. These components stay under `src/Miller.Server/Tools/Context/` and may use existing server-side types; no I/O-dependent type moves into `Miller.Core`.

**Tech Stack:** .NET 10, C#, MCP tool metadata, existing `IWorkspaceIndexProvider`, `WorkspaceReadContext`, `ContextQueryRetrieval`, semantic arm/vector sidecar seams, `ToolOutputBudget`, xUnit focused tests, and existing CLI/evaluation entry points.

**Architecture Quality:** This is an internal responsibility-boundary refactor with behavior preservation as the acceptance criterion. The risk is high because the current class combines routing, retrieval, semantic admission, graph/body reads, cancellation, telemetry, and rendering. Keep the existing seams and types until characterization parity proves each move; do not split by arbitrary line count or introduce plugin/provider interfaces.

## Global Constraints

- Preserve the public `ContextTool` constructors and MCP `Context`/`ContextWithCancellation` signatures, attributes, descriptions, defaults, schema, and cancellation-token behavior.
- Preserve `ContextTool` output byte-for-byte for the same fixture, arguments, index generation, semantic mode, and freshness state in both compact and JSON formats.
- Preserve all scoring limits and gates, including `SemanticSeedGateLimit = 2`, `TermRescuePromotionReadLimit = 8`, and `TermRescueRetrievalLimit = 6`; these are distinct behaviors and must not be unified.
- Preserve `ContextQueryRetrieval`, its index identity guard, memoization keys, and retrieval/read-count behavior. Reuse the existing caches; do not add a second cache.
- Preserve `ensure_fresh`, workspace routing, semantic-off zero-work behavior, semantic failure/degradation behavior, telemetry fields, empty-result diagnostics, output budgets, dispositions, and next-step hints.
- Preserve cancellation phase order and the rule that only completed phases are reported. Preserve the existing read counts and cold term-rescue versus warm retrieval behavior.
- Keep all I/O-dependent types and implementations in `Miller.Server`; do not move them into pure `Miller.Core` and do not invent plugin interfaces.
- Migrate all existing CLI direct static calls and `eval/semantic-model-eval/Program.cs` callers to the shared internal service/builder path. Temporary internal forwarders may bridge commits but must be deleted before completion.
- Capture compact and JSON golden outputs from the current implementation before moving code. Never establish parity by comparing two wrappers around the same newly moved implementation.
- Use public caller-boundary tests plus existing behavioral fixtures. Retain `ContextToolTests`, `ContextQueryRetrievalTests`, `ContextPivotRankerTests`, `AgentInstructionsTests`, `McpToolSchemaTests`, `CliDispatchTests`, and real-extract Scale coverage.
- Do not change public schemas, scoring limits, telemetry contracts, error codes, freshness policy, semantic defaults, or protocol contracts as part of this plan.
- Inner-loop verification uses focused tests. Run the fast suite once at the coherent task boundary; run Scale only if the read/index path changes require it.

## Current Code Map and Interfaces

The public adapter is `src/Miller.Server/Tools/ContextTool.cs:27`; `Context` begins at line 81 and `ContextWithCancellation` at line 111. `ContextWithCancellation` currently performs request validation, workspace routing, read-context resolution, telemetry activation, cancellation checkpoints, token-budget clamping, retrieval and semantic admission, selection, and final rendering. The internal selection and rendering paths include `RunActionable` overloads at lines 829, 863, and 899, reference-aware paths at lines 1224 and 1272, source-rescue seed loading at line 720, nested model records from line 1555, and JSON rendering at lines 3227–3239.

The existing `ContextQueryRetrieval` at `src/Miller.Server/Tools/ContextQueryRetrieval.cs:17` is already the memoized retrieval seam. Its `For` method must remain the only way a request reuses a memo, and its `RetrievalCount` is an important test seam. The CLI entry point is `src/Miller.Server/Cli/CliDispatch.cs:2020`; its source-rescue helper at line 2133 currently refers to `ContextTool.ContextSourceSeed`. The semantic-model evaluator invokes the server tools from `eval/semantic-model-eval/Program.cs` and must use the same moved path.

The current behavioral fixture includes an `OrderController` → `OrderService` → `OrderRepo` graph and a test caller in `tests/Miller.Tests/Server/LiveContextImpactTests.cs:19`. The current test corpus also covers source rescue, sufficiency refusal, ambiguity, cancellation, budgets, and usage evidence. These fixtures are the proof boundary for the extraction.

## Baseline and Golden Artifact Format

Before moving implementation bodies, add a characterization harness that calls the current public `ContextTool` boundary with a fixed in-memory index and the existing fixture data. Store the result for each case as a test-owned expected value or generated artifact with:

- operation (`Context` or `ContextWithCancellation`), format (`compact` or `json`), query, token budget, max hops, entry symbols, failing test, reference mode, reference depth, exclude-tests, semantic policy, and refresh mode;
- exact output string and UTF-8 byte count;
- expected disposition, selected pivot ids/names, next actions, phase sequence, retrieval count, source-read count, and cancellation outcome when the fixture exposes them;
- the fixture/index identity and the current implementation commit used to capture it.

The baseline must be captured from the pre-move `ContextTool` implementation. A later test that invokes a new component directly is supplemental; it cannot replace public-boundary comparison. If a nondeterministic field exists, assert the documented stable projection while recording why the raw value cannot be compared.

## Verification Strategy

**Project source of truth:** `AGENTS.md`, `docs/contracts/context-json-v1.md`, `src/Miller.Server/Tools/ContextTool.cs`, `src/Miller.Server/Tools/ContextQueryRetrieval.cs`, and the existing Context/CLI/evaluation tests.

**Worker red/green scope:** Characterization tests first, then focused ContextTool and retrieval tests after each extraction task:

```bash
dotnet test --filter "FullyQualifiedName~ContextToolTests"
dotnet test --filter "FullyQualifiedName~ContextQueryRetrievalTests"
dotnet test --filter "FullyQualifiedName~ContextPivotRankerTests"
dotnet test --filter "FullyQualifiedName~McpToolSchemaTests"
dotnet test --filter "FullyQualifiedName~AgentInstructionsTests"
dotnet test --filter "FullyQualifiedName~CliDispatchTests"
```

**Worker ceiling:** The focused groups above plus `dotnet build eval/semantic-model-eval/Miller.SemanticModelEval.csproj -c Release` if the evaluator caller is touched. The evaluator has no test project. Do not run the full fast suite or Scale tests in the inner loop.

**Worker gate invariant:** Every task preserves public compact/JSON output, schema and description metadata, cancellation phase order, retrieval/read counts, semantic-off behavior, and freshness routing for its owned cases.

**Lead affected-change scope:** After the five tasks, run the focused Context, retrieval, schema, guidance, and CLI groups together, inspect the diff impact, and build `eval/semantic-model-eval/Miller.SemanticModelEval.csproj -c Release` when its caller changes. Confirm no `ContextTool.*` forwarders or duplicate rendering paths remain.

**Branch gate:** `dotnet build Miller.slnx -c Release` with zero warnings/errors, then one bare `dotnet test` fast suite at the task boundary. Run `scripts/test.sh scale` only if the final diff changes real index/read behavior or the lead's impact analysis selects the Scale path.

**Security scope:** `none declared` for this behavior-preserving internal refactor.

**Replay/metric evidence:** Golden compact/JSON parity, schema parity, cancellation phase order, retrieval/read counts, semantic-off zero-work, freshness behavior, and focused tests are hard gates. Class size, number of files, runtime latency, and any incidental allocation change are report-only; no numeric class-size quota defines success.

**Escalation triggers:** Any output byte difference without a documented pre-existing nondeterminism; any changed semantic admission or retrieval count; a moved type requiring Core I/O; a public constructor/signature change; a new plugin interface; a new schema/telemetry/error field; a CLI/evaluation caller that cannot share the service; or a failed real-extract behavior test.

**Assigned verification failure:** Preserve the failing output and fixture identity, determine whether the failure is a movement error or a pre-existing baseline issue, and stop the owned task if the behavior cannot be restored without changing the contract.

**Verification ledger:** Record command, scope, commit SHA, fixture/index identity, test result, output hash, retrieval/read counts, phase sequence, and UTC timestamp. For each parity case record the pre-move and post-move output hashes and the caller boundary used.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Capture public-boundary baseline | None - serial | `tests/Miller.Tests/Server/ContextToolTests.cs` and explicitly owned golden/fixture files only | Yes | Must observe the pre-move implementation before any wrapper or body extraction. |
| Task 2: Extract query orchestration | None - serial | Create `src/Miller.Server/Tools/Context/ContextQueryService.cs`; modify `ContextTool.cs`, CLI/evaluation adapters, and focused routing tests | Yes | Depends on immutable baseline and must establish the shared resolved-read-context path before builder migration. |
| Task 3: Extract bundle construction | None - serial | Create `src/Miller.Server/Tools/Context/ContextBundleBuilder.cs` and model file only if required; modify `ContextTool.cs`, CLI/evaluation callers, and behavioral tests | Yes | Builder depends on Task 2's service context and must preserve the existing retrieval/cache seam. |
| Task 4: Extract rendering | None - serial | Create `src/Miller.Server/Tools/Context/ContextBundleRenderer.cs`; modify adapter/builder call sites and output tests | Yes | Renderer must consume the completed builder result and be proven against Task 1 compact/JSON goldens. |
| Task 5: Remove forwarders and complete integration verification | None - serial | Cleanup in `src/Miller.Server/Tools/ContextTool.cs`, CLI/evaluation callers, project files if needed, and final focused tests | Yes | Requires all moved paths to be live before deleting compatibility forwarders and running final impact/build gates. |

## Task 1: Capture public-boundary characterization and goldens

**Files:**

- Modify: `tests/Miller.Tests/Server/ContextToolTests.cs` only for characterization tests and stable expected projections.
- Read: `src/Miller.Server/Tools/ContextTool.cs`, `src/Miller.Server/Tools/ContextQueryRetrieval.cs`, `tests/Miller.Tests/Server/ContextToolTests.cs`, `tests/Miller.Tests/Server/ContextQueryRetrievalTests.cs`, `tests/Miller.Tests/Server/ContextPivotRankerTests.cs`, `tests/Miller.Tests/Server/LiveContextImpactTests.cs`.
- Do not create production components in this task.

**Contract inputs:** Public `ContextTool.Context` and `ContextWithCancellation`; existing `ContextToolTests` fixtures; `ToolOutputBudget`; `ContextQueryRetrieval.RetrievalCount`; existing phase observer and read-count seams.

**Approach:**

1. Inspect the current public methods and fixture helpers. Select cases that cover empty/invalid input, ordinary symbol context, actionable graph context, source rescue, reference-aware usage, semantic-off, budget-bounded output, ambiguity, and cancellation.
2. Add a test helper that invokes the current public method and records compact and JSON output plus stable diagnostics. Keep the helper at the caller boundary; do not call private methods as the primary oracle.
3. Capture at least one real fixture case using the existing `OrderController`/`OrderService`/`OrderRepo` graph and one source-rescue case. Capture cold and warm term-rescue cases separately so `ContextQueryRetrieval` reuse is visible.
4. Capture cancellation using the existing phase observer and assert only completed phases, preserving the current order. Capture retrieval and source read counts where existing fakes expose them.
5. Run focused tests and commit the baseline result through the lead's normal commit mode before extraction begins. This commit contains characterization tests/fixtures only; it contains no new Context components or production delegation.

**Meaningful characterization example:**

```csharp
[Fact]
public void Public_context_json_fixture_is_the_pre_move_oracle()
{
    var (index, resolver) = BuildFixture();

    string actual = ContextTool.Run(index, resolver,
        query: "OrderService", tokenBudget: 4000, maxHops: 1,
        entrySymbols: null, failingTest: null, stackTrace: null,
        json: false, out int count, out _);

    Assert.Contains("OrderService", actual);
    Assert.Contains("OrderController", actual);
    Assert.Contains("OrderRepo", actual);
    Assert.True(count >= 4);
}
```

This test must fail if the current output changes; it must not construct the future renderer. If the exact output contains a documented unstable field, compare the existing stable projection and assert the field's contract separately.

**Expected outputs/assertions:** focused tests pass against the unchanged implementation; the baseline records stable compact/JSON outputs, disposition, selected pivots, phase order, and read counts; no production file changes exist.

**Acceptance:**

- [x] Compact and JSON pre-move goldens cover ordinary, actionable, source-rescue, reference-aware, semantic-off, budget-bounded, and empty paths.
- [x] Cancellation phase order and completed-phase behavior are captured.
- [x] Cold/warm retrieval and source-read counts are captured where existing seams expose them.
- [x] The oracle is produced by the current public boundary and is committed before movement.

## Task 2: Extract resolved query orchestration

**Files:**

- Create: `src/Miller.Server/Tools/Context/ContextQueryService.cs`.
- Modify: `src/Miller.Server/Tools/ContextTool.cs` to retain public constructors and MCP metadata while delegating the resolved request.
- Modify: `src/Miller.Server/Cli/CliDispatch.cs:2020` and its context-specific helper path as needed to consume the shared service/builder route.
- Modify: `eval/semantic-model-eval/Program.cs` only where it directly invokes the old ContextTool static path.
- Modify focused routing/cancellation tests owned by this task.

**Interfaces:** Define an internal server-side service with a request/result shape that carries the already parsed context arguments, resolved `WorkspaceReadContext`/index, telemetry and cancellation seams, and the builder-ready request. `ContextTool` constructs it from its existing injected dependencies; the static CLI path constructs the same service from its already resolved `WorkspaceContext`, index, graph, resolver, and one-shot/fact-cache policy. Do not force CLI through the resident MCP provider or discard `OpenForOneShotCli` bounded-facts behavior. Use existing types where possible. Do not expose the service through MCP, add a new tool, or move the public `ContextTool` constructor contract.

**Approach:**

1. Inspect `ContextWithCancellation` fully and map each operation into routing/resolve, query preparation, builder invocation, and render invocation. Preserve `ReadToolWorkspaceRouting.ResolveRefreshMode`, effective token-budget clamping, `TelemetryContext`, and `WorkspaceIndexProvider.Resolve` exactly.
2. Write focused tests around the service through `ContextTool.ContextWithCancellation` and the CLI caller. Initially route through an internal forwarder that calls the existing body; prove no change in refresh mode, semantic-off short circuit, cancellation before resolve, telemetry activation, and preservation of CLI one-shot bounded-facts selection.
3. Move only request orchestration into `ContextQueryService`: validate arguments, resolve the read context, create/reuse `ContextQueryRetrieval.For(index, existing)`, and pass a request to the existing builder logic. Keep result rendering in the old path until Task 4.
4. Migrate CLI `Context` and its source-rescue seed path to the shared request/result route. Preserve CLI's output formatting and error exit behavior.
5. Update the semantic-model evaluator's direct ContextTool call to use the shared route without adding an evaluator-only behavior.
6. Run focused Context, CLI, schema, guidance, and retrieval tests. Compare every Task 1 golden after the service is live.

**Meaningful red/green example:**

```csharp
[Fact]
public void Context_adapter_resolves_the_requested_workspace_before_building()
{
    string root = Path.Combine(Path.GetTempPath(), "miller-context-cancel-" + Guid.NewGuid().ToString("N"));
    var provider = new RecordingWorkspaceIndexProvider(
        ReadToolRoutingTestSupport.ContextFor(EmptyIndex(), "current.db", "current-ws", root));
    var tool = new ContextTool(provider);
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();

    Assert.Throws<OperationCanceledException>(() => tool.ContextWithCancellation(
        "OrderService", cancellationToken: cancellation.Token));
}
```

Add the test before delegation and observe the current result; after extraction the same test proves the shared service received the same route. Do not invent a different expected mode.

**Expected outputs/assertions:** MCP and CLI callers produce the same outputs and diagnostics; invalid/cancelled requests do not resolve an index; semantic-off performs zero semantic work; retrieval/read counts remain unchanged.

**Acceptance:**

- [x] `ContextTool` remains the public adapter with unchanged constructors, metadata, and public methods.
- [x] MCP, CLI, and evaluator callers share one internal resolved-query route.
- [x] Freshness, cancellation, telemetry, semantic-off, and budget behavior match the baseline.
- [x] No temporary forwarder is used as the final shared implementation boundary.

## Task 3: Extract bundle construction

**Files:**

- Create: `src/Miller.Server/Tools/Context/ContextBundleBuilder.cs`.
- Create `src/Miller.Server/Tools/Context/ContextBundleModel.cs` only if existing nested records cannot be shared cleanly; keep all such types internal and server-side.
- Modify: `src/Miller.Server/Tools/ContextTool.cs` to delegate `Run`, `RunActionable`, `RunReferenceAwareActionableWithCancellation`, candidate selection, source rescue, graph/body reads, and sufficiency decisions.
- Modify: `src/Miller.Server/Tools/ContextQueryRetrieval.cs` only if required to preserve its current internal access; do not change its keys or semantics.
- Modify: `tests/Miller.Tests/Server/ContextToolTests.cs`, `ContextQueryRetrievalTests.cs`, and `ContextPivotRankerTests.cs` for public-boundary and existing behavioral coverage.

**Interfaces:** The builder accepts the resolved index/graph/read seams, parsed request, `ContextQueryRetrieval`, semantic seeds/arm results, cancellation and phase observers, and returns the existing internal result facts. Its result must expose the existing `ContextEvidenceDisposition` object (`status` plus `reason`), selected items, diagnostics, and bounded body facts needed by the renderer; it must not materialize unbounded source text. It must not own workspace resolution, MCP metadata, telemetry persistence, or JSON/compact serialization.

**Approach:**

1. Inventory nested records and private methods from `ContextTool.cs:1555` onward. Keep their fields and ordering; move a type only when both the builder and renderer need it.
2. Move ordinary pivot selection and body reads first, preserving `BuildCandidates`, `Select`, rank tie-breaking, and body-read freshness guards. Compare Task 1 ordinary and actionable goldens.
3. Move source rescue and term-promotion paths. Keep `TermRescuePromotionReadLimit = 8`, `TermRescueRetrievalLimit = 6`, and the existing `ContextQueryRetrieval` memo. Add/retain tests that demonstrate cold term rescue and warm reuse have the same `RetrievalCount` and source reads.
4. Move semantic seed admission without widening the gate. `SemanticSeedGateLimit = 2` remains the pivot membership window and must not be merged with term rescue or pivot over-fetch limits. Preserve `MILLER_SEMANTIC=off`, missing sidecar, degraded semantic, and rerank admission behavior.
5. Move reference-aware usage, graph expansion, body reads, and sufficiency/disposition facts. Preserve `reference_mode=usage`, `reference_depth`, `exclude_tests`, `max_hops`, and `ContextReferenceReadCounts` behavior.
6. Keep cancellation checkpoints in the same logical order and report only completed phases. Compare the cancellation and read-count cases before deleting old bodies.
7. Run focused tests and compare all Task 1 goldens while the old renderer remains the oracle.

**Meaningful red/green example:**

```csharp
[Fact]
public void Source_rescue_body_does_not_authorize_sufficient()
{
    var (index, resolver) = BuildFixture();

    string output = ContextTool.Run(index, resolver,
        query: "OrderService", tokenBudget: 4000, maxHops: 1,
        entrySymbols: null, failingTest: null, stackTrace: null,
        json: false, out _, out _);

    Assert.Contains("evidence=", output, StringComparison.Ordinal);
    Assert.Contains("reason=", output, StringComparison.Ordinal);
}
```

This preserves the existing behavior already covered by `ContextToolTests.RunActionable_SourceRescueBodyDoesNotAuthorizeSufficient`; it is not a new product rule.

**Expected outputs/assertions:** candidate ordering, graph neighbors, source-rescue mapping, semantic admission, body freshness, usage evidence, sufficiency, read counts, and cancellation remain identical.

**Acceptance:**

- [x] Builder owns selection and evidence construction without workspace I/O orchestration or rendering.
- [x] Existing retrieval caches and all three load-bearing limits retain their values and behavior.
- [x] Ordinary, actionable, source-rescue, semantic, reference-aware, and cancellation cases match Task 1.
- [x] No I/O-dependent type moved into `Miller.Core`.

## Task 4: Extract compact/JSON rendering and budgeting

**Files:**

- Create: `src/Miller.Server/Tools/Context/ContextBundleRenderer.cs`.
- Modify: `src/Miller.Server/Tools/ContextTool.cs` to delegate compact/JSON output and disposition/next-action formatting while retaining MCP adapter metadata.
- Modify: `tests/Miller.Tests/Server/ContextToolTests.cs`, `McpToolSchemaTests.cs`, `AgentInstructionsTests.cs`, and any budget tests selected by impact analysis.

**Interfaces:** The renderer consumes builder result facts and the requested format/budget. It uses existing `ToolOutputBudget`, `ContextEvidenceDisposition` (`status`/`reason`), next-step hint formatting, and JSON contract writers. Context has no inspect/trace continuation parameter; do not introduce continuation tokens here. The renderer does not retrieve symbols, read source, resolve workspaces, consult the semantic sidecar, or emit telemetry. It must preserve the existing bounded body facts and must not call an unbounded read-all operation merely to render.

**Approach:**

1. Inventory `RenderJson` at lines 3227–3239 and all compact renderers, bounded body-preview helpers, diagnostic renderers, and next-action formatters. Preserve field order, omission rules, truncation, and UTF-8 byte budgets.
2. Add a renderer characterization test that compares its output to the Task 1 public golden only after the builder facts are captured independently from the old renderer. This prevents two wrappers over the same new code from falsely proving parity.
3. Move compact rendering and compare ordinary/actionable/source-rescue/empty outputs. Then move JSON rendering and compare exact JSON strings and schema assertions.
4. Preserve `ToolOutputBudget.ContextMcpMaxTokens`, the existing budget renderer and refusal behavior, `disposition.status`/`disposition.reason`, `next_actions`, `coverage`, `usage_evidence`, and compact-only `next:` hints.
5. Run focused tool schema, guidance, budget, and ContextTool tests. Verify `ContextWithCancellation` still omits the framework cancellation token from MCP schema as the existing test requires.

**Meaningful red/green example:**

```csharp
[Fact]
public void Context_json_keeps_the_existing_field_contract()
{
    var (index, resolver) = BuildFixture();
    string json = ContextTool.Run(index, resolver,
        query: "OrderService", tokenBudget: 100, maxHops: 0,
        entrySymbols: null, failingTest: null, stackTrace: null,
        json: true, out _, out _);
    using JsonDocument document = JsonDocument.Parse(json);

    JsonElement disposition = document.RootElement.GetProperty("disposition");
    Assert.True(disposition.TryGetProperty("status", out _));
    Assert.True(disposition.TryGetProperty("reason", out _));
    Assert.True(document.RootElement.TryGetProperty("pivots", out _));
}
```

Use the existing expected disposition and fixture; the example proves field presence at the public boundary, while Task 1 proves exact bytes.

**Expected outputs/assertions:** compact and JSON outputs are byte-identical to the pre-move oracle; output budgets, omission rules, hints, and diagnostics are unchanged.

**Acceptance:**

- [ ] Renderer has no index, graph, source, semantic, workspace, or telemetry dependency.
- [ ] Compact and JSON outputs match Task 1 exact output hashes for all stable cases; compact uses `evidence=<status>  reason=<reason>` while JSON uses the disposition object.
- [ ] Existing schema, budget, disposition, and next-action tests pass.
- [ ] No duplicate renderer remains in `ContextTool` except a deleted/temporary forwarder awaiting Task 5 cleanup.

## Task 5: Remove forwarders and complete integration verification

**Files:**

- Modify: `src/Miller.Server/Tools/ContextTool.cs` to remove temporary internal forwarders and leave only the adapter surface and dependency composition.
- Modify: `src/Miller.Server/Cli/CliDispatch.cs` and `eval/semantic-model-eval/Program.cs` to remove direct old static calls and use the shared service/builder path.
- Modify: project files only if new files require explicit inclusion; SDK defaults should make this unnecessary.
- Modify: tests only for final caller-boundary assertions or stale symbol references found by impact analysis.

**Approach:**

1. Search and inspect every `ContextTool.ContextSourceSeed`, `ContextTool.RunActionable`, `ContextTool.RenderJson`, and other moved-symbol reference. Confirm CLI, evaluator, MCP, and tests point to the intended internal components.
2. Delete temporary forwarders and old nested implementations. A remaining forwarder is a failure unless it is the public adapter required by the contract.
3. Run Miller impact on the changed paths and trace the service/builder/renderer callers. Check that no production caller accidentally bypasses workspace routing or uses a different renderer.
4. Run all focused tests listed below, then the Release build and one fast suite at the task boundary:

```bash
dotnet test --filter "FullyQualifiedName~ContextToolTests"
dotnet test --filter "FullyQualifiedName~ContextQueryRetrievalTests"
dotnet test --filter "FullyQualifiedName~ContextPivotRankerTests"
dotnet test --filter "FullyQualifiedName~McpToolSchemaTests"
dotnet test --filter "FullyQualifiedName~AgentInstructionsTests"
dotnet test --filter "FullyQualifiedName~CliDispatchTests"
dotnet build Miller.slnx -c Release
dotnet test
```

5. Run Scale only if impact analysis shows a real index/read path changed; if run, use the repository's required Scale command and record why it was selected. Otherwise cite the existing `LiveContextImpactTests` Scale coverage as unchanged.
6. Re-run the public golden suite against MCP and CLI caller boundaries and record pre/post hashes, read counts, phase order, and final commit state.

**Expected outputs/assertions:** no stale forwarders or duplicate implementations; all callers share the intended path; focused tests, Release build, and fast suite pass; output and behavioral goldens remain unchanged.

**Acceptance:**

- [ ] Only the adapter, query service, builder, renderer, and any necessary shared internal model file remain.
- [ ] CLI and semantic-model evaluator use the shared internal path.
- [ ] Public MCP metadata and all output/behavior contracts are unchanged.
- [ ] Impact analysis finds no bypass, dead forwarder, or unexpected caller.
- [ ] Verification ledger records focused tests, build, fast suite, and parity evidence.

## Completion Boundary

This plan is complete when the extraction is behavior-preserving at MCP, CLI, and evaluator caller boundaries, all pre-move goldens match, and the focused/build/fast verification gates pass. A redesign of scoring, retrieval, semantic policy, telemetry, freshness, protocol, or public tool shape is outside scope and requires a separate approved plan.
