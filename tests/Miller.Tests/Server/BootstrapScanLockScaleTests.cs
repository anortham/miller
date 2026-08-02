using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// The live regression guard for the bootstrap lease hole (W1): two fresh Miller processes on one worktree used
/// to scan and PROMOTE the same artifact concurrently, because <c>RunBootstrap</c> decided from an unlocked probe
/// and then scanned with no <see cref="SingleWriterLock"/> held. Every test here drives the REAL bootstrap — the
/// real lock, the real artifact probes, the real <see cref="JulieExtractRunner"/> subprocess — so the suite fails
/// if the production gate is weakened; they are therefore <c>[Trait("Category","Scale")]</c> and skip (never
/// fail) when <c>.tools/julie-extract</c> is absent.
/// </summary>
[Trait("Category", "Scale")]
public sealed class BootstrapScanLockScaleTests
{
    [Fact]
    public void Bootstrap_LosesTheLockAndTheWinnerFinishesMidWait_BindsThatArtifactWithoutScanning()
    {
        ScaleTestSupport.RequireJulieServer();
        using var fx = Fixture.Create();
        string staged = fx.ScanToStagingArtifact();

        using var held = SingleWriterLock.TryAcquire(fx.MillerDir);
        Assert.NotNull(held);

        using var bootstrap = fx.CreateBootstrap();
        using var winner = fx.PublishArtifactAfter(staged, TimeSpan.FromSeconds(1.5));

        fx.RunBootstrapToCompletion(bootstrap);

        Assert.Equal(0, fx.ScanCount);
        Assert.Contains(fx.Log, m => m.Contains("loading the artifact it produced instead of scanning"));
        Assert.Contains(
            bootstrap.Index.Search("CreateFleetWidget", limit: 10),
            h => bootstrap.Index.Resolve(h.Document.DocId).Name == "CreateFleetWidget");
        using SingleWriterLock? stolen = SingleWriterLock.TryAcquire(fx.MillerDir);
        Assert.Null(stolen);
    }

    [Fact]
    public void BootstrapScanLease_WinnerArtifactRecordsAnotherWorkspaceRoot_RefusesToStandDown()
    {
        ScaleTestSupport.RequireJulieServer();
        using var fx = Fixture.Create();
        File.Move(fx.ScanForeignArtifact(), fx.DbPath);

        using var held = SingleWriterLock.TryAcquire(fx.MillerDir);
        Assert.NotNull(held);

        Assert.Equal(IndexBootstrapService.BootstrapLeaseOutcome.TimedOut, fx.AcquireLease().Outcome);
    }

    [Fact]
    public void Bootstrap_ThatScansForReal_ReleasesTheWriterLeaseBeforeItReturns()
    {
        ScaleTestSupport.RequireJulieServer();
        using var fx = Fixture.Create();

        using var bootstrap = fx.CreateBootstrap();
        fx.RunBootstrapToCompletion(bootstrap);

        Assert.Equal(1, fx.ScanCount);
        Assert.DoesNotContain(fx.Log, m => m.Contains("loading the artifact it produced instead of scanning"));
        Assert.True(bootstrap.Index.DocumentCount > 0);
        using SingleWriterLock? reacquired = SingleWriterLock.TryAcquire(fx.MillerDir);
        Assert.NotNull(reacquired);
    }

    [Fact]
    public void BootstrapScanLease_WinnerWroteMetadataButCommittedNoRevision_RefusesToStandDown()
    {
        ScaleTestSupport.RequireJulieServer();
        using var fx = Fixture.Create();
        fx.StageInFlightArtifact(fx.ScanToStagingArtifact());

        using var held = SingleWriterLock.TryAcquire(fx.MillerDir);
        Assert.NotNull(held);

        Assert.Equal(IndexBootstrapService.BootstrapLeaseOutcome.TimedOut, fx.AcquireLease().Outcome);
    }

    [Fact]
    public void BootstrapScanLease_WinnerArtifactThisBuildCannotRead_RefusesToStandDown()
    {
        ScaleTestSupport.RequireJulieServer();
        using var fx = Fixture.Create();
        File.Move(fx.ScanToStagingArtifact(), fx.DbPath);
        fx.ExecuteOnArtifact(
            "UPDATE artifact_metadata SET value = '99999' WHERE key = 'sqlite_schema_version';");

        using var held = SingleWriterLock.TryAcquire(fx.MillerDir);
        Assert.NotNull(held);

        Assert.Equal(IndexBootstrapService.BootstrapLeaseOutcome.TimedOut, fx.AcquireLease().Outcome);
    }

    [Fact]
    public void BootstrapScanLease_FinishedWinnerArtifact_StandsDownAndReportsTheReuseDecision()
    {
        ScaleTestSupport.RequireJulieServer();
        using var fx = Fixture.Create();
        File.Move(fx.ScanToStagingArtifact(), fx.DbPath);

        using var held = SingleWriterLock.TryAcquire(fx.MillerDir);
        Assert.NotNull(held);

        var result = fx.AcquireLease();

        Assert.Equal(IndexBootstrapService.BootstrapLeaseOutcome.WinnerArtifactUsable, result.Outcome);
        Assert.Null(result.Lease);
        Assert.False(result.Decision.ShouldScan);
    }

    [Fact]
    public void BootstrapScanLease_LockHeldAndNoArtifactEverAppears_NeverScansAndNamesTheHolder()
    {
        ScaleTestSupport.RequireJulieServer();
        using var fx = Fixture.Create();

        using var held = SingleWriterLock.TryAcquire(fx.MillerDir);
        Assert.NotNull(held);

        var result = fx.AcquireLease();

        Assert.Equal(IndexBootstrapService.BootstrapLeaseOutcome.TimedOut, result.Outcome);
        Assert.Null(result.Lease);
        Assert.True(result.Decision.ShouldScan);
        Assert.False(File.Exists(fx.DbPath));
        Assert.Contains("leader identity", IndexBootstrapService.DescribeBootstrapLockHolder(fx.MillerDir));
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly TimeSpan BootstrapBudget = TimeSpan.FromMinutes(3);

        private readonly string _work;
        private readonly string _home;
        private readonly RecordingLogger _logger = new();
        private int _scanCount;

        private Fixture(string work, string home, string repo, string millerDir, string dbPath)
        {
            _work = work;
            _home = home;
            Repo = repo;
            MillerDir = millerDir;
            DbPath = dbPath;
        }

        public string Repo { get; }

        public string MillerDir { get; }

        public string DbPath { get; }

        public int ScanCount => Volatile.Read(ref _scanCount);

        public IReadOnlyList<string> Log => _logger.Messages;

        public static Fixture Create()
        {
            string work = Path.Combine(
                Path.GetTempPath(), "miller-bootstrap-lease-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(work, "repo"));
            string home = Path.Combine(work, "home");
            Directory.CreateDirectory(home);

            // The recorded root_path julie writes is symlink-resolved, and the temp dir is behind one on macOS,
            // so the fixture must compare against the same canonical root RunBootstrap binds to.
            string repo = PathCanonicalizer.CanonicalizeRoot(Path.Combine(work, "repo"));
            string millerDir = Path.Combine(repo, ".miller");
            Directory.CreateDirectory(millerDir);
            File.WriteAllText(Path.Combine(repo, "widget.cs"), """
                namespace Demo;

                public sealed class FleetWidgetFactory
                {
                    public FleetWidget CreateFleetWidget(int size) => new FleetWidget(size);
                }

                public sealed record FleetWidget(int Size);
                """);

            return new Fixture(work, home, repo, millerDir, Path.Combine(millerDir, "symbols.db"));
        }

        public IndexBootstrapService CreateBootstrap()
        {
            var bootstrap = new IndexBootstrapService(_logger)
            {
                TestHomeDirectoryOverride = _home,
            };
            bootstrap.TestScanObserver = () => Interlocked.Increment(ref _scanCount);
            return bootstrap;
        }

        public void RunBootstrapToCompletion(IndexBootstrapService bootstrap)
        {
            Assert.Equal(BindOutcome.Started,
                bootstrap.BootstrapForRoot(Repo, WorkspaceBindingResolver.WorkspaceSource.Roots));

            DateTimeOffset deadline = DateTimeOffset.UtcNow + BootstrapBudget;
            BootstrapSnapshot snapshot = bootstrap.Snapshot;
            while (snapshot.Phase == BootstrapPhase.Running && DateTimeOffset.UtcNow < deadline)
            {
                Thread.Sleep(50);
                snapshot = bootstrap.Snapshot;
            }

            Assert.True(
                snapshot.Phase == BootstrapPhase.Bound,
                $"bootstrap ended in {snapshot.Phase}: {snapshot.FailureMessage ?? "(still running)"}");
        }

        public string ScanToStagingArtifact() => ScanToStaging(Repo, "staging");

        /// <summary>
        /// A finished artifact for a DIFFERENT workspace root, as a copied <c>.miller</c> directory leaves behind.
        /// Standing down on one would bind another repo's symbols under this workspace's id.
        /// </summary>
        public string ScanForeignArtifact()
        {
            string other = Path.Combine(_work, "other-repo");
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "other.cs"), "namespace Other; public sealed class Unrelated;");
            return ScanToStaging(PathCanonicalizer.CanonicalizeRoot(other), "staging-foreign");
        }

        private string ScanToStaging(string root, string stagingName)
        {
            string staging = Path.Combine(_work, stagingName, "symbols.db");
            Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
            new JulieExtractRunner(ScaleTestSupport.RequireJulieServer()).Scan(root, staging, force: true);
            SqliteConnection.ClearAllPools();
            return staging;
        }

        /// <summary>
        /// Move a finished artifact into place after <paramref name="delay"/>, modelling the winner committing
        /// mid-wait. The move is atomic so a poll never observes a half-copied file.
        /// </summary>
        public IDisposable PublishArtifactAfter(string stagedArtifact, TimeSpan delay)
        {
            var cts = new CancellationTokenSource();
            Task publish = Task.Run(async () =>
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                File.Move(stagedArtifact, DbPath);
            });
            return new Publication(cts, publish);
        }

        /// <summary>
        /// Reproduce the artifact julie-extract has on disk between opening its writer and committing: the full
        /// schema and every <c>artifact_metadata</c> row (both written in autocommit), and not one committed
        /// data row. Copied off a real artifact so the shape stays faithful as the extractor's schema moves.
        /// </summary>
        public void StageInFlightArtifact(string finishedArtifact)
        {
            var schema = new List<string>();
            var metadata = new List<(string Key, string Value)>();
            using (var source = new SqliteConnection($"Data Source={finishedArtifact};Mode=ReadOnly;Pooling=False"))
            {
                source.Open();
                schema.AddRange(Query(source, "SELECT sql FROM sqlite_schema WHERE sql IS NOT NULL;",
                    r => r.GetString(0)));
                metadata.AddRange(Query(source, "SELECT key, value FROM artifact_metadata;",
                    r => (r.GetString(0), r.GetString(1))));
            }

            using (var target = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
            {
                target.Open();
                foreach (string statement in schema)
                    Execute(target, statement);

                foreach ((string key, string value) in metadata)
                {
                    using SqliteCommand insert = target.CreateCommand();
                    insert.CommandText = "INSERT INTO artifact_metadata(key, value) VALUES ($key, $value);";
                    insert.Parameters.AddWithValue("$key", key);
                    insert.Parameters.AddWithValue("$value", value);
                    insert.ExecuteNonQuery();
                }
            }

            SqliteConnection.ClearAllPools();
        }

        public void ExecuteOnArtifact(string sql)
        {
            using (var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
            {
                connection.Open();
                Execute(connection, sql);
            }

            SqliteConnection.ClearAllPools();
        }

        private static void Execute(SqliteConnection connection, string sql)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static List<T> Query<T>(SqliteConnection connection, string sql, Func<SqliteDataReader, T> project)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            var results = new List<T>();
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
                results.Add(project(reader));
            return results;
        }

        public IndexBootstrapService.BootstrapScanLease<SingleWriterLock> AcquireLease()
        {
            var now = DateTimeOffset.UnixEpoch;
            var probe = new IndexBootstrapService.WinnerArtifactProbe(
                DbPath, Repo, WorkspaceId.FromCanonicalRoot(Repo));
            return IndexBootstrapService.AcquireBootstrapScanLease(
                tryAcquire: () => SingleWriterLock.TryAcquire(MillerDir),
                decide: () => IndexBootstrapService.ReadBootstrapScanDecision(DbPath, Repo).Decision,
                winnerArtifactUsable: probe.IsFinished,
                wait: TimeSpan.FromSeconds(1),
                pollInterval: TimeSpan.FromMilliseconds(50),
                utcNow: () => now,
                sleep: d => now += d);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_work, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private sealed class RecordingLogger : ILogger<IndexBootstrapService>
        {
            private readonly List<string> _messages = [];

            public IReadOnlyList<string> Messages
            {
                get
                {
                    lock (_messages)
                        return _messages.ToArray();
                }
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (_messages)
                    _messages.Add(formatter(state, exception));
            }
        }

        private sealed class Publication(CancellationTokenSource cancellation, Task publish) : IDisposable
        {
            public void Dispose()
            {
                cancellation.Cancel();
                try
                {
                    publish.Wait(TimeSpan.FromSeconds(30));
                }
                catch (AggregateException)
                {
                }

                cancellation.Dispose();
            }
        }
    }
}
