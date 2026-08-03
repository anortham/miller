using Microsoft.Data.Sqlite;
using Miller.Core.Freshness;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins <see cref="IndexLevels"/>: the policy parse (fail-closed), the env &gt; registry &gt; default
/// resolution, the one shared level-for-scan decision, and the derived upgrade-owed rule. All through the pure
/// overloads — no test mutates the process environment (xUnit runs collections in parallel).
/// </summary>
public sealed class IndexLevelsTests
{
    [Theory]
    [InlineData(null, IndexLevelPolicy.Progressive)]
    [InlineData("", IndexLevelPolicy.Progressive)]
    [InlineData("progressive", IndexLevelPolicy.Progressive)]
    [InlineData("on", IndexLevelPolicy.Progressive)]
    [InlineData("1", IndexLevelPolicy.Progressive)]
    [InlineData("full", IndexLevelPolicy.Full)]
    [InlineData("off", IndexLevelPolicy.Full)]
    [InlineData("0", IndexLevelPolicy.Full)]
    [InlineData("symbols-only", IndexLevelPolicy.SymbolsOnly)]
    [InlineData("symbols", IndexLevelPolicy.SymbolsOnly)]
    [InlineData("  Progressive  ", IndexLevelPolicy.Progressive)]
    public void FromEnvValues_ParsesTheDocumentedTokens(string? raw, IndexLevelPolicy expected) =>
        Assert.Equal(expected, IndexLevels.FromEnvValues(raw));

    [Fact]
    public void FromEnvValues_AnUnknownTokenFailsClosedToFull() =>
        Assert.Equal(IndexLevelPolicy.Full, IndexLevels.FromEnvValues("aggressive"));

    [Fact]
    public void FromEnvValues_TheInPlaceRebuildHatchForcesFull_WhateverThePolicySays() =>
        Assert.Equal(IndexLevelPolicy.Full, IndexLevels.FromEnvValues("progressive", inPlaceRebuild: "1"));

    [Fact]
    public void Resolve_TheEnvironmentBeatsTheRegistryPolicy() =>
        Assert.Equal(
            IndexLevelPolicy.Full,
            IndexLevels.Resolve(envRaw: "full", inPlaceRebuild: null, registryPolicy: "symbols-only"));

    [Fact]
    public void Resolve_TheRegistryPolicyBeatsTheDefault() =>
        Assert.Equal(
            IndexLevelPolicy.SymbolsOnly,
            IndexLevels.Resolve(envRaw: null, inPlaceRebuild: null, registryPolicy: "symbols-only"));

    [Fact]
    public void Resolve_NothingSetIsProgressive() =>
        Assert.Equal(
            IndexLevelPolicy.Progressive,
            IndexLevels.Resolve(envRaw: null, inPlaceRebuild: null, registryPolicy: null));

    [Fact]
    public void Resolve_TheInPlaceHatchBeatsEverything() =>
        Assert.Equal(
            IndexLevelPolicy.Full,
            IndexLevels.Resolve(envRaw: "progressive", inPlaceRebuild: "1", registryPolicy: "progressive"));

    [Theory]
    [InlineData(IndexLevelPolicy.Progressive, "progressive")]
    [InlineData(IndexLevelPolicy.Full, "full")]
    [InlineData(IndexLevelPolicy.SymbolsOnly, "symbols-only")]
    public void StorageValue_RoundTripsThroughTheParser(IndexLevelPolicy policy, string expected)
    {
        Assert.Equal(expected, IndexLevels.StorageValue(policy));
        Assert.Equal(policy, IndexLevels.FromEnvValues(IndexLevels.StorageValue(policy)));
    }

    [Theory]
    [InlineData(ScanIntent.IncrementalReconcile, true)]
    [InlineData(ScanIntent.IncrementalReconcile, false)]
    [InlineData(ScanIntent.UserFullRebuild, false)]
    [InlineData(ScanIntent.RootRebind, false)]
    [InlineData(ScanIntent.SchemaHeal, false)]
    [InlineData(ScanIntent.CorruptionHeal, false)]
    [InlineData(ScanIntent.ExtractorUpgrade, false)]
    [InlineData(ScanIntent.LevelUpgrade, false)]
    public void LevelForScan_UnderFullPolicy_NeverEmitsTheFlag(ScanIntent intent, bool newArtifact) =>
        Assert.Equal(
            ExtractIndexLevel.Full,
            IndexLevels.LevelForScan(intent, newArtifact, IndexLevelPolicy.Full));

    [Fact]
    public void LevelForScan_UnderProgressive_AFreshFirstBuildIsSymbols() =>
        Assert.Equal(
            ExtractIndexLevel.Symbols,
            IndexLevels.LevelForScan(
                ScanIntent.IncrementalReconcile, newArtifact: true, IndexLevelPolicy.Progressive));

    [Fact]
    public void LevelForScan_UnderProgressive_ARoutineDeltaInheritsByEmittingNothing() =>
        Assert.Equal(
            ExtractIndexLevel.Full,
            IndexLevels.LevelForScan(
                ScanIntent.IncrementalReconcile, newArtifact: false, IndexLevelPolicy.Progressive));

    [Theory]
    [InlineData(ScanIntent.RootRebind)]
    [InlineData(ScanIntent.SchemaHeal)]
    [InlineData(ScanIntent.CorruptionHeal)]
    public void LevelForScan_UnderProgressive_ARepairRebuildsAtSymbols_RestoreServingFast(ScanIntent repair) =>
        Assert.Equal(
            ExtractIndexLevel.Symbols,
            IndexLevels.LevelForScan(repair, newArtifact: false, IndexLevelPolicy.Progressive));

    [Theory]
    [InlineData(ScanIntent.UserFullRebuild)]
    [InlineData(ScanIntent.ExtractorUpgrade)]
    [InlineData(ScanIntent.LevelUpgrade)]
    public void LevelForScan_UnderProgressive_TheFullLevelForcesRunFull(ScanIntent intent) =>
        Assert.Equal(
            ExtractIndexLevel.Full,
            IndexLevels.LevelForScan(intent, newArtifact: false, IndexLevelPolicy.Progressive));

    [Theory]
    [InlineData(ScanIntent.IncrementalReconcile, true, ExtractIndexLevel.Symbols)]
    [InlineData(ScanIntent.IncrementalReconcile, false, ExtractIndexLevel.Full)]
    [InlineData(ScanIntent.UserFullRebuild, false, ExtractIndexLevel.Symbols)]
    [InlineData(ScanIntent.CorruptionHeal, false, ExtractIndexLevel.Symbols)]
    public void LevelForScan_UnderSymbolsOnly_EveryNewArtifactIsSymbols_DeltasInherit(
        ScanIntent intent, bool newArtifact, ExtractIndexLevel expected) =>
        Assert.Equal(expected, IndexLevels.LevelForScan(intent, newArtifact, IndexLevelPolicy.SymbolsOnly));

    [Theory]
    [InlineData("symbols", IndexLevelPolicy.Progressive, true)]
    [InlineData("full", IndexLevelPolicy.Progressive, false)]
    [InlineData("symbols", IndexLevelPolicy.SymbolsOnly, false)]
    [InlineData("symbols", IndexLevelPolicy.Full, false)]
    [InlineData(null, IndexLevelPolicy.Progressive, false)]
    public void UpgradeOwed_OnlyASymbolsArtifactUnderProgressiveOwesTheUpgrade(
        string? recorded, IndexLevelPolicy policy, bool expected) =>
        Assert.Equal(expected, IndexLevels.UpgradeOwed(recorded, policy));
}

/// <summary>
/// Pins <see cref="ExtractIndexLevelReader"/>'s tolerance: absent files, artifacts without the key, and broken
/// databases all read as full — pre-levels artifacts ARE full-level artifacts, and a broken artifact must
/// degrade to "no levels behavior" rather than crash a caller.
/// </summary>
public sealed class ExtractIndexLevelReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("miller-levels-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string DbPath => Path.Combine(_dir, "symbols.db");

    private void CreateArtifact(string? indexLevel)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE artifact_metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
        if (indexLevel is not null)
        {
            cmd.CommandText = "INSERT INTO artifact_metadata (key, value) VALUES ('index_level', $level);";
            cmd.Parameters.AddWithValue("$level", indexLevel);
            cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public void Read_AnAbsentFileReadsAsFull() =>
        Assert.Equal("full", ExtractIndexLevelReader.Read(Path.Combine(_dir, "missing.db")));

    [Fact]
    public void Read_AnArtifactWithoutTheKeyReadsAsFull_PreLevelsArtifactsAreFull()
    {
        CreateArtifact(indexLevel: null);
        Assert.Equal("full", ExtractIndexLevelReader.Read(DbPath));
    }

    [Fact]
    public void Read_ASymbolsArtifactReadsAsSymbols()
    {
        CreateArtifact(indexLevel: "symbols");
        Assert.Equal("symbols", ExtractIndexLevelReader.Read(DbPath));
    }

    [Fact]
    public void Read_AFileThatIsNotADatabaseReadsAsFull()
    {
        File.WriteAllText(DbPath, "not a sqlite database at all");
        Assert.Equal("full", ExtractIndexLevelReader.Read(DbPath));
    }

    [Fact]
    public void ReadStrict_AnAbsentKeyStillReadsAsFull_ButABrokenArtifactThrows()
    {
        CreateArtifact(indexLevel: null);
        Assert.Equal("full", ExtractIndexLevelReader.ReadStrict(DbPath));

        string broken = Path.Combine(_dir, "broken.db");
        File.WriteAllText(broken, "not a sqlite database at all");
        Assert.ThrowsAny<Exception>(() => ExtractIndexLevelReader.ReadStrict(broken));
    }

    [Fact]
    public void ReadStrict_ASymbolsArtifactReadsAsSymbols()
    {
        CreateArtifact(indexLevel: "symbols");
        Assert.Equal("symbols", ExtractIndexLevelReader.ReadStrict(DbPath));
    }
}
