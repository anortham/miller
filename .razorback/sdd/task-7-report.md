# Task 7 report — shared semantic broker client

## State

- Worktree: `/Users/murphy/source/miller/.worktrees/shared-semantic-broker-plan`
- Branch: `codex/shared-semantic-broker-plan`
- Baseline/HEAD: `2befa4b48a5a3c7acf45a64c2ede6ec937ad9565`
- Sidecar dependency: `741850a`
- Commit/push: none
- Task brief updated from its stale prior-plan contents to the current shared-broker Task 7 contract.

## RED evidence

- Endpoint test first failed with CS0246/CS0103 because `SemanticBrokerEndpoint` did not exist.
- Factory tests first failed with CS0246 because `SharedSemanticBrokerConnectionFactory` and `WindowsBrokerJobAttachment` did not exist.
- The first real-process factory run failed 3/3 with a 10-second broker-connect timeout. Boundary diagnostics proved the test child wrote its load counter but stalled before socket bind because it performed async work from a module initializer. Replacing that harness with a normal console test host retained the real OS process/service-lock race.
- Host/CLI wiring tests then failed with CS1729 because `CliSemanticSession` did not yet accept `millerHome`; Off/On factory registration was also absent.

## Implementation

- Added deterministic endpoint identity, Unix socket/Windows pipe paths, service/accelerator locks, and exact broker argv.
- Added connect-first shared IPC with a 250 ms direct probe, instance spawn gate, retry, broker spawn, 120-second cold-start polling, owner/non-owner lifetime, reconnect/re-election, and a handshake-populated immutable snapshot.
- Added Windows kill-on-close Job Object attachment through NativeAOT-safe `LibraryImport`; attachment failure is visible while retained stdin remains authoritative.
- Server DI registers one lazy shared factory singleton only for enabled production semantics. Off and evaluation graphs do not register it.
- CLI creates one invocation-scoped factory only when a semantic arm can run and derives `millerHome` from the registry parent at that point.
- Production MCP/CLI no longer call `ProcessSemanticSidecarLauncher.ForServe`; stdio remains available for evaluation, package-smoke, conformance, and tests.
- Added a cross-platform real-process broker test host for cold-load concurrency, ownership, reconnect, Unix sockets, and Windows named pipes.

## GREEN evidence

- Endpoint/factory/DI/CLI focused gate: 8/8 passed.
- Broader semantic, host, CLI, and search-arm gate: 258/258 passed.
- Clean-helper focused factory rerun: 3/3 passed.
- `scripts/test.sh`: 5234 passed, 2 pre-existing skips, 0 failed; 26 seconds.
- `dotnet build Miller.slnx -c Release --no-restore`: 0 warnings, 0 errors.
- `git diff --check`: clean.
- Miller final impact completed for all production and test paths; the full fast suite covered the reported broad CLI/search/convergence blast radius.

## Acceptance

- PASS: production server has one process-local client factory and no per-session production model child.
- PASS: server factory is lazy singleton; CLI factory is invocation-scoped; no static global.
- PASS: eight independent factories converge on one model-loading broker and all handshake through a cold start.
- PASS: non-owner disposal leaves the broker running; owner disposal stops it; a survivor reconnects and re-elects.
- PASS: Job attachment is attempted before broker use; failure is surfaced and stdin EOF stops the owner.
- PASS: connection/spawn/handshake failures remain stated semantic failures consumed as lexical fallback, not host failures.
- PASS: semantic Off registers no factory and performs no broker/session/path/directory work.

## Remaining platform boundary

- Native Windows Job Object and named-pipe runtime execution was not available on this macOS host. The Windows P/Invoke compiled with warnings-as-errors, the injected attach-order/degradation seam passed, and the test host implements the Windows named-pipe protocol. A Windows CI/runtime pass must still prove real Job assignment and kill-on-close behavior.
- The broker-aware semantic Scale gate was not run because this worktree has no restored semantic runtime and its checked-in pin is still rc.4, which predates the broker contract. Task 9 owns the rc.5 restore/pin boundary.

## Post-Grok lifecycle review

Four Important lifecycle findings were validated and fixed:

1. A cold-load lock holder could die while every waiter only polled until timeout. RED: all eight handshakes were null after the 10-second test budget. The factory now performs bounded one-second re-election attempts inside the existing initialization budget. GREEN: the first holder exits during model load, one replacement loads, and all eight factories handshake in about one second.
2. An exited owner could be overwritten without retiring its process/stdin/Job handles. RED: the lifecycle test could not observe any retirement and initially failed to compile against the missing fact. `EnsureOwnerCandidateAsync` now clears and disposes an exited owner before replacement and snapshots `RetiredOwnerCount`. GREEN: the replacement has a new PID and exactly one retired owner.
3. Disposal did not serialize with active/waiting connects and disposed their semaphore underneath them. RED: owner-aware teardown returned in 228 ms while both connect tasks were still incomplete. Connects now hold an active-operation lease linked to factory lifetime; disposal marks/cancels, waits for all connects to drain, then cleans owned resources. The spawn semaphore is never disposed. GREEN: disposal waits for both tasks, both receive factory-scoped `ObjectDisposedException`, and the test completes in about 225 ms.
4. The old Windows assertion checked Unix socket absence and was vacuous. The test now captures the owner PID, proves that process exits, and then performs a platform-specific named-pipe or Unix-socket connection probe proving the endpoint is unavailable.

Post-review gates:

- Factory lifecycle suite: 6/6 passed.
- Broader semantic/host/CLI/search gate: 261/261 passed.
- One full-suite run exposed an unrelated parallel-order failure in `SearchToolRescueTests`; its isolated three-case rerun passed. The immediate full rerun passed 5234 with 2 pre-existing skips and 0 failures.
- Release build: 0 warnings, 0 errors.

## Architecture quality

- Affected module: `SharedSemanticBrokerConnectionFactory` and its real-process test host.
- Caller-facing interface: unchanged (`ConnectAsync`, `DisposeAsync`, immutable `Snapshot`).
- Locality: election, owner retirement, and connect/dispose serialization remain inside the factory.
- Test surface: public connect/dispose behavior plus broker PID/endpoint and immutable snapshot facts.
- Rejected shortcut: respawning on every 20 ms poll; bounded one-second election avoids a tight loser-spawn loop during healthy cold loads.
- Risk: medium because lifecycle concurrency changed; deterministic process tests and the full fast suite cover the seam.
