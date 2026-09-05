# Fact-Cache Resource Accounting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, no-delegation runs.

**Goal:** Make fact-cache lifetime and soft-budget behavior measurable while preserving query-time resolution answers and safe concurrent readers.

**Architecture:** Shared cache acquisition returns an owned lease handle; a lease is never disposed before its cache is consumed. The map's retained reference and active lease references are tracked as a union of unique cache objects. Stale loader completion may serve its original waiter but can never replace a newer scope entry.

**Tech Stack:** .NET 10, C#, Microsoft.Data.Sqlite, xUnit v3, existing cache/session fixtures, explicit benchmark script.

**Architecture Quality:** Miller.Indexing owns resource lifetime; Miller.Core remains pure. The risk is accounting leaks and loader races, not resolution policy correctness.

## Global Constraints

- The 256 MiB value is a soft retained-cache budget, never a hard process RSS cap.
- Acquire must never return a bare cache after disposing or dropping its lease.
- Delete `GetOrAdvance` only after every real production holder migrates and Miller trace proves no holder remains.
- Active bytes count unique cache objects, not one copy per lease.
- Retained and active sets overlap; total live bytes is their union, not their sum.
- A stale loader completion must never overwrite or resurrect a newer scope identity.
- Never kill, cancel, invalidate, or force-GC an active read to enforce budget.
- Bounded one-shot caches remain private and outside shared retained accounting.
- Existing resolution, graph, MCP, and serialized outputs remain unchanged.
- Existing cache fixtures use real SQLite I/O; tests must say so and use the actual helpers.
- No new environment variable or MCP field is added.

---

## Verified interfaces and contract inputs

- `RevisionFactCacheStore` is `src/Miller.Indexing/Resolution/RevisionFactCacheStore.cs:6-223`; default budget is 256 MiB and `GetOrAdvance` owns lazy scope replacement/eviction.
- `WarmInBackground` is `RevisionFactCacheStore.cs:74-114` and is already single-flight per scope.
- Full `RevisionFactCache` is immutable after load; bounded cache fills under `_boundedGate` (`src/Miller.Indexing/Resolution/RevisionFactCache.cs:491-527`).
- `FamilyStoreReadSession.CreateResolutionReader` is `src/Miller.Indexing/Reads/FamilyStoreReadSession.cs:132-197`; disposal is `:579-593`.
- `WorkspaceReadHandle` wraps `IWorkspaceReadSession`; handle disposal must not become a second cache owner.
- Existing SQLite fixtures are `tests/Miller.Tests/Indexing/Resolution/ResolutionStoreFixture.cs`, `RevisionFactCacheStoreTests.cs`, `BoundedRevisionFactCacheTests.cs`, and `FamilyStoreReadSessionTests.cs`.
- Historical evidence measured resident cache bytes above configured budget; this plan explicitly reports soft accounting and does not infer RSS.

## Verification Strategy

**Project source of truth:** `AGENTS.md`, `src/Miller.Indexing/Resolution/RevisionFactCacheStore.cs`, and named tests/fixtures.

**Worker red/green scope:** `dotnet test --filter "FullyQualifiedName~RevisionFactCacheStoreTests"`, `dotnet test --filter "FullyQualifiedName~BoundedRevisionFactCacheTests"`, and `dotnet test --filter "FullyQualifiedName~FamilyStoreReadSessionTests"`.

**Worker ceiling:** Focused classes only; no bare or Scale suite per task.

**Worker gate invariant:** No lease leak, no stale resurrection, unique union accounting, and byte-identical resolution answers.

**Lead affected-change scope:** `dotnet test --filter "FullyQualifiedName~RevisionFactCacheStoreTests|FullyQualifiedName~BoundedRevisionFactCacheTests|FullyQualifiedName~FamilyStoreReadSessionTests|FullyQualifiedName~FactCacheResourceAccountingTests|FullyQualifiedName~QueryTimeResolutionReaderTests"`.

**Branch gate:** One bare `dotnet test` and `dotnet build Miller.slnx -c Release`; Scale only for large real extracts or producer/store artifacts.

**Security scope:** `none declared`.

**Replay/metric evidence:** Lease counts, unique active/retained union bytes, single-flight, race safety, and parity are hard gates. Wall time/RSS are report-only.

**Escalation triggers:** Public cache API changes, bounded-cache behavior changes, unresolved loader race, or RSS claims.

**Assigned verification failure:** Investigate focused failure; never weaken lifecycle or parity assertions.

**Verification ledger:** Record invariant, command, scope, SHA, result, timestamp, SQLite fixture, configured budget, counters, and separate RSS.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Ownership model | Batch A | `src/Miller.Indexing/Resolution/CacheResourceSnapshot.cs`; `RevisionFactCacheStore.cs`; `tests/Miller.Tests/Indexing/Resolution/RevisionFactCacheStoreTests.cs` | Yes | Lease implementation uses the fixed unique-object model. |
| Task 2: Lease/race-safe store | None - serial | `src/Miller.Indexing/Resolution/RevisionFactCacheLease.cs`; `RevisionFactCacheStore.cs`; new lease tests | Yes | Depends on Task 1. |
| Task 3: Reader migration | None - serial | `src/Miller.Indexing/Reads/FamilyStoreReadSession.cs`; `WorkspaceReadHandle.cs` only if required; session tests | Yes | Requires non-bare lease API. |
| Task 4: Bounded parity/benchmark | None - serial | `FactCacheResourceAccountingTests.cs`; `scripts/bench-fact-cache-resources.sh`; existing bounded tests | Yes | Requires shared/one-shot ownership separation. |
| Task 5: Documentation | None - serial | `docs/known-limits.md`; this plan; linked plans | Yes | Requires evidence. |

## Tasks

### Task 1 — Freeze ownership and accounting model

**Files:** create `src/Miller.Indexing/Resolution/CacheResourceSnapshot.cs`; modify `RevisionFactCacheStore.cs`; modify only `RevisionFactCacheStoreTests.cs`.

**Interfaces:** Internal snapshot only; no lease migration yet.

**Contract inputs:** `ScopeEntry`, `_scopes`, `_warms`, `ResidentBytes`, and real SQLite `ResolutionStoreFixture`.

**Ownership/serialization:** These files only; serialize before Task 2. Dependency: none.

1. Write red state-model tests for two retained scopes, two identities, union accounting, and an oversized entry. Add one retained SQLite fixture baseline that proves existing loaded-byte estimates remain unchanged. Do not test active lease lifetime in Task 1.
2. Run `dotnet test --filter "FullyQualifiedName~RevisionFactCacheStoreTests"`; record red result.
3. Implement:

```csharp
internal readonly record struct CacheResourceSnapshot(
    int RetainedEntryCount, long RetainedBytes,
    int ActiveLeaseCount, long ActiveBytes,
    int EvictedHeldEntryCount, long EvictedHeldBytes,
    int UniqueLiveEntryCount, long UniqueLiveBytes,
    int LoadCount, int CoalescedLoadCount,
    int OversizedEntryCount);

internal readonly record struct CacheResourceState(
    IReadOnlySet<object> RetainedObjects,
    IReadOnlySet<object> ActiveObjects,
    IReadOnlyDictionary<object, long> ObjectBytes);
```

4. Define active/retained sets by cache object identity. Compute `UniqueLiveBytes` from the union, avoiding retained+active double count. Task 1 tests this explicit state object without acquiring a lease.
5. Read snapshots without forcing a lazy load under `_gate`; loading belongs outside store bookkeeping locks.
6. Run focused tests green and Miller impact.

- [ ] Unique-object union semantics are explicit.
- [ ] The retained SQLite baseline is named as an I/O test.
- [ ] Pure accounting-state tests do not pretend to exercise cache lifetime.
- [ ] Snapshot reads do not trigger hidden loads.
- [ ] Focused tests pass.

### Task 2 — Implement owned lease and stale-loader fencing

**Files:** create `src/Miller.Indexing/Resolution/RevisionFactCacheLease.cs`; modify `RevisionFactCacheStore.cs`; create `tests/Miller.Tests/Indexing/Resolution/RevisionFactCacheLeaseTests.cs`.

**Interfaces:** Internal `Acquire` returns `RevisionFactCacheLease`, never `RevisionFactCache` alone.

**Contract inputs:** Existing lazy/scope behavior at `RevisionFactCacheStore.cs:117-206`.

**Ownership/serialization:** Lease/store files and new tests; serialize after Task 1. Dependency: snapshot model.

1. Write red tests holding a lease through LRU eviction, switching revision, double-disposing, and throwing in lazy construction.
2. Run `dotnet test --filter "FullyQualifiedName~RevisionFactCacheLeaseTests"`; record red result.
3. Implement:

```csharp
internal sealed class RevisionFactCacheLease : IDisposable
{
    internal RevisionFactCache Cache { get; }
    internal string Scope { get; }
    internal string Identity { get; }
    public void Dispose();
}

internal RevisionFactCacheLease Acquire(
    string workspaceScope, string revisionIdentity,
    Func<SqliteConnection> openRead,
    StoreVisibility visibility);
```

4. Keep one retained map reference and one active reference per lease. Eviction drops map ownership only; final active release removes evicted-held accounting.
5. Assign each installed scope entry a unique entry token. A stale lazy completion can return to its waiter but must compare token before touching `_scopes`; it must never overwrite a newer identity.
6. Coalesce identical scope+identity loads. Install a newer identity while an old load is in flight without allowing old completion to resurrect it.
7. Permit one oversized current/active object and report it; no cancellation or forced GC.
8. Run focused tests green.

- [ ] No API returns an unleased cache.
- [ ] Unique active bytes and union bytes are correct.
- [ ] Evicted live cache remains usable/countable.
- [ ] Stale loader cannot overwrite newer scope.
- [ ] Double dispose and exceptions leak no lease.

### Task 3 — Migrate all real resolution holders

**Files:** modify `src/Miller.Indexing/Reads/FamilyStoreReadSession.cs`; modify `WorkspaceReadHandle.cs` only if necessary; modify `FamilyStoreReadSessionTests.cs`; modify resolution reader tests.

**Interfaces:** Session stores one lease beside `_resolution`; handle delegates disposal only.

**Contract inputs:** `CreateResolutionReader` and `Dispose` locations above; Miller trace of `GetOrAdvance`, `WarmInBackground`, `Resolution`, and `ResolutionReader` callers.

**Ownership/serialization:** Session/handle/test files; serialize after Task 2. Dependency: lease API.

1. Use Miller trace to enumerate every production holder. Do not delete `GetOrAdvance` while any holder remains.
2. Write red tests for lazy reader success, constructor exception, session disposal before/after access, duplicate handle disposal, and concurrent reads.
3. Run `dotnet test --filter "FullyQualifiedName~FamilyStoreReadSessionTests"`; record red result.
4. Acquire a lease in `CreateResolutionReader`; pass `lease.Cache` only to the reader while the session retains the lease.
5. On reader construction failure, dispose the new lease before propagating. In session `Dispose`, release bounded resources, shared lease, and connections exactly once under `_gate`.
6. Keep legacy artifact sessions unchanged. Keep bounded `LoadBounded` connection/gate private and outside shared accounting.
7. After trace proves no production holder uses `GetOrAdvance`, remove the old bare-cache API and migrate its tests to `Acquire`.
8. Run focused session and resolution tests green.

- [ ] Every real holder is enumerated and migrated.
- [ ] No bare cache escapes.
- [ ] Exceptional construction and disposal release exactly once.
- [ ] Legacy/bounded behavior remains unchanged.
- [ ] `GetOrAdvance` is removed only after trace proves zero production callers.

### Task 4 — Validate parity and resource benchmark

**Files:** create `tests/Miller.Tests/Indexing/Resolution/FactCacheResourceAccountingTests.cs`; create `scripts/bench-fact-cache-resources.sh`; modify existing bounded tests only for needed assertions.

**Interfaces:** Counters/benchmark output internal; no MCP/configuration.

**Contract inputs:** Real SQLite fixture helpers and existing bounded/full parity tests.

**Ownership/serialization:** Listed test/script files; serialize after Task 3. Dependency: migrated leases.

1. Write red tests for full/bounded parity after shared eviction, old revision lease during switch, duplicate loads, and oversized entry.
2. Run `dotnet test --filter "FullyQualifiedName~FactCacheResourceAccountingTests|FullyQualifiedName~BoundedRevisionFactCacheTests"`; record red result.
3. Add exact command `scripts/bench-fact-cache-resources.sh --fixture sqlite-synthetic --workspaces 2 --revisions 2 --budget-mb 256 --runs 5 --output <path>`.
4. Record retained bytes, active unique bytes, evicted-held bytes, union bytes, loads, coalesced loads, oversized count, wall time, and process RSS separately.
5. Repeat benchmark runs through `--runs 5`; assert deterministic counters, and keep time/RSS report-only.
6. Run focused tests green; preserve evidence in the ledger.

- [ ] Tests explicitly use real SQLite I/O.
- [ ] Full/bounded answers remain byte-identical.
- [ ] Benchmark options and output fields are explicit.
- [ ] RSS is not inferred from cache estimates.

### Task 5 — Update limits honestly

**Files:** `docs/known-limits.md`, this plan, `2026-09-04-reader-retention-integration.md`, `2026-09-04-architecture-review-program.md`.

**Interfaces:** Documentation only.

**Ownership/serialization:** Documentation owner; serialize after Tasks 1-4. Dependency: ledger evidence.

1. Compare each memory claim against `CacheResourceSnapshot` and benchmark reports.
2. Run repository docs link/path verification.
3. State that 256 MiB is soft, active readers may retain evicted objects, and RSS is separately measured.
4. Do not claim a hard cap or close the gap solely because accounting exists.

- [ ] Known limits match evidence.
- [ ] No hard RSS promise appears.
- [ ] Links resolve.
- [ ] No public/MCP contract changed.

## Safety matrix

| Scenario | Required behavior | Hard invariant |
|---|---|---|
| Same key concurrent | One load, many leases | One object; active count equals live leases |
| Current over budget | Serve normally | Oversized object reported; no kill |
| Eviction with reader | Drop map ownership only | Held object remains counted |
| Revision switch | Install new entry | Old lease remains valid |
| Loader race | Old waiter may finish | Old entry cannot overwrite new scope |
| Construction throw | Release acquired lease | No active leak |
| Dispose twice | One release | Counts never negative |
| Bounded one-shot | Private connection/gate | Excluded from shared budget |
| Resolution query | Existing answer | No graph/output change |

## Completion evidence

Completion requires trace coverage of all holders, focused lease/race/disposal tests, full/bounded parity, deterministic resource counters, and the explicit benchmark report. The plan makes the budget observable and soft; it does not promise process RSS containment.

## Implementation review checklist

- The lease handle is the only path by which a shared cache reaches a real resolution reader.
- `RevisionFactCacheLease.Cache` is read while the owning session keeps the lease alive; no adapter disposes the lease before reader construction or returns the cache after disposal.
- The store map records entry tokens, so an old `Lazy` completion cannot install itself over a newer scope identity.
- The active set is keyed by cache object identity; two leases over one object contribute one `ActiveBytes` value.
- The retained and active sets are joined by object identity before `UniqueLiveBytes` is calculated.
- Evicted objects remain counted while a session, handle, or reader still owns a lease.
- The bounded one-shot cache is excluded because it owns a private connection and is never inserted into `_scopes`.
- All real SQLite fixture tests use `ResolutionStoreFixture`, not a fake in-memory cache that would miss connection and lazy-load behavior.
- `QueryTimeResolutionReaderTests` is the actual reader test class for affected-change verification; `QueryTimeResolutionParity` is a helper and is not used as a test filter.
- `scripts/bench-fact-cache-resources.sh` is created by Task 4 and its command line is the only benchmark entry point named by this plan.
- The benchmark reports RSS from the process sampler and cache estimates from `CacheResourceSnapshot` as separate fields.
- No branch gate is claimed green from a benchmark alone; focused tests, affected-change tests, fast suite, build, and required Scale gates remain separate.
- The cache lease does not pin a SQLite transaction beyond the existing session behavior.
- Session disposal remains the release boundary even when a cache object was evicted from the store map.
- A failed revision advance removes only the failed scope entry and leaves unrelated workspace scopes intact.
- An entry removed after a failed load cannot be observed as warm by a later caller.
- Warm background work is single-flight and its task completion cannot install stale identity state.
- Existing `ResidentBytes` consumers receive the retained estimate they already expect until the migration is complete.
- New accounting fields are internal diagnostic facts and are not a public API promise.
- Tests cover both normal and exception paths for every ownership transfer.
