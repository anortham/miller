using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// The WAL durability proof (m3-design verified-fact 8, decision-2; Scale): ONE writer (the live julie-server
/// doing repeated <c>extract update</c>) while N long-lived <c>Mode=ReadOnly</c> <see cref="FreshnessReader"/>s
/// poll the revision cursor concurrently. Asserts: no corruption / no exception under contention, and every
/// reader OBSERVES the writer's revision bumps WITHOUT reopening its connection (the no-lingering-transaction
/// contract — a long-lived read connection sees later commits on its next command). Same host + local FS, which
/// is exactly Miller's topology (N reader processes + 1 writer subprocess sharing the <c>-shm</c> wal-index).
/// <c>[Trait("Category","Scale")]</c>, EXCLUDED by default; skips if the binary is absent.
/// </summary>
[Trait("Category", "Scale")]
public sealed class MultiProcessWalTests
{
    [Fact]
    public async Task Live_OneWriter_ManyReaders_NoCorruption_ReadersSeeTheBumps()
    {
        var ct = TestContext.Current.CancellationToken;
        string binary = ScaleTestSupport.RequireJulieServer();

        string work = Path.Combine(Path.GetTempPath(), "miller-wal-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(work, "repo");
        string db = Path.Combine(work, ".miller", "symbols.db");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);

        const int readerCount = 6;
        const int writeIterations = 8;

        try
        {
            string file = Path.Combine(repo, "counter.cs");
            File.WriteAllText(file, "namespace Demo; public sealed class Counter { public int V0() => 0; }");

            string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(repo);
            var runner = new JulieExtractRunner(binary!);
            var scan = runner.Scan(canonicalRoot, db, force: true);
            Assert.NotEqual("failed", scan.Status);
            // v1 stores no workspace_id; the stable id is derived from the canonical root (reconciliation #17).
            string workspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot);

            long startRevision;
            using (var seed = new FreshnessReader(db))
                startRevision = seed.LatestRevision(workspaceId);

            using var cts = new CancellationTokenSource();
            var maxObserved = new long[readerCount];
            var readerErrors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            // N long-lived ReadOnly readers, each holding ONE connection and polling repeatedly. They must never
            // reopen and must observe the writer's bumps as they happen.
            var readers = new Task[readerCount];
            for (int i = 0; i < readerCount; i++)
            {
                int idx = i;
                readers[i] = Task.Run(async () =>
                {
                    try
                    {
                        using var fr = new FreshnessReader(db); // ONE connection for the whole loop (no reopen)
                        while (!cts.IsCancellationRequested)
                        {
                            long rev = fr.LatestRevision(workspaceId);
                            if (rev > maxObserved[idx])
                                maxObserved[idx] = rev;
                            // Also exercise the changed-file delta read under contention (must not corrupt).
                            _ = fr.ChangedSince(startRevision, workspaceId);
                            try { await Task.Delay(5, cts.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) { break; }
                        }
                        // A final read after cancellation to capture the last committed revision.
                        long finalRev = fr.LatestRevision(workspaceId);
                        if (finalRev > maxObserved[idx])
                            maxObserved[idx] = finalRev;
                    }
                    catch (Exception ex)
                    {
                        readerErrors.Add(ex);
                    }
                }, ct);
            }

            // The single writer: a sequence of real `extract update` calls, each a genuine content change so the
            // revision bumps every iteration (verified-fact 2).
            long lastWritten = startRevision;
            for (int it = 1; it <= writeIterations; it++)
            {
                File.WriteAllText(file,
                    $"namespace Demo; public sealed class Counter {{ public int V{it}() => {it}; }}");
                var report = runner.Update(canonicalRoot, db, Path.Combine(canonicalRoot, "counter.cs"));
                Assert.NotEqual("failed", report.Status);
                if (report.Revision is { } r)
                    lastWritten = Math.Max(lastWritten, r);
            }

            // Let the readers catch up to the final commit, then stop them.
            await Task.Delay(150, ct);
            await cts.CancelAsync();
            await Task.WhenAll(readers);

            Assert.Empty(readerErrors); // no corruption / no exception under 1-writer/N-reader WAL contention

            long finalRevision;
            using (var verify = new FreshnessReader(db))
                finalRevision = verify.LatestRevision(workspaceId);
            Assert.True(finalRevision > startRevision, "the writer's updates must have advanced the revision");

            // Every reader observed the final committed revision via its long-lived connection (no reopen).
            for (int i = 0; i < readerCount; i++)
                Assert.Equal(finalRevision, maxObserved[i]);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }
}
