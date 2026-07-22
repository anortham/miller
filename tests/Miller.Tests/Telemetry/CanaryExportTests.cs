using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Miller.Server.Telemetry;
using Xunit;

namespace Miller.Tests.Telemetry;

public sealed class CanaryExportTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "miller-canary-export-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _generatedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    public CanaryExportTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
    }

    private string Db => Path.Combine(_temp, "telemetry.db");

    [Fact]
    public void Envelope_CarriesTheFrozenTopLevelShape()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            for (int i = 0; i < 6; i++)
                seeder.InsertCanary("ws-hex-a", "2026-07-14", "prose", "treatment", bucket: 73);
        }

        JsonElement root = ParseExport(from: "2026-07-02", to: "2026-08-01");

        Assert.Equal(2, root.GetProperty("schema_version").GetInt32());
        Assert.Equal(1, root.GetProperty("canary_contract_version").GetInt32());
        Assert.Equal("semantic_hybrid_search_v1", root.GetProperty("experiment_id").GetString());
        Assert.Equal("2026-08-01T12:00:00Z", root.GetProperty("generated_at_utc").GetString());
        Assert.Equal("2026-07-02", root.GetProperty("window").GetProperty("from_utc").GetString());
        Assert.Equal("2026-08-01", root.GetProperty("window").GetProperty("to_utc").GetString());
        Assert.Equal(0, root.GetProperty("suppressed_unit_count").GetInt32());
        Assert.Equal(1, root.GetProperty("units").GetArrayLength());
        Assert.Equal(0, root.GetProperty("shadow_units").GetArrayLength());
    }

    [Fact]
    public void ExperimentUnit_CarriesTheFrozenFieldSetAndCounters()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            for (int i = 0; i < 5; i++)
                seeder.InsertCanary(
                    "ws-hex-a", "2026-07-14", "prose", "treatment",
                    bucket: 73, embedWarmth: "warm", durationMs: 120,
                    encoderFingerprint: "3f9a1c22b0e4d781", storageSchema: "vec0-int8-256-cosine-v1",
                    corpusGeneration: "cards-v1-chunks-v1", fusionProfile: "rrf-mixed-v1",
                    semanticContribution: 2);
        }

        JsonElement unit = ParseExport("2026-07-02", "2026-08-01").GetProperty("units")[0];

        Assert.Equal("2026-07-14", unit.GetProperty("utc_date").GetString());
        Assert.Equal("prose", unit.GetProperty("query_class").GetString());
        Assert.Equal("treatment", unit.GetProperty("arm").GetString());
        Assert.Equal(73, unit.GetProperty("bucket").GetInt32());
        Assert.Equal(5, unit.GetProperty("calls").GetInt32());
        Assert.Equal(5, unit.GetProperty("ok_calls").GetInt32());
        Assert.Equal(0, unit.GetProperty("error_calls").GetInt32());
        Assert.Equal(5, unit.GetProperty("semantic_contribution_calls").GetInt32());
        Assert.Equal("3f9a1c22b0e4d781", unit.GetProperty("encoder_fingerprint").GetString());
        Assert.Equal("rrf-mixed-v1", unit.GetProperty("fusion_profile").GetString());
        Assert.Equal("1.14.0+abc1234", unit.GetProperty("miller_version").GetString());
        Assert.Equal(5, unit.GetProperty("embed_warmth_counts").GetProperty("warm").GetInt32());
        Assert.Equal(5, unit.GetProperty("total_latency_bucket_counts").GetProperty("lt_250").GetInt32());
    }

    [Fact]
    public void UnitId_IsTheFirstTwelveHexOfTheExactStratumDigest()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            for (int i = 0; i < 5; i++)
                seeder.InsertCanary("ws-hex-a", "2026-07-14", "prose", "treatment");
        }

        string material = string.Concat(
            UnitIdPart("semantic_hybrid_search_v1"),
            UnitIdPart("1"),
            UnitIdPart("ws-hex-a"),
            UnitIdPart("2026-07-14"),
            UnitIdPart("prose"),
            UnitIdPart("treatment"),
            UnitIdPart("0"),
            UnitIdPart("1.14.0+abc1234"),
            UnitIdPart(null),
            UnitIdPart(null),
            UnitIdPart(null),
            UnitIdPart(null),
            UnitIdPart("1"));
        string expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..12];

        JsonElement unit = ParseExport("2026-07-02", "2026-08-01").GetProperty("units")[0];
        Assert.Equal(expected, unit.GetProperty("unit_id").GetString());
    }

    [Fact]
    public void AnalysisUnits_NeverPoolExactVersionsOrSemanticIdentityStrata()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            for (int i = 0; i < 5; i++)
            {
                seeder.InsertCanary(
                    "ws-hex-a", "2026-07-14", "prose", "treatment",
                    millerVersion: "1.14.0+aaa", encoderFingerprint: "encoder-a",
                    storageSchema: "vec0-int8-384-cosine-v1", corpusGeneration: "cards-v1",
                    fusionProfile: "rrf-a");
                seeder.InsertCanary(
                    "ws-hex-a", "2026-07-14", "prose", "treatment",
                    millerVersion: "1.14.0+bbb", encoderFingerprint: "encoder-b",
                    storageSchema: "vec0-f32-512-cosine-v1", corpusGeneration: "cards-v2",
                    fusionProfile: "rrf-b", policyVersion: 2);
            }
        }

        JsonElement[] units = ParseExport("2026-07-02", "2026-08-01")
            .GetProperty("units").EnumerateArray().ToArray();

        Assert.Equal(2, units.Length);
        Assert.Equal(2, units.Select(unit => unit.GetProperty("unit_id").GetString()).Distinct().Count());
        Assert.Contains(units, unit =>
            unit.GetProperty("miller_version").GetString() == "1.14.0+aaa"
            && unit.GetProperty("encoder_fingerprint").GetString() == "encoder-a"
            && unit.GetProperty("storage_schema").GetString() == "vec0-int8-384-cosine-v1"
            && unit.GetProperty("corpus_generation").GetString() == "cards-v1"
            && unit.GetProperty("fusion_profile").GetString() == "rrf-a"
            && unit.GetProperty("policy_version").GetInt32() == 1);
        Assert.Contains(units, unit =>
            unit.GetProperty("miller_version").GetString() == "1.14.0+bbb"
            && unit.GetProperty("encoder_fingerprint").GetString() == "encoder-b"
            && unit.GetProperty("storage_schema").GetString() == "vec0-f32-512-cosine-v1"
            && unit.GetProperty("corpus_generation").GetString() == "cards-v2"
            && unit.GetProperty("fusion_profile").GetString() == "rrf-b"
            && unit.GetProperty("policy_version").GetInt32() == 2);
    }

    [Fact]
    public void UnknownIdentity_IsAnExplicitNullStratumAndNeverBorrowsAnotherRowsIdentity()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            for (int i = 0; i < 5; i++)
                seeder.InsertCanary("ws-hex-a", "2026-07-14", "prose", "control", millerVersion: null, policyVersion: null);
            for (int i = 0; i < 5; i++)
                seeder.InsertCanary(
                    "ws-hex-a", "2026-07-14", "prose", "control",
                    encoderFingerprint: "encoder-known", storageSchema: "vec0-int8-384-cosine-v1");
        }

        JsonElement[] units = ParseExport("2026-07-02", "2026-08-01")
            .GetProperty("units").EnumerateArray().ToArray();

        Assert.Equal(2, units.Length);
        JsonElement unknown = Assert.Single(
            units, unit => unit.GetProperty("miller_version").ValueKind == JsonValueKind.Null);
        Assert.Equal(JsonValueKind.Null, unknown.GetProperty("encoder_fingerprint").ValueKind);
        Assert.Equal(JsonValueKind.Null, unknown.GetProperty("storage_schema").ValueKind);
        Assert.Equal(JsonValueKind.Null, unknown.GetProperty("corpus_generation").ValueKind);
        Assert.Equal(JsonValueKind.Null, unknown.GetProperty("fusion_profile").ValueKind);
        Assert.Equal(JsonValueKind.Null, unknown.GetProperty("policy_version").ValueKind);
    }

    [Fact]
    public void TotalLatencyBucketCounts_SumToCallsAndOmitZeroKeys()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            seeder.InsertCanary("ws-hex-a", "2026-07-14", "prose", "control", durationMs: 5);
            seeder.InsertCanary("ws-hex-a", "2026-07-14", "prose", "control", durationMs: 5);
            seeder.InsertCanary("ws-hex-a", "2026-07-14", "prose", "control", durationMs: 60);
            seeder.InsertCanary("ws-hex-a", "2026-07-14", "prose", "control", durationMs: 60);
            seeder.InsertCanary("ws-hex-a", "2026-07-14", "prose", "control", durationMs: 60);
        }

        JsonElement counts = ParseExport("2026-07-02", "2026-08-01")
            .GetProperty("units")[0].GetProperty("total_latency_bucket_counts");

        int sum = counts.EnumerateObject().Sum(p => p.Value.GetInt32());
        Assert.Equal(5, sum);
        Assert.Equal(2, counts.GetProperty("lt_10").GetInt32());
        Assert.Equal(3, counts.GetProperty("lt_100").GetInt32());
        Assert.False(counts.TryGetProperty("lt_25", out _));
    }

    [Fact]
    public void AttributedSuccessCalls_CountOnlyAttributedOkRowsWithResults()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            for (int i = 0; i < 5; i++)
            {
                string hash = CanarySeeder.Digest($"Save-{i}");
                seeder.InsertCanary("ws-hex-a", "2026-07-14", "prose", "treatment", nameHashes: [hash]);
                seeder.InsertFollowUp("ws-hex-a", "2026-07-14", hash);
            }
            seeder.InsertCanary("ws-hex-a", "2026-07-14", "prose", "treatment", nameHashes: [CanarySeeder.Digest("Unseen")]);
        }

        JsonElement unit = ParseExport("2026-07-02", "2026-08-01").GetProperty("units")[0];
        Assert.Equal(6, unit.GetProperty("calls").GetInt32());
        Assert.Equal(5, unit.GetProperty("attributed_success_calls").GetInt32());
    }

    [Fact]
    public void UnitWithFewerThanFiveCalls_IsSuppressedAndCounted()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            for (int i = 0; i < 4; i++)
                seeder.InsertCanary("ws-hex-a", "2026-07-14", "prose", "treatment");
        }

        JsonElement root = ParseExport("2026-07-02", "2026-08-01");
        Assert.Equal(1, root.GetProperty("suppressed_unit_count").GetInt32());
        Assert.Equal(0, root.GetProperty("units").GetArrayLength());
    }

    [Fact]
    public void Export_NeverCarriesHashesWorkspaceIdsOrRawMilliseconds()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            for (int i = 0; i < 5; i++)
                seeder.InsertCanary(
                    "ws-secret-hex", "2026-07-14", "prose", "treatment", durationMs: 137,
                    nameHashes: [CanarySeeder.Digest("Save")], pathHashes: [CanarySeeder.Digest("src/x.cs")]);
        }

        string json = CanaryExport.BuildJson(Db, new DateOnly(2026, 7, 2), new DateOnly(2026, 8, 1), _generatedAt);

        Assert.DoesNotContain("ws-secret-hex", json, StringComparison.Ordinal);
        Assert.DoesNotContain(CanarySeeder.Digest("Save"), json, StringComparison.Ordinal);
        Assert.DoesNotContain("workspace_id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("target_hash", json, StringComparison.Ordinal);

        var numbers = new List<long>();
        CollectNumbers(JsonDocument.Parse(json).RootElement, numbers);
        Assert.DoesNotContain(137L, numbers);
    }

    private static void CollectNumbers(JsonElement element, List<long> numbers)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number when element.TryGetInt64(out long value):
                numbers.Add(value);
                break;
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                    CollectNumbers(property.Value, numbers);
                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                    CollectNumbers(item, numbers);
                break;
        }
    }

    [Fact]
    public void ReExportOfAnUnchangedWindow_IsByteIdentical()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            for (int i = 0; i < 7; i++)
                seeder.InsertCanary("ws-hex-a", "2026-07-14", "prose", "treatment");
            for (int i = 0; i < 6; i++)
                seeder.InsertCanary("ws-hex-b", "2026-07-13", "mixed", "control");
        }

        string first = CanaryExport.BuildJson(Db, new DateOnly(2026, 7, 2), new DateOnly(2026, 8, 1), _generatedAt);
        string second = CanaryExport.BuildJson(Db, new DateOnly(2026, 7, 2), new DateOnly(2026, 8, 1), _generatedAt);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Units_AreOrderedByDateThenClassThenUnitId()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            for (int i = 0; i < 5; i++)
                seeder.InsertCanary("ws-hex-a", "2026-07-15", "mixed", "treatment");
            for (int i = 0; i < 5; i++)
                seeder.InsertCanary("ws-hex-b", "2026-07-14", "prose", "control");
        }

        JsonElement units = ParseExport("2026-07-02", "2026-08-01").GetProperty("units");
        Assert.Equal("2026-07-14", units[0].GetProperty("utc_date").GetString());
        Assert.Equal("2026-07-15", units[1].GetProperty("utc_date").GetString());
    }

    [Fact]
    public void WindowFiltering_ExcludesRowsOutsideTheRequestedDates()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            for (int i = 0; i < 5; i++)
                seeder.InsertCanary("ws-hex-a", "2026-06-01", "prose", "treatment");
            for (int i = 0; i < 5; i++)
                seeder.InsertCanary("ws-hex-a", "2026-07-14", "prose", "treatment");
        }

        JsonElement units = ParseExport("2026-07-02", "2026-08-01").GetProperty("units");
        Assert.Equal(1, units.GetArrayLength());
        Assert.Equal("2026-07-14", units[0].GetProperty("utc_date").GetString());
    }

    [Fact]
    public void ShadowUnit_CarriesTheFrozenShadowShape()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            for (int i = 0; i < 5; i++)
                seeder.InsertShadow("ws-hex-a", "2026-07-14", "ok", overlapAt10: 9, top1Changed: i == 0, lexicalTop1Rank: 1);
        }

        JsonElement shadow = ParseExport("2026-07-02", "2026-08-01").GetProperty("shadow_units")[0];
        Assert.Equal("identifier", shadow.GetProperty("query_class").GetString());
        Assert.Equal(5, shadow.GetProperty("calls").GetInt32());
        Assert.Equal(5, shadow.GetProperty("shadow_status_counts").GetProperty("ok").GetInt32());
        Assert.Equal(1, shadow.GetProperty("top1_changed_calls").GetInt32());
        Assert.Equal(5, shadow.GetProperty("overlap_at_10_histogram").GetProperty("9").GetInt32());
    }

    [Fact]
    public void PoisonedCountMapLabels_AreExcludedWhileValidKeysSurvive()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            for (int i = 0; i < 5; i++)
                seeder.InsertCanary(
                    "ws-hex-a", "2026-07-14", "prose", "treatment",
                    embedWarmth: "warm", fallbackReason: "../../../etc/passwd");
        }

        string json = CanaryExport.BuildJson(Db, new DateOnly(2026, 7, 2), new DateOnly(2026, 8, 1), _generatedAt);
        JsonElement unit = JsonDocument.Parse(json).RootElement.GetProperty("units")[0];

        Assert.DoesNotContain("etc/passwd", json, StringComparison.Ordinal);
        Assert.Empty(unit.GetProperty("fallback_reason_counts").EnumerateObject());
        Assert.Equal(5, unit.GetProperty("embed_warmth_counts").GetProperty("warm").GetInt32());
    }

    [Fact]
    public void OutOfRangeHistogramKeys_AreExcludedWhileInRangeKeysSurvive()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            for (int i = 0; i < 3; i++)
                seeder.InsertShadow("ws-hex-a", "2026-07-14", "ok", overlapAt10: 9, top1Changed: false, lexicalTop1Rank: 2);
            for (int i = 0; i < 2; i++)
                seeder.InsertShadow("ws-hex-a", "2026-07-14", "ok", overlapAt10: 99, top1Changed: false, lexicalTop1Rank: 999);
        }

        JsonElement shadow = ParseExport("2026-07-02", "2026-08-01").GetProperty("shadow_units")[0];
        JsonElement overlap = shadow.GetProperty("overlap_at_10_histogram");
        JsonElement rank = shadow.GetProperty("lexical_top1_rank_histogram");

        Assert.Equal(3, overlap.GetProperty("9").GetInt32());
        Assert.False(overlap.TryGetProperty("99", out _));
        Assert.Equal(3, rank.GetProperty("2").GetInt32());
        Assert.False(rank.TryGetProperty("999", out _));
    }

    [Fact]
    public void GeneratedAtUtc_DerivedFromWindow_ReExportsByteIdenticalAcrossInvocations()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            for (int i = 0; i < 6; i++)
                seeder.InsertCanary("ws-hex-a", "2026-07-14", "prose", "treatment");
        }

        var from = new DateOnly(2026, 7, 2);
        var to = new DateOnly(2026, 8, 1);

        string first = CanaryExport.BuildJson(Db, from, to, DerivedGeneratedAt(to));
        string second = CanaryExport.BuildJson(Db, from, to, DerivedGeneratedAt(to));

        Assert.Equal(first, second);
        Assert.Equal(
            "2026-08-02T00:00:00Z",
            JsonDocument.Parse(first).RootElement.GetProperty("generated_at_utc").GetString());
    }

    private static DateTimeOffset DerivedGeneratedAt(DateOnly to) =>
        new(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    private static string UnitIdPart(string? value) => value is null ? "-1:" : $"{value.Length}:{value}";

    private JsonElement ParseExport(string from, string to)
    {
        string json = CanaryExport.BuildJson(Db, DateOnly.Parse(from), DateOnly.Parse(to), _generatedAt);
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}

/// <summary>
/// Seeds a temporary machine-global ledger with contract-faithful <c>tool_telemetry</c> rows: real column values
/// and real <c>canary_*</c> metadata keys with real SHA-256 served-result digests. The schema is created by
/// <see cref="TelemetryLedger.Open"/> so the seed exercises the exact table the readers open; rows are inserted
/// with an explicit <c>ts</c>/<c>miller_version</c>/<c>target_hash</c> the production write path cannot set.
/// </summary>
internal sealed class CanarySeeder : IDisposable
{
    private const string Hybrid = "semantic_hybrid_search_v1";
    private const string Identifier = "semantic_identifier_noninferiority_v1";

    private readonly SqliteConnection _connection;
    private int _sequence;

    public CanarySeeder(string dbPath)
    {
        using (TelemetryLedger.Open(dbPath, workspaceId: null))
        {
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _connection.Open();
    }

    public static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public string InsertCanary(
        string workspaceId,
        string utcDate,
        string queryClass,
        string arm,
        string eligibility = "eligible",
        string outcome = "ok",
        int resultCount = 3,
        long durationMs = 5,
        string embedWarmth = "none",
        string? millerVersion = "1.14.0+abc1234",
        int bucket = 0,
        int? semanticContribution = null,
        string? encoderFingerprint = null,
        string? storageSchema = null,
        string? corpusGeneration = null,
        string? fusionProfile = null,
        string embedLatencyBucket = "none",
        string knnLatencyBucket = "none",
        string fallbackReason = "none",
        string backend = "none",
        IReadOnlyList<string>? nameHashes = null,
        IReadOnlyList<string>? pathHashes = null,
        IReadOnlyList<string>? qualifiedHashes = null,
        int contractVersion = 1,
        int? policyVersion = 1,
        string? timeOfDay = null)
    {
        var meta = new JsonObject
        {
            ["canary_contract_version"] = contractVersion,
            ["canary_experiment_id"] = Hybrid,
            ["canary_assignment_version"] = 1,
            ["canary_arm"] = arm,
            ["canary_query_class"] = queryClass,
            ["canary_eligibility"] = eligibility,
        };
        if (policyVersion is { } policy) meta["canary_policy_version"] = policy;

        if (arm is "control" or "treatment")
        {
            meta["canary_bucket"] = bucket;
            meta["canary_lexical_result_count"] = resultCount;
            meta["canary_fallback_reason"] = fallbackReason;
            meta["canary_backend"] = backend;
            meta["canary_embed_warmth"] = embedWarmth;
            meta["canary_embed_latency_bucket"] = embedLatencyBucket;
            meta["canary_knn_latency_bucket"] = knnLatencyBucket;
            if (semanticContribution is { } sc) meta["canary_semantic_contribution_count"] = sc;
            AddOptional(meta, "canary_encoder_fingerprint", encoderFingerprint);
            AddOptional(meta, "canary_storage_schema", storageSchema);
            AddOptional(meta, "canary_corpus_generation", corpusGeneration);
            AddOptional(meta, "canary_fusion_profile", fusionProfile);
            AddHashArray(meta, "canary_result_name_hashes", nameHashes);
            AddHashArray(meta, "canary_result_path_hashes", pathHashes);
            AddHashArray(meta, "canary_result_qualified_hashes", qualifiedHashes);
        }

        return Insert("search", "auto", workspaceId, utcDate, timeOfDay, durationMs, outcome, resultCount, null, millerVersion, meta);
    }

    public string InsertShadow(
        string workspaceId,
        string utcDate,
        string shadowStatus,
        int overlapAt10,
        bool top1Changed,
        int lexicalTop1Rank,
        string? millerVersion = "1.14.0+abc1234",
        string? timeOfDay = null)
    {
        var meta = new JsonObject
        {
            ["canary_contract_version"] = 1,
            ["canary_experiment_id"] = Identifier,
            ["canary_assignment_version"] = 1,
            ["canary_arm"] = "shadow",
            ["canary_query_class"] = "identifier",
            ["canary_eligibility"] = "ineligible_query_class",
            ["canary_policy_version"] = 1,
            ["canary_shadow_status"] = shadowStatus,
        };
        if (shadowStatus == "ok")
        {
            meta["canary_shadow_overlap_at_10"] = overlapAt10;
            meta["canary_shadow_top1_changed"] = top1Changed;
            meta["canary_shadow_lexical_top1_rank"] = lexicalTop1Rank;
        }

        return Insert("search", "auto", workspaceId, utcDate, timeOfDay, 5, "ok", 3, null, millerVersion, meta);
    }

    public string InsertFollowUp(
        string workspaceId,
        string utcDate,
        string targetHash,
        string tool = "inspect",
        string? op = null,
        string outcome = "ok",
        string? timeOfDay = null)
    {
        return Insert(tool, op, workspaceId, utcDate, timeOfDay, 4, outcome, 1, targetHash, "1.14.0+abc1234", meta: null);
    }

    private string Insert(
        string tool, string? op, string workspaceId, string utcDate, string? timeOfDay,
        long durationMs, string outcome, int? resultCount, string? targetHash, string? millerVersion, JsonObject? meta)
    {
        string id = $"row-{_sequence:D6}";
        string ts = $"{utcDate}T{timeOfDay ?? DefaultTime()}Z";
        _sequence++;

        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tool_telemetry
                (id, ts, tool, op, workspace_id, duration_ms, outcome, result_count, target_hash, metadata_json, miller_version)
            VALUES ($id, $ts, $tool, $op, $ws, $dur, $outcome, $rc, $hash, $meta, $version);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$ts", ts);
        cmd.Parameters.AddWithValue("$tool", tool);
        cmd.Parameters.AddWithValue("$op", (object?)op ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ws", workspaceId);
        cmd.Parameters.AddWithValue("$dur", durationMs);
        cmd.Parameters.AddWithValue("$outcome", outcome);
        cmd.Parameters.AddWithValue("$rc", (object?)resultCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hash", (object?)targetHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$meta", meta?.ToJsonString() ?? "{}");
        cmd.Parameters.AddWithValue("$version", (object?)millerVersion ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return id;
    }

    private string DefaultTime()
    {
        int second = _sequence % 60;
        int minute = (_sequence / 60) % 60;
        int hour = (_sequence / 3600) % 24;
        return $"{hour:D2}:{minute:D2}:{second:D2}.000";
    }

    private static void AddOptional(JsonObject meta, string key, string? value)
    {
        if (value is not null)
            meta[key] = value;
    }

    private static void AddHashArray(JsonObject meta, string key, IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
            return;
        var array = new JsonArray();
        foreach (string value in values)
            array.Add(value);
        meta[key] = array;
    }

    public void Dispose() => _connection.Dispose();
}
