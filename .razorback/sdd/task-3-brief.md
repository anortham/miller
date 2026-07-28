### Task 3: Make Miller semantic sessions connection-factory based

**Files:**
- Modify: Miller `src/Miller.Indexing/Semantic/SemanticEmbeddingSession.cs:60-1097`
- Modify: Miller `src/Miller.Indexing/Semantic/SemanticEvaluationAdapter.cs`
- Modify: Miller `scripts/Miller.PackageSemanticSmoke/PackageSemanticSmoke.cs`
- Modify: Miller `tests/Miller.Tests/Support/FakeSemanticSidecar.cs`
- Modify: Miller `tests/Miller.Tests/Indexing/SemanticEmbeddingSessionTests.cs`
- Modify: Miller `tests/Miller.Tests/Indexing/SemanticEmbeddingSessionBrokerTests.cs`
- Modify: Miller `tests/Miller.Tests/Indexing/SemanticEvaluationAdapterTests.cs`

**Interfaces:**
- Consumes: existing session retry/circuit/handshake behavior.
- Produces: async, transport-neutral `ISemanticSidecarConnectionFactory` and `ISemanticSidecarConnection`; `StdioSemanticSidecarConnectionFactory` preserves current process behavior.

**Contract inputs:** Task 1 client-disposal and deadline rules.

**File ownership:** Miller session abstractions and existing fake/session tests only.

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**Step 1: Write failing async-factory tests**

```csharp
[Fact]
public async Task TransportFailure_AbortsOnlyTheConnectionThenReconnectsThroughTheFactory()
{
    var factory = new SequencedConnectionFactory(faultedConnection, healthyConnection);
    await using var session = new SemanticEmbeddingSession(factory, expectedEncoder: Pin);
    Assert.True((await session.EmbedQueryAsync("natural language")).Success);
    Assert.Equal(2, factory.ConnectCount);
    Assert.True(faultedConnection.Aborted);
}
```

**Step 2: Verify red**

Run: `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter FullyQualifiedName~SemanticEmbeddingSessionTests`

**Step 3: Replace process-specific interfaces**

```csharp
public interface ISemanticSidecarConnectionFactory : IAsyncDisposable
{
    ValueTask<ISemanticSidecarConnection> ConnectAsync(CancellationToken cancellationToken);
}

public interface ISemanticSidecarConnection : IAsyncDisposable
{
    TextWriter Input { get; }
    TextReader Output { get; }
    bool IsClosed { get; }
    void Abort();
}
```

`SemanticEmbeddingSession.StartIfNeededAsync` awaits `ConnectAsync`. Fatal recovery aborts only the connection. Session disposal always closes its connection. It disposes the factory only when constructed with explicit factory ownership for stdio/evaluation/test use; production broker sessions borrow the server/CLI-owned factory and never dispose it. The server host disposes its DI singleton, while `CliSemanticSession` disposes its invocation-wide factory after disposing the session.

**Step 4: Run focused and Miller fast gates**

Run the three semantic session test classes, then `scripts/test.sh`.

**Step 5: Apply commit mode**

`parallel-lead-commit`: hand the verified diff to the lead; do not commit from this lane.

**Acceptance criteria:**
- [ ] All existing retry, circuit, handshake, application-error, timeout, and byte-identity tests remain green.
- [ ] A connection factory may represent either a child process or shared IPC without session branching.
- [ ] Session disposal cannot tear down a borrowed shared factory or broker owner lease.
- [ ] Disposal has no implicit global `shutdown`.

