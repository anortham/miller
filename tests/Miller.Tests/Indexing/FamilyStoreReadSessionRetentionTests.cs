using System.Data;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Miller.Indexing.Reads;
using Miller.Indexing.Resolution;
using Miller.Indexing.Store;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class FamilyStoreReadSessionRetentionTests
{
    [Fact]
    public void FailureAfterNativeOpenIsTrackedBeforeCleanupCanFail()
    {
        using StoreFixture fixture = StoreFixture.Create();
        FailingCloseConnection? connection = null;
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        using IDisposable scope = StoreReaderRegistrationContext.Use(fixture.Binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((args, _) => fixture.ReaderReply(args)), registry,
                path => connection = new FailingCloseConnection(path, failOpen: true)));
        try
        {
            var error = Assert.Throws<FamilyStoreReadException>(() => FamilyStoreReadSession.Open(fixture.Binding));
            Assert.IsType<IOException>(error.InnerException);
            Assert.Equal("failure after native open", error.InnerException.Message);
            Assert.Equal(ConnectionState.Open, connection!.State);
            Assert.Equal(1, registry.Count);
            connection.FailClose = false;
            registry.Tick(DateTimeOffset.UtcNow.AddMinutes(1), TestContext.Current.CancellationToken);
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.Equal(0, registry.Count);
        }
        finally { if (connection is not null) { connection.FailClose = false; connection.Dispose(); } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CloseFailureKeepsPinUntilPositiveClosureAndAllowsRetry(bool scheduler)
    {
        using StoreFixture fixture = StoreFixture.Create();
        FailingCloseConnection? connection = null;
        int releases = 0;
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        using IDisposable scope = StoreReaderRegistrationContext.Use(fixture.Binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((args, _) =>
            {
                if (args[2] == "release") releases++;
                return fixture.ReaderReply(args);
            }), registry, path =>
            {
                connection = new FailingCloseConnection(path);
                connection.Open();
                return connection;
            }));
        var session = FamilyStoreReadSession.Open(fixture.Binding);
        Assert.Throws<IOException>(() => session.Dispose());
        try
        {
            Assert.Equal(ConnectionState.Open, connection!.State);
            Assert.Equal(0, releases);
            Assert.Equal(1, registry.Count);
            connection.FailClose = false;
            if (scheduler) registry.Tick(DateTimeOffset.UtcNow.AddMinutes(1), TestContext.Current.CancellationToken);
            else session.Dispose();
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.Equal(1, releases);
            Assert.Equal(0, registry.Count);
        }
        finally { connection!.FailClose = false; connection.Dispose(); session.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidationFailureKeepsItsErrorAndOwesUnknownConnectionClose(bool probe)
    {
        using StoreFixture fixture = StoreFixture.Create();
        FailingCloseConnection? connection = null;
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        using IDisposable scope = StoreReaderRegistrationContext.Use(fixture.Binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((args, _) =>
            {
                ReaderProcessResult reply = fixture.ReaderReply(args);
                if (args[2] == "acquire") Execute(fixture, "UPDATE views SET root='/wrong-root'");
                return reply;
            }), registry, path =>
            {
                connection = new FailingCloseConnection(path);
                connection.Open();
                return connection;
            }));
        try
        {
            var error = Assert.Throws<FamilyStoreReadException>(() =>
            {
                if (probe) _ = FamilyStoreReadSession.Probe(fixture.Binding);
                else using (FamilyStoreReadSession.Open(fixture.Binding)) { }
            });
            Assert.Equal(FamilyStoreReadFailure.ViewRootMismatch, error.Failure);
            Assert.Null(error.InnerException);
            Assert.Equal(ConnectionState.Open, connection!.State);
            Assert.Equal(1, registry.Count);
            connection.FailClose = false;
            registry.Tick(DateTimeOffset.UtcNow.AddMinutes(1), TestContext.Current.CancellationToken);
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.Equal(0, registry.Count);
        }
        finally { if (connection is not null) { connection.FailClose = false; connection.Dispose(); } }
    }

    [Fact]
    public async Task BackgroundCloseFailureKeepsItsConnectionAndPinOwnedAfterTaskFault()
    {
        using StoreFixture fixture = StoreFixture.Create();
        FailingCloseConnection? background = null;
        int opens = 0;
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        using IDisposable scope = StoreReaderRegistrationContext.Use(fixture.Binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((args, _) => fixture.ReaderReply(args)), registry, path =>
            {
                if (++opens == 1) return RecordingOpen(path, [], 1);
                background = new FailingCloseConnection(path);
                background.Open();
                return background;
            }));
        var session = FamilyStoreReadSession.Open(fixture.Binding, null, new RevisionFactCacheStore());
        try
        {
            await Assert.ThrowsAsync<IOException>(() => session.WarmResolutionFactsInBackground());
            session.Dispose();
            Assert.Equal(ConnectionState.Open, background!.State);
            Assert.Equal(1, registry.Count);
            background.FailClose = false;
            registry.Tick(DateTimeOffset.UtcNow.AddMinutes(1), TestContext.Current.CancellationToken);
            Assert.Equal(ConnectionState.Closed, background.State);
            Assert.Equal(0, registry.Count);
        }
        finally { if (background is not null) { background.FailClose = false; background.Dispose(); } session.Dispose(); }
    }

    private sealed class FailingCloseConnection(string path, bool failOpen = false)
        : SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False")
    {
        internal bool FailClose { get; set; } = true;
        public override void Open()
        {
            base.Open();
            if (failOpen) throw new IOException("failure after native open");
        }
        public override void Close()
        {
            if (FailClose) throw new IOException("close failed before native close");
            base.Close();
        }
    }

    [Theory]
    [InlineData(false, 44, 1)]
    [InlineData(false, 44, 44)]
    [InlineData(false, 2, 1)]
    [InlineData(true, 44, 1)]
    [InlineData(true, 44, 44)]
    [InlineData(true, 2, 1)]
    public void InternallyConsistentButMissingRetainedLogRowsRefuse(bool probe, long served, long floor)
    {
        using StoreFixture fixture = StoreFixture.Create();
        if (served == 2) Execute(fixture, "DELETE FROM store_log WHERE sequence=1");
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        using IDisposable scope = StoreReaderRegistrationContext.Use(fixture.Binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((args, _) =>
            {
                ReaderProcessResult reply = fixture.ReaderReply(args);
                return args[2] == "acquire" ? WithBounds(reply, served, floor) : reply;
            }), registry));
        var error = Assert.Throws<FamilyStoreReadException>(() =>
        {
            if (probe) _ = FamilyStoreReadSession.Probe(fixture.Binding);
            else using (FamilyStoreReadSession.Open(fixture.Binding)) { }
        });
        Assert.Equal(FamilyStoreReadFailure.Corrupt, error.Failure);
        Assert.Equal(0, registry.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroOrOtherViewRetainedBoundsDoNotReplaceThePerViewCursor(bool empty)
    {
        using StoreFixture fixture = StoreFixture.Create();
        if (empty) Execute(fixture, "DELETE FROM store_log");
        else Execute(fixture, "INSERT INTO store_log VALUES(10,'other','manifest_flipped','other-view',1,NULL,NULL,1,'{}','2026-09-05')");
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        using IDisposable scope = StoreReaderRegistrationContext.Use(fixture.Binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((args, _) =>
            {
                ReaderProcessResult reply = fixture.ReaderReply(args);
                if (args[2] == "acquire" && !empty)
                    Execute(fixture, "INSERT OR IGNORE INTO store_log VALUES(11,'new-other','manifest_flipped','other-view',2,NULL,NULL,1,'{}','2026-09-05')");
                return reply;
            }), registry));
        using var session = FamilyStoreReadSession.Open(fixture.Binding);
        Assert.Equal(empty ? 0 : 2, session.Snapshot.Freshness.Revision);
    }

    private static ReaderProcessResult WithBounds(ReaderProcessResult reply, long served, long floor)
    {
        JsonNode node = JsonNode.Parse(reply.StandardOutput)!;
        var snapshot = new StoreReaderSnapshot(node["family_id"]!.GetValue<string>(), node["view_id"]!.GetValue<string>(),
            node["generation_name"]!.GetValue<string>(), node["manifest_generation"]!.GetValue<long>(),
            node["store_instance_id"]!.GetValue<string>(), node["manifest_hash"]!.GetValue<string>(),
            node["extraction_identity_epoch"]!.GetValue<long>(), served, floor, 1, "");
        node["served_store_log_sequence"] = served;
        node["min_retained_store_log_sequence"] = floor;
        node["snapshot_fingerprint"] = snapshot.ComputeFingerprint();
        return reply with { StandardOutput = node.ToJsonString() };
    }

    [Fact]
    public void FreshnessProbeNeedsNoFileVersionOrFactTables()
    {
        using StoreFixture fixture = StoreFixture.Create();
        Execute(fixture, "DROP TABLE file_versions; DROP TABLE symbols; DROP TABLE structural_facts");
        Assert.Equal(2, FamilyStoreReadSession.Probe(fixture.Binding).Revision);
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("capacity_insufficient")]
    [InlineData("stale_snapshot")]
    public void TransientAdmissionRefusalIsNotAStoreCorruptionSignal(string failure)
    {
        using StoreFixture fixture = StoreFixture.Create();
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        using IDisposable scope = StoreReaderRegistrationContext.Use(fixture.Binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((_, _) => new(1,
                $$"""{"report_schema_version":1,"operation":"reader_acquire","state":"refused","failure_class":"{{failure}}"}""", "")), registry));
        Assert.Equal(FamilyStoreReadFailure.BindingNotReady,
            Assert.Throws<FamilyStoreReadException>(() => FamilyStoreReadSession.Open(fixture.Binding)).Failure);
    }

    [Fact]
    public void BoundedConstructionFailureClosesItsConnectionBeforeRetryOrSessionDispose()
    {
        using StoreFixture fixture = StoreFixture.Create();
        var events = new List<string>();
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        int opens = 0;
        using IDisposable scope = StoreReaderRegistrationContext.Use(fixture.Binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((args, _) =>
            {
                events.Add(args[2]);
                return fixture.ReaderReply(args);
            }), registry, path => RecordingOpen(path, events, ++opens)));
        using var session = FamilyStoreReadSession.Open(fixture.Binding, null, null, boundedFactsRequested: true);
        Execute(fixture, "DROP TABLE manifest_entries");
        Assert.Throws<SqliteException>(() => session.Resolution);
        Assert.Equal(new[] { "acquire", "open:1", "open:2", "close:2" }, events);
        session.Dispose();
        Assert.Equal(new[] { "acquire", "open:1", "open:2", "close:2", "close:1", "release" }, events);
    }

    [Fact]
    public void GenerationOpenFailurePreservesExceptionAndReleasesAdmission()
    {
        using StoreFixture fixture = StoreFixture.Create();
        var expected = new IOException("open failed");
        var events = new List<string>();
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        using IDisposable scope = StoreReaderRegistrationContext.Use(fixture.Binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((args, _) =>
            {
                events.Add(args[2]);
                return fixture.ReaderReply(args);
            }), registry, _ => throw expected));
        Assert.Same(expected, Assert.Throws<FamilyStoreReadException>(() => FamilyStoreReadSession.Open(fixture.Binding)).InnerException);
        Assert.Equal(new[] { "acquire", "release" }, events);
        Assert.Equal(0, registry.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackgroundCompletionOrFailureDoesNotLeaveAnExtraPinOwner(bool fails)
    {
        using StoreFixture fixture = StoreFixture.Create();
        int opens = 0;
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        using IDisposable scope = StoreReaderRegistrationContext.Use(fixture.Binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((args, _) => fixture.ReaderReply(args)), registry, path =>
            {
                if (++opens == 2 && fails) throw new IOException("warm failed");
                return RecordingOpen(path, [], opens);
            }));
        var session = FamilyStoreReadSession.Open(fixture.Binding, null, new RevisionFactCacheStore());
        if (fails)
            await Assert.ThrowsAsync<IOException>(() => session.WarmResolutionFactsInBackground());
        else
        {
            await session.WarmResolutionFactsInBackground();
            await session.WarmResolutionFactsInBackground();
            Assert.Equal(2, opens);
        }
        session.Dispose();
        Assert.Equal(0, registry.Count);
    }

    [Theory]
    [InlineData("UPDATE manifests SET manifest_hash='changed' WHERE generation=2")]
    [InlineData("UPDATE store_meta SET value='3' WHERE key='extraction_identity_epoch'")]
    [InlineData("UPDATE views SET root='/different-workspace'")]
    public void OpenIdentityFailureClosesBeforeOwedReleaseAndPreservesPrimaryError(string mutation)
    {
        using StoreFixture fixture = StoreFixture.Create();
        var events = new List<string>();
        bool releaseFails = true;
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        using IDisposable scope = StoreReaderRegistrationContext.Use(fixture.Binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((args, _) =>
            {
                events.Add(args[2]);
                if (args[2] == "release" && releaseFails)
                    return new ReaderProcessResult(null, "", "", TransportLost: true);
                ReaderProcessResult reply = fixture.ReaderReply(args);
                if (args[2] == "acquire") Execute(fixture, mutation);
                return reply;
            }), registry, path => RecordingOpen(path, events, 1)));
        FamilyStoreReadException error = Assert.Throws<FamilyStoreReadException>(() => FamilyStoreReadSession.Open(fixture.Binding));
        Assert.Null(error.InnerException);
        Assert.Equal(new[] { "acquire", "open:1", "close:1", "release" }, events);
        Assert.Equal(1, registry.Count);
        releaseFails = false;
        registry.Tick(DateTimeOffset.UtcNow.AddMinutes(1), TestContext.Current.CancellationToken);
        Assert.Equal(0, registry.Count);
        Assert.Equal("release", events[^1]);
    }

    [Fact]
    public void ChangedProducerGenerationNeverOpensAndKeepsOneExactOwedAcquire()
    {
        using StoreFixture fixture = StoreFixture.Create();
        var nonces = new List<string>();
        int opens = 0;
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        using IDisposable scope = StoreReaderRegistrationContext.Use(fixture.Binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((args, _) =>
            {
                nonces.Add(Argument(args, "--nonce"));
                ReaderProcessResult reply = fixture.ReaderReply(args);
                JsonNode node = JsonNode.Parse(reply.StandardOutput)!;
                node["generation_name"] = "gen-002";
                return reply with { StandardOutput = node.ToJsonString() };
            }), registry, path => { opens++; return RecordingOpen(path, [], 1); }));
        Assert.Throws<FamilyStoreReadException>(() => FamilyStoreReadSession.Open(fixture.Binding));
        Assert.Equal(0, opens);
        Assert.Equal(3, nonces.Count);
        Assert.Single(nonces.Distinct());
        Assert.Equal(1, registry.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingOrOldProducerRefusesBeforeAnyGenerationOpen(bool oldProducer)
    {
        using StoreFixture fixture = StoreFixture.Create();
        int opens = 0;
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        using IDisposable scope = StoreReaderRegistrationContext.Use(fixture.Binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((_, _) => oldProducer
                ? new(2, "", "unsupported reader subcommand")
                : throw new StoreReaderRegistrationException(ReaderFailure.Transport)), registry,
                path => { opens++; return RecordingOpen(path, [], 1); }));
        Assert.Throws<FamilyStoreReadException>(() => FamilyStoreReadSession.Open(fixture.Binding));
        Assert.Equal(0, opens);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void AdmittedRetiredGenerationSurvivesCurrentAndManifestPointerChanges()
    {
        using StoreFixture fixture = StoreFixture.Create();
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        using IDisposable scope = StoreReaderRegistrationContext.Use(fixture.Binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((args, _) =>
            {
                ReaderProcessResult reply = fixture.ReaderReply(args);
                if (args[2] == "acquire")
                {
                    Execute(fixture, "UPDATE views SET current_generation=1; UPDATE store_meta SET value='retired' WHERE key='generation_state'");
                    File.WriteAllText(Path.Combine(fixture.Binding.StoreRoot, "CURRENT"), "gen-999\n");
                }
                return reply;
            }), registry));
        using var session = FamilyStoreReadSession.Open(fixture.Binding);
        Assert.Equal("gen-001", session.Snapshot.GenerationName);
        Assert.Equal(2, session.Snapshot.ManifestGeneration);
        Assert.Equal("manifest-current", session.Snapshot.Freshness.ManifestHash);
        Assert.Equal("Visible", session.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM symbols";
            return command.ExecuteScalar();
        }));
    }

    [Fact]
    public void FreshnessProbeAcquiresAndClosesBeforeReleasing()
    {
        using StoreFixture fixture = StoreFixture.Create();
        int acquisitions = 0, releases = 0;
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        using IDisposable scope = StoreReaderRegistrationContext.Use(fixture.Binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((args, _) =>
            {
                if (args[2] == "acquire") acquisitions++;
                if (args[2] == "release") releases++;
                return fixture.ReaderReply(args);
            }), registry));
        Assert.Equal(2, FamilyStoreReadSession.Probe(fixture.Binding).Revision);
        Assert.Equal(1, acquisitions);
        Assert.Equal(1, releases);
    }

    [Fact]
    public void BoundedFactsConnectionClosesBeforeMainAndRelease()
    {
        using StoreFixture fixture = StoreFixture.Create();
        var events = new List<string>();
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        int opens = 0;
        using IDisposable scope = StoreReaderRegistrationContext.Use(fixture.Binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((args, _) =>
            {
                events.Add(args[2]);
                return fixture.ReaderReply(args);
            }), registry, path => RecordingOpen(path, events, ++opens)));
        var session = FamilyStoreReadSession.Open(fixture.Binding, null, null, boundedFactsRequested: true);
        _ = session.Resolution;
        session.Dispose();
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.Resolution);
        Assert.Equal(new[] { "acquire", "open:1", "open:2", "close:2", "close:1", "release" }, events);
    }

    [Fact]
    public async Task BackgroundWarmKeepsTheSamePinUntilItsConnectionCloses()
    {
        using StoreFixture fixture = StoreFixture.Create();
        var events = new List<string>();
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        int opens = 0;
        using IDisposable scope = StoreReaderRegistrationContext.Use(fixture.Binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((args, _) =>
            {
                lock (events) events.Add(args[2]);
                return fixture.ReaderReply(args);
            }), registry, path =>
            {
                int ordinal = Interlocked.Increment(ref opens);
                if (ordinal == 2)
                {
                    entered.Set();
                    if (!finish.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                }
                return RecordingOpen(path, events, ordinal);
            }));
        var session = FamilyStoreReadSession.Open(fixture.Binding, null, new RevisionFactCacheStore());
        Task warm = session.WarmResolutionFactsInBackground();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken), "Background load must use the session's retained connection factory.");
            Task shared = session.WarmResolutionFactsInBackground();
            Assert.Same(warm, shared);
            session.Dispose();
            Assert.Equal(1, registry.Count);
            Assert.DoesNotContain("release", events);
            Assert.Throws<ObjectDisposedException>(() => { _ = session.WarmResolutionFactsInBackground(); });
        }
        finally
        {
            finish.Set();
            await warm.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            session.Dispose();
        }
        Assert.Equal(new[] { "acquire", "open:1", "close:1", "open:2", "close:2", "release" }, events);
        Assert.Equal(0, registry.Count);
    }

    [Theory]
    [InlineData("2.40.0", true)]
    [InlineData("2.41.0", false)]
    public void ReaderCapabilityAcceptsImplementedFloorAndRefusesFutureFloor(string floor, bool accepted)
    {
        using StoreFixture fixture = StoreFixture.Create();
        Execute(fixture, $"UPDATE store_meta SET value='{floor}' WHERE key='min_reader_version'");
        if (accepted)
        {
            using var session = FamilyStoreReadSession.Open(fixture.Binding);
            Assert.Equal(2, session.Snapshot.ManifestGeneration);
        }
        else
            Assert.Equal(FamilyStoreReadFailure.ReaderFloorIncompatible,
                Assert.Throws<FamilyStoreReadException>(() => FamilyStoreReadSession.Open(fixture.Binding)).Failure);
    }

    private static SqliteConnection RecordingOpen(string path, List<string> events, int ordinal)
    {
        lock (events) events.Add("open:" + ordinal);
        var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.StateChange += (_, change) =>
        {
            if (change.CurrentState == ConnectionState.Closed)
                lock (events) events.Add("close:" + ordinal);
        };
        connection.Open();
        return connection;
    }

    private static void Execute(StoreFixture fixture, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(fixture.Binding.StoreRoot, "gen-001", "store.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void AdmissionPrecedesGenerationOpenAndReleaseFollowsConnectionClose()
    {
        using StoreFixture fixture = StoreFixture.Create();
        var events = new List<string>();
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        using IDisposable scope = StoreReaderRegistrationContext.Use(fixture.Binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((args, _) =>
            {
                events.Add(args[2]);
                Assert.Equal(fixture.Binding.StoreRoot, Argument(args, "--store"));
                if (args[2] == "acquire")
                {
                    Assert.Equal("view-a", Argument(args, "--view"));
                    Assert.Equal("gen-001", Argument(args, "--generation"));
                    Assert.Equal(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), Argument(args, "--owner-pid"));
                    Assert.Equal("120000", Argument(args, "--lease-ms"));
                }
                return fixture.ReaderReply(args);
            }), registry, path =>
            {
                events.Add("open:" + Path.GetFileName(Path.GetDirectoryName(path)));
                var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
                connection.StateChange += (_, change) =>
                {
                    if (change.CurrentState == ConnectionState.Closed) events.Add("close");
                };
                connection.Open();
                return connection;
            }));

        var session = FamilyStoreReadSession.Open(fixture.Binding);
        Assert.Equal(2, session.Snapshot.ManifestGeneration);
        session.Dispose();
        session.Dispose();
        Assert.Equal(new[] { "acquire", "open:gen-001", "close", "release" }, events);
        Assert.Equal(0, registry.Count);
    }

    private static string Argument(IReadOnlyList<string> args, string name) =>
        args[Array.IndexOf(args.ToArray(), name) + 1];
}
