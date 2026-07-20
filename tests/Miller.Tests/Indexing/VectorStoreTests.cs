using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Semantic;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Locates the pinned sqlite-vec loadable extension for the Scale suite, or SKIPs (never fails) when it has
/// not been fetched. This is THE launch signal for tests that load the native extension: it checks the
/// <c>MILLER_SQLITE_VEC_PATH</c> override first, then the cache <c>scripts/spike-sqlite-vec.sh</c> writes.
/// </summary>
internal static class SqliteVecTestSupport
{
    public static string RequireExtension()
    {
        string? extension = Locate();
        Assert.SkipWhen(extension is null,
            $"sqlite-vec {VectorStore.PinnedVecVersion} not found. Run scripts/spike-sqlite-vec.sh or set " +
            $"{VectorStore.ExtensionPathEnvVar} to enable the Scale test.");
        return extension!;
    }

    private static string? Locate()
    {
        if (VectorStore.ResolveExtensionPath() is { } configured && File.Exists(configured))
            return configured;

        string cacheRoot = Environment.GetEnvironmentVariable("SPIKE_CACHE_DIR")
            ?? Path.Combine(Path.GetTempPath(), "miller-sqlite-vec-spike", VectorStore.PinnedVecVersion, Rid());

        string candidate = Path.Combine(cacheRoot, MemberName());
        return File.Exists(candidate) ? candidate : null;
    }

    private static string Rid()
    {
        string architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        if (OperatingSystem.IsMacOS())
            return $"osx-{architecture}";
        return OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
    }

    private static string MemberName()
    {
        if (OperatingSystem.IsMacOS())
            return "vec0.dylib";
        return OperatingSystem.IsWindows() ? "vec0.dll" : "vec0.so";
    }
}

/// <summary>
/// Schema composition and meta validation — no sqlite-vec, no database — so the vec0 declaration staying
/// derived from the lane string is guarded by the fast suite.
/// </summary>
public sealed class VectorStoreSchemaTests
{
    private static readonly SemanticGenerationIdentity Identity =
        MillerSemanticContract.PinnedIdentity(MillerSemanticContract.DefaultEncoder);

    [Fact]
    public void SchemaDdl_DerivesTheVec0DeclarationFromTheDefaultLane()
    {
        string ddl = VectorStore.SchemaDdl(MillerSemanticContract.ParseStorageSchema("vec0-int8-512-cosine-v1"));

        Assert.Contains("embedding int8[512] distance_metric=cosine", ddl, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaDdl_DerivesTheVec0DeclarationFromTheFallbackLane()
    {
        string ddl = VectorStore.SchemaDdl(MillerSemanticContract.ParseStorageSchema("vec0-int8-384-cosine-v1"));

        Assert.Contains("embedding int8[384] distance_metric=cosine", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("[512]", ddl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CREATE TABLE vectors_meta")]
    [InlineData("CREATE VIRTUAL TABLE symbol_vectors USING vec0(")]
    [InlineData("CREATE VIRTUAL TABLE chunk_vectors USING vec0(")]
    [InlineData("CREATE TABLE symbol_vector_map")]
    [InlineData("CREATE TABLE chunk_vector_map")]
    [InlineData("CREATE INDEX symbol_vector_map_path")]
    [InlineData("CREATE INDEX symbol_vector_map_revision")]
    [InlineData("CREATE INDEX chunk_vector_map_path")]
    [InlineData("CREATE INDEX chunk_vector_map_revision")]
    public void SchemaDdl_CarriesEveryContractTable(string fragment)
    {
        string ddl = VectorStore.SchemaDdl(MillerSemanticContract.ParseStorageSchema("vec0-int8-512-cosine-v1"));

        Assert.Contains(fragment, ddl, StringComparison.Ordinal);
    }

    [Fact]
    public void IdentityFrom_RejectsMetaMissingAnyKeyAReaderMustHave()
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["contract_version"] = "1",
            ["encoder_fingerprint"] = "sha256:abc",
        };

        var ex = Assert.Throws<VectorStoreException>(() => VectorStore.IdentityFrom(meta));

        Assert.Contains("storage_schema", ex.Message, StringComparison.Ordinal);
        Assert.Contains("corpus_generation", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IdentityFrom_ReadsTheFiveIdentityFields()
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["contract_version"] = "1",
            ["encoder_fingerprint"] = "sha256:abc",
            ["storage_schema"] = "vec0-int8-512-cosine-v1",
            ["corpus_generation"] = "cards-v1-chunks-v1",
            ["writer_version"] = "1.13.0+abc1234",
            ["min_reader_version"] = "1.13.0",
            ["fusion_profile"] = "fusion-v1",
        };

        SemanticGenerationIdentity identity = VectorStore.IdentityFrom(meta);

        Assert.Equal("sha256:abc", identity.EncoderFingerprint);
        Assert.Equal("vec0-int8-512-cosine-v1", identity.StorageSchema);
        Assert.Equal("cards-v1-chunks-v1", identity.CorpusGeneration);
        Assert.Equal("1.13.0+abc1234", identity.WriterVersion);
        Assert.Equal("1.13.0", identity.MinReaderVersion);
        Assert.Equal("fusion-v1", identity.FusionProfile);
    }

    [Fact]
    public void Create_RefusesARelativeExtensionPath()
    {
        var ex = Assert.Throws<VectorStoreException>(() =>
            VectorStore.Create("ignored.db", Identity, "artifact-0001", "vec0.dylib"));

        Assert.Contains("must be absolute", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RefusesAnAbsentExtensionRatherThanDegradingSilently()
    {
        string missing = Path.Combine(Path.GetTempPath(), "miller-no-such-vec0-" + Guid.NewGuid() + ".dylib");

        var ex = Assert.Throws<VectorStoreException>(() =>
            VectorStore.Create("ignored.db", Identity, "artifact-0001", missing));

        Assert.Contains("sqlite-vec extension not found", ex.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// The sidecar's open path: <c>vectors_meta</c> completeness and the two reader gates, driven through fake
/// probe/opener seams so the whole classification is fast-suite pure.
/// </summary>
public sealed class VectorSidecarOpenPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "miller-vec-open-" + Guid.NewGuid());

    private static readonly SemanticReaderIdentity Reader = new(
        MillerSemanticContract.EncoderFingerprint(MillerSemanticContract.DefaultEncoder),
        "1.13.0");

    public VectorSidecarOpenPathTests() => Directory.CreateDirectory(Path.Combine(_root, ".miller"));

    [Fact]
    public void ReadyGeneration_ClassifiesAsReady()
    {
        VectorSidecar sidecar = SidecarOver(ReadyMeta());

        Assert.Equal("ready", sidecar.Inspect(_root).State);
    }

    [Fact]
    public void UnreadableArtifact_IsUnavailableWithTheOpenersStatedReason()
    {
        VectorSidecar sidecar = SidecarOver(new FakeOpener("sqlite-vec 0.1.8 != pinned 0.1.9"));

        VectorSidecarFacts facts = sidecar.Inspect(_root);

        Assert.Equal("unavailable", facts.State);
        Assert.Contains("sqlite-vec 0.1.8 != pinned 0.1.9", facts.Reason!, StringComparison.Ordinal);
        Assert.Contains("miller workspace refresh", facts.Reason!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("encoder_fingerprint")]
    [InlineData("storage_schema")]
    [InlineData("corpus_generation")]
    public void MetaMissingARequiredKey_IsTreatedAsCorrupt(string missingKey)
    {
        Dictionary<string, string> meta = ReadyMeta();
        meta.Remove(missingKey);

        VectorSidecarFacts facts = SidecarOver(meta).Inspect(_root);

        Assert.Equal("unavailable", facts.State);
        Assert.Contains("corrupt", facts.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void MismatchedContractVersion_IsIncompatible()
    {
        Dictionary<string, string> meta = ReadyMeta();
        meta["contract_version"] = "2";

        VectorSidecarFacts facts = SidecarOver(meta).Inspect(_root);

        Assert.Equal("incompatible", facts.State);
        Assert.Contains("contract_version", facts.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void MismatchedEncoderFingerprint_IsIncompatibleAndTriggersNoReEmbed()
    {
        Dictionary<string, string> meta = ReadyMeta();
        meta["encoder_fingerprint"] = MillerSemanticContract.EncoderFingerprint(MillerSemanticContract.FallbackEncoder);
        var opener = new FakeOpener(meta);
        var sidecar = new VectorSidecar(SemanticMode.On, new StubProbe(exists: true), opener, Reader);

        VectorSidecarFacts facts = sidecar.Inspect(_root);

        Assert.Equal("incompatible", facts.State);
        Assert.Contains("different encoder", facts.Reason!, StringComparison.Ordinal);
        Assert.Contains("left untouched", facts.Reason!, StringComparison.Ordinal);
        Assert.Equal(1, opener.Reads);
    }

    [Fact]
    public void ReaderBelowMinReaderVersion_IsRefusedWithAStatedReason()
    {
        Dictionary<string, string> meta = ReadyMeta();
        meta["min_reader_version"] = "2.0.0";

        var sidecar = new VectorSidecar(SemanticMode.On, new StubProbe(exists: true), new FakeOpener(meta), Reader);
        VectorSidecarFacts facts = sidecar.Inspect(_root);

        Assert.Equal("incompatible", facts.State);
        Assert.Contains("2.0.0", facts.Reason!, StringComparison.Ordinal);
        Assert.Null(sidecar.TryOpen(_root, out _));
        Assert.Throws<InvalidOperationException>(() => { sidecar.OpenRequired(_root); });
    }

    [Fact]
    public void ReaderAboveMinReaderVersion_IsAcceptedDespiteTextOrdering()
    {
        Dictionary<string, string> meta = ReadyMeta();
        meta["min_reader_version"] = "1.9.0";

        var sidecar = new VectorSidecar(
            SemanticMode.On, new StubProbe(exists: true), new FakeOpener(meta),
            Reader with { ReaderVersion = "1.13.0+abc1234" });

        Assert.Equal("ready", sidecar.Inspect(_root).State);
    }

    [Fact]
    public void GenerationStillBuilding_IsNotQueryable()
    {
        Dictionary<string, string> meta = ReadyMeta();
        meta["build_state"] = "building";
        meta["build_progress_percent"] = "42";

        VectorSidecarFacts facts = SidecarOver(meta).Inspect(_root);

        Assert.Equal("building", facts.State);
        Assert.Contains("42%", facts.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingArtifact_NeverAsksTheOpenerAnything()
    {
        var opener = new FakeOpener(ReadyMeta());
        var sidecar = new VectorSidecar(SemanticMode.On, new StubProbe(exists: false), opener, Reader);

        Assert.Equal("unavailable", sidecar.Inspect(_root).State);
        Assert.Equal(0, opener.Reads);
    }

    private VectorSidecar SidecarOver(Dictionary<string, string> meta) =>
        new(SemanticMode.On, new StubProbe(exists: true), new FakeOpener(meta), Reader);

    private VectorSidecar SidecarOver(FakeOpener opener) =>
        new(SemanticMode.On, new StubProbe(exists: true), opener, Reader);

    private static Dictionary<string, string> ReadyMeta() => new(StringComparer.Ordinal)
    {
        ["contract_version"] = MillerSemanticContract.ContractVersion,
        ["encoder_fingerprint"] = MillerSemanticContract.EncoderFingerprint(MillerSemanticContract.DefaultEncoder),
        ["storage_schema"] = "vec0-int8-512-cosine-v1",
        ["corpus_generation"] = "cards-v1-chunks-v1",
        ["writer_version"] = "1.13.0+abc1234",
        ["min_reader_version"] = "1.13.0",
        ["fusion_profile"] = MillerSemanticContract.FusionProfile,
        ["build_state"] = "ready",
    };

    private sealed class StubProbe(bool exists) : IVectorFileProbe
    {
        public bool FileExists(string path) => exists;

        public IReadOnlyList<string> EnumerateRetainedGenerations(string millerDir) => [];
    }

    private sealed class FakeOpener : IVectorStoreOpener
    {
        private readonly IReadOnlyDictionary<string, string>? _meta;
        private readonly string _failureReason;

        public FakeOpener(IReadOnlyDictionary<string, string> meta)
        {
            _meta = meta;
            _failureReason = string.Empty;
        }

        public FakeOpener(string failureReason) => _failureReason = failureReason;

        public int Reads { get; private set; }

        public bool TryReadMeta(string path, out IReadOnlyDictionary<string, string> meta, out string failureReason)
        {
            Reads++;
            if (_meta is null)
            {
                meta = new Dictionary<string, string>(StringComparer.Ordinal);
                failureReason = _failureReason;
                return false;
            }

            meta = _meta;
            failureReason = string.Empty;
            return true;
        }

        /// <summary>A fake cannot manufacture a real sqlite-vec-backed store, so the ready-open path is
        /// covered by <see cref="VectorStoreTests.TryOpen_ReadyGeneration_ReturnsAUsableStore"/> instead.</summary>
        public VectorStore? OpenStore(string path, out string failureReason)
        {
            failureReason = "the fake opener manufactures no store";
            return null;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}

/// <summary>
/// The physical artifact against the real pinned sqlite-vec extension. Scale-tagged because it loads a native
/// loadable extension; every test funnels through <see cref="SqliteVecTestSupport.RequireExtension"/>, which
/// skips rather than fails when the extension has not been fetched.
/// </summary>
[Trait("Category", "Scale")]
public sealed class VectorStoreTests : IDisposable
{
    private const string ArtifactId = "artifact-0001";

    private readonly string _dir = Directory.CreateTempSubdirectory("miller-vector-store-").FullName;

    private string DbPath => Path.Combine(_dir, "vectors.db");

    [Fact]
    public void Create_StampsThePinnedIdentityAndMatchesTheContractVecVersion()
    {
        using VectorStore store = CreateStore();

        Assert.Equal(VectorStore.PinnedVecVersion, store.VecVersion);
        Assert.Equal("1", store.Meta("contract_version"));
        Assert.Equal(
            MillerSemanticContract.EncoderFingerprint(MillerSemanticContract.DefaultEncoder),
            store.Meta("encoder_fingerprint"));
        Assert.Equal("vec0-int8-512-cosine-v1", store.Meta("storage_schema"));
        Assert.Equal("cards-v1-chunks-v1", store.Meta("corpus_generation"));
        Assert.Equal(MillerSemanticContract.MinReaderVersion, store.Meta("min_reader_version"));
        Assert.Equal(MillerSemanticContract.FusionProfile, store.Meta("fusion_profile"));
        Assert.Equal(ArtifactId, store.Meta("artifact_id"));
        Assert.Equal("blake3", store.Meta("hash_algorithm"));
        Assert.Equal("building", store.Meta("build_state"));
    }

    [Fact]
    public void Create_BothCursorsStartAtZeroAndIndependently()
    {
        using VectorStore store = CreateStore();

        Assert.Equal("0", store.Meta("symbol_completed_revision"));
        Assert.Equal("0", store.Meta("symbol_target_revision"));
        Assert.Equal("0", store.Meta("chunk_completed_revision"));
        Assert.Equal("0", store.Meta("chunk_target_revision"));
        Assert.Equal(ArtifactId, store.Meta("chunk_source_artifact_id"));
    }

    [Fact]
    public void MappingTables_CarryTheContractColumnsInOrder()
    {
        using VectorStore store = CreateStore();

        Assert.Equal(
            ["rowid_ref", "symbol_id", "path", "embed_text_hash", "revision"],
            store.TableColumns("symbol_vector_map"));
        Assert.Equal(
            ["rowid_ref", "chunk_id", "path", "embed_text_hash", "revision"],
            store.TableColumns("chunk_vector_map"));
        Assert.Equal(["key", "value"], store.TableColumns("vectors_meta"));
    }

    [Theory]
    [InlineData("symbol_vectors")]
    [InlineData("chunk_vectors")]
    public void VectorTables_CarryTheContractMetadataColumns(string table)
    {
        using VectorStore store = CreateStore();

        IReadOnlyList<string> columns = store.TableColumns(table);

        Assert.Contains("embedding", columns);
        Assert.Contains("path", columns);
        Assert.Contains("kind", columns);
        Assert.Contains("is_test", columns);
    }

    [Theory]
    [InlineData(VectorUnitKind.Symbol)]
    [InlineData(VectorUnitKind.Chunk)]
    public void WriteThenSearch_RoundTripsTheNearestUnit(VectorUnitKind kind)
    {
        using VectorStore store = CreateStore();

        store.Upsert(kind, 1, "unit-near", "src/Near.cs", Vector(100), "hash-near", 7, "class", isTest: false);
        store.Upsert(kind, 2, "unit-far", "src/Far.cs", Vector(-100), "hash-far", 7, "class", isTest: false);

        IReadOnlyList<VectorMatch> matches = store.Search(kind, Vector(100), k: 2);

        Assert.Equal(2, matches.Count);
        Assert.Equal("unit-near", matches[0].UnitId);
        Assert.Equal("src/Near.cs", matches[0].Path);
        Assert.Equal(1, matches[0].RowId);
        Assert.True(matches[0].Distance <= matches[1].Distance);
    }

    [Fact]
    public void Upsert_ReplacesAUnitsVectorAndMappingRowInPlace()
    {
        using VectorStore store = CreateStore();

        store.Upsert(VectorUnitKind.Symbol, 1, "unit", "src/A.cs", Vector(100), "hash-1", 7, "class", isTest: false);
        store.Upsert(VectorUnitKind.Symbol, 1, "unit", "src/A.cs", Vector(-100), "hash-2", 9, "class", isTest: false);

        VectorMatch only = Assert.Single(store.Search(VectorUnitKind.Symbol, Vector(-100), k: 4));

        Assert.Equal("unit", only.UnitId);
    }

    [Fact]
    public void Upsert_RefusesAnEmbeddingWhoseDimsDisagreeWithTheLane()
    {
        using VectorStore store = CreateStore();

        var ex = Assert.Throws<VectorStoreException>(() => store.Upsert(
            VectorUnitKind.Symbol, 1, "unit", "src/A.cs", new sbyte[8], "hash", 1, "class", isTest: false));

        Assert.Contains("512", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveGlob_IsTheMappingTablePathSurface()
    {
        using VectorStore store = CreateStore();

        store.Upsert(VectorUnitKind.Symbol, 1, "a", "src/ui/A.cs", Vector(10), "h1", 1, "class", isTest: false);
        store.Upsert(VectorUnitKind.Symbol, 2, "b", "src/core/B.cs", Vector(20), "h2", 1, "class", isTest: false);

        Assert.Equal([1L], store.ResolveGlob(VectorUnitKind.Symbol, "src/ui/*"));
    }

    [Fact]
    public void Open_ReadsBackTheStampedIdentity()
    {
        string extension = SqliteVecTestSupport.RequireExtension();

        using (VectorStore created = CreateStore())
        {
            created.SetMeta("build_state", "ready");
        }

        using VectorStore reopened = VectorStore.Open(DbPath, extension, readOnly: true);

        Assert.Equal(
            MillerSemanticContract.PinnedIdentity(MillerSemanticContract.DefaultEncoder),
            reopened.Identity);
        Assert.Equal("ready", reopened.Meta("build_state"));
        Assert.Equal(512, reopened.Lane.Dims);
    }

    [Fact]
    public void TryOpen_ReadyGeneration_ReturnsAUsableStore()
    {
        string extension = SqliteVecTestSupport.RequireExtension();
        string workspaceRoot = Path.Combine(_dir, "workspace");
        Directory.CreateDirectory(Path.Combine(workspaceRoot, ".miller"));
        string artifact = VectorSidecar.PathFor(workspaceRoot);

        using (VectorStore created = VectorStore.Create(
                   artifact,
                   MillerSemanticContract.PinnedIdentity(MillerSemanticContract.DefaultEncoder),
                   ArtifactId,
                   extension))
        {
            created.Upsert(VectorUnitKind.Symbol, 1, "unit", "src/A.cs", Vector(100), "h1", 1, "class", isTest: false);
            created.SetMeta("build_state", "ready");
        }

        var sidecar = new VectorSidecar(
            SemanticMode.On, SystemVectorFileProbe.Instance, new RealOpener(extension));

        using VectorStore? opened = sidecar.TryOpen(workspaceRoot, out string? reason);

        Assert.NotNull(opened);
        Assert.Null(reason);
        Assert.Equal("unit", Assert.Single(opened!.Search(VectorUnitKind.Symbol, Vector(100), k: 4)).UnitId);
    }

    /// <summary>Opens the real artifact through an explicit extension path, so the test never mutates the
    /// process environment (unsafe under xunit's parallel collections).</summary>
    private sealed class RealOpener(string extensionPath) : IVectorStoreOpener
    {
        public bool TryReadMeta(string path, out IReadOnlyDictionary<string, string> meta, out string failureReason)
        {
            meta = VectorStore.ReadMetaAt(path, extensionPath);
            failureReason = string.Empty;
            return true;
        }

        public VectorStore? OpenStore(string path, out string failureReason)
        {
            failureReason = string.Empty;
            return VectorStore.Open(path, extensionPath, readOnly: true);
        }
    }

    [Fact]
    public void Open_RefusesAnArtifactThatIsNotAVectorStore()
    {
        string extension = SqliteVecTestSupport.RequireExtension();
        File.WriteAllText(DbPath, "not a database at all");

        Assert.ThrowsAny<Exception>(() => VectorStore.Open(DbPath, extension, readOnly: true));
    }

    [Fact]
    public void Search_AgainstAMissingVectorTable_FailsAsAVectorStoreException()
    {
        string extension = SqliteVecTestSupport.RequireExtension();
        using VectorStore store = CreateStore();
        DropTable("symbol_vectors", extension);

        VectorStoreException failure = Assert.Throws<VectorStoreException>(
            () => store.Search(VectorUnitKind.Symbol, Vector(1), 4));

        Assert.IsType<SqliteException>(failure.InnerException);
    }

    [Fact]
    public void MappedUnits_AgainstAMissingMappingTable_FailsAsAVectorStoreException()
    {
        string extension = SqliteVecTestSupport.RequireExtension();
        using VectorStore store = CreateStore();
        DropTable("symbol_vector_map", extension);

        VectorStoreException failure = Assert.Throws<VectorStoreException>(
            () => store.MappedUnits(VectorUnitKind.Symbol, null));

        Assert.IsType<SqliteException>(failure.InnerException);
    }

    private void DropTable(string table, string extensionPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());

        connection.Open();
        connection.EnableExtensions(true);
        connection.LoadExtension(extensionPath);
        connection.EnableExtensions(false);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"DROP TABLE {table}";
        command.ExecuteNonQuery();
    }

    private VectorStore CreateStore() => VectorStore.Create(
        DbPath,
        MillerSemanticContract.PinnedIdentity(MillerSemanticContract.DefaultEncoder),
        ArtifactId,
        SqliteVecTestSupport.RequireExtension());

    private static sbyte[] Vector(sbyte leading)
    {
        var values = new sbyte[512];
        values[0] = leading;
        return values;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
