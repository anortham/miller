using System.Globalization;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the D5 compatibility gate. The gate runs BEFORE any read and must reject anything that is not the
/// pinned julie extract (schema <see cref="MillerExtractContract.ExpectedSchemaVersion"/> / contract
/// <see cref="MillerExtractContract.ExpectedExtractContractVersion"/>) with a typed, actionable error —
/// silently misreading a future/older schema is the failure mode this prevents. Every failure test asserts
/// the message NAMES the offending value so an operator can act on it.
///
/// Versions here are expressed RELATIVE to the pin (pin, pin±1), sourced from
/// <see cref="MillerExtractContract"/>, so a julie re-pin needs NO edits to this file — only the one
/// constants file changes. (This is why the M4 26/1→28/2 bump did not have to touch these assertions.)
/// </summary>
public sealed class JulieSchemaGateTests
{
    private static readonly IReadOnlyList<JulieDbFixture.SymbolRow> NoRows = Array.Empty<JulieDbFixture.SymbolRow>();

    private static readonly long PinSchema = MillerExtractContract.ExpectedSchemaVersion;
    private static readonly long PinContract = MillerExtractContract.ExpectedExtractContractVersion;
    private static readonly string PinContractStr = S(PinContract);
    private static readonly string PinnedVer = MillerExtractContract.PinnedJulieExtractVersion;

    private static string S(long v) => v.ToString(CultureInfo.InvariantCulture);

    [Fact]
    // Name deliberately carries no version: it was Julie2320 while asserting 2.32.1, and would go
    // stale again on the next pin bump.
    public void Contract_IsSchemaSevenExtractFourAndTheCurrentJuliePin()
    {
        Assert.Equal(7, MillerExtractContract.ExpectedSchemaVersion);
        Assert.Equal(7, MillerExtractContract.ExpectedSqliteSchemaVersion);
        Assert.Equal(4, MillerExtractContract.ExpectedExtractContractVersion);
        Assert.Equal(5, MillerExtractContract.ExpectedJsonlSchemaVersion);
        Assert.Equal("2.34.1", MillerExtractContract.PinnedJulieExtractVersion);
    }

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        var csb = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly };
        var conn = new SqliteConnection(csb.ToString());
        conn.Open();
        return conn;
    }

    [Fact]
    public void Verify_AtPinnedSchemaAndContract_DoesNotThrow()
    {
        using var fx = JulieDbFixture.Create(PinSchema, PinContractStr, NoRows);
        using var conn = OpenReadOnly(fx.DbPath);

        // No exception == compatible. Calling it is the assertion; a throw would fail the test.
        JulieSchemaGate.Verify(conn);
    }

    [Fact]
    public void Verify_SchemaFourContractThree_RequiresFullRebuild()
    {
        using var fx = JulieDbFixture.Create(4, "3", NoRows);
        using var conn = OpenReadOnly(fx.DbPath);

        var ex = Assert.Throws<IncompatibleExtractException>(() => JulieSchemaGate.Verify(conn));
        Assert.Contains("workspace full", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("schema 4", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_NewerSchema_ThrowsNamingTheValueAndPointsAtUpgrade()
    {
        using var fx = JulieDbFixture.Create(PinSchema + 1, PinContractStr, NoRows);
        using var conn = OpenReadOnly(fx.DbPath);

        var ex = Assert.Throws<IncompatibleExtractException>(() => JulieSchemaGate.Verify(conn));
        Assert.Contains(S(PinSchema + 1), ex.Message);     // names the offending schema version
        Assert.Contains(S(PinSchema), ex.Message);         // names what Miller expects
        Assert.Contains("newer", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("upgrade Miller", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_NewerContract_ThrowsNamingTheValueAndPointsAtUpgrade()
    {
        using var fx = JulieDbFixture.Create(PinSchema, S(PinContract + 1), NoRows);
        using var conn = OpenReadOnly(fx.DbPath);

        var ex = Assert.Throws<IncompatibleExtractException>(() => JulieSchemaGate.Verify(conn));
        Assert.Contains(S(PinContract + 1), ex.Message);   // offending contract value
        Assert.Contains(PinContractStr, ex.Message);       // expected contract
        Assert.Contains("newer", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_OlderContract_ThrowsNamingTheValueAndPointsAtRebuild()
    {
        using var fx = JulieDbFixture.Create(PinSchema, S(PinContract - 1), NoRows);
        using var conn = OpenReadOnly(fx.DbPath);

        var ex = Assert.Throws<IncompatibleExtractException>(() => JulieSchemaGate.Verify(conn));
        Assert.Contains(S(PinContract - 1), ex.Message);
        Assert.Contains(PinContractStr, ex.Message);
        Assert.Contains("workspace full", ex.Message, StringComparison.OrdinalIgnoreCase); // force-rebuild remedy
        Assert.Contains("restore", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_OlderSchema_ThrowsPointingAtRebuild()
    {
        using var fx = JulieDbFixture.Create(PinSchema - 1, PinContractStr, NoRows);
        using var conn = OpenReadOnly(fx.DbPath);

        var ex = Assert.Throws<IncompatibleExtractException>(() => JulieSchemaGate.Verify(conn));
        Assert.Contains(S(PinSchema - 1), ex.Message);
        Assert.Contains($"v{PinnedVer}", ex.Message);      // error message names the pinned julie-extract version
        Assert.Contains("workspace full", ex.Message, StringComparison.OrdinalIgnoreCase); // force-rebuild remedy
        Assert.Contains("restore", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_MissingMetadataTable_ThrowsNamingArtifactMetadata()
    {
        // A non-julie / corrupt DB: no artifact_metadata table at all (the only metadata surface). The
        // gate hits it on the FIRST metadata read (sqlite_schema_version) and rejects (fail-fast on non-julie DBs).
        using var fx = JulieDbFixture.Create(PinSchema, null, NoRows, createMetadataTable: false);
        using var conn = OpenReadOnly(fx.DbPath);

        var ex = Assert.Throws<IncompatibleExtractException>(() => JulieSchemaGate.Verify(conn));
        Assert.Contains("artifact_metadata", ex.Message);
        Assert.Contains("not a compatible julie-extract artifact", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_MissingSqliteSchemaVersionKey_ThrowsNamingTheKey()
    {
        // Table present, but the sqlite_schema_version row absent (older/corrupt artifact).
        using var fx = JulieDbFixture.Create(schemaVersion: null, PinContractStr, NoRows, createMetadataTable: true);
        using var conn = OpenReadOnly(fx.DbPath);

        var ex = Assert.Throws<IncompatibleExtractException>(() => JulieSchemaGate.Verify(conn));
        Assert.Contains("sqlite_schema_version", ex.Message);
    }

    [Fact]
    public void Verify_MissingContractKey_ThrowsNamingTheKey()
    {
        // The metadata table exists and the schema key is present (so we reach the contract check), but the
        // extract_contract_version row is absent (contractValue null with the table present) → incompatible.
        using var fx = JulieDbFixture.Create(PinSchema, null, NoRows, createMetadataTable: true);
        using var conn = OpenReadOnly(fx.DbPath);

        var ex = Assert.Throws<IncompatibleExtractException>(() => JulieSchemaGate.Verify(conn));
        Assert.Contains("extract_contract_version", ex.Message);
    }

    [Fact]
    public void Verify_NonIntegerContractValue_ThrowsNamingTheValue()
    {
        // Metadata values are TEXT; a present-but-garbage value (not parseable as an integer) must be
        // rejected by the gate's long.TryParse branch, naming the offending value so the operator can act.
        using var fx = JulieDbFixture.Create(PinSchema, "abc", NoRows);
        using var conn = OpenReadOnly(fx.DbPath);

        var ex = Assert.Throws<IncompatibleExtractException>(() => JulieSchemaGate.Verify(conn));
        Assert.Contains("abc", ex.Message);              // names the offending value
        Assert.Contains("non-integer", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_MissingHashAlgorithmKey_ThrowsNamingTheKey()
    {
        using var fx = JulieDbFixture.Create(PinSchema, PinContractStr, NoRows, hashAlgorithm: null);
        using var conn = OpenReadOnly(fx.DbPath);

        var ex = Assert.Throws<IncompatibleExtractException>(() => JulieSchemaGate.Verify(conn));
        Assert.Contains("hash_algorithm", ex.Message);
        Assert.Contains("blake3", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("reference_sites")]
    [InlineData("identifiers")]
    [InlineData("relationships")]
    [InlineData("pending_relationships")]
    [InlineData("structural_facts")]
    [InlineData("language_capability_gaps")]
    public void Verify_MissingRequiredSchemaFiveTable_ThrowsNamingTheTable(string tableName)
    {
        using var fx = JulieDbFixture.Create(PinSchema, PinContractStr, NoRows);
        using (var writer = new SqliteConnection($"Data Source={fx.DbPath}"))
        {
            writer.Open();
            using var drop = writer.CreateCommand();
            drop.CommandText = $"DROP TABLE {tableName};";
            drop.ExecuteNonQuery();
        }

        using var conn = OpenReadOnly(fx.DbPath);
        var ex = Assert.Throws<IncompatibleExtractException>(() => JulieSchemaGate.Verify(conn));

        Assert.Contains(tableName, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_WrongHashAlgorithm_ThrowsNamingTheValueAndExpectedAlgorithm()
    {
        using var fx = JulieDbFixture.Create(PinSchema, PinContractStr, NoRows, hashAlgorithm: "sha256");
        using var conn = OpenReadOnly(fx.DbPath);

        var ex = Assert.Throws<IncompatibleExtractException>(() => JulieSchemaGate.Verify(conn));
        Assert.Contains("sha256", ex.Message);
        Assert.Contains("blake3", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hash_algorithm", ex.Message);
    }
}
