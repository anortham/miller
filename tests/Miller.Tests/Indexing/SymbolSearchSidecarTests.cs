using Microsoft.Data.Sqlite;
using Miller.Core.Search;
using Miller.Indexing;
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
    private readonly string _dir;
    private readonly string _symbolsDbPath;
    private readonly string _searchDbPath;

    public SymbolSearchSidecarTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-sidecar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        // The symbols.db need not exist — TryOpen only ever touches the sibling search.db.
        _symbolsDbPath = Path.Combine(_dir, "symbols.db");
        _searchDbPath = Path.Combine(_dir, "search.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static IndexedSymbol[] Corpus() => new[]
    {
        new IndexedSymbol(0, "a", "Alpha", "interface Alpha", "interface", "csharp", "src/A.cs",
            StartLine: 1, EndLine: 2, ParentId: null, IsTest: false),
        new IndexedSymbol(1, "b", "Beta", null, "class", "csharp", "src/B.cs",
            StartLine: 1, EndLine: 2, ParentId: null, IsTest: false),
    };

    private void WriteSearchDb(long revision) => SearchIndexWriter.Write(_searchDbPath, Corpus(), revision);

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

        bool built = sidecar.EnsureBuilt(julie.DbPath, revision: 5);

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
    public void EnsureBuilt_EnabledAndArtifactAlreadyFresh_SkipsAndReturnsFalse()
    {
        using var julie = JulieDb();
        var sidecar = new SymbolSearchSidecar(enabled: true);

        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 5));    // first build
        Assert.False(sidecar.EnsureBuilt(julie.DbPath, revision: 5));   // already fresh → no rebuild
    }

    [Fact]
    public void EnsureBuilt_ExistingArtifactHasCorruptRevision_RebuildsWithoutThrowing()
    {
        using var julie = JulieDb();
        var sidecar = new SymbolSearchSidecar(enabled: true);
        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 5));

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

        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 5));
        Assert.NotNull(sidecar.TryOpen(julie.DbPath, expectedRevision: 5));
    }

    [Fact]
    public void EnsureBuilt_EnabledAndArtifactStale_RebuildsAtTheNewRevisionAndReturnsTrue()
    {
        using var julie = JulieDb();
        var sidecar = new SymbolSearchSidecar(enabled: true);

        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 5));
        Assert.Null(sidecar.TryOpen(julie.DbPath, expectedRevision: 6));   // stale for revision 6
        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 6));       // rebuild to revision 6
        Assert.NotNull(sidecar.TryOpen(julie.DbPath, expectedRevision: 6));
    }

    [Fact]
    public void EnsureBuilt_ExistingArtifactSchemaStaleAtMatchingRevision_RebuildsAndReturnsTrue()
    {
        using var julie = JulieDb();
        var sidecar = new SymbolSearchSidecar(enabled: true);
        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 5));
        Assert.NotNull(sidecar.TryOpen(julie.DbPath, expectedRevision: 5));   // fresh artifact opens before downgrade

        // Simulate an artifact left behind by an OLDER writer: SAME extract revision, but a schema_version that
        // predates the current SearchIndexWriter.SchemaVersion. The READ gate rejects it on the version mismatch,
        // so the freshness gate MUST treat it as needs-rebuild even though the revision matches — otherwise the
        // sidecar would self-heal to the in-memory index forever at a matching revision (the silent-disable bug
        // class of commit 5362b3d). This pins the two gates in lockstep after a schema bump.
        string searchDb = SymbolSearchSidecar.SearchDbPathFor(julie.DbPath);
        SetSchemaVersion(searchDb, SearchIndexWriter.SchemaVersion - 1);

        Assert.Null(sidecar.TryOpen(julie.DbPath, expectedRevision: 5));      // read gate now rejects the stale schema
        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 5));          // freshness gate rebuilds at the SAME revision
        Assert.NotNull(sidecar.TryOpen(julie.DbPath, expectedRevision: 5));   // rebuilt artifact is current-schema again
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
}
