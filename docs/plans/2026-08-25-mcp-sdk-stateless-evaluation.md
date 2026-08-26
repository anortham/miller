# MCP SDK / Stateless MCP Evaluation

> **Status:** evaluation + phased plan. No code change is approved by this document. Phase 0 (build-only
> upgrade) and the Phase 2 spike each need explicit user approval before implementation.

**Goal:** Decide whether Miller should move from `ModelContextProtocol` 1.4.0 to the 2.x C# SDK, and whether
the new stateless-first protocol removes the per-session cost Miller pays under a gateway that launches one
`miller` child per agent session.

**Architecture:** Miller's MCP surface is one stdio process per client session. The expensive state is not in
the MCP transport — it is the pinned generation Miller loads into each process. A transport change alone
moves nothing; a shared read process is what would.

**Tech Stack:** .NET 10, `ModelContextProtocol` 1.4.0, Serilog, Native AOT publish for `miller`.

**Architecture Quality:**

- **Affected modules:** `Miller.Server` host wiring (`Program.cs`), the workspace binding path, and — for the
  spike only — `Miller.Dashboard` as an HTTP host.
- **Caller-facing interface:** no tool contract change. `ServerInstructions`, the ten tool descriptions, and
  every JSON contract stay byte-identical.
- **Depth/locality check:** the SDK upgrade is local to host registration. The shared-process question is a
  separate, larger change and is kept separate here on purpose.
- **Test surface:** `HostStartupRegistrationTests`, `AgentInstructionsTests`, the binding-filter tests, and
  the AOT publish gate in `release.yml`.
- **Seams/adapters:** the pure tool cores (`SearchTool.Run`, `InspectTool.Run`, …) are already transport
  independent — the CLI proves it. That seam is what makes an HTTP host cheap to try.
- **Rejected shortcuts:** upgrading to 2.x "for the stateless win" without a shared process; suppressing
  `MCP9005` globally instead of deciding what replaces roots.
- **Architecture risk:** medium. The protocol revision removes the server-initiated request Miller's deferred
  workspace binding depends on.

---

## 1. Current shape (from the code)

### 1.1 Package and host wiring

`src/Miller.Server/Miller.Server.csproj:35` pins `ModelContextProtocol` **1.4.0**. `Directory.Build.props:3`
sets `net10.0`; `Directory.Build.props:7` sets `TreatWarningsAsErrors` — every SDK warning is a build error.

`src/Miller.Server/Program.cs:116-139` is the whole MCP registration:

- `AddMcpServer` sets `ServerInfo` (`name: miller`, `MillerVersion.Current`) and `ServerInstructions` from the
  embedded `MILLER_AGENT_INSTRUCTIONS.md` (`Program.cs:119-122`).
- `WithStdioServerTransport()` — stdio only. Miller ships no HTTP MCP endpoint.
- Ten explicit `WithTools<T>()` calls, one per tool. Explicit registration, not assembly scanning, because of
  Native AOT.
- `WithRequestFilters` adds `WorkspaceBindingCallToolFilter` then `TelemetryCallToolFilter`.

`Program.cs:141-142` registers `WorkspaceRootsNotificationService` as a hosted service.

### 1.2 The `-- serve` branch

`Program.cs:31-41` branches before any filesystem touch. `CliDispatch.IsCliInvocation(args)` decides: no args
or `serve` falls through to the MCP host; any other verb runs one-shot and exits. `mcp-config.json` launches
`dotnet run --project src/Miller.Server -c Release -- serve`; the plugin manifest
(`.claude-plugin/plugin.json:22-29`) launches `node bin/miller-plugin-launcher.cjs`, which downloads or
reuses a cached release archive and spawns the `miller` binary.

### 1.3 The multi-process shape a gateway produces

stdio means the client launches the server as a subprocess. So:

- **One `miller` process per MCP client session.** A gateway such as Hermes that opens a session per agent
  gets one child per agent. There is no cross-session reuse at the transport layer, and none is possible
  over stdio.
- **Plus the one-shot CLI**, which is a separate short-lived process per verb.
- **Plus the CT daemon**, one family daemon per repo, launched only on an explicit `tests start`.
- Coordination between them is on disk, not in the transport: one indexer lock holder (leader), every other
  process a permanent reader; `scan-failure.json` for shared backoff; `BackgroundRefreshGate` for
  per-workspace refresh coalescing *inside* one process.

### 1.4 What each session process costs

- **Fact cache.** `MillerServiceRegistration.cs:212` registers `RevisionFactCacheStore` as a **singleton** —
  process-wide, not per-request. `RevisionFactCacheStore.cs:7` sets `DefaultByteBudget` to **256 MB**. A
  resident process keeps the whole pinned generation via `RevisionFactCache.Load`
  (`RevisionFactCache.cs:227`). CLAUDE.md records the cold whole-generation load at **~5s on this repo
  (1,785 files)**. `RevisionFactCache.LoadBounded` (`RevisionFactCache.cs:299`) exists, but is requested by
  name only from `WorkspaceReadSessionFactory.OpenForOneShotCli` — the one-shot CLI. A server session never
  takes it, by design: it has a store to reuse the load into.
- **Bootstrap grace.** `WorkspaceBindingCallToolFilter.cs:115-129` — the first tool call waits up to
  `MILLER_BOOTSTRAP_GRACE_SECONDS`, **default 5 seconds**, then returns an actionable not-ready result rather
  than blocking. Every session pays this window independently.
- **Semantic broker.** `SharedSemanticBrokerConnectionFactory` already dedupes across processes: sessions
  with the same broker/protocol/model identity share one broker process and one loaded model, and a
  user-global accelerator lease admits at most one owner. This cost is **already shared**; N sessions do not
  load N models.
- **Sidecars.** `search.db`, `content.db`, `vectors.db`, `ct.db` are on disk and opened read-only per
  process. Cheap to attach, not duplicated in memory.

So the honest per-session marginal cost is: one process, one ~5s whole-generation load, up to 256 MB of
resident facts, one 5s grace window, and a connection to an already-shared broker.

### 1.5 Where long-lived reader assumptions are load-bearing

These are not incidental — CLAUDE.md's server-host section makes them contracts:

1. **Version-aware leadership.** A process claims the indexer lock, or is a permanent reader with a stated
   reason. Yield/cooldown is a live negotiation between running processes. A process that exists only for
   the length of one HTTP request cannot hold or yield leadership.
2. **The resident fact cache is the whole point.** "Only the one-shot CLI reads reference facts bounded;
   every resident process loads the generation once." The bounded mode is deliberately *not* inferred from
   the absence of a store, because `EditTool`, `TestsCore`, and `ContinuousTestRevisionPoller` open
   store-less sessions inside resident processes.
3. **Deferred workspace binding needs a server-initiated request.**
   `WorkspaceBindingService.cs:144-145` calls `server.RequestRootsAsync(...)` — the server asks the client
   for its roots. `WorkspaceRootsNotificationService` registers a handler for
   `notifications/roots/list_changed` and marks the cached roots dirty. This is the fallback that makes a
   plugin launch with a bad global cwd work at all.
4. **Background services outlive any one call.** `FreshnessService`, the watcher, the presence monitor, the
   converge queue, and `BackgroundRefreshGate` all assume a resident host.

---

## 2. What the new SDK offers

Researched 2026-08-25. Versions and dates are from the live NuGet listing and the SDK release pages.

### 2.1 Versions

| Version | Published | Note |
| --- | --- | --- |
| 1.4.0 | 2026-06-04 | **Miller's current pin.** |
| 1.4.1 | 2026-07-09 | HTTP/SSE response-stream memory-leak fix. |
| 2.0.0 | 2026-07-28 | Stable alignment with MCP spec revision `2026-07-28`. |
| 2.1.0 | 2026-08-05 | Opt-in `subscriptions/listen` handler; HTTP fallback reliability. |
| 2.2.0 | 2026-08-13 | `HttpServerSessionMode` — hybrid stateful/stateless serving on one endpoint. |

Sources: [NuGet — ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol),
[csharp-sdk releases](https://github.com/modelcontextprotocol/csharp-sdk/releases),
[v2.0.0 notes](https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v2.0.0),
[v2.2.0 notes](https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v2.2.0),
[.NET Blog: Announcing v2.0](https://devblogs.microsoft.com/dotnet/announcing-v20-of-the-official-mcp-csharp-sdk/).

`ModelContextProtocol` 2.2.0 targets `net8.0` and `netstandard2.0`, with computed compatibility for `net9.0`
and `net10.0`. Miller's `net10.0` is fine.

### 2.2 Stateless semantics

The 2026-07-28 spec revision is **stateless-first at the protocol level**, not only at the HTTP transport
level:

- The `initialize` handshake is gone. Negotiation is **discovery-first**: a client probes `server/discover`
  and reads `supportedVersions`, falling back to `initialize` only for a legacy server
  ([spec: stdio, Backward Compatibility](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio)).
- **All protocol metadata moves into the message body** — protocol version, per-request client capabilities,
  and optional client identity live in `_meta.io.modelcontextprotocol/*`. Streamable HTTP mirrors those into
  headers; **stdio has no header layer at all** and carries everything inline
  ([spec: transports](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports)).
- **Servers do not initiate JSON-RPC requests.** The stdio binding states it directly: "The server **MUST
  NOT** write JSON-RPC *requests* to `stdout`." Server-to-client interaction is carried only in
  `InputRequiredResult` replies under Multi Round-Trip Requests (MRTR).
- `Mcp-Session-Id` and the mandatory handshake are eliminated for HTTP; any healthy instance can serve any
  call, so no sticky routing and no shared session store
  ([Ben Abt: MCP C# SDK 2.0](https://benjamin-abt.com/blog/2026/08/03/mcp-csharp-sdk-2/)).

### 2.3 SDK-side surface

- `HttpServerTransportOptions.Stateless` now defaults to **`true`**. Set `false` for legacy stateful
  behavior. v2.2.0 adds `HttpServerSessionMode` so one endpoint can serve both the 2025-11-25 and 2026-07-28
  revisions.
- **Roots, Sampling, and Logging are deprecated**, and their stable API surfaces emit **`MCP9005`** warnings:
  "The Roots feature is deprecated as of specification version 2026-07-28 and may be removed in a future
  version." They still compile and run for down-level compatibility — these are warnings, not removals.
- Tasks moved to `ModelContextProtocol.Extensions.Tasks` and is **not wire-compatible** with the 1.3.x–1.4.x
  preview. Miller does not use Tasks.
- Tool deserialization now **requires `inputSchema`**; omitting it throws instead of defaulting.
- OAuth: issuer validation per RFC 9207 and mandatory PKCE S256. Not applicable to a local stdio server.
- v1.2.0 already disabled legacy SSE endpoints by default (`EnableLegacySse` opts back in). Not applicable.
- The maintainers state **v2.0 is backward compatible**: existing v1 code continues to compile and run.

### 2.4 Does stdio benefit, or only HTTP?

**Mostly HTTP.** The stateless *win* — no session pinning, no sticky load balancing, any instance serves any
call, horizontal scale-out — is an HTTP-deployment win. Neither the .NET blog nor the SDK release notes
describe a stdio benefit, and both the announcement and the migration write-up discuss stateless purely in
HTTP terms.

What stdio does get from the revision is real but narrow:

- Protocol-level statelessness makes **process restart cheap by contract**: "Because the protocol is
  stateless, any in-flight requests are simply lost and the client can retry them against the fresh process."
- One fewer round trip at startup (no `initialize`), replaced by an optional `server/discover` probe.
- Per-request client capabilities and optional client identity in `_meta` — a gateway could identify which
  agent is calling **within one session**, which today it cannot.

What stdio does **not** get: any reduction in process count. One client session is still one child process.
The spec's stdio binding is unchanged on that point.

---

## 3. Fit analysis

### 3.1 What the SDK upgrade actually fixes

| Miller pain point | Does SDK 2.x address it? |
| --- | --- |
| N gateway sessions → N `miller` processes | **No.** stdio still means one subprocess per session. Only an HTTP endpoint changes this, and the SDK does not supply the shared process — Miller does. |
| ~5s whole-generation fact load per session process | **No.** This is Miller-internal. A stateless HTTP endpoint that spun a fresh scope per request would make it *worse*, not better, unless the load stays process-wide. |
| Up to 256 MB resident facts × N sessions | **No**, directly. **Yes**, indirectly: it removes the protocol obstacle to one shared process serving many clients. |
| 5s bootstrap grace paid per session | **No.** Miller-internal. A shared process pays it once. |
| Semantic model loaded per session | **Already solved** by `SharedSemanticBrokerConnectionFactory`. The SDK adds nothing. |
| Multi-client fan-in under a gateway | **Partly.** Stateless HTTP + `HttpServerSessionMode` is the supported way to let many clients hit one Miller. Per-request client identity in `_meta` gives the gateway attribution it does not have today. |
| Cold/warm path surprises | **No.** Miller's cold path is extraction, sidecar convergence, and the fact load — none of it transport-shaped. |
| Staying current with the ecosystem | **Yes.** 1.4.0 will negotiate down-level as clients adopt `server/discover`. That is a maintenance argument, not a performance one. |

### 3.2 The blunt version

**The stateless SDK does not solve Miller's per-session cost. A shared reader process would, and Miller could
build one today on 1.4.0 with a hand-rolled endpoint — the SDK just makes it supported and standard.**

The SDK upgrade and the shared-process change are **two separate decisions**, and conflating them is the main
risk this document exists to prevent. The SDK upgrade is a maintenance move worth doing on its own schedule.
The shared-process change is the one with the payoff and the one with the architectural cost.

### 3.3 What a shared process would actually have to solve

Before any spike claims value, note what the resident-process contracts imply for a multi-client Miller:

- Only **one** process can hold the indexer lock. A shared HTTP reader would either be the leader or a
  permanent reader — that is already a supported state, so this is tractable.
- The fact cache must stay **process-wide**, not per-request. Stateless at the protocol layer must not become
  stateless in the application layer, or every request pays the ~5s load. `RevisionFactCacheStore` is already
  a singleton, so the correct wiring is the wiring that already exists.
- Workspace binding cannot use `RequestRootsAsync` (see §4.2). A shared process serving many clients from
  different roots needs an explicit per-request workspace selector, which the tools **already accept**
  (`workspace_id`).
- Telemetry attribution (`TelemetryCallToolFilter`) would need the client identity from `_meta` rather than
  from "this process is this session".

---

## 4. Risks

### 4.1 stdio purity

Low risk, but non-negotiable. `Program.cs:20-22` states the rule and Serilog Console is on stderr. The 2026-07-28
stdio binding restates it: "The server **MUST NOT** write anything to its `stdout` that is not a valid MCP
message," and explicitly permits stderr for logging. Nothing in the upgrade changes Miller's obligation.
`StartupBreadcrumb` and `StartupFailureLog` already write to stderr only.

### 4.2 Roots — the real blocker

**This is the finding that matters.** Miller's deferred workspace binding calls
`server.RequestRootsAsync(...)` (`WorkspaceBindingService.cs:144-145`). Under 2026-07-28 the server cannot
initiate a JSON-RPC request at all, and the SDK marks Roots deprecated with `MCP9005`.

Two consequences:

1. **Build break.** `Directory.Build.props:7` sets `TreatWarningsAsErrors`. Upgrading to 2.x makes `MCP9005`
   a **build error** at `WorkspaceBindingService` and `WorkspaceRootsNotificationService`. This is a good
   outcome — the build refuses to let the deprecation pass silently — but it must be planned for, not
   discovered mid-upgrade. Suppressing `MCP9005` project-wide would hide exactly the decision that needs
   making.
2. **Functional gap on a modern client.** As clients adopt 2026-07-28, `roots/list` stops being available.
   Miller's deferred-binding fallback (plugin launched with a bad global cwd) then has env override and cwd
   only. Miller needs a replacement before that lands: an explicit `workspace_id` on the call, a launcher-set
   env var, or a first-call binding argument. The launcher already runs on every plugin start and knows the
   invoking directory, so a launcher-set `MILLER_WORKSPACE` is the cheapest candidate — it must be designed,
   not assumed.

### 4.3 ServerInstructions and the description budget

ADR-0001's budgets are Miller-side character limits enforced by `AgentInstructionsTests`; they are not SDK
behavior. The upgrade does not move them. But two things need checking on a real client:

- `ServerInstructions` still reaches the client identically under discovery-first negotiation (the field is
  carried in `DiscoverResult` rather than `InitializeResult`).
- Tool descriptions are unchanged in transport. The **≤1,900 char** core limit exists because Claude Code
  truncates at ~2KB per server; that is a client constraint and survives any SDK change.

Also: v2.0 makes `inputSchema` **required** on tool deserialization. Miller's ten tools are attribute-declared
and the SDK generates their schemas, so no tool should be affected — but the AOT publish is where a missing
schema would surface, so the gate belongs in Phase 0.

### 4.4 The binding filter

`WorkspaceBindingCallToolFilter` is registered through `WithRequestFilters(filters => filters.AddCallToolFilter(...))`
(`Program.cs:135-139`). Filter registration is a stable, non-deprecated 1.x API, so it should carry forward.
The filter's job — render `workspace` status when nothing is bound, because the real tool cannot construct —
is unaffected by transport. **But** it reads `request.Services?.GetService<McpServer>()` to call
`EnsurePrimaryBoundAsync(server, …)`. That coupling to a live `McpServer` for a roots request is the same
dependency §4.2 breaks. Fixing roots fixes this too.

### 4.5 Native AOT

Miller publishes `miller` with Native AOT (`Miller.Server.csproj:11-13` scopes the Serilog `IL2104`
suppression to `PublishAot`). The two known SDK AOT rules are already satisfied:

- **Use `WithTools<T>()`, not assembly scanning.** `Program.cs:125-134` uses ten explicit generic
  registrations. Correct already.
- **Provide a `JsonSerializerContext` for tool DTOs.** Miller has several
  (`ServerJson.cs:56`, `LeaderIdentityFile.cs:171`, and others).

The SDK does not publish an AOT compatibility statement for 2.x. **Treat AOT as unverified until a real
`dotnet publish -p:PublishAot=true` succeeds with 0 warnings.** This is the single highest-value cheap check
in the whole evaluation and belongs in Phase 0, because a new trim/AOT warning is a release-packaging
failure, not a runtime surprise.

### 4.6 Down-level and dashboard

- `ModelContextProtocol.Extensions.Tasks` is not wire-compatible with the 1.3.x–1.4.x preview. Miller does not
  use Tasks, so this is a non-issue — recorded so nobody re-checks it.
- `Miller.Dashboard` is already `Microsoft.NET.Sdk.Web` and self-contained/non-AOT (Razor Components do not
  support AOT). It is the only long-lived ASP.NET process Miller ships, which makes it the natural host for
  the Phase 2 spike and means the spike costs no new process type.

---

## 5. Recommendation and phased plan

### Recommendation

**Upgrade the SDK for maintenance. Do not expect stateless to fix the per-session cost — that needs a shared
reader process, which is a separate, larger decision.**

Ordered:

1. **Do Phase 0** (upgrade to 2.2.0, keep stdio, keep behavior identical). It is cheap, it surfaces the roots
   deprecation as a build error while there is time to answer it, and it keeps Miller negotiating with modern
   clients.
2. **Do Phase 1** (replace the roots dependency) — it is required by Phase 0's build break and is worth doing
   regardless, because roots is on a removal path.
3. **Spike Phase 2 behind a flag** before committing to anything larger. Measure before believing.
4. **Do not** ship a stateless HTTP endpoint as the default transport. stdio stays the shipped default; the
   plugin launcher, `mcp-config.json`, and every install path depend on it.

### Phase 0 — SDK upgrade, stdio unchanged (1 agent session)

Bump `ModelContextProtocol` to 2.2.0. Build, capture every `MCP*` diagnostic, and change nothing else.
Deliverable is a diagnostic inventory plus a green build, or a written list of what blocks it.

- Gate: `dotnet build Miller.slnx -c Release` at 0 warnings / 0 errors.
- Gate: `dotnet publish -p:PublishAot=true` for `Miller.Server` succeeds at 0 warnings. **This is the pass/fail
  for the whole upgrade.**
- Gate: `AgentInstructionsTests` and `HostStartupRegistrationTests` green; `ServerInstructions` verified
  reaching a real client unchanged.
- Expected to fail first: `MCP9005` at `WorkspaceBindingService` and `WorkspaceRootsNotificationService`.
  That failure is the Phase 1 trigger, not a reason to suppress.

### Phase 1 — Replace the roots dependency (1–2 agent sessions)

Design and ship a binding path that does not need a server-initiated request. Candidates, cheapest first:
launcher-set `MILLER_WORKSPACE` env var; explicit `workspace_id` on the first call; a first-call binding
argument. Keep `RequestRootsAsync` as a down-level fallback behind a version check for as long as it exists.

- Gate: a plugin launch from a bad global cwd still binds the right workspace, proved on a real client.
- Gate: the binding filter's unbound-`workspace` rendering is unchanged.
- Needs a user decision on which candidate; do not pick silently.

### Phase 2 — The narrowest spike worth running (1–2 agent sessions)

**One streamable-HTTP MCP endpoint, behind `MILLER_HTTP_MCP=<port>`, default off, hosted in
`Miller.Dashboard`, serving the four read tools (`search`, `inspect`, `context`, `trace`) from one shared
process, with `RevisionFactCacheStore` resolved as the existing singleton.**

Why this is the right spike:

- The dashboard is already an ASP.NET host and already reads workspace artifacts read-only. No new process
  type, no new packaging.
- The four read tools are already pure cores with no write path and no leadership requirement. A permanent
  reader is a supported state.
- Every tool already accepts a `workspace_id` selector, so multi-root multi-client is expressible without
  roots.
- Default-off behind a flag means stdio, the launcher, and every install path are untouched.

**Measure exactly three numbers, N = 1, 4, 8 concurrent clients:**

1. Wall time of the first `search` per client (does the shared process amortize the ~5s load?).
2. Total resident bytes across all Miller processes (does 256 MB × N collapse to 256 MB × 1?).
3. Steady-state warm `inspect` latency vs. the stdio baseline (does fan-in cost anything?).

**Kill criteria — abandon the spike if any holds:** the shared process does not amortize the fact load;
stateless request scoping forces a per-request load; or concurrent fan-in makes warm latency worse than N
separate processes. Record the numbers either way.

### Phase 3 — Conditional, not planned (estimate deferred)

Only if Phase 2's numbers justify it: a supported shared-reader mode, gateway integration, per-request client
identity for telemetry attribution, and a decision on whether the CLI should route through it. **Do not
estimate this before Phase 2 reports.** Anything here needs its own plan and its own approval, including the
MCP-surface rules in CLAUDE.md.

### Human time (separate from agent sessions)

- One decision on the Phase 1 binding replacement.
- One approval before Phase 2 touches code.
- One approval before any release carries the SDK bump.

---

## Sources

- [NuGet — ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol)
- [csharp-sdk releases](https://github.com/modelcontextprotocol/csharp-sdk/releases)
- [csharp-sdk v2.0.0 release notes](https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v2.0.0)
- [csharp-sdk v2.2.0 release notes](https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v2.2.0)
- [.NET Blog — Announcing v2.0 of the official MCP C# SDK](https://devblogs.microsoft.com/dotnet/announcing-v20-of-the-official-mcp-csharp-sdk/)
- [MCP spec 2026-07-28 — Transports](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports)
- [MCP spec 2026-07-28 — stdio binding](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio)
- [MCP blog — Beta SDKs for the 2026-07-28 spec RC](https://blog.modelcontextprotocol.io/posts/sdk-betas-2026-07-28/)
- [Ben Abt — MCP C# SDK 2.0: Stateless HTTP, Interactive Tools and a Practical Migration Path](https://benjamin-abt.com/blog/2026/08/03/mcp-csharp-sdk-2/)
