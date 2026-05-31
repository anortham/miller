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
    private static readonly string PinnedVer = MillerExtractContract.PinnedJulieServerVersion;

    private static string S(long v) => v.ToString(CultureInfo.InvariantCulture);

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
    public void Verify_OlderSchema_ThrowsPointingAtRestore()
    {
        using var fx = JulieDbFixture.Create(PinSchema - 1, PinContractStr, NoRows);
        using var conn = OpenReadOnly(fx.DbPath);

        var ex = Assert.Throws<IncompatibleExtractException>(() => JulieSchemaGate.Verify(conn));
        Assert.Contains(S(PinSchema - 1), ex.Message);
        Assert.Contains($"v{PinnedVer}", ex.Message);      // older path names the pinned julie-server
        Assert.Contains("restore", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_MissingSchemaVersionTable_ThrowsNamingTheTable()
    {
        // A non-julie / corrupt DB: no schema_version table at all. COALESCE(MAX(version),0) can't even
        // run, so the gate must detect the missing table and reject (fail-fast on non-julie DBs).
        using var fx = JulieDbFixture.Create(null, PinContractStr, NoRows, createSchemaVersionTable: false);
        using var conn = OpenReadOnly(fx.DbPath);

        var ex = Assert.Throws<IncompatibleExtractException>(() => JulieSchemaGate.Verify(conn));
        Assert.Contains("schema_version", ex.Message);
        Assert.Contains($"not a v{PinnedVer} julie extract", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_MissingMetadataTable_ThrowsNamingTheTable()
    {
        // Schema is at the pin (so the schema check passes); the metadata table is absent, so the gate must
        // fail on the missing table next.
        using var fx = JulieDbFixture.Create(PinSchema, null, NoRows, createMetadataTable: false);
        using var conn = OpenReadOnly(fx.DbPath);

        var ex = Assert.Throws<IncompatibleExtractException>(() => JulieSchemaGate.Verify(conn));
        Assert.Contains("external_extract_metadata", ex.Message);
    }

    [Fact]
    public void Verify_MissingContractKey_ThrowsNamingTheKey()
    {
        // The metadata table exists but the extract_contract_version row is absent (contractValue null
        // with the table present). Schema is at the pin so we reach the contract check; the SELECT returns
        // no row → treated as incompatible (older/missing).
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
}
