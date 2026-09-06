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
    public async Task JoiningAFailedWarmDoesNotClaimAnotherSessionsUnclosedConnection()
    {
        using StoreFixture fixture = StoreFixture.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finish = new ManualResetEventSlim();
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        FailingCloseConnection? failed = null;
        int opens = 0;
        using IDisposable scope = StoreReaderRegistrationContext.Use(fixture.Binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((args, _) => fixture.ReaderReply(args)), registry, path =>
            {
                int ordinal = Interlocked.Increment(ref opens);
                if (ordinal != 3) return RecordingOpen(path, [], ordinal);
                entered.SetResult();
                if (!finish.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)) throw new TimeoutException();
                return failed = new FailingCloseConnection(path);
            }));
        var cache = new RevisionFactCacheStore();
        using var owner = FamilyStoreReadSession.Open(fixture.Binding, null, cache);
        using var joining = FamilyStoreReadSession.Open(fixture.Binding, null, cache);
        Task original = owner.WarmResolutionFactsInBackground();
        Task shared = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            shared = joining.WarmResolutionFactsInBackground();
            finish.Set();
            await Assert.ThrowsAsync<IOException>(() => original);
            await Assert.ThrowsAsync<IOException>(() => shared);
            Assert.Equal(3, opens);
            Assert.Equal(ConnectionState.Open, failed!.State);
            await joining.WarmResolutionFactsInBackground();
            Assert.Equal(4, opens);
            Assert.True(joining.ResolutionFactsWarm);
            joining.Dispose();
            Assert.Equal(1, registry.Count);
            owner.Dispose();
            Assert.Equal(1, registry.Count);
            Assert.Equal(ConnectionState.Open, failed.State);
            failed.FailClose = false;
            registry.Tick(DateTimeOffset.UtcNow.AddMinutes(1), TestContext.Current.CancellationToken);
            Assert.Equal(ConnectionState.Closed, failed.State);
            Assert.Equal(0, registry.Count);
        }
        finally
        {
            finish.Set();
            try { await Task.WhenAll(original, shared); } catch { }
            if (failed is not null) { failed.FailClose = false; failed.Dispose(); }
            owner.Dispose();
        }
    }

    [Fact]
    public void ReleaseOwedRemainsScopedAcrossSessionsAndReacquireUsesANewNonce()
    {
        using StoreFixture first = StoreFixture.Create();
        using StoreFixture second = StoreFixture.Create();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var releases = new List<string[]>();
        var acquisitions = new List<string[]>();
        var failedRoots = new HashSet<string> { first.Binding.StoreRoot, second.Binding.StoreRoot };
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false, utcNow: () => now);
        StoreReaderRegistrationContext Context(StoreFixture fixture) => new(new StoreReaderRegistrationRunner((args, _) =>
        {
            Assert.Contains(fixture.Binding.StoreRoot, args);
            if (args[2] == "acquire") acquisitions.Add(args.ToArray());
            if (args[2] == "release")
            {
                releases.Add(args.ToArray());
                if (failedRoots.Contains(fixture.Binding.StoreRoot)) return new(null, "", "private stderr", TransportLost: true);
            }
            return fixture.ReaderReply(args);
        }), registry);
        using IDisposable firstScope = StoreReaderRegistrationContext.Use(first.Binding.StoreRoot, Context(first));
        using IDisposable secondScope = StoreReaderRegistrationContext.Use(second.Binding.StoreRoot, Context(second));
        using var a = FamilyStoreReadSession.Open(first.Binding);
        using var b = FamilyStoreReadSession.Open(second.Binding);
        a.Dispose(); b.Dispose(); a.Dispose(); b.Dispose();
        Assert.Equal(2, releases.Count);
        Assert.Equal(2, registry.Count);
        for (int tick = 0; tick < 4; tick++) registry.Tick(now, TestContext.Current.CancellationToken);
        Assert.Equal(2, releases.Count);
        failedRoots.Remove(first.Binding.StoreRoot);
        now += TimeSpan.FromSeconds(30);
        registry.Tick(now, TestContext.Current.CancellationToken);
        Assert.Equal(4, releases.Count);
        Assert.Equal(2, releases.Count(args => args.SequenceEqual(releases[0])));
        Assert.Equal(2, releases.Count(args => args.SequenceEqual(releases[1])));
        Assert.Equal(1, registry.Count);
        using var reopened = FamilyStoreReadSession.Open(first.Binding);
        Assert.Equal(2, registry.Count);
        static string Nonce(string[] args) => args[Array.IndexOf(args, "--nonce") + 1];
        Assert.NotEqual(Nonce(acquisitions[0]), Nonce(acquisitions[2]));
        Assert.Equal(a.Snapshot.Freshness, reopened.Snapshot.Freshness);
        reopened.Dispose();
        Assert.Equal(1, registry.Count);
        failedRoots.Clear();
        now += TimeSpan.FromSeconds(30);
        registry.Tick(now, TestContext.Current.CancellationToken);
        Assert.Equal(releases[1], releases[^1]);
        Assert.Equal(0, registry.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("reader_owner_mismatch")]
    [InlineData("reader_identity_unknown")]
    [InlineData("reader_not_found")]
    [InlineData("busy")]
    public void ScheduledRenewalKeepsTheAdmittedSessionThroughExpiryAndProducerRefusals(string? refusal)
    {
        using StoreFixture fixture = StoreFixture.Create();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var calls = new List<string[]>();
        JsonObject? admitted = null;
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false, utcNow: () => now);
        using IDisposable scope = StoreReaderRegistrationContext.Use(fixture.Binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((args, _) =>
            {
                calls.Add(args.ToArray());
                if (args[2] == "release") return fixture.ReaderReply(args);
                if (args[2] == "acquire")
                {
                    admitted = JsonNode.Parse(fixture.ReaderReply(args).StandardOutput)!.AsObject();
                    admitted["expires_at"] = now.AddSeconds(120).ToUnixTimeMilliseconds();
                    return new(0, admitted.ToJsonString(), "");
                }
                Assert.Equal("renew", args[2]);
                if (refusal is not null)
                    return new(1, $$"""{"report_schema_version":1,"operation":"reader_renew","state":"refused","failure_class":"{{refusal}}"}""", "private producer stderr");
                var renewed = admitted!.DeepClone().AsObject();
                renewed["operation"] = "reader_renew";
                renewed["state"] = "renewed";
                renewed["expires_at"] = now.AddSeconds(120).ToUnixTimeMilliseconds();
                return new(0, renewed.ToJsonString(), "");
            }), registry));
        var session = FamilyStoreReadSession.Open(fixture.Binding);
        var snapshot = session.Snapshot;
        for (int tick = 0; tick < 2; tick++)
        {
            now += TimeSpan.FromSeconds(30);
            registry.Tick(now, TestContext.Current.CancellationToken);
            Assert.Single(calls);
        }
        Execute(fixture, "INSERT INTO store_log VALUES(10,'foreign','manifest_flipped','other-view',1,NULL,NULL,1,'{}','2026-09-05')");
        now += TimeSpan.FromSeconds(30);
        registry.Tick(now, TestContext.Current.CancellationToken);
        Assert.Equal(2, calls.Count);
        for (int tick = 0; tick < 5; tick++) registry.Tick(now, TestContext.Current.CancellationToken);
        Assert.Equal(2, calls.Count);
        now += TimeSpan.FromSeconds(60);
        registry.Tick(now, TestContext.Current.CancellationToken);
        Assert.Equal(refusal is null ? 2 : 3, calls.Count);
        Assert.Equal(snapshot, session.Snapshot);
        Assert.Equal(2, session.Snapshot.Freshness.StoreLogSequence);
        Assert.Equal("Visible", session.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM symbols";
            return command.ExecuteScalar();
        }));
        Assert.Equal(1, registry.Count);
        string nonce = admitted!["owner_nonce"]!.GetValue<string>();
        string pin = admitted["pin_id"]!.GetValue<string>();
        Assert.All(calls.Skip(1), args =>
        {
            Assert.Contains(nonce, args);
            Assert.Contains(pin, args);
            Assert.DoesNotContain("--generation", args);
        });
        session.Dispose();
        session.Dispose();
        Assert.Equal("release", calls[^1][2]);
        Assert.Equal(1, calls.Count(args => args[2] == "release"));
        Assert.Equal(0, registry.Count);
    }

    [Theory]
    [InlineData("busy", FamilyStoreReadFailure.BindingNotReady, "Busy", 1)]
    [InlineData("capacity_insufficient", FamilyStoreReadFailure.BindingNotReady, "CapacityInsufficient", 1)]
    [InlineData("stale_snapshot", FamilyStoreReadFailure.BindingNotReady, "StaleSnapshot", 1)]
    [InlineData("incompatible_store", FamilyStoreReadFailure.ReaderFloorIncompatible, "Incompatible", 3)]
    public void TypedAdmissionRefusalNeverServesAnAvailableLegacyArtifact(
        string refusal, FamilyStoreReadFailure expected, string typedCause, int exitCode)
    {
        using StoreFixture fixture = StoreFixture.Create();
        using JulieDbFixture legacy = JulieDbFixture.CreateForInspect();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);
        int opens = 0;
        int calls = 0;
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        using IDisposable scope = StoreReaderRegistrationContext.Use(fixture.Binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((_, _) =>
            {
                calls++;
                return new(exitCode, $$"""{"report_schema_version":1,"operation":"reader_acquire","state":"refused","failure_class":"{{refusal}}","error":"private producer error"}""", "private producer stderr");
            }), registry, path => { opens++; return RecordingOpen(path, [], 1); }));
        var error = Assert.Throws<FamilyStoreReadException>(() => WorkspaceReadSessionFactory.Open(
            legacy.DbPath, fixture.Binding.WorkspaceRoot, null, storeEnabled: true));
        Assert.Equal(expected, error.Failure);
        Assert.Equal(typedCause, Assert.IsType<StoreReaderRegistrationException>(error.InnerException).Failure.ToString());
        Assert.DoesNotContain("private producer", error.ToString());
        Assert.Equal(0, opens);
        Assert.Equal(1, calls);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void CompatibilityPreflightDoesNotReadUnrelatedMetadataValues()
    {
        using StoreFixture fixture = StoreFixture.Create();
        int calls = 0;
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        using IDisposable scope = StoreReaderRegistrationContext.Use(fixture.Binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((_, _) =>
            {
                calls++;
                throw new InvalidOperationException("Preflight must not acquire a serving snapshot.");
            }), registry));
        Execute(fixture, """
            INSERT INTO store_meta(key,value) VALUES ('unrelated_payload','unused');
            ALTER TABLE store_meta RENAME TO preflight_metadata;
            CREATE VIEW store_meta AS SELECT key,
                CASE WHEN key='unrelated_payload' THEN abs(-9223372036854775808) ELSE value END AS value
                FROM preflight_metadata;
            """);
        Assert.Equal("2.31.0", FamilyStoreReadSession.ReadFamilyBinaryVersion(fixture.Binding));
        Assert.True(FamilyStoreReadSession.HasViewForImportPreflight(fixture.Binding));
        Execute(fixture, "UPDATE views SET current_generation=NULL");
        Assert.False(FamilyStoreReadSession.HasViewForImportPreflight(fixture.Binding));
        Execute(fixture, "UPDATE views SET root='wrong-root'");
        Assert.Equal(FamilyStoreReadFailure.ViewRootMismatch,
            Assert.Throws<FamilyStoreReadException>(() =>
                FamilyStoreReadSession.HasViewForImportPreflight(fixture.Binding)).Failure);
        Execute(fixture, "DELETE FROM views");
        Assert.False(FamilyStoreReadSession.HasViewForImportPreflight(fixture.Binding));
        Assert.Equal("2.31.0", FamilyStoreReadSession.ReadFamilyBinaryVersion(fixture.Binding));
        Assert.Equal(0, calls);
        Assert.Equal(0, registry.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecondaryQueryFailureSurvivesCloseFailureAndBlocksRetryUntilClosed(bool background)
    {
        using StoreFixture fixture = StoreFixture.Create();
        var failed = new List<FailingCloseConnection>();
        int opens = 0;
        int releases = 0;
        bool failClose = true;
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        using IDisposable scope = StoreReaderRegistrationContext.Use(fixture.Binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((args, _) =>
            {
                if (args[2] == "release") releases++;
                return fixture.ReaderReply(args);
            }), registry, path =>
            {
                if (++opens == 1 || !failClose) return RecordingOpen(path, [], opens);
                var connection = new FailingCloseConnection(path);
                failed.Add(connection);
                return connection;
            }));
        var session = FamilyStoreReadSession.Open(fixture.Binding, null,
            background ? new RevisionFactCacheStore() : null, boundedFactsRequested: !background);
        Task Read() => background ? session.WarmResolutionFactsInBackground()
            : Task.Run(() => { _ = session.Resolution; }, TestContext.Current.CancellationToken);
        try
        {
            Execute(fixture, "ALTER TABLE manifest_entries RENAME TO unavailable_manifest_entries");
            var primary = await Assert.ThrowsAsync<SqliteException>(Read);
            Assert.Contains("manifest_entries", primary.Message);
            for (int attempt = 0; attempt < 4; attempt++)
            {
                var error = await Assert.ThrowsAsync<FamilyStoreReadException>(Read);
                Assert.Equal(FamilyStoreReadFailure.BindingNotReady, error.Failure);
                Assert.Equal(2, opens);
            }
            Assert.Single(failed);
            Assert.Equal(0, releases);
            Assert.Equal(1, registry.Count);
            failed[0].FailClose = false;
            failed[0].Dispose();
            failClose = false;
            Execute(fixture, "ALTER TABLE unavailable_manifest_entries RENAME TO manifest_entries");
            await Read();
            Assert.True(session.ResolutionFactsWarm);
            Assert.Equal(3, opens);
            session.Dispose();
            Assert.Equal(1, releases);
            Assert.Equal(0, registry.Count);
        }
        finally
        {
            foreach (var connection in failed) { connection.FailClose = false; connection.Dispose(); }
            session.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSecondaryCleanupRefusesFurtherOpensUntilClosure(bool background)
    {
        using StoreFixture fixture = StoreFixture.Create();
        var failed = new List<FailingCloseConnection>();
        int opens = 0;
        int releases = 0;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false, utcNow: () => now);
        using IDisposable scope = StoreReaderRegistrationContext.Use(fixture.Binding.StoreRoot,
            new(new StoreReaderRegistrationRunner((args, _) =>
            {
                if (args[2] == "release") releases++;
                return fixture.ReaderReply(args);
            }), registry, path =>
            {
                if (++opens == 1) return RecordingOpen(path, [], 1);
                var connection = new FailingCloseConnection(path, failOpen: true);
                failed.Add(connection);
                return connection;
            }));
        var session = FamilyStoreReadSession.Open(fixture.Binding, null,
            background ? new RevisionFactCacheStore() : null, boundedFactsRequested: !background);
        Task Read() => background ? session.WarmResolutionFactsInBackground()
            : Task.Run(() => { _ = session.Resolution; }, TestContext.Current.CancellationToken);
        try
        {
            await Assert.ThrowsAsync<IOException>(Read);
            for (int attempt = 0; attempt < 4; attempt++)
            {
                var error = await Assert.ThrowsAsync<FamilyStoreReadException>(Read);
                Assert.Equal(FamilyStoreReadFailure.BindingNotReady, error.Failure);
                Assert.Equal(2, opens);
            }
            Assert.Single(failed);
            Assert.Equal(ConnectionState.Open, failed[0].State);
            Assert.Equal(0, releases);
            session.Dispose();
            for (int attempt = 0; attempt < 6; attempt++)
            {
                now += TimeSpan.FromSeconds(30);
                registry.Tick(now, TestContext.Current.CancellationToken);
                Assert.Equal(1, registry.Count);
                Assert.Equal(0, releases);
            }
            failed[0].FailClose = false;
            now += TimeSpan.FromSeconds(30);
            registry.Tick(now, TestContext.Current.CancellationToken);
            Assert.Equal(ConnectionState.Closed, failed[0].State);
            Assert.Equal(0, registry.Count);
            Assert.Equal(1, releases);
        }
        finally
        {
            foreach (var connection in failed) { connection.FailClose = false; connection.Dispose(); }
            session.Dispose();
        }
    }

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

    [Fact(Timeout = 30_000)]
    public async Task BackgroundWarmKeepsTheSamePinUntilItsConnectionCloses()
    {
        using StoreFixture fixture = StoreFixture.Create();
        var events = new List<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finish = new ManualResetEventSlim();
        using var registry = new StoreReaderRegistrationRegistry(startScheduler: false);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
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
                    entered.SetResult();
                    finish.Wait(cancellationToken);
                }
                return RecordingOpen(path, events, ordinal);
            }));
        var session = FamilyStoreReadSession.Open(fixture.Binding, null, new RevisionFactCacheStore());
        Task warm = session.WarmResolutionFactsInBackground();
        try
        {
            Task first = await Task.WhenAny(entered.Task, warm)
                .WaitAsync(cancellationToken);
            if (ReferenceEquals(first, warm)) await warm;
            Assert.Same(entered.Task, first);
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
            try { await warm.WaitAsync(cancellationToken); }
            finally { session.Dispose(); }
        }
        Assert.Equal(new[] { "acquire", "open:1", "close:1", "open:2", "close:2", "release" }, events);
        Assert.Equal(0, registry.Count);
    }

    [Theory]
    [InlineData("2.40.0", true)]
    [InlineData("2.40.1", true)]
    [InlineData("2.40.2", true)]
    [InlineData("2.40.3", true)]
    [InlineData("2.40.4", true)]
    [InlineData("2.40.5", true)]
    [InlineData("2.40.6", true)]
    [InlineData("2.40.7", false)]
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
