# Background Bootstrap + Fast Not-Ready Responses Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when
> subagent delegation is available. Fall back to razorback:executing-plans for single-task,
> tightly-sequential, or no-delegation runs.

**Goal:** Move Miller's initial index scan off the MCP tool-call path onto a background task and
give tool calls fast, actionable not-ready/failed responses, per
[`2026-07-06-background-bootstrap-design.md`](2026-07-06-background-bootstrap-design.md) (rev 2 —
the authoritative spec; implementers MUST read the cited sections before coding).

**Architecture:** A bootstrap state machine (`Idle/Running/Bound/Failed`) in
`IndexBootstrapService` with atomic `BoundWorkspace` publication under `_gate`; `BootstrapForRoot`
returns a `BindOutcome` and dispatches `RunBootstrap` to `Task.Run`; the binding filter waits a
run-generation-keyed grace gate and returns `CallToolResult{IsError=true}` status text on
timeout/failure, rendering the `workspace` tool's snapshot itself while unbound.

**Tech Stack:** .NET 10, existing MCP SDK filter chain, xUnit fast suite.

**Architecture Quality:** Approved shape per design rev 2: state machine + publication in
`IndexBootstrapService`; outcome/dirty contract in `WorkspaceBindingService`; grace/not-ready/
snapshot-render in `WorkspaceBindingCallToolFilter`; no new services, no DI changes to
`WorkspaceTool` construction. Architecture risk: medium (concurrency + filter contract). If code
reality contradicts this shape, report a plan mismatch — do not redesign locally.

## Global Constraints

- Design rev 2 is the spec. Exact contracts: `BindOutcome = Started | AlreadyBound |
  JoinedRunning | RebindDeferred`; snapshot
  `BootstrapSnapshot { Phase, CanonicalRoot?, StartedAtUtc?, FailureMessage?, RunGeneration }`;
  grace env knob `MILLER_BOOTSTRAP_GRACE_SECONDS` (default 5, `0` = immediate fail-fast).
- Exact response texts (agent-facing contract, keep verbatim): not-ready →
  `"Miller is indexing this workspace for the first time: <root> (started <N>s ago). Tool calls
  will work once indexing completes — retry shortly, or run 'workspace status' for progress."`;
  failed → `"bootstrap failed: <stored message>; retry started — call again shortly."`
- The `d225dc5` sensitive-root guard stays synchronous at bind time for every source; its
  regression tests must pass unchanged.
- `TestBootstrapInterceptor` stays synchronous and short-circuits background dispatch (existing
  tests pass unmodified).
- Every task ends with `scripts/test.sh` green (fast suite, warnings-as-errors build). No new
  test may spawn julie-extract (all simulation via seams; anything else would need
  `[Trait("Category","Scale")]` — this plan should not need it).
- CLAUDE.md is edited first, then `scripts/sync-agents.sh`, then `cmp -s CLAUDE.md AGENTS.md`
  (repo rule) — Task 4 only.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` "Testing" section; `scripts/test.sh`.

**Worker red/green scope:** `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter
"FullyQualifiedName~<TestClassOrMethod>"` for the task's named tests.

**Worker ceiling:** `scripts/test.sh` (fast suite). Workers never run the scale suite.

**Worker gate invariant:** each task's acceptance criteria name the behaviors its tests prove;
red-first is required where a behavior changes.

**Lead affected-change scope:** `scripts/test.sh` after each task's commit.

**Branch gate:** `scripts/test.sh all` — the scale suite is REQUIRED here because this plan
touches the indexing/bootstrap path (CLAUDE.md rule), plus `dotnet build Miller.slnx -c Release`
(0 warnings) so running servers pick up the fix on next launch.

**Replay/metric evidence:** none — behavior gates only.

**Escalation triggers:** any change touching `JulieExtractRunner`, `FullRebuildPromotion`, or
leadership files escalates to the scale suite immediately (not expected in this plan).

**Assigned verification failure:** Workers stop and report when assigned verification fails.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, timestamp
per task. Reuse same-HEAD passing evidence.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: State machine + atomic publication + background dispatch | None - serial | Modify: `src/Miller.Server/Hosting/IndexBootstrapService.cs`, `src/Miller.Server/Hosting/WorkspaceBindingService.cs` (minimal compile adaptation only); Test: `tests/Miller.Tests/Server/WorkspaceBindingServiceTests.cs` | Yes | Foundation — every later task consumes its types; same files as Task 2. |
| Task 2: BindOutcome / rootsDirty contract | None - serial | Modify: `src/Miller.Server/Hosting/WorkspaceBindingService.cs`; Test: `tests/Miller.Tests/Server/WorkspaceBindingServiceTests.cs` | Yes | Consumes Task 1's `BindOutcome`; same test file as Task 1. |
| Task 3: Filter grace/not-ready/failed/snapshot render | None - serial | Modify: `src/Miller.Server/Telemetry/WorkspaceBindingCallToolFilter.cs`, `src/Miller.Server/Hosting/WorkspaceBindingService.cs` (snapshot passthrough on `IWorkspaceBindingService`); Test: `tests/Miller.Tests/Server/WorkspaceBindingCallToolFilterTests.cs` | Yes | Consumes Task 1's snapshot + run-generation gate and Task 2's outcome semantics. |
| Task 4: Status surfaces, docs, branch gate | None - serial | Modify: `src/Miller.Server/Tools/WorkspaceTool.cs`, `src/Miller.Server/Tools/WorkspaceRender.cs`, `CLAUDE.md` (+ regenerated `AGENTS.md`); Test: `tests/Miller.Tests/Server/WorkspaceToolTests.cs` | Yes | Consumes the full stack; runs the branch gate. |

Commit mode: `serial-worker-commit` for all tasks.

---

### Task 1: State machine, atomic publication, background dispatch (both paths)

**Files:**
- Modify: `src/Miller.Server/Hosting/IndexBootstrapService.cs` (`StartAsync` :100, `BootstrapForRoot` :127, `SignalBound` :158, `RunBootstrap` :174, publication block :328-340, `IsBound`/`WaitUntilBoundAsync` :64-86)
- Modify: `src/Miller.Server/Hosting/WorkspaceBindingService.cs` — ONLY the minimal adaptation to the new `BootstrapForRoot` return type (ignore the outcome for now; Task 2 owns the contract)
- Test: `tests/Miller.Tests/Server/WorkspaceBindingServiceTests.cs`

**Interfaces:**
- Consumes: design rev 2 §"Bootstrap state machine" and §"`BootstrapForRoot` splits".
- Produces (Tasks 2–4 rely on):
  - `enum BindOutcome { Started, AlreadyBound, JoinedRunning, RebindDeferred }`;
    `BootstrapForRoot(...) -> BindOutcome`.
  - `BootstrapSnapshot Snapshot { get; }` with
    `{ Phase (Idle|Running|Bound|Failed), CanonicalRoot?, StartedAtUtc?, FailureMessage?,
    LastFailureMessage?, RunGeneration }`.
  - `Task WaitForRunAsync(int runGeneration, CancellationToken ct)` — completes when THAT run
    binds or fails (both outcomes complete the wait; caller re-reads the snapshot).
  - `internal Action<string>? TestRunBootstrapOverride` — replaces `RunBootstrap` inside the
    background task (throw = Failed path; block on a gate = slow-scan simulation).
  - Semantics: guard + idempotence + interceptor synchronous; `RunBootstrap` + publication on
    `Task.Run`; `BoundWorkspace` built in locals and published in ONE step under `_gate`
    (registry ready-mark + `ledger.RebindWorkspace` at the publish point, error-mark on failure);
    `SignalBound` only after publish; `Failed → Running` allowed on same-root retry.

**Contract inputs:** existing `_gate`, `_bindingReady` gate + `BindingGeneration` counter,
`TestBootstrapInterceptor` (must stay synchronous), `d225dc5` guard tests
(`BootstrapForRoot_RejectsSensitiveRoot*`), `HostStartupRegistrationTests` failed-rebind-keeps-
previous-workspace behavior (:88).

**File ownership:** Modify: `src/Miller.Server/Hosting/IndexBootstrapService.cs`, `src/Miller.Server/Hosting/WorkspaceBindingService.cs` (minimal compile adaptation only); Test: `tests/Miller.Tests/Server/WorkspaceBindingServiceTests.cs`

**Serialization required:** Yes

**Dependency reason:** Foundation — every later task consumes its types; same files as Task 2.

**What to build:** The state machine and the thread-safety core: split `BootstrapForRoot`,
background both the eager (`StartAsync`) and deferred paths, publish atomically, key the wait
gate by run generation, keep failed rebinds serving the previous workspace.

**Approach:** Restructure `RunBootstrap`'s tail so holder/resolver/workspace/ledger land in a
`BoundWorkspace` record; move the registry `MarkReady`/scan-stamp and `ledger.RebindWorkspace`
calls to the publish step (verify current call order at :299-340 while editing). TDD via the
seams: slow-run test (override blocks) proves `IsBound` stays false and the snapshot reads
`Running`; failing-run test proves `Failed` + `FailureMessage` + gate NOT signaled + registry
error-marked; retry test proves `Failed → Running → Bound`; rebind-failure test proves workspace
A keeps serving. Concurrency test: two threads call `BootstrapForRoot` for the same root — one
`Started`, one `JoinedRunning`, single background run.

**Acceptance criteria:**
- [x] `d225dc5` guard tests and all existing bootstrap/host tests pass unchanged
- [x] Slow-run simulation: `IsBound` false, snapshot `Running` with root+start time, host
      `StartAsync` returns immediately in BOTH eager and deferred paths
- [x] Failed-run simulation: snapshot `Failed`+message, gate unsignaled, registry error-marked,
      same-root retry transitions to `Running`
- [x] Atomic publication proven: no observer sees `IsBound==true` before the full
      `BoundWorkspace` (test polls getters from another thread during a gated publish)
- [x] `WaitForRunAsync` completes on that run's bind AND on its failure
- [x] Worker-scope verification passes; `serial-worker-commit` with recorded SHA

### Task 2: BindOutcome / rootsDirty contract in WorkspaceBindingService

**Files:**
- Modify: `src/Miller.Server/Hosting/WorkspaceBindingService.cs` (`EnsurePrimaryBoundCoreAsync` :73-104, `NeedsRefresh` :106)
- Test: `tests/Miller.Tests/Server/WorkspaceBindingServiceTests.cs`

**Interfaces:**
- Consumes: Task 1's `BindOutcome`.
- Produces: `_rootsDirty` cleared ONLY on `Started | AlreadyBound | JoinedRunning`;
  `RebindDeferred` preserves dirty + cached roots stay uncleared so the first call after the
  in-flight run completes re-resolves and starts the rebind (design rev 2 review blocker 2).
  `_bindLock` held only for the resolve+dispatch (never scan duration).

**Contract inputs:** design rev 2 §"`BootstrapForRoot` splits" (the stranded-root sequence);
existing roots caching in `GetRootUrisAsync`.

**File ownership:** Modify: `src/Miller.Server/Hosting/WorkspaceBindingService.cs`; Test: `tests/Miller.Tests/Server/WorkspaceBindingServiceTests.cs`

**Serialization required:** Yes

**Dependency reason:** Consumes Task 1's `BindOutcome`; same test file as Task 1.

**What to build:** The outcome-conditional dirty clear plus the stranded-root regression test:
bind A (slow, running) → roots change to B (`MarkRootsDirty`) → `EnsurePrimaryBound` returns
`RebindDeferred` (dirty stays true) → A completes → next `EnsurePrimaryBound` starts B →
B binds. Without the fix this sequence leaves the session on A forever.

**Approach:** Use Task 1's seams: gate-blocked override for A's run, release, assert B's run
starts on the next ensure call (snapshot root == B). Keep `EnsurePrimaryBoundFromRootsAsync`
test seam behavior intact.

**Acceptance criteria:**
- [x] Stranded-root regression sequence passes (fails on pre-task code)
- [x] Dirty cleared on accepting outcomes only; existing binding tests pass
- [x] Worker-scope verification passes; `serial-worker-commit` with recorded SHA

### Task 3: Filter — grace wait, not-ready/failed results, workspace snapshot render, ordering pin

**Files:**
- Modify: `src/Miller.Server/Telemetry/WorkspaceBindingCallToolFilter.cs`
- Modify: `src/Miller.Server/Hosting/WorkspaceBindingService.cs` — add `BootstrapSnapshot Snapshot { get; }` + `Task WaitForRunAsync(int, CancellationToken)` passthroughs to `IWorkspaceBindingService`
- Test: `tests/Miller.Tests/Server/WorkspaceBindingCallToolFilterTests.cs` (exists — extend)

**Interfaces:**
- Consumes: Task 1's snapshot + `WaitForRunAsync`; Task 2's fast-return semantics; the exact
  response texts from Global Constraints; tool name via `request.Params?.Name` (pattern:
  `TelemetryCallToolFilter.cs:52`); is-error result shape (pattern: `TelemetryCallToolFilter.cs:117`).
- Produces: the full agent-facing not-ready contract — `Bound` → next(); `Running` →
  `WaitForRunAsync(snapshot.RunGeneration)` with grace timeout from
  `MILLER_BOOTSTRAP_GRACE_SECONDS` (default 5, `0` = skip wait) → re-read snapshot → proceed if
  `Bound`, else is-error not-ready text; `Failed` → is-error failed text (the ensure call
  already started the retry — assert snapshot went `Running` with `LastFailureMessage`
  preserved); unbound `workspace` tool → SUCCESSFUL result rendering the snapshot without
  invoking the tool.

**Contract inputs:** design rev 2 §"Fast not-ready tool responses", §"Workspace-tool exemption",
§"Filter ordering and telemetry"; filter registration order at `src/Miller.Server/Program.cs:111-115`
(binding added before telemetry — the ordering-pin test asserts binding runs OUTSIDE, i.e. an
unbound call never reaches the telemetry filter; verify actual composition direction in the MCP
SDK while writing the test and pin whichever direction reality has, reporting a plan mismatch if
telemetry turns out to be outer).

**File ownership:** Modify: `src/Miller.Server/Telemetry/WorkspaceBindingCallToolFilter.cs`, `src/Miller.Server/Hosting/WorkspaceBindingService.cs` (snapshot passthrough on `IWorkspaceBindingService`); Test: `tests/Miller.Tests/Server/WorkspaceBindingCallToolFilterTests.cs`

**Serialization required:** Yes

**Dependency reason:** Consumes Task 1's snapshot + run-generation gate and Task 2's outcome semantics.

**What to build:** The user-visible half: grace wait, the two is-error texts, the workspace
snapshot render, env knob, and the composition-order pin test.

**Approach:** Drive the filter directly in tests (compose a fake `next`, fake binding service
with scripted snapshots) — no MCP transport needed; follow the existing test file's pattern.
Cover: bound passthrough, running + gate opens within grace → proceeds, running + timeout →
not-ready text, grace `0` → immediate, cancellation during grace honors the token, failed →
failed text + retry started, unbound `workspace` → successful snapshot render, bound
`workspace` → passthrough.

**Acceptance criteria:**
- [x] All eight filter behaviors above tested and green; texts match Global Constraints verbatim
- [x] Ordering pin test locks binding-outside-telemetry (or documents + pins reality per plan
      mismatch note)
- [x] Worker-scope verification passes; `serial-worker-commit` with recorded SHA

### Task 4: Status surfaces, CLAUDE.md, branch gate

**Files:**
- Modify: `src/Miller.Server/Tools/WorkspaceTool.cs` (status op), `src/Miller.Server/Tools/WorkspaceRender.cs:193` (status render — one-line rebind notice when a new run is in flight while bound)
- Modify: `CLAUDE.md` "Host lifecycle gotcha" + "Server host & startup" sections ("getters throw until BOUND", background bootstrap, `MILLER_BOOTSTRAP_GRACE_SECONDS`), then `scripts/sync-agents.sh`, then `cmp -s CLAUDE.md AGENTS.md`
- Test: `tests/Miller.Tests/Server/WorkspaceToolTests.cs` (bound-side rebind notice only; unbound
  `workspace` snapshot rendering belongs to Task 3's filter tests because the tool cannot
  construct before binding)

**Interfaces:**
- Consumes: Task 1's snapshot (inject `IndexBootstrapService` into `WorkspaceTool` — safe, the
  tool only constructs when bound; the UNBOUND case is fully handled by Task 3's filter render
  and needs nothing here).
- Produces: bound `workspace status` shows `rebinding: <new root> (started <N>s ago)` when
  `Snapshot.Phase == Running` while bound; docs match shipped behavior.

**Contract inputs:** design rev 2 §"Workspace-tool exemption" (bound-side notice only) and the
review-record nit; CLAUDE.md edit-then-sync rule.

**File ownership:** Modify: `src/Miller.Server/Tools/WorkspaceTool.cs`, `src/Miller.Server/Tools/WorkspaceRender.cs`, `CLAUDE.md` (+ regenerated `AGENTS.md`); Test: `tests/Miller.Tests/Server/WorkspaceToolTests.cs`

**Serialization required:** Yes

**Dependency reason:** Consumes the full stack; runs the branch gate.

**What to build:** The bound-side rebind notice, the doc truth-up, and the branch gate.

**Approach:** Render test for the rebind line; CLAUDE.md wording change scoped to the lifecycle
invariant and the new env knob (do not restructure other sections); finish with the branch gate:
`scripts/test.sh all` (scale suite required — indexing path touched) and
`dotnet build Miller.slnx -c Release` (0 warnings), recording both in the ledger.

**Acceptance criteria:**
- [ ] Rebind notice rendered only when Running-while-bound; absent otherwise
- [ ] CLAUDE.md updated, AGENTS.md regenerated, `cmp -s` clean
- [ ] Branch gate green: fast + scale suites, Release build 0 warnings
- [ ] Worker-scope verification passes; `serial-worker-commit` with recorded SHA

---

## Out of scope

Mid-scan progress percentages, scan preemption on rebind, MCP progress notifications (design
§YAGNI). Pushing the commits and any release remain user-approval items.
