# Workspace Open Background Prime Design

**Status:** Approved 2026-08-16 as part of the performance-recovery work.

## Problem

The MCP `workspace(operation="open", path=...)` call is synchronous. After validation and writer/governor admission, `WorkspaceTool.Open` calls `_scanForOpen(...)` inline and does not render a response until extraction, publication, and registry updates finish.

Opening the clean Julie worktree on 2026-08-16 blocked the MCP call for more than three minutes before the caller terminated it. The current five-second `DefaultOpenPrimeGovernorWait` limits only scan-governor admission; it does not bound the scan that follows.

This violates the server lifecycle rule already applied to primary-workspace bootstrap: long indexing belongs in background ownership, while agent tool calls return bounded ready/refreshing/error state.

## Goals

- Return from MCP `workspace open` immediately after validation, registration, and durable queue admission.
- Continue priming the opened workspace in the server background.
- Surface `refreshing`, `ready`, `error`, or `missing` through the existing workspace registry and status/list views.
- Preserve single-writer, scan-governor, scan-failure backoff, root-safety, store-mode, sidecar convergence, and atomic publication behavior.
- Keep CLI `miller workspace open` synchronous because its one-shot process cannot own background work after exit.

## Non-goals

- Adding an MCP tool, argument, environment variable, daemon, or machine service.
- Switching the current Miller session to the opened workspace.
- Running more than one prime for the same workspace in one server process.
- Hiding scan failures or claiming an index is ready before the existing refresh service proves it.
- Changing cross-workspace read freshness semantics.

## Decision

Add one hosted singleton, `WorkspaceOpenPrimeService`, with a fixed-capacity in-process queue keyed by stable `workspace_id`.

`WorkspaceTool.Open` keeps the synchronous work that is required to accept a request safely:

1. Validate the path and format.
2. Canonicalize the root and reject sensitive/current roots.
3. Compute stable workspace and display IDs.
4. Atomically register the row as `refreshing` only when it is new; preserve an existing row's state, error, and revision on conflict.
5. Enqueue the stable workspace ID once.
6. Render an existing `WorkspaceActionResult` with operation `open`, `scanned=false`, the current revision if known, and status `refreshing` or the preserved row state.

It must not acquire the target writer lock, wait on the scan governor, reap target staging, locate/build an extractor process, or read the target artifact in the MCP request.

`WorkspaceOpenPrimeService` owns the long work. Its constructor receives only `IndexBootstrapService`, the root `IServiceProvider`, and logging; it does not resolve bootstrap-backed factories. After `WaitUntilBoundAsync` completes inside `ExecuteAsync`, it lazily resolves `CrossWorkspaceRefreshService` and `WorkspaceRegistry` and then:

1. Read one deduplicated workspace ID from its channel.
2. Call the existing `CrossWorkspaceRefreshService.Refresh(workspaceId, force:false, bypassBackoff:true)` outside the MCP request.
3. Let that service retain ownership of root checks, writer locking, scan-governor admission, store/legacy mode, persisted failure policy, extraction, sidecars, and registry ready/error/missing updates.
4. Log the terminal result and remove the ID from the in-process active set so a later explicit open can retry a failed or deferred attempt.

The service processes one queued prime at a time. Cross-process duplication remains prevented by the existing target writer lock and machine scan governor. An abrupt Miller exit may leave a row `refreshing`; the next explicit open, refresh-first read, or Miller bootstrap re-evaluates the durable workspace state exactly as today.

## Queue and Lifecycle Contract

- `TryEnqueue(string workspaceId)` is non-blocking and returns `Queued`, `AlreadyQueued`, or `Stopping`.
- The active set covers both queued and running work, so repeated opens cannot enqueue duplicate scans.
- The channel is process-local with a fixed capacity of 64 unique workspace IDs. `TryEnqueue` uses non-blocking `TryWrite`; a full queue returns `Full`, removes the just-added active-set entry, and leaves the row retryable by a later explicit open.
- `ExecuteAsync` is the only place that calls the synchronous refresh operation.
- The hosted-service constructor must not resolve `WorkspaceRegistry`, `JulieExtractRunner`, `CrossWorkspaceRefreshService`, or read `IndexBootstrapService.Holder`, `.Resolver`, `.Workspace`, or `.Ledger`; each is bootstrap-backed in the production graph.
- Shutdown sets the stopping flag, completes the channel, and delegates to `BackgroundService.StopAsync`. The host-provided cancellation token bounds the wait for an in-flight synchronous refresh; the extractor retains current parent-PID supervision. The queue does not invent a second cancellation or retry timer.
- A nonterminal/busy refresh result is not hot-looped. The ID leaves the active set and the durable registry/backoff state remains authoritative; a later explicit request or refresh-first read retries through existing policy.

## Rendering and Contracts

The MCP response uses `WorkspaceRender.Action`/`WorkspaceActionResult`, which already renders CLI `open` and carries workspace ID, root, status, revision, durations, and an honesty note. Extend its API documentation from `refresh/full` to `open/refresh/full`; do not add a JSON field.

For a newly registered path, compact output says the workspace was registered and queued for background indexing and tells the caller to use `workspace status` or `workspace list`. JSON carries:

- `operation: "open"`
- `scanned: false`
- `swapped: false`
- `workspace_id` and `root`
- `status: "refreshing"`
- the existing note field

An already queued path returns the same truthful state without another work item. A full or stopping queue returns an error diagnostic and marks a newly created row `error` rather than stranding a false `refreshing` claim.

The existing `WorkspaceOpenResult` renderer remains for compatibility and tests but is no longer used by MCP `WorkspaceTool.Open`. The one-shot CLI keeps its synchronous `WorkspaceActionResult` path unchanged.

## Error and Recovery Behavior

- Missing, sensitive, and current-workspace paths retain their immediate typed refusals and perform no registry/queue mutation.
- Queue refusal because the queue is full or stopping returns immediately with a typed error; a newly inserted row becomes `error`.
- Background missing-root, safety, extractor, corruption, backoff, lock-busy, and publication outcomes are rendered later by existing registry status/health/list behavior.
- The background service catches and logs unexpected exceptions, atomically marks the row `error` when it still exists, and continues so one failed prime cannot terminate the host or stop later queue items.
- No task observes an unhandled fire-and-forget exception.
- Reopening an error/deferred workspace after the first work item leaves the active set may enqueue one new explicit attempt, still governed by persisted scan-failure policy.
- A transient governor/lock refusal is not retried by a second timer. The registry records the existing refresh result, and a later explicit open or refresh-first read retries through the established policy.
- MCP status does not rewrite a `refreshing` row to `missing` merely because the artifact has not appeared yet. The row remains visibly pending until the worker records a terminal state or a later explicit lifecycle operation re-evaluates it.

## Architecture Quality

**Affected modules:** `WorkspaceTool` becomes registration/queue admission only for MCP open; new `WorkspaceOpenPrimeService` owns background scheduling; `CrossWorkspaceRefreshService` remains the single refresh implementation; `MillerServiceRegistration` owns hosted lifecycle; `WorkspaceRender` renders the existing action contract.

**Caller-facing interface:** The existing `workspace` MCP tool and `operation="open"` argument remain unchanged. The response becomes immediate and honestly reports queued/refreshing rather than completed prime counts.

**Depth/locality check:** Long-running ownership is removed from the tool method and placed in one hosted service. Scan policy is not duplicated; the worker calls the existing refresh service.

**Test surface:** Tests call `WorkspaceTool.Workspace(operation:"open")`, hold the background refresh behind a gate, and prove the tool responds before that gate is released. Service tests then release the gate and assert registry convergence.

**Seams/adapters:** One earned seam, non-blocking `TryEnqueue`, separates request latency from background completion. It carries only workspace IDs and does not expose scan internals.

**Rejected shortcuts:** `Task.Run` from the tool (unowned exceptions/lifecycle and no deduplication); registration-only open (does not continue priming); changing timeouts (still makes scan latency part of the call); duplicating scan logic in the new service; making CLI open asynchronous (the process exits).

**Architecture risk:** Medium. The behavior is local but introduces hosted queue lifecycle, changes MCP open timing/output semantics, and must preserve the host-construction invariant.

## Verification

### Deterministic tests

- A fake background refresh blocked on a test gate does not block `WorkspaceTool.Workspace(open)`.
- The response is produced before the scan gate is released and the registry row is `refreshing`.
- Repeated opens while queued/running produce one background refresh; queue-full and stopping races remove the active-set entry.
- Releasing the gate moves the registry row to `ready`; failure moves it to `error`; missing root moves it to `missing`.
- A full or stopping queue returns a typed error and does not strand a new row as `refreshing`.
- Atomic registration cannot demote or clear a concurrently updated ready/error row.
- Status/list remain responsive while the refresh gate is held and status preserves the pending `refreshing` row.
- An injected worker exception records `error` and later queue items still run.
- Host stop honors its cancellation budget while a fake synchronous refresh is held.
- Missing, sensitive, live-workspace, already-served, and JSON cases remain bounded and truthful.
- Resolving all hosted services before bootstrap does not touch bootstrap getters.
- Existing refresh/full, CLI open, registry, status/list, and `WorkspaceRender.Action` contracts remain green.

### Performance evidence

Baseline workload: one MCP `workspace open` for a clean Julie worktree with no target index. Before: no response after more than 180 seconds; caller terminated the request.

After implementation:

- Direct tool test proves response completion without releasing the scan gate; this is the deterministic regression guard.
- On one warm MCP connection, open at least 20 distinct clean temporary workspaces and report median, nearest-rank p95, and maximum time-to-response; p95 must be at most 1 second.
- Independently wait for each registry row to reach `ready` and report background scan duration. Scan duration is report-only and must not be folded into tool latency.
- Verify a following status/list call remains responsive while the background scan runs.

## Acceptance Criteria

- [ ] MCP `workspace open` performs no synchronous extraction or scan-governor wait.
- [ ] A newly accepted workspace is durably registered before the response and queued exactly once.
- [ ] Background work uses `CrossWorkspaceRefreshService`; scan/locking/backoff logic is not duplicated.
- [ ] The response truthfully reports queued/refreshing state using existing action JSON fields.
- [ ] Registry state converges to ready/error/missing after background completion.
- [ ] CLI `workspace open` remains synchronous and compatible.
- [ ] Host construction remains safe before bootstrap binding.
- [ ] No new MCP tool, argument, environment variable, JSON field, or schema is added.
- [ ] Direct gated tests prove non-blocking behavior; live warm p95 is at most 1 second versus the >180-second baseline.
