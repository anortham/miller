### Task 1: Freeze the broker lifecycle and transport contract

**Files:**
- Create: Miller `docs/contracts/semantic-broker-v1.md`
- Modify: Miller `docs/adr/ADR-0003-semantic-retrieval-ownership.md`
- Modify: Miller `docs/plans/2026-07-19-miller-semantic-integration-design.md`
- Modify: Miller `docs/plans/2026-07-21-semantic-production-readiness-repair-design.md`
- Modify: Miller `docs/README.md`
- Test: Miller `tests/Miller.Tests/Docs/SemanticBrokerContractTests.cs`

**Interfaces:**
- Consumes: frozen `julie.embedding.sidecar` v1 envelopes and `SemanticEncoderPin`.
- Produces: exact identity/hash algorithm, endpoint/lock layout, `broker` argv, owner lease, scheduling, security, OOM, compatibility, and fail-open rules used by every later task.

**Contract inputs:** Global Constraints and External API Grounding above.

**File ownership:** Miller contract, ADR/design/docs-map files only.

**Serialization required:** Yes.

**Dependency reason:** All implementation consumes this contract.

**Step 1: Write the failing contract guard**

```csharp
[Fact]
public void BrokerContract_LocksTheFailureProneLifecycleOut()
{
    string text = File.ReadAllText(RepoFile("docs/contracts/semantic-broker-v1.md"));
    Assert.Contains("stdin EOF", text, StringComparison.Ordinal);
    Assert.Contains("JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE", text, StringComparison.Ordinal);
    Assert.Contains("No PID file", text, StringComparison.Ordinal);
    Assert.Contains("No broker-initiated restart", text, StringComparison.Ordinal);
    Assert.Contains("PIPE_REJECT_REMOTE_CLIENTS", text, StringComparison.Ordinal);
    Assert.Contains("julie.semantic.broker|1|julie.embedding.sidecar|1|", text, StringComparison.Ordinal);
    Assert.Contains("shutdown closes only the requesting connection", text, StringComparison.Ordinal);
}
```

**Step 2: Run it to verify it fails**

Run: `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter FullyQualifiedName~SemanticBrokerContractTests`

Expected: FAIL because `semantic-broker-v1.md` does not exist.

**Step 3: Write the contract**

The contract must include this normative command and identity:

```text
julie-semantic-sidecar broker \
  --model <model-id> \
  --endpoint <uds-path-or-full-pipe-name> \
  --lock <model-service-lock-path> \
  --accelerator-lock <user-global-accelerator-lock-path>

identity_input = "julie.semantic.broker|1|julie.embedding.sidecar|1|" + model_id + "|" + model_sha256
identity = lowercase_hex(sha256(UTF8(identity_input)))[0..16]
```

It must say:

- Each IPC connection carries frozen protocol-v1 NDJSON, one request in flight per connection, and multiple connections per broker.
- Unix endpoints are absolute socket paths. Windows derives both `\\.\pipe\<name>` for `CreateNamedPipeW` and the short `<name>` for `NamedPipeClientStream`.
- `shutdown` preserves stdio process-stop behavior but closes only the requesting broker connection.
- The stdin watcher is armed before model load; only the service-lock holder may unlink a stale Unix endpoint.
- Owner disposal closes stdin/Job ownership; non-owner disposal closes only client connections.
- Spawn losers poll the endpoint through the full initialization budget.
- While a batch waits, at most eight interactive dequeues precede one batch dequeue.

**Step 4: Run the focused docs gate**

Expected: PASS with all lifecycle exclusions and exact literals present.

**Step 5: Apply commit mode**

`serial-worker-commit`: commit the owned documentation/tests after focused verification and record the SHA.

**Acceptance criteria:**
- [ ] Contract contains no PID, state, token, HTTP, port, workspace, DB, or self-update mechanism.
- [ ] Existing sidecar protocol remains frozen and separately referenced.
- [ ] ADR and historical design explicitly supersede process-local ownership with the approved broker.
- [ ] Focused contract guard passes.

