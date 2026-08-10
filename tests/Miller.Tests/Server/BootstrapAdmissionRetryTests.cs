using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Hosting;
using Miller.Server.Workspaces;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the bootstrap's answer to a machine-wide scan-admission timeout. A fleet worktree hit the ten-minute
/// admission budget, threw, and then never retried — the server sat unbound for the next fifty minutes until it
/// was restarted by hand (2026-08-06 P4 scale validation §3). The timeout is contention, not a workspace fault,
/// so it self-heals; every other bootstrap failure stays terminal.
/// </summary>
public sealed class BootstrapAdmissionRetryTests : IDisposable
{
    private readonly List<string> _temporaryDirectories = [];

    [Fact]
    public void AnAdmissionTimeoutFailureRetriesUntilTheBootstrapBinds()
    {
        string root = NewTempDirectory("retry-root");
        string home = NewTempDirectory("retry-home");
        using var bootstrap = NewBootstrap(home, TimeSpan.FromMilliseconds(20));

        int runs = 0;
        BootstrapPhase? phaseDuringRetry = null;
        bootstrap.TestRunBootstrapOverride = canonicalRoot =>
        {
            if (Interlocked.Increment(ref runs) == 1)
                throw new ScanAdmissionTimeoutException("Timed out waiting for machine-wide scan admission.");

            phaseDuringRetry = bootstrap.Snapshot.Phase;
            Bind(bootstrap, canonicalRoot, home);
        };

        bootstrap.BootstrapForRoot(root, WorkspaceBindingResolver.WorkspaceSource.Cwd);

        Assert.True(WaitUntil(() => bootstrap.IsBound));
        Assert.Equal(2, Volatile.Read(ref runs));
        Assert.Equal(BootstrapPhase.Running, phaseDuringRetry);
    }

    [Fact]
    public void AStoreRollbackFailureRetriesUntilTheBootstrapBinds()
    {
        string root = NewTempDirectory("rollback-retry-root");
        string home = NewTempDirectory("rollback-retry-home");
        using var bootstrap = NewBootstrap(home, TimeSpan.FromMilliseconds(20));

        int runs = 0;
        BootstrapPhase? phaseDuringRetry = null;
        bootstrap.TestRunBootstrapOverride = canonicalRoot =>
        {
            if (Interlocked.Increment(ref runs) == 1)
                throw new StoreRollbackRetryException(new IOException("store export temporarily unavailable"));

            phaseDuringRetry = bootstrap.Snapshot.Phase;
            Bind(bootstrap, canonicalRoot, home);
        };

        bootstrap.BootstrapForRoot(root, WorkspaceBindingResolver.WorkspaceSource.Cwd);

        Assert.True(WaitUntil(() => bootstrap.IsBound));
        Assert.Equal(2, Volatile.Read(ref runs));
        Assert.Equal(BootstrapPhase.Running, phaseDuringRetry);
    }

    [Fact]
    public void ADeterministicBootstrapFailureStaysTerminal()
    {
        string root = NewTempDirectory("terminal-root");
        string home = NewTempDirectory("terminal-home");
        using var bootstrap = NewBootstrap(home, TimeSpan.FromMilliseconds(20));

        int runs = 0;
        bootstrap.TestRunBootstrapOverride = _ =>
        {
            Interlocked.Increment(ref runs);
            throw new InvalidOperationException("julie-extract is missing from the tools root.");
        };

        bootstrap.BootstrapForRoot(root, WorkspaceBindingResolver.WorkspaceSource.Cwd);

        Assert.True(WaitUntil(() => bootstrap.Snapshot.Phase == BootstrapPhase.Failed));
        Thread.Sleep(TimeSpan.FromMilliseconds(300));

        Assert.Equal(1, Volatile.Read(ref runs));
        Assert.Equal(BootstrapPhase.Failed, bootstrap.Snapshot.Phase);
    }

    [Fact]
    public void ARetryWhoseGenerationAdvancedDoesNotStartASecondRun()
    {
        string root = NewTempDirectory("stale-root");
        string home = NewTempDirectory("stale-home");
        using var bootstrap = NewBootstrap(home, TimeSpan.FromMilliseconds(400));

        int runs = 0;
        bootstrap.TestRunBootstrapOverride = _ =>
        {
            if (Interlocked.Increment(ref runs) == 1)
                throw new ScanAdmissionTimeoutException("Timed out waiting for machine-wide scan admission.");
            throw new InvalidOperationException("the replacement run failed deterministically.");
        };

        bootstrap.BootstrapForRoot(root, WorkspaceBindingResolver.WorkspaceSource.Cwd);
        Assert.True(WaitUntil(() =>
            bootstrap.Snapshot.Phase == BootstrapPhase.Failed && Volatile.Read(ref runs) == 1));
        int timedOutGeneration = bootstrap.Snapshot.RunGeneration;

        bootstrap.RebootstrapForReplacedRoot(PathCanonicalizer.CanonicalizeRoot(root));
        Assert.True(WaitUntil(() =>
            bootstrap.Snapshot.Phase == BootstrapPhase.Failed && Volatile.Read(ref runs) == 2));
        Thread.Sleep(TimeSpan.FromMilliseconds(700));

        Assert.Equal(2, Volatile.Read(ref runs));
        Assert.NotEqual(timedOutGeneration, bootstrap.Snapshot.RunGeneration);
    }

    [Fact]
    public void ARetryWhoseRootVanishedDuringTheDelayNeverStartsARun()
    {
        string root = NewTempDirectory("vanished-root");
        string home = NewTempDirectory("vanished-home");
        using var bootstrap = NewBootstrap(home, TimeSpan.FromSeconds(1));

        int runs = 0;
        bootstrap.TestRunBootstrapOverride = _ =>
        {
            Interlocked.Increment(ref runs);
            throw new ScanAdmissionTimeoutException("Timed out waiting for machine-wide scan admission.");
        };

        bootstrap.BootstrapForRoot(root, WorkspaceBindingResolver.WorkspaceSource.Cwd);
        Assert.True(WaitUntil(() => Directory.Exists(Path.Combine(root, ".miller"))));
        Directory.Delete(root, recursive: true);
        Thread.Sleep(TimeSpan.FromSeconds(2));

        Assert.Equal(1, Volatile.Read(ref runs));
        Assert.Equal(BootstrapPhase.Failed, bootstrap.Snapshot.Phase);
        Assert.False(Directory.Exists(root));
        Assert.False(Directory.Exists(Path.Combine(root, ".miller")));
    }

    [Fact]
    public void ARetryWhoseRootWasReplacedByASymlinkNeverStartsARun()
    {
        SkipIfNoSymlinks();
        string root = NewTempDirectory("swapped-root");
        string replacement = NewTempDirectory("swapped-replacement");
        string home = NewTempDirectory("swapped-home");
        using var bootstrap = NewBootstrap(home, TimeSpan.FromSeconds(1));

        int runs = 0;
        bootstrap.TestRunBootstrapOverride = _ =>
        {
            Interlocked.Increment(ref runs);
            throw new ScanAdmissionTimeoutException("Timed out waiting for machine-wide scan admission.");
        };

        bootstrap.BootstrapForRoot(root, WorkspaceBindingResolver.WorkspaceSource.Cwd);
        Assert.True(WaitUntil(() => Directory.Exists(Path.Combine(root, ".miller"))));
        Directory.Delete(root, recursive: true);
        try
        {
            Directory.CreateSymbolicLink(root, replacement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"Symlink creation is unavailable on this host: {ex.Message}");
        }

        Thread.Sleep(TimeSpan.FromSeconds(2));

        Assert.Equal(
            PathCanonicalizer.CanonicalizeRoot(replacement), PathCanonicalizer.CanonicalizeRoot(root));
        Assert.Equal(1, Volatile.Read(ref runs));
        Assert.Equal(BootstrapPhase.Failed, bootstrap.Snapshot.Phase);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(-4.0)]
    [InlineData(9.0)]
    public void TheAdmissionRetryDelayStaysInsideTheJitterBand(double sample)
    {
        var baseDelay = TimeSpan.FromSeconds(60);

        var delay = IndexBootstrapService.JitterAdmissionRetryDelay(baseDelay, sample);

        Assert.InRange(delay, baseDelay, baseDelay * 1.25);
    }

    [Fact]
    public void TheAdmissionRetryDelayIsTheBaseDelayPlusItsJitterShare()
    {
        var baseDelay = TimeSpan.FromSeconds(60);

        Assert.Equal(baseDelay, IndexBootstrapService.JitterAdmissionRetryDelay(baseDelay, 0.0));
        Assert.Equal(TimeSpan.FromSeconds(75), IndexBootstrapService.JitterAdmissionRetryDelay(baseDelay, 1.0));
    }

    [Fact]
    public void AnIneligibleRebindLogsWhyTheBootstrapFellBackToAFullScan()
    {
        var logger = new RecordingLogger();

        IndexBootstrapService.LogRebindFallback(
            logger, "/repo/wt", RebindBootstrapOutcome.Ineligible("the workspace is not a linked worktree"));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("the workspace is not a linked worktree", entry.Message, StringComparison.Ordinal);
        Assert.Contains("/repo/wt", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedRebindKeepsItsWarning()
    {
        var logger = new RecordingLogger();

        IndexBootstrapService.LogRebindFallback(
            logger,
            "/repo/wt",
            RebindBootstrapOutcome.Failed(
                RebindStage.Copy, "the snapshot copy was interrupted", "/repo/main", "miller-abc"));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("the snapshot copy was interrupted", entry.Message, StringComparison.Ordinal);
        Assert.Contains("/repo/main", entry.Message, StringComparison.Ordinal);
    }

    private static void SkipIfNoSymlinks()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Symbolic-link creation requires elevation / Developer Mode on Windows; POSIX-only test.");
    }

    private static IndexBootstrapService NewBootstrap(string home, TimeSpan retryDelay) =>
        new(NullLogger<IndexBootstrapService>.Instance)
        {
            TestHomeDirectoryOverride = home,
            TestAdmissionRetryDelay = retryDelay,
        };

    private static void Bind(IndexBootstrapService bootstrap, string canonicalRoot, string home)
    {
        var workspace = WorkspaceContext.Create(canonicalRoot, AppContext.BaseDirectory, home) with
        {
            WorkspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot),
            CanonicalRoot = canonicalRoot,
            CanonicalExtractDbPath = Path.Combine(canonicalRoot, ".miller", "symbols.db"),
        };
        bootstrap.SeedForTest(
            workspace, new IndexHolder(MillerRepositoryIndex.Build([]), builtRevision: 1));
    }

    private static bool WaitUntil(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
                return true;
            Thread.Sleep(5);
        }

        return condition();
    }

    private string NewTempDirectory(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), $"miller-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _temporaryDirectories.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (string path in _temporaryDirectories)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class RecordingLogger : ILogger<IndexBootstrapService>
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                    return _entries.ToArray();
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
            lock (_entries)
                _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }
}
