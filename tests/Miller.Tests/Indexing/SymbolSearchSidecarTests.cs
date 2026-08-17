using Microsoft.Data.Sqlite;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the Phase 3 routing gate <see cref="SymbolSearchSidecar"/>: the decision of whether a search resolves
/// against the on-disk <c>search.db</c> (<see cref="FtsSymbolSearchIndex"/>) or self-heals to the in-memory
/// index. The artifact is built with <see cref="SearchIndexWriter.Write"/> (no julie subprocess — fast suite).
/// The load-bearing guarantee: <see cref="SymbolSearchSidecar.TryOpen"/> NEVER throws — a missing/stale/corrupt
/// sidecar yields <c>null</c> so the caller stays correct on the slow path.
/// </summary>
public sealed class SymbolSearchSidecarTests : IDisposable
{
    private readonly JulieDbFixture _julie;
    private readonly string _symbolsDbPath;
    private readonly string _searchDbPath;

    public SymbolSearchSidecarTests()
    {
        // A real extract artifact, not a bare path: the routing gate compares the sidecar's stamp against the
        // live artifact's id, and a symbols.db that does not exist is a state production never serves from.
        _julie = JulieDbFixture.CreateDefault();
        _symbolsDbPath = _julie.DbPath;
        _searchDbPath = SymbolSearchSidecar.SearchDbPathFor(_symbolsDbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _julie.Dispose();
    }

    private static IndexedSymbol[] Corpus() => new[]
    {
        new IndexedSymbol(0, "a", "Alpha", "interface Alpha", "interface", "csharp", "src/A.cs",
            StartLine: 1, EndLine: 2, ParentId: null, IsTest: false),
        new IndexedSymbol(1, "b", "Beta", null, "class", "csharp", "src/B.cs",
            StartLine: 1, EndLine: 2, ParentId: null, IsTest: false),
    };

    private void WriteSearchDb(long revision) =>
        SearchIndexWriter.Write(
            _searchDbPath, Corpus(), revision, _symbolsDbPath, workspaceRoot: null, RegionIndexOptions.Disabled);

    // Default-ON semantics (Phase 5): only an explicit falsy token opts out; unset/empty/truthy/unknown stays on.
    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("False")]
    [InlineData("off")]
    [InlineData("OFF")]
    [InlineData("no")]
    [InlineData("  no  ")]
    public void IsDisabledValue_ExplicitFalsyTokens_OptOut(string raw) =>
        Assert.True(SymbolSearchSidecar.IsDisabledValue(raw));

    [Theory]
    [InlineData(null)]       // unset ⇒ default on
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("on")]
    [InlineData("yes")]
    [InlineData("garbage")]  // unrecognized ⇒ NOT a disable (opt-out only)
    public void IsDisabledValue_UnsetTruthyOrUnknown_StaysEnabled(string? raw) =>
        Assert.False(SymbolSearchSidecar.IsDisabledValue(raw));

    [Theory]
    [InlineData(null, true)]       // unset ⇒ enabled by default
    [InlineData("", true)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("garbage", true)]  // unknown ⇒ still enabled
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("off", false)]
    [InlineData("no", false)]
    public void FromEnvValue_DefaultsOn_OptsOutOnFalsy(string? raw, bool expectedEnabled) =>
        Assert.Equal(expectedEnabled, SymbolSearchSidecar.FromEnvValue(raw).Enabled);

    [Theory]
    [InlineData(null, true, true)]
    [InlineData("", true, true)]
    [InlineData("1", true, true)]
    [InlineData("true", true, true)]
    [InlineData("on", true, true)]
    [InlineData("yes", true, true)]
    [InlineData("garbage", true, true)]
    [InlineData("0", true, false)]
    [InlineData("false", true, false)]
    [InlineData("off", true, false)]
    [InlineData("no", true, false)]
    public void FromEnvValue_RegionIndexDefaultsOn_AndOptsOutFalsy(
        string? regionRaw, bool expectedSidecarEnabled, bool expectedRegionEnabled)
    {
        SymbolSearchSidecar sidecar = SymbolSearchSidecar.FromEnvValue(sidecarRaw: null, regionRaw);

        Assert.Equal(expectedSidecarEnabled, sidecar.Enabled);
        Assert.Equal(expectedRegionEnabled, sidecar.RegionOptions.Enabled);
    }

    [Fact]
    public void Constructor_EnabledSidecar_DefaultsRegionIndexOn()
    {
        var sidecar = new SymbolSearchSidecar(enabled: true);

        Assert.True(sidecar.Enabled);
        Assert.True(sidecar.RegionOptions.Enabled);
        Assert.Equal(RegionIndexOptions.DefaultMaxRegionBytes, sidecar.RegionOptions.MaxRegionBytes);
    }

    [Fact]
    public void FromEnvValue_DisabledSidecar_ForcesRegionIndexOff()
    {
        SymbolSearchSidecar sidecar = SymbolSearchSidecar.FromEnvValue(sidecarRaw: "0", regionRaw: null);

        Assert.False(sidecar.Enabled);
        Assert.False(sidecar.RegionOptions.Enabled);
    }

    [Theory]
    [InlineData(null, RegionIndexOptions.DefaultMaxRegionBytes)]
    [InlineData("", RegionIndexOptions.DefaultMaxRegionBytes)]
    [InlineData("garbage", RegionIndexOptions.DefaultMaxRegionBytes)]
    [InlineData("0", RegionIndexOptions.DefaultMaxRegionBytes)]
    [InlineData("-1", RegionIndexOptions.DefaultMaxRegionBytes)]
    [InlineData("4096", 4096)]
    [InlineData(" 8192 ", 8192)]
    public void FromEnvValue_RegionMaxBytesDefaultsAndParsesPositiveValues(
        string? maxRegionBytesRaw, int expectedMaxRegionBytes)
    {
        SymbolSearchSidecar sidecar = SymbolSearchSidecar.FromEnvValue(
            sidecarRaw: null,
            regionRaw: "1",
            maxRegionBytesRaw);

        Assert.True(sidecar.Enabled);
        Assert.True(sidecar.RegionOptions.Enabled);
        Assert.Equal(expectedMaxRegionBytes, sidecar.RegionOptions.MaxRegionBytes);
    }

    [Fact]
    public void SearchDbPathFor_IsTheSiblingSearchDbInTheSameDirectory()
    {
        string derived = SymbolSearchSidecar.SearchDbPathFor(_symbolsDbPath);
        Assert.Equal(_searchDbPath, derived);
        Assert.Equal("search.db", Path.GetFileName(derived));
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(_symbolsDbPath)), Path.GetDirectoryName(derived));
    }

    [Fact]
    public void TryOpen_Disabled_ReturnsNullEvenWhenAFreshArtifactExists()
    {
        WriteSearchDb(revision: 7);
        Assert.Null(SymbolSearchSidecar.Disabled.TryOpen(_symbolsDbPath, expectedRevision: 7));
    }

    [Fact]
    public void TryOpen_EnabledButArtifactMissing_ReturnsNull()
    {
        var sidecar = new SymbolSearchSidecar(enabled: true);
        Assert.False(File.Exists(_searchDbPath));
        Assert.Null(sidecar.TryOpen(_symbolsDbPath, expectedRevision: 7));
    }

    [Fact]
    public void TryOpen_EnabledPresentAndRevisionFresh_ReturnsDiskIndex()
    {
        WriteSearchDb(revision: 7);
        var sidecar = new SymbolSearchSidecar(enabled: true);

        FtsSymbolSearchIndex? index = sidecar.TryOpen(_symbolsDbPath, expectedRevision: 7);

        Assert.NotNull(index);
        Assert.Equal(7L, index!.Revision);
        var hit = Assert.Single(index.Search("Alpha", limit: 10));
        Assert.Equal("Alpha", index.Resolve(hit.Document.DocId).Name);
    }

    [Fact]
    public void TryOpen_EnabledButRevisionStale_ReturnsNull()
    {
        WriteSearchDb(revision: 6);                       // artifact built from an older extract revision
        var sidecar = new SymbolSearchSidecar(enabled: true);
        Assert.Null(sidecar.TryOpen(_symbolsDbPath, expectedRevision: 7));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryOpen_EnabledButPathUnusable_ReturnsNullWithoutThrowing(string? badPath)
    {
        // The "NEVER throws" contract covers path derivation too: an unusable symbols.db path must degrade to
        // the in-memory fallback (null), not surface as an ArgumentException out of a search request.
        var sidecar = new SymbolSearchSidecar(enabled: true);
        Assert.Null(sidecar.TryOpen(badPath!, expectedRevision: 7));
    }

    [Fact]
    public void TryOpen_EnabledButArtifactCorrupt_ReturnsNullWithoutThrowing()
    {
        File.WriteAllText(_searchDbPath, "this is not a sqlite database");
        var sidecar = new SymbolSearchSidecar(enabled: true);
        Assert.Null(sidecar.TryOpen(_symbolsDbPath, expectedRevision: 7));
    }

    // ---- EnsureBuilt (Phase 4: the lock-holding writer builds search.db from the extract) ----------------

    private static JulieDbFixture JulieDb() => JulieDbFixture.Create(
        JulieDbFixture.PinnedSchema,
        JulieDbFixture.PinnedContract,
        new[]
        {
            new JulieDbFixture.SymbolRow("s1", "IAuthenticationProvider", "interface", "csharp",
                "src/Auth.cs", "public interface IAuthenticationProvider", 1, ParentId: null),
            new JulieDbFixture.SymbolRow("s2", "Cache", "class", "csharp",
                "src/Cache.cs", "public class Cache", 1, ParentId: null),
        });

    [Fact]
    public void EnsureBuilt_Disabled_DoesNotWriteAndReturnsFalse()
    {
        using var julie = JulieDb();
        string searchDb = SymbolSearchSidecar.SearchDbPathFor(julie.DbPath);

        bool built = SymbolSearchSidecar.Disabled.EnsureBuilt(julie.DbPath, revision: 5);

        Assert.False(built);
        Assert.False(File.Exists(searchDb));
    }

    [Fact]
    public void EnsureBuilt_EnabledAndArtifactMissing_BuildsFreshUsableArtifactAndReturnsTrue()
    {
        using var julie = JulieDb();
        var sidecar = new SymbolSearchSidecar(enabled: true);
        string searchDb = SymbolSearchSidecar.SearchDbPathFor(julie.DbPath);

        bool built = sidecar.EnsureBuilt(julie.DbPath, revision: 5, workspaceRoot: julie.WorkspaceRoot);

        Assert.True(built);
        Assert.True(File.Exists(searchDb));
        FtsSymbolSearchIndex? index = sidecar.TryOpen(julie.DbPath, expectedRevision: 5);
        Assert.NotNull(index);
        Assert.Equal(5L, index!.Revision);
        // 'thenti' is interior to IAuthen|tica|tion — only the disk artifact's trigram arm recovers it.
        var hit = Assert.Single(index.Search("thenti", limit: 10));
        Assert.Equal("IAuthenticationProvider", index.Resolve(hit.Document.DocId).Name);
    }

    [Fact]
    public void EnsureBuilt_RegionIndexEnabled_RequiresWorkspaceRoot()
    {
        using var julie = JulieDb();
        var sidecar = new SymbolSearchSidecar(enabled: true, RegionIndexOptions.EnabledDefault);

        Assert.ThrowsAny<ArgumentException>(() => sidecar.EnsureBuilt(julie.DbPath, revision: 5));
    }

    [Fact]
    public void EnsureBuilt_RegionIndexEnabled_PopulatesRegionTables()
    {
        const string path = "src/A.cs";
        const string text = "// region TODO\nclass A {}\n";
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("sym-a", "A", "class", "csharp", path, "class A", 2, ParentId: null),
            },
            fileContent: new Dictionary<string, string> { [path] = text },
            sourceRegions: new[]
            {
                new JulieDbFixture.SourceRegionRow(
                    "region-a", "file:" + path, path, "csharp", "comment", "sym-a",
                    1, 1, 1, 15, 0, text.IndexOf('\n'), null),
            });
        var sidecar = new SymbolSearchSidecar(enabled: true, RegionIndexOptions.EnabledDefault);

        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 5, workspaceRoot: julie.WorkspaceRoot));

        string searchDb = SymbolSearchSidecar.SearchDbPathFor(julie.DbPath);
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = searchDb,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM search_regions WHERE region_id='region-a';";
        Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    [Fact]
    public void EnsureBuilt_RegionOptionChangedToEnabled_RebuildsAtMatchingRevision()
    {
        const string path = "src/A.cs";
        const string text = "// region TODO\nclass A {}\n";
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("sym-a", "A", "class", "csharp", path, "class A", 2, ParentId: null),
            },
            fileContent: new Dictionary<string, string> { [path] = text },
            sourceRegions: new[]
            {
                new JulieDbFixture.SourceRegionRow(
                    "region-a", "file:" + path, path, "csharp", "comment", "sym-a",
                    1, 1, 1, 15, 0, text.IndexOf('\n'), null),
            });
        var disabledRegions = new SymbolSearchSidecar(enabled: true, RegionIndexOptions.Disabled);
        var enabledRegions = new SymbolSearchSidecar(enabled: true);
        Assert.True(disabledRegions.EnsureBuilt(julie.DbPath, revision: 5));

        Assert.True(enabledRegions.EnsureBuilt(julie.DbPath, revision: 5, workspaceRoot: julie.WorkspaceRoot));

        string searchDb = SymbolSearchSidecar.SearchDbPathFor(julie.DbPath);
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = searchDb,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM search_regions WHERE region_id='region-a';";
        Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    [Fact]
    public void EnsureBuilt_EnabledAndArtifactAlreadyFresh_SkipsAndReturnsFalse()
    {
        using var julie = JulieDb();
        var sidecar = new SymbolSearchSidecar(enabled: true);

        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 5, workspaceRoot: julie.WorkspaceRoot));    // first build
        Assert.False(sidecar.EnsureBuilt(julie.DbPath, revision: 5, workspaceRoot: julie.WorkspaceRoot));   // already fresh → no rebuild
    }

    [Fact]
    public void EnsureCurrent_StaleArtifactAppliesRevisionFileChangesIncrementally()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("edit-new", "UpdatedType", "class", "csharp",
                    "src/Edit.cs", "public class UpdatedType", 1, ParentId: null),
                new JulieDbFixture.SymbolRow("keep", "Anchor", "class", "csharp",
                    "src/Keep.cs", "public class Anchor", 1, ParentId: null),
            },
            revisions: new[]
            {
                new JulieDbFixture.RevisionRow(1),
                new JulieDbFixture.RevisionRow(2, Kind: "single_file"),
            },
            fileChanges: new[]
            {
                new JulieDbFixture.RevisionFileChangeRow(2, "src/Edit.cs", "updated"),
                new JulieDbFixture.RevisionFileChangeRow(2, "src/Delete.cs", "deleted"),
            });
        string searchDb = SymbolSearchSidecar.SearchDbPathFor(julie.DbPath);
        SearchIndexWriter.Write(searchDb, new[]
        {
            new IndexedSymbol(0, "delete-old", "RemovedThing", "public class RemovedThing", "class",
                "csharp", "src/Delete.cs", 1, 1, ParentId: null, IsTest: false),
            new IndexedSymbol(1, "edit-old", "LegacyWidget", "public class LegacyWidget", "class",
                "csharp", "src/Edit.cs", 1, 1, ParentId: null, IsTest: false),
            new IndexedSymbol(2, "keep", "Anchor", "public class Anchor", "class",
                "csharp", "src/Keep.cs", 1, 1, ParentId: null, IsTest: false),
        }, revision: 1, symbolsDbPath: julie.DbPath, workspaceRoot: julie.WorkspaceRoot,
            regionOptions: RegionIndexOptions.EnabledDefault);
        CreateSentinelTable(searchDb);
        var sidecar = new SymbolSearchSidecar(enabled: true);

        Assert.True(sidecar.EnsureCurrent(julie.DbPath, revision: 2, workspaceRoot: julie.WorkspaceRoot));

        Assert.True(TableExists(searchDb, "incremental_sentinel"));
        FtsSymbolSearchIndex index = Assert.IsType<FtsSymbolSearchIndex>(
            sidecar.TryOpen(julie.DbPath, expectedRevision: 2));
        Assert.Equal(2, index.DocumentCount);
        Assert.Empty(index.Search("RemovedThing", limit: 10));
        Assert.Empty(index.Search("LegacyWidget", limit: 10));
        Assert.Equal("UpdatedType", index.Resolve(Assert.Single(index.Search("UpdatedType", limit: 10)).Document.DocId).Name);
        Assert.Equal("Anchor", index.Resolve(Assert.Single(index.Search("Anchor", limit: 10)).Document.DocId).Name);
    }

    [Fact]
    public void EnsureCurrent_IncrementalUpdateKeepsQualifiedTrigramForUnchangedParent()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("parent", "ParentAnchor", "class", "csharp",
                    "src/AParent.cs", "public class ParentAnchor", 1, ParentId: null),
                new JulieDbFixture.SymbolRow("child", "ChildAction", "method", "csharp",
                    "src/BChild.cs", "void ChildAction()", 1, ParentId: "parent")
                    { IsTest = true, TestContainer = true },
            },
            revisions: new[]
            {
                new JulieDbFixture.RevisionRow(1),
                new JulieDbFixture.RevisionRow(2, Kind: "single_file"),
            },
            fileChanges: new[]
            {
                new JulieDbFixture.RevisionFileChangeRow(2, "src/BChild.cs", "updated"),
            });
        string searchDb = SymbolSearchSidecar.SearchDbPathFor(julie.DbPath);
        SearchIndexWriter.Write(searchDb, new[]
        {
            new IndexedSymbol(0, "parent", "ParentAnchor", "public class ParentAnchor", "class",
                "csharp", "src/AParent.cs", 1, 1, ParentId: null, IsTest: false),
            new IndexedSymbol(1, "child", "ChildAction", "void ChildAction()", "method",
                "csharp", "src/BChild.cs", 1, 1, ParentId: "parent", IsTest: true,
                TestContainer: false, TestLifecycle: true,
                TestEvidenceStatus: TestRoleEvidence.UnknownStatus,
                TestEvidenceReason: TestRoleEvidence.FileStatusReason),
        }, revision: 1, symbolsDbPath: julie.DbPath, workspaceRoot: julie.WorkspaceRoot,
            regionOptions: RegionIndexOptions.EnabledDefault);
        var sidecar = new SymbolSearchSidecar(enabled: true);

        Assert.Equal("ChildAction",
            Assert.Single(sidecar.TryOpen(julie.DbPath, expectedRevision: 1)!.Search("anchorchild", limit: 10))
                .Document.Name);

        Assert.True(sidecar.EnsureCurrent(julie.DbPath, revision: 2, workspaceRoot: julie.WorkspaceRoot));

        FtsSymbolSearchIndex index = Assert.IsType<FtsSymbolSearchIndex>(
            sidecar.TryOpen(julie.DbPath, expectedRevision: 2));
        SearchHit hit = Assert.Single(index.Search("anchorchild", limit: 10));
        IndexedSymbol child = index.Resolve(hit.Document.DocId);
        Assert.Equal("ChildAction", child.Name);
        Assert.Equal(1, child.DocId);
        Assert.Equal(
            new TestRoleEvidence(
                IsTest: true,
                IsContainer: true,
                IsLifecycle: false,
                Status: TestRoleEvidence.CurrentStatus,
                Reason: null),
            child.TestEvidence);
        Assert.Equal(0, index.FindBySymbolId("parent")!.DocId);
    }

    [Fact]
    public void EnsureCurrent_StaleArtifactWithNoRevisionFileChanges_RebuildsFromCurrentSymbols()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("new", "UpdatedType", "class", "csharp",
                    "src/Edit.cs", "public class UpdatedType", 1, ParentId: null),
            },
            revisions: new[]
            {
                new JulieDbFixture.RevisionRow(1),
                new JulieDbFixture.RevisionRow(2, Kind: "full"),
            });
        string searchDb = SymbolSearchSidecar.SearchDbPathFor(julie.DbPath);
        SearchIndexWriter.Write(searchDb, new[]
        {
            new IndexedSymbol(0, "old", "LegacyWidget", "public class LegacyWidget", "class",
                "csharp", "src/Edit.cs", 1, 1, ParentId: null, IsTest: false),
        }, revision: 1, symbolsDbPath: julie.DbPath, workspaceRoot: julie.WorkspaceRoot,
            regionOptions: RegionIndexOptions.EnabledDefault);
        CreateSentinelTable(searchDb);
        var sidecar = new SymbolSearchSidecar(enabled: true);

        Assert.True(sidecar.EnsureCurrent(julie.DbPath, revision: 2, workspaceRoot: julie.WorkspaceRoot));

        Assert.False(TableExists(searchDb, "incremental_sentinel"));
        FtsSymbolSearchIndex index = Assert.IsType<FtsSymbolSearchIndex>(
            sidecar.TryOpen(julie.DbPath, expectedRevision: 2));
        Assert.Empty(index.Search("LegacyWidget", limit: 10));
        Assert.Equal("UpdatedType", index.Resolve(Assert.Single(index.Search("UpdatedType", limit: 10)).Document.DocId).Name);
    }

    [Fact]
    public void EnsureBuilt_ExistingArtifactHasCorruptRevision_RebuildsWithoutThrowing()
    {
        using var julie = JulieDb();
        var sidecar = new SymbolSearchSidecar(enabled: true);
        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 5, workspaceRoot: julie.WorkspaceRoot));

        // A partially-written / damaged artifact: meta.revision is non-numeric. The freshness peek must treat
        // it as "needs rebuild", not propagate a FormatException out of the lock-holding writer.
        string searchDb = SymbolSearchSidecar.SearchDbPathFor(julie.DbPath);
        using (var rw = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = searchDb, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString()))
        {
            rw.Open();
            using var cmd = rw.CreateCommand();
            cmd.CommandText = "UPDATE meta SET revision = 'not-a-number';";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 5, workspaceRoot: julie.WorkspaceRoot));
        Assert.NotNull(sidecar.TryOpen(julie.DbPath, expectedRevision: 5));
    }

    [Fact]
    public void EnsureBuilt_ExistingArtifactHasDuplicateMetaRows_RebuildsAtMatchingRevision()
    {
        using var julie = JulieDb();
        var sidecar = new SymbolSearchSidecar(enabled: true);
        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 5, workspaceRoot: julie.WorkspaceRoot));

        string searchDb = SymbolSearchSidecar.SearchDbPathFor(julie.DbPath);
        CreateSentinelTable(searchDb);
        using (var rw = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = searchDb, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString()))
        {
            rw.Open();
            using var cmd = rw.CreateCommand();
            cmd.CommandText = """
                INSERT INTO meta(
                    revision, doc_count, avgdl, schema_version, region_count, region_avgdl, region_index_enabled)
                VALUES (5, 2, 1.0, $schema, 0, 0.0, 1);
                """;
            cmd.Parameters.AddWithValue("$schema", SearchIndexWriter.SchemaVersion);
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 5, workspaceRoot: julie.WorkspaceRoot));
        Assert.False(TableExists(searchDb, "incremental_sentinel"));
        Assert.NotNull(sidecar.TryOpen(julie.DbPath, expectedRevision: 5));
    }

    [Fact]
    public void EnsureBuilt_EnabledAndArtifactStale_RebuildsAtTheNewRevisionAndReturnsTrue()
    {
        using var julie = JulieDb();
        var sidecar = new SymbolSearchSidecar(enabled: true);

        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 5, workspaceRoot: julie.WorkspaceRoot));
        Assert.Null(sidecar.TryOpen(julie.DbPath, expectedRevision: 6));   // stale for revision 6
        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 6, workspaceRoot: julie.WorkspaceRoot));       // rebuild to revision 6
        Assert.NotNull(sidecar.TryOpen(julie.DbPath, expectedRevision: 6));
    }

    [Fact]
    public void EnsureBuilt_ExistingArtifactSchemaStaleAtMatchingRevision_RebuildsAndReturnsTrue()
    {
        using var julie = JulieDb();
        var sidecar = new SymbolSearchSidecar(enabled: true);
        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 5, workspaceRoot: julie.WorkspaceRoot));
        Assert.NotNull(sidecar.TryOpen(julie.DbPath, expectedRevision: 5));   // fresh artifact opens before downgrade

        // Simulate an artifact left behind by an OLDER writer: SAME extract revision, but a schema_version that
        // predates the current SearchIndexWriter.SchemaVersion. The READ gate rejects it on the version mismatch,
        // so the freshness gate MUST treat it as needs-rebuild even though the revision matches — otherwise the
        // sidecar would self-heal to the in-memory index forever at a matching revision (the silent-disable bug
        // class of commit 5362b3d). This pins the two gates in lockstep after a schema bump.
        string searchDb = SymbolSearchSidecar.SearchDbPathFor(julie.DbPath);
        Assert.Equal(9, SearchIndexWriter.SchemaVersion);
        SetSchemaVersion(searchDb, 7);

        Assert.Null(sidecar.TryOpen(julie.DbPath, expectedRevision: 5));      // read gate now rejects the stale schema
        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 5, workspaceRoot: julie.WorkspaceRoot));          // freshness gate rebuilds at the SAME revision
        Assert.NotNull(sidecar.TryOpen(julie.DbPath, expectedRevision: 5));   // rebuilt artifact is current-schema again
    }

    private static void CreateSentinelTable(string searchDb)
    {
        using (var rw = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = searchDb, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString()))
        {
            rw.Open();
            using var cmd = rw.CreateCommand();
            cmd.CommandText = "CREATE TABLE incremental_sentinel(value INTEGER);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    private static bool TableExists(string searchDb, string tableName)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = searchDb, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name;";
        cmd.Parameters.AddWithValue("$name", tableName);
        return cmd.ExecuteScalar() is not null;
    }

    [Fact]
    public void EnsureBuilt_PromotedArtifactAtTheSameRevision_RebuildsInsteadOfServingPreRebuildSymbols()
    {
        using var julie = JulieDb();
        var sidecar = new SymbolSearchSidecar(enabled: true);
        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 1, workspaceRoot: julie.WorkspaceRoot));

        SetSymbolsArtifactId(julie.DbPath, "artifact-promoted");

        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 1, workspaceRoot: julie.WorkspaceRoot));
    }

    [Fact]
    public void EnsureCurrent_PromotedArtifactAtTheSameRevision_RebuildsRatherThanApplyingADeltaAcrossTheSwap()
    {
        using var julie = JulieDb();
        var sidecar = new SymbolSearchSidecar(enabled: true);
        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 1, workspaceRoot: julie.WorkspaceRoot));

        SetSymbolsArtifactId(julie.DbPath, "artifact-promoted");

        Assert.True(sidecar.EnsureCurrent(julie.DbPath, revision: 1, workspaceRoot: julie.WorkspaceRoot));
        Assert.False(sidecar.EnsureCurrent(julie.DbPath, revision: 1, workspaceRoot: julie.WorkspaceRoot));
    }

    [Fact]
    public void EnsureCurrent_ArtifactMetadataWithoutAnId_RebuildsRatherThanDeltaingIntoASidecarTheReadGatesRefuse()
    {
        using var julie = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("edit-new", "UpdatedType", "class", "csharp",
                    "src/Edit.cs", "public class UpdatedType", 1, ParentId: null),
            },
            revisions: new[]
            {
                new JulieDbFixture.RevisionRow(1),
                new JulieDbFixture.RevisionRow(2, Kind: "single_file"),
            },
            fileChanges: new[] { new JulieDbFixture.RevisionFileChangeRow(2, "src/Edit.cs", "updated") });
        var sidecar = new SymbolSearchSidecar(enabled: true);
        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 1, workspaceRoot: julie.WorkspaceRoot));

        string searchDb = SymbolSearchSidecar.SearchDbPathFor(julie.DbPath);
        CreateSentinelTable(searchDb);

        // A metadata table that yields no id has a null ArtifactId just like a pre-stamping extract does, so a
        // raw null check reads it as "same artifact" and applies a delta — into a sidecar the read gates then
        // refuse. The converge decision has to use the same verdict the read gates do. The sentinel survives an
        // in-place delta and does not survive a rebuild, so it is what separates the two paths.
        DeleteSymbolsArtifactId(julie.DbPath);

        Assert.True(sidecar.EnsureCurrent(julie.DbPath, revision: 2, workspaceRoot: julie.WorkspaceRoot));
        Assert.False(TableExists(searchDb, "incremental_sentinel"));
    }

    [Fact]
    public void EnsureCurrent_UnreadableSymbolsDb_LeavesTheSidecarAloneWhileTryOpenRefusesToServeIt()
    {
        using var julie = JulieDb();
        var sidecar = new SymbolSearchSidecar(enabled: true);
        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 1, workspaceRoot: julie.WorkspaceRoot));

        string quarantined = julie.DbPath + ".moved";
        File.Move(julie.DbPath, quarantined);
        File.WriteAllText(julie.DbPath, "not sqlite");

        try
        {
            // The two gates read the same unprovable identity and reach OPPOSITE verdicts on purpose. A build
            // gate cannot rebuild from a source it cannot read, so it must leave the sidecar alone; a read gate
            // must not serve what it cannot prove, because a lock is likeliest during the promote that changes
            // the generation.
            Assert.False(sidecar.EnsureCurrent(julie.DbPath, revision: 1, workspaceRoot: julie.WorkspaceRoot));
            Assert.Null(sidecar.TryOpen(julie.DbPath, expectedRevision: 1));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(julie.DbPath);
            File.Move(quarantined, julie.DbPath);
        }
    }

    private static void DeleteSymbolsArtifactId(string symbolsDb)
    {
        using (var rw = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = symbolsDb, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString()))
        {
            rw.Open();
            using var cmd = rw.CreateCommand();
            cmd.CommandText = "DELETE FROM artifact_metadata WHERE key = 'artifact_id';";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    private static void SetSymbolsArtifactId(string symbolsDb, string artifactId)
    {
        using (var rw = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = symbolsDb, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString()))
        {
            rw.Open();
            using var cmd = rw.CreateCommand();
            cmd.CommandText = "UPDATE artifact_metadata SET value = $v WHERE key = 'artifact_id';";
            cmd.Parameters.AddWithValue("$v", artifactId);
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    private static void SetSchemaVersion(string searchDb, int version)
    {
        using (var rw = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = searchDb, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString()))
        {
            rw.Open();
            using var cmd = rw.CreateCommand();
            cmd.CommandText = "UPDATE meta SET schema_version = $v;";
            cmd.Parameters.AddWithValue("$v", version);
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void TryOpen_EnabledButSchemaIncompatible_ReturnsNullWithoutThrowing()
    {
        WriteSearchDb(revision: 7);
        using (var rw = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _searchDbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString()))
        {
            rw.Open();
            using var cmd = rw.CreateCommand();
            cmd.CommandText = "UPDATE meta SET schema_version = 999;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var sidecar = new SymbolSearchSidecar(enabled: true);
        Assert.Null(sidecar.TryOpen(_symbolsDbPath, expectedRevision: 7));
    }

    [Fact]
    public void OpenStoreRequired_ConvergingSnapshot_ServesLastGoodSidecarHits()
    {
        string storeRoot = Path.Combine(
            Path.GetTempPath(),
            "miller-search-last-good-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeRoot);
        try
        {
            WorkspaceReadSnapshot lastGood = StoreSnapshot(storeRoot, sequence: 17);
            WorkspaceReadSnapshot live = StoreSnapshot(storeRoot, sequence: 21, resolutionState: "converging");
            string searchPath = StoreSidecarCatalog.PathFor(storeRoot, StoreSidecarKind.Search, lastGood.ViewId);
            SearchIndexWriter.Write(searchPath, Corpus(), revision: 17);
            StoreSidecarCatalog.Stamp(
                searchPath,
                StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Search, lastGood));

            var sidecar = new SymbolSearchSidecar(enabled: true);
            FtsSymbolSearchIndex index = sidecar.OpenStoreRequired(storeRoot, live);

            Assert.Equal(17L, index.Revision);
            var hit = Assert.Single(index.Search("Alpha", limit: 10));
            Assert.Equal("Alpha", index.Resolve(hit.Document.DocId).Name);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(storeRoot))
                Directory.Delete(storeRoot, recursive: true);
        }
    }

    [Fact]
    public void OpenStoreRequired_ExactSnapshotWithEarlierSidecar_StillThrows()
    {
        string storeRoot = Path.Combine(
            Path.GetTempPath(),
            "miller-search-exact-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeRoot);
        try
        {
            WorkspaceReadSnapshot lastGood = StoreSnapshot(storeRoot, sequence: 17);
            WorkspaceReadSnapshot live = StoreSnapshot(storeRoot, sequence: 21, resolutionState: "exact");
            string searchPath = StoreSidecarCatalog.PathFor(storeRoot, StoreSidecarKind.Search, lastGood.ViewId);
            SearchIndexWriter.Write(searchPath, Corpus(), revision: 17);
            StoreSidecarCatalog.Stamp(
                searchPath,
                StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Search, lastGood));

            var sidecar = new SymbolSearchSidecar(enabled: true);
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => sidecar.OpenStoreRequired(storeRoot, live));

            Assert.Contains("Search sidecar for view 'view-a' is missing or stale", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(storeRoot))
                Directory.Delete(storeRoot, recursive: true);
        }
    }

    [Fact]
    public void OpenStoreRequired_MissingSidecar_StillThrowsCurrentMessage()
    {
        string storeRoot = Path.Combine(
            Path.GetTempPath(),
            "miller-search-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeRoot);
        try
        {
            WorkspaceReadSnapshot live = StoreSnapshot(storeRoot, sequence: 21, resolutionState: "converging");
            var sidecar = new SymbolSearchSidecar(enabled: true);

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => sidecar.OpenStoreRequired(storeRoot, live));

            Assert.Contains("Search sidecar for view 'view-a' is missing or stale", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(storeRoot))
                Directory.Delete(storeRoot, recursive: true);
        }
    }

    [Fact]
    public void InspectStore_ConvergingLastGood_StillReportsStale()
    {
        string storeRoot = Path.Combine(
            Path.GetTempPath(),
            "miller-search-stale-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeRoot);
        try
        {
            WorkspaceReadSnapshot lastGood = StoreSnapshot(storeRoot, sequence: 17);
            WorkspaceReadSnapshot live = StoreSnapshot(storeRoot, sequence: 21, resolutionState: "converging");
            string searchPath = StoreSidecarCatalog.PathFor(storeRoot, StoreSidecarKind.Search, lastGood.ViewId);
            SearchIndexWriter.Write(searchPath, Corpus(), revision: 17);
            StoreSidecarCatalog.Stamp(
                searchPath,
                StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Search, lastGood));

            SearchSidecarFacts facts = new SymbolSearchSidecar(enabled: true).InspectStore(storeRoot, live);

            Assert.Equal("stale", facts.State);
            Assert.Equal(21, facts.ExpectedRevision);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(storeRoot))
                Directory.Delete(storeRoot, recursive: true);
        }
    }

    private static WorkspaceReadSnapshot StoreSnapshot(
        string storeRoot,
        long sequence,
        string? resolutionState = null) =>
        new(
            storeRoot,
            "workspace-a",
            "family-a",
            "view-a",
            new WorkspaceFreshnessToken(
                "family-a",
                3,
                "manifest-a",
                sequence,
                "resolution-a",
                StoreInstanceId: "family-a:gen-001",
                ViewId: "view-a",
                GenerationName: "gen-001",
                ManifestGeneration: 3,
                IndexLevel: IndexLevels.FullMetadataValue,
                LevelStampL1: "l1-a",
                LevelStampL2: "l2-a",
                LevelStampL3: "l3-a"),
            IndexLevels.FullMetadataValue,
            WorkspaceReadMode.FamilyStore,
            GenerationName: "gen-001",
            ManifestGeneration: 3,
            ResolutionState: resolutionState);

    [Fact]
    public void TryOpen_EnabledButFtsTablesDamaged_ReturnsNullWithoutThrowing()
    {
        WriteSearchDb(revision: 7);
        using (var rw = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _searchDbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString()))
        {
            rw.Open();
            using var cmd = rw.CreateCommand();
            cmd.CommandText = "DROP TABLE symbols_fts;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var sidecar = new SymbolSearchSidecar(enabled: true);
        Assert.Null(sidecar.TryOpen(_symbolsDbPath, expectedRevision: 7));
    }
}
