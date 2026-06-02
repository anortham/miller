using Miller.Indexing;
using Miller.Server;
using Miller.Server.Hosting;
using Miller.Server.Resolution;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// The M6 end-to-end Scale proof (m6-design impl-order step 10): restore julie-server → scan a temp repo →
/// drive the REAL <see cref="EditService"/> with <c>apply=true</c> for a symbol body-replace + an add-doc + a
/// cross-file rename → the files on disk are correct → write-through reindexes (the canonical revision bumps) →
/// a re-read of the extract reflects the rename → an externally-modified file trips the freshness gate (refused
/// without <c>allow_stale</c>). Depends on the binary + a real extract, so it is
/// <c>[Trait("Category","Scale")]</c> and EXCLUDED from the default suite; it <see cref="Assert.Skip"/>s if
/// <c>.tools/julie-server</c> is absent rather than failing.
/// </summary>
[Trait("Category", "Scale")]
public sealed class LiveEditTests
{
    // A write-through that canonicalizes + reindexes each changed file through the real binary (what the leader
    // does inline) and records the converged paths so the test can assert it ran.
    private sealed class LiveWriteThrough(JulieExtractOps ops) : IEditWriteThrough
    {
        public List<string> Converged { get; } = [];
        public void Converge(IReadOnlyList<string> changedFiles)
        {
            foreach (var f in changedFiles)
            {
                ops.Update(f);
                Converged.Add(f);
            }
        }
    }

    private sealed class NoopLease : IDisposable { public void Dispose() { } }

    [Fact]
    public void Live_ScanEditApplyAndConverge_AcrossFiles_WithStaleGate()
    {
        string binary = ScaleTestSupport.RequireJulieServer();

        string work = Path.Combine(Path.GetTempPath(), "miller-m6live-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(work, "repo");
        string millerDir = Path.Combine(repo, ".miller");
        string db = Path.Combine(millerDir, "symbols.db");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(millerDir);

        try
        {
            // Two files: OrderService.Total is the rename target; CallSite.cs references it across a file boundary.
            File.WriteAllText(Path.Combine(repo, "OrderService.cs"), """
                namespace Shop;

                public sealed class OrderService
                {
                    public int Total()
                    {
                        return 1 + 2;
                    }
                }
                """);
            File.WriteAllText(Path.Combine(repo, "CallSite.cs"), """
                namespace Shop;

                public sealed class CallSite
                {
                    public int Use(OrderService o)
                    {
                        return o.Total();
                    }
                }
                """);

            // --- scan with the real binary into the Miller-owned .miller/symbols.db ---
            string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(repo);
            string canonicalDb = Path.Combine(canonicalRoot, ".miller", "symbols.db");
            var runner = new JulieExtractRunner(binary!);
            var report = runner.Scan(canonicalRoot, canonicalDb, force: true);
            Assert.NotEqual("failed", report.Status);
            Assert.True(report.SymbolsExtracted > 0);

            // v1 stores no workspace_id; the stable id is derived from the canonical root (reconciliation #17).
            string workspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot);

            long revAfterScan;
            using (var fr = new FreshnessReader(canonicalDb))
                revAfterScan = fr.LatestRevision(workspaceId);

            // --- build the edit service against the real extract + a real reindexing write-through ---
            // NOTE: julie reassigns a symbol's opaque id on every `extract update` (verified: an id is NOT stable
            // across re-extraction). So after each apply's write-through reindexes, the in-memory index is stale
            // (it maps the symbol to its OLD id, whose span no longer exists). In production the FreshnessService
            // polls the revision and SWAPS holder.Current; each `edit` MCP call reads holder.Current fresh. This
            // test models that swap by rebuilding the service from the current DB before each symbol-id-dependent
            // edit — the honest equivalent of the holder re-point.
            var ops = JulieExtractOps.Create(canonicalRoot, canonicalDb, runner);
            var applier = new EditApplier(() => new NoopLease());

            EditService FreshService(out LiveWriteThrough wt)
            {
                var idx = MillerRepositoryIndex.Build(SqliteSymbolReader.Read(canonicalDb));
                wt = new LiveWriteThrough(ops);
                return new EditService(idx, new SmartTargetResolver(idx), canonicalDb, canonicalRoot, applier, wt);
            }

            // === 1. body-replace + add-doc on OrderService.Total (apply) ===
            var bodyResult = FreshService(out _).Execute(new EditRequest("replace_symbol_body", "OrderService.Total")
            {
                NewText = "{\n        return 42;\n    }",
                Apply = true,
            });
            Assert.True(bodyResult.Applied, bodyResult.Output);

            // Re-point the index (the write-through reindexed; the symbol's id changed) before the next edit.
            var docResult = FreshService(out _).Execute(new EditRequest("add_doc", "OrderService.Total")
            {
                NewText = "    // computes the total",
                Apply = true,
            });
            Assert.True(docResult.Applied, docResult.Output);

            string ordersDisk = File.ReadAllText(Path.Combine(repo, "OrderService.cs"));
            Assert.Contains("return 42;", ordersDisk);
            Assert.DoesNotContain("return 1 + 2;", ordersDisk);
            Assert.Contains("// computes the total", ordersDisk);

            // Write-through reindexed → the canonical revision advanced past the scan.
            long revAfterEdits;
            using (var fr = new FreshnessReader(canonicalDb))
                revAfterEdits = fr.LatestRevision(workspaceId);
            Assert.True(revAfterEdits > revAfterScan,
                $"revision should bump after write-through (scan={revAfterScan}, edits={revAfterEdits}).");

            // === 2. cross-file rename Total → GrandTotal (apply) ===
            // Rebuild the index/resolver/service from the freshly reindexed DB (the edits moved byte offsets).
            var index2 = MillerRepositoryIndex.Build(SqliteSymbolReader.Read(canonicalDb));
            var resolver2 = new SmartTargetResolver(index2);
            var writeThrough2 = new LiveWriteThrough(ops);
            var service2 = new EditService(index2, resolver2, canonicalDb, canonicalRoot, applier, writeThrough2);

            var renameResult = service2.Execute(new EditRequest("rename_symbol", "OrderService.Total")
            {
                NewText = "GrandTotal",
                Apply = true,
            });
            Assert.True(renameResult.Applied, renameResult.Output);

            // The def in OrderService.cs AND the cross-file call in CallSite.cs are both rewritten.
            Assert.Contains("GrandTotal", File.ReadAllText(Path.Combine(repo, "OrderService.cs")));
            string callSiteDisk = File.ReadAllText(Path.Combine(repo, "CallSite.cs"));
            Assert.Contains("o.GrandTotal()", callSiteDisk);
            Assert.DoesNotContain("o.Total()", callSiteDisk);

            // Write-through converged BOTH changed files.
            Assert.Contains(writeThrough2.Converged, p => p.EndsWith("OrderService.cs", StringComparison.Ordinal));
            Assert.Contains(writeThrough2.Converged, p => p.EndsWith("CallSite.cs", StringComparison.Ordinal));

            // === 3. the renamed symbol is visible in the re-read extract (inspect/search reflect it) ===
            var index3 = MillerRepositoryIndex.Build(SqliteSymbolReader.Read(canonicalDb));
            Assert.NotEmpty(index3.FindByName("GrandTotal"));
            Assert.Empty(index3.FindByName("Total")); // the old name is gone from the symbols table

            // === 4. an externally-modified file trips the freshness gate (refused without allow_stale) ===
            File.AppendAllText(Path.Combine(repo, "OrderService.cs"), "\n// external edit\n");
            var resolver3 = new SmartTargetResolver(index3);
            var service3 = new EditService(index3, resolver3, canonicalDb, canonicalRoot, applier,
                new LiveWriteThrough(ops));

            var blocked = service3.Execute(new EditRequest("replace_symbol_body", "OrderService.GrandTotal")
            {
                NewText = "{ return 0; }",
                Apply = true, // no allow_stale
            });
            Assert.False(blocked.Applied);
            Assert.Contains("stale", blocked.Output, StringComparison.OrdinalIgnoreCase);
            // The external edit survives (no write happened).
            Assert.Contains("// external edit", File.ReadAllText(Path.Combine(repo, "OrderService.cs")));

            // allow_stale lets a deliberate edit through despite the drift.
            var forced = service3.Execute(new EditRequest("replace_symbol_body", "OrderService.GrandTotal")
            {
                NewText = "{\n        return 7;\n    }",
                Apply = true,
                AllowStale = true,
            });
            Assert.True(forced.Applied, forced.Output);
            Assert.True(forced.StaleAllowed);
            Assert.Contains("return 7;", File.ReadAllText(Path.Combine(repo, "OrderService.cs")));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }
}
