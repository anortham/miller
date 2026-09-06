# Sidecar Convergence Costs and Producer Cursor Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, no-delegation runs.

**Goal:** Preserve sidecar correctness while making incremental versus full convergence costs and producer retention safety observable.

**Architecture:** Keep public sidecar booleans and serialized convergence output unchanged. Add Indexing-owned internal detail and a typed adapter for Julie's existing consumer-cursor watermark CLI. Operate the cursor inside the separately protected Miller read session; create the prior watermark before a delta read and advance only after the sidecar transaction commits.

**Tech Stack:** .NET 10, C#, Microsoft.Data.Sqlite, xUnit v3, existing phase/telemetry, Julie `store maintain cursor` CLI.

**Architecture Quality:** Consumer-cursor watermarking is isolated behind an Indexing adapter; reader retention remains the separate M1 session concern; Server orchestrates. The main risk is advancing a watermark before sidecar commit or accepting a producer report for a different generation.

## Global Constraints

- Preserve `SymbolSearchSidecar.EnsureStoreCurrent(string, IWorkspaceReadSession)` and `ContentCorpusSidecar.EnsureStoreCurrent(string, IWorkspaceReadSession)` behavior.
- Preserve `StoreSidecarConvergenceResult`, public per-sidecar output, phase names, and JSON compatibility.
- Internal path/reason types live in `Miller.Indexing`, never `Miller.Core` or Server.
- Use verified Julie commands and report schema; do not invent flags or write producer databases.
- Validate producer returned generation before relying on protection.
- Retry owed cursor advance/release work while a sidecar is already current; current status does not suppress cleanup.
- A missing baseline may be advanced first; only a failed/incomplete delta requires full fallback.
- Cursor advance follows committed sidecar data and stamp; failure retains the old watermark and owed work.
- No new MCP parameters/schemas, graph changes, hard latency promises, hard RSS promises, or forced GC.

---

## Verified interfaces and contract inputs

- Search path: `src/Miller.Indexing/SymbolSearchSidecar.cs:314-341`; content path: `src/Miller.Indexing/ContentCorpusSidecar.cs:49-69`.
- Convergence orchestration: `src/Miller.Server/Hosting/IndexerSidecarConverger.cs:215-334`; existing `StoreSidecarConvergenceOutcome` remains the serialized compatibility boundary.
- Stamps: `src/Miller.Indexing/StoreSidecarStamp.cs:42,277,351`; delta reader consumes prior sequence through `src/Miller.Indexing/RevisionDeltaReader.cs`.
- Existing process pattern: `src/Miller.Indexing/Store/StoreMaintenanceRunner.cs:68-126`.
- Julie cursor command contract: `../../../julie-extractors/docs/contracts/cli.md:60`; current `consumer_cursors` implementation is `../../../julie-extractors/crates/julie-extract-artifact/src/store/coordinator.rs:2089-2184`.
- The cursor report's `action`, `mode`, `source_generation`, `consumer_id`, and `consumer_sequence` must be validated against the requested operation, generation, consumer, and sequence before the watermark is trusted; process exit and report disposition must also indicate applied success.
- Existing tests: `tests/Miller.Tests/Server/StoreSidecarConvergerTests.cs:32-452`; `tests/Miller.Tests/Indexing/FamilyStoreReadSessionTests.cs:503-844`.

## Verification Strategy

**Project source of truth:** `AGENTS.md`, Miller contracts, the named source/tests, and Julie `docs/contracts/cli.md`.

**Worker red/green scope:** `dotnet test --filter "FullyQualifiedName~StoreSidecarConvergerTests"`, `dotnet test --filter "FullyQualifiedName~FamilyStoreReadSessionTests"`, and exact new test-class filters.

**Worker ceiling:** Focused classes only; no bare `dotnet test` or Scale suite per task.

**Worker gate invariant:** Public output is stable; no protected delta is read before valid admission; final protection follows commit.

**Lead affected-change scope:** `dotnet test --filter "FullyQualifiedName~StoreSidecarConvergerTests|FullyQualifiedName~FamilyStoreReadSessionTests|FullyQualifiedName~StoreConsumerCursorRunnerTests|FullyQualifiedName~SidecarConvergenceCostTests"`.

**Branch gate:** One bare `dotnet test` and `dotnet build Miller.slnx -c Release`; `scripts/test.sh scale` when real producer/store artifacts are touched.

**Security scope:** `none declared`.

**Replay/metric evidence:** Row/stamp equivalence, identity/order, generation validation, and deterministic counters are hard gates; wall/RSS are report-only.

**Escalation triggers:** Julie contract change, producer response mismatch, public JSON change, or real artifact integration.

**Assigned verification failure:** Investigate focused failures; do not weaken safety or parity assertions.

**Verification ledger:** Record invariant, command, scope, SHA, result, timestamp, fixture, pin, hard metrics, and report-only metrics; reuse passing evidence for unchanged HEAD.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Detail classification | Batch A | `src/Miller.Indexing/SidecarConvergenceOutcome.cs`; both sidecars; `src/Miller.Server/Hosting/IndexerSidecarConverger.cs`; `tests/Miller.Tests/Server/StoreSidecarConvergerTests.cs` | Yes | Task 3 consumes detail. |
| Task 2: Consumer cursor adapter | Batch A | `src/Miller.Indexing/Store/StoreConsumerCursorRunner.cs`; `tests/Miller.Tests/Indexing/StoreConsumerCursorRunnerTests.cs` | No | None - safe parallel batch. |
| Task 3: Ordering integration | None - serial | both sidecars; converger; exact cleanup integration; `tests/Miller.Tests/Server/StoreSidecarCursorIntegrationTests.cs` | Yes | Requires Tasks 1, 2, and producer contract. |
| Task 4: Cost evidence | None - serial | `src/Miller.Indexing/SidecarConvergenceCounters.cs`; `tests/Miller.Tests/Indexing/SidecarConvergenceCostTests.cs`; `scripts/bench-sidecar-convergence.sh` | Yes | Measures final ordering. |
| Task 5: Documentation | None - serial | `docs/known-limits.md`; linked plans | Yes | Requires evidence. |

## Tasks

### Task 1 — Add internal detail without changing output

**Files:** create `src/Miller.Indexing/SidecarConvergenceOutcome.cs`; modify both sidecars and converger; modify only `tests/Miller.Tests/Server/StoreSidecarConvergerTests.cs`.

**Interfaces:** Public booleans and serialized records remain unchanged. New detail is internal logging/telemetry input only.

**Contract inputs:** Existing branch order and `StoreSidecarConvergenceOutcome`.

**Ownership:** Task 1 owns listed files. **Serialization:** yes, before Task 3. **Dependency:** none.

1. Write red tests for current, empty delta, incremental, full, identity, incomplete, apply, and stamp cases.
2. Run `dotnet test --filter "FullyQualifiedName~StoreSidecarConvergerTests"` and capture red output.
3. Implement:

```csharp
internal enum SidecarConvergencePath { Current, EmptyDelta, Incremental, Full }
internal enum SidecarConvergenceReason
{
    None, DeltaMissing, DeltaIncomplete, IdentityChanged, ApplyFailed, StampMismatch
}
internal readonly record struct SidecarConvergenceDetail(
    SidecarConvergencePath Path,
    SidecarConvergenceReason Reason,
    bool DidWork);
```

4. Map current to false and completed writes to true exactly as today. Do not add fields to public JSON records.
5. Record detail through existing phase/telemetry facilities; recorder failures stay contained.
6. Run the focused class and Miller impact; confirm no Core dependency.

- [ ] Detail types are Indexing-owned.
- [ ] Existing bool/result/JSON tests pass.
- [ ] Every path/reason is asserted.

### Task 2 — Add typed Julie consumer-cursor runner

**Files:** create `src/Miller.Indexing/Store/StoreConsumerCursorRunner.cs`; create `tests/Miller.Tests/Indexing/StoreConsumerCursorRunnerTests.cs`.

**Interfaces:** Typed process runner for existing `store maintain cursor advance|release`; no direct coordinator DB access and no reader-lifecycle API.

**Contract inputs:** Julie CLI `store maintain cursor advance|release` at `../../../julie-extractors/docs/contracts/cli.md:60`; existing process handling in `StoreMaintenanceRunner.cs:68-126`.

**Ownership:** Adapter/test files only. **Serialization:** no. **Dependency:** none - safe parallel batch.

1. Write red fake-process tests for exact advance/release arguments, malformed JSON, timeout, nonzero exit, rejected monotonic advance, and source-generation mismatch.
2. Run `dotnet test --filter "FullyQualifiedName~StoreConsumerCursorRunnerTests"` and capture red output.
3. Implement the target shape:

```csharp
internal sealed record StoreConsumerCursorOutcome(
    bool Succeeded,
    bool Applied,
    string? SourceGeneration,
    string? ConsumerId,
    long? ConsumerSequence,
    string? Error);

internal static StoreConsumerCursorOutcome Advance(
    string binaryPath, string storeRoot, string? familyId,
    string consumerId, long sequence, TimeSpan? timeout = null);

internal static StoreConsumerCursorOutcome Release(
    string binaryPath, string storeRoot, string? familyId,
    string consumerId, TimeSpan? timeout = null);
```

4. Use only the documented flags. The current CLI derives generation from the store; do not send a generation flag. Reuse bounded capture/timeouts.
5. Parse `report_schema_version`, `action`, `mode`, `disposition`, `family_id`, `source_generation`, `consumer_id`, and `consumer_sequence`. Follow the existing `StoreMaintenanceReport.with_cursor` implementation in Julie `crates/julie-extract-cli/src/store/maintenance_report.rs:375`: an advance succeeds with `action=cursor_advance`, `mode=apply`, and `disposition=advanced` or `no_change`; a release succeeds with `action=cursor_release`, `mode=apply`, and `disposition=released` or `no_change`. The generic `applied` disposition is NOT the cursor success value. Require exit 0, schema v1, no failure, and matching family/consumer ID. Advance additionally requires `source_generation` equal to the session generation and `consumer_sequence` equal to the requested sequence. Release has no requested sequence and may report a different current `source_generation` when removing an old-generation cursor; its exact consumer ID is the delete target. Test idempotent no-change success and this old-generation release explicitly.
6. Run focused tests green.

- [x] Exact existing cursor command/report behavior is tested.
- [x] Producer errors are typed and nonthrowing.
- [x] Source generation and cursor sequence cannot be silently trusted.

### Task 3 — Integrate safe ordering and recovery

**Files:** modify both sidecars and converger; modify exact view cleanup path; create `tests/Miller.Tests/Server/StoreSidecarCursorIntegrationTests.cs`.

**Interfaces:** Inject Task 2 adapter through existing internal converger constructor; no public/MCP additions.

**Contract inputs:** `FamilyStoreSidecarWriteLease`, stamps, delta reader, existing `consumer_cursors` watermark, and producer report fields.

**Ownership:** Integration/test files. **Serialization:** yes. **Dependency:** Tasks 1 and 2 plus the existing published consumer-cursor contract; M1's protected read session is a prerequisite, not a new cursor API dependency.

1. Write red tests for no-baseline cursor creation, complete delta, GC-trimmed/incomplete delta, source-generation mismatch, sidecar commit before final cursor advance, current-sidecar owed retry, generation switch, and view removal.
2. Run `dotnet test --filter "FullyQualifiedName~StoreSidecarCursorIntegrationTests"`; capture red output.
3. If a stale sidecar has no cursor, advance a new cursor to its prior sequence before reading the delta. A successful baseline does not force a full rebuild. If the protected read is incomplete because GC already trimmed it, use full fallback.
4. Validate producer `source_generation` and `consumer_sequence` against the requested generation and sequence, and require returned `consumer_id` to equal the requested ID. Mismatch means no incremental apply; choose full and retain owed cursor cleanup.
5. Commit sidecar rows and stamp. Only then advance the cursor to target. Failure preserves the old watermark and committed sidecar.
6. Current sidecars retry owed cursor advance/release without rebuilding. Generation changes use distinct hashed identities. Removal releases exact captured cursor identities only.
7. Run focused tests and existing sidecar fixtures green.

M1 reader retention protects the open read session independently. M2 persists only a consumer-cursor watermark for sidecar delta history; it never retargets or replaces immutable reader protection.

The canonical cursor ID is bounded and deterministic:

```csharp
internal static string CursorId(
    string familyId, string storeInstanceId, string viewId,
    StoreSidecarKind kind, string generationName)
{
    byte[] bytes = LengthPrefixedUtf8(
        familyId, storeInstanceId, viewId, kind.ToString(), generationName);
    return "miller-sc-v1:" + Convert.ToHexString(SHA256.HashData(bytes));
}
```

`LengthPrefixedUtf8` writes a four-byte little-endian byte length followed by UTF-8 bytes for each field, in the listed order. Empty fields are rejected before encoding. The digest prevents delimiter ambiguity and stays below Julie's 128-character identity limit. Store instance is mandatory so a removed-and-recreated family cannot inherit an old cursor identity.

Cursor operations are monotonic watermark operations. A lost advance reply is retried with the same consumer ID and sequence; the producer result is accepted only after source generation and cursor sequence validation.

Sidecar and cursor writes are separate database/process operations and cannot be one transaction. The safety invariant is ordered durability: baseline cursor advance, delta read, sidecar commit, final cursor advance. Each boundary receives failure injection; tests must not assume cross-database atomicity.

- [ ] The cursor stores only a watermark; M1 reader protection remains separate.
- [ ] Cursor ID uses length-prefixed SHA-256 over family/store instance/view/kind/generation.
- [ ] Lost cursor replies retry idempotently and validate source generation and sequence.
- [ ] Cross-database boundaries have explicit failure tests.

- [ ] Baseline precedes protected read.
- [ ] Producer response generation is validated.
- [ ] Commit precedes final cursor advance.
- [ ] Current-sidecar owed work retries without rescan.
- [ ] Cleanup cannot affect survivors.

### Task 4 — Add deterministic cost evidence

**Files:** create `src/Miller.Indexing/SidecarConvergenceCounters.cs`; create `tests/Miller.Tests/Indexing/SidecarConvergenceCostTests.cs`; create `scripts/bench-sidecar-convergence.sh`.

**Interfaces:** Internal counters through existing telemetry; no MCP fields.

**Contract inputs:** Existing SQLite fixture helpers, sidecar equivalence tests, phase names.

**Ownership:** Listed files. **Serialization:** yes. **Dependency:** Task 3 final path.

1. Write red tests using a real SQLite synthetic store with fixed files, aliases, changed/deleted paths, and incomplete delta.
2. Run `dotnet test --filter "FullyQualifiedName~SidecarConvergenceCostTests"`; record red output.
3. Implement:

```csharp
internal readonly record struct SidecarConvergenceCounters(
    int DeltaRowsRead, int ChangedPaths, int DeletedPaths,
    int RowsInserted, int RowsUpdated, int RowsDeleted,
    int FullFiles, int FullDocuments, TimeSpan Elapsed);
```

4. Add exact command `scripts/bench-sidecar-convergence.sh --fixture sqlite-synthetic --mode both --runs 5 --output <path>` with commit, pin, fixture, cold/warm state, counters, wall time, and RSS.
5. Hard-gate full/incremental row and stamp equivalence. Treat timing/RSS as report-only baseline metrics.
6. Run focused tests and the benchmark command; record the report.

- [ ] Tests explicitly use SQLite I/O.
- [ ] Counters are deterministic.
- [ ] Equivalence is a hard gate.
- [ ] Benchmark options are explicit and RSS is separate.

### Task 5 — Close documentation honestly

**Files:** `docs/known-limits.md`, this plan, `2026-09-04-reader-retention-integration.md`, `2026-09-04-architecture-review-program.md`.

**Interfaces:** Documentation only. **Serialization:** yes. **Dependency:** all prior evidence.

1. Compare A8 wording against the ledger.
2. Run the repository docs link/path check.
3. Keep A8 open unless producer ordering, response validation, and local cost evidence all pass.
4. Correctly link Julie with `../../../julie-extractors/...` from Miller `docs/plans`.

- [ ] A8 wording matches evidence.
- [ ] Links resolve.
- [ ] No MCP/schema contract changes.

## Safety matrix

| Scenario | Path | Required invariant |
|---|---|---|
| Current, no owed work | Current | No sidecar rebuild or cursor mutation |
| Current, owed work | Current + cleanup | Retry without rescan |
| New baseline | Baseline advance then delta | Missing baseline is not automatic full fallback |
| Delta trimmed/incomplete | Full | No fake incremental completion |
| Generation mismatch | Full | Returned identity is validated first |
| Sidecar commit then cursor advance failure | Committed + owed | Old watermark retained |
| Generation switch | New identity then old release | New artifact established first |
| View removal | Exact cleanup | Survivor identities untouched |

## Completion evidence

Completion requires focused tests for the matrix, SQLite equivalence, deterministic counters, producer response validation, exact command evidence, and Scale validation against the pinned Julie release when real artifacts are used. No hard latency or RSS bound is claimed.
