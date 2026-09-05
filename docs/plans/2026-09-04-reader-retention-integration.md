# Reader Retention Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `razorback:subagent-driven-development` when subagent delegation is available. Fall back to `razorback:executing-plans` for tightly-sequential or no-delegation runs.

**Goal:** Make every Miller family-store read session acquire, validate, renew, and release the producer-owned reader registration before opening a generation, so maintenance cannot delete files or log rows still being read.

**Architecture:** Add a typed `StoreReaderRegistrationRunner` in the indexing/server boundary and a disposable registration handle owned by each `FamilyStoreReadSession`. The runner invokes the producer's `store reader acquire|renew|release` contract; the session opens only the exact admitted snapshot and validates all returned identity/path fields before serving reads. Legacy mode performs zero reader-pin activity, while an incompatible old pinned producer refuses safely or uses only an explicitly opted-in legacy export path.

**Tech Stack:** .NET 10/C#, `FamilyStoreReadSession`, `WorkspaceReadSessionFactory`, `StoreFamilyResolver`, producer CLI JSON v1, bounded JSON parsing, SQLite read-only sessions, hosted lifecycle services, CLI/evaluation callers, and fast/Scale tests with the real producer where required.

**Architecture Quality:** High-risk cross-repository lifecycle integration. The producer contract is authoritative at `../../../julie-extractors/docs/plans/2026-09-04-producer-retention-contract.md`; this plan consumes its typed report and sequencing rules without inventing a new wire format. The concrete retention boundary is `FamilyStoreReadSession`, not only `WorkspaceReadSessionFactory`, because direct session opens already exist in bootstrap, rollback export, tree diff, CT/evaluation, and Scale paths.

## Global Constraints

- Producer pin acquisition happens before any serving generation `SQLiteConnection` handle is opened. No serving caller may open first and register later. The approved metadata-only exception below is the only pre-admission database-read exception.
- `FamilyStoreReadSession` is the concrete enforcement boundary for all direct callers; changing only the factory is insufficient.
- Use a typed `Indexing.StoreReaderRegistrationRunner`/registration handle in the indexing/server boundary. Do not move process, CLI, SQLite, or filesystem I/O into `Miller.Core`.
- The registration handle owns the opaque nonce and bounded producer report, validates every snapshot identity and path field, and exposes only the admitted snapshot needed to open the session.
- Before acquire, compare only the requested family, view, and generation with the selected `StoreFamilyBinding`; do not open or read a generation database to manufacture expected post-acquire fields.
- After acquire, compare the returned family, view, manifest generation, generation name, store instance, manifest hash, extraction epoch, served log sequence, minimum retained sequence, and snapshot fingerprint with the known binding and then with the opened generation's actual `Snapshot`. Index level and level stamps are derived by Miller after protection from its existing read-snapshot logic; they are not producer fields.
- Validate the producer `store_instance_id` using the existing family identity convention (family UUID plus `:` plus admitted generation name); do not invent a new consumer-side instance-id derivation.
- Open the generation named by the admitted snapshot. Never re-resolve `CURRENT`, refresh, or retarget to a later generation after acquire.
- On validation or open failure, close bounded transaction/connection resources first, then release the registration. Preserve the primary error if release also fails; record a scoped retry/owed record for the release failure.
- Registration disposal closes any bounded snapshot transaction and bounded connection, then the main session connection, before release. Disposal is idempotent. A release failure must remain retryable and must never clobber the primary read/open error.
- A lost acquire reply retries the same nonce and exact request. Do not mint a new nonce after an ambiguous result.
- Renewal is deadline-based with a 120-second lease and a 30-second diagnostic schedule, using one bounded shared scheduler per process/session owner. Do not create an unbounded timer per query. A producer invocation has a 10-second process timeout and 64 KiB bounded stdout/stderr capture. Failed renewal retains protection and reports degradation; it never authorizes deletion or fabricates a normal released pin.
- The owner PID sent to the producer is Miller's original process PID, never a short-lived producer subprocess PID. Miller captures its process birth identity locally only for diagnostics; the producer captures authoritative birth identity from the live PID and never trusts a CLI birth-identity field.
- Legacy mode has zero acquire, renew, release, pin, and producer reader activity. It remains the explicit opted-in legacy export path only.
- A mixed old pinned producer that cannot prove it honors reader registrations is typed incompatible and refuses unsafe family-store serving. It must not silently fall back to a stale legacy artifact. An existing explicit legacy export path may be used only when the user has opted into it.
- Source-build and producer pin acquisition occur before release integration. Do not invent a release number, pin, checksum, or package asset; do not publish or release without explicit approval. A source-built producer may be injected into focused tests without replacing installed `.tools` or bypassing version guards. No M1 pin adoption is complete until an actual compatible producer version/checksum is verified against `scripts/julie-pins.json` and the packaged compatibility guard; source-built injection alone cannot authorize release.
- Preserve bounded one-shot fact reads, CT snapshot disposal, semantic/search sidecar behavior, workspace routing, freshness, and all existing read-session contracts.
- Acquire exactly once per `FamilyStoreReadSession` lifecycle and release exactly once (or one scoped owed retry); do not spawn a producer process per symbol, query, test case, or read operation. Measure session-open producer process overhead explicitly. Pooled registration reuse is not assumed or claimed unless separately implemented and evidenced.
- Preserve lexical-only behavior and all `MILLER_*` zero-work guarantees. Reader registration must not activate semantic, CT, or unrelated background work.
- Existing producer fields and exit/report semantics are authoritative. Do not add a new MCP tool or a new producer wire field in this plan.
- Use explicit workspace paths and task-specific environment variables in commands; do not repurpose `$HOME`, `$CODEX_HOME`, or common system variables.

## Producer Contract Inputs

### Approved metadata-only exception, 2026-09-05

The user approved bounded metadata-only discovery and compatibility reads before registration. This resolves the family/view discovery cycle described in `docs/findings/2026-09-05-m1-discovery-boundary.md` without extending the producer wire contract.

- Only family identity, view/root catalogue, and writer/reader compatibility facts may be read through a narrowly named discovery/preflight path. Use read-only bounded transactions and close every connection before acquiring a registration.
- Discovery results are provisional. Revalidate the selected family/view/root and admitted generation/manifest identity after acquisition. A race fails or retries safely; it never permits unpinned serving or a stale legacy fallback.
- No symbols, relationships, source facts, freshness snapshots, query results, or sidecar hydration may use this exception. `Probe` reads that expose serving freshness still require admission.
- Writer preflight must work when the requested view or retention catalogue does not yet exist. Existing WAL checkpointing and producer mutation anchors are maintenance mechanisms, not serving sessions; do not turn them into a circular registration prerequisite.
- Every serving session, including deferred fact warming and bounded CLI/CT reads, keeps its registration until its last generation connection closes. Deferred workers retain the existing handle before scheduling and release it after their shared task settles; they do not acquire another pin.
- Miller's workspace `indexer.lock` is not the producer writer lease or maintenance intent. Preserve existing workspace serialization. No same-PID fence bypass is permitted.

This exception supersedes blanket references to "any generation handle" only for the metadata paths above. All serving-session acceptance gates remain unchanged.

The producer plan defines `store reader acquire|renew|release` JSON report schema v1, immutable identity `(pin_id, owner_nonce, owner_pid, owner_birth_identity, view_id, manifest_generation, generation_name)`, `manifest_hash`, `store_instance_id`, `extraction_identity_epoch`, `protected_manifest_count`, `served_store_log_sequence`, `min_retained_store_log_sequence`, and `snapshot_fingerprint`. Acquire is idempotent for the same nonce and identical request; a mismatched nonce/request refuses. The producer commits the registration before returning `acquired` and captures process birth identity itself. Miller receives no copied per-reader version-root list, index level, or level stamps.

The producer also requires maintenance-intent fencing, bounded reports, fail-closed unknown identity handling, and no deletion based on heartbeat timeout alone. Miller must treat `reader_identity_unknown`, failed renewal, producer unavailability, and release failure as retained protection/degraded lifecycle states, never as permission to open a different generation or assume the pin is gone.

J1 execution clarified that process birth identity remains producer-internal and is never a Miller wire field. The served log sequence is the maximum retained committed log sequence at admission, or zero when legitimate receipt-based pruning has emptied the log. If the manifest's original flip event remains, its sequence is the retention floor; otherwise the floor is the served sequence. Accept zero and do not substitute the allocator's reserved high-water or require pruned history to exist. The registration does not certify complete delta history: sidecar consumers still validate their own continuity and fall back conservatively. An ordinary writer lease alone does not prevent coordinator-only renew/release, although foreign maintenance intent does.

Rollout protects registered readers, not arbitrary existing SQLite readers. The producer's writer floor excludes older maintenance writers; it does not retrofit registration into an already-running older Miller or another direct consumer. Document a consumer restart/upgrade step before relying on the new retention guarantee during rollout. Never infer that legacy readers are absent from cursor state or claim that J1 prevents direct filesystem access. Do not kill older processes automatically.

The retained committed log maximum can decrease after pruning, even when the manifest is unchanged. Do not substitute it for Miller's existing monotonic revision or level-stamp identities. Inspect the actual revision derivation and preserve CT/cache freshness invariants; characterize a release → log prune → reacquire sequence before changing those mappings.

Producer admission is a coordinator `BEGIN IMMEDIATE` transaction paired with a read-only store connection, `busy_timeout=0`, and no writer open or schema migration inside admission. A busy coordinator rolls back all admission state before a bounded retry. Miller must audit callers that already hold maintenance/writer locks and reorder the read before that lock or use the producer's delegated fence proof; a same-PID shortcut is never valid.

## Current Miller Read Boundaries

`FamilyStoreReadSession` is `src/Miller.Indexing/Reads/FamilyStoreReadSession.cs:37`; its constructors/open methods and disposal own the main connection, optional bounded connection/transaction, visibility, and snapshot identity. `WorkspaceReadSessionFactory` is `src/Miller.Indexing/Reads/WorkspaceReadSessionFactory.cs:6`, but direct opens also occur below the factory boundary.

The integration must trace and cover these direct paths: `IndexBootstrapService` (`src/Miller.Server/Hosting/IndexBootstrapService.cs:1133`), `StoreFamilyResolver` (`src/Miller.Indexing/Store/StoreFamilyResolver.cs:253`), `StoreRollbackExporter` (`src/Miller.Server/Workspaces/StoreRollbackExporter.cs:170`), `StoreWorkspaceCoordinator` (`src/Miller.Server/Workspaces/StoreWorkspaceCoordinator.cs:527,1148`), `CrossWorkspaceRefreshService` rollback/export paths, tree diff (`StoreWorkspaceCoordinator.DiffCurrentTree`), `WorkspaceReadSessionFactory` including `OpenForOneShotCli`, `IndexerService`, CT revision polling, dashboard/onboarding read paths, `CliDispatch` direct read paths, `EvaluationWorkspaceLeaseService`, and all tests/Scale fixtures that call `FamilyStoreReadSession.Open` directly. Use Miller `trace`/`search` again before editing because direct call sites can move.

The public factory remains a routing convenience and legacy artifact selector. It must not be the only place that can enforce registration. A family binding handed to `FamilyStoreReadSession.Open` must already carry the producer runner or an explicit legacy-mode marker; an enabled family session without either is a refusal, not an unpinned read.

## Proposed Internal Types

These are new internal C# types, not existing public APIs:

```csharp
// Proposed internal files and types; these do not exist yet.
// src/Miller.Indexing/Store/StoreReaderRegistrationContracts.cs
internal sealed record ReaderAcquireRequest(
    string StoreRoot,
    string FamilyId,
    string ViewId,
    string GenerationName,
    string OwnerLabel,
    int OwnerPid,
    string OwnerNonce,
    TimeSpan Lease);

internal sealed record ReaderAcquireResult(
    int ReportSchemaVersion,
    string State,
    string FamilyId,
    string ViewId,
    string GenerationName,
    long ManifestGeneration,
    string PinId,
    string OwnerNonce,
    int OwnerPid,
    string StoreInstanceId,
    string ManifestHash,
    long ExtractionIdentityEpoch,
    long ServedStoreLogSequence,
    long MinRetainedStoreLogSequence,
    int ProtectedManifestCount,
    string SnapshotFingerprint,
    DateTimeOffset ExpiresAt,
    string? Warning);

internal sealed record ReaderRenewRequest(string StoreRoot, string FamilyId, string PinId, string OwnerNonce, int OwnerPid, TimeSpan Lease);
internal sealed record ReaderReleaseRequest(string StoreRoot, string FamilyId, string PinId, string OwnerNonce);
internal sealed record ReaderRenewResult(string State, string PinId, DateTimeOffset ExpiresAt, string? Warning);
internal sealed record ReaderReleaseResult(string State, string PinId, bool Released, string? Warning);

// src/Miller.Indexing/Store/StoreReaderRegistrationRunner.cs
internal sealed class StoreReaderRegistrationRunner
{
    internal ReaderAcquireResult Acquire(ReaderAcquireRequest request, CancellationToken cancellationToken);
    internal ReaderRenewResult Renew(ReaderRenewRequest request, CancellationToken cancellationToken);
    internal ReaderReleaseResult Release(ReaderReleaseRequest request, CancellationToken cancellationToken);
}

// src/Miller.Indexing/Reads/StoreReaderRegistrationHandle.cs
internal sealed class StoreReaderRegistrationHandle : IDisposable
{
    internal StoreReaderSnapshot Snapshot { get; }
    internal ReaderLifecycleStatus Status { get; }
    internal void RenewIfBefore(DateTimeOffset deadline, CancellationToken cancellationToken);
    public void Dispose();
}

// src/Miller.Indexing/Store/StoreReaderRegistrationRegistry.cs
internal sealed class StoreReaderRegistrationRegistry : IDisposable
{
    internal StoreReaderRegistrationHandle Attach(ReaderAcquireResult result, CancellationToken cancellationToken);
}
```

These are proposed internal signatures, not existing APIs. The named files are fixed ownership boundaries. The runner owns `JulieStoreClient` process invocation and bounded report parsing; the handle owns one session's nonce and lifecycle; the registry owns one bounded renewal schedule per Miller process/session owner.

The exact method signatures must follow the existing process-runner and session seams discovered by Miller before implementation. The runner owns producer process invocation, bounded stdout/stderr capture, exit classification, JSON v1 parsing, and retry of an ambiguous acquire with the same nonce. The handle owns lifecycle ordering and session identity; it does not expose arbitrary producer JSON or permit callers to mutate the admitted snapshot.

## Snapshot and Path Validation

Before `FamilyStoreReadSession` opens the main generation connection, the runner must validate:

1. Before acquire, family ID, view ID, and requested generation equal the selected `StoreFamilyBinding`; no generation SQLite handle is opened for this check.
2. The producer report is schema v1, one bounded JSON object, has no duplicate keys, and has no unrecognized identity values that would cause a later re-resolution.
3. After acquire, store instance, manifest hash, extraction identity epoch, served sequence, minimum retained sequence, snapshot fingerprint, and protected manifest count are present, bounded, and internally consistent. Validate `store_instance_id` against the existing family UUID plus `:` plus generation-name convention. Index level and level stamps are not parsed from the producer report.
4. The selected binding's manifest root and admitted physical generation resolve beneath the canonical family root without traversal or symlink retargeting. Miller does not validate copied per-file version paths because the producer does not return them.
5. The opened session's actual `Snapshot` matches the producer report; any mismatch closes before release and cannot refresh or retarget.

The consumer must open the exact admitted generation path and validate `FamilyStoreReadSession.Snapshot` against the producer result. An advanced `views.current_generation` pointer does not substitute the admitted generation. After protection and exact open succeed, Miller computes `index_level` and level stamps through its existing read-snapshot logic (`ReadIndexLevel`/`ReadLevelStamps`) and preserves the existing consumer freshness tokens and output. A successful open does not permit a freshness poll, factory retry, or `CURRENT` lookup to change the selected generation. If any validation fails, close all opened handles, release the registration, and report the validation error with the producer `pin_id` when available. If the caller already holds a foreign maintenance or writer lock, acquire must use the producer's delegated fence proof or reorder the read before taking that lock; it must never bypass the fence because the PID matches.

## Verification Strategy

**Project source of truth:** producer retention plan, `AGENTS.md`, `docs/contracts/context-json-v1.md`, `FamilyStoreReadSession`, `WorkspaceReadSessionFactory`, store binding/visibility contracts, CT snapshot rules, and existing Store/FamilyReadSession tests.

**Worker red/green scope:** focused C# tests for registration parsing, snapshot/path validation, session lifecycle ordering, legacy zero-work, and caller-boundary disposal. Use the repository's focused command form:

```bash
dotnet test --filter "FullyQualifiedName~FamilyStoreReadSessionTests"
dotnet test --filter "FullyQualifiedName~StoreFamilyResolverTests"
dotnet test --filter "FullyQualifiedName~StoreRollbackExporterTests"
dotnet test --filter "FullyQualifiedName~WorkspaceReadSessionFactoryEnvironmentTests"
```

Producer-backed tests that launch a real extractor use `ScaleTestSupport.RequireJulieServer()` and are `[Trait("Category", "Scale")]`; they do not run in the fast inner loop.

**Worker ceiling:** Focused registration/session tests and pure fake-runner tests. No real producer process, model extraction, Windows guest suite, or full fast suite until the lead's task boundary.

**Worker gate invariant:** Enabled family-store sessions acquire before opening, open only the admitted snapshot, close all SQLite resources before release, preserve primary errors, and keep failed/unknown renewal protected. Legacy sessions perform zero reader activity.

**Lead affected-change scope:** Run Miller impact on `FamilyStoreReadSession`, `WorkspaceReadSessionFactory`, and all direct opens; run all focused read/session/store/CT/evaluation tests affected by the diff. Verify no direct enabled family open bypasses the runner.

**Branch gate:** `dotnet build Miller.slnx -c Release`, one bare `dotnet test` fast suite, and required Scale tests over a real producer extract where direct read paths changed. Run the local Windows fast suite through `win-test sync miller` followed by the documented `win-test run miller -- powershell -Command "dotnet test --filter 'Category!=Scale'"` before release-facing handoff.

**Security scope:** `none declared`; path traversal, bounded parsing, nonce opacity, process identity, and fail-closed retention are correctness gates in this plan.

**Replay/metric evidence:** Hard gates are acquire-before-open, exact snapshot/path validation, zero unsafe unpinned opens, idempotent same-nonce retry (at most three cancellation-aware attempts), disposal ordering, and zero legacy pin activity. Report acquire/renew/release latency, producer process count, retry count, bytes, and renewal overhead separately. No performance threshold is invented here.

**Escalation triggers:** producer schema/version mismatch, missing source-built producer, changed pin/checksum, any direct open not routed through the boundary, path validation ambiguity, release failure that could overwrite a primary error, renewal timer growth, mixed-version unsafe serving, or any Scale race failure.

**Assigned verification failure:** Preserve the producer transcript with nonce redaction, snapshot/path values, process identity status, and SQLite state. Stop the task when restoring behavior would require a producer wire change, stale fallback, or weakened retention guarantee.

**Verification ledger:** Record invariant, command, Miller commit, producer source commit, producer binary/checksum once authorized, family/view/generation, model-independent store fixture, result, timing, and UTC timestamp. Record whether each lifecycle end was explicit release, release retry/owed, definitive producer death, or retained unknown identity.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Lock runner/report and session boundary | None - serial | Create `src/Miller.Indexing/Store/StoreReaderRegistrationContracts.cs`, `StoreReaderRegistrationRunner.cs`, `StoreReaderRegistrationRegistry.cs`, `src/Miller.Indexing/Reads/StoreReaderRegistrationHandle.cs`; modify `FamilyStoreReadSession.cs`; focused unit tests | Yes | The exact internal boundary and parser must be frozen before caller migration. |
| Task 2: Enforce acquire-before-open and disposal | None - serial | `FamilyStoreReadSession.cs`, `WorkspaceReadSessionFactory.cs`, lifecycle tests | Yes | Depends on Task 1 and protects the concrete session boundary before every caller is migrated. |
| Task 3: Migrate server/bootstrap/export/tree callers | Batch A | `IndexBootstrapService.cs`, `StoreRollbackExporter.cs`, `StoreWorkspaceCoordinator.cs`, `CrossWorkspaceRefreshService.cs`, related tests | Yes | Depends on session enforcement; these callers share family bindings and rollback/tree side effects. |
| Task 4: Migrate factory, CT, dashboard, CLI, and evaluator callers | Batch B | `WorkspaceReadSessionFactory.cs`, `IndexerService.cs`, `ContinuousTestRevisionPoller.cs`, dashboard/onboarding/CLI/evaluation callers, related tests | Yes | Depends on Task 2 and must preserve bounded one-shot and CT disposal rules. |
| Task 5: Renewal, legacy, and mixed-version qualification | Batch C | Runner lifecycle files, compatibility guards, renewal tests, legacy/mixed-version tests, docs/evidence | Yes | Depends on all enabled callers using the handle; renewal ownership cannot be proven earlier. |
| Task 6: Real producer race/Scale and final integration gate | None - serial | Scale fixtures/tests, verification finding, no new runtime surface | Yes | Requires producer implementation/pin availability and all callers migrated before race evidence. |

## Task 1: Lock typed runner/report and concrete session boundary

**Files:**

- Create: `src/Miller.Indexing/Store/StoreReaderRegistrationContracts.cs`, `StoreReaderRegistrationRunner.cs`, `StoreReaderRegistrationRegistry.cs`, and `src/Miller.Indexing/Reads/StoreReaderRegistrationHandle.cs`.
- Modify: `src/Miller.Indexing/Reads/FamilyStoreReadSession.cs` only to accept an internal runner/legacy marker and retain the existing connection model.
- Add: `tests/Miller.Tests/Indexing/StoreReaderRegistrationRunnerTests.cs`, `StoreReaderRegistrationHandleTests.cs`, and `StoreReaderRegistrationRegistryTests.cs`.

**Contract inputs:** Producer report v1 and acquire/renew/release fields; existing `StoreFamilyBinding`, `StoreVisibility`, `WorkspaceReadSnapshot`, `FamilyStoreReadSession.Snapshot`, process runner, and bounded output helpers.

**Approach:**

1. Inspect the existing producer/process invocation abstractions and `FamilyStoreReadSession` constructors. Reuse `JulieStoreClient` where its process/report boundary fits; do not invent a second subprocess runner if one already handles executable paths, timeout, stderr, and exit classification.
2. Write failing parser tests for one valid bounded acquire report, missing identity field, wrong report schema, duplicate JSON key, oversized output, manifest-root path traversal, and incompatible producer state. The valid fixture must contain every required snapshot field, including `protected_manifest_count` and manifest/generation identity.
3. Implement typed parsing with strict bounds and no arbitrary JSON passthrough. Keep nonce/pin redacted in ordinary logs while retaining the handle's in-memory nonce for release. Do not add `index_level` or `level_stamp_*` parser fields.
4. Write a fake runner test for same-nonce lost-reply retry: first acquire reports transport loss, second identical request returns `acquired`; assert one nonce and no new generation request.
5. Define the registration handle's ownership and disposal state machine without opening SQLite yet. It must represent `acquired`, `renew-degraded`, `release-owed`, `released`, and `legacy` distinctly. Bound producer process calls to 10 seconds and stdout/stderr capture to 64 KiB; bound ambiguous acquire retries to three attempts and honor cancellation between attempts.
6. Verify focused tests and record the typed contract fields in the ledger.

**Example test shape:**

```csharp
[Fact]
public void Acquire_report_requires_the_exact_snapshot_identity()
{
    ReaderAcquireResult result = ParseAcquire(ValidReport with { GenerationName = "gen-999999" });

    Assert.Throws<StoreReaderSnapshotMismatchException>(() =>
        result.ValidateAgainst(ExpectedBinding("gen-000042")));
}
```

The fixture and expected generation must come from the selected store binding; the test is not permission to retarget.

**Acceptance:**

- [x] Typed runner/report parsing validates all producer snapshot fields and bounded paths.
- [x] Same-nonce ambiguous acquire retries identically.
- [x] Legacy and incompatible states are distinct typed outcomes.
- [x] No generation connection is opened by the runner/parser.

Task 1 component slice committed as `a70c305c`, with 31 registration tests and 25 existing client tests passing. The concrete session signature adjustment moves with Task 2 after approval of the metadata exception.

## Task 2: Enforce acquire-before-open and deterministic disposal

**Files:**

- Modify: `src/Miller.Indexing/Reads/FamilyStoreReadSession.cs` constructors/open/dispose and bounded fact-read cleanup.
- Modify: `src/Miller.Indexing/Reads/WorkspaceReadSessionFactory.cs` only to pass the runner/binding mode without making it the sole enforcement point.
- Modify: `tests/Miller.Tests/Indexing/FamilyStoreReadSessionTests.cs`, `WorkspaceReadSessionFactoryEnvironmentTests.cs`, and focused disposal tests.

**Contract inputs:** Task 1 typed runner; existing `FamilyStoreReadSession.Open` overloads; `OpenForOneShotCli`; bounded `RevisionFactCache.LoadBounded`; store visibility/snapshot fields.

**Approach:**

1. Add failing tests with a recording runner and connection factory proving acquire is called before the first generation connection open. Assert the requested family/view/generation are the binding's values.
2. Add tests for a producer report whose generation changes after the request. Assert refusal before opening and no refresh retarget.
3. Move the smallest lifecycle call into `FamilyStoreReadSession.Open`: acquire, validate the producer retention identity, open the exact admitted manifest/generation, validate the opened `Snapshot`, then derive index level and level stamps with existing Miller read-snapshot readers before constructing the session/registration handle.
4. Add failure-path tests where validation/open fails. Assert bounded transaction/connection and main connection close before release; inject release failure and assert the original open/validation exception remains primary while release becomes scoped owed state.
5. Add idempotent disposal tests for normal session close, repeated close, bounded one-shot facts, and construction failure. Assert no second release and no use-after-close.
6. Verify legacy mode makes no runner calls and continues to use only the explicit legacy artifact path.
7. Run focused FamilyStore and factory tests; compare existing session snapshots and bounded-fact tests byte-for-byte.

**Example test shape:**

```csharp
[Fact]
public void Session_opens_only_after_registration_and_closes_before_release()
{
    var events = new List<string>();
    using (CreateSession(events)) { }

    Assert.Equal(["acquire", "open:gen-000042", "close:bounded", "close:main", "release"], events);
}
```

Use the existing connection seams and exact admitted generation; do not build a second fake session that bypasses `FamilyStoreReadSession.Open`.

**Acceptance:**

- [x] Every enabled `FamilyStoreReadSession.Open` acquires before opening a generation handle.
- [x] Open uses the admitted snapshot and never re-resolves `CURRENT`.
- [x] All connections/transactions close before release; disposal is idempotent.
- [x] Release failure never overwrites a primary session error and remains retryable.
- [x] Legacy mode performs zero producer reader activity.

Task 2 adds a final-release connection guard and `CloseOwed` state on the existing shared lifecycle scheduler. Attempted disposal is not proof of closure. Deferred fact workers retain the same registration until their connection closes. Positive admitted log bounds are checked against retained database rows without replacing per-view freshness identities. Focused implementation scope passed 147 tests; real producer and caller qualification remain Tasks 3–6.

## Task 3: Migrate bootstrap, export, coordinator, rollback, and tree callers

**Files:**

- Modify: `src/Miller.Server/Hosting/IndexBootstrapService.cs`.
- Modify: `src/Miller.Server/Workspaces/StoreRollbackExporter.cs`, `StoreWorkspaceCoordinator.cs`, and `CrossWorkspaceRefreshService.cs`.
- Modify: direct tree-diff and rollback/export tests, including `StoreRollbackExporterTests` and `StoreWorkspaceCoordinatorTests`.

**Contract inputs:** Task 2 session boundary; existing bootstrap/export rollback semantics; `StoreFamilyResolver`; `DiffCurrentTree`; pending/recovery markers.

**Approach:**

1. Trace every `FamilyStoreReadSession.Open` and family binding handoff in these paths. For each, identify the original Miller process PID and ensure the producer request receives that PID rather than a producer child PID.
2. Add fake-runner caller tests for bootstrap read, rollback export, tree diff, and coordinator inspection. Assert exactly one acquisition per session lifecycle and exact generation identity.
3. Migrate callers to construct sessions through the enforced boundary. Keep rollback pending/recovery cleanup behavior independent from reader release; a failed release must not delete or rewrite the primary rollback marker.
4. Audit each caller's existing writer/maintenance lock. Verify that reader acquire does not deadlock behind a foreign maintenance lock; use the producer's delegated fence proof or reorder the read before taking that lock, never a same-PID bypass.
5. Verify that export rollback does not open a generation before acquire and does not use a later `CURRENT` pointer during pointer cleanup.
6. Verify tree diff and coordinator reads close their sessions before any release/rollback mutation that follows.
7. Run focused bootstrap, rollback, coordinator, and FamilyStore tests; run impact analysis to find bypasses.

**Acceptance:**

- [ ] Bootstrap, rollback export, coordinator, refresh, and tree-diff reads all use the enforced session boundary.
- [ ] Each session lifecycle has one acquire and one release attempt, with no per-symbol producer process.
- [ ] Original process PID ownership survives producer subprocess lifetime.
- [ ] Rollback/recovery primary errors and markers remain authoritative when release fails.

## Task 4: Migrate factory, CT, dashboard, CLI, and evaluator callers

**Files:**

- Modify: `src/Miller.Indexing/Reads/WorkspaceReadSessionFactory.cs` and all direct factory/session callers selected by trace.
- Modify: `src/Miller.Server/Hosting/IndexerService.cs`, `src/Miller.Testing/Daemon/ContinuousTestRevisionPoller.cs`, dashboard/onboarding read assemblers, and `src/Miller.Server/Cli/CliDispatch.cs` read paths.
- Modify: `eval/semantic-model-eval/EvaluationWorkspaceLeaseService.cs` and `Program.cs` only where they open family-store sessions.
- Modify corresponding tests: `FamilyStoreReadSessionTests`, CT revision/snapshot disposal tests, dashboard/read tests, CLI read tests, and evaluator lease tests.

**Contract inputs:** `OpenForOneShotCli` bounded fact semantics; CT self-contained revision-keyed sidecar rules; dashboard read-only aggregation; evaluator workspace lease lifecycle.

**Approach:**

1. Trace all `WorkspaceReadSessionFactory.Open`, `OpenForOneShotCli`, and direct `FamilyStoreReadSession.Open` references. Classify each as enabled family-store, explicit legacy, or non-family legacy artifact.
2. Add caller-boundary tests that assert bounded one-shot fact reads still own their separate bounded connection/transaction and release the reader only after all session connections close.
3. Migrate CT polling and evaluator lease paths without starting CT or semantic services as a side effect. A CT snapshot may renew a reader registration through its owning session, but must not create a timer per case/query.
4. Migrate dashboard/onboarding and CLI reads while preserving cheap status behavior and output budgets. Status/list views must not hydrate an index merely to acquire a reader.
5. Ensure a producer process is launched only for registration lifecycle work and that the owner PID remains Miller's long-lived process. Record process count in the test seam.
6. Run focused CT, dashboard, CLI, evaluator, factory, and FamilyStore tests; compare existing JSON/compact output and disposal assertions.

**Acceptance:**

- [ ] All enabled direct callers use the concrete registration boundary.
- [ ] One-shot bounded facts, CT snapshots, dashboard reads, CLI reads, and evaluator leases preserve their existing disposal and zero-work contracts.
- [ ] No caller creates a per-query/per-case renewal timer or producer process.
- [ ] Output schemas and budgets remain unchanged.

## Task 5: Renewal, legacy, mixed-version, and retry qualification

**Files:**

- Modify runner/session lifecycle files from Tasks 1–4 and add focused tests for renewal, release owed state, compatibility, and zero-work legacy paths.
- Add a bounded evidence document under `docs/findings/` only after the producer contract and exact test artifacts exist.

**Contract inputs:** Producer renewal deadline semantics, definitive PID/birth identity, release idempotency, incompatible-store failure classes, and legacy explicit export rule.

**Approach:**

1. Add a fake clock and shared scheduler seam. Test one renewal schedule per session/process owner with the producer's 120-second lease and 30-second diagnostic cadence, deadline-only renewal, and bounded retry/backoff. A failed renewal must leave the registration protected and surface a degraded lifecycle status.
2. Test renewal with matching PID/birth identity, changed identity, unknown identity, expired registration, and foreign maintenance intent. Miller must not infer that any of these authorizes deletion or generation retargeting.
3. Test release on normal close, construction/open failure, cancellation, host shutdown, and repeated disposal. Preserve the primary exception and record a release-owed retry scoped to the exact `pin_id`/nonce; never overwrite a different workspace's owed record.
4. Test legacy mode with a recording runner and assert zero acquire/renew/release calls, zero pin activity, and no stale legacy fallback when enabled family-store mode lacks a compatible producer.
5. Test mixed old/new producer behavior. An incompatible producer must return a typed refusal before generation open. The only alternative is an existing explicit legacy export command selected by the user; no automatic fallback is permitted. Do not mark the M1 integration releasable while `scripts/julie-pins.json` still selects a producer without this contract; source-built injection can qualify tests but cannot satisfy release pin adoption.
6. Test bounded report output, redacted nonce logging, invalid paths, wrong nonce, lost reply same-nonce retry, and producer exit/timeout classification.
7. Run focused tests and inspect all telemetry/error paths to ensure release errors do not replace primary failures.

**Acceptance:**

- [ ] Renewal is shared, bounded, deadline-based, and never grants deletion permission.
- [ ] Failed renewal/unknown identity retains protection and reports degradation.
- [ ] Release retry/owed state is idempotent, scoped, and primary-error preserving.
- [ ] Legacy and incompatible mixed-version behavior is fail-closed with no stale fallback.

## Task 6: Real producer race, Linux/Windows Scale, and final integration gate

**Files:**

- Modify/create Scale fixtures and tests under `tests/Miller.Tests/Indexing/` and `tests/Miller.Tests/Server/` only as needed for the real producer contract.
- Create `docs/findings/2026-09-04-reader-retention-integration.md` with exact evidence and limitations.
- Do not modify producer source or release/pin metadata in this task.

**Contract inputs:** source-built producer implementation and its published report contract; exact family/view/generation fixture; `ScaleTestSupport.RequireJulieServer()`; Windows win-test procedure.

**Approach:**

1. Build or locate the producer from the approved local source path and record its source commit. If no compatible source build is available, stop with `producer-unavailable`; do not invent a pin/checksum or use a stale producer binary.
2. Prepare a real family-store fixture and run the enabled Miller read session against it. Assert acquire precedes the first generation open, exact producer retention identity is served, the admitted manifest remains selected even when `views.current_generation` advances, and disposal releases after all connections close. Record producer process count and session-open overhead to prove one acquire per session lifecycle; do not infer pooled reuse.
3. Race reader acquire against maintenance intent, promotion, rollback, view retirement, and GC. Assert either the registration commits before the fence and protects the manifest, its entries, referenced physical generation, and required log floor, or acquire refuses with no partial rows.
4. Hold an old generation while publishing a new generation; assert reads remain on the admitted old generation. Attempt refresh/rebind and assert no retarget within the live session.
5. Exercise lost acquire reply with the same nonce (at most three cancellation-aware attempts), failed renewal, release retry, process termination, PID reuse/unknown identity, and mixed-version incompatibility. Query producer registration/root facts where the contract permits; CLI output alone is insufficient.
6. Run Linux and Windows Scale coverage. Every real-producer test is `[Trait("Category", "Scale")]` and uses `ScaleTestSupport.RequireJulieServer()`; Windows runs through the required win-test guest procedure. Record the exact producer process/PID ownership and platform identity results.
7. Run the focused suite, Release build, one fast suite, and the Scale tests selected by impact. Confirm no changed MCP schema, telemetry API, semantic default, CT rule, or legacy behavior.
8. Verify the installed producer pin and package compatibility guard against the actual producer artifact. If `scripts/julie-pins.json` names a producer without reader registration support, leave the integration status blocked; do not make source-built test injection look like a release-ready pin.
9. Write the finding with exact commits, commands, fixture IDs, reports (nonces redacted), SQL/root counts, race outcomes, and statuses `qualified`, `producer-unavailable`, `pin-blocked`, or `hardware/platform-blocked` as applicable. Update public pins, package metadata, or release notes only in a separately approved release task using the actual producer version and checksums.

**Expected commands:**

```bash
dotnet test --filter "FullyQualifiedName~FamilyStoreReadSessionTests"
dotnet test --filter "FullyQualifiedName~StoreReaderRegistrationRunnerTests"
dotnet build Miller.slnx -c Release
dotnet test
scripts/test.sh scale
```

Use the real producer command and source-build command from the producer repository's current plan; do not write a guessed release number, executable pin, or checksum into the finding.

**Acceptance:**

- [ ] Real producer evidence proves acquire-before-open, exact snapshot serving, manifest/entry/generation retention, safe disposal, renewal, release, and no retarget.
- [ ] Linux/Windows race and process-identity results are recorded, or the lane is explicitly blocked/unverified.
- [ ] No per-symbol/session-query producer spawning occurs; acquisition is once per session lifecycle.
- [ ] Fast, Release, and required Scale verification pass for the final diff.
- [ ] The finding distinguishes implementation completion from producer/platform evidence that remains unavailable.

## Completion Boundary

The integration is complete when all enabled family-store opens pass through `FamilyStoreReadSession`, acquire before opening, serve only the admitted snapshot, dispose in the required order, and retain protection on renewal/release uncertainty. Legacy mode remains zero-pin and explicit. Producer source/build or platform evidence may block qualification, but it must be reported with the exact missing artifact or authority rather than replaced by stale fallback or fabricated pin data.
