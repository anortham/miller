# Sidecar Convergence Reliability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Make unchanged workspace refreshes and quiet resident leaders repair or truthfully report stale search/content/vector sidecars without re-extraction, false completeness, or retry loops.

**Architecture:** `IndexerSidecarConverger` becomes the single typed convergence-result boundary for content/search/vector scheduling. The resident indexer retries stale derived sidecars with bounded backoff, while `VectorConvergeService` reconciles durable desired state from the current store sequence and completeness stamp at startup/idle; a one-shot CLI never generates embeddings. Existing workspace refresh/status/health surfaces report the outcome.

**Tech Stack:** .NET 10 hosted services, SQLite family-store sidecars, local semantic broker, MCP/CLI workspace schema 1.

**Architecture Quality:** Affected modules are store-sidecar convergence, the resident indexer/vector service, cross-workspace refresh, and workspace health rendering. The caller-facing interface stays `workspace refresh|status|health`; no new tool or persistent command queue is added. The durable source of truth is already the store sequence plus sidecar completeness stamps, so resident reconciliation is deeper and safer than adding another target database. Tests exercise refresh/health and the hosted-service boundary. Rejected shortcuts are full extraction, in-process signals as cross-process coordination, stamping current before cursors complete, embedding in a one-shot CLI, and unbounded polling. Architecture risk is medium-high because vector work is default-on and shared-broker lifecycle is load-bearing.

## Global Constraints

- Do not add an MCP tool or generate embeddings in a one-shot/cross-workspace CLI process.
- `MILLER_SEMANTIC=off` remains a permanent zero-work guarantee.
- Lexical-only output remains byte-identical with semantic retrieval off or unavailable.
- Search/content/vector stamps are published only after exact current-view/current-sequence completion.
- Stale search keeps the last-good/lagging read behavior; never relabel stale data as current.
- Derived-sidecar repair never forces source extraction.
- Retry work is bounded, coalesced, target-aware, and reset only by success or a newer target.
- Hosted-service constructors do not read bootstrap getters; reconciliation reads them lazily in `ExecuteAsync`.
- Apply TDD per task. Parallel workers use `parallel-lead-commit`; serialized tasks use `serial-worker-commit` after lead review.
- `docs/contracts/vectors-v1.md` is frozen and remains unchanged; refresh/health vocabulary belongs to `refresh-wait-v1.md` and `workspace-health-v1.md`.
- Cross-plan acceptance tasks run in this fixed order to avoid `docs/README.md` conflicts: CT Task 6, sidecar Task 5, generated-ignore Task 3. Each later task preserves earlier map entries.

---

## Verification Strategy

**Project source of truth:** `AGENTS.md`, `docs/contracts/vectors-v1.md`, ADR-0003, and the Build/Testing sections of `AGENTS.md`.

**Worker red/green scope:** One focused test-class filter per assigned behavior: `IndexerSidecarConvergerTests`, `StoreSidecarConvergerTests`, `CrossWorkspaceRefreshServiceTests`, `VectorConvergeServiceTests`, `WorkspaceFactsAssemblerTests`, or renderer tests.

**Worker ceiling:** Focused fast test classes only. No worker runs the semantic soak, full suite, real extractor, or cross-repo refresh campaign.

**Worker gate invariant:** Task 1 proves typed outcomes never hide sidecar failure; Task 2 proves quiet search/content repair is bounded; Task 3 proves a resident vector leader catches up a missing stamp without external file changes; Task 4 proves actions direct the user to work that can succeed.

**Lead affected-change scope:** After each coherent batch, run the union of focused filters and `dotnet build Miller.slnx -c Release`.

**Branch gate:** Run `scripts/test.sh`; run `scripts/test.sh scale` if a real extraction/refresh path changes; run `scripts/semantic-broker-soak.sh` because resident vector scheduling changes.

**Security scope:** none declared.

**Replay/metric evidence:** Hard gates are exact stamp identity, no semantic work when off, no duplicate retry per target/window, truthful refresh outcome, last-good search reads, and lexical byte identity. Catch-up latency, embeddings/sec, and retry count are report-only with explicit ceilings recorded by tests where deterministic.

**Escalation triggers:** Changes to shared-broker identity/lease, vector schema, family-store coordinator schema, or accelerator ownership require a separate architecture review; this plan does not authorize them.

**Assigned verification failure:** Workers stop and report when assigned verification fails unless the plan explicitly assigns the gate update.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, timestamp, target store sequence, final stamp identity, retry count, and semantic-off work count.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Typed convergence outcome | None - serial | `src/Miller.Server/Hosting/IndexerSidecarConverger.cs`; `src/Miller.Server/Hosting/IndexerService.cs`; `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`; focused converger/refresh tests | Yes | Defines the shared result consumed by later slices. |
| Task 2: Bounded search/content retry | Batch A | `src/Miller.Server/Hosting/IndexerService.cs`; `src/Miller.Indexing/SymbolSearchSidecar.cs`; focused indexer/search tests | No | None - safe parallel batch after Task 1. |
| Task 3: Resident vector desired-state reconciliation | Batch A | `src/Miller.Server/Hosting/VectorConvergeService.cs`; `tests/Miller.Tests/Server/VectorConvergeServiceTests.cs`; semantic-off tests | No | None - safe parallel batch after Task 1. |
| Task 4: Actionable refresh and health rendering | None - serial | `src/Miller.Server/Tools/WorkspaceHealthFacts.cs`; `src/Miller.Server/Tools/WorkspaceFactsAssembler.cs`; `src/Miller.Server/Tools/WorkspaceRender.cs`; `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`; `docs/contracts/workspace-health-v1.md`; `docs/contracts/refresh-wait-v1.md`; related tests | Yes | Consumes final outcome/state vocabulary from Tasks 1-3. |
| Task 5: Sidecar dogfood acceptance | None - serial | `docs/findings/2026-08-22-sidecar-convergence-reliability-verification.md`; `docs/README.md`; plan status checkboxes | Yes | Runs after implementation and branch gates. |

### Task 1: Typed convergence outcome

**Files:**
- Modify: `src/Miller.Server/Hosting/IndexerSidecarConverger.cs`
- Modify: `src/Miller.Server/Hosting/IndexerService.cs`
- Modify: `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`
- Test: `tests/Miller.Tests/Server/IndexerSidecarConvergerTests.cs`
- Test: `tests/Miller.Tests/Server/StoreSidecarConvergerTests.cs`
- Test: `tests/Miller.Tests/Server/CrossWorkspaceRefreshServiceTests.cs`

**Interfaces:**
- Consumes: current store sequence, per-sidecar ensure operations, vector target signal, and family-store write lease.
- Produces: a bounded `StoreSidecarConvergenceResult` from `ConvergeStore` with content/search outcomes and vector scheduling outcome: `disabled|current|repaired|queued|leader_required|failed` plus target sequence and bounded reason.

**Contract inputs:** Convergence exceptions remain isolated per derived sidecar. A failure is returned as typed evidence after logging rather than swallowed into a successful `Unchanged` refresh. `queued` requires a live resident leader/drain loop in the current process; one-shot and foreign refreshes return `leader_required` even if they touched a process-local signal.

**File ownership:** `src/Miller.Server/Hosting/IndexerSidecarConverger.cs`; `src/Miller.Server/Hosting/IndexerService.cs`; `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`; focused converger/refresh tests

**Serialization required:** Yes.

**Dependency reason:** Defines the shared result consumed by later slices.

**What to build:** Change the existing `ConvergeStore` boundary from `void` to a local typed result. Propagate it through resident and cross-workspace refresh callers without introducing a global coordinator or schema. Carry explicit process-role/drain availability so a one-shot CLI cannot report vector work as queued.

**Approach:** Keep the result record beside `IndexerSidecarConverger` so it does not become a shallow module. Record exact target sequence, did-work, pending/leader requirement, and bounded failure text per sidecar. Preserve per-sidecar failure isolation and phase telemetry.

**Acceptance criteria:**
- [x] An unchanged refresh distinguishes current, repaired, queued, leader-required, and failed sidecars.
- [x] Search/content failure cannot render as a clean `Unchanged` action.
- [x] Vector scheduling is not reported as completion before the completeness stamp exists.
- [x] A one-shot or foreign refresh with no live drain reports `leader_required`, never `queued`.
- [x] Focused converger/refresh tests pass; the lead will record the serialized worker commit after review.

### Task 2: Bounded search/content retry

**Files:**
- Modify: `src/Miller.Server/Hosting/IndexerService.cs`
- Modify: `src/Miller.Indexing/SymbolSearchSidecar.cs`
- Test: `tests/Miller.Tests/Server/IndexerServiceScanTests.cs`
- Test: `tests/Miller.Tests/Indexing/SymbolSearchSidecarTests.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs`

**Interfaces:**
- Consumes: Task 1 typed result, `SymbolSearchSidecar.InspectStore`, the current store sequence, and the indexer's existing idle drain tick.
- Produces: target-aware retry state with 5s, 15s, and 30s intervals capped at 30s, reset on success or target change.

**Contract inputs:** Retry only when the resident process is leader, the source index is current, and a derived sidecar is stale/failed. Never re-extract and never remove the last-good readable sidecar.

**File ownership:** `src/Miller.Server/Hosting/IndexerService.cs`; `src/Miller.Indexing/SymbolSearchSidecar.cs`; focused indexer/search tests

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch after Task 1.

**What to build:** Extend the idle no-scan path to inspect current store sidecars and retry only missing/stale content/search work. Publish Task 1 outcomes on every explicit refresh and keep automatic retry silent except phase/log telemetry.

**Approach:** Store retry state in the singleton resident `IndexerService`, not `WorkspaceIndexProvider` or another transient. Coalesce one in-flight attempt and use the current view/sequence as the retry key.

**Acceptance criteria:**
- [x] A quiet unchanged workspace repairs a stale search/content sidecar without a file nudge.
- [x] Repeated failure follows the bounded schedule and never attempts more than once per due interval.
- [x] A newer store target resets retry immediately; success clears it.
- [x] Last-good/lagging reads and semantic-off behavior remain unchanged.
- [x] Focused indexer/search tests pass; the lead will record the parallel batch commit after review.

### Task 3: Resident vector desired-state reconciliation

**Files:**
- Modify: `src/Miller.Server/Hosting/VectorConvergeService.cs`
- Test: `tests/Miller.Tests/Server/VectorConvergeServiceTests.cs`
- Test: `tests/Miller.Tests/Indexing/SemanticOffGuaranteeTests.cs`
- Test: `tests/Miller.Tests/Indexing/StoreSidecarStampTests.cs`

**Interfaces:**
- Consumes: bootstrap-bound current family-store snapshot, store sequence, vector completeness stamp/cursors, existing `VectorConvergeSignal`, and held-retry machinery.
- Produces: startup and quiet-idle reconciliation that stamps/wakes the exact current target when completeness is absent or stale.

**Contract inputs:** Use store sequence plus completeness stamp as durable desired state. Do not add a persistent request DB, change broker ownership, or let a foreign one-shot process generate embeddings.

**File ownership:** `src/Miller.Server/Hosting/VectorConvergeService.cs`; `tests/Miller.Tests/Server/VectorConvergeServiceTests.cs`; semantic-off/stamp tests

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch after Task 1.

**What to build:** After bootstrap binds, inspect the current store before the first wait and wake convergence when the exact vector stamp is missing. Reuse the existing held-retry mechanism for quiet incomplete cursors and issue a post-shadow-promotion wake so the chunk lane cannot remain held forever.

**Approach:** Read bootstrap getters only inside `ExecuteAsync`. Probe only while incomplete; stop embed, cursor, and retry work immediately when exact completeness is published. Preserve the existing bounded `WarmBrokerAsync` startup unless semantic mode is off so status does not regress to `not_started` during long indexing.

**Acceptance criteria:**
- [x] A resident leader repairs a missing completeness stamp on a static workspace without `workspace refresh` or a file nudge.
- [x] Shadow rebuild completion wakes the held chunk lane and reaches one exact completeness stamp.
- [x] Restart with an already-current stamp performs no re-embedding or cursor/retry work while retaining bounded broker warmup.
- [x] `MILLER_SEMANTIC=off` opens no vector state, broker, or retry work.
- [x] Focused vector/off tests pass; the lead will record the parallel batch commit after review.

### Task 4: Actionable refresh and health rendering

**Files:**
- Modify: `src/Miller.Server/Tools/WorkspaceHealthFacts.cs`
- Modify: `src/Miller.Server/Tools/WorkspaceFactsAssembler.cs`
- Modify: `src/Miller.Server/Tools/WorkspaceRender.cs`
- Modify: `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`
- Modify: `docs/contracts/workspace-health-v1.md`
- Modify: `docs/contracts/refresh-wait-v1.md`
- Test: `tests/Miller.Tests/Server/WorkspaceFactsAssemblerTests.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceVectorFactsRenderTests.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceRenderTests.cs`
- Test: `tests/Miller.Tests/Server/CrossWorkspaceRefreshServiceTests.cs`

**Interfaces:**
- Consumes: Task 1 outcomes and final search/content/vector facts from Tasks 2-3.
- Produces: refresh JSON/compact sidecar outcomes and prioritized health actions that distinguish repairable-here, queued, resident-leader-required, and failed states.

**Contract inputs:** Preserve existing summary bounds and schema version. Compact output must not hide the highest-severity actionable sidecar repair behind generic warnings.

**File ownership:** `src/Miller.Server/Tools/WorkspaceHealthFacts.cs`; `src/Miller.Server/Tools/WorkspaceFactsAssembler.cs`; `src/Miller.Server/Tools/WorkspaceRender.cs`; `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`; `docs/contracts/workspace-health-v1.md`; `docs/contracts/refresh-wait-v1.md`; related tests/contracts

**Serialization required:** Yes.

**Dependency reason:** Consumes final outcome/state vocabulary from Tasks 1-3.

**What to build:** Add a bounded `sidecars` object to refresh results and rank actions by whether the current process can perform them. When refresh has queued resident vector work, say so; when no resident leader can build, say to open/keep one; when search repair failed, name the failure and retry posture.

**Approach:** Keep warnings truthful until exact stamps are current. Ensure the first compact action is the one that can change the state, not a repeated generic `workspace refresh` that just failed.

**Acceptance criteria:**
- [ ] The reproduced missing-vector-stamp state no longer loops on an ineffective generic refresh action.
- [ ] Stale search refresh reports repaired, queued, or failed rather than silent `Unchanged`.
- [ ] JSON/compact output remains bounded and old optional-field fixtures remain byte-identical.
- [ ] Focused facts/render/refresh tests pass and the serialized worker commit is recorded.

### Task 5: Sidecar dogfood acceptance

**Files:**
- Create: `docs/findings/2026-08-22-sidecar-convergence-reliability-verification.md`
- Modify: `docs/README.md`
- Modify: `docs/plans/2026-08-22-sidecar-convergence-reliability-plan.md`

**Interfaces:**
- Consumes: Tasks 1-4 on a built resident server and controlled stale/missing sidecars.
- Produces: exact current-stamp, refresh-outcome, semantic-off, and retry evidence.

**Contract inputs:** Use isolated `MILLER_HOME`/workspace fixtures. Do not corrupt or delete the user's live sidecars to construct the replay.

**File ownership:** `docs/findings/2026-08-22-sidecar-convergence-reliability-verification.md`; `docs/README.md`; plan status checkboxes

**Serialization required:** Yes.

**Dependency reason:** Runs after implementation and branch gates.

**What to build:** Reproduce missing vector completeness, stale search, unchanged refresh, quiet leader catch-up, restart-current, and semantic-off in isolated workspaces. Record exact target/stamp identities and bounded retry counts.

**Approach:** Run fast/Scale/semantic-soak gates on one exact tree, then exercise CLI and MCP refresh/health renderings. Treat lexical byte identity and zero semantic-off work as hard gates.

**Acceptance criteria:**
- [ ] Missing vector and stale search states converge without source changes or full extraction.
- [ ] Refresh and health state/action output matches the work actually scheduled or completed.
- [ ] Semantic-off zero-work and lexical byte identity are proven.
- [ ] No retry loop, broker leak, or sidecar lease remains after shutdown.
- [ ] Verification evidence is mapped in `docs/README.md`, all plan checkboxes are updated, and the serialized worker commit is recorded.
