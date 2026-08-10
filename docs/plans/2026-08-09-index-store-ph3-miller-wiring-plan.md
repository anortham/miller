# Versioned Index Store Ph3 Miller Wiring Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Status:** A1-A7 cleanup complete 2026-08-09; A9.1-A9.4 review fixes complete 2026-08-09. This
wiring plan is retained as the implementation record; the durable lock/freshness and cursor-order
amendments remain open, while Ph4 dashboard work and Ph5 physical validation/default-on decisions
remain.

**Goal:** Pin Miller to the published `julie-extract 2.31.1` release and make Miller create, refresh, and read family stores through the amended v4.1 store contract while preserving every existing MCP/CLI surface and the legacy artifact off-switch.

**Architecture:** Introduce one deep `IWorkspaceReadSession` seam in `Miller.Indexing`. A legacy adapter preserves current standalone-artifact behavior and a family-store adapter owns pointer/registry validation, generation pins, the session visibility table, attached resolution/sidecar files, and the freshness token. A separate `JulieStoreClient` owns the public `julie-extract store ... --json` protocol; `StoreWorkspaceCoordinator` composes that client with Miller's registry, governor, bootstrap, and refresh paths without reimplementing Rust store semantics.

**Tech Stack:** .NET 10, C# 13, Microsoft.Data.Sqlite, SQLite WAL/FTS5, xUnit, the published `julie-extract 2.31.1` CLI and store schema v2.

**Architecture Quality:**

- **Affected modules:** `Miller.Indexing` read/query adapters and sidecars; `Miller.Server` workspace registry/provider/bootstrap/refresh/status orchestration; dashboard read-only provenance.
- **Caller-facing interface:** `IWorkspaceReadSession.Read(...)`, `WorkspaceReadSnapshot`, and `JulieStoreClient.Submit(...)`. Tool cores continue receiving `WorkspaceReadContext`; no MCP or public read-command shape changes.
- **Depth/locality check:** path, pointer, generation, pin, visibility, attachment, and store compatibility knowledge stay inside the session adapters. Queue JSON and retry semantics stay inside the store client/coordinator.
- **Test surface:** legacy/store adapter parity through `IWorkspaceReadSession`; store requests through the public `julie-extract` binary in Scale tests; existing tool contract tests remain unchanged.
- **Seams/adapters:** `LegacyArtifactReadSession` is the compatibility adapter; `FamilyStoreReadSession` is the second adapter proving the seam. `JulieStoreClient` is a process-contract adapter, not a second store implementation.
- **Rejected shortcuts:** ambient/current database paths; raw `IndexDbPath` leaking through the new read contract; post-filtering hidden versions after top-K; Miller writes to `store.db`/`coord.db`; copying/exporting an artifact on every store read; per-tool pointer resolution; silently serving a stale legacy artifact when store mode is disabled.
- **Architecture risk:** high. The read seam has broad caller impact, and ranking/cursor mistakes can return plausible but wrong cross-view results.

## Global Constraints

- The published producer is `julie-extract 2.31.1` for the original wiring slice; the release candidate
  adopts the published 2.31.2 patch without changing the legacy SQLite schema `6`, extraction contract
  `4`, report schema `3`, or JSONL schema `4`.
- The family store contract is store contract `1`, store SQLite schema `2`, format epoch `1`, and request/maintenance report schema `1`.
- A family is one git common-dir lineage; a non-git workspace is a family of one.
- `family_id` is a UUID minted at family creation and stored in store metadata, the registry family row, and each member workspace pointer file. It is never a path hash.
- The store `views` table is authoritative. Registry rows and pointer files are caches reconciled idempotently on open.
- Raw `WorkspaceReadContext.IndexDbPath` retires from the read contract. All extraction-data readers obtain access through `IWorkspaceReadSession`.
- A store session resolves `CURRENT` once and pins store instance, generation, view, manifest generation/hash, level stamps, and resolution generation. The pin has bounded heartbeat/expiry and is released on dispose.
- Build the session visibility TEMP table once per store session. Apply visibility before every ranking window or limit; post-filtering is forbidden.
- BM25 document count, average length, and document frequency are view-local. Canonical result order is `score DESC, path ASC, start_line ASC, symbol_id ASC`.
- Reader transactions are bounded. No long read transaction may hold a store or sidecar WAL open.
- `store_log`, not `revision_file_changes`, is the store-mode sidecar feed. Each sidecar owns an idempotent sequence cursor and publishes a completeness stamp validated by the read session.
- Store writes are executed only by `julie-extract` through its coordinator. Miller never writes `store.db`, `coord.db`, manifests, resolution bases, or generation files directly.
- Lock order is machine governor → store-writer lease → sidecar-converger lease; release in reverse order.
- Store mode preserves L1-first serving and reports truthful per-capability degradation until L2, L3, resolution, and sidecar stamps converge.
- A Full extraction stamp does not certify exact identifier resolution; usage-dependent consumers report
  or refuse a store view until its resolution state is exact. Store freshness and sidecar metadata use
  the store-log sequence, not the legacy extraction revision.
- Disabling store mode exports the current view to a fresh legacy artifact or reports not-ready. It never serves an old per-workspace artifact as current.
- The store default-on decision remains deferred to Ph5. Ph3 ships an explicit environment/config switch and exercises both modes.
- `Miller.Core` remains I/O-free. No new MCP tools. Existing lexical-only output stays byte-identical.
- Every test that launches `julie-extract` is class-level `Category=Scale` and obtains the binary through `ScaleTestSupport.RequireJulieServer()`.
- Language-dependent acceptance uses the real all-language producer fixture; store behavior is uniform across all 38 supported languages.

## Verification Strategy

**Project source of truth:** `AGENTS.md` testing/build rules, `docs/plans/2026-08-07-index-store-v4-contract.md`, `docs/plans/2026-08-06-index-store-views-program.md`, and the published `julie-extract 2.31.1` release contracts.

**Worker red/green scope:** the smallest named xUnit class or method through `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~<test>`; store subprocess tests also include `Category=Scale`.

**Worker ceiling:** `scripts/test.sh` for pure/fast work; one named Scale class for producer-backed work. Do not run the whole Scale suite after every slice.

**Worker gate invariant:** the named test must prove behavior through `IWorkspaceReadSession`, `JulieStoreClient`, or a public Miller workspace action, not private helper state.

**Lead affected-change scope:** `dotnet build Miller.slnx -c Release`, `scripts/test.sh`, and focused Scale classes for store protocol, read equivalence, coordinator takeover, migration, and rollback.

**Branch gate:** `scripts/test.sh all`, `scripts/test-plugin.sh`, release build with the restored `2.31.1` binary, and the reopened Ph3 end-to-end acceptance harness.

**Security scope:** none declared; this plan adds no dependency, credential, network listener, or write surface outside existing local CLI/SQLite paths.

**Replay/metric evidence:** hard gates are zero dedicated-vs-store row mismatches, byte-identical lexical output, exactly-once request effects, and no stale-artifact rollback serving. Fast-suite elapsed time is report-only; repeatable performance evidence and physical-byte measurements belong to a local machine, with CI checking correctness rather than timing. The store import/resolve request window defaults to Miller's four-hour process hard cap (honoring `MILLER_EXTRACT_HARD_CAP`) and is controlled by `MILLER_STORE_REQUEST_TIMEOUT`; it is a liveness setting, not a performance gate.

**Escalation triggers:** any public-output drift runs the corresponding CLI/MCP contract suite; any sidecar-key or ranking change runs search/content/vector parity; any bootstrap/refresh change runs all Scale workspace lifecycle tests; any package/pin change runs restore and release build.

**Assigned verification failure:** workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** record invariant, command, scope label, commit SHA, result, and timestamp. Replay/metric entries also record mismatch count, request/effect counts, store-open timing, time-to-exact, fast-suite wall time, and whether truncation or skips occurred.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Adopt 2.31.1 | Batch A | `scripts/julie-pins.json`; `src/Miller.Indexing/MillerExtractContract.cs`; pinned-version assertions; adoption tests/doc | No | Mechanical pin/contract work is independent of new store modules. |
| Task 2: Store process contract | Batch A | new `src/Miller.Indexing/Store/JulieStoreContract.cs`, `JulieStoreClient.cs`, `StoreReports.cs`; store-client tests | No | Uses only the published CLI contract and does not touch provider/registry code. |
| Task 3: Registry family/view identity | Batch A | `WorkspaceRegistry.cs`, `WorkspaceRegistryModels.cs`, new family resolver/pointer files, registry tests | No | New schema columns and resolution logic are independent until orchestration consumes them. |
| Task 4: Read-session seam and legacy adapter | None - serial | new read-session files; `SqliteReadOnlyAccess.cs`; extraction readers; `WorkspaceReadContext.cs`; provider/read tests | Yes | Must establish byte-identical legacy behavior before the store adapter and sidecars use the seam. |
| Task 5: Family-store read adapter | None - serial | new `FamilyStoreReadSession.cs`, store visibility/ranking readers, provider store tests | Yes | Consumes Tasks 3–4 registry and read-session contracts. |
| Task 6: Store-aware sidecars | None - serial | search/content/vector sidecar writers/readers, convergers, parity tests | Yes | Requires the store read snapshot and visibility semantics from Task 5. |
| Task 7: Bootstrap, refresh, migration, rollback | None - serial | `IndexBootstrapService.cs`, `IndexerService.cs`, `CrossWorkspaceRefreshService.cs`, new coordinator, lifecycle tests | Yes | Composes Tasks 1–6 and is the first slice that makes store mode operational. |
| Task 8: Provenance, docs, and acceptance | None - serial | workspace facts/renderers, dashboard read model, CLI/health tests, CLAUDE/AGENTS/docs, acceptance harness | Yes | Verifies and documents the completed behavior from every prior task. |

All tasks use `serial-worker-commit` in a no-delegation run. If delegation becomes available, Batch A uses `parallel-lead-commit`; all later tasks remain serial.

### Task 1: Adopt the published julie-extract 2.31.1 release

**Files:**
- Modify: `scripts/julie-pins.json`
- Modify: `src/Miller.Indexing/MillerExtractContract.cs`
- Modify: `tests/Miller.Tests/Indexing/JulieSchemaGateTests.cs`
- Modify: `tests/Miller.Tests/Indexing/MillerExtractContractTests.cs`
- Modify: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`
- Create: `docs/findings/2026-08-09-julie-extract-2.31.1-adoption.md`

**Interfaces:**
- Consumes: live release `v2.31.1`, four public archive names/digests, legacy contract versions, store contract versions.
- Produces: one restored `.tools/julie-extract` whose version and archive digest match the pin; checked-in producer contract constants for later tasks.

**Contract inputs:** archive SHA-256: Apple ARM `2265be55ec682b9079995aff34841d29b82a9be3a5d8161629bf79353e00ec4f`; Apple x64 `552521d19d65e42362c72f55cbe9dbe2a04648632854af4e36d03de72c10f58f`; Linux x64 `ba9f5f151546aec2f33c5bdc244d1c897793f9158ec4f3e40e6cfc7c7c0f6334`; Windows x64 `9e978620f578830cd53a778e5e5780b9a3daef4a0debca4a3b26c567783bcf8d`.

**Steps:**

1. Change only test expectations to `2.31.1`; run the focused pin tests and capture the expected `2.30.0` failure.
2. Update all four digests and `PinnedJulieExtractVersion`; keep legacy schema/contract constants unchanged and add store constants `1/2/1/1` in `JulieStoreContract` when Task 2 lands.
3. Replace the worktree's setup-only `.tools` symlink with a local ignored directory, run `scripts/restore-julie-extract.sh`, and verify `.tools/julie-extract --version` plus the restored archive digest.
4. Run focused pin tests, release build, and record live/downloaded asset evidence.
5. Commit `chore: adopt julie-extract 2.31.1` after the Goldfish checkpoint.

**Acceptance criteria:**
- [x] Pin JSON, contract constant, direct assertions, restored binary, and downloaded release agree on `2.31.1`.
- [x] Legacy compatibility numbers remain `6/4/3/4`; store numbers are asserted separately.
- [x] Restore, focused tests, and Release build pass.

### Task 2: Add the typed julie store process contract

**Files:**
- Create: `src/Miller.Indexing/Store/JulieStoreContract.cs`
- Create: `src/Miller.Indexing/Store/JulieStoreClient.cs`
- Create: `src/Miller.Indexing/Store/StoreReports.cs`
- Create: `tests/Miller.Tests/Indexing/JulieStoreClientTests.cs`
- Create: `tests/Miller.Tests/Indexing/LiveJulieStoreClientScaleTests.cs`

**Interfaces:**
- Consumes: public `julie-extract store import|update|delete|resolve|export --json` commands and report schema 1.
- Produces: `StoreRequest`, `StoreRequestResult`, `StoreRequestState`, and `JulieStoreClient.Submit(StoreRequest, CancellationToken)`.

**Implementation shape:**

```csharp
internal sealed record StoreRequest(
    StoreOperation Operation,
    string StoreRoot,
    string FamilyId,
    string ViewId,
    string WorkspaceRoot,
    IReadOnlyList<string> Paths,
    string RequestedLevel,
    string RequestId,
    string IdempotencyKey,
    TimeSpan RequestTimeout);

internal interface IJulieStoreClient
{
    StoreRequestResult Submit(StoreRequest request, CancellationToken cancellationToken);
}
```

**Steps:**

1. Write argv/report tests for every operation, IDs, repeated delete paths, level, timeout, JSON stdout purity, stable failure classes, and malformed/incompatible reports.
2. Implement one process invocation path with bounded cancellation and report parsing; do not duplicate store state or retry policy in Miller.
3. Add Scale tests against a temporary family for idempotent import/update/delete, L1→Full resolution, export, and a timed-out request later completed by a successor.
4. Run the named unit and Scale classes and commit.

**Acceptance criteria:**
- [x] Every store command is represented by typed inputs/results; raw JSON does not escape the adapter.
- [x] Miller never opens either store database writable.
- [x] Repeated idempotency keys observe the original request and result.

### Task 3: Persist and reconcile family/view identity

**Files:**
- Modify: `src/Miller.Indexing/WorkspaceRegistry.cs`
- Modify: `src/Miller.Indexing/WorkspaceRegistryModels.cs`
- Create: `src/Miller.Indexing/Store/StoreFamilyResolver.cs`
- Create: `src/Miller.Indexing/Store/StoreWorkspacePointer.cs`
- Modify: `tests/Miller.Tests/Indexing/WorkspaceRegistryTests.cs`
- Create: `tests/Miller.Tests/Indexing/StoreFamilyResolverTests.cs`

**Interfaces:**
- Consumes: canonical git common dir plus `WorkspaceRootIdentity`; non-git singleton identity; store `views` authority.
- Produces: `StoreFamilyBinding ResolveOrCreate(WorkspaceRootFacts, StoreMode)` and atomic `<workspace>/.miller/store.json` pointer read/write/reconcile.

**Implementation shape:**

```csharp
internal sealed record StoreFamilyBinding(
    Guid FamilyId,
    string StoreRoot,
    string ViewId,
    string WorkspaceRoot,
    StoreBindingState State);
```

**Steps:**

1. Add registry migration tests for family rows, member rows, unique `(family_id, view_id)`, old-registry compatibility, and no path-hash family identity.
2. Implement create/lookup with UUID minting and common-dir identity replacement semantics.
3. Implement pointer containment, atomic replacement, schema validation, registry/store reconciliation, and root/view mismatch errors.
4. Prove path reuse creates a new family only after disappearance plus identity change; missing identity evidence never replaces one.
5. Run registry/provider tests and commit.

**Acceptance criteria:**
- [x] The same live git lineage shares one family; unrelated or replaced lineages do not.
- [x] Store view state wins over stale registry/pointer caches and repairs them idempotently.
- [x] No registry mutation occurs for malformed or mismatched stores.

### Task 4: Introduce the read-session seam without changing legacy output

**Files:**
- Create: `src/Miller.Indexing/Reads/IWorkspaceReadSession.cs`
- Create: `src/Miller.Indexing/Reads/WorkspaceReadSnapshot.cs`
- Create: `src/Miller.Indexing/Reads/LegacyArtifactReadSession.cs`
- Modify: `src/Miller.Indexing/SqliteReadOnlyAccess.cs`
- Modify: extraction-data readers under `src/Miller.Indexing/` currently calling `SqliteReadOnlyAccess.Open(...)`
- Modify: `src/Miller.Indexing/RepositoryIndexLoader.cs`
- Modify: `src/Miller.Server/Workspaces/WorkspaceReadContext.cs`
- Modify: `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`
- Modify: `tests/Miller.Tests/ReadToolRoutingTestSupport.cs`
- Modify: existing reader/provider/tool contract tests

**Interfaces:**
- Consumes: current standalone SQLite artifact and all existing reader SQL.
- Produces: the only extraction-data connection seam used by callers.

**Implementation shape:**

```csharp
public interface IWorkspaceReadSession : IDisposable
{
    WorkspaceReadSnapshot Snapshot { get; }
    TResult Read<TResult>(Func<SqliteConnection, TResult> query);
}

public sealed record WorkspaceReadSnapshot(
    string WorkspaceRoot,
    string? WorkspaceId,
    string ArtifactOrStoreId,
    string ViewId,
    WorkspaceFreshnessToken Freshness,
    string IndexLevel,
    WorkspaceReadMode Mode);
```

**Steps:**

1. Add a source-guard test that rejects extraction readers accepting/opening a raw artifact path after migration.
2. Implement the legacy adapter and migrate one reader family at a time: repository/symbol graph; path/edit/freshness; patterns/metrics/reference evidence; sidecar source readers; dashboard facts.
3. Keep each migrated family byte-equivalent through its existing tests before moving to the next.
4. Replace `WorkspaceReadContext.IndexDbPath` with `ReadSession`/`Snapshot`; keep tool cores otherwise unchanged.
5. Run all fast tests and commit.

**Acceptance criteria:**
- [x] Legacy mode produces byte-identical MCP/CLI output and retains current cache/freshness behavior.
- [x] Production extraction readers do not accept or open a raw path outside the two session adapters.
- [x] Fast suite stays below its 30-second ceiling.

### Task 5: Implement manifest-scoped family-store reads

**Files:**
- Create: `src/Miller.Indexing/Reads/FamilyStoreReadSession.cs`
- Create: `src/Miller.Indexing/Reads/StoreVisibility.cs`
- Create: `src/Miller.Indexing/Reads/WorkspaceFreshnessToken.cs`
- Create: `src/Miller.Indexing/Reads/ViewRankingState.cs`
- Modify: `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`
- Create: `tests/Miller.Tests/Indexing/FamilyStoreReadSessionTests.cs`
- Create: `tests/Miller.Tests/Server/StoreWorkspaceIndexProviderScaleTests.cs`

**Interfaces:**
- Consumes: `StoreFamilyBinding`, store schema v2, `CURRENT`, coord pin API, manifest/resolution/sidecar stamps.
- Produces: the second `IWorkspaceReadSession` adapter and store-aware provider cache keys.

**Steps:**

1. Test exact pointer/registry/view/CURRENT/generation validation and typed incompatibility/floor/corruption outcomes before any pin is created.
2. Implement bounded pin acquire/heartbeat/release and a freshness token containing every contract component.
3. Build ordered visibility once per session, attach only validated ready files, and expose L1/L2/L3/resolution/sidecar capabilities.
4. Implement path seeks and candidate windows with visibility before limit, view-local BM25 facts, and canonical tie ordering.
5. Run adversarial retained-history parity: more than 200 hidden trigram candidates and more than 500 hidden vector candidates must not crowd the visible top-K.
6. Commit after zero row/output mismatches.

**Acceptance criteria:**
- [x] Dedicated artifact and store view return row-equivalent results across all read families.
- [x] Lexical output is byte-identical and cache invalidation keys only from the freshness token.
- [x] Sessions release pins on success, error, and disposal; expired/dead pins never become permanent GC roots.

### Task 6: Re-key and converge Miller sidecars from store_log

**Files:**
- Modify: `src/Miller.Indexing/SymbolSearchSidecar.cs`
- Modify: `src/Miller.Indexing/SearchIndexWriter.cs`
- Modify: `src/Miller.Indexing/ContentCorpusSidecar.cs`
- Modify: `src/Miller.Indexing/ContentCorpusWriter.cs`
- Modify: `src/Miller.Indexing/VectorSidecar.cs`
- Modify: `src/Miller.Server/Hosting/IndexerSidecarConverger.cs`
- Modify: `src/Miller.Server/Hosting/VectorConvergeService.cs`
- Modify: sidecar tests and add `StoreSidecarConvergenceScaleTests.cs`

**Interfaces:**
- Consumes: request/effect `store_log` sequence, store version identities, read-session visibility, family/view stamps.
- Produces: family-keyed search/content/vector databases with idempotent cursors and validated completeness stamps.

**Steps:**

1. Add cursor schema/state-machine tests covering replay before/after sidecar commit, sequence gaps, generation change, view flip, failure, and rebuild.
2. Re-key source rows by version identity and family; keep explicit external/web content family-scoped.
3. Apply visibility inside FTS and vector candidate windows; retain `collapsed_len` trigram ordering and lexical byte equivalence.
4. Converge in lock order without the store-writer lease; publish a stamp only after the sidecar transaction and cursor commit.
5. Prove kill/replay exactly once for every sidecar and commit.

**Acceptance criteria:**
- [x] A crash can replay work but cannot duplicate or skip visible sidecar rows.
- [x] The read session refuses stale stamps instead of silently using a stale sidecar.
- [x] `MILLER_SEMANTIC=off` remains a zero-work guarantee.

### Task 7: Wire bootstrap, refresh, migration, and rollback

**Files:**
- Create: `src/Miller.Server/Workspaces/StoreWorkspaceCoordinator.cs`
- Modify: `src/Miller.Server/Hosting/IndexBootstrapService.cs`
- Modify: `src/Miller.Server/Hosting/IndexerService.cs`
- Modify: `src/Miller.Server/Hosting/JulieExtractOps.cs`
- Modify: `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`
- Modify: `src/Miller.Server/Hosting/MillerServiceRegistration.cs`
- Modify: bootstrap/indexer/refresh/startup tests
- Create: `tests/Miller.Tests/Server/StoreWorkspaceLifecycleScaleTests.cs`

**Interfaces:**
- Consumes: store client, family resolver, read sessions, machine governor, current scan intents/failure journal.
- Produces: store-aware open/refresh/full/update/delete orchestration and honest legacy migration/rollback.

**Steps:**

1. Add pure coordinator decision tests for open/import/update/delete/repoint/deepen/resolve/migrate/export and lock-order refusal.
2. For a new family, run capacity preflight, enqueue import, serve once L1 is committed, and continue L2→L3→resolution/sidecars in background.
3. Map watcher updates/deletes and explicit refresh/full intents to idempotent requests; preserve failure journal/backoff and scan governor admission.
4. Replace linked-worktree copy/rebind with family/view creation and manifest bind when store mode is enabled; keep the old path only in legacy mode.
5. Migrate readable legacy artifacts with `store import --from-artifact`; leave them read-only/reclaimable until store equivalence passes.
6. On disable, export each active current view before legacy serving; return not-ready on failure or insufficient capacity.
7. Run process-kill/takeover, mixed-version, root disappearance/reuse, and concurrent-worktree Scale tests; commit.

**Acceptance criteria:**
- [x] A fresh linked worktree serves L1 from the family store without copying a standalone artifact; current level-up and rollback paths are covered by focused Scale and lifecycle tests.
- [x] Interactive updates remain bounded behind batch chunks and no lock-order cycle exists; machine
  admission ends before sidecar convergence, and the family sidecar lease serializes all Miller-owned
  family sidecar writers and vector lifecycle mutations.
- [x] Migration and rollback never make a stale artifact look current; malformed-pointer and cross-workspace refresh cases force source reconciliation.

### Task 8: Surface provenance, synchronize guidance, and close Ph3 acceptance

**Files:**
- Modify: `src/Miller.Server/Tools/WorkspaceFacts.cs`
- Modify: `src/Miller.Server/Tools/WorkspaceFactsAssembler.cs`
- Modify: `src/Miller.Server/Tools/WorkspaceRender.cs`
- Modify: `src/Miller.Server/Cli/CliDispatch.cs`
- Modify: `src/Miller.Dashboard/DashboardData.cs`
- Modify: `src/Miller.Dashboard/Components/WorkspaceDetailPanel.razor`
- Modify: corresponding CLI/MCP/dashboard contract tests
- Create: `docs/findings/2026-08-09-index-store-ph3-acceptance.md`
- Modify: `docs/README.md`
- Modify: `docs/plans/2026-08-06-index-store-views-program.md`
- Modify: `CLAUDE.md`
- Regenerate: `AGENTS.md`

**Interfaces:**
- Consumes: final registry/session/coordinator/sidecar state.
- Produces: existing status/health/dashboard surfaces with family/view/generation/level/resolution/sidecar/migration/rollback provenance; Ph3 evidence.

**Steps:**

1. Add compact and JSON snapshots for legacy, L1-serving, deepening, exact, degraded, migration, disk-blocked, rollback-exporting, incompatible, and failed states.
2. Add dashboard read-only provenance; leave prune/purge mutations to Ph4.
3. Run the all-language end-to-end matrix: primary import, fresh worktree bind, branch churn/repoint, concurrent requests, kill/takeover, sidecar replay, migration, disable/export, and dedicated-artifact equivalence.
4. Record hard-gate facts and report-only timing/size metrics in the acceptance finding.
5. Amend ownership guidance in `CLAUDE.md`, run `scripts/sync-agents.sh`, and prove `CLAUDE.md`/`AGENTS.md` match.
6. Run affected-change, branch, plugin, and security scopes; complete the Ph2/Ph3 program checkboxes only from evidence; commit.

**Acceptance criteria:**
- [x] Fresh worktree serving, existing contract compatibility, fast-suite budget, and honest off-switch boxes are checked with current v4.1 evidence.
- [x] Existing nine MCP tools and public read-command contracts remain unchanged except additive workspace provenance.
- [ ] No uncommitted or untracked task work remains; this remains a final handoff criterion, not a historical claim.
