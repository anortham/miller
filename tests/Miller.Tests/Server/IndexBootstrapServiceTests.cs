using Microsoft.Data.Sqlite;
using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Telemetry;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the bootstrap's seed-revision discipline (finding-5, decision-10): a MISSING extract DB is the only
/// safe degrade-to-revision-0 case (the workspace genuinely has no revision yet). A present-but-unreadable DB
/// (corruption, the WAL writable-dir violation, a lock) is an operator/config error that must surface LOUDLY
/// rather than silently seeding revision 0 — which would mask the problem and trigger a spurious first-tick
/// rebuild. <see cref="IndexBootstrapService.ReadLatestRevisionOrZero"/> is the testable seam (the full
/// <c>Run()</c> needs the live binary + CWD and is exercised by the Scale suite).
/// </summary>
public sealed class IndexBootstrapServiceTests
{
    [Fact]
    public void DecideBootstrapScan_MissingDb_DeltaScansBeforeFirstLoad()
    {
        var decision = IndexBootstrapService.DecideBootstrapScan(
            dbExists: false,
            existingRootPath: null,
            canonicalRoot: "/work/repo");

        Assert.True(decision.ShouldScan);
        Assert.False(decision.Force);
        Assert.Equal(WorkspaceRegistryState.Ready, decision.RegistryStateAfterLoad);
    }

    [Fact]
    public void DecideBootstrapScan_ExistingDbWithMatchingRootPath_LoadsExistingWithoutScan()
    {
        // v1 identity is the recorded canonical root_path; a match reuses the DB (reconciliation #14).
        var decision = IndexBootstrapService.DecideBootstrapScan(
            dbExists: true,
            existingRootPath: "/work/repo",
            canonicalRoot: "/work/repo");

        Assert.False(decision.ShouldScan);
        Assert.False(decision.Force);
        Assert.Equal(WorkspaceRegistryState.LoadedExisting, decision.RegistryStateAfterLoad);
    }

    [Fact]
    public void DecideBootstrapScan_ExistingDbWithWindowsVerbatimRootPath_LoadsExistingWithoutScan()
    {
        // The exact AccessIQ failure: julie-extract recorded root_path=\\?\C:\source\AccessIQ (Rust canonicalize
        // verbatim prefix) but Miller's canonical root is C:\source\AccessIQ. These identify the SAME workspace, so
        // bootstrap must REUSE the DB — not force a 30s+ rescan that trips the MCP connect timeout on every launch.
        var decision = IndexBootstrapService.DecideBootstrapScan(
            dbExists: true,
            existingRootPath: @"\\?\C:\source\AccessIQ",
            canonicalRoot: @"C:\source\AccessIQ");

        Assert.False(decision.ShouldScan);
        Assert.False(decision.Force);
        Assert.Equal(WorkspaceRegistryState.LoadedExisting, decision.RegistryStateAfterLoad);
    }

    [Theory]
    [InlineData(null)]                       // pre-v1 artifact with no root_path key
    [InlineData("")]                         // empty recorded root
    [InlineData("/work/other-repo")]         // a different workspace's DB at this path
    [InlineData("/work/repo/")]              // a non-identical (trailing-slash) spelling never matches ordinally
    public void DecideBootstrapScan_ExistingDbWithMissingOrMismatchedRootPath_ForceScansBeforeLoad(string? existingRootPath)
    {
        var decision = IndexBootstrapService.DecideBootstrapScan(
            dbExists: true,
            existingRootPath: existingRootPath,
            canonicalRoot: "/work/repo");

        Assert.True(decision.ShouldScan);
        Assert.True(decision.Force);
        Assert.Equal(WorkspaceRegistryState.Ready, decision.RegistryStateAfterLoad);
    }

    [Theory]
    [InlineData(null, "/work/repo", false)]            // pre-v1 (no key) never matches
    [InlineData("", "/work/repo", false)]              // empty never matches
    [InlineData("/work/repo", "/work/repo", true)]     // exact canonical match
    [InlineData("/work/other", "/work/repo", false)]   // different root
    // Rust's std::fs::canonicalize (julie-extract) records the Windows extended-length verbatim prefix `\\?\`.
    // The two canonical roots are otherwise identical, so the recorded root MUST match after the prefix is
    // stripped — else a Windows workspace force-rescans every startup. Cross-platform: stripping leaves identical
    // case-identical strings.
    [InlineData(@"\\?\C:\source\AccessIQ", @"C:\source\AccessIQ", true)]
    [InlineData(@"\\?\UNC\server\share\repo", @"\\server\share\repo", true)]
    public void RootPathsEqual_NormalizesWindowsVerbatimPrefixBeforeMatch(string? recorded, string canonical, bool expected)
    {
        Assert.Equal(expected, IndexBootstrapService.RootPathsEqual(recorded, canonical));
    }

    [Theory]
    [InlineData(true, false, StringComparison.OrdinalIgnoreCase)]
    [InlineData(false, true, StringComparison.OrdinalIgnoreCase)]
    [InlineData(false, false, StringComparison.Ordinal)]
    public void RootPathComparison_MatchesWorkspaceIdentityPolicy(
        bool isWindows, bool isMacOS, StringComparison expected)
    {
        Assert.Equal(expected, IndexBootstrapService.RootPathComparison(isWindows, isMacOS));
    }

    [Fact]
    public void RootPathsEqual_OnCaseInsensitiveHosts_IsCaseInsensitive()
    {
        // Windows/macOS-default filesystems are case-insensitive; Rust's canonicalize (on-disk case) and Miller's GetFullPath
        // (as-launched case) can disagree on case for the SAME root, which must NOT force a rescan.
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            return; // POSIX is case-sensitive — covered by RootPathsEqual_OnPosix_IsCaseSensitive.
        Assert.True(IndexBootstrapService.RootPathsEqual(@"\\?\C:\Source\AccessIQ", @"C:\source\accessiq"));
    }

    [Fact]
    public void RootPathsEqual_OnPosix_IsCaseSensitive()
    {
        // POSIX filesystems are case-sensitive: /work/Repo and /work/repo are DIFFERENT directories, so the
        // recorded root must not match — Windows case-folding must never leak to POSIX.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            return;
        Assert.False(IndexBootstrapService.RootPathsEqual("/work/Repo", "/work/repo"));
    }

    // ---- auto-rebuild: an incompatible-but-root-matching DB force-rescans once instead of crashing startup ----

    [Fact]
    public void LoadIndexWithAutoRebuild_CompatibleDb_LoadsOnceAndNeverRescans()
    {
        // The reuse happy path: the DB is compatible, so the index loads on the first try and the force-rescan
        // escape hatch is never touched (no needless full scan on every healthy startup).
        int loads = 0, rescans = 0;
        var result = IndexBootstrapService.LoadIndexWithAutoRebuild(
            load: () => { loads++; return "index"; },
            forceRescan: _ => { rescans++; return 5L; },
            onBeforeRetry: () => Assert.Fail("onBeforeRetry must not fire for a compatible DB"),
            onIncompatible: _ => Assert.Fail("onIncompatible must not fire for a compatible DB"),
            onCorrupt: _ => Assert.Fail("onCorrupt must not fire for a compatible DB"));

        Assert.Equal("index", result.Index);
        Assert.False(result.Rebuilt);
        Assert.Null(result.RebuiltRevision);
        Assert.Equal(1, loads);
        Assert.Equal(0, rescans);
    }

    [Fact]
    public void LoadIndexWithAutoRebuild_RunsTheBarrier_AfterRescanAndBeforeRetry()
    {
        // Regression guard for the SQLite pool-staleness bug: a force rebuild REPLACES the DB file, so the failed
        // first load's pooled read connection would re-read the OLD inode on retry. onBeforeRetry (wired to
        // SqliteConnection.ClearAllPools in production) MUST run exactly once, AFTER forceRescan rewrote the DB and
        // BEFORE the retry load — otherwise the auto-heal deterministically re-throws the incompatibility.
        var order = new List<string>();
        int loads = 0;
        var result = IndexBootstrapService.LoadIndexWithAutoRebuild<string>(
            load: () =>
            {
                loads++;
                order.Add("load" + loads);
                if (loads == 1)
                    throw new IncompatibleExtractException("DB schema is 1 but this Miller build expects 2");
                return "rebuilt";
            },
            forceRescan: _ => { order.Add("rescan"); return 3L; },
            onBeforeRetry: () => order.Add("barrier"),
            onIncompatible: _ => order.Add("warn"),
            onCorrupt: _ => Assert.Fail("onCorrupt must not fire for an incompatible DB"));

        Assert.Equal("rebuilt", result.Index);
        Assert.True(result.Rebuilt);
        Assert.Equal(new[] { "load1", "warn", "rescan", "barrier", "load2" }, order);
    }

    [Fact]
    public void LoadIndexWithAutoRebuild_IncompatibleDb_ForceRescansOnceThenReloads()
    {
        // The bug this fixes: a stale schema-1 DB (after a julie-extract schema bump) used to crash bootstrap.
        // Now the first load throws IncompatibleExtractException, we force-rescan exactly once, and the reload
        // succeeds — the server self-heals instead of failing to connect. The rebuilt revision is reported back
        // so the holder + registry record the scan.
        int loads = 0, rescans = 0, warned = 0, barriers = 0;
        var result = IndexBootstrapService.LoadIndexWithAutoRebuild(
            load: () =>
            {
                loads++;
                if (loads == 1)
                    throw new IncompatibleExtractException("DB schema is 1 but this Miller build expects 2");
                return "rebuilt-index";
            },
            forceRescan: _ => { rescans++; return 7L; },
            onBeforeRetry: () => barriers++,
            onIncompatible: _ => warned++,
            onCorrupt: _ => Assert.Fail("onCorrupt must not fire for an incompatible DB"));

        Assert.Equal("rebuilt-index", result.Index);
        Assert.True(result.Rebuilt);
        Assert.Equal(7L, result.RebuiltRevision);
        Assert.Equal(2, loads);   // initial attempt (threw) + one retry after the rebuild
        Assert.Equal(1, rescans); // exactly one force-rescan
        Assert.Equal(1, barriers); // exactly one pool barrier before the retry
        Assert.Equal(1, warned);  // the operator is told the DB was incompatible and is being rebuilt
    }

    [Fact]
    public void LoadIndexWithAutoRebuild_StillIncompatibleAfterRebuild_PropagatesWithoutLooping()
    {
        // The loud-failure guard: if the freshly-rebuilt DB is STILL incompatible, the bundled julie-extract does
        // not match this Miller build — a real config error. We must rethrow the ORIGINAL exception after exactly
        // one rebuild attempt, never loop forever force-rescanning.
        int loads = 0, rescans = 0;
        var boom = new IncompatibleExtractException("still schema 1 after rebuild — bundled tool mismatch");

        var thrown = Assert.Throws<IncompatibleExtractException>(() =>
            IndexBootstrapService.LoadIndexWithAutoRebuild<string>(
                load: () => { loads++; throw boom; },
                forceRescan: _ => { rescans++; return 1L; },
                onBeforeRetry: () => { },
                onIncompatible: _ => { },
                onCorrupt: _ => { }));

        Assert.Same(boom, thrown);
        Assert.Equal(2, loads);   // initial + a single retry, then give up
        Assert.Equal(1, rescans); // rescanned exactly once — no infinite loop
    }

    [Fact]
    public void LoadIndexWithAutoRebuild_CorruptDb_ForceRescansOnceThenReloads()
    {
        // The reliability gap this closes: a torn/half-written symbols.db (a writer killed mid-scan) raises
        // SqliteException(SQLITE_CORRUPT/NOTADB) on load and used to crash startup. Now it self-heals exactly like
        // the incompatible path — force-rebuild once, reload — instead of failing to connect.
        int loads = 0, rescans = 0, corrupt = 0, barriers = 0;
        var result = IndexBootstrapService.LoadIndexWithAutoRebuild(
            load: () =>
            {
                loads++;
                if (loads == 1)
                    throw new SqliteException("database disk image is malformed", 11 /* SQLITE_CORRUPT */);
                return "rebuilt-index";
            },
            forceRescan: _ => { rescans++; return 9L; },
            onBeforeRetry: () => barriers++,
            onIncompatible: _ => Assert.Fail("onIncompatible must not fire for a corrupt DB"),
            onCorrupt: _ => corrupt++);

        Assert.Equal("rebuilt-index", result.Index);
        Assert.True(result.Rebuilt);
        Assert.Equal(9L, result.RebuiltRevision);
        Assert.Equal(2, loads);
        Assert.Equal(1, rescans);
        Assert.Equal(1, barriers);
        Assert.Equal(1, corrupt);
    }

    [Fact]
    public void LoadIndexWithAutoRebuild_NonCorruptionSqliteError_IsNotSwallowed()
    {
        // Guard the narrow catch: a NON-corruption SqliteException (e.g. SQLITE_BUSY) must propagate, not trigger a
        // needless rebuild — only the corruption codes (11/26) self-heal.
        int rescans = 0;
        var busy = new SqliteException("database is locked", 5 /* SQLITE_BUSY */);

        var thrown = Assert.Throws<SqliteException>(() =>
            IndexBootstrapService.LoadIndexWithAutoRebuild<string>(
                load: () => throw busy,
                forceRescan: _ => { rescans++; return 1L; },
                onBeforeRetry: () => { },
                onIncompatible: _ => { },
                onCorrupt: _ => { }));

        Assert.Same(busy, thrown);
        Assert.Equal(0, rescans); // never rebuilt for a non-corruption error
    }

    [Fact]
    public void LoadIndexWithAutoRebuild_RescanSkippedBecauseTheLockWasBusy_ReportsNotRebuiltAndStillRunsTheBarrier()
    {
        int loads = 0, barriers = 0;

        var result = IndexBootstrapService.LoadIndexWithAutoRebuild<string>(
            load: () =>
            {
                loads++;
                if (loads == 1)
                    throw new IncompatibleExtractException("DB schema is 1 but this Miller build expects 2");
                return "winner-artifact";
            },
            forceRescan: _ => null,
            onBeforeRetry: () => barriers++,
            onIncompatible: _ => { },
            onCorrupt: _ => { });

        Assert.Equal("winner-artifact", result.Index);
        Assert.False(result.Rebuilt);
        Assert.Null(result.RebuiltRevision);
        Assert.Equal(2, loads);
        Assert.Equal(1, barriers);
    }

    [Fact]
    public void LoadIndexWithAutoRebuild_RescanRan_ReportsRebuiltWithItsRevision()
    {
        int loads = 0, barriers = 0;

        var result = IndexBootstrapService.LoadIndexWithAutoRebuild<string>(
            load: () =>
            {
                loads++;
                if (loads == 1)
                    throw new IncompatibleExtractException("DB schema is 1 but this Miller build expects 2");
                return "rebuilt";
            },
            forceRescan: _ => 11L,
            onBeforeRetry: () => barriers++,
            onIncompatible: _ => { },
            onCorrupt: _ => { });

        Assert.True(result.Rebuilt);
        Assert.Equal(11L, result.RebuiltRevision);
        Assert.Equal(1, barriers);
    }

    [Fact]
    public void AcquireBootstrapScanLease_AcquiredOnFirstAttempt_ReturnsTheLeaseAndNeverSleeps()
    {
        var lease = new FakeLease();
        int attempts = 0, decisions = 0, usableProbes = 0, sleeps = 0;

        var result = IndexBootstrapService.AcquireBootstrapScanLease(
            tryAcquire: () => { attempts++; return lease; },
            decide: () => { decisions++; return Scan(force: false); },
            winnerArtifactUsable: () => { usableProbes++; return false; },
            wait: TimeSpan.FromMinutes(10),
            pollInterval: TimeSpan.FromMilliseconds(500),
            utcNow: () => DateTimeOffset.UnixEpoch,
            sleep: _ => sleeps++);

        Assert.Equal(IndexBootstrapService.BootstrapLeaseOutcome.Acquired, result.Outcome);
        Assert.Same(lease, result.Lease);
        Assert.True(result.Decision.ShouldScan);
        Assert.Equal(1, attempts);
        Assert.Equal(1, decisions);
        Assert.Equal(0, usableProbes);
        Assert.Equal(0, sleeps);
    }

    [Fact]
    public void AcquireBootstrapScanLease_PostLockRecheckSaysAnotherInstanceAlreadyHealedIt_ReturnsTheRecheckedDecision()
    {
        var result = IndexBootstrapService.AcquireBootstrapScanLease(
            tryAcquire: () => new FakeLease(),
            decide: Reuse,
            winnerArtifactUsable: () => false,
            wait: TimeSpan.FromMinutes(10),
            pollInterval: TimeSpan.FromMilliseconds(500),
            utcNow: () => DateTimeOffset.UnixEpoch,
            sleep: _ => Assert.Fail("an immediate acquire must not sleep"));

        Assert.Equal(IndexBootstrapService.BootstrapLeaseOutcome.Acquired, result.Outcome);
        Assert.False(result.Decision.ShouldScan);
        Assert.False(result.Decision.Force);
        Assert.Equal(WorkspaceRegistryState.LoadedExisting, result.Decision.RegistryStateAfterLoad);
    }

    [Fact]
    public void AcquireBootstrapScanLease_ContendedUntilTheWinnerArtifactIsUsable_ExitsWithoutWaitingForRelease()
    {
        int usableProbes = 0, sleeps = 0;
        var now = DateTimeOffset.UnixEpoch;

        var result = IndexBootstrapService.AcquireBootstrapScanLease<FakeLease>(
            tryAcquire: () => null,
            decide: Reuse,
            winnerArtifactUsable: () => ++usableProbes == 3,
            wait: TimeSpan.FromMinutes(10),
            pollInterval: TimeSpan.FromMilliseconds(500),
            utcNow: () => now,
            sleep: d => { sleeps++; now += d; });

        Assert.Equal(IndexBootstrapService.BootstrapLeaseOutcome.WinnerArtifactUsable, result.Outcome);
        Assert.Null(result.Lease);
        Assert.False(result.Decision.ShouldScan);
        Assert.Equal(2, sleeps);
    }

    [Fact]
    public void AcquireBootstrapScanLease_ContendedAndNoArtifactEverAppears_TimesOutWithoutSleepingPastTheDeadline()
    {
        var start = DateTimeOffset.UnixEpoch;
        var wait = TimeSpan.FromSeconds(1);
        var poll = TimeSpan.FromMilliseconds(250);
        var now = start;
        int sleeps = 0;

        var result = IndexBootstrapService.AcquireBootstrapScanLease<FakeLease>(
            tryAcquire: () => null,
            decide: () => Scan(force: true),
            winnerArtifactUsable: () => false,
            wait: wait,
            pollInterval: poll,
            utcNow: () => now,
            sleep: d => { sleeps++; now += d; });

        Assert.Equal(IndexBootstrapService.BootstrapLeaseOutcome.TimedOut, result.Outcome);
        Assert.Null(result.Lease);
        Assert.True(result.Decision.ShouldScan);
        Assert.Equal(4, sleeps);
        Assert.Equal(start + wait, now);
    }

    [Fact]
    public void AcquireBootstrapScanLease_ContendedTwiceThenAcquired_ReturnsTheDecisionEvaluatedAfterAcquisition()
    {
        int attempts = 0;
        var lease = new FakeLease();
        var now = DateTimeOffset.UnixEpoch;
        var decidedAtAttempt = new List<int>();

        var result = IndexBootstrapService.AcquireBootstrapScanLease(
            tryAcquire: () => ++attempts < 3 ? null : lease,
            decide: () =>
            {
                decidedAtAttempt.Add(attempts);
                return attempts >= 3 ? Reuse() : Scan(force: true);
            },
            winnerArtifactUsable: () => false,
            wait: TimeSpan.FromMinutes(10),
            pollInterval: TimeSpan.FromMilliseconds(500),
            utcNow: () => now,
            sleep: d => now += d);

        Assert.Equal(IndexBootstrapService.BootstrapLeaseOutcome.Acquired, result.Outcome);
        Assert.Same(lease, result.Lease);
        Assert.Equal(3, attempts);
        Assert.Equal(new[] { 3 }, decidedAtAttempt);
        Assert.False(result.Decision.ShouldScan);
    }

    [Fact]
    public void AcquireBootstrapScanLease_ZeroWait_AttemptsOnceAndNeverSleeps()
    {
        int attempts = 0;

        var result = IndexBootstrapService.AcquireBootstrapScanLease<FakeLease>(
            tryAcquire: () => { attempts++; return null; },
            decide: () => Scan(force: false),
            winnerArtifactUsable: () => false,
            wait: TimeSpan.Zero,
            pollInterval: TimeSpan.FromMilliseconds(500),
            utcNow: () => DateTimeOffset.UnixEpoch,
            sleep: _ => Assert.Fail("a zero wait must never sleep"));

        Assert.Equal(IndexBootstrapService.BootstrapLeaseOutcome.TimedOut, result.Outcome);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void AcquireBootstrapScanLease_DecideThrowsAfterTheLeaseIsWon_DisposesItAndRethrows()
    {
        var lease = new FakeLease();

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            IndexBootstrapService.AcquireBootstrapScanLease(
                tryAcquire: () => lease,
                decide: () => throw new InvalidOperationException("SQLITE_READONLY_RECOVERY"),
                winnerArtifactUsable: () => false,
                wait: TimeSpan.FromMinutes(10),
                pollInterval: TimeSpan.FromMilliseconds(500),
                utcNow: () => DateTimeOffset.UnixEpoch,
                sleep: _ => Assert.Fail("an immediate acquire must not sleep")));

        Assert.Equal("SQLITE_READONLY_RECOVERY", thrown.Message);
        Assert.True(lease.Disposed);
    }

    [Fact]
    public void AcquireBootstrapScanLease_DecideThrowsOnAContendedOutcome_StillRethrows()
    {
        var now = DateTimeOffset.UnixEpoch;

        Assert.Throws<SqliteException>(() =>
            IndexBootstrapService.AcquireBootstrapScanLease<FakeLease>(
                tryAcquire: () => null,
                decide: () => throw new SqliteException("disk I/O error", 10),
                winnerArtifactUsable: () => true,
                wait: TimeSpan.FromMinutes(10),
                pollInterval: TimeSpan.FromMilliseconds(500),
                utcNow: () => now,
                sleep: d => now += d));
    }

    [Fact]
    public void ReadBootstrapScanDecision_MissingArtifact_ScansWithoutForcing()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-bootstrap-decide-" + Guid.NewGuid().ToString("N"));
        var probe = IndexBootstrapService.ReadBootstrapScanDecision(
            Path.Combine(root, ".miller", "symbols.db"), root);

        Assert.True(probe.Decision.ShouldScan);
        Assert.False(probe.Decision.Force);
        Assert.Null(probe.ExistingRootPath);
    }

    [Fact]
    public void ReadBootstrapScanDecision_ArtifactTooCorruptToRead_ScansInsteadOfFailingTheBootstrap()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-bootstrap-decide-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "symbols.db");
        try
        {
            File.WriteAllText(dbPath, "not a sqlite database, just bytes a torn promote could leave behind");

            var probe = IndexBootstrapService.ReadBootstrapScanDecision(dbPath, dir);

            Assert.True(probe.Decision.ShouldScan);
            Assert.True(probe.Decision.Force);
            Assert.Null(probe.ExistingRootPath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ReadBootstrapScanDecision_ArtifactRecordsAnotherRoot_ForceRebindsAndReportsThatRoot()
    {
        using var fx = JulieDbFixture.CreateDefault();

        var probe = IndexBootstrapService.ReadBootstrapScanDecision(fx.DbPath, "/work/somewhere-else");

        Assert.True(probe.Decision.ShouldScan);
        Assert.True(probe.Decision.Force);
        Assert.Equal("/work/repo", probe.ExistingRootPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    [InlineData("1,5")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e400")]
    [InlineData("1e30")]
    public void ParseBootstrapScanLockWait_AbsentOrInvalidOrNegative_FallsBackToTheDefault(string? raw)
    {
        Assert.Equal(
            IndexBootstrapService.DefaultBootstrapScanLockWait,
            IndexBootstrapService.ParseBootstrapScanLockWait(raw));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("45", 45)]
    [InlineData("1.5", 1.5)]
    public void ParseBootstrapScanLockWait_ValidSeconds_IsHonored(string raw, double expectedSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            IndexBootstrapService.ParseBootstrapScanLockWait(raw));
    }

    [Fact]
    public void RegisterBootstrapWorkspace_LoadedExisting_RecordsStableIdentityAndRevisionWithoutScanTimestamp()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-bootstrap-registry-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(dir, "repo");
        string home = Path.Combine(dir, "home");
        Directory.CreateDirectory(root);
        try
        {
            string canonicalRoot = Path.GetFullPath(root);
            string stable = WorkspaceId.FromCanonicalRoot(canonicalRoot);
            string canonicalDb = Path.Combine(canonicalRoot, ".miller", "symbols.db");
            var workspace = WorkspaceContext.Create(root, AppContext.BaseDirectory, home) with
            {
                WorkspaceId = stable,
                CanonicalRoot = canonicalRoot,
                CanonicalExtractDbPath = canonicalDb,
            };

            var row = IndexBootstrapService.RegisterBootstrapWorkspace(
                workspace, stable, WorkspaceRegistryState.LoadedExisting, revision: 42);

            Assert.Equal(stable, row.WorkspaceId);
            Assert.Equal(WorkspaceId.Display(canonicalRoot, stable), row.DisplayId);
            Assert.Equal(canonicalRoot, row.CanonicalRoot);
            Assert.Equal(canonicalDb, row.IndexDbPath);
            Assert.Equal(WorkspaceRegistryState.LoadedExisting, row.State);
            Assert.Equal("loaded_existing", row.StateText);
            Assert.Equal(42, row.LastRevision);
            Assert.Null(row.LastScanAt);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void MarkRegistryScanned_RecordsReadyScanRevision()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-bootstrap-scanned-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(dir, "repo");
        string home = Path.Combine(dir, "home");
        Directory.CreateDirectory(root);
        try
        {
            string canonicalRoot = Path.GetFullPath(root);
            string stable = WorkspaceId.FromCanonicalRoot(canonicalRoot);
            string canonicalDb = Path.Combine(canonicalRoot, ".miller", "symbols.db");
            var workspace = WorkspaceContext.Create(root, AppContext.BaseDirectory, home) with
            {
                WorkspaceId = stable,
                CanonicalRoot = canonicalRoot,
                CanonicalExtractDbPath = canonicalDb,
            };

            var row = IndexBootstrapService.MarkRegistryScanned(workspace, stable, revision: 9);

            Assert.Equal(WorkspaceRegistryState.Ready, row.State);
            Assert.Equal(9, row.LastRevision);
            Assert.NotNull(row.LastScanAt);
            Assert.Equal(canonicalRoot, row.CanonicalRoot);
            Assert.Equal(canonicalDb, row.IndexDbPath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ReadLatestRevisionOrZero_NullWorkspaceId_ReturnsZero()
    {
        // No workspace id known yet (a brand-new DB before the metadata is read) → no revision to seed.
        using var fx = JulieDbFixture.CreateDefault();
        Assert.Equal(0L, IndexBootstrapService.ReadLatestRevisionOrZero(fx.DbPath, workspaceId: null));
    }

    [Fact]
    public void ReadLatestRevisionOrZero_MissingDbFile_DegradesToZero()
    {
        // The DB file does not exist → the workspace has no persisted revision; safe to start fresh at 0.
        string missing = Path.Combine(
            Path.GetTempPath(), "miller-bootstrap-missing-" + Guid.NewGuid().ToString("N"), "symbols.db");
        Assert.Equal(0L, IndexBootstrapService.ReadLatestRevisionOrZero(missing, "ws-1"));
    }

    [Fact]
    public void ReadLatestRevisionOrZero_ReusedDbWithRevisions_ReturnsTheMaxRevisionId()
    {
        // The happy path: a reused DB with persisted revisions seeds the holder from MAX(revision_id) (so the
        // freshness poll does not rebuild on the first tick). v1: one DB = one root, no workspace filter — the
        // workspaceId arg is now only the null-sentinel guard.
        using var fx = JulieDbFixture.Create(
            schemaVersion: JulieDbFixture.PinnedSchema, contractValue: JulieDbFixture.PinnedContract,
            rows: System.Array.Empty<JulieDbFixture.SymbolRow>(),
            revisions: new[]
            {
                new JulieDbFixture.RevisionRow(3),
                new JulieDbFixture.RevisionRow(7),
                new JulieDbFixture.RevisionRow(5),
            });

        Assert.Equal(7L, IndexBootstrapService.ReadLatestRevisionOrZero(fx.DbPath, "ws-anything"));
    }

    [Fact]
    public void ReadLatestRevisionOrZero_CorruptDb_ThrowsLoudly_NotDegradeToZero()
    {
        // A present-but-corrupt DB file is an operator/config error: decision-10 says surface loudly. The
        // narrowed catch (FileNotFoundException only) must let the SqliteException propagate rather than hide it
        // as revision 0 (which would mask corruption and trigger a spurious rebuild).
        string dir = Path.Combine(Path.GetTempPath(), "miller-bootstrap-corrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "symbols.db");
        try
        {
            // Not a valid SQLite file — opening + querying it raises SqliteException, not FileNotFound.
            File.WriteAllText(dbPath, "this is not a sqlite database header at all, just garbage bytes");

            Assert.Throws<SqliteException>(
                () => IndexBootstrapService.ReadLatestRevisionOrZero(dbPath, "ws-1"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ---- finding-4: a prune failure after the ledger is opened must dispose it (no leak) ----

    [Fact]
    public void OpenAndPrune_Success_ReturnsLiveLedger_ThatStillRecords()
    {
        // The happy path: the returned ledger is OPEN (the caller owns it) and prune returns a count.
        string dir = Path.Combine(Path.GetTempPath(), "miller-openprune-ok-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "telemetry.db");
        try
        {
            var ledger = IndexBootstrapService.OpenAndPrune(dbPath, "ws-1", "/repo/work", retentionDays: 30, out int pruned);
            using (ledger)
            {
                Assert.Equal(0, pruned); // empty DB → nothing to prune
                // A live (undisposed) ledger records without dropping (a disposed one would increment Dropped).
                ledger.Record(new TelemetryRecord(
                    Tool: "probe", Op: null, WorkspaceId: "ws-1", WorkspaceRoot: null,
                    DurationMs: 0, Outcome: "ok",
                    ErrorKind: null, ResultCount: null, BytesExamined: 0, BytesReturned: 0, SourceBytes: 0,
                    EstTokens: null, IndexFresh: null, TargetHash: null, MetadataJson: "{}"));
                Assert.Equal(0, ledger.DroppedWrites);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void OpenAndPrune_PruneThrows_DisposesTheLedger_AndRethrows()
    {
        // finding-4 (end-to-end on the real ledger): a negative retentionDays makes Prune throw AFTER the
        // ledger is opened. OpenAndPrune must dispose the just-opened ledger and rethrow rather than leak it.
        string dir = Path.Combine(Path.GetTempPath(), "miller-openprune-throw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "telemetry.db");
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => IndexBootstrapService.OpenAndPrune(dbPath, "ws-1", "/repo/work", retentionDays: -1, out _));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>A disposable spy that records whether Dispose was called — the discriminating observation.</summary>
    private sealed class DisposeSpy : IDisposable
    {
        public int DisposeCount { get; private set; }
        public bool Disposed => DisposeCount > 0;
        public void Dispose() => DisposeCount++;
    }

    [Fact]
    public void PrimeOrDispose_PrimeThrows_DisposesTheResource_AndRethrowsTheSameException()
    {
        // The discriminating unit test for finding-4's disposal contract: when priming throws, the resource is
        // disposed exactly once and the ORIGINAL exception propagates (not a dispose-time error).
        var spy = new DisposeSpy();
        var boom = new InvalidOperationException("prime failed");

        var thrown = Assert.Throws<InvalidOperationException>(
            () => IndexBootstrapService.PrimeOrDispose(spy, _ => throw boom));

        Assert.Same(boom, thrown);
        Assert.True(spy.Disposed);
        Assert.Equal(1, spy.DisposeCount);
    }

    [Fact]
    public void PrimeOrDispose_PrimeSucceeds_ReturnsTheLiveResource_Undisposed()
    {
        // The happy path: the resource is returned to the caller still OPEN (the caller owns disposal); priming
        // ran exactly once.
        var spy = new DisposeSpy();
        int primeCalls = 0;

        var returned = IndexBootstrapService.PrimeOrDispose(spy, _ => primeCalls++);

        Assert.Same(spy, returned);
        Assert.False(spy.Disposed); // NOT disposed on the success path
        Assert.Equal(1, primeCalls);
    }

    [Fact]
    public void ReadLatestRevisionOrZero_NonWritableDbDirectory_ThrowsLoudly_NotDegradeToZero()
    {
        // The WAL writable-dir guard (D4) raises InvalidOperationException. It is a config error → propagate
        // loudly (decision-10), NOT degrade to revision 0. POSIX-only (dir-permission semantics).
        if (OperatingSystem.IsWindows())
            return;

        using var fx = JulieDbFixture.Create(
            schemaVersion: JulieDbFixture.PinnedSchema, contractValue: JulieDbFixture.PinnedContract,
            rows: System.Array.Empty<JulieDbFixture.SymbolRow>(),
            workspaceId: "ws-1",
            revisions: new[] { new JulieDbFixture.RevisionRow(2) });

        string dir = fx.Directory;
        var original = File.GetUnixFileMode(dir);
        try
        {
            File.SetUnixFileMode(dir,
                UnixFileMode.UserRead | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            Assert.Throws<InvalidOperationException>(
                () => IndexBootstrapService.ReadLatestRevisionOrZero(fx.DbPath, "ws-1"));
        }
        finally
        {
            File.SetUnixFileMode(dir, original);
        }
    }

    private static IndexBootstrapService.BootstrapScanDecision Scan(bool force) =>
        new(ShouldScan: true, force ? ScanIntent.RootRebind : ScanIntent.IncrementalReconcile, WorkspaceRegistryState.Ready);

    private static IndexBootstrapService.BootstrapScanDecision Reuse() =>
        new(ShouldScan: false, ScanIntent.IncrementalReconcile, WorkspaceRegistryState.LoadedExisting);

    private sealed class FakeLease : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
