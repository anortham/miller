# Workspace Open Background Prime Implementation Plan

> **Execution:** Use `razorback:subagent-driven-development`; each serial task is owned by one `luna_worker`, with the lead reviewing every diff and running the branch gate.

**Goal:** Make MCP `workspace open` durably register and return immediately while a hosted worker primes the index in the background.

**Architecture:** `WorkspaceTool.Open` performs validation, atomic registration, and non-blocking queue admission only. `WorkspaceOpenPrimeService` waits for bootstrap binding, lazily resolves the existing cross-workspace refresh path, and serially processes a deduplicated fixed-capacity queue. CLI open remains synchronous.

**Risk:** Medium. The change alters MCP timing and adds hosted lifecycle, but does not add a tool, argument, JSON field, schema, or scan implementation.

## Global constraints

- Do not resolve `WorkspaceRegistry`, `JulieExtractRunner`, or `CrossWorkspaceRefreshService` in the hosted-service constructor.
- Do not duplicate extraction, lock, governor, sidecar, corruption, or scan-failure policy.
- Queue admission is non-blocking and capacity 64; duplicate, full, and stopping outcomes are explicit.
- Existing registry state/error/revision are preserved atomically on an open conflict.
- A status read does not demote an actively pending durable `refreshing` row merely because its artifact is not published yet.
- Unexpected worker exceptions record `error`; transient lock/governor outcomes use existing refresh results and no new retry timer.
- Follow TDD: add a focused failing test, implement the minimum behavior, rerun only that focused scope.

## Verification strategy

- Worker scopes use `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~<TestClass>"`.
- Lead affected scope: `WorkspaceRegistryTests`, `WorkspaceFactsAssemblerTests`, `WorkspaceOpenPrimeServiceTests`, `WorkspaceToolTests`, `WorkspaceRenderTests`, and `HostStartupRegistrationTests`.
- Branch gate: `dotnet build Miller.slnx -c Release`; `scripts/test.sh`; `scripts/test.sh scale`; `git diff --check`.
- Live gate after rebuild/restart: one warm MCP connection, at least 20 clean temporary workspaces, nearest-rank p95 open response at most 1 second; report median/p95/max separately from background-ready duration; status/list must respond while a prime is running.
- Baseline evidence: the clean Julie worktree open produced no response after more than 180 seconds and was terminated.

## Task 1: Add atomic registration and pending-status semantics

**Ownership:**

- Modify `src/Miller.Indexing/WorkspaceRegistry.cs`.
- Modify the registered-row missing-index path in `src/Miller.Server/Tools/WorkspaceFactsAssembler.cs`.
- Modify focused registry/facts tests under `tests/Miller.Tests/Indexing` and `tests/Miller.Tests/Server`.

**Build:** Add one registry operation that inserts a new row as `Refreshing` but, on conflict, updates only identity/last-seen/lineage facts and preserves state, error, revision, and artifact facts. Return both the resulting row and whether it was created. Preserve a durable `Refreshing` row in MCP status when the root still exists and publication is pending.

**Acceptance:**

- Concurrent ready/error updates cannot be demoted or cleared by registration.
- A new row is `Refreshing` before queue admission.
- Status during a gated prime stays responsive and does not rewrite the row to `MissingIndex`.
- Existing missing-root and terminal missing-index behavior remains green.

## Task 2: Add the hosted background-prime queue

**Ownership:**

- Add `src/Miller.Server/Workspaces/WorkspaceOpenPrimeService.cs`.
- Modify `src/Miller.Server/Hosting/MillerServiceRegistration.cs`.
- Add `tests/Miller.Tests/Server/WorkspaceOpenPrimeServiceTests.cs`.
- Modify `tests/Miller.Tests/Server/HostStartupRegistrationTests.cs`.

**Build:** Implement a single-reader channel with capacity 64 and an active set spanning queued and running IDs. Expose non-blocking `TryEnqueue` outcomes `Queued`, `AlreadyQueued`, `Full`, and `Stopping`. Wait for `IndexBootstrapService.WaitUntilBoundAsync` inside `ExecuteAsync`, then lazily resolve `CrossWorkspaceRefreshService` and `WorkspaceRegistry`. Run `Refresh(id, force:false, bypassBackoff:true)` once per dequeued ID. On unexpected exception, mark the existing row `Error`; always release the active ID. Complete the writer during stop and honor the host cancellation budget.

**Acceptance:**

- Hosted-service construction succeeds before bootstrap binding.
- Queued and running duplicates produce one refresh.
- Full/stopping races release the active-set entry and return immediately.
- One injected exception records `Error` and does not stop the next item.
- A held synchronous fake refresh does not make `StopAsync` exceed its cancellation budget.

## Task 3: Refactor MCP open to register and enqueue only

**Ownership:**

- Modify `src/Miller.Server/Tools/WorkspaceTool.cs`.
- Modify `src/Miller.Server/Tools/WorkspaceRender.cs` documentation/compact wording only as needed.
- Modify `tests/Miller.Tests/Server/WorkspaceToolTests.cs` and `WorkspaceRenderTests.cs`.

**Build:** Replace MCP open's inline writer-lock, governor, failure-journal, and `_scanForOpen` path with atomic registration plus `WorkspaceOpenPrimeService.TryEnqueue`. Return an existing `WorkspaceActionResult` with `operation=open`, `scanned=false`, `swapped=false`, stable ID/root, current/pending status, and a note directing the caller to status/list. On `Full` or `Stopping`, mark only a newly created row `Error` and return a typed diagnostic. Leave `CliDispatch.WorkspaceOpen` unchanged.

**Acceptance:**

- A refresh fake held behind a gate cannot delay the MCP open response.
- MCP open performs no writer-lock or scan-governor admission and never calls an extractor delegate.
- Repeated opens queue once and preserve an existing ready/error row.
- Missing, sensitive, current-workspace, compact, JSON, full, stopping, and already-queued cases are truthful.
- CLI open and refresh/full output contracts remain green.

## Task 4: Integrated performance and behavior verification

**Ownership:** Lead only; no product edits unless a failed gate produces a bounded correction packet.

**Run:** Review the combined Miller diff with `impact`, run all affected test classes once, then the build/fast/scale branch gates. Rebuild/restart Miller, verify version/startup/host binding, measure 20 warm-connection open responses with nearest-rank p95, observe registry convergence, and time representative status/list/search/inspect/context/workspace calls. Verify the Julie stale-artifact fallback and accumulated-resolution fixture after the Julie tasks land.

**Acceptance:**

- All hard gates pass on the final tree with zero warnings.
- MCP open p95 is at most 1 second and no response includes scan duration.
- Background primes reach terminal registry states; status/list stay responsive during work.
- Startup and common tool calls return within their documented grace/budget.
