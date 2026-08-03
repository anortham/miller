using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Scale proof of the progressive-levels contract against the real julie-extract binary: a symbols-level first
/// build produces a servable core with EMPTY reference/facts tables and a <c>symbols</c> level stamp, deltas
/// inherit the level, and the full-level upgrade rebuild (Miller's rebuild-and-promote path) converges the
/// gated tables and restamps <c>full</c>. Spawns julie-extract, so it is excluded from the fast suite. Skips
/// (not fails) on a pinned binary that predates <c>--level</c> (&lt; 2.25.0).
/// </summary>
[Trait("Category", "Scale")]
public sealed class IndexLevelsScaleTests : IDisposable
{
    private readonly string _work;

    public IndexLevelsScaleTests()
    {
        _work = Path.Combine(Path.GetTempPath(), "miller-levels-scale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_work, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void SymbolsFirstBuild_ServesCoreWithEmptyReferenceLayer_ThenUpgradePromotesToFull()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        SkipUnlessBinarySupportsLevels(binary);

        string repo = Path.Combine(_work, "repo");
        string db = Path.Combine(_work, "repo", ".miller", "symbols.db");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "Levels.cs"), """
            namespace LevelsProbe;

            // A comment region so the full level has source_regions to emit.
            public sealed class Widget
            {
                public string Label { get; } = "literal-needle";
            }

            public static class WidgetUser
            {
                public static int Use()
                {
                    var widget = new Widget();
                    return widget.Label.Length;
                }
            }
            """);

        var runner = new JulieExtractRunner(binary);

        // 1. The bootstrap-fresh shape under progressive policy: a NON-force scan of an absent DB carrying
        //    --level symbols. julie creates + root-binds the artifact at symbols level.
        ExtractReport first = runner.Scan(repo, db, force: false, jobs: 1, level: ExtractIndexLevel.Symbols);
        Assert.NotEqual("failed", first.Status);
        Assert.Equal("symbols", ExtractIndexLevelReader.Read(db));
        Assert.True(Count(db, "SELECT COUNT(*) FROM symbols;") > 0, "symbols core must be populated");
        Assert.Equal(0, Count(db, "SELECT COUNT(*) FROM identifiers;"));
        Assert.Equal(0, Count(db, "SELECT COUNT(*) FROM source_regions;"));
        Assert.Equal(0, Count(db, "SELECT COUNT(*) FROM structural_facts;"));
        Assert.Equal("symbols", RepositoryIndexLoader.Load(db).IndexLevel);

        // 2. A routine delta (no --level) inherits the recorded level rather than upgrading it.
        File.WriteAllText(Path.Combine(repo, "Second.cs"), "namespace LevelsProbe; public sealed class Second { }");
        ExtractReport delta = runner.Scan(repo, db, force: false, jobs: 1);
        Assert.NotEqual("failed", delta.Status);
        Assert.Equal("symbols", ExtractIndexLevelReader.Read(db));
        Assert.Equal(0, Count(db, "SELECT COUNT(*) FROM identifiers;"));

        // 3. The LevelUpgrade shape: a full-level force. JulieExtractRunner extracts into symbols.db.rebuild
        //    and promotes, so the level change never touches the served artifact in place.
        ExtractReport upgrade = runner.Scan(repo, db, force: true, jobs: 1, level: ExtractIndexLevel.Full);
        Assert.NotEqual("failed", upgrade.Status);
        SqliteConnection.ClearAllPools(); // the promote replaced the file; drop pooled handles to the old inode
        Assert.Equal("full", ExtractIndexLevelReader.Read(db));
        Assert.True(
            Count(db, "SELECT COUNT(*) FROM identifiers;") > 0,
            "the full-level upgrade must converge the identifier layer");
        Assert.True(
            Count(db, "SELECT COUNT(*) FROM source_regions;") > 0,
            "the full-level upgrade must converge source regions");
        MillerRepositoryIndex upgraded = RepositoryIndexLoader.Load(db);
        Assert.Equal("full", upgraded.IndexLevel);
        Assert.False(IndexLevels.UpgradeOwed(upgraded.IndexLevel, IndexLevelPolicy.Progressive));
    }

    // A pinned binary older than 2.25.0 rejects --level with a clap usage error. Probe `scan --help` once and
    // skip — this test proves the levels contract, not the pin's age.
    private static void SkipUnlessBinarySupportsLevels(string binary)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = binary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        process.StartInfo.ArgumentList.Add("scan");
        process.StartInfo.ArgumentList.Add("--help");
        process.Start();
        string help = process.StandardOutput.ReadToEnd();
        process.WaitForExit(10_000);
        Assert.SkipWhen(
            !help.Contains("--level", StringComparison.Ordinal),
            "the restored julie-extract predates `scan --level` (< 2.25.0); restore the levels build to run this test.");
    }

    private static long Count(string db, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = db,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
