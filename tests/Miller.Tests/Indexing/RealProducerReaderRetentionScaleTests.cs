using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>Reader-v1 qualification against the selected real producer. No admission report is fabricated.</summary>
[Trait("Category", "Scale")]
public sealed class RealProducerReaderRetentionScaleTests(ITestOutputHelper output)
{
    [Fact]
    public void AdmissionRootsTheSnapshotBeforeOpeningAndReleasesAfterClosing()
    {
        using var fixture = new Fixture(ScaleTestSupport.RequireJulieServer(), output);
        using var session = fixture.Open();
        Assert.Equal(new[] { "acquire", "open" }, fixture.Events);
        fixture.AssertRoots(session.Snapshot);
        Assert.Equal(1, fixture.CountRegistrations());
        Assert.Equal(Environment.ProcessId, fixture.CoordinatorLong("SELECT owner_pid FROM reader_registrations"));
        Assert.True(fixture.CoordinatorLong("SELECT length(owner_birth_identity) FROM reader_registrations") > 0);
        for (int i = 0; i < 5; i++)
            Assert.True(HasSymbol(session, "Original"));
        Assert.Equal(1, fixture.Acquires);
        session.Dispose();
        session.Dispose();
        Assert.Equal(new[] { "acquire", "open", "close", "release" }, fixture.Events);
        Assert.Equal(0, fixture.CountRegistrations());
        Assert.Equal(0, fixture.Registry.Count);
        fixture.Report("normal", session.Snapshot);
    }

    [Fact]
    public void LostCommittedAcquireReplyReusesOneRegistrationAndOneSnapshot()
    {
        using var fixture = new Fixture(ScaleTestSupport.RequireJulieServer(), output) { LoseFirstAcquireReply = true };
        using var session = fixture.Open();
        Assert.Equal(2, fixture.Acquires);
        Assert.True(fixture.SameAcquireNonce, "Acquire retries must use the original nonce.");
        Assert.Equal(1, fixture.CountRegistrations());
        fixture.AssertRoots(session.Snapshot);
        Assert.True(HasSymbol(session, "Original"));
        session.Dispose();
        Assert.Equal(0, fixture.CountRegistrations());
        fixture.Report("lost-reply", session.Snapshot);
    }

    [Fact]
    public void PromotionBetweenAdmissionAndFirstOpenKeepsTheAdmittedPhysicalGeneration()
    {
        using var fixture = new Fixture(ScaleTestSupport.RequireJulieServer(), output);
        fixture.AfterFirstAcquire = () => fixture.Maintain("promote");
        using var session = fixture.Open();
        Assert.Equal("gen-001", session.Snapshot.GenerationName);
        Assert.NotEqual("gen-001", fixture.CurrentGeneration);
        Assert.All(fixture.OpenedPaths, path => Assert.Equal("gen-001", Path.GetFileName(Path.GetDirectoryName(path))));
        Assert.True(HasSymbol(session, "Original"));
        fixture.Maintain("gc");
        fixture.AssertRoots(session.Snapshot);
        Assert.Equal(1, fixture.Acquires);
        session.Dispose();
        Assert.Equal(0, fixture.CountRegistrations());
        fixture.Report("promotion-before-open", session.Snapshot);
    }

    [Fact]
    public void ImportPromotionAndRetirementCannotRetargetOrCollectALiveSnapshot()
    {
        using var fixture = new Fixture(ScaleTestSupport.RequireJulieServer(), output);
        using var old = fixture.Open();
        WorkspaceReadSnapshot admitted = old.Snapshot;
        fixture.AssertRoots(admitted);
        fixture.Import("Replacement");
        Assert.True(fixture.StoreLong("SELECT current_generation FROM views WHERE view_id='retention-view'") > admitted.ManifestGeneration);
        Assert.True(HasSymbol(old, "Original"));
        Assert.False(HasSymbol(old, "Replacement"));
        fixture.Maintain("promote");
        using (var latest = fixture.Open())
        {
            Assert.NotEqual(admitted.GenerationName, latest.Snapshot.GenerationName);
            Assert.True(HasSymbol(latest, "Replacement"));
            Assert.False(HasSymbol(latest, "Original"));
            Assert.True(HasSymbol(old, "Original"));
        }
        fixture.AssertRetirementRefused();
        fixture.Maintain("gc");
        fixture.AssertRoots(admitted);
        Assert.Equal(admitted, old.Snapshot);
        Assert.True(HasSymbol(old, "Original"));
        old.Dispose();
        Assert.Equal(0, fixture.CountRegistrations());
        fixture.Maintain("retire-view", "--view", fixture.Binding.ViewId);
        Assert.Equal(0, fixture.StoreLong("SELECT COUNT(*) FROM views WHERE view_id='retention-view'"));
        fixture.Report("import-promote-retire-gc", admitted);
    }

    [Fact]
    public void ForeignMaintenanceFenceRefusesAcquisitionWithoutOpeningOrPartialRoots()
    {
        using var fixture = new Fixture(ScaleTestSupport.RequireJulieServer(), output);
        fixture.SetMaintenanceFence();
        var error = Assert.Throws<FamilyStoreReadException>(() => fixture.Open());
        Assert.Equal(ReaderFailure.Busy, Assert.IsType<StoreReaderRegistrationException>(error.InnerException).Failure);
        Assert.Equal(0, fixture.CountRegistrations());
        Assert.Empty(fixture.OpenedPaths);
        Assert.Equal(0, fixture.Registry.Count);
        fixture.ClearMaintenanceFence();
        using var session = fixture.Open();
        fixture.AssertRoots(session.Snapshot);
    }

    [Fact]
    public void FailedRenewAndReleaseKeepRootsUntilTheForeignFenceClears()
    {
        using var fixture = new Fixture(ScaleTestSupport.RequireJulieServer(), output);
        using var session = fixture.Open();
        long expires = fixture.CoordinatorLong("SELECT expires_at FROM reader_registrations");
        fixture.SetMaintenanceFence();
        fixture.Registry.Tick(DateTimeOffset.FromUnixTimeMilliseconds(expires), TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.Renews);
        fixture.AssertRoots(session.Snapshot);
        Assert.True(HasSymbol(session, "Original"));
        session.Dispose();
        Assert.Equal(1, fixture.Releases);
        Assert.Equal(1, fixture.CountRegistrations());
        Assert.Equal(1, fixture.Registry.Count);
        fixture.ClearMaintenanceFence();
        fixture.Registry.Tick(DateTimeOffset.UtcNow.AddMinutes(3), TestContext.Current.CancellationToken);
        Assert.Equal(2, fixture.Releases);
        Assert.Equal(0, fixture.CountRegistrations());
        Assert.Equal(0, fixture.Registry.Count);
        fixture.Report("renew-release-fence", session.Snapshot);
    }

    [Fact]
    public void SuccessfulRenewalPreservesTheOriginalSnapshot()
    {
        using var fixture = new Fixture(ScaleTestSupport.RequireJulieServer(), output);
        using var session = fixture.Open();
        long expires = fixture.CoordinatorLong("SELECT expires_at FROM reader_registrations");
        fixture.Import("Replacement");
        fixture.Registry.Tick(DateTimeOffset.FromUnixTimeMilliseconds(expires), TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.Renews);
        Assert.True(fixture.CoordinatorLong("SELECT expires_at FROM reader_registrations") >= expires);
        fixture.AssertRoots(session.Snapshot);
        Assert.True(HasSymbol(session, "Original"));
        Assert.False(HasSymbol(session, "Replacement"));
        Assert.Equal(1, fixture.Acquires);
    }

    [Fact]
    public void MissingViewIsMetadataAbsenceButReadyAdmissionIsStaleSnapshot()
    {
        using var fixture = new Fixture(ScaleTestSupport.RequireJulieServer(), output);
        StoreFamilyBinding absent = fixture.Binding with { ViewId = "never-published" };
        Assert.False(FamilyStoreReadSession.HasViewForImportPreflight(absent with { State = StoreBindingState.Planned }));
        Assert.Equal(0, fixture.Acquires);
        var error = Assert.Throws<FamilyStoreReadException>(() => FamilyStoreReadSession.Open(absent));
        Assert.Equal(ReaderFailure.StaleSnapshot, Assert.IsType<StoreReaderRegistrationException>(error.InnerException).Failure);
        Assert.Equal(0, fixture.CountRegistrations());
        Assert.Empty(fixture.OpenedPaths);
    }

    [Theory]
    [InlineData("gc")]
    [InlineData("promote")]
    [InlineData("retire-view")]
    public async Task ConcurrentMaintenanceEitherRetainsTheAdmittedSnapshotOrRefusesWithoutPartialRoots(string action)
    {
        using var fixture = new Fixture(ScaleTestSupport.RequireJulieServer(), output);
        using var start = new ManualResetEventSlim();
        WorkspaceReadHandle? session = null;
        FamilyStoreReadException? refusal = null;
        Task reader = Task.Run(() =>
        {
            start.Wait(TestContext.Current.CancellationToken);
            try { session = fixture.Open(); }
            catch (FamilyStoreReadException error) { refusal = error; }
        }, TestContext.Current.CancellationToken);
        Task maintenance = Task.Run(() =>
        {
            start.Wait(TestContext.Current.CancellationToken);
            fixture.MaintainConcurrently(action);
        }, TestContext.Current.CancellationToken);
        start.Set();
        try
        {
            await Task.WhenAll(reader, maintenance);
            if (session is not null)
            {
                fixture.AssertRoots(session.Snapshot);
                Assert.True(HasSymbol(session, "Original"));
                fixture.Report("concurrent-" + action, session.Snapshot);
            }
            else
            {
                Assert.NotNull(refusal);
                var cause = Assert.IsType<StoreReaderRegistrationException>(refusal.InnerException);
                Assert.Contains(cause.Failure, new[] { ReaderFailure.Busy, ReaderFailure.StaleSnapshot });
                Assert.Equal(0, fixture.CountRegistrations());
                Assert.Empty(fixture.OpenedPaths);
                Assert.Equal(0, fixture.Registry.Count);
                output.WriteLine($"concurrent-{action}: admission_refused={cause.Failure}; partial_roots=0");
            }
        }
        finally { session?.Dispose(); }
        Assert.Equal(0, fixture.CountRegistrations());
    }

    private static bool HasSymbol(IWorkspaceReadSession session, string name) => session.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM symbols WHERE name=$name";
        command.Parameters.AddWithValue("$name", name);
        return (long)command.ExecuteScalar()! > 0;
    });

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "miller-real-reader-" + Guid.NewGuid().ToString("N"));
        private readonly string _binary;
        private readonly ITestOutputHelper _output;
        private readonly JulieStoreClient _transport;
        private readonly JulieStoreClient _observedClient;
        private readonly IDisposable _scope;
        private readonly List<SqliteConnection> _connections = [];
        private readonly Dictionary<string, List<SqliteConnection>> _ownerConnections = [];
        private string? _openingNonce;
        private string? _firstNonce;
        private int _imports;
        private double _acquireMilliseconds;

        internal Fixture(string binary, ITestOutputHelper output)
        {
            _binary = binary;
            _output = output;
            string root = Path.Combine(_directory, "workspace");
            Directory.CreateDirectory(root);
            Binding = new(Guid.NewGuid(), Path.Combine(_directory, "family"), "retention-view", root, StoreBindingState.Ready);
            Registry = new(startScheduler: false);
            _transport = new(binary);
            _observedClient = new(binary, InvokeReader);
            Import("Original");
            StoreWorkspacePointer.Write(root, Binding);
            _scope = StoreReaderRegistrationContext.Use(Binding.StoreRoot,
                new(new StoreReaderRegistrationRunner(_observedClient), Registry, OpenRead));
        }

        internal StoreFamilyBinding Binding { get; }
        internal StoreReaderRegistrationRegistry Registry { get; }
        internal List<string> Events { get; } = [];
        internal List<string> OpenedPaths { get; } = [];
        internal int Acquires { get; private set; }
        internal int Renews { get; private set; }
        internal int Releases { get; private set; }
        internal bool SameAcquireNonce { get; private set; } = true;
        internal bool LoseFirstAcquireReply { get; init; }
        internal Action? AfterFirstAcquire { get; set; }
        internal string CurrentGeneration => File.ReadAllText(Path.Combine(Binding.StoreRoot, "CURRENT")).Trim();

        internal WorkspaceReadHandle Open() => WorkspaceReadSessionFactory.Open(
            Path.Combine(Binding.WorkspaceRoot, ".miller", "symbols.db"), Binding.WorkspaceRoot, null,
            _observedClient, storeEnabled: true);

        private ReaderProcessResult InvokeReader(IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            string operation = args[2];
            Events.Add(operation);
            if (operation == "acquire")
            {
                Acquires++;
                string nonce = args[Array.IndexOf(args.ToArray(), "--nonce") + 1];
                _openingNonce = nonce;
                _ownerConnections.TryAdd(nonce, []);
                _firstNonce ??= nonce;
                SameAcquireNonce &= nonce == _firstNonce;
            }
            else if (operation == "renew") Renews++;
            else if (operation == "release")
            {
                Releases++;
                string nonce = args[Array.IndexOf(args.ToArray(), "--nonce") + 1];
                Assert.All(_ownerConnections[nonce], connection => Assert.Equal(ConnectionState.Closed, connection.State));
            }
            var watch = Stopwatch.StartNew();
            ReaderProcessResult result = _transport.InvokeReader(args, cancellationToken);
            if (operation == "acquire")
            {
                _acquireMilliseconds += watch.Elapsed.TotalMilliseconds;
                if (result.ExitCode == 0 && Acquires == 1)
                {
                    Assert.Equal(1, CountRegistrations());
                    AfterFirstAcquire?.Invoke();
                    if (LoseFirstAcquireReply) return new(null, "", "", TransportLost: true);
                }
            }
            return result;
        }

        private SqliteConnection OpenRead(string path)
        {
            var connection = CreateConnection(path);
            _connections.Add(connection);
            _ownerConnections[_openingNonce!].Add(connection);
            connection.StateChange += (_, change) =>
            {
                if (change.CurrentState == ConnectionState.Open)
                {
                    Assert.True(CountRegistrations() > 0, "A real committed reader root must precede every generation open.");
                    OpenedPaths.Add(path);
                    Events.Add("open");
                }
                else if (change.CurrentState == ConnectionState.Closed) Events.Add("close");
            };
            return connection;
        }

        internal void Import(string symbol)
        {
            File.WriteAllText(Path.Combine(Binding.WorkspaceRoot, "Sample.cs"),
                $"namespace Retention; public static class {symbol} {{ public static int Value() => 17; }}");
            string request = "retention-import-" + ++_imports;
            ScaleTestSupport.RunJulie(_binary, "store", "import", "--store", Binding.StoreRoot,
                "--family", Binding.FamilyId.ToString("D"), "--root", Binding.WorkspaceRoot,
                "--view", Binding.ViewId, "--level", "full", "--jobs", "1",
                "--request-id", request, "--idempotency-key", request, "--json");
        }

        internal void Maintain(string action, params string[] extra)
        {
            string report = ScaleTestSupport.RunJulie(_binary,
                ["store", "maintain", action, "--store", Binding.StoreRoot, "--apply", "--json", .. extra]);
            using var document = JsonDocument.Parse(report);
            Assert.Equal("none", document.RootElement.GetProperty("failure_class").GetString());
        }

        internal void AssertRetirementRefused()
        {
            ReaderProcessResult result = _transport.InvokeReader(
                ["store", "maintain", "retire-view", "--store", Binding.StoreRoot,
                 "--view", Binding.ViewId, "--apply", "--json"], TestContext.Current.CancellationToken);
            Assert.NotEqual(0, result.ExitCode);
            using var report = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal("busy", report.RootElement.GetProperty("failure_class").GetString());
            Assert.Equal(1, StoreLong("SELECT COUNT(*) FROM views WHERE view_id='retention-view'"));
        }

        internal void MaintainConcurrently(string action)
        {
            string[] extra = action == "retire-view" ? ["--view", Binding.ViewId] : [];
            ReaderProcessResult result = _transport.InvokeReader(
                ["store", "maintain", action, "--store", Binding.StoreRoot, "--apply", "--json", .. extra],
                TestContext.Current.CancellationToken);
            Assert.False(result.TransportLost);
            using var report = JsonDocument.Parse(result.StandardOutput);
            string failure = report.RootElement.GetProperty("failure_class").GetString()!;
            if (result.ExitCode == 0) Assert.Equal("none", failure);
            else Assert.Contains(failure, new[] { "busy", "stale_plan" });
            _output.WriteLine($"concurrent-{action}: maintenance_failure={failure}; maintenance_exit={result.ExitCode}");
        }

        internal long CountRegistrations() => CoordinatorLong("SELECT COUNT(*) FROM reader_registrations");
        internal long CoordinatorLong(string sql) => Scalar(Path.Combine(Binding.StoreRoot, "coord.db"), sql);
        internal long StoreLong(string sql) => Scalar(Path.Combine(Binding.StoreRoot, CurrentGeneration, "store.db"), sql);

        internal void AssertRoots(WorkspaceReadSnapshot snapshot)
        {
            string database = Path.Combine(Binding.StoreRoot, snapshot.GenerationName!, "store.db");
            Assert.True(File.Exists(database), "The admitted physical generation must survive maintenance.");
            using var coordinator = Connect(Path.Combine(Binding.StoreRoot, "coord.db"));
            using var command = coordinator.CreateCommand();
            command.CommandText = """
                SELECT manifest_generation, generation_name, manifest_hash, served_store_log_sequence,
                       min_retained_store_log_sequence FROM reader_registrations
                WHERE owner_pid=$pid AND view_id=$view AND generation_name=$generation
                  AND manifest_generation=$manifest
                """;
            command.Parameters.AddWithValue("$pid", Environment.ProcessId);
            command.Parameters.AddWithValue("$view", Binding.ViewId);
            command.Parameters.AddWithValue("$generation", snapshot.GenerationName);
            command.Parameters.AddWithValue("$manifest", snapshot.ManifestGeneration);
            using var row = command.ExecuteReader();
            Assert.True(row.Read(), "The coordinator must retain this exact admitted manifest.");
            Assert.Equal(snapshot.ManifestGeneration, row.GetInt64(0));
            Assert.Equal(snapshot.GenerationName, row.GetString(1));
            long served = row.GetInt64(3), floor = row.GetInt64(4);
            using var store = Connect(database);
            using var manifest = store.CreateCommand();
            manifest.CommandText = "SELECT manifest_hash FROM manifests WHERE view_id=$view AND generation=$manifest";
            manifest.Parameters.AddWithValue("$view", Binding.ViewId);
            manifest.Parameters.AddWithValue("$manifest", snapshot.ManifestGeneration);
            Assert.Equal(row.GetString(2), (string)manifest.ExecuteScalar()!);
            Assert.Equal(1, Scalar(database, $"SELECT COUNT(*) FROM manifest_entries WHERE view_id='retention-view' AND generation={snapshot.ManifestGeneration}"));
            Assert.Equal(1, Scalar(database, $"SELECT COUNT(*) FROM manifest_entries e JOIN file_versions f USING(version_id) WHERE e.view_id='retention-view' AND e.generation={snapshot.ManifestGeneration}"));
            long retained = Scalar(database, $"SELECT COUNT(*) FROM store_log WHERE sequence BETWEEN {floor} AND {served}");
            Assert.Equal(served - floor + (floor == 0 ? 0 : 1), retained);
        }

        internal void SetMaintenanceFence()
        {
            // Deliberate deterministic fixture fence, matching producer store_reader_cursor_contract.rs.
            // This is not a claim that a maintenance process is concurrently executing.
            using var connection = Connect(Path.Combine(Binding.StoreRoot, "coord.db"), write: true);
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO maintenance_intent
                (resource,run_id,action,source_generation_name,owner_id,owner_pid,fencing_token,
                 heartbeat_at,expires_at,started_at,plan_fingerprint,source_min_writer_version)
                VALUES ('store-maintenance','retention-test-fence','gc',$generation,'foreign-owner',$pid,
                        41,1,$expires,1,'foreign-plan','2.40.0')
                """;
            command.Parameters.AddWithValue("$generation", CurrentGeneration);
            command.Parameters.AddWithValue("$pid", Environment.ProcessId);
            command.Parameters.AddWithValue("$expires", long.MaxValue);
            command.ExecuteNonQuery();
        }

        internal void ClearMaintenanceFence()
        {
            using var connection = Connect(Path.Combine(Binding.StoreRoot, "coord.db"), write: true);
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM maintenance_intent WHERE run_id='retention-test-fence'";
            command.ExecuteNonQuery();
        }

        internal void Report(string scenario, WorkspaceReadSnapshot snapshot) => _output.WriteLine(
            $"{scenario}: owner_pid={Environment.ProcessId}; generation={snapshot.GenerationName}; manifest={snapshot.ManifestGeneration}; " +
            $"acquire_processes={Acquires}; renew_processes={Renews}; release_processes={Releases}; " +
            $"acquire_ms={_acquireMilliseconds.ToString("F2", CultureInfo.InvariantCulture)}; latency=report-only; roots={CountRegistrations()}");

        private static SqliteConnection Connect(string path, bool write = false)
        {
            var connection = CreateConnection(path, write);
            connection.Open();
            return connection;
        }

        private static SqliteConnection CreateConnection(string path, bool write = false) =>
            new(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = write ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString());

        private static long Scalar(string path, string sql)
        {
            using var connection = Connect(path);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (long)command.ExecuteScalar()!;
        }

        public void Dispose()
        {
            ClearMaintenanceFence();
            foreach (var connection in _connections) connection.Dispose();
            Registry.Tick(DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);
            _scope.Dispose();
            Registry.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
