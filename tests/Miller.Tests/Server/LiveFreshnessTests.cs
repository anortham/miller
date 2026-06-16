using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Server.Hosting;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// The M3 end-to-end freshness proof (m3-design §Test strategy, Scale): with the real pinned julie-extract,
/// scan a tiny throwaway repo → build + hold the index → MODIFY a file → drain/route through the production
/// dispatch (<see cref="IndexerCore"/> + <see cref="JulieExtractOps"/>, real subprocess) → the revision bumps →
/// the freshness poll (<see cref="FreshnessReader"/> + <see cref="FreshnessPoller"/>, real SQLite) rebuilds +
/// swaps → <c>search</c> through the holder sees the NEW symbol; DELETE a file → its symbol disappears; touch
/// <c>.git/HEAD</c> → a forced <c>scan</c> reconcile runs and a manually-added file is picked up. Drives the
/// PRODUCTION cores against the live binary (not the timer-driven hosted shell, which is non-deterministic), so
/// every M3 link — extract update/delete/scan, revision cursor, rebuild, atomic swap, holder-backed search — is
/// exercised against real julie output. <c>[Trait("Category","Scale")]</c>, EXCLUDED by default;
/// <see cref="Assert.Skip"/>s if <c>.tools/julie-extract</c> is absent.
/// </summary>
[Trait("Category", "Scale")]
public sealed class LiveFreshnessTests
{
    [Fact]
    public void Live_ModifyUpdateRebuildSwap_ThenDelete_ThenHeadScan_Converges()
    {
        string binary = ScaleTestSupport.RequireJulieServer();

        string work = Path.Combine(Path.GetTempPath(), "miller-m3live-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(work, "repo");
        string millerDir = Path.Combine(work, ".miller");
        string db = Path.Combine(millerDir, "symbols.db");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(millerDir);
        // A fake .git/HEAD so the HEAD-change reconcile path is exercisable.
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main\n");

        string alphaFile = Path.Combine(repo, "alpha.cs");
        string betaFile = Path.Combine(repo, "beta.cs");

        try
        {
            // Single-token, mutually NON-overlapping class names so an OR search for one never matches another
            // via a shared component token (e.g. "...Service"). Each unique token isolates exactly one symbol.
            File.WriteAllText(alphaFile, """
                namespace Demo;
                public sealed class Quokkanaut { public int One() => 1; }
                """);
            File.WriteAllText(betaFile, """
                namespace Demo;
                public sealed class Vortle { public int Two() => 2; }
                """);

            string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(repo);
            var runner = new JulieExtractRunner(binary!);

            // --- initial scan: seeds the first revision (v1 stores no workspace_id) ---
            var scan = runner.Scan(canonicalRoot, db, force: true);
            Assert.NotEqual("failed", scan.Status);
            // v1 has no workspace_id metadata; the stable id is derived from the canonical root (reconciliation #17).
            string workspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot);

            using var reader = new FreshnessReader(db);
            long initialRevision = reader.LatestRevision();
            Assert.True(initialRevision > 0, "scan should establish a revision cursor");

            var holder = new IndexHolder(
                MillerRepositoryIndex.Build(SqliteSymbolReader.Read(db)), initialRevision);
            var rebuilder = new IndexRebuilder(db);
            var holderProvider = new HolderWorkspaceIndexProvider(holder, db, workspaceId, canonicalRoot);
            var searchTool = new SearchTool(holderProvider, holderProvider);
            var ops = JulieExtractOps.Create(canonicalRoot, db, runner);
            var core = new IndexerCore(new WatchEventQueue(), ops, File.Exists);

            // baseline: the to-be-added symbol is absent.
            Assert.StartsWith("No results.", searchTool.Search("Zigglethorpe").Trim());

            // --- MODIFY alpha.cs to add a new symbol, route the watcher event through the live update ---
            File.WriteAllText(alphaFile, """
                namespace Demo;
                public sealed class Quokkanaut { public int One() => 1; }
                public sealed class Zigglethorpe { public int Three() => 3; }
                """);
            core.Enqueue(new WatchEvent(alphaFile, WatchEventKind.Modified));
            core.DrainAndProcess(headChanged: false); // real `extract update` subprocess

            long afterModify = reader.LatestRevision();
            Assert.True(afterModify > initialRevision, "a real content change must bump the revision");

            // the freshness poll rebuilds + swaps; search through the holder now sees the new symbol.
            Assert.True(FreshnessPoller.PollOnce(holder, afterModify, rebuilder.Rebuild));
            Assert.Equal(afterModify, holder.BuiltRevision);
            Assert.Contains("Zigglethorpe", searchTool.Search("Zigglethorpe"));

            // a second poll at the same revision is a no-op (no churn while the writer is idle).
            Assert.False(FreshnessPoller.PollOnce(holder, reader.LatestRevision(), rebuilder.Rebuild));

            // --- DELETE beta.cs: route the delete event, then converge; its symbol disappears ---
            Assert.Contains("Vortle", searchTool.Search("Vortle")); // present before delete
            File.Delete(betaFile);
            core.Enqueue(new WatchEvent(betaFile, WatchEventKind.Deleted));
            core.DrainAndProcess(headChanged: false); // real `extract delete` subprocess

            long afterDelete = reader.LatestRevision();
            Assert.True(afterDelete > afterModify, "a delete must bump the revision");
            Assert.True(FreshnessPoller.PollOnce(holder, afterDelete, rebuilder.Rebuild));
            Assert.StartsWith("No results.", searchTool.Search("Vortle").Trim());

            // --- HEAD change: add a file out-of-band (no per-file event), then force a scan reconcile ---
            File.WriteAllText(Path.Combine(repo, "delta.cs"), """
                namespace Demo;
                public sealed class Plumbus { public int Four() => 4; }
                """);
            // Simulate a branch switch: HEAD moved, so the indexer forces a single scan (drops per-file events).
            File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/feature\n");
            core.DrainAndProcess(headChanged: true); // real `extract scan` reconcile

            long afterScan = reader.LatestRevision();
            Assert.True(afterScan >= afterDelete, "the scan reconcile should not regress the revision");
            Assert.True(FreshnessPoller.PollOnce(holder, afterScan, rebuilder.Rebuild) || afterScan == afterDelete);
            // Force a rebuild even if the revision happened not to bump (e.g. scan no-op), so we read latest state.
            holder.Swap(rebuilder.Rebuild(), afterScan);
            Assert.Contains("Plumbus", searchTool.Search("Plumbus"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }
}
