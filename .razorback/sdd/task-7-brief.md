# Task 7 — Connect, spawn, and supervise the broker from Miller

## Worktree

- Repository: `/Users/murphy/source/miller/.worktrees/shared-semantic-broker-plan`
- Branch: `codex/shared-semantic-broker-plan`
- Baseline: `2befa4b4`
- Sidecar dependency: local `codex/shared-semantic-broker` commit `741850a`
- Keep all edits in this Miller worktree. Do not commit or push.

## Goal

Replace production MCP/CLI per-session stdio model children with one lazy, process-local client factory that connects to the deterministic shared broker first, spawns only when needed, retains explicit owner lifetime, reconnects after transport loss, and preserves semantic Off as a strict zero-work guarantee.

## Required files

- Create `src/Miller.Indexing/Semantic/SemanticBrokerEndpoint.cs`
- Create `src/Miller.Indexing/Semantic/SharedSemanticBrokerConnectionFactory.cs`
- Create `src/Miller.Indexing/Semantic/WindowsBrokerJob.cs`
- Modify `src/Miller.Indexing/Semantic/SemanticEmbeddingSession.cs`
- Modify `src/Miller.Indexing/Semantic/SemanticSearchArm.cs`
- Modify `src/Miller.Indexing/Semantic/SemanticEmbeddingSessionBroker.cs`
- Modify `src/Miller.Server/Hosting/MillerServiceRegistration.cs`
- Modify `src/Miller.Server/Cli/CliDispatch.cs`
- Add endpoint, connection-factory, semantic-Off, host-registration, and CLI tests in the Task 7 plan.

## Frozen contracts

- `Miller.Indexing` receives pure `millerHome` and `toolsRoot` strings and must not reference Server-layer `WorkspaceContext`.
- Server/CLI derive `millerHome` from the parent of `WorkspaceContext.RegistryDbPath`.
- The executable remains `SemanticSidecarLayout.ExecutablePath(toolsRoot)`.
- Endpoint identity and broker argv must match the frozen Task 1 contract exactly.
- Implement `SharedSemanticBrokerConnectionFactory : ISemanticSidecarConnectionFactory` with:
  - `ValueTask<ISemanticSidecarConnection> ConnectAsync(CancellationToken)`
  - `SemanticBrokerSnapshot Snapshot`
- Connection order:
  1. direct connect with a 250 ms budget;
  2. enter a process-local spawn gate;
  3. retry direct connect;
  4. spawn `broker` with the exact Task 1 argv and redirected stdin;
  5. on Windows attach the child to a kill-on-close Job Object before broker use;
  6. poll and handshake through the existing 120-second initialization budget.
- Losing children may exit on the sidecar service lock. Their factories must continue polling the deterministic endpoint until the winner handshakes.
- The owner factory retains `Process`, stdin, and Windows Job handle. Owner disposal closes stdin then the Job handle. A non-owner closes only its client connections.
- Job attachment failure is recorded as degraded ownership in `SemanticBrokerSnapshot`; stdin EOF remains authoritative.
- A transport failure closes only that client and re-enters connect-first logic.
- Windows client: `NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous)` with cancellable timeout.
- Unix client: `Socket(AddressFamily.Unix, ...)` with cancellation.
- Server DI registers one lazy singleton factory and creates it only on first semantic use.
- One-shot CLI owns one invocation-wide factory inside `CliSemanticSession`.
- No static global.
- Production MCP/CLI must not call `ProcessSemanticSidecarLauncher.ForServe`; stdio remains for explicit evaluation, conformance, package-smoke, and tests.
- Missing, incompatible, or failed broker must yield lexical outcomes, never MCP failures.
- `MILLER_SEMANTIC=off` must not construct the factory, derive an endpoint, create `semantic/`, resolve workspace/tools/home, connect, or launch.

## TDD and verification

1. Use Miller search/context/inspect before reading and Miller impact before edits.
2. Add failing tests first and record exact RED evidence:
   - deterministic endpoint identity;
   - eight factories converge and handshake;
   - cold-load loser polling;
   - owner versus non-owner disposal;
   - reconnect/re-election after owner death;
   - Windows Job attach and degraded ownership seam;
   - Off host/CLI path proves no path/factory/directory/connection/launch work.
3. Implement the smallest production design satisfying the frozen contracts.
4. Run focused tests, `scripts/test.sh`, `dotnet build Miller.slnx -c Release`, and the narrow semantic Scale class only if the restored sidecar candidate supports the broker contract.
5. Run Miller impact on the final diff and `git diff --check`.
6. Write `.razorback/sdd/task-7-report.md` with path, branch, baseline, dirty state, RED/GREEN evidence, design decisions, exact acceptance-criteria result, and remaining Windows runtime boundary.
7. Do not commit or push. The lead will review the diff with Grok and commit after fixes.

## Acceptance

- Production DI has one process-local client broker and no process-local model child.
- Server DI owns one lazy factory singleton; CLI owns one invocation-wide factory.
- Eight independent factories share one same-model broker.
- Spawn losers wait through cold initialization and handshake.
- Owner disposal terminates its broker; non-owner disposal does not.
- Windows Job attach happens before broker use; failure is visible and stdin EOF remains authoritative.
- Owner death releases the broker; surviving clients reconnect/re-elect within 30 seconds.
- Broker failures remain lexical fallbacks.
- Semantic Off performs zero semantic broker work.
