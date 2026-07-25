using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Miller.Server.Telemetry;
using Xunit;

namespace Miller.Tests.Server;

public sealed class TelemetryOnboardingReaderTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public TelemetryOnboardingReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-onboarding-telemetry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "telemetry.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Read_MissingDb_ReturnsUnavailableFacts()
    {
        TelemetryOnboardingFacts facts = TelemetryOnboardingReader.Read(_dbPath, "ws-a");

        Assert.False(facts.Available);
        Assert.Equal("missing_telemetry_db", facts.State);
        Assert.Equal(0, facts.TotalCalls);
        Assert.Empty(facts.ToolMix);
    }

    [Fact]
    public void Read_ScopesWorkspaceAndExtractsSignalsFromRecentRows()
    {
        using (TelemetryLedger.Open(_dbPath, workspaceId: "ws-a"))
        {
            // Create the STRICT table, then insert deterministic timestamps/durations.
        }

        string getUserHash = Sha256Hex("GetUser");
        Insert("2026-06-23T10:00:00.000Z", "search", "auto", "ws-a", "ok", 100, 3, 600, 90, getUserHash);
        Insert("2026-06-23T10:00:20.000Z", "inspect", "summary", "ws-a", "ok", 40, 1, 300, 45, getUserHash);
        Insert("2026-06-23T10:02:00.000Z", "search", "auto", "ws-a", "empty", 35, 0, 120, 20, null,
            """{"empty_reason":"no_symbol_hits"}""");
        Insert("2026-06-23T10:04:00.000Z", "context", null, "ws-a", "ok", 900, 5, 3000, 700, null);

        Insert("2026-06-23T10:05:00.000Z", "search", "auto", "other-ws", "ok", 20, 9, 999, 99, Sha256Hex("Other"));
        Insert("2026-05-01T10:05:00.000Z", "search", "auto", "ws-a", "ok", 20, 9, 999, 99, Sha256Hex("Old"));

        TelemetryOnboardingFacts facts = TelemetryOnboardingReader.Read(_dbPath, "ws-a", windowDays: 30);

        Assert.True(facts.Available);
        Assert.Equal("ready", facts.State);
        Assert.Equal(4, facts.TotalCalls);
        Assert.Equal("2026-06-23T10:00:00.000Z", facts.WindowStartTs);
        Assert.Equal("2026-06-23T10:04:00.000Z", facts.WindowEndTs);
        Assert.Contains(facts.ToolMix, row => row.Tool == "search" && row.Op == "auto" && row.Calls == 2);
        Assert.Contains(facts.SuccessfulFlows, row => row.From == "search:auto" && row.To == "inspect:summary" && row.Calls == 1);
        Assert.Contains(facts.TargetHashes, row => row.TargetHash == getUserHash && row.Calls == 2);
        Assert.Contains(facts.CommonMisses, row => row.Tool == "search" && row.Reason == "no_symbol_hits" && row.Calls == 1);
        Assert.Contains(facts.Friction, row => row.Tool == "context" && row.Calls == 1 && row.AvgMs >= 900);
        Assert.Equal(3, facts.ToolMixTotal);
        Assert.Equal(1, facts.SuccessfulFlowsTotal);
        Assert.Equal(1, facts.TargetHashesTotal);
        Assert.Equal(1, facts.CommonMissesTotal);
        Assert.Equal(3, facts.FrictionTotal);
    }

    [Fact]
    public void Read_LimitBoundsRowsAndPreservesExactSectionTotals()
    {
        using (TelemetryLedger.Open(_dbPath, workspaceId: "ws-a"))
        {
        }

        for (int i = 0; i < 5; i++)
        {
            Insert(
                $"2026-06-23T10:00:{i * 10:D2}.000Z",
                $"tool-{i}",
                $"op-{i}",
                "ws-a",
                "ok",
                10 + i,
                1,
                100 + i,
                20 + i,
                Sha256Hex($"target-{i}"));
        }

        TelemetryOnboardingFacts facts =
            TelemetryOnboardingReader.Read(_dbPath, "ws-a", windowDays: 30, limit: 2);

        Assert.Equal(2, facts.ToolMix.Count);
        Assert.Equal(5, facts.ToolMixTotal);
        Assert.Equal(2, facts.SuccessfulFlows.Count);
        Assert.Equal(4, facts.SuccessfulFlowsTotal);
        Assert.Equal(2, facts.TargetHashes.Count);
        Assert.Equal(5, facts.TargetHashesTotal);
        Assert.Equal(2, facts.Friction.Count);
        Assert.Equal(5, facts.FrictionTotal);
    }

    [Fact]
    public void Read_InvalidOrNonTextMetadataFallsBackToOutcomeReason()
    {
        using (TelemetryLedger.Open(_dbPath, workspaceId: "ws-a"))
        {
        }
        Insert(
            "2026-06-23T10:00:00.000Z",
            "search",
            "auto",
            "ws-a",
            "empty",
            10,
            0,
            10,
            2,
            null,
            "{not-json");
        Insert(
            "2026-06-23T10:00:01.000Z",
            "search",
            "auto",
            "ws-a",
            "empty",
            11,
            0,
            10,
            2,
            null,
            """{"empty_reason":123}""");

        TelemetryOnboardingFacts facts = TelemetryOnboardingReader.Read(_dbPath, "ws-a");

        Assert.True(facts.Available);
        TelemetryMiss miss = Assert.Single(facts.CommonMisses);
        Assert.Equal("empty", miss.Reason);
        Assert.Equal(2, miss.Calls);
        Assert.Equal(1, facts.CommonMissesTotal);
    }

    private void Insert(
        string ts,
        string tool,
        string? op,
        string? workspaceId,
        string outcome,
        long durationMs,
        long? resultCount,
        long bytesReturned,
        long estTokens,
        string? targetHash,
        string metadataJson = "{}")
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tool_telemetry
                (id, ts, tool, op, workspace_id, duration_ms, outcome, result_count,
                 bytes_examined, bytes_returned, source_bytes, est_tokens, index_fresh,
                 target_hash, metadata_json)
            VALUES
                ($id, $ts, $tool, $op, $ws, $dur, $outcome, $rc,
                 0, $bytes, 0, $tokens, 1, $hash, $meta);
            """;
        command.Parameters.AddWithValue("$id", Guid.CreateVersion7().ToString());
        command.Parameters.AddWithValue("$ts", ts);
        command.Parameters.AddWithValue("$tool", tool);
        command.Parameters.AddWithValue("$op", (object?)op ?? DBNull.Value);
        command.Parameters.AddWithValue("$ws", (object?)workspaceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$dur", durationMs);
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$rc", (object?)resultCount ?? DBNull.Value);
        command.Parameters.AddWithValue("$bytes", bytesReturned);
        command.Parameters.AddWithValue("$tokens", estTokens);
        command.Parameters.AddWithValue("$hash", (object?)targetHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$meta", metadataJson);
        command.ExecuteNonQuery();
    }

    private static string Sha256Hex(string raw) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
