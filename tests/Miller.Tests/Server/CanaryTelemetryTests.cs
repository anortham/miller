using System.Text.Json;
using Miller.Server.Telemetry;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the canary-telemetry-v1 write path: verbatim field names, complete enums, the frozen assignment
/// derivation, the served-result hash arrays, and the privacy rule that persisted telemetry carries no query
/// text. P2 ships the plumbing only — the arm is the constant <c>control</c> until P5 activates the experiment.
/// </summary>
public sealed class CanaryTelemetryTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "miller-canary-" + Guid.NewGuid());

    public CanaryTelemetryTests() => Directory.CreateDirectory(_temp);

    [Fact]
    public void Activation_DefaultsToOffAndAliasesZeroAndOne()
    {
        Assert.Equal(CanaryMode.Off, CanaryActivation.Parse(null));
        Assert.Equal(CanaryMode.Off, CanaryActivation.Parse("off"));
        Assert.Equal(CanaryMode.Off, CanaryActivation.Parse("0"));
        Assert.Equal(CanaryMode.On, CanaryActivation.Parse("on"));
        Assert.Equal(CanaryMode.On, CanaryActivation.Parse("1"));
        Assert.Equal(CanaryMode.Off, CanaryActivation.Parse("wat"));
    }

    [Fact]
    public void Off_WritesNoCanaryKeyAtAll()
    {
        using TelemetryLedger ledger = OpenLedger();
        using TelemetryScope scope = ledger.Measure("search", "auto");

        CanaryTelemetry.Stamp(scope, CanaryMode.Off, Call());

        Assert.Equal("{}", scope.MetadataJson);
    }

    [Fact]
    public void On_WritesTheAlwaysPresentFieldsWithAConstantControlArm()
    {
        JsonElement metadata = Stamp(Call());

        Assert.Equal(1, metadata.GetProperty("canary_contract_version").GetInt32());
        Assert.Equal("semantic_hybrid_search_v1", metadata.GetProperty("canary_experiment_id").GetString());
        Assert.Equal(1, metadata.GetProperty("canary_assignment_version").GetInt32());
        Assert.Equal("control", metadata.GetProperty("canary_arm").GetString());
        Assert.Equal("prose", metadata.GetProperty("canary_query_class").GetString());
        Assert.Equal("eligible", metadata.GetProperty("canary_eligibility").GetString());
        Assert.Equal(1, metadata.GetProperty("canary_policy_version").GetInt32());
        Assert.InRange(metadata.GetProperty("canary_bucket").GetInt32(), 0, 99);
    }

    [Fact]
    public void On_EligibleRowCarriesTheCounterAndEnumFieldsBothArmsShare()
    {
        JsonElement metadata = Stamp(Call() with { LexicalResultCount = 5 });

        Assert.Equal(5, metadata.GetProperty("canary_lexical_result_count").GetInt32());
        Assert.Equal("none", metadata.GetProperty("canary_fallback_reason").GetString());
        Assert.Equal("none", metadata.GetProperty("canary_backend").GetString());
        Assert.Equal("none", metadata.GetProperty("canary_embed_warmth").GetString());
        Assert.Equal("none", metadata.GetProperty("canary_embed_latency_bucket").GetString());
        Assert.Equal("none", metadata.GetProperty("canary_knn_latency_bucket").GetString());
    }

    [Fact]
    public void IneligibleRow_RecordsOnlyTheReasonAndClassNeverSemanticCounters()
    {
        JsonElement metadata = Stamp(Call() with
        {
            QueryClass = CanaryQueryClass.Identifier,
            Eligibility = CanaryEligibility.IneligibleQueryClass,
            LexicalResultCount = 5,
        });

        Assert.Equal("ineligible", metadata.GetProperty("canary_arm").GetString());
        Assert.Equal("identifier", metadata.GetProperty("canary_query_class").GetString());
        Assert.Equal("ineligible_query_class", metadata.GetProperty("canary_eligibility").GetString());
        Assert.False(metadata.TryGetProperty("canary_bucket", out _));
        Assert.False(metadata.TryGetProperty("canary_lexical_result_count", out _));
        Assert.False(metadata.TryGetProperty("canary_fallback_reason", out _));
    }

    [Fact]
    public void AbsentIsNeverZeroed_SemanticFieldsAreOmittedOnAControlRow()
    {
        JsonElement metadata = Stamp(Call());

        Assert.False(metadata.TryGetProperty("canary_semantic_result_count", out _));
        Assert.False(metadata.TryGetProperty("canary_fused_result_count", out _));
        Assert.False(metadata.TryGetProperty("canary_semantic_contribution_count", out _));
        Assert.False(metadata.TryGetProperty("canary_encoder_fingerprint", out _));
    }

    [Fact]
    public void ServedResults_HashNamePathAndQualifiedSpellingsCappedAtTen()
    {
        var results = Enumerable.Range(0, 12)
            .Select(i => new CanaryServedResult($"Name{i}", $"src/File{i}.cs", $"Type{i}.Name{i}"))
            .ToList();

        JsonElement metadata = Stamp(Call() with { ResultCount = 12, ServedResults = results });

        Assert.Equal(10, metadata.GetProperty("canary_result_name_hashes").GetArrayLength());
        Assert.Equal(10, metadata.GetProperty("canary_result_path_hashes").GetArrayLength());
        Assert.Equal(10, metadata.GetProperty("canary_result_qualified_hashes").GetArrayLength());
        Assert.True(metadata.GetProperty("canary_result_hash_truncated").GetBoolean());
    }

    [Fact]
    public void ServedResults_HashesMatchTheTargetHashMechanismSoFollowUpsAttribute()
    {
        using TelemetryLedger ledger = OpenLedger();
        using TelemetryScope followUp = ledger.Measure("inspect", null);
        followUp.SetTarget("LedgerWriter.Save");

        JsonElement metadata = Stamp(Call() with
        {
            ResultCount = 1,
            ServedResults = [new CanaryServedResult("Save", "src/LedgerWriter.cs", "LedgerWriter.Save")],
        });

        List<string?> qualified = metadata.GetProperty("canary_result_qualified_hashes")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(followUp.TargetHash, qualified);
        Assert.False(metadata.GetProperty("canary_result_hash_truncated").GetBoolean());
    }

    [Fact]
    public void ServedResults_TopLevelResultContributesNoQualifiedEntry()
    {
        JsonElement metadata = Stamp(Call() with
        {
            ResultCount = 1,
            ServedResults = [new CanaryServedResult("LedgerWriter", "src/LedgerWriter.cs", null)],
        });

        Assert.Equal(1, metadata.GetProperty("canary_result_name_hashes").GetArrayLength());
        Assert.False(metadata.TryGetProperty("canary_result_qualified_hashes", out _));
    }

    [Fact]
    public void ServedResults_EmptyResultSetWritesNoHashArrays()
    {
        JsonElement metadata = Stamp(Call() with { ResultCount = 0 });

        Assert.False(metadata.TryGetProperty("canary_result_name_hashes", out _));
        Assert.False(metadata.TryGetProperty("canary_result_hash_truncated", out _));
    }

    [Fact]
    public void PersistedMetadataNeverContainsQueryOrPathText()
    {
        using TelemetryLedger ledger = OpenLedger();
        using TelemetryScope scope = ledger.Measure("search", "auto");
        scope.SetTarget("where do we validate the refresh token");

        CanaryTelemetry.Stamp(scope, CanaryMode.On, Call() with
        {
            ResultCount = 1,
            ServedResults = [new CanaryServedResult("ValidateRefreshToken", "src/Auth/TokenService.cs", "TokenService.ValidateRefreshToken")],
        });

        foreach (string forbidden in new[]
                 {
                     "where do we validate the refresh token",
                     "ValidateRefreshToken",
                     "src/Auth/TokenService.cs",
                     "TokenService.ValidateRefreshToken",
                 })
        {
            Assert.DoesNotContain(forbidden, scope.MetadataJson, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Assignment_IsTheFrozenDerivationAndReproducibleOffline()
    {
        int first = CanaryAssignment.Bucket(
            CanaryAssignment.HybridExperimentId, "ws-hex", "2026-07-20", CanaryQueryClass.Prose);
        int again = CanaryAssignment.Bucket(
            CanaryAssignment.HybridExperimentId, "ws-hex", "2026-07-20", CanaryQueryClass.Prose);

        Assert.Equal(first, again);
        Assert.InRange(first, 0, 99);
        Assert.NotEqual(first, CanaryAssignment.Bucket(
            CanaryAssignment.HybridExperimentId, "ws-hex", "2026-07-21", CanaryQueryClass.Prose));
    }

    [Fact]
    public void Assignment_SplitsFiftyFiftyOnBucketFiftyAtP5()
    {
        for (int bucket = 0; bucket < 50; bucket++)
            Assert.Equal(CanaryArm.Control, CanaryAssignment.ResolveArm(bucket));
        for (int bucket = 50; bucket < 100; bucket++)
            Assert.Equal(CanaryArm.Treatment, CanaryAssignment.ResolveArm(bucket));
    }

    [Fact]
    public void LatencyBuckets_CoverTheFrozenEdges()
    {
        Assert.Equal("lt_10", CanaryLatencyBucket.For(0));
        Assert.Equal("lt_10", CanaryLatencyBucket.For(9));
        Assert.Equal("lt_25", CanaryLatencyBucket.For(10));
        Assert.Equal("lt_50", CanaryLatencyBucket.For(25));
        Assert.Equal("lt_100", CanaryLatencyBucket.For(50));
        Assert.Equal("lt_250", CanaryLatencyBucket.For(100));
        Assert.Equal("lt_500", CanaryLatencyBucket.For(250));
        Assert.Equal("lt_1000", CanaryLatencyBucket.For(500));
        Assert.Equal("lt_3000", CanaryLatencyBucket.For(1000));
        Assert.Equal("gte_3000", CanaryLatencyBucket.For(3000));
        Assert.Equal("none", CanaryLatencyBucket.For(null));
    }

    [Fact]
    public void Enums_MatchTheContractValueSets()
    {
        Assert.Equal(
            ["control", "treatment", "shadow", "ineligible"],
            CanaryArm.All);
        Assert.Equal(
            ["identifier", "path", "short_token", "prose", "docs_like", "mixed"],
            CanaryQueryClass.All);
        Assert.Equal(
            [
                "eligible", "ineligible_query_class", "ineligible_semantic_disabled",
                "ineligible_experiment_inactive", "ineligible_vectors_unavailable",
                "ineligible_vectors_incompatible", "ineligible_circuit_open",
                "ineligible_cross_workspace_no_generation", "ineligible_surface",
            ],
            CanaryEligibility.All);
        Assert.Equal(
            [
                "none", "vectors_missing", "vectors_stale", "vectors_incompatible", "vectors_building",
                "model_not_prepared", "circuit_open", "embed_timeout", "embed_error", "knn_error",
                "disk_blocked", "disabled", "unknown",
            ],
            CanaryFallbackReason.All);
        Assert.Equal(["metal", "vulkan", "cuda", "cpu", "none"], CanaryBackend.All);
        Assert.Equal(["warm", "cold", "none"], CanaryEmbedWarmth.All);
        Assert.Equal(["ok", "timeout", "error", "skipped"], CanaryShadowStatus.All);
        Assert.Equal(
            ["none", "source", "file", "semantic_symbol", "semantic_docs", "semantic_mixed", "unavailable"],
            CanaryRescueKind.All);
    }

    [Fact]
    public void StampedRowSurvivesToTheLedger()
    {
        string dbPath = Path.Combine(_temp, "telemetry.db");
        using (TelemetryLedger ledger = TelemetryLedger.Open(dbPath, "ws-canary", _temp))
        using (TelemetryScope scope = ledger.Measure("search", "auto"))
        {
            CanaryTelemetry.Stamp(scope, CanaryMode.On, Call() with { LexicalResultCount = 3 });
        }

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        connection.Open();
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT metadata_json FROM tool_telemetry ORDER BY ts DESC LIMIT 1;";
        string persisted = Assert.IsType<string>(command.ExecuteScalar());

        Assert.Contains("\"canary_arm\":\"control\"", persisted, StringComparison.Ordinal);
    }

    private JsonElement Stamp(CanaryCallFacts call)
    {
        using TelemetryLedger ledger = OpenLedger();
        using TelemetryScope scope = ledger.Measure("search", "auto");
        CanaryTelemetry.Stamp(scope, CanaryMode.On, call);
        return JsonDocument.Parse(scope.MetadataJson).RootElement.Clone();
    }

    private static CanaryCallFacts Call() => new()
    {
        WorkspaceId = "ws-hex",
        UtcDate = "2026-07-20",
        QueryClass = CanaryQueryClass.Prose,
        Eligibility = CanaryEligibility.Eligible,
    };

    private TelemetryLedger OpenLedger() =>
        TelemetryLedger.Open(Path.Combine(_temp, "telemetry-" + Guid.NewGuid() + ".db"), "ws-canary", _temp);

    public void Dispose()
    {
        if (Directory.Exists(_temp))
            Directory.Delete(_temp, recursive: true);
    }
}
