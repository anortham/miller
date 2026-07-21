# Task 5 report: GC scheduler + live-reader registry

**Status:** COMPLETE. Retained vector generations are now garbage-collected. The pure GC plan in
`VectorGenerationManager` has real callers for the first time; an in-process live-reader registry populates
`TagsWithLiveReaders`.

## What shipped

- **`VectorLiveReaderRegistry`** (new, `src/Miller.Indexing/Semantic/`): process-wide, thread-safe refcount over
  generation tags. `Register(tag) : IDisposable` (idempotent dispose), `LiveTags` snapshot backed by
  `ConcurrentDictionary<string,int>`. `Shared` static instance pairs the reader arm with the leader's GC — the
  same pattern as `VectorConvergeSignal.Shared`.
- **`IVectorGenerationGc` + `VectorGenerationGc`** (new seam in `VectorConvergeService.cs`): enumerates
  `manager.Retained()`, calls the pure `PlanGarbageCollection`, and deletes each eligible generation on its own —
  one info log per deletion, and a held-handle failure (`IOException`/`UnauthorizedAccessException`) logged and
  left for the next wake instead of aborting the pass. Decision logic stays pure in the manager; this class is
  the "when/who" glue only.
- **`VectorGenerationManager.DeleteRetained(RetainedGeneration)`** (new public seam): single-generation trio
  delete, so the scheduler can drive deletions one at a time with per-item logging + resilience. This is the only
  manager change — the plan logic was untouched.
- **`VectorConvergeService` wiring**: `_openGc` factory + lazy `_gc` field (bound in `DrainOnceAsync`, like
  `_session`) + `_readerRegistry`. `CollectGarbage(port)` runs at the tail of every `DrainAsync` (both the
  normal branch and the post-promote reopened branch), reading `ActiveIsReady` from the port's `build_state` and
  live tags from the registry. Wrapped so a GC fault can never crash the drain.
- **`SemanticSearchArm` reader registration**: registers the served generation tag for the port's open lifetime
  and disposes on close. `IVectorSearchPort.Tag` added as a **default interface member** (`=> ""`) so the many
  fake ports in non-owned test files (`HybridSearchTests`, `CliDispatchTests`, `SearchDeterminismTests`,
  `SemanticSearchArmTests`) need no change; `VectorStoreSearchPort.Tag` returns
  `GenerationTag(store.Identity)`. Registry injected via an optional ctor arg defaulting to `Shared`, so all
  existing construction sites are unchanged.

## Plan-mismatch note (reader lifetime)

The brief flagged this to report rather than invent: **`SemanticSearchArm` opens and disposes the port inside a
single `QueryAsync` (try/finally) — there is no long-lived reader handle.** This is deliberate (the arm's own
docs: a connection held across queries would pin a generation's inode across a promote). So the in-process
protection window equals one query's duration; cross-query protection is the soak window's job. This still meets
the acceptance bar — a live in-process reader blocks deletion, disposal unblocks it — proven directly at the
registry+GC level (`GcScheduler_ALiveReaderBlocksDeletion_AndDisposalLetsTheNextPassCollectIt`). No lifetime was
invented. Cross-process readers stay soak-window-only, as the P2 B6 posture requires.

## Miller-first orientation (evidence)

- `trace PlanGarbageCollection` / `trace CollectGarbage`: only non-test references were inside
  `VectorGenerationManager` itself — confirmed the plan was caller-less before this task.
- Read of `VectorGenerationManager.cs` (records `RetainedGeneration`/`VectorGcDecision`/`VectorGcPlan`,
  `VectorGcInputs`, `IVectorGenerationFiles`, `DefaultRetentionCap=2`, `DefaultSoakWindow=24h`,
  `PlanGarbageCollection`, `Classify`), `VectorConvergeService.cs` (Task 1/2 `DrainState`+`DiskGate`+`ResolvePause`
  shape confirmed from the current file), `SemanticSearchArm.cs` (per-query open/dispose), and `VectorSidecar.cs`
  (read-only — owned by another worker): confirmed `facts.ServingTag` for a retained-serving reader equals the
  retained file tag, which equals `GenerationTag(store.Identity)`, so the registry key matches the GC key for
  both active and retained ports.

## Tests (TDD, red→green)

- Registry: register/dispose, refcount, idempotent double-dispose, `LiveTags` snapshot isolation, empty-tag
  reject, concurrent register/release smoke (`VectorLiveReaderRegistryTests`).
- GC execution via the fake files seam + fake clock (`VectorGenerationManagerTests`): deletes past-soak/no-reader;
  keeps within-soak; keeps when active-not-ready; live reader blocks then disposal collects; a throwing delete is
  swallowed and retried next pass.
- Scheduler wiring (`VectorConvergeServiceTests`): GC runs on a leader wake with `ActiveIsReady` from the port
  and tags from the registry; `ActiveIsReady=false` when `build_state != ready`; GC never runs without a converge
  wake (a reader instance's signal is never stamped → proves GC is leader-only).

**Gate invariant per test:** registry tests assert refcount/snapshot correctness independent of GC; GC-execution
tests assert the three keep-outcomes and the delete-outcome are honored on real file mutations; wiring tests
assert GC is invoked exactly when (and only when) a drain runs, with inputs derived from the port + registry.

## Verification

- worker-red-green: `dotnet test --filter VectorGenerationManagerTests|VectorLiveReaderRegistry|VectorConvergeServiceTests`
  → **78 passed, 0 failed**.
- worker-ceiling: `scripts/test.sh` (Release, warnings-as-errors) → **4223 passed, 2 skipped, 0 failed, 28s**
  (under the 30s ceiling; the known Task 8 wall-flake did not trip this run).

## Concerns

- None blocking. The per-query registration window (above) is by design, not a gap.
- File footprint is exactly the allowed set; the concurrently-edited files (`VectorSidecar.cs`,
  `SemanticPrepareCli.cs`, and their tests) were read-only references, untouched.
- No DI registration was needed: arm and service both default to `VectorLiveReaderRegistry.Shared`.
