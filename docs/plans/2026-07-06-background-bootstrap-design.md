# Background Bootstrap + Fast Not-Ready Tool Responses — Design

Date: 2026-07-06
Status: design for user review (lightweight path — same-session implementation intended)
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
`BootstrapSnapshot { Phase, CanonicalRoot?, StartedAtUtc?, FailureMessage? }` — a read-only
snapshot property, safe to render from any thread.

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
- A `BootstrapForRoot` for a DIFFERENT root while `Running` is ignored with a warning log
  (callers see not-ready for the in-flight root; the rebind happens naturally on the next
  `EnsurePrimaryBound` after the current run completes). Rebind-while-running is rare
  (roots/list_changed mid-scan) and serializing it is simpler than a preemption protocol.
- Internal test seam `TestRunBootstrapOverride: Action<string>?` — replaces `RunBootstrap` in
  the background task so tests can simulate slow (block on a gate) and failing (throw)
  bootstraps deterministically.

### Fast not-ready tool responses (`WorkspaceBindingCallToolFilter`)

After `EnsurePrimaryBoundAsync` returns (now fast — it only triggers/joins the bind), the filter
consults the snapshot:

- `Bound` → call the tool handler (today's happy path).
- `Running` → await the binding gate with a **grace timeout** (default 5s; env knob
  `MILLER_BOOTSTRAP_GRACE_SECONDS`, `0` = julie-style immediate fail-fast). If the gate opens in
  time → proceed to the handler. If not → return
  `CallToolResult { IsError = true }` with:
  `"Miller is indexing this workspace for the first time: <root> (started <N>s ago). Tool calls
  will work once indexing completes — retry shortly, or run 'workspace status' for progress."`
  A tool-result error (not a protocol error) so agents read the text and retry naturally.
- `Failed` → return `CallToolResult { IsError = true }` with the stored failure message plus
  `"The next tool call retries bootstrap automatically."` (and it does — `Failed → Running`).
- `Idle` with no resolvable root → existing `CreateBindingFailureException` behavior unchanged.

`EnsurePrimaryBoundCoreAsync` no longer holds `_bindLock` for the scan duration (BootstrapForRoot
returns after dispatch), so queued calls fall through to the grace-wait immediately.

**Workspace-tool exemption:** the `workspace` tool bypasses the not-ready gate so
`workspace status`/`health` are usable DURING indexing (julie parity: `manage_workspace` worked
while indexing). When unbound, `workspace status` renders the bootstrap snapshot only —
`"bootstrap: running <root>, started <N>s ago"` or `"bootstrap: failed — <message>"` — without
touching index getters (which throw pre-bound). Other workspace operations that need the index
report the same not-ready line instead of throwing.

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
- Test: `tests/Miller.Tests/Server/WorkspaceBindingServiceTests.cs` (state transitions, retry
  from Failed, different-root-while-running ignored),
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
- [ ] Existing binding/bootstrap tests pass unchanged (synchronous `TestBootstrapInterceptor`
      seam preserved).
- [ ] Fast suite green within budget; no new Scale-tagged tests needed (all simulation via the
      test seams, no real julie-extract spawn).
