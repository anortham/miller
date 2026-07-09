# Impact Traversal Evidence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Make `miller impact --json --from-index-revision` report whether its bounded reverse-graph traversal exhausted the current indexed frontier, which paths seeded that traversal, and which paths could not seed it.

**Architecture:** Preserve `delta_status` as the changed-path journal completeness signal and add an independent `traversal` object for graph-execution evidence. `GraphTraversal` returns nodes plus depth/limit truncation metadata; `ImpactTool` adds seeded/unseeded path accounting and renders an additive JSON object. Capability negotiation advertises `impact_traversal_evidence` so older Eros versions ignore it and newer Eros versions degrade safely when absent.

**Tech Stack:** .NET 10, xUnit, Miller.Core graph traversal, SQLite symbol graph, Miller CLI JSON contracts.

**Architecture Quality:** Affected modules are the pure graph traversal core, the graph reachability seam, `ImpactTool`, delta CLI rendering, and capabilities/docs. The caller-facing interface is `ISymbolGraphReachability.ReachWithEvidence`; existing `Reach` remains a compatibility convenience that returns `.Nodes`. Tests cover the pure graph interface and the public CLI JSON, not private renderer details alone. Rejected shortcuts: infer completeness from `tests[]`, call the graph twice with larger arbitrary caps, rename `delta_status`, claim semantic dependency completeness, or add a new MCP tool. Architecture risk: medium for the graph interface and high if consumers misread traversal exhaustion as semantic impact completeness.

## Global Constraints

- `delta_status=complete` continues to mean only that the watched changed-path span is reconstructable for the same artifact generation.
- The new contract is named traversal evidence, never impact completeness.
- `traversal.status=exhausted` means only that Miller found no additional nodes beyond the effective depth/limit in the current indexed graph, starting from the reported seeded paths.
- Dynamic dispatch, reflection, configuration, generated code, unresolved references, and missing extractor edges remain outside that claim.
- `tests[]` remains a likely-test set; an empty array is never proof that no test is affected.
- Any changed path with no current indexed symbols appears in `unseeded_paths`; deleted/config/data paths must not disappear silently.
- Fields are additive to the frozen v1 delta envelope. Existing `delta_status`, revision, artifact, changed-path, impacted, and tests fields do not change shape or meaning.
- Advertise feature string `impact_traversal_evidence` only when every documented field and status is emitted.
- Do not add an MCP tool or read/modify Eros private state.
- Keep `Miller.Core` pure with zero I/O dependencies.
- Execution uses @razorback:test-driven-development for behavior changes and @razorback:verification-before-completion before each commit/handoff.
- No release, tag, push, extractor pin, or Eros version bump is part of this plan.

---

## Public JSON Contract

Every index-revision delta JSON response adds:

```json
"traversal": {
  "status": "exhausted",
  "reason": "complete",
  "max_depth": 2,
  "limit": 100,
  "reached_count": 17,
  "returned_count": 17,
  "truncated_by_depth": false,
  "truncated_by_limit": false,
  "seeded_paths": ["src/Service.cs"],
  "unseeded_paths": ["config/settings.json"]
}
```

Allowed status/reason pairs:

- `exhausted` / `complete`: traversal ran and neither bound hid a reachable current-graph node.
- `truncated` / `depth`, `limit`, or `depth_and_limit`: at least one bound hid a reachable node.
- `not_run` / `delta_unavailable`: the changed-path span was unavailable.
- `not_run` / `no_changes`: the complete delta contained no watched changed paths.
- `not_run` / `index_unavailable`: changed paths existed but the current symbol/graph index could not be loaded.
- `not_run` / `no_seeds`: changed paths existed but none contained current indexed symbols.

`reached_count` is the number of non-seed nodes discovered within `max_depth` before applying `limit`; `returned_count` is the number actually rendered across `impacted[]` plus `tests[]`. A mixed set of seeded and unseeded paths may still have `status=exhausted`; the `unseeded_paths` array remains a separate warning that the graph made no claim for those paths.

## Verification Strategy

**Project source of truth:** `AGENTS.md`, `docs/contracts/impact-index-revision-delta-v1.md`, `docs/contracts/cli-eros-v1.md`, and `tests/Miller.Tests`.

**Worker red/green scope:** Run the focused graph, impact-tool, or CLI test filter named by each task. Follow TDD for each contract field and truncation case.

**Worker ceiling:** Focused `Miller.Tests` filters and the fast `scripts/test.sh` suite. Workers do not run scale tests or releases unless the lead explicitly expands scope.

**Worker gate invariant:** Graph tests prove exact exhaustion flags; impact tests prove test partition and seed accounting; CLI tests prove the additive public JSON and capability negotiation.

**Lead affected-change scope:** `scripts/test.sh` plus `dotnet build Miller.slnx -c Release` after the coherent batch.

**Branch gate:** `scripts/test.sh` and `dotnet build Miller.slnx -c Release` with zero warnings/errors. Run `scripts/test.sh scale` only if implementation crosses into extractor launch/index rebuild behavior rather than the read-only graph path planned here.

**Replay/metric evidence:** Hard gates are deterministic depth/limit flags, no silent unseeded path, unchanged v1 fields, and absent-feature compatibility. Dogfood traversal counts and duration are report-only.

**Escalation triggers:** Any schema migration, extractor invocation, MCP surface change, or material traversal performance regression requires a separate plan/review and broader gate.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp. For live replay also record bounds, counts, status/reason, and seeded/unseeded paths.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Add graph reach evidence | Batch A | `GraphTraversal.cs`, `SymbolGraph.cs`, `SqliteSymbolGraphIndex.cs`, `GraphTraversalTests.cs`, `SymbolGraphTests.cs`, `SqliteSymbolGraphIndexTests.cs` | No | None - safe parallel batch. |
| Task 2: Add impact seed and traversal rendering | None - serial | `ImpactTool.cs`, `CliDispatch.cs`, `ImpactToolTests.cs`, `ImpactRevisionDeltaCliTests.cs` | Yes | Requires Task 1's `ReachWithEvidence` contract. |
| Task 3: Publish capability and contract docs | Batch B | `CliCapabilities.cs`, `impact-index-revision-delta-v1.md`, `impact-traversal-evidence-v1.md`, `cli-eros-v1.md`, `docs/README.md`, `ImpactRevisionDeltaCliTests.cs` | No | Runs after Task 2 fixes the final JSON shape; safe in parallel with Task 4. |
| Task 4: Run local Eros-facing dogfood | Batch B | `docs/findings/2026-07-09-impact-traversal-evidence-dogfood.md` (create only) | No | None - safe parallel batch after Task 2; does not edit production files. |

Tasks 1-2 use `serial-worker-commit`. Batch B uses `parallel-lead-commit`; workers hand the docs/capability and findings diffs to the lead for one reviewed closeout commit.

### Task 1: Add graph reach evidence

**Files:**
- Modify: `src/Miller.Core/Graph/GraphTraversal.cs:5`
- Modify: `src/Miller.Core/Graph/SymbolGraph.cs:36`
- Modify: `src/Miller.Indexing/SqliteSymbolGraphIndex.cs:39`
- Test: `tests/Miller.Tests/Graph/GraphTraversalTests.cs`
- Test: `tests/Miller.Tests/Graph/SymbolGraphTests.cs`
- Test: `tests/Miller.Tests/Indexing/SqliteSymbolGraphIndexTests.cs`

**Interfaces:**
- Consumes: existing BFS inputs `(starts, maxDepth, limit, direction, contains, neighbours)`.
- Produces: `GraphReachResult ReachWithEvidence(...)`; existing `Reach(...)` returns `ReachWithEvidence(...).Nodes`.

**Contract inputs:** Deterministic ordering remains hop ascending then ID; starts remain excluded; unknown starts remain skipped.

**File ownership:** `GraphTraversal.cs`, `SymbolGraph.cs`, `SqliteSymbolGraphIndex.cs`, `GraphTraversalTests.cs`, `SymbolGraphTests.cs`, `SqliteSymbolGraphIndexTests.cs`

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**Step 1: Write failing pure-graph tests**

Pin empty, exhausted, depth-truncated, limit-truncated, both-truncated, cycle, diamond, and unknown-start behavior:

```csharp
GraphReachResult result = graph.ReachWithEvidence(["a"], maxDepth: 2, limit: 2, Direction.Reverse);

Assert.Equal(2, result.Nodes.Count);
Assert.True(result.TruncatedByLimit);
Assert.True(result.TruncatedByDepth);
Assert.Equal(3, result.ReachedCount);
```

Use graphs where the expected frontier is unambiguous; do not infer depth truncation merely because a node sits at `maxDepth`—it must have at least one unvisited neighbour beyond the boundary.

**Step 2: Run tests to verify failure**

Run: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~GraphTraversalTests|FullyQualifiedName~SymbolGraphTests|FullyQualifiedName~SqliteSymbolGraphIndexTests"`

Expected: FAIL because no evidence-returning interface exists.

**Step 3: Implement one-pass evidence**

Add the result record in the pure graph namespace:

```csharp
public sealed record GraphReachResult(
    IReadOnlyList<ReachedNode> Nodes,
    int ReachedCount,
    bool TruncatedByDepth,
    bool TruncatedByLimit)
{
    public bool Exhausted => !TruncatedByDepth && !TruncatedByLimit;
}
```

During BFS, when a node is at `maxDepth`, inspect its requested-direction neighbours and set `TruncatedByDepth` only if an unseen neighbour exists; do not enqueue it. After traversal, compute all reached non-seed nodes before `Take(limit)`, set `TruncatedByLimit` when `ReachedCount > limit`, and render the same deterministic node prefix as today.

Both `SymbolGraph` and `SqliteSymbolGraphIndex` implement the new interface by delegating to the shared pure algorithm. Preserve existing `Reach` behavior exactly.

**Step 4: Run tests to verify pass**

Run the Step 2 command.

Expected: PASS in both in-memory and SQLite implementations with identical evidence.

**Step 5: Apply commit mode**

Use `serial-worker-commit`: commit the pure graph slice after focused tests pass; record the SHA.

**Acceptance criteria:**
- [x] Depth and limit truncation are independently observable.
- [x] `ReachedCount` is pre-limit and nodes remain deterministic.
- [x] Existing `Reach` callers retain their behavior.
- [x] In-memory and SQLite graph implementations agree.
- [x] Worker-scope verification passes and the change is committed per commit mode.

### Task 2: Add impact seed and traversal rendering

**Files:**
- Modify: `src/Miller.Server/Tools/ImpactTool.cs:392`
- Modify: `src/Miller.Server/Cli/CliDispatch.cs:1322`
- Test: `tests/Miller.Tests/Server/ImpactToolTests.cs`
- Test: `tests/Miller.Tests/Server/Cli/ImpactRevisionDeltaCliTests.cs`

**Interfaces:**
- Consumes: `GraphReachResult`, changed paths, current symbol lookup, effective max depth/limit.
- Produces: `traversal` JSON object with exact status/reason/count/path semantics defined above.

**Contract inputs:** `delta_status` and existing top-level arrays are frozen. `symbols.is_test` remains extractor-owned and continues to partition reached nodes.

**File ownership:** `ImpactTool.cs`, `CliDispatch.cs`, `ImpactToolTests.cs`, `ImpactRevisionDeltaCliTests.cs`

**Serialization required:** Yes.

**Dependency reason:** Requires Task 1's `ReachWithEvidence` contract.

**Step 1: Write failing renderer and CLI tests**

Cover:

- complete empty delta → `not_run/no_changes`;
- unavailable delta → `not_run/delta_unavailable`;
- non-empty delta with index load failure → `not_run/index_unavailable`;
- all paths unseeded → `not_run/no_seeds` with every changed path reported;
- mixed seeded/unseeded path set;
- exhausted, depth, limit, and combined truncation;
- `returned_count == impacted.length + tests.length`;
- current Rust `is_test` partition remains unchanged.

**Step 2: Run tests to verify failure**

Run: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~ImpactToolTests|FullyQualifiedName~ImpactRevisionDeltaCliTests"`

Expected: FAIL because delta JSON lacks `traversal` and seed accounting.

**Step 3: Implement seed accounting and rendering**

Replace the tuple returned by `ReachFromChangedPaths` with an internal result carrying reached groups, graph evidence, and path partitions:

```csharp
private sealed record ImpactTraversal(
    IReadOnlyList<Reached> Impacted,
    IReadOnlyList<Reached> Tests,
    GraphReachResult? Graph,
    IReadOnlyList<string> SeededPaths,
    IReadOnlyList<string> UnseededPaths,
    string Status,
    string Reason);
```

`SeedFromFile` already returns a count; use it to classify each changed path. Pass explicit load state from `CliDispatch` so `index_unavailable` is distinct from `no_seeds`. Render bounds even when traversal did not run. Keep compact output useful but treat the JSON contract as authoritative.

**Step 4: Run tests to verify pass**

Run the Step 2 command.

Expected: PASS; every delta response has honest independent delta and traversal state.

**Step 5: Apply commit mode**

Use `serial-worker-commit`: commit after focused impact/CLI tests pass; record the SHA.

**Acceptance criteria:**
- [x] No changed path silently disappears from seed accounting.
- [x] Index unavailable and no seeds are distinguishable.
- [x] Traversal evidence exactly matches public status/reason definitions.
- [x] Existing top-level delta fields remain byte-shape compatible except for the additive object.
- [x] Worker-scope verification passes and the change is committed per commit mode.

### Task 3: Publish capability and contract docs

**Files:**
- Modify: `src/Miller.Server/Cli/CliCapabilities.cs:19`
- Modify: `docs/contracts/impact-index-revision-delta-v1.md:28`
- Create: `docs/contracts/impact-traversal-evidence-v1.md`
- Modify: `docs/contracts/cli-eros-v1.md`
- Modify: `docs/README.md`
- Test: `tests/Miller.Tests/Server/Cli/ImpactRevisionDeltaCliTests.cs`

**Interfaces:**
- Consumes: final Task 2 JSON shape.
- Produces: feature string `impact_traversal_evidence` and JSON contract entry `impact_traversal_evidence` schema version 1.

**Contract inputs:** Additive negotiation only; `impact_index_revision_delta` remains separately advertised.

**File ownership:** `CliCapabilities.cs`, `impact-index-revision-delta-v1.md`, `impact-traversal-evidence-v1.md`, `cli-eros-v1.md`, `docs/README.md`, `ImpactRevisionDeltaCliTests.cs`

**Serialization required:** No.

**Dependency reason:** Runs after Task 2 fixes the final JSON shape; safe in parallel with Task 4.

**Step 1: Write a failing capability test**

Assert the feature and JSON contract are present iff the implementation-active flag is true, while the prior delta feature remains unchanged.

**Step 2: Run test to verify failure**

Run: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~ImpactRevisionDeltaCliTests`

Expected: FAIL because the new feature/contract is absent.

**Step 3: Add capability and exact docs**

Add:

```csharp
public const string ImpactTraversalEvidenceFeature = "impact_traversal_evidence";
public const bool ImpactTraversalEvidenceActive = true;
```

Extend `NegotiatedFeatures` with a separately gated argument and add the v1 JSON contract row. The new contract must include the semantic limitation prominently: exhaustion is relative to seeded paths and current indexed edges, and cannot exonerate tests by itself.

**Step 4: Run tests to verify pass**

Run the Step 2 command, then `scripts/test.sh` and `dotnet build Miller.slnx -c Release`.

Expected: PASS with zero build warnings/errors.

**Step 5: Apply commit mode**

Use `parallel-lead-commit`: hand the verified diff and ledger to the lead without committing.

**Acceptance criteria:**
- [x] Feature negotiation is separate from revision-delta negotiation.
- [x] Contract docs enumerate every field/status/reason.
- [x] Docs state the semantic non-completeness limitation.
- [x] Fast suite and release build pass.
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode.

### Task 4: Run local Eros-facing dogfood

**Files:**
- Create: `docs/findings/2026-07-09-impact-traversal-evidence-dogfood.md`

**Interfaces:**
- Consumes: locally built Miller CLI and registered Eros, Miller, and julie-extractors workspaces.
- Produces: live payload evidence for exhausted, unseeded, and at least one deliberately truncated traversal.

**Contract inputs:** Use reversible comment-only edits and restore them; do not release or change pins.

**File ownership:** `docs/findings/2026-07-09-impact-traversal-evidence-dogfood.md` (create only)

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch after Task 2; does not edit production files.

**Step 1: Prepare three probes**

Use the local built binary against:

1. a source path known to seed impact;
2. a watched config/data path with no current symbol seed;
3. the source probe with intentionally small `--max-depth` and/or `--limit` to force truncation.

**Step 2: Capture and validate payloads**

For every probe record version, artifact ID, from/to revisions, bounds, changed paths, traversal object, and tests count. Assert the intentionally truncated case never says exhausted.

**Step 3: Restore probes and run final state check**

Restore only the reversible probe edits, refresh the workspaces, and verify all three repos retain their pre-probe git status.

**Step 4: Write the findings document**

Include exact commands, outputs, timings, and a statement that traversal evidence is not semantic completeness.

**Step 5: Apply commit mode**

Use `parallel-lead-commit`: hand the findings doc to the lead without committing.

**Acceptance criteria:**
- [x] Live exhausted, unseeded, and truncated payloads are recorded.
- [x] Truncation flags match deliberately chosen bounds.
- [x] Probe edits are fully restored.
- [x] No release/pin change occurred.
- [x] Worker-scope verification passes and the findings are handed to the lead per commit mode.

## Program Exit Criteria

- [x] Graph traversal reports depth and limit truncation without changing existing callers.
- [x] Delta JSON reports effective bounds, counts, and seeded/unseeded paths.
- [x] `delta_status` semantics remain unchanged.
- [x] Capabilities advertise the additive contract independently.
- [x] Fast suite, release build, and local dogfood pass.
- [x] Documentation explicitly forbids treating traversal exhaustion as semantic test-impact completeness.
