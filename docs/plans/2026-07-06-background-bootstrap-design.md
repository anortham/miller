# Background Bootstrap + Fast Not-Ready Tool Responses — Design

Date: 2026-07-06
Status: design for user review, rev 2 — Codex design review findings folded in (see "Review
record" at the end); same-session implementation intended
Driver: 2026-07-06 incident follow-up. The sensitive-root bind fix (`d225dc5`) removed the
*trigger*, but the underlying UX defect remains: the first MCP tool call runs the entire initial
julie-extract scan synchronously, and every other call waits behind it with no timeout — 30
silent minutes against a huge tree. julie (the product-family predecessor) ran indexing in
`spawn_blocking` off the request path and answered tools immediately with an actionable
not-ready message (`julie/src/tools/workspace/indexing/index.rs:104`,
`julie/src/handler/workspace_resolution.rs:91`). Miller regressed that; this restores it.

## Verified current behavior (the defect)

- `WorkspaceBindingCallToolFilter` awaits `EnsurePrimaryBoundAsync` before EVERY tool handler.
- Deferred path: `EnsurePrimaryBoundCoreAsync` takes `_bindLock`, then
  `IndexBootstrapService.BootstrapForRoot` → `RunBootstrap` runs `runner.Scan(...)`
  **synchronously inside the tool call** (`IndexBootstrapService.cs:227`), holding `_bindLock`
  for the scan's whole duration. Queued calls wait on `_bindLock.WaitAsync(ct)` — cancellation
  is the only exit.
- Eager path: `StartAsync` calls `BootstrapForRoot` inline, blocking host startup on the scan.
- Other waiters use `WaitUntilBoundAsync` → `_bindingReady.Task.WaitAsync(ct)` — no timeout.
- No progress signal, no not-ready response, no failure surface: a failed background rebind or a
  wedged julie-extract is indistinguishable from a slow scan.

## Design

### Bootstrap state machine (`IndexBootstrapService`)

```
Idle ──BootstrapForRoot──▶ Running { canonicalRoot, source, startedAtUtc }
Running ──scan+load ok──▶ Bound        (existing SignalBound gate fires)
Running ──exception─────▶ Failed { canonicalRoot, message, failedAtUtc }
Failed ──BootstrapForRoot (retry)──▶ Running
Bound ──BootstrapForRoot (new root)──▶ Running   (existing rebind semantics)
```

State transitions happen under the existing `_gate`. Exposed as
`BootstrapSnapshot { Phase, CanonicalRoot?, StartedAtUtc?, FailureMessage?, RunGeneration }` — a
read-only snapshot property, safe to render from any thread.

**Atomic publication (review blocker 1):** today `RunBootstrap` writes `_holder` first and
`IsBound` is just `_holder is not null` — safe only because everything runs under `_gate` on the
caller's thread. On a background task that ordering is a torn-state hazard. The background run
builds a complete immutable `BoundWorkspace { Holder, Resolver, Workspace, Ledger }` in locals,
then publishes it in ONE step under `_gate` — registry ready-marking and
`ledger.RebindWorkspace` happen at that same publish point (review should-fix: today they run
mid-scan, so telemetry attribution and registry state could reflect a bind that then fails), and
only then does `SignalBound` fire. `IsBound` reads the published record. On background failure:
state → `Failed`, registry row marked error best-effort, nothing published.

### `BootstrapForRoot` splits into synchronous gate + background work

**Stays synchronous on the caller's thread (unchanged semantics):**
- `CanonicalizeAndRejectSensitiveRoot` for every source — the `d225dc5` guard still throws
  immediately at bind time, BEFORE any state transition or background dispatch.
- Same-canonical-root idempotence check (already-bound → return; already-Running for the same
  root → return).
- `TestBootstrapInterceptor` — invoked synchronously exactly as today; returning true skips the
  background dispatch entirely. Existing tests keep passing unmodified.

**Moves to a background task (`Task.Run`):**
- `RunBootstrap` (julie-extract scan, index load, registry write) + `SignalBound`.
- On exception: state → `Failed(message)`, error logged; the binding gate is NOT signaled.
  A later `BootstrapForRoot` for the same root retries from `Failed` (self-healing after a
  transient failure — e.g. julie-extract binary restored).
- `BootstrapForRoot` returns a `BindOutcome` (`Started | AlreadyBound | JoinedRunning |
  RebindDeferred`). A DIFFERENT root while `Running` returns `RebindDeferred` with a warning log
  — and critically, `WorkspaceBindingService` clears `_rootsDirty` ONLY for `Started` /
  `AlreadyBound` / `JoinedRunning` outcomes (review blocker 2: the current unconditional clear
  at `WorkspaceBindingService.cs:93-98` would otherwise strand the session on the old root
  forever — once the old-root run bound, `IsBound && !NeedsRefresh` early-returns and the new
  root is never bound). With the dirty flag preserved, the first call after the in-flight run
  completes re-resolves roots and starts the rebind. No preemption protocol needed.
- Internal test seam `TestRunBootstrapOverride: Action<string>?` — replaces `RunBootstrap` in
  the background task so tests can simulate slow (block on a gate) and failing (throw)
  bootstraps deterministically.

### Fast not-ready tool responses (`WorkspaceBindingCallToolFilter`)

After `EnsurePrimaryBoundAsync` returns (now fast — it only triggers/joins the bind), the filter
consults the snapshot:

- `Bound` → call the tool handler (today's happy path).
- `Running` → await a **run-generation-keyed gate** with a **grace timeout** (default 5s; env
  knob `MILLER_BOOTSTRAP_GRACE_SECONDS`, `0` = julie-style immediate fail-fast). Generation-keyed
  because `WaitUntilBoundAsync` returns immediately whenever ANY holder exists (review blocker
  4): during a rebind — `Bound(A) → Running(B)` — the old gate reads "bound" and tools would
  silently answer from workspace A while B indexes. The filter waits on THIS run's completion;
  if it opens in time → proceed. If not → return
  `CallToolResult { IsError = true }` with:
  `"Miller is indexing this workspace for the first time: <root> (started <N>s ago). Tool calls
  will work once indexing completes — retry shortly, or run 'workspace status' for progress."`
  A tool-result error (not a protocol error) so agents read the text and retry naturally.
- `Failed` → one exact contract (review should-fix: "return stored error, next call retries"
  was ambiguous because `EnsurePrimaryBoundCoreAsync` re-triggers `BootstrapForRoot` BEFORE the
  filter inspects state, so the filter would observe `Running`, not `Failed`): the call that
  finds `Failed` STARTS the retry (`Failed → Running`) and returns
  `CallToolResult { IsError = true }` with `"bootstrap failed: <stored message>; retry started —
  call again shortly."` The snapshot keeps `LastFailureMessage` through the retry so the text is
  available while `Running`.
- `Idle` with no resolvable root → existing `CreateBindingFailureException` behavior unchanged.

`EnsurePrimaryBoundCoreAsync` no longer holds `_bindLock` for the scan duration (BootstrapForRoot
returns after dispatch), so queued calls fall through to the grace-wait immediately.

**Workspace-tool exemption — rendered by the FILTER, not the tool (review blocker 3):**
`WorkspaceTool`'s constructor requires `IndexHolder`, `WorkspaceContext`, `TelemetryLedger`,
`JulieExtractRunner`, … — all resolved through bootstrap getters that THROW before binding
(`MillerServiceRegistration.cs:45-48`), so an unbound `workspace` call cannot even construct the
tool. Instead: when the snapshot is not `Bound` and the tool name is `workspace`
(`request.Params?.Name`, same pattern telemetry uses), the filter itself returns a SUCCESSFUL
`CallToolResult` rendering the bootstrap snapshot — `"bootstrap: running <root>, started <N>s
ago"` / `"bootstrap: failed — <message>"` — for every operation, without invoking the tool.
Once bound, `workspace` flows normally (and its status render gains a one-line rebind notice
when a new run is in flight, via an injected `IndexBootstrapService` snapshot — constructible
because the tool only resolves when bound). julie parity: `manage_workspace` worked while
indexing.

### Filter ordering and telemetry (review should-fix)

`Program.cs` composes the binding filter OUTSIDE the telemetry filter. That order is now
load-bearing twice over: telemetry's filter resolves `TelemetryLedger` (a bound-only getter)
before calling `next`, so it must never run for unbound calls — and consequently not-ready
responses are NOT telemetered (they are logged by the binding filter instead; acceptable and now
documented). An integration test pins the composition order so a refactor cannot silently flip
it.

### Eager startup path

`StartAsync` uses the same split: guard synchronously, dispatch the scan to background, return.
Host startup and MCP initialize no longer block on the initial scan; for small repos the scan
typically wins the race with the first tool call anyway, and the grace period absorbs the rest.
The M3 hosted services are unaffected — they already read bootstrap getters lazily inside
`ExecuteAsync` and wait on the binding gate, which still signals on success and stays unsignaled
on failure (same as today's deferred-never-bound case).

### What this does NOT do (YAGNI)

- No mid-scan progress percentages: julie-extract reports at scan end; the status line reports
  root + elapsed, which is what an operator needs to distinguish "working" from "wedged".
- No preemptive cancel of an in-flight scan on rebind, no scan queue.
- No MCP progress notifications (revisit if agent harnesses start rendering them).

## Files

- Modify: `src/Miller.Server/Hosting/IndexBootstrapService.cs` (state machine, background
  dispatch, test seam), `src/Miller.Server/Hosting/WorkspaceBindingService.cs` (fast return,
  snapshot passthrough on `IWorkspaceBindingService`),
  `src/Miller.Server/Telemetry/WorkspaceBindingCallToolFilter.cs` (grace wait + not-ready/failed
  results + workspace-tool exemption), `src/Miller.Server/Tools/WorkspaceTool.cs` +
  `WorkspaceRender` (unbound status rendering).
- Test: `tests/Miller.Tests/Server/WorkspaceBindingServiceTests.cs` (state transitions,
  atomic-publication visibility, `BindOutcome` contract, retry-from-Failed contract,
  rebind-deferred keeps `_rootsDirty` and rebinds after the in-flight run),
  `tests/Miller.Tests/Server/WorkspaceBindingCallToolFilterTests.cs` (new: grace path, not-ready
  result, failed result, workspace exemption, `MILLER_BOOTSTRAP_GRACE_SECONDS=0`),
  `tests/Miller.Tests/Server/WorkspaceToolTests.cs` (unbound status render).

## Acceptance criteria

- [ ] Sensitive-root rejection still throws synchronously at bind time for every source
      (existing `d225dc5` regression tests pass unchanged).
- [ ] The initial scan runs on a background task in BOTH eager and deferred paths; no tool call
      and no host startup ever blocks for the scan's duration.
- [ ] Tool calls during `Running` return an is-error result with root + elapsed after the grace
      timeout; `MILLER_BOOTSTRAP_GRACE_SECONDS` honored, `0` = immediate.
- [ ] Tool calls after a bootstrap failure return the stored error and the next call retries.
- [ ] `workspace status`/`health` work during indexing and render the bootstrap snapshot when
      unbound.
- [ ] `BoundWorkspace` publishes atomically under `_gate`; registry-ready + ledger rebind occur
      only at the publish point; failed rebind keeps the previous workspace serving (existing
      `HostStartupRegistrationTests` behavior preserved).
- [ ] Grace wait is run-generation-keyed; during a rebind, tools return not-ready for the new
      run rather than silently answering from the old root.
- [ ] `RebindDeferred` preserves `_rootsDirty`; the deferred rebind starts on the first call
      after the in-flight run completes (regression test for the stranded-root sequence).
- [ ] Filter composition order (binding outside telemetry) pinned by an integration test;
      not-ready responses documented as logged-not-telemetered.
- [ ] CLAUDE.md host-lifecycle section updated ("getters throw until BOUND", not "until
      StartAsync"); AGENTS.md regenerated in sync.
- [ ] Existing binding/bootstrap tests pass unchanged (synchronous `TestBootstrapInterceptor`
      seam preserved).
- [ ] Fast suite green within budget; no new Scale-tagged tests needed (all simulation via the
      test seams, no real julie-extract spawn).

## Review record (Codex, read-only, 2026-07-06)

Verdict on rev 1: not implement-ready — 4 blockers, 3 should-fix, 1 nit. All verified against
code before acceptance; all folded into rev 2:

1. **Confirmed — background publication unsafe as drafted** (`_holder` first, `IsBound` =
   holder-null check). → Atomic `BoundWorkspace` publish under `_gate`, then signal.
2. **Confirmed — ignore-rebind-while-Running strands the session on the old root** (unconditional
   `_rootsDirty` clear). → `BindOutcome` return; dirty cleared only on accepting outcomes.
3. **Confirmed — workspace-tool exemption impossible with current DI** (ctor needs bound-only
   getters). → Snapshot rendered by the filter itself; tool untouched when unbound.
4. **Confirmed — `WaitUntilBoundAsync` breaks the grace wait for rebinds** (returns immediately
   when any holder exists). → Run-generation-keyed gate.
5. **Accepted — Failed-retry contract was ambiguous.** → Retry-starts-and-reports contract.
6. **Accepted — filter/telemetry ordering unspecified.** → Binding outside telemetry, pinned by
   test; not-ready responses logged, not telemetered.
7. **Accepted — registry/ledger writes mid-scan corrupt attribution on failed rebinds.** →
   Moved to the atomic publish point; failure marks registry error best-effort.
8. **Accepted (nit) — CLAUDE.md lifecycle wording.** → "until bound" language + AGENTS regen in
   the implementation slice.

Dropped by the reviewer after verification: `CallToolResult{IsError}` expressibility and
tool-name access in filters (both already used by `TelemetryCallToolFilter`), and M3
hosted-service lifecycle concerns (constructors already take bootstrap lazily; guard test
covers it).
