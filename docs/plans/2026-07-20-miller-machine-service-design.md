# Miller Machine Service Architecture — Deferred Design

**Status:** Draft for review. Direction agreed in discussion; implementation is deferred until the
current semantic integration program completes.

**Date:** 2026-07-20

**Related decisions and designs:**

- [`ADR-0002`](../adr/ADR-0002-dashboard-registry-lifecycle-mutations.md) — the dashboard may
  perform registry-lifecycle mutations only through shared cores.
- [`ADR-0003`](../adr/ADR-0003-semantic-retrieval-ownership.md) — Miller owns optional local
  semantic retrieval; Eros owns fleet-level semantic orchestration.
- [Miller semantic integration design](2026-07-19-miller-semantic-integration-design.md) — the
  current semantic program, including the per-process embedding child and `vectors.db` contract.

This document records a follow-on architecture direction. It does not change the scope, contracts,
or sequencing of the semantic work already in progress.

## 1. Context and problem

Miller is increasingly machine-wide in data and user experience, but remains session-shaped in
execution:

- Every MCP session starts a `miller` process that bootstraps a primary workspace, attempts
  indexer leadership, runs freshness services, and maintains its own read caches.
- One-shot CLI commands build their own process graph and open artifacts independently.
- The dashboard is already a separately launched machine-wide singleton that reads the global
  registry, shared telemetry, and per-workspace artifacts.
- `~/.miller/workspaces.db` and `~/.miller/telemetry.db` are already machine-global.
- Explicit cross-workspace reads are supported, but refresh and background ownership are still
  negotiated between per-session processes.
- The semantic design adds a lazy `SemanticEmbeddingSession` child to each Miller server process.
  Only a workspace's own leader may converge that workspace's vectors; foreign workspaces degrade
  lexically until one of their own leaders builds a compatible generation.

A process snapshot on 2026-07-20 observed 12 long-lived Miller/dashboard processes with about
4.45 GiB aggregate RSS before semantic embedding children were active. Aggregate RSS double-counts
shared pages, so it is directional evidence rather than physical-memory accounting. It still proves
that the process multiplier is real. A large model, compute context, GPU buffers, watchers, caches,
and background queues make that multiplier more expensive.

The semantic design rejected a shared daemon initially because socket discovery, singleton
coordination, and Windows named-pipe behavior were not justified without measured swarm cost. The
current process evidence and the addition of an expensive shared semantic runtime justify reopening
that alternative. Reopening it does not justify recreating Julie's former always-on, self-restarting
daemon architecture.

## 2. Decision summary

After the semantic integration program completes, evolve Miller toward a **demand-started,
machine-wide service** with thin MCP and CLI clients.

The target shape is:

1. A packaged, non-Native-AOT `Miller.Service` process owns machine-wide runtime behavior:
   workspace activation, watchers, writer queues, artifact caches, semantic scheduling, shared
   telemetry, and the dashboard host.
2. The existing Native AOT `miller` binary remains the public executable. In normal operation its
   MCP stdio and CLI paths become thin adapters to the local service.
3. Registered workspaces remain visible machine-wide, but only **active or explicitly pinned**
   workspaces stay hot. Registration alone never starts a watcher, scan, vector build, or model
   download.
4. All workspace artifacts remain local under `<workspace>/.miller/`. There is no global symbol,
   content, or vector index.
5. The semantic sidecar remains a pinned child binary, not an independently installed daemon. The
   machine service owns one shared embedding session and schedules query and convergence work across
   hot workspaces.
6. The service is disposable runtime state. It never updates its own executable and is never
   installed as a Windows Service, launchd agent, or systemd unit by default.
7. Version upgrades use immutable, side-by-side package directories and graceful newest-compatible
   handoff. An older client never downgrades or restarts a newer compatible service.
8. Existing MCP tool names, parameter shapes, compact output, JSON contracts, CLI verbs, exit codes,
   artifact contracts, and local-first privacy rules remain the public compatibility surface.

## 3. Goals

- Keep one coordinated runtime per Miller home instead of one full runtime per agent session.
- Share the embedding model and inference resources across clients and workspaces.
- Give the dashboard, MCP, and CLI one truthful view of workspace state and background work.
- Keep registered-but-idle workspaces cold.
- Preserve per-workspace artifact isolation and existing rebuild/recovery contracts.
- Make service upgrades automatic, one-directional, observable, and safe on Windows.
- Ensure a service crash degrades availability but cannot corrupt an artifact or wedge a client
  indefinitely.
- Keep lifecycle, scheduling, and version negotiation behind a small local interface rather than
  spreading those obligations across every adapter.

## 4. Non-goals and boundaries

- **No implementation during the current semantic program.** That work may complete using its
  approved per-process lifecycle.
- **No global code index.** `symbols.db`, `search.db`, `content.db`, `vectors.db`, and `history.db`
  stay under each workspace.
- **No fleet semantics.** Cross-workspace ranking, embeddings-as-a-service, guidance/confidence
  views, and commercial orchestration remain outside Miller per ADR-0003.
- **No extraction ownership change.** `julie-extractors` / `julie-extract` still own parser-backed
  extraction.
- **No semantic-sidecar ownership change.** `julie-semantic-sidecar` still owns embedding
  generation and model acquisition.
- **No new MCP tool by default.** A new tool would still require explicit user approval.
- **No mandatory OS service installation.** User-level demand start is the default.
- **No self-update.** The running service never downloads, overwrites, or replaces its executable.
- **No always-hot registry.** Registered workspaces do not become background jobs merely because
  they exist.
- **No silent weakening of `MILLER_SEMANTIC=off`.** Shared-service configuration semantics must be
  approved before implementation.

## 5. Target architecture

```text
Agent harness             Shell / scripts                 Browser
     │                          │                             │
     ▼                          ▼                             ▼
Native AOT `miller`       Native AOT `miller`       Loopback HTTP
MCP stdio adapter         CLI adapter                       │
     │                          │                             │
     └──── versioned user-only local IPC ────┐               │
                                             │               │
                                             ▼               ▼
                                      non-AOT `Miller.Service`
                  ┌────────────────────────────┐
                  │ client/session bindings    │
                  │ machine registry/telemetry │
                  │ workspace activation       │
                  │ dashboard host             │
                  │ semantic scheduler/session │
                  │ service lifecycle/handoff  │
                  └──────────────┬─────────────┘
                                 │
                 ┌───────────────┼────────────────┐
                 ▼               ▼                ▼
          hot workspace A  hot workspace B  cold registry row
          watcher + queue   watcher + queue   no process resources
          artifact handles  artifact handles
                 │               │
                 ▼               ▼
          A/.miller/*.db    B/.miller/*.db
```

### 5.1 Process and project shape

The dashboard's ASP.NET Razor Components prevent the main executable from remaining Native AOT if
the dashboard is hosted in the same process. The recommended split is therefore:

- **`miller`** — the current Native AOT public executable, eventually thin in its normal MCP/CLI
  modes.
- **`Miller.Service`** — a new packaged, non-AOT executable that hosts the local control transport,
  workspace runtimes, semantic runtime, and dashboard.
- **`Miller.Dashboard`** — dashboard components, endpoints, assets, and read models consumed by the
  service host. Its standalone process entry is removed only after service-host parity is proven.
- **Shared application layer** — the caller-facing operation interface used by the in-process test
  adapter and service transport. Existing pure cores remain below this seam.

The service executable ships in the same release archive and reports the same Miller product
version. It locates the same packaged `.tools/julie-extract` and semantic-sidecar binaries as the
client package that started it.

### 5.2 Application interface

The internal application interface should be smaller than the behavior it unlocks. The proposed
transport has five capability families:

1. **Handshake** — protocol version, product version, build identity, executable identity, service
   state, and compatibility verdict.
2. **Execute tool** — existing MCP tool name, existing argument document, client context, request
   identity, and output format.
3. **Execute CLI command** — existing command/arguments plus stdout, stderr, and exit-code result.
4. **Client context** — register/update the client's roots and primary workspace; release them on
   disconnect.
5. **Service control** — status, graceful drain, and version handoff. This is an internal launcher
   surface, not a new MCP tool.

Business rules, rendering, telemetry classification, and artifact access stay in the application
layer. MCP, CLI, transport, and dashboard adapters translate only their native input/output shapes.
The local wire schema is independently versioned and is not the public MCP contract.

### 5.3 Client-scoped workspace context

The service must not have one process-global "current workspace." Each connected client has an
ephemeral context:

- a client/session identity;
- the roots reported by that harness;
- one primary root when the harness supplies one;
- any explicit `workspace_id` selector carried by a request;
- client-specific output and semantic participation preferences.

Omitted `workspace_id` resolves through that client's primary root. Explicit selectors continue to
resolve through the machine registry. Disconnecting a client removes only its binding; it does not
unregister the workspace.

### 5.4 Workspace activation model

A registered workspace has one of three runtime states:

- **Cold:** registry metadata only. No watcher, open artifact cache, scan, vector convergence, or
  model activation.
- **Active:** at least one connected client uses it, an explicit operation is running, or bounded
  background work triggered by that use remains. It owns one logical workspace runtime.
- **Pinned:** explicitly configured to remain active without a connected client.

The default transition is direct: when no client, explicit operation, pin, or pending bounded work
needs a workspace, it becomes cold. A reconnect debounce may be added only if load testing proves
that immediate cooling causes harmful churn.

Each hot workspace runtime owns:

- one watcher set;
- one coalescing mutation queue;
- one writer/leadership coordinator;
- artifact readers and revision-keyed caches;
- search/content/history/vector convergence state;
- workspace-local health and backpressure facts.

Registration does not imply activation. The service must be able to list all registered workspaces
without hydrating their full indexes, preserving the dashboard's current aggregate-read discipline.

### 5.5 Writer discipline

Central ownership simplifies normal operation but does not replace OS safety:

- Per-workspace writer locks remain authoritative.
- The service acquires a workspace's writer lock only while that workspace is hot and eligible.
- Version-aware leadership continues to prevent an older extractor from rewriting a newer
  artifact.
- A direct-mode client or a previous service version may coexist temporarily, but only the eligible
  lock holder may mutate an artifact.
- Full rebuild promotion, artifact identity, revision-file-change logs, and sidecar freshness
  contracts remain unchanged.

The service must not introduce a second durable work queue when the existing artifact already
contains the durable convergence log. In-memory scheduling may coalesce wakeups; recovery always
derives desired work from durable artifact state.

### 5.6 Semantic runtime ownership

After semantic integration lands, the service becomes the owner of:

- one lazy `SemanticEmbeddingSession` for the machine service;
- query-priority scheduling across connected clients;
- bounded background quotas across hot workspaces;
- per-workspace `VectorConvergeService` state;
- the semantic circuit breaker and health facts;
- model acquisition coordination and shared-cache visibility.

The service does not merge vector artifacts. Each workspace retains its own `vectors.db`, generation
identity, dual cursors, shadow rebuild, promote, rollback, and corruption recovery.

Moving convergence into a machine service changes the current foreign-workspace limitation: a hot
or pinned foreign workspace may converge vectors under its own logical runtime even when no MCP
process is rooted there. This is still local multi-workspace management, not fleet-level semantic
ranking.

### 5.7 Dashboard ownership

`Miller.Service` hosts the dashboard directly:

- one dashboard port and health endpoint per service;
- the existing machine registry and telemetry views;
- direct access to service-owned live workspace state instead of reconstructing it from unrelated
  processes;
- current ADR-0002 registry-lifecycle mutations through the same shared cores and protections;
- no new per-symbol semantic detail beyond the existing dashboard boundary.

The loopback browser surface remains separate from the user-only control transport. Dashboard HTTP
must not become the MCP/CLI control protocol.

## 6. Disposable lifecycle and update contract

Avoiding Julie's former daemon-update behavior is a load-bearing acceptance criterion, not later
hardening.

### 6.1 No installed daemon

The service is demand-started by the packaged `miller` launcher/client. It is not installed in an OS
service manager and does not require administrator privileges.

The service may remain resident while clients, pins, or bounded background work need it. An idle
exit policy can be added after real usage data, but it is not part of correctness.

### 6.2 Immutable versioned packages

Miller's plugin launcher already caches packages under a version and target-specific directory:

```text
~/.miller/plugin-cache/<version>/<target>/package/
```

The service runs from that immutable directory. A new Miller version installs beside the old one.
The launcher never overwrites, renames, or deletes the directory containing a running executable.
This is mandatory on Windows, where the executable and loaded assemblies may remain locked.

Old version directories are garbage-collected only after process-liveness checks prove they are not
in use. Cleanup failure is harmless and retryable.

### 6.3 Discovery and singleton authority

Use two layers with distinct jobs:

- A **kernel-held machine-service lock** under `~/.miller` is the singleton authority. The file
  persists; the OS releases the lock when the process exits or crashes.
- A **discovery record** is advisory metadata. It contains service version, protocol version,
  executable path and fingerprint, PID plus process-creation identity, endpoint, start time, and
  lifecycle state.

The endpoint is versioned and generation-specific:

- Unix: a user-only Unix-domain socket under `~/.miller`.
- Windows: a named pipe containing the Miller-home identity, protocol major, and service generation.

Clients read the discovery record and then validate the handshake. They never infer liveness from a
PID file alone. Stale discovery may be deleted only after the kernel lock and process identity prove
that no live owner matches it.

### 6.4 Compatibility policy

Compatibility is based on the local protocol, not exact Miller product-version equality.

Recommended policy:

- A newer service may serve an older client when their protocol major is compatible.
- A newer client may request an upward handoff from an older service.
- An older client never requests a downgrade and never restarts a newer service.
- An incompatible old client receives a bounded, actionable "client update required" error.
- A protocol-major upgrade replaces the active service through the same drain/handoff sequence;
  incompatible old clients do not start a competing singleton.

This policy remains marked **recommended, not yet approved** until the owner confirms that
same-protocol older clients should remain usable.

### 6.5 Graceful upward handoff

Upgrade sequence:

1. The client downloads/verifies its versioned package without touching the running package.
2. It connects to the current service and evaluates the handshake.
3. If the current service is compatible and at least as new, the client uses it.
4. If the client is newer, it starts the new service binary in handoff-wait mode using a new
   generation-specific endpoint.
5. The old service publishes `draining`, refuses new mutations with a retryable result, and finishes
   in-flight mutations within a bounded window.
6. The old service checkpoints/flushes owned state, releases per-workspace locks, closes the
   semantic child, then releases the machine-service lock.
7. The new service acquires the machine-service lock, re-derives work from durable artifacts,
   atomically publishes discovery, and reports ready.
8. Clients reconnect.

If the new process fails before taking ownership, the old service leaves draining state and keeps
serving. A healthy handoff never force-kills the old process.

### 6.6 Request completion during disconnects

No request may hang indefinitely because a service restarts, crashes, or drops its transport.

- Pending requests have deadlines.
- Read-only/idempotent requests may reconnect and retry after a compatible handoff.
- Mutating requests are never automatically replayed after a lost response.
- A mutation completes during the bounded drain or returns an explicit lost-connection /
  outcome-unknown result carrying its request identity.
- The adapter must resolve every pending MCP request when a connection closes.

The implementation plan must classify every service operation as read-only, idempotent mutation, or
non-replayable mutation before automatic retry exists.

### 6.7 Observability

Service status and doctor output must expose:

- client product/protocol version;
- service product/protocol version;
- service executable path and fingerprint;
- service PID, creation identity, endpoint, and lifecycle state;
- bundled extractor and semantic-sidecar versions;
- active/pinned/cold workspace counts;
- current handoff or compatibility error;
- semantic activation and model state;
- last clean/unclean shutdown fact.

Version probing must use a side-effect-free path. A `--version` command must never acquire the
singleton lock, bind a pipe, start the dashboard, or activate the semantic sidecar.

## 7. Windows-specific requirements

The Windows gate must prove all of the following:

- A running old service executable remains locked while a new version installs and starts from a
  different directory.
- Upgrade succeeds without deleting or replacing the running directory.
- `LockFileEx` contention, including raw `ERROR_LOCK_VIOLATION` code 33, is treated as a live lock.
- Named-pipe names are deterministic for discovery but generation-specific for handoff; a stale pipe
  never becomes proof of a live service.
- The pipe ACL permits only the current user.
- Process creation redirects or detaches standard handles deliberately; inherited stdin/stdout
  handles cannot keep a parent capture pipe alive or prevent EOF.
- A crash between package start, lock acquisition, and discovery publication recovers without a
  second active writer.
- Competing older/newer clients converge upward once and do not flap the service version.
- A failed new-version start leaves the old service usable.
- Every in-flight MCP request resolves during a handoff or crash.

## 8. Configuration ownership

A shared process cannot safely inherit whichever environment happened to start it first without an
explicit configuration contract.

Classify configuration into:

- **Machine-service settings:** artifact-writing behavior, semantic activation, model identity,
  sidecar enablement, resource budgets, dashboard port, and pinned workspaces.
- **Client/request settings:** output format, requested mode/arm, deadlines, compact limits, and a
  lexical-only request preference.
- **Package identity:** extractor/sidecar pins and protocol/product versions; never client-overridden
  after handshake.

### 8.1 `MILLER_SEMANTIC=off` blocking decision

The current contract says `MILLER_SEMANTIC=off` causes no model download, child process, vector
writes, GPU probe, or added latency. With a shared service, one client cannot guarantee that no
other client is using semantic work on the machine.

Recommended follow-up design:

- A durable machine-service semantic setting is authoritative for background work.
- A request may opt out of semantic participation without reconfiguring the service.
- A client environment that conflicts with the running service never silently changes or restarts
  it.
- `MILLER_SEMANTIC=off` plus an incompatible running service can use explicit direct lexical mode
  during migration, preserving zero semantic work attributable to that invocation.

This changes the scope of the existing environment guarantee and therefore requires explicit owner
approval plus an ADR amendment before implementation. Until then, the current semantic contract
remains authoritative.

The same audit must cover every environment flag that currently changes durable artifacts or
host-wide behavior. No first-client-wins configuration is permitted.

## 9. Security and privacy

- The control socket/pipe is current-user-only.
- The discovery record and lock live under the protected Miller home.
- The dashboard remains loopback-only and retains its current request/antiforgery protections.
- The service reuses `WorkspaceRootSafety` before registering or activating a root.
- Query and source content never leave the machine.
- Service logs and telemetry preserve the existing no-query-text rule.
- Client-supplied roots and selectors are canonicalized and resolved through existing registry and
  containment guards.

## 10. Failure and recovery model

- **Client dies:** its binding is removed; shared work continues only for other clients, pins, or
  bounded already-committed work.
- **Service dies:** OS locks release. A later client starts the newest compatible package and
  re-derives desired work from durable artifact state.
- **Semantic child dies:** the service circuit breaker degrades semantic participation to lexical;
  the service itself remains available.
- **One workspace fails:** that workspace becomes degraded; other workspaces and the dashboard
  remain available.
- **Artifact is corrupt:** existing per-artifact recovery applies. The service never deletes
  `symbols.db` to repair a derived sidecar.
- **Dashboard fails to render one workspace:** best-effort aggregate behavior remains; the service
  control plane does not fail.
- **Handoff fails:** the old service remains active when possible; discovery never points to a
  service that has not completed readiness.
- **Protocol mismatch:** fail bounded and actionable. Never restart repeatedly.

## 11. Alternatives considered

### Keep the current per-session topology

Lowest migration cost, but retains duplicated host graphs, watchers, caches, semantic children, and
fragmented background ownership. Idle unload reduces model residence but does not create a truthful
machine-wide scheduler. Rejected as the long-term target.

### Add only a shared semantic host

Reduces model duplication but creates a second coordinator while leaving the dashboard, indexers,
workspace activation, and caches distributed. Julie used this shape after tearing down its main
daemon because its embedding model required a singleton; Miller now has enough machine-wide state
that a semantic-only broker would likely be an intermediate architecture. Rejected as the final
target.

### Install an always-on OS service

Adds installer privileges, service-manager integration, separate update mechanics, and Windows
running-binary replacement problems. It also makes portable plugin installs harder. Rejected.

### Self-updating stable-path daemon

Recreates the failure class this design is meant to avoid: stale binaries, locked files, ambiguous
ownership, downgrade fights, and restart loops. Rejected.

### Move all artifacts into a global database

Increases failure blast radius, weakens workspace portability, complicates deletion/privacy, and is
unnecessary for shared scheduling. Rejected.

### Keep every registered workspace hot

Turns the registry into an uncontrolled fleet crawler and makes resource use grow with historical
registrations rather than current work. Rejected.

## 12. Architecture quality

**Affected modules:** MCP/CLI dispatch, host registration, bootstrap/freshness/indexer services,
workspace registry and providers, cross-workspace refresh, dashboard launch/host, plugin launcher,
packaging, logging/telemetry, and the semantic runtime after it lands.

**Caller-facing interface:** Existing MCP and CLI contracts remain unchanged. The new caller-facing
internal interface is the versioned local service protocol plus client-scoped workspace context.

**Depth/locality check:** Lifecycle, version negotiation, workspace activation, scheduling, and
recovery move behind the service interface. MCP, CLI, and dashboard callers no longer need to
understand locks, process discovery, artifact loading, or semantic-session ownership.

**Test surface:** Prove behavior through the application interface, then run the same contract suite
through an in-process adapter and the real local transport. Public byte/JSON/exit-code parity tests
remain the compatibility gate.

**Seams/adapters:** The seam is earned by three real callers: MCP, CLI, and dashboard. Production
local transport and in-process tests are the two adapters across the owned-service dependency.

**Rejected shortcuts:** Moving tool classes wholesale into a service host without an application
seam; using dashboard HTTP as the control protocol; replacing running binaries; PID-file singleton
authority; first-client-wins configuration; permanent dual implementations; globalizing workspace
artifacts.

**Architecture risk:** High. The design intentionally delays implementation until semantic behavior
and contracts are stable enough to preserve through the move.

## 13. Deferred rollout shape

This is not an implementation plan. A later plan should preserve these gates:

1. **Baseline after semantic GA:** measure process/RAM/GPU duplication, latency, workspace counts,
   semantic convergence, and Windows behavior.
2. **Application seam:** route current in-process MCP/CLI behavior through one application interface;
   prove lexical compact/JSON/CLI byte parity before adding IPC.
3. **Optional service transport:** package and start `Miller.Service`; run transport conformance and
   multi-client tests while current direct mode remains default.
4. **Workspace runtime ownership:** move activation, watchers, writer coordination, and caches into
   the service; preserve per-workspace locks and artifacts.
5. **Dashboard integration:** host dashboard components/endpoints in the service and remove the
   standalone dashboard launcher only after route and mutation parity.
6. **Semantic relocation:** move embedding-session and convergence scheduling ownership without
   changing sidecar/vector contracts or retrieval output.
7. **Versioned handoff:** enable side-by-side newest-compatible upgrades only after Windows and
   interrupted-request gates pass.
8. **Default thin clients:** switch packaged MCP/CLI launchers to service mode through a canary.
   Keep explicit direct mode through at least one stable release.
9. **Cleanup:** remove duplicated direct-host plumbing only after operational evidence decides
   whether direct mode remains a permanent recovery surface.

## 14. Acceptance criteria

### Public behavior

- [ ] Existing MCP tool names and parameter schemas are unchanged unless separately approved.
- [ ] Lexical-only compact and JSON output is byte-identical through direct and service paths.
- [ ] CLI stdout, stderr, and exit codes remain contract-compatible.
- [ ] No new MCP tool is introduced without explicit approval.
- [ ] Local-first privacy and sensitive-root guards remain intact.

### Workspace management

- [ ] All registered workspaces are listable without hydrating full indexes.
- [ ] Only active or pinned workspaces own watchers, caches, or background queues.
- [ ] Each hot workspace has exactly one logical runtime in the active service.
- [ ] Per-workspace artifacts and writer locks remain under the workspace.
- [ ] One workspace failure cannot make other workspace reads unavailable.

### Semantic behavior

- [ ] One service embedding session serves concurrent clients by default.
- [ ] Query-priority and minimum-background-quota behavior remains bounded and observable.
- [ ] `vectors.db` identity, cursor, shadow, promote, rollback, and corruption contracts are
      unchanged.
- [ ] Semantic failure never converts a lexical success into a tool error.
- [ ] The shared-service meaning of `MILLER_SEMANTIC=off` is explicitly approved before shipping.

### Lifecycle and upgrades

- [ ] The service never updates or overwrites its own executable.
- [ ] Versioned packages install side-by-side on every supported platform.
- [ ] A Windows upgrade succeeds while the old executable and assemblies remain locked.
- [ ] Older clients cannot downgrade or flap a newer compatible service.
- [ ] Failed handoff leaves the old service usable when it was healthy.
- [ ] Kernel locks, not PID/discovery files, enforce singleton ownership.
- [ ] Every pending MCP request resolves during disconnect, crash, or handoff.
- [ ] `miller version` remains side-effect-free.

### Verification

- [ ] Application behavior passes through both in-process and real-transport adapters.
- [ ] Multi-client scale tests cover concurrent reads, mutations, workspace activation, and
      semantic queries.
- [ ] Windows tests cover locked binaries, named pipes, error 33, inherited handles, stale
      discovery, crash recovery, and competing versions.
- [ ] Packaged smokes prove the client, service, dashboard assets, extractor, and semantic sidecar
      are version-aligned.
- [ ] Resource benchmarks show a material reduction versus equivalent per-session hosts without a
      meaningful regression in warm query latency.

## 15. Follow-up decisions before implementation planning

1. Approve protocol-based compatibility: older client → newer compatible service is allowed; older
   client → downgrade is forbidden.
2. Approve the machine-global configuration model and the revised scope of
   `MILLER_SEMANTIC=off`.
3. Decide whether workspace pinning needs a public CLI/dashboard control or begins as configuration
   only. A new MCP tool is not proposed.
4. Decide whether explicit direct mode remains a permanent recovery surface after the migration
   period.
5. Run a fresh doubt pass against the completed semantic implementation and live Windows packaging
   before writing the implementation plan.
