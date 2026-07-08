using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// The end-to-end version-aware leadership handoff proof (design D2–D5, Scale): two real
/// <see cref="IndexerService"/> instances share one temp workspace inside this process — the
/// <see cref="SingleWriterLock"/> <c>FileStream</c> lock contends in-proc exactly as it does cross-proc.
/// Instance A leads with an injected OLDER extractor fitness ("2.0.0", version-equal to a deliberately
/// downgraded artifact stamp); instance B carries the REAL pinned julie-extract. B's reader tick writes a
/// real yield request file, A's drain abdicates (lease released, leader.json gone), A's reclaim is blocked
/// by the post-yield cooldown while B's pid is alive, then B wins the real lock via the production
/// <c>StartAsync</c> path and runs the auto-upgrade forced full rescan with the live binary — after which
/// <c>artifact_metadata.binary_version</c> equals the real binary's version, having never regressed at any
/// observed point. <c>[Trait("Category","Scale")]</c>, excluded by default;
/// <see cref="ScaleTestSupport.RequireJulieServer"/> skips when <c>.tools/julie-extract</c> is absent.
/// </summary>
[Trait("Category", "Scale")]
public sealed class VersionAwareLeadershipScaleTests
{
    /// <summary>The simulated "older extractor" fitness; every shipped pin is strictly newer than this.</summary>
    private const string OldVersion = "2.0.0";

    /// <summary>
    /// Real <see cref="JulieExtractOps"/> wrapped with scan recording: captures each scan's force flag (the
    /// D3 proof is the <c>[delta:false, upgrade:true]</c> sequence) and signals once N scans completed so the
    /// test can wait on the background service deterministically.
    /// </summary>
    private sealed class RecordingRealOps : IExtractOps
    {
        private readonly IExtractOps _inner;
        private readonly object _gate = new();
        private readonly List<bool> _scanForce = new();

        public RecordingRealOps(IExtractOps inner) => _inner = inner;

        public ManualResetEventSlim ScansReached { get; } = new();
        public int SignalAtScanCount { get; init; } = 1;

        public IReadOnlyList<bool> ScanForce
        {
            get
            {
                lock (_gate)
                    return _scanForce.ToArray();
            }
        }

        public ExtractReport Update(string path) => _inner.Update(path);
        public ExtractReport Delete(string path) => _inner.Delete(path);

        public ExtractReport Scan(bool force = false)
        {
            ExtractReport report = _inner.Scan(force); // real julie-extract subprocess
            lock (_gate)
            {
                _scanForce.Add(force);
                if (_scanForce.Count >= SignalAtScanCount)
                    ScansReached.Set();
            }
            return report;
        }
    }

    [Fact]
    public async Task FullYieldHandoff_OldLeaderAbdicates_NewLeaderRunsUpgradeScan_VersionNeverRegresses()
    {
        string binary = ScaleTestSupport.RequireJulieServer();

        string work = Path.Combine(Path.GetTempPath(), "miller-leadership-handoff-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(work, "repo");
        string home = Path.Combine(work, "home");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(home);

        try
        {
            // A small real source tree (two languages) so the forced upgrade rescan has genuine work.
            File.WriteAllText(Path.Combine(repo, "alpha.cs"), """
                namespace Demo;
                public sealed class Quokkanaut { public int One() => 1; }
                """);
            File.WriteAllText(Path.Combine(repo, "beta.ts"),
                "export function vortleTwo(): number { return 2; }\n");

            string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(repo);
            string db = Path.Combine(canonicalRoot, ".miller", "symbols.db");
            Directory.CreateDirectory(Path.GetDirectoryName(db)!);
            var runner = new JulieExtractRunner(binary);

            string? realVersion = runner.QueryVersion();
            Assert.False(string.IsNullOrWhiteSpace(realVersion), "the pinned julie-extract must report --version");
            Assert.True(
                LeadershipEligibility.CompareVersions(realVersion!, OldVersion) > 0,
                $"this test assumes the pinned binary ({realVersion}) outranks the simulated old leader ({OldVersion})");

            // --- setup: a REAL initial artifact, then simulate one written by an older extractor. No old
            // binary fixture exists, so rewrite the version stamp directly — every other byte of the artifact
            // is genuine current-binary output.
            var initial = runner.Scan(canonicalRoot, db, force: true);
            Assert.NotEqual("failed", initial.Status);
            Assert.Equal(realVersion, ExtractBinaryVersionReader.TryRead(db));
            DowngradeArtifactStamp(db, OldVersion);
            Assert.Equal(OldVersion, ExtractBinaryVersionReader.TryRead(db));

            WorkspaceContext workspace = WorkspaceContext.Create(canonicalRoot, ScaleTestSupport.RepoRoot(), home) with
            {
                WorkspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot),
                CanonicalRoot = canonicalRoot,
                CanonicalExtractDbPath = db,
            };
            string millerDir = Path.GetDirectoryName(workspace.ExtractDbPath)!;

            // binary_version observations across the whole sequence (post-downgrade baseline onward): the
            // load-bearing invariant is that it NEVER goes backwards.
            var observed = new List<string?> { ExtractBinaryVersionReader.TryRead(db) };

            // --- instance A: an older-fitness leader. ONLY its own version is injected; the artifact-version
            // read, yield drain, leader-identity read, alive probes, clock, and cooldown all run for real.
            int aAcquireAttempts = 0;
            string tempHomeA = Path.Combine(work, "home-a");
            Directory.CreateDirectory(tempHomeA);
            var aBootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
            aBootstrap.TestHomeDirectoryOverride = tempHomeA;
            var instanceA = new IndexerService(
                aBootstrap,
                NullLogger<IndexerService>.Instance,
                NullLoggerFactory.Instance,
                tryAcquireLeadership: dir =>
                {
                    Interlocked.Increment(ref aAcquireAttempts);
                    return SingleWriterLock.TryAcquire(dir);
                },
                createOps: static (_, _, _) => throw new InvalidOperationException("instance A never scans in this test"),
                leaderRetryInterval: TimeSpan.FromHours(1),
                SymbolSearchSidecar.Disabled,
                attachFileWatchers: false,
                ownExtractorVersion: () => OldVersion);

            // --- instance B: REAL fitness. Its own version comes from the production probe (ToolsRoot points
            // at the repo's .tools, so JulieExtractRunner.Locate + QueryVersion run the live binary); its scans
            // are the real JulieExtractOps behind a recording wrapper.
            var bBootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
            bBootstrap.TestHomeDirectoryOverride = home;
            bBootstrap.SeedForTest(
                workspace,
                new IndexHolder(MillerRepositoryIndex.Build(System.Array.Empty<IndexedSymbol>()), builtRevision: 0));
            var bOps = new RecordingRealOps(JulieExtractOps.Create(canonicalRoot, db, runner)) { SignalAtScanCount = 2 };
            int bAcquireWins = 0;
            var instanceB = new IndexerService(
                bBootstrap,
                NullLogger<IndexerService>.Instance,
                NullLoggerFactory.Instance,
                tryAcquireLeadership: dir =>
                {
                    SingleWriterLock? lease = SingleWriterLock.TryAcquire(dir);
                    if (lease is not null)
                        Interlocked.Increment(ref bAcquireWins);
                    return lease;
                },
                createOps: (_, _, _) => bOps,
                leaderRetryInterval: TimeSpan.FromMilliseconds(100),
                SymbolSearchSidecar.Disabled,
                attachFileWatchers: false);

            using (instanceA)
            using (instanceB)
            {
                // --- 1. A claims leadership: version-equal to the artifact, so eligible; the REAL lock is won.
                Assert.True(instanceA.AttemptClaimForTest(millerDir, db));
                Assert.True(instanceA.IsLeader);
                Assert.Equal(1, aAcquireAttempts);
                // The claim hook stops at the lease; production writes leader.json immediately after winning
                // it (RunLeadershipSessionAsync). Mirror that write so B's reader tick can see WHO leads.
                LeaderIdentityFile.Write(millerDir, new LeaderIdentity(
                    Environment.ProcessId, MillerVersion.Current, Environment.ProcessPath,
                    DateTimeOffset.UtcNow, OldVersion));
                observed.Add(ExtractBinaryVersionReader.TryRead(db));

                // --- 2+3. B's reader retry tick: eligible (real version beats the artifact), sees a LIVE
                // leader bundling a strictly older extractor, and writes a REAL yield request file.
                instanceB.MaybeRequestYieldForTest(millerDir, workspace.WorkspaceId!, db);
                Assert.NotNull(instanceB.EligibilityVerdict);
                Assert.True(instanceB.EligibilityVerdict!.Eligible);

                // --- 4. A's debounce tick drains the request and abdicates: lease released, identity gone.
                Assert.True(instanceA.ProcessYieldRequestsForTest(millerDir));
                Assert.False(instanceA.IsLeader);
                Assert.Null(LeaderIdentityFile.TryRead(millerDir));
                using (SingleWriterLock? probe = SingleWriterLock.TryAcquire(millerDir))
                {
                    Assert.NotNull(probe); // the OS-level lock really was released, not just the flag
                }
                observed.Add(ExtractBinaryVersionReader.TryRead(db));

                // --- 5. A respects the post-yield cooldown: B's pid (this process) is alive and the 60s
                // window is open, so A's claim is suppressed BEFORE the acquire func — the lock is never touched.
                Assert.False(instanceA.AttemptClaimForTest(millerDir, db));
                Assert.Equal(1, aAcquireAttempts);
                observed.Add(ExtractBinaryVersionReader.TryRead(db));

                // --- 6. B claims through the production path: StartAsync's claim loop wins the real lock,
                // records its identity, runs the startup delta scan, and — because the artifact predates its
                // extractor — exactly ONE forced upgrade rescan with the real binary.
                await instanceB.StartAsync(CancellationToken.None);
                try
                {
                    Assert.True(
                        bOps.ScansReached.Wait(120_000, CancellationToken.None),
                        "instance B should run the startup delta scan plus the forced upgrade rescan");
                    Assert.True(instanceB.IsLeader);
                    Assert.Equal(1, bAcquireWins);
                    Assert.Equal(new[] { false, true }, bOps.ScanForce);

                    LeaderIdentity? identity = LeaderIdentityFile.TryRead(millerDir);
                    Assert.NotNull(identity);
                    Assert.Equal(Environment.ProcessId, identity!.Pid);
                    Assert.Equal(realVersion, identity.ExtractorVersion);
                }
                finally
                {
                    await instanceB.StopAsync(CancellationToken.None);
                }
                observed.Add(ExtractBinaryVersionReader.TryRead(db));
            }

            // --- 7. The artifact now carries the real binary's version, and at no observed point did
            // binary_version move backwards.
            Assert.Equal(realVersion, observed[^1]);
            for (int i = 1; i < observed.Count; i++)
            {
                Assert.NotNull(observed[i]);
                Assert.True(
                    LeadershipEligibility.CompareVersions(observed[i]!, observed[i - 1]!) >= 0,
                    $"artifact binary_version regressed at step {i}: {observed[i - 1]} -> {observed[i]}");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Simulate an artifact produced by an OLDER julie-extract by rewriting the version stamp in place. The
    /// repo pins only the current binary (no historical fixture), so this is the sanctioned downgrade shim:
    /// the rest of the artifact remains genuine current-binary output.
    /// </summary>
    private static void DowngradeArtifactStamp(string dbPath, string version)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE artifact_metadata SET value = $version WHERE key = 'binary_version';";
        command.Parameters.AddWithValue("$version", version);
        Assert.Equal(1, command.ExecuteNonQuery());
    }
}
