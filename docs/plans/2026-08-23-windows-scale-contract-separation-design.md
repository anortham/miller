# Windows Scale Contract Separation Design

## Goal

Make the Windows Scale gate deterministic on CPU-only hosts without weakening Miller runtime contracts or treating accelerator throughput as broker-lifecycle correctness.

## Architecture Quality

**Affected modules:** the semantic broker probe script, its multiprocess Scale test, and the .NET provider Scale fixture.

**Caller-facing interface:** Miller.SemanticBrokerProbe gains an internal test-harness --health-only true|false option. Production Miller, the sidecar protocol, the sidecar queue, and public MCP/CLI surfaces do not change.

**Depth/locality check:** broker sharing remains in SemanticBrokerScaleTests; concurrent embedding throughput remains in semantic-broker-soak.*. The provider change stays inside its fixture startup/cleanup helpers.

**Test surface:** eight health-only probe processes must converge on one endpoint and one owner with zero failures or hangs. Existing soak validation continues to require zero failed/hung traffic requests. Provider cancellation tests still prove the real process-tree behavior after bounded readiness.

**Rejected shortcuts:** do not raise Miller production timeouts, raise the sidecar's fixed queue deadline, accept BrokerRequestExpired, reduce the process count, or disable semantic Scale coverage.

**Architecture risk:** low. Changes are test-harness-only and preserve every production boundary.

## Design

### Broker sharing versus throughput

BrokerProbe will parse --health-only as a strict Boolean. After a successful handshake it will hold the session for duration-seconds, emit the normal complete record with zero traffic counters, and exit. It will not enqueue query or batch work in this mode.

EightSameModelProcesses_LoadOneBrokerAndCompleteWithoutHangs will launch all eight probes in health-only mode. It will retain the existing endpoint identity, owner count, exit code, failure count, and hung count assertions. The separate semantic broker soak remains the batch-8 concurrency and recovery gate.

### Loaded-host provider readiness

WaitForPidFileAsync will use a 30-second startup deadline. Runner_cancel_kills_the_entire_process_tree will always cancel and observe runTask in finally, so readiness failures cannot strand an unobserved provider task or process tree.

## Files

- Modify scripts/Miller.SemanticBrokerProbe/Program.cs
- Modify tests/Miller.Tests/Indexing/SemanticBrokerScaleTests.cs
- Modify tests/Miller.Tests/Testing/Providers/Dotnet/DotnetProviderScaleTests.cs

## Acceptance Criteria

- [x] Probe parsing rejects invalid --health-only values.
- [x] Health-only probes handshake, remain alive for their duration, emit complete, and issue zero query/batch requests.
- [x] Eight same-model health-only processes report one endpoint identity, exactly one owner, zero failures, and zero hangs.
- [x] Batch-8 throughput and zero-failure requirements remain unchanged in the semantic broker soak.
- [x] Provider PID readiness allows 30 seconds and cancellation cleanup always observes runTask.
- [x] Focused broker and provider Scale tests pass on Linux.
- [x] Windows fast passes on the synced final SHA.
- [x] Windows Scale passes on the synced final SHA.
- [x] Linux Release build, fast suite, and Scale suite pass on the final source tree.

## Verification

- Worker: exact semantic broker Scale test and SemanticBrokerScaleTests.
- Worker: exact provider cancellation test and DotnetProviderScaleTests.
- Affected scope: both owning Scale classes.
- Branch gate: dotnet build Miller.slnx -c Release, scripts/test.sh, scripts/test.sh scale.
- Windows specialist gate: win-test run miller -- powershell -File scripts/test.ps1 scale.
- Security scope: none declared.
