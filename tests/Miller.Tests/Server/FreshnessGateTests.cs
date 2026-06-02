using System.Text;
using Microsoft.Data.Sqlite;
using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Server.Hosting;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the M6 freshness gate (m6-design Components/3, impl-order step 6): normalize julie's v1
/// <c>files.content_hash</c> (<c>blake3:&lt;hex&gt;</c>) and compare it to the BLAKE3 of the current disk bytes
/// through <see cref="Miller.Core.Freshness.StalenessCheck"/>. Stored==disk → Fresh; differ → Stale; missing
/// hash / wrong hash_algorithm → Stale (can't verify) unless <c>allow_stale</c>. v1 stores no snapshot text, so
/// the verdict is the hash compare alone. Driven against <see cref="JulieDbFixture.CreateForEdit"/> — fast suite.
/// </summary>
public sealed class FreshnessGateTests
{
    [Fact]
    public void Check_ByteIdenticalFileHash_IsFresh()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        using var workspace = FreshWorkspace();
        string diskPath = WriteFile(workspace, "orders/OrderService.cs", Encoding.UTF8.GetBytes(JulieDbFixture.OrderServiceContent));
        SetFileHash(fx.DbPath, "orders/OrderService.cs", "blake3:" + ContentHasher.Blake3FileHex(diskPath));

        var result = FreshnessGate.Check(
            fx.DbPath,
            "orders/OrderService.cs",
            diskPath,
            JulieDbFixture.OrderServiceContent);

        Assert.Equal(FreshnessResult.Fresh, result.Result);
        Assert.True(result.IndexedContentFound);
    }

    [Fact]
    public void Check_StoredHashHasBlake3Prefix_StillFreshAgainstByteIdenticalFile()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        using var workspace = FreshWorkspace();
        string diskPath = WriteFile(workspace, "orders/OrderService.cs",
            Encoding.UTF8.GetBytes(JulieDbFixture.OrderServiceContent));
        // julie v1 stores files.content_hash as "blake3:<hex>". The gate must normalize before comparing to the
        // bare-hex disk hash, else a byte-identical file would read Stale.
        SetFileHash(fx.DbPath, "orders/OrderService.cs",
            "blake3:" + ContentHasher.Blake3FileHex(diskPath));

        var result = FreshnessGate.Check(fx.DbPath, "orders/OrderService.cs", diskPath,
            JulieDbFixture.OrderServiceContent);

        Assert.Equal(FreshnessResult.Fresh, result.Result);
        Assert.True(result.IndexedContentFound);
    }

    [Fact]
    public void Check_ChangedBytes_IsStale()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        using var workspace = FreshWorkspace();
        SetFileHash(fx.DbPath, "orders/OrderService.cs", "blake3:" + ContentHasher.Blake3Hex(Encoding.UTF8.GetBytes(JulieDbFixture.OrderServiceContent)));
        string mutated = JulieDbFixture.OrderServiceContent.Replace("Total", "Sum", StringComparison.Ordinal);
        string diskPath = WriteFile(workspace, "orders/OrderService.cs", Encoding.UTF8.GetBytes(mutated));

        var result = FreshnessGate.Check(fx.DbPath, "orders/OrderService.cs", diskPath, mutated);

        Assert.Equal(FreshnessResult.Stale, result.Result);
        Assert.True(result.IndexedContentFound);
    }

    [Fact]
    public void Check_SameDecodedTextWithDifferentBytes_IsStale()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        using var workspace = FreshWorkspace();
        byte[] indexedBytesWithBom =
        [
            0xEF, 0xBB, 0xBF,
            .. Encoding.UTF8.GetBytes(JulieDbFixture.OrderServiceContent),
        ];
        SetFileHash(fx.DbPath, "orders/OrderService.cs", "blake3:" + ContentHasher.Blake3Hex(indexedBytesWithBom));
        string diskPath = WriteFile(workspace, "orders/OrderService.cs", Encoding.UTF8.GetBytes(JulieDbFixture.OrderServiceContent));

        var result = FreshnessGate.Check(
            fx.DbPath,
            "orders/OrderService.cs",
            diskPath,
            JulieDbFixture.OrderServiceContent);

        Assert.Equal(FreshnessResult.Stale, result.Result);
        Assert.True(result.IndexedContentFound);
    }

    [Fact]
    public void Check_MissingIndexedContent_IsStale_WhenNotAllowingStale()
    {
        using var fx = JulieDbFixture.CreateForEdit();

        // A file julie never indexed has no snapshot to compare → can't verify → Stale.
        var result = FreshnessGate.Check(fx.DbPath, "ghost/Unknown.cs", "anything on disk");

        Assert.Equal(FreshnessResult.Stale, result.Result);
        Assert.False(result.IndexedContentFound);
    }

    // The gate reports the verdict; the TOOL decides whether allow_stale overrides it. But the gate exposes
    // IndexedContentFound so the tool can craft the right message ("no indexed snapshot" vs "changed on disk").
    [Fact]
    public void Check_MissingIndexedContent_ReportsNotFound_SoToolCanDistinguish()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        using var workspace = FreshWorkspace();
        string diskPath = WriteFile(workspace, "orders/OrderService.cs", Encoding.UTF8.GetBytes(JulieDbFixture.OrderServiceContent));
        SetFileHash(fx.DbPath, "orders/OrderService.cs", "blake3:" + ContentHasher.Blake3FileHex(diskPath));

        var found = FreshnessGate.Check(fx.DbPath, "orders/OrderService.cs", diskPath, JulieDbFixture.OrderServiceContent);
        var missing = FreshnessGate.Check(fx.DbPath, "ghost/Unknown.cs", "x");

        Assert.True(found.IndexedContentFound);
        Assert.False(missing.IndexedContentFound);
    }

    [Fact]
    public void Check_MissingHashAlgorithm_IsStaleEvenWhenBytesMatch()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        using var workspace = FreshWorkspace();
        string diskPath = WriteFile(workspace, "orders/OrderService.cs", Encoding.UTF8.GetBytes(JulieDbFixture.OrderServiceContent));
        SetFileHash(fx.DbPath, "orders/OrderService.cs", "blake3:" + ContentHasher.Blake3FileHex(diskPath));
        SetHashAlgorithm(fx.DbPath, null);

        var result = FreshnessGate.Check(
            fx.DbPath,
            "orders/OrderService.cs",
            diskPath,
            JulieDbFixture.OrderServiceContent);

        Assert.Equal(FreshnessResult.Stale, result.Result);
        Assert.False(result.IndexedContentFound);
    }

    [Fact]
    public void Check_WrongHashAlgorithm_IsStaleEvenWhenBytesMatch()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        using var workspace = FreshWorkspace();
        string diskPath = WriteFile(workspace, "orders/OrderService.cs", Encoding.UTF8.GetBytes(JulieDbFixture.OrderServiceContent));
        SetFileHash(fx.DbPath, "orders/OrderService.cs", "blake3:" + ContentHasher.Blake3FileHex(diskPath));
        SetHashAlgorithm(fx.DbPath, "sha256");

        var result = FreshnessGate.Check(
            fx.DbPath,
            "orders/OrderService.cs",
            diskPath,
            JulieDbFixture.OrderServiceContent);

        Assert.Equal(FreshnessResult.Stale, result.Result);
        Assert.False(result.IndexedContentFound);
    }

    [Fact]
    public void Check_EmptyFileHash_IsStaleEvenWhenBytesMatch()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        using var workspace = FreshWorkspace();
        string diskPath = WriteFile(workspace, "orders/OrderService.cs", Encoding.UTF8.GetBytes(JulieDbFixture.OrderServiceContent));
        SetFileHash(fx.DbPath, "orders/OrderService.cs", "");

        var result = FreshnessGate.Check(
            fx.DbPath,
            "orders/OrderService.cs",
            diskPath,
            JulieDbFixture.OrderServiceContent);

        Assert.Equal(FreshnessResult.Stale, result.Result);
        Assert.False(result.IndexedContentFound);
    }

    [Fact]
    public void Check_NullDbPath_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => FreshnessGate.Check(null!, "f.cs", "x"));
    }

    [Fact]
    public void Check_NullDiskText_Throws()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        Assert.Throws<ArgumentNullException>(
            () => FreshnessGate.Check(fx.DbPath, "orders/OrderService.cs", null!));
    }

    private static TempDir FreshWorkspace() =>
        new(Path.Combine(Path.GetTempPath(), "miller-freshness-gate-" + Guid.NewGuid().ToString("N")));

    private static string WriteFile(TempDir workspace, string relativePath, byte[] bytes)
    {
        string path = Path.Combine(workspace.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // Store the value verbatim into the v1 files.content_hash column. Callers store the "blake3:<hex>" form to
    // mirror what julie writes — the gate normalizes the prefix before comparing (D5).
    private static void SetFileHash(string dbPath, string relativePath, string hash)
    {
        using var conn = OpenReadWrite(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE files SET content_hash = $hash WHERE path = $path;";
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$path", relativePath);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    private static void SetHashAlgorithm(string dbPath, string? hashAlgorithm)
    {
        using var conn = OpenReadWrite(dbPath);
        if (hashAlgorithm is null)
        {
            using var delete = conn.CreateCommand();
            delete.CommandText = "DELETE FROM artifact_metadata WHERE key = 'hash_algorithm';";
            delete.ExecuteNonQuery();
            return;
        }

        using var upsert = conn.CreateCommand();
        upsert.CommandText = """
            INSERT INTO artifact_metadata (key, value)
            VALUES ('hash_algorithm', $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        upsert.Parameters.AddWithValue("$value", hashAlgorithm);
        upsert.ExecuteNonQuery();
    }

    private static SqliteConnection OpenReadWrite(string dbPath)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        };
        var conn = new SqliteConnection(csb.ToString());
        conn.Open();
        return conn;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir(string path)
        {
            Path = path;
            Directory.CreateDirectory(path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
