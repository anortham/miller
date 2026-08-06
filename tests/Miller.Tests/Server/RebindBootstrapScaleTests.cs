using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Live proof of the rebind bootstrap (contract design §7) against real git worktrees and the real pinned
/// julie-extract: a fresh linked worktree is seeded from its main checkout's artifact — copy, retarget, delta
/// scan, promote — instead of extracting the tree again, the source artifact is never written, and staging
/// debris left by a dead rebind is reclaimed by the fallback scan. Spawns the binary, so it is
/// <c>[Trait("Category","Scale")]</c> and obtains it through <see cref="ScaleTestSupport.RequireJulieServer"/>;
/// SKIPS when <c>.tools/julie-extract</c> is absent.
/// </summary>
[Trait("Category", "Scale")]
public sealed class RebindBootstrapScaleTests
{
    [Fact]
    public void Bootstrap_OfAFreshLinkedWorktree_RebindsTheMainCheckoutArtifactAndLeavesTheSourceUntouched()
    {
        ScaleTestSupport.RequireJulieServer();
        using var fx = Fixture.Create();
        fx.BootstrapMainCheckout();
        string worktree = fx.AddLinkedWorktree("feature");
        fx.AgeSourceScanHeartbeat();
        string sourceHash = fx.HashArtifact(fx.MainDbPath);
        string sourceArtifactId = fx.ReadMetadata(fx.MainDbPath, "artifact_id")!;

        fx.BootstrapWorkspace(worktree);

        string worktreeDb = Path.Combine(worktree, ".miller", "symbols.db");
        Assert.True(File.Exists(worktreeDb));
        Assert.Equal(worktree, fx.ReadMetadata(worktreeDb, "root_path"));
        Assert.Equal(fx.MainRoot, fx.ReadMetadata(worktreeDb, "rebound_from_root"));
        Assert.Equal(sourceArtifactId, fx.ReadMetadata(worktreeDb, "rebound_from_artifact_id"));
        Assert.NotNull(fx.ReadMetadata(worktreeDb, "rebound_at"));
        Assert.NotEqual(sourceArtifactId, fx.ReadMetadata(worktreeDb, "artifact_id"));
        Assert.Contains(fx.Log, m => m.Contains("by rebinding the index of", StringComparison.Ordinal));
        Assert.Equal(sourceHash, fx.HashArtifact(fx.MainDbPath));
        Assert.False(File.Exists(FullRebuildPromotion.RebuildDbPathFor(worktreeDb)));
    }

    [Fact]
    public void TryRebind_OnAByteIdenticalWorktree_ReconcilesWithANoChangeDeltaScan()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var fx = Fixture.Create();
        fx.BootstrapMainCheckout();
        string worktree = fx.AddLinkedWorktree("identical");
        fx.AgeSourceScanHeartbeat();
        string sourceHash = fx.HashArtifact(fx.MainDbPath);
        Directory.CreateDirectory(Path.Combine(worktree, ".miller"));

        var runner = new JulieExtractRunner(binary);
        ExtractReport? delta = null;
        RebindBootstrapOutcome outcome = RebindBootstrap.TryRebind(
            new RebindBootstrapRequest
            {
                TargetRoot = worktree,
                TargetDbPath = Path.Combine(worktree, ".miller", "symbols.db"),
                RegistryDbPath = fx.RegistryDbPath,
                RootReplacementDetected = false,
                TargetLevelPolicy = IndexLevelPolicy.Full,
                FailurePolicy = new InMemoryScanFailurePolicy(),
                Jobs = 1,
            },
            new RebindBootstrapSeams
            {
                Rebind = (db, root, ct) => runner.Rebind(db, root, ct),
                RunDeltaScan = (db, level) =>
                {
                    delta = runner.Scan(worktree, db, force: false, jobs: 1, level);
                    return delta;
                },
            },
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Result == RebindBootstrapOutcome.Kind.Promoted, outcome.Reason);
        Assert.NotNull(delta);
        Assert.True(delta.IsNoChange, $"expected no_change, got {delta.Status}");
        Assert.Equal(sourceHash, fx.HashArtifact(fx.MainDbPath));
    }

    [Fact]
    public void Bootstrap_WithStagingDebrisFromADeadRebind_ClearsItWhileFallingBackToTheFullScan()
    {
        ScaleTestSupport.RequireJulieServer();
        using var fx = Fixture.Create();
        string debris = FullRebuildPromotion.RebuildDbPathFor(fx.MainDbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(debris)!);
        File.WriteAllText(debris, "a dead rebind's staging file");
        File.WriteAllText(debris + "-wal", "a dead rebind's write-ahead log");

        fx.BootstrapMainCheckout();

        Assert.True(File.Exists(fx.MainDbPath));
        Assert.False(File.Exists(debris));
        Assert.False(File.Exists(debris + "-wal"));
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly TimeSpan BootstrapBudget = TimeSpan.FromMinutes(5);

        private readonly string _work;
        private readonly string _home;
        private readonly RecordingLogger _logger = new();

        private Fixture(string work, string home, string mainRoot)
        {
            _work = work;
            _home = home;
            MainRoot = mainRoot;
        }

        public string MainRoot { get; }

        public string MainDbPath => Path.Combine(MainRoot, ".miller", "symbols.db");

        public string RegistryDbPath => Path.Combine(_home, ".miller", "workspaces.db");

        public IReadOnlyList<string> Log => _logger.Messages;

        public static Fixture Create()
        {
            string work = Path.Combine(Path.GetTempPath(), "miller-rebind-bootstrap-" + Guid.NewGuid().ToString("N"));
            string home = Path.Combine(work, "home");
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(Path.Combine(work, "main"));
            string mainRoot = PathCanonicalizer.CanonicalizeRoot(Path.Combine(work, "main"));

            File.WriteAllText(Path.Combine(mainRoot, "widget.cs"), """
                namespace Demo;

                public sealed class FleetWidgetFactory
                {
                    public FleetWidget CreateFleetWidget(int size) => new FleetWidget(size);
                }

                public sealed record FleetWidget(int Size);
                """);
            File.WriteAllText(Path.Combine(mainRoot, "widget.py"), """
                def create_fleet_widget(size):
                    return {"size": size}
                """);

            Git(mainRoot, "init", "-b", "main");
            Git(mainRoot, "add", ".");
            Git(
                mainRoot, "-c", "user.email=tests@miller.invalid", "-c", "user.name=Miller Tests",
                "commit", "-m", "fixture");

            return new Fixture(work, home, mainRoot);
        }

        public void BootstrapMainCheckout() => BootstrapWorkspace(MainRoot);

        public void BootstrapWorkspace(string root)
        {
            using var bootstrap = new IndexBootstrapService(_logger) { TestHomeDirectoryOverride = _home };
            Assert.Equal(
                BindOutcome.Started,
                bootstrap.BootstrapForRoot(root, WorkspaceBindingResolver.WorkspaceSource.Roots));

            DateTimeOffset deadline = DateTimeOffset.UtcNow + BootstrapBudget;
            BootstrapSnapshot snapshot = bootstrap.Snapshot;
            while (snapshot.Phase == BootstrapPhase.Running && DateTimeOffset.UtcNow < deadline)
            {
                Thread.Sleep(50);
                snapshot = bootstrap.Snapshot;
            }

            Assert.True(
                snapshot.Phase == BootstrapPhase.Bound,
                $"bootstrap of {root} ended in {snapshot.Phase}: {snapshot.FailureMessage ?? "(still running)"}");
            SqliteConnection.ClearAllPools();
        }

        public string AddLinkedWorktree(string branch)
        {
            string path = Path.Combine(_work, branch);
            Git(MainRoot, "worktree", "add", "-b", branch, path);
            return PathCanonicalizer.CanonicalizeRoot(path);
        }

        /// <summary>
        /// Backdate the heartbeat the main checkout's own scan just wrote. The pre-check reads it as "the source
        /// is scanning right now" for <see cref="RebindBootstrap.SourceScanHeartbeatWindow"/> after ANY scan
        /// finishes, and a fixture that scans and rebinds in the same second would otherwise only ever exercise
        /// the stand-down branch.
        /// </summary>
        public void AgeSourceScanHeartbeat()
        {
            string heartbeat = Path.Combine(MainRoot, ".miller", ExtractSupervisionPolicy.ProgressFileName);
            if (File.Exists(heartbeat))
                File.SetLastWriteTimeUtc(heartbeat, DateTime.UtcNow - TimeSpan.FromHours(1));
        }

        public string HashArtifact(string dbPath)
        {
            SqliteConnection.ClearAllPools();
            using FileStream stream = File.OpenRead(dbPath);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        public string? ReadMetadata(string dbPath, string key)
        {
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM artifact_metadata WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }

        private static void Git(string workingDirectory, params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string arg in args)
                psi.ArgumentList.Add(arg);

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("git did not start.");
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(
                process.ExitCode == 0,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"git {string.Join(' ', args)} failed ({process.ExitCode}): {stderr}"));
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_work, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
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
    }
}
