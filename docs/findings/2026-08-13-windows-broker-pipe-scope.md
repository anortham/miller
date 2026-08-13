# Windows semantic-broker pipe scope — open question

- **Date:** 2026-08-13
- **Status:** Open. No code change shipped in v1.19.0.
- **Area:** `src/Miller.Indexing/Semantic/SemanticBrokerEndpoint.cs`, `docs/contracts/semantic-broker-v1.md`

## What we found

`SemanticBrokerEndpoint` puts the Miller home in the broker directory path. On Unix that is enough:
the service lock and the socket both live under `<miller-home>/semantic/`, so two homes get two
locks, two sockets, and two brokers.

Windows has no such path. The rendezvous is a named pipe, and the pipe namespace is machine-global.
The frozen contract fixes the pipe name as `miller-semantic-<identity>`, and `<identity>` hashes only
the protocol, the model id, and the model sha. The home is not in it.

So on Windows, two Miller processes with different `MILLER_HOME` values share one broker, while the
same pair on Unix gets two. Each process still writes its own lock file under its own home, so each
one believes it owns a broker that the other is really serving.

## Why nothing shipped in v1.19.0

1. `docs/contracts/semantic-broker-v1.md` is marked **Frozen** and states the exact layout, including
   `broker-<identity>.lock`, `broker-<identity>.sock`, and `miller-semantic-<identity>`. Changing the
   pipe alone is not enough — the lock elects the owner, so the lock has to move with it, and that
   rewrites the Unix names too. This is a contract amendment, not a bug fix.
2. Splitting the rendezvous splits a mixed-version rollout. Two Miller builds on one machine would
   load two models and compete for one accelerator lease. That is in tension with the CLAUDE.md
   invariant that concurrent sessions with the same broker/protocol/model identity share one broker
   and one loaded model.
3. The gate for any broker change is `scripts/semantic-broker-soak.ps1`, which is run by hand. CI
   never exercises the broker. A layout change with no soak run behind it is not releasable.

A change that moved the lock, the socket, and the pipe together was written and measured during this
release. It works, but items 1-3 above are release decisions, not implementation details.

## Cost today

Tests, not users. Three fast-suite tests and the whole `SemanticBrokerScaleTests` class cannot pass on
a machine that already runs a Miller plugin, because their probes attach to the live pipe as
non-owners. They now skip with an explicit reason instead of failing:

- `CliDispatchTests.WorkspaceStatus_UnsetSemanticReportsNotStartedWithoutCreatingBrokerState`
- `CliDispatchTests.WorkspaceStatus_DefaultOnAndShadowPassivelyObserveAnExistingSharedBroker` (x2)

For users, the shared broker is mostly benign: the model and the accelerator are the scarce
resources, and sharing them is the design goal. The real exposure is an operator who sets
`MILLER_HOME` to isolate two Miller installs and gets isolation everywhere except the broker.

## What would close it

1. Amend `docs/contracts/semantic-broker-v1.md` to v2 with a home-scoped rendezvous, and state the
   mixed-version behavior explicitly rather than leaving it implied.
2. Reconcile the CLAUDE.md shared-broker invariant with home scoping: decide whether the home is part
   of the broker identity or only part of its address.
3. Move the lock, the socket, and the pipe in one change. Moving the pipe alone deadlocks a
   version-skewed pair: the old build holds the lock the new build never watches for, so the new
   process re-elects until its 120-second budget is gone. This was measured at 120,710 ms and 107
   sidecar spawns against a 28 ms control.
4. Run `scripts/semantic-broker-soak.ps1` and record the result before release.
