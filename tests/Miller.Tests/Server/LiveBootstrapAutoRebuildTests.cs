using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// The live regression guard for the auto-heal path (the bug that surfaced as "miller failed to connect"): a
/// reused DB whose <c>root_path</c> matched — so <see cref="IndexBootstrapService.DecideBootstrapScan"/> chose
/// reuse — but whose schema/contract is INCOMPATIBLE (e.g. a julie-extract schema bump raised the expected
/// version) used to throw out of <c>StartAsync</c> and crash the whole host. <see cref="IndexBootstrapService.LoadIndexWithAutoRebuild{T}"/>
/// now force-rebuilds once with the bundled julie-extract and reloads. This drives the REAL loader + REAL runner
/// exactly as <c>Run()</c> wires them, so it spawns the subprocess and is <c>[Trait("Category","Scale")]</c>
/// (skips, never fails, when <c>.tools/julie-extract</c> is absent).
/// </summary>
[Trait("Category", "Scale")]
public sealed class LiveBootstrapAutoRebuildTests
{
    [Fact]
    public void LoadIndexWithAutoRebuild_IncompatibleReusedDb_RebuildsWithRealExtractAndRecovers()
    {
        string binary = ScaleTestSupport.RequireJulieServer();

        string work = Path.Combine(Path.GetTempPath(), "miller-live-autoheal-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(work, "repo");
        string db = Path.Combine(work, ".miller", "symbols.db");
        Directory.CreateDirectory(repo);
        try
        {
            File.WriteAllText(Path.Combine(repo, "widget.cs"), """
                namespace Demo;

                public sealed class WidgetFactory
                {
                    public Widget CreateMillerWidget(int size) => new Widget(size);
                }

                public sealed record Widget(int Size);
                """);

            var runner = new JulieExtractRunner(binary!);

            // 1) Produce a REAL, valid schema-2 artifact, then downgrade only its recorded schema version. The
            //    result is an otherwise-real DB with a matching root_path but an incompatible schema — precisely
            //    the artifact that bricked startup after the 2.1.0 bump.
            runner.Scan(repo, db, force: true);
            using (var conn = new SqliteConnection($"Data Source={db}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE artifact_metadata SET value='1' WHERE key='sqlite_schema_version';";
                Assert.Equal(1, cmd.ExecuteNonQuery()); // the downgrade actually took (guards a false green)
            }
            SqliteConnection.ClearAllPools(); // release the write handle before the loader/julie reopen the file

            // 2) Sanity: the schema gate now rejects the reused DB — this IS the production crash.
            Assert.Throws<IncompatibleExtractException>(() => RepositoryIndexLoader.Load(db));
            SqliteConnection.ClearAllPools();

            // 3) The auto-heal seam, wired with the same real loader + force-rescan + pool barrier that Run() uses.
            //    onBeforeRetry MUST drop pooled read connections: julie's force rebuild replaces the DB file, so the
            //    pooled connection from the failed first load still points at the old inode and would re-read schema 1.
            var result = IndexBootstrapService.LoadIndexWithAutoRebuild(
                load: () => RepositoryIndexLoader.Load(db),
                forceRescan: () => runner.Scan(repo, db, force: true).Revision,
                onBeforeRetry: SqliteConnection.ClearAllPools,
                onIncompatible: _ => { },
                onCorrupt: _ => { });

            // Healed: a rebuild happened, the rebuilt index is queryable, and the on-disk DB is a compatible
            // schema-2 artifact again (the force rescan overwrote the downgraded metadata).
            Assert.True(result.Rebuilt);
            var hits = result.Index.Search("CreateMillerWidget", limit: 10);
            Assert.Contains(hits, h => result.Index.Resolve(h.Document.DocId).Name == "CreateMillerWidget");
            Assert.Equal(
                MillerExtractContract.ExpectedSqliteSchemaVersion.ToString(),
                ReadMetadataValue(db, "sqlite_schema_version"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, recursive: true); } catch (IOException) { /* best-effort temp cleanup */ }
        }
    }

    private static string? ReadMetadataValue(string db, string key)
    {
        using var conn = new SqliteConnection($"Data Source={db};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM artifact_metadata WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar()?.ToString();
    }
}
