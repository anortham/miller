# Semantic broker contract v1

**Status:** Frozen
**Contract owner:** Miller local semantic retrieval
**Wire dependency:** [`julie.embedding.sidecar` protocol v1](semantic-sidecar-protocol-v1.md)

This contract defines the user-local compute broker that shares one loaded embedding model across
concurrent Miller processes. It freezes process ownership, discovery, local transport, scheduling,
accelerator arbitration, and failure behavior. The broker remains a pure embedding compute
process; it is not a general Miller or Julie daemon.

## Activation and boundary

Miller evaluates semantic activation before deriving any broker path or touching any broker
resource. `MILLER_SEMANTIC=off` is a permanent zero work guarantee: no path derivation, directory
creation, lock attempt, endpoint probe, process launch, model access, accelerator probe, vector
write, or added request latency.

When semantics is enabled, the broker may perform only these frozen protocol-v1 methods:

- `health`
- `embed_query`
- `embed_batch`
- connection-scoped `shutdown`

The broker has no workspace awareness and performs no indexing, vector-artifact reads or writes,
file watching, model acquisition, or self-update work. Its exclusions are normative:

- No PID file or process registry.
- No state file or persisted ownership record.
- No bearer token, capability token, or authentication secret.
- No HTTP server, TCP listener, or port allocation.
- No workspace root, index lifecycle, watcher, or workspace registry.
- No DB connection or artifact ownership.
- No self-update mechanism.
- No broker-initiated restart of itself or another broker.

No new MCP tool is introduced. Broker failures remain internal semantic availability facts and
degrade Miller requests to lexical behavior. Releases remain approval-gated.

## Command

The normative launch form is:

```text
julie-semantic-sidecar broker \
  --model <model-id> \
  --endpoint <uds-path-or-full-pipe-name> \
  --lock <model-service-lock-path> \
  --accelerator-lock <user-global-accelerator-lock-path>
```

`--endpoint` receives the absolute Unix-domain-socket path on Unix and the full server pipe name
on Windows. The process inherits a readable owner-lease stdin handle. Broker mode does not detach.

## Identity

Miller obtains `model_id` and `model_sha256` from `SemanticEncoderPin`. The identity input and
derivation are exact:

```text
identity_input = "julie.semantic.broker|1|julie.embedding.sidecar|1|" + model_id + "|" + model_sha256
identity = lowercase_hex(sha256(UTF8(identity_input)))[0..16]
```

The sidecar binary version is deliberately excluded. Broker-contract version, frozen wire
protocol, model identity, and model content are sufficient to select a compatible broker.
A client must validate `health` before use. A version, model, fingerprint, or capability mismatch
fails that connection and degrades the caller to lexical behavior; it never restarts or replaces
the shared process.

## Discovery and service locks

`<miller-home>` is the directory containing Miller's user-global registry. All paths are derived
only after semantics is known to be enabled.

The v1 layout is flat and exact:

```text
Unix directory:        <miller-home>/semantic/
Model service lock:    <miller-home>/semantic/broker-<identity>.lock
Unix endpoint:         <miller-home>/semantic/broker-<identity>.sock
Accelerator lock:      <miller-home>/semantic/accelerator-v1.lock
Windows short pipe:    miller-semantic-<identity>
Windows server pipe:   \\.\pipe\miller-semantic-<identity>
```

The spawning Miller factory is the owner: it retains the owner stdin lease and, on Windows, the
Job Object ownership handle. The sidecar process is the service broker. A Miller factory probes
the endpoint first, then starts a service-broker contender only when it cannot connect. The
contender that acquires the model service lock becomes the service-lock holder and may load the
model. The service broker holds the model service lock and, when accelerated, the
user-global accelerator lock; the spawning Miller factory retains the owner stdin lease. Spawn
losers do not start another model and do not fail immediately: they poll the endpoint through the
full 120-second initialization budget.

Only the service-lock holder may unlink a stale Unix endpoint, and it does so before binding.
No non-holder may infer ownership from endpoint age, a process lookup, or a persisted record.

There is one user-global accelerator lock, independent of model identity. Its holder may load an
accelerated engine. A broker that does not acquire it loads CPU directly, so two model identities
cannot overcommit the same accelerator by racing model initialization.

## Unix transport and permissions

The Unix endpoint is an absolute Unix domain socket path. Before binding, the service-lock holder
ensures `<miller-home>/semantic/` has mode `0700`. After binding, it ensures the socket has mode
`0600`. A permission failure prevents broker readiness; it does not relax permissions.

The socket is local to the current user. The service-lock holder removes its socket on orderly
shutdown. Crash residue is removed only by the next service-lock holder.

## Windows transport and ownership

Windows derives both the full server form `\\.\pipe\<name>` and the short client form `<name>`.
The server creates `\\.\pipe\miller-semantic-<identity>` with `CreateNamedPipeW`. The .NET client
passes the short `miller-semantic-<identity>` name to `NamedPipeClientStream`.
Implementations must not interchange the full server name and short client name.

The pipe uses a current-user ACL, `PIPE_REJECT_REMOTE_CLIENTS`, byte mode, and overlapped handles.
Accept, read, and write operations use cancellable I/O; cancellation and deadlines must be able
to break a stalled connection without terminating the broker.

The owner Miller process attempts to attach the broker to a Windows Job Object with
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` before the broker is considered usable. An attach failure
does not silently pretend the guarantee exists: ownership becomes visibly degraded and the
owner-stdin lease remains authoritative.

## Owner lease and disposal

The service broker arms its watcher for the owner's stdin before model load. Owner stdin EOF is
authoritative: stdin EOF must terminate the broker even while model load is blocked.
Cooperative cancellation is preferred. When engine load cannot be cancelled,
process-fatal exit is permitted so the OS releases the model service and accelerator locks and no
orphan broker remains. This lifetime rule requires no process polling and covers normal owner
exit, crash, and terminal teardown.

Owner disposal closes its client connections and then closes the inherited stdin lease. On
Windows it also closes its Job Object ownership handle. Non-owner disposal closes only that
Miller process's client connections; it never closes the owner lease, service lock, endpoint, or
Job Object.

If the owner dies, existing connections close and another Miller process may recover ownership
through the service-lock protocol. Clients do not kill an old process and do not use a PID-based
reaper.

## Wire behavior

Each IPC connection carries the frozen `julie.embedding.sidecar` protocol-v1 NDJSON envelopes
without additions or reinterpretation. There is one request in flight per connection and
multiple connections per broker. Request IDs, error envelopes, limits, health fields, prompt
policy, and embedding normalization remain governed by the separately frozen sidecar protocol.

In stdio `serve` mode, `shutdown` retains its existing process-loop stop behavior. In broker mode,
shutdown closes only the requesting connection after returning its response. It does not stop
the broker, close other connections, release ownership, or unload the model.

Normal Miller disposal closes the transport and does not send `shutdown`.

## Scheduling and deadlines

The broker has one engine-owning scheduler because the embedding engine is not concurrently
entered. Requests are classified as interactive (`health`, `embed_query`) or batch
(`embed_batch`).

- The admitted queue capacity is 64 requests across all connections.
- A full queue rejects new work with the existing protocol-v1 `internal_error` envelope; it does
  not grow without bound. No new method, field, or error code is introduced for saturation.
- While a batch request waits, at most eight interactive dequeues occur before one batch dequeue.
- When no batch waits, interactive work may continue without artificial batch slots.
- A 60-second active-request watchdog terminates a wedged broker so ownership can recover.
- Miller's client request deadline remains 30 seconds.
- Connection attempts are individually bounded and repeat within the full initialization budget.

The watchdog is process-fatal because a wedged engine cannot be proven reusable. Ordinary
request cancellation or application failure closes or fails only the affected request or
connection.

## Accelerator exhaustion

Accelerator selection is guarded by the user-global accelerator lock. Initial accelerated-engine
failure may fall back to CPU before the endpoint becomes ready.

After readiness, only a typed, proven `ResourceExhausted` engine failure demotes the accelerated
engine to CPU. `ContextAlloc` is the initial classified exhaustion source. The scheduler replaces
the engine with CPU and retries the failed request once. The accelerator lock is released as part
of demotion.

Ordinary `Decode`, `Encode`, validation, protocol, cancellation, and application failures do not
demote the engine and are not retried as out-of-memory failures. A second
`ResourceExhausted` result after the CPU retry is returned as an application error.

## Compatibility and fail-open rules

- Broker v1 consumes, but does not revise, the frozen sidecar protocol v1.
- Binary upgrades do not change broker identity and never evict a compatible live broker.
- A client accepts a broker only after matching protocol, model ID, model sha256, dimensions,
  prompt policy, and required health capabilities.
- A missing, starting, saturated, incompatible, timed-out, disconnected, or failed broker causes
  a typed semantic-unavailable result and lexical fallback.
- Production Miller has no hidden per-process stdio fallback. Falling back to another model
  process would defeat the single-model ownership guarantee.
- Broker diagnostics, logs, and telemetry never record query text, document text, source text,
  workspace paths, symbols, snippets, vectors, or authentication material.
- Broker adoption does not change vector-artifact ownership: Miller continues to own
  `<workspace>/.miller/vectors.db`.

This contract can gain clarifying text without changing behavior. Any change to identity,
discovery paths, ownership, wire behavior, scheduling ratios, security, or failure policy
requires a new broker contract version and compatibility review.
