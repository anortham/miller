# Stateless workspace targeting design

Date: 2026-08-30
Status: implemented
Linear: [BRE-57](https://linear.app/breakingdevelopment/issue/BRE-57/require-explicit-workspace-id-for-stateless-mcp-calls)
Implementation: [implementation plan](2026-08-30-stateless-workspace-targeting-implementation.md), integrated in commit [`e36a6f6a`](https://github.com/anortham/miller/commit/e36a6f6a0e6475f4b85440a94e2a2e55e3cf54ba)

## Goal

Make one user-level Miller MCP registration work safely across Codex, Cursor, VS Code, and other GUI clients.
Every workspace-bound MCP request names its target. Process working directory, MCP Roots, connection identity,
and prior requests never select a workspace.

This is an MCP contract change. The CLI keeps its working-directory-based `current` behavior.

## Why now

GUI applications do not provide a trustworthy process working directory to a user-level MCP server. A project
environment variable works only in project-local configuration and cannot represent several projects behind one
user-level registration.

The old fallbacks have failed repeatedly:

- Cursor user-global launches used plugin-cache paths or unresolved workspace placeholders.
- GUI clients launched servers from filesystem roots, home directories, or unrelated repositories.
- MCP Roots repaired some clients, but made the target connection-scoped and dependent on a client callback.
- Registry recovery can find known workspaces, but cannot safely choose one for a write.

MCP revision `2026-07-28` makes this an interface problem rather than another path-detection bug. The protocol is
stateless and sessionless, and Roots is deprecated. Requests must carry the information or explicit state handle
needed to process them.

Official references:

- [MCP 2026-07-28 release](https://blog.modelcontextprotocol.io/posts/2026-07-28/)
- [Stateless protocol rules](https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/docs/specification/draft/basic/index.mdx#statelessness)
- [Roots deprecation](https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/docs/specification/2026-07-28/client/roots.mdx)
- [Sessionless MCP](https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/seps/2567-sessionless-mcp.md)

## Current behavior

Miller has an implicit primary workspace:

- `WorkspaceBindingResolver.TryResolveStartup` chooses `MILLER_WORKSPACE_ROOT` or process cwd.
- `WorkspaceBindingService.EnsurePrimaryBoundAsync` requests MCP Roots when startup defers.
- `WorkspaceBindingCallToolFilter` tries to bind a primary before nearly every tool call.
- `WorkspaceIndexProvider.Resolve(null, ...)` and `ResolveSymbolRead(null, ...)` choose the primary.
- Explicit selectors matching the primary take a different read path from the same selector while unbound.
- `WorkspaceContext` mixes per-workspace paths with machine-global registry, telemetry, and tools paths.
- Several tool constructors cannot be created before a primary bind because DI factories read throwing bootstrap getters.
- `WorkspaceOpenPrimeService` waits for a primary bind before refreshing a newly opened registered workspace.
- `edit` has no `workspace_id`; it always reads, locks, writes, and converges the primary workspace.

This model assumes a process belongs to one editor window or project. MCP 2026 explicitly says clients should not
use a task, thread, or conversation as the stdio-process lifetime.

## Approaches considered

### Required workspace selector on each tool call

Every workspace-bound MCP tool requires `workspace_id`. `workspace list` and `workspace open(path)` bootstrap the
handle. The server resolves every explicit selector from the registry on that request.

This is the recommended design. It follows MCP's explicit-handle model, works on current protocol versions, and
does not need client-specific support.

### Custom workspace metadata in `_meta`

A Miller extension could carry a workspace URI in every request's `_meta`. It would keep tool schemas smaller,
but no current GUI client sends that extension. It moves the same requirement into a less visible field and does
not solve first use. Reject.

### Keep cwd, Roots, and registry heuristics

More precedence rules cannot prove which project a long-lived GUI process means. Read recovery would remain
unreliable and write recovery would remain unsafe. Roots is deprecated. Reject.

### Mint a session or context handle

Miller already has a durable workspace identifier derived from the canonical root. A second handle adds lifecycle
and handoff rules without adding information. Reject.

## Decision

### MCP tools require an explicit workspace

The following MCP tools require a non-empty `workspace_id` in their advertised JSON schemas:

- `search`
- `inspect`
- `context`
- `trace`
- `impact`
- `edit`
- `patterns`
- `content`
- `tests`

The parameter is required at the MCP wrapper, not inside shared CLI cores. C# method signatures put the non-default
workspace parameter before optional parameters so `ModelContextProtocol` 1.4 advertises it in the schema's
`required` array. Runtime validation remains mandatory because schemas do not enforce business rules in the SDK.

`content workspace_id=all` remains valid only for its existing registered-workspace search. It is explicit scope,
not an implicit current workspace. It searches registered rows only. Unopened workspaces are invisible.

### Workspace operations that do not require an ID

The `workspace` tool keeps `workspace_id` conditionally optional because these operations bootstrap or manage the
machine-global registry:

- `workspace list`
- `workspace open(path)`
- `workspace remove(path)`
- `workspace prune`
- `workspace dashboard`

All other workspace operations require a selector. `workspace open(path)` returns the canonical full ID and a
copyable display ID. The first-use flow is:

1. Call `workspace open(path=<project root>)`, or `workspace list` for an existing project.
2. Copy the returned ID into every workspace-bound call.
3. Reuse that ID across tasks, connections, and server processes.

### Selector policy

The canonical handle is the full workspace ID. Existing request-contained aliases may remain for usability:

- an exact display ID;
- a unique ID prefix;
- an exact registered root path.

`current` and `primary` are rejected at the MCP boundary. Shared CLI cores may continue to resolve them.

Reads use `WorkspaceSelectorIntent.Read`, preserving the existing one-live-root tie break. Mutations use
`WorkspaceSelectorIntent.Mutate`, which never guesses between ambiguous matches. `edit` and mutating `tests` or
`content` operations must use mutate intent. A mutation accepts an alias only when it resolves exactly and
unambiguously. Error text recommends the full ID.

### One target policy and one diagnostic

Replace binding-first request filtering with a small `McpWorkspaceTargetPolicy` at the MCP argument boundary. It
classifies the tool and operation as one of:

- registry-wide;
- explicit single workspace;
- explicit all registered workspaces;
- missing or forbidden implicit workspace.

The policy returns a `ToolDiagnostic` with stable code `workspace_id_required` or
`implicit_workspace_selector_refused`. It runs before a tool type is constructed, so an unbound process returns a
useful error instead of resolving a throwing primary-workspace dependency.

The same policy drives tests for tool schemas, call filtering, and diagnostic telemetry. Tool implementations still
validate the resolved target at their own trust boundary.

## Host architecture

### Machine-global paths are available before workspace binding

Add a `MillerHostPaths` value created from `MillerHome.Resolve()` and the application base directory. It owns:

- Miller home directory;
- workspace registry path;
- telemetry database path;
- bundled tools root.

`MILLER_HOME` and the existing test home override feed this one value. Tests must never fall through to the real
profile. `WorkspaceContext` remains the per-workspace value. Its machine-global properties delegate to
`MillerHostPaths` during migration so there is one path source, not two independent copies.

Open the machine-global telemetry ledger without a default workspace ID. Each resolved call stamps its actual ID
and root through `TelemetryContext`. Workspace status and onboarding queries pass their target explicitly.

Register these services from `MillerHostPaths`, not a bound `WorkspaceContext`:

- `WorkspaceRegistry`;
- `TelemetryLedger`;
- `JulieExtractRunner`;
- semantic broker connection factory;
- scan governor;
- cross-workspace refresh and open-prime services.

### A server may be unbound and still serve explicit calls

Every MCP tool type and every dependency needed for a registered target must be constructible while
`IndexBootstrapService` is deferred.

`WorkspaceOpenPrimeService` no longer waits for a primary bind. It drains IDs through the machine-global registry
and `CrossWorkspaceRefreshService` immediately.

Startup binding from `MILLER_WORKSPACE_ROOT` or a usable process cwd remains. It supplies background indexing,
watching, and vector convergence for that workspace. It never selects an MCP target. `IndexBootstrapService` keeps
its `UpsertSeen` behavior so an actual project launch enters the registry.

Remove request-time Roots binding and Roots change rebinding from the default MCP path. An unbound GUI process
stays unbound until shutdown, while list, open, refresh, reads, edits, content, and tests operate through explicit
registered targets. No tool call sends `roots/list`.

Hosted primary services may keep waiting when no primary exists. Registered operations must not wait on them.

### Explicit IDs always use registered routing

`WorkspaceIndexProvider` must be constructible with lazy access to primary-only services. Null selectors remain
available to internal and CLI paths, where they use the primary. Every non-null MCP selector uses registered
routing even when it names the bound primary.

This rule is observable and therefore mandatory. Binding state must not change:

- freshness status;
- refresh behavior;
- selected database or sidecar;
- warning text;
- continuation identity;
- result bytes, apart from nondeterministic timing fields that already vary.

An A/B test calls one explicit ID with and without a matching bound primary and requires the same registered-route
result. The primary may cache the same underlying artifacts, but it cannot change semantics.

`IndexFreshProbe` becomes optional at the central telemetry layer. A resolved workspace context is the only source
of target `index_fresh` facts.

## Editing an explicit workspace

`edit` becomes a normal workspace-routed tool.

### Resolved edit context

Extend the resolved symbol-read context, or add a focused edit context, so it carries:

- canonical full workspace ID;
- canonical workspace root;
- index database path;
- pinned read session and lookup index;
- current freshness facts.

`EditTool` builds `EditService` only from that resolved context. It no longer reads root or database paths from the
ambient `WorkspaceContext`.

### Per-target lock

Create `EditApplier` per resolved call. Its lock is `<target>/.miller/edit.lock`. A registered edit must never take
the primary workspace's lock.

### Per-target convergence

Add a target-bound `IEditWriteThrough` implementation:

- For a target serviced by the local primary leader, reuse `LeaderWriteThrough`.
- Otherwise write `LeaderScanRequestQueue.RequestFileConverge` into the target `.miller` directory using the
  canonical full ID and changed full paths.
- For stale recovery, request file convergence and poll the target revision for the existing bounded recovery
  window.
- If no leader advances the target, fall back to a bounded `CrossWorkspaceRefreshService.Refresh` with
  `bypassBackoff: true`. This is a direct user edit, not automatic background traffic.
- Never start with a whole-workspace refresh. The file-converge queue is the normal edit path.

The next edit's freshness gate remains the final safety check. Apply success cannot claim index convergence when
the request or fallback was deferred.

## Registry lifecycle safety

`workspace remove` and `workspace prune` remain available while unbound.

- When a primary exists, its optional bound snapshot supplies the live root, workspace ID, and protected workspace
  `.miller` directory used by existing self-removal guards.
- When no primary exists, those in-process guards are absent. The existing cross-process writer lock remains the
  authority and must refuse an active target.
- `protectedMillerDir` must never point at machine-global Miller home. It protects a bound workspace's local
  `.miller` directory only.
- Prune continues to consider only rows whose canonical roots are absent.

## Compatibility and scope

### CLI

The CLI contract is unchanged. CLI dispatch may continue to derive its local workspace from process cwd and may
use `current` or `primary` inside shared cores.

### MCP protocol and SDK

Miller currently pins `ModelContextProtocol` 1.4.0. The C# SDK's 2026-era implementation is still a 2.0 release
candidate as of this design. Do not couple workspace correctness to adopting a prerelease SDK.

Implement the explicit workspace contract on 1.4.0. A later SDK/protocol migration removes the legacy handshake
and adds 2026 transport behavior, but it does not redesign workspace selection.

### No new MCP tool

This design changes existing tool arguments and the existing `workspace` operation. It adds no MCP tool.

## Error behavior

Missing or implicit MCP targets fail closed before tool construction. Compact and JSON results use the shared
diagnostic renderer and stable reason codes. The diagnostic tells the agent to call `workspace list` or
`workspace open(path)` and then retry with the returned ID.

Unknown IDs, ambiguous aliases, missing roots, unreadable indexes, and stale artifacts keep their existing typed
diagnostics. Mutating calls never use the read-only live-root ambiguity tie break.

Continuation tokens stay bound to the canonical full workspace ID. Replaying a token with another selector is a
refusal.

## Guidance and documentation

Update all workspace parameter descriptions together. Remove `current` and `primary` from MCP descriptions, while
keeping CLI help intact.

Update the embedded server instructions within the existing 1,900-character cap. Replace old implicit-workspace
text instead of growing the budget. The key instruction is: use `workspace list` or `workspace open(path)` once,
then pass `workspace_id` on every workspace-bound call.

Update:

- `README.md` user-level GUI setup;
- `docs/install.md`;
- the Cursor user-global finding;
- the MCP Roots binding design, marked superseded for target selection;
- `CLAUDE.md`, followed by `scripts/sync-agents.sh` so `AGENTS.md` remains generated.

## Architecture quality

**Affected modules:** MCP tool schemas, request filtering, host path registration, workspace binding, registered
index routing, edit locking and convergence, content, CT, telemetry, and public guidance.

**Caller-facing interface:** every workspace-bound MCP tool requires `workspace_id`; `workspace list/open` are the
bootstrap interface.

**Depth and locality:** target selection is centralized at the MCP boundary. Machine-global paths stop leaking
through a primary workspace. Edit target resolution, locking, and convergence move together behind one target-bound
context.

**Test surface:** generated MCP schemas, in-process and spawned MCP calls, the existing tool interfaces, and CLI A/B
guards. Private helpers are not the contract.

**Seams and adapters:** `MillerHostPaths` separates machine-global state from a workspace. The target-bound edit
write-through is a real adapter because local-primary and registered-workspace convergence differ. No protocol or
client-specific workspace adapter is added.

**Rejected shortcuts:** user-level environment variables, process cwd, Roots, prior-call state, connection state,
registry guessing, primary edit locks, whole-workspace edit scans, and a second session handle.

**Architecture risk:** high. The change removes an assumption shared by host startup and all MCP tools.

## Verification design

### Contract tests

- Generated schemas require `workspace_id` for all nine workspace-bound tools.
- `workspace` keeps only the listed registry-wide operations callable without an ID.
- Missing IDs and `current` or `primary` return stable typed diagnostics.
- Tool-description and embedded-instruction budgets remain green.

### Unbound host tests

- Start Miller from an unsafe or unrelated cwd with isolated `MILLER_HOME` and no Roots callback.
- `workspace list` works without constructing primary services.
- `workspace open(path)` returns an ID and its primer runs without a primary.
- Every read tool works with that ID.
- Two different IDs on one process never share target facts or continuations.
- No request issues `roots/list`.

### Binding-state A/B

- The same explicit ID produces the same registered-route result with no primary, a different primary, and a
  matching primary.
- Startup cwd may register and index a primary but never changes an explicit call's target.

### Mutation tests

- `edit` previews and applies against a registered non-primary workspace.
- The target `.miller/edit.lock` serializes edits; the primary lock is untouched.
- Apply writes a file-converge request under the target workspace.
- A target leader drains that request.
- With no target leader, stale recovery falls back to a bounded explicit refresh and reopens the target context.
- Ambiguous mutation selectors refuse without touching disk.
- `tests` and mutating `content` operations resolve with mutate intent.

### Global-path isolation

- `MILLER_HOME` and the test home override move registry, telemetry, scan-governor, broker, and related global paths
  together.
- An unbound test process writes nothing under the developer's real profile.
- Telemetry rows carry the explicitly resolved workspace.

### Regression gates

- Focused workspace-binding, host-startup, provider, tool, edit, content, CT, telemetry, and instruction tests.
- Bare fast suite once at the task boundary.
- Release build with zero warnings and errors.
- Scale suite because indexing, refresh, and edit convergence change.
- Focused Windows verification through `win-test` because GUI launch cwd and path behavior are part of the bug.
- `git diff --check`, generated `AGENTS.md` parity, and a post-edit Miller impact pass.

## Acceptance criteria

- [ ] One user-level MCP registration safely serves several projects in one process.
- [ ] Every workspace-bound MCP schema requires `workspace_id`.
- [ ] Registry-wide bootstrap operations work with no primary bound.
- [ ] `workspace open(path)` returns the ID needed for subsequent calls and primes without a primary.
- [ ] Explicit calls never derive a target from cwd, Roots, prior calls, or connection identity.
- [ ] `current` and `primary` are rejected only at the MCP boundary; CLI behavior remains intact.
- [ ] Explicit IDs always use registered routing, independent of primary binding state.
- [ ] All MCP tool types can be constructed while the primary bootstrap is deferred.
- [ ] Registry, telemetry, tools, broker, and scan-governor paths share one `MillerHome` source.
- [ ] `edit` resolves, locks, writes, and converges the named workspace.
- [ ] A mutation never guesses between aliases.
- [ ] `content all` searches registered workspaces only.
- [ ] No MCP call sends `roots/list`.
- [ ] Existing typed diagnostics, output budgets, continuation guards, and privacy rules remain intact.
- [ ] Public docs explain the list/open then pass-ID flow for Codex, Cursor, and VS Code.
