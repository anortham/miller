using System.Text.Json;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the user-global scan-admission lease. <see cref="ScanGovernor.TryAcquire"/> takes an OS-level exclusive
/// lock on <c>&lt;millerHome&gt;/scan/scan-v1.lock</c>: the first caller is admitted, a second waits out its
/// budget and is REFUSED with null (never a TimeoutException), and releasing the first makes it re-acquirable.
/// The genuinely cross-PROCESS variant is the Scale suite; here a second <see cref="ScanGovernor"/> instance over
/// the same home stands in for "another Miller" — it exercises the same <c>FileShare.None</c> exclusion the OS
/// enforces between processes.
/// </summary>
public sealed class ScanGovernorTests
{
    private sealed class TempMillerDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "miller-scan-" + Guid.NewGuid().ToString("N"));

        public TempMillerDir() => Directory.CreateDirectory(Path);

        public string ScanDir => System.IO.Path.Combine(Path, "scan");

        public string LockFile => System.IO.Path.Combine(ScanDir, ScanGovernor.LockFileName);

        public string OwnerFile => System.IO.Path.Combine(ScanDir, ScanGovernor.OwnerFileName);

        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static ScanGovernorRequest Request(string root = "/repo", string reason = "test", int jobs = 4) =>
        new(root, reason, jobs);

    private static readonly TimeSpan ShortBudget = TimeSpan.FromMilliseconds(150);

    [Fact]
    public void TryAcquire_OnAFreeLease_IsAdmitted_AndCreatesTheVersionedLockFile()
    {
        using var dir = new TempMillerDir();
        ScanGovernor governor = ScanGovernor.ForMillerHome(dir.Path);

        using ScanGovernorLease? lease = governor.TryAcquire(Request(), ShortBudget, CancellationToken.None);

        Assert.NotNull(lease);
        Assert.True(File.Exists(dir.LockFile));
    }

    [Fact]
    public void TryAcquire_WhileHeld_ReturnsNullAfterTheBudget()
    {
        using var dir = new TempMillerDir();
        using ScanGovernorLease? held = ScanGovernor.ForMillerHome(dir.Path)
            .TryAcquire(Request(), ShortBudget, CancellationToken.None);
        Assert.NotNull(held);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        ScanGovernorLease? refused = ScanGovernor.ForMillerHome(dir.Path)
            .TryAcquire(Request("/other"), ShortBudget, CancellationToken.None);
        clock.Stop();

        Assert.Null(refused);
        Assert.True(clock.Elapsed >= ShortBudget, $"waited {clock.Elapsed} for a {ShortBudget} budget");
    }

    [Fact]
    public void Dispose_ReleasesAdmission_SoTheNextAcquireSucceeds()
    {
        using var dir = new TempMillerDir();
        ScanGovernor governor = ScanGovernor.ForMillerHome(dir.Path);

        ScanGovernorLease? first = governor.TryAcquire(Request(), ShortBudget, CancellationToken.None);
        Assert.NotNull(first);
        first!.Dispose();

        using ScanGovernorLease? second = governor.TryAcquire(Request(), ShortBudget, CancellationToken.None);
        Assert.NotNull(second);
    }

    [Fact]
    public void TryAcquire_ReEnteredOnTheSameThread_Throws()
    {
        using var dir = new TempMillerDir();
        ScanGovernor governor = ScanGovernor.ForMillerHome(dir.Path);

        using ScanGovernorLease? outer = governor.TryAcquire(Request(), ShortBudget, CancellationToken.None);
        Assert.NotNull(outer);

        Assert.Throws<InvalidOperationException>(
            () => governor.TryAcquire(Request("/other"), ShortBudget, CancellationToken.None));
    }

    [Fact]
    public void TryAcquire_FromASecondThread_Queues_AndSucceedsOnceTheFirstReleases()
    {
        using var dir = new TempMillerDir();
        ScanGovernor governor = ScanGovernor.ForMillerHome(dir.Path);

        ScanGovernorLease? first = governor.TryAcquire(Request(), ShortBudget, CancellationToken.None);
        Assert.NotNull(first);

        Exception? failure = null;
        bool admitted = false;
        var contender = new Thread(() =>
        {
            try
            {
                using ScanGovernorLease? second = governor.TryAcquire(
                    Request("/other"), TimeSpan.FromSeconds(10), CancellationToken.None);
                admitted = second is not null;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        contender.Start();
        Thread.Sleep(50);
        first!.Dispose();
        Assert.True(contender.Join(TimeSpan.FromSeconds(10)));

        Assert.Null(failure);
        Assert.True(admitted);
    }

    [Fact]
    public void TryAcquire_WithAnAlreadyCancelledToken_Throws_WithoutBurningTheBudget()
    {
        using var dir = new TempMillerDir();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(
            () => ScanGovernor.ForMillerHome(dir.Path)
                .TryAcquire(Request(), TimeSpan.FromMinutes(30), cancellation.Token));
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"cancellation waited {clock.Elapsed}");
    }

    [Fact]
    public void TryAcquire_CancelledWhileQueued_Throws_WithoutBurningTheBudget()
    {
        using var dir = new TempMillerDir();
        using ScanGovernorLease? held = ScanGovernor.ForMillerHome(dir.Path)
            .TryAcquire(Request(), ShortBudget, CancellationToken.None);
        Assert.NotNull(held);

        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(
            () => ScanGovernor.ForMillerHome(dir.Path)
                .TryAcquire(Request("/other"), TimeSpan.FromMinutes(30), cancellation.Token));
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"cancellation waited {clock.Elapsed}");
    }

    [Fact]
    public void OwnerRecord_RoundTripsTheRequest_AndIsRemovedOnDispose()
    {
        using var dir = new TempMillerDir();
        ScanGovernor governor = ScanGovernor.ForMillerHome(dir.Path);

        ScanGovernorLease? lease = governor.TryAcquire(
            new ScanGovernorRequest("/repo/worktree-a", "leader-ondemand", 3),
            ShortBudget,
            CancellationToken.None);
        Assert.NotNull(lease);

        ScanGovernorOwner? owner = governor.TryReadOwner();
        Assert.NotNull(owner);
        Assert.Equal(Environment.ProcessId, owner!.Pid);
        Assert.Equal("/repo/worktree-a", owner.WorkspaceRoot);
        Assert.Equal("leader-ondemand", owner.Reason);
        Assert.Equal(3, owner.Jobs);
        Assert.True(owner.StartedAtUtc > DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5));

        lease!.Dispose();

        Assert.False(File.Exists(dir.OwnerFile));
        Assert.Null(governor.TryReadOwner());
    }

    [Fact]
    public void TryReadOwner_OnMissingTruncatedOrGarbageRecords_ReturnsNull()
    {
        using var dir = new TempMillerDir();
        ScanGovernor governor = ScanGovernor.ForMillerHome(dir.Path);

        Assert.Null(governor.TryReadOwner());

        Directory.CreateDirectory(dir.ScanDir);
        File.WriteAllText(dir.OwnerFile, "{\"pid\": 1234, \"workspace_root\": \"/repo\"");
        Assert.Null(governor.TryReadOwner());

        File.WriteAllText(dir.OwnerFile, "definitely not json");
        Assert.Null(governor.TryReadOwner());
    }

    [Fact]
    public void TryAcquire_WithAStaleOwnerRecordNamingALivePid_StillSucceeds()
    {
        using var dir = new TempMillerDir();
        ScanGovernor governor = ScanGovernor.ForMillerHome(dir.Path);
        Directory.CreateDirectory(dir.ScanDir);
        File.WriteAllText(
            dir.OwnerFile,
            JsonSerializer.Serialize(new
            {
                pid = Environment.ProcessId,
                workspace_root = "/some/other/worktree",
                reason = "leader-ondemand",
                jobs = 4,
                started_at_utc = DateTimeOffset.UtcNow,
            }));

        using ScanGovernorLease? lease = governor.TryAcquire(Request(), ShortBudget, CancellationToken.None);

        Assert.NotNull(lease);
    }

    // Built through FromEnvValue with a REAL directory, not Disabled(): the off instance constructed with a null
    // directory has no relationship to any temp dir, so asserting that dir stayed empty proved nothing.
    [Fact]
    public void Disabled_AlwaysAdmits_AndTouchesNoFilesUnderItsMillerHome()
    {
        using var dir = new TempMillerDir();
        ScanGovernor governor = ScanGovernor.FromEnvValue("0", dir.Path);

        using ScanGovernorLease? first = governor.TryAcquire(Request(), TimeSpan.Zero, CancellationToken.None);
        using ScanGovernorLease? second = governor.TryAcquire(Request(), TimeSpan.Zero, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.False(governor.Enabled);
        Assert.Null(governor.TryReadOwner());
        Assert.False(Directory.Exists(Path.Combine(dir.Path, ScanGovernor.DirectoryName)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Path));
    }

    [Fact]
    public void FromEnvValue_TreatsTheSameFalsyTokensAsTheSearchSidecarFlag()
    {
        using var dir = new TempMillerDir();

        Assert.False(ScanGovernor.FromEnvValue("0", dir.Path).Enabled);
        Assert.False(ScanGovernor.FromEnvValue("Off", dir.Path).Enabled);
        Assert.False(ScanGovernor.FromEnvValue("FALSE", dir.Path).Enabled);
        Assert.False(ScanGovernor.FromEnvValue("no", dir.Path).Enabled);
        Assert.True(ScanGovernor.FromEnvValue(null, dir.Path).Enabled);
        Assert.True(ScanGovernor.FromEnvValue("1", dir.Path).Enabled);
        Assert.True(ScanGovernor.FromEnvValue("anything-else", dir.Path).Enabled);
    }

    [Theory]
    [InlineData(null, 30 * 60)]
    [InlineData("", 30 * 60)]
    [InlineData("45", 45)]
    [InlineData("0", 0)]
    [InlineData("00:00:30", 30)]
    [InlineData("-5", 30 * 60)]
    [InlineData("NaN", 30 * 60)]
    [InlineData("Infinity", 30 * 60)]
    [InlineData("1e400", 30 * 60)]
    [InlineData("banana", 30 * 60)]
    public void ParseWait_FallsBackToTheDefaultForEveryUnusableValue(string? raw, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), ScanGovernor.ParseWait(raw));
    }

    [Fact]
    public void PollDelays_AreJitteredWithinTheDocumentedBand()
    {
        using var dir = new TempMillerDir();
        using ScanGovernorLease? held = ScanGovernor.ForMillerHome(dir.Path)
            .TryAcquire(Request(), ShortBudget, CancellationToken.None);
        Assert.NotNull(held);

        var delays = new List<TimeSpan>();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ScanGovernor.ForMillerHome(dir.Path).TryAcquire(
                Request("/other"),
                TimeSpan.FromMinutes(5),
                CancellationToken.None,
                delay =>
                {
                    delays.Add(delay);
                    if (delays.Count >= 16)
                        throw new OperationCanceledException();
                }));

        Assert.Equal(16, delays.Count);
        Assert.All(delays, delay => Assert.InRange(delay, ScanGovernor.MinPollDelay, ScanGovernor.MaxPollDelay));
        Assert.True(delays.Distinct().Count() > 1, "poll delays were not jittered");
    }
}
