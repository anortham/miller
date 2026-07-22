using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Miller.Server.Telemetry;
using Xunit;

namespace Miller.Tests.Telemetry;

public sealed class CanaryAggregateTests
{
    private const string SourceA = "00112233445566778899aabbccddeeff";
    private const string SourceB = "ffeeddccbbaa99887766554433221100";

    [Fact]
    public void OneValidExport_ReproducesFrozenSuccessAndShadowMath()
    {
        JsonArray units = [];
        JsonArray shadows = [];
        for (int i = 0; i < 30; i++)
        {
            units.Add(Unit(i, "control", attributedSuccesses: 0));
            units.Add(Unit(100 + i, "treatment", attributedSuccesses: 5));
            shadows.Add(ShadowUnit(200 + i));
        }

        CanaryAggregateReport report = CanaryAggregate.Combine(
            [Document(SourceA, "2026-07-01", "2026-07-31", units, shadows, suppressed: 4)]);

        Assert.Equal(1, report.InputDocuments);
        Assert.Equal(1, report.UniqueDocuments);
        Assert.Equal(0, report.DuplicateDocuments);
        Assert.Equal(1, report.SourceCount);
        Assert.Equal(4, report.SuppressedUnitCount);
        CanaryAggregateCohort cohort = Assert.Single(report.Cohorts);
        Assert.Equal(30, cohort.SuccessRate.ControlUnits);
        Assert.Equal(30, cohort.SuccessRate.TreatmentUnits);
        Assert.Equal(CanaryClauseVerdict.Pass, cohort.SuccessRate.Verdict);
        Assert.Equal(150, cohort.ControlDiagnostics.Calls);
        Assert.Equal(150, cohort.TreatmentDiagnostics.Calls);
        Assert.Equal(150, cohort.TreatmentDiagnostics.SemanticContributionCalls);
        Assert.Equal(150, cohort.TreatmentDiagnostics.FallbackReasonCounts["none"]);
        Assert.Equal(150, cohort.TreatmentDiagnostics.RescueKindCounts["none"]);
        Assert.Equal(30, cohort.IdentifierShadow.ShadowUnits);
        Assert.Equal(CanaryClauseVerdict.Pass, cohort.IdentifierShadow.Verdict);
        Assert.Equal(CanaryLatencyScreenVerdict.NoHigherBucket, cohort.WarmLatencyScreen.Verdict);
        Assert.Equal(150, cohort.WarmLatencyScreen.ControlRows);
        Assert.Equal(150, cohort.WarmLatencyScreen.TreatmentWarmRows);
    }

    [Fact]
    public void DisjointSources_CombineDeterministicallyRegardlessOfInputOrder()
    {
        string first = Document(SourceA, "2026-07-01", "2026-07-07", [Unit(1, "control")], []);
        string second = Document(SourceB, "2026-07-01", "2026-07-07", [Unit(2, "treatment")], []);

        string forward = CanaryAggregate.Render(CanaryAggregate.Combine([first, second]), json: true);
        string reverse = CanaryAggregate.Render(CanaryAggregate.Combine([second, first]), json: true);

        Assert.Equal(forward, reverse);
        Assert.Equal(2, CanaryAggregate.Combine([first, second]).SourceCount);
    }

    [Fact]
    public void ExactDuplicateDocument_IsDeduplicated()
    {
        string document = Document(SourceA, "2026-07-01", "2026-07-07", [Unit(1, "control")], []);

        CanaryAggregateReport report = CanaryAggregate.Combine([document, document]);

        Assert.Equal(2, report.InputDocuments);
        Assert.Equal(1, report.UniqueDocuments);
        Assert.Equal(1, report.DuplicateDocuments);
        Assert.Equal(1, Assert.Single(report.Cohorts).SuccessRate.ControlUnits);
    }

    [Fact]
    public void SameSourceAndWindowWithDifferentContent_IsRejected()
    {
        string first = Document(SourceA, "2026-07-01", "2026-07-07", [Unit(1, "control")], []);
        string conflicting = Document(SourceA, "2026-07-01", "2026-07-07", [Unit(2, "control")], []);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => CanaryAggregate.Combine([first, conflicting]));

        Assert.Contains("same source and window", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PartiallyOverlappingWindowsFromOneSource_AreRejected()
    {
        string first = Document(SourceA, "2026-07-01", "2026-07-07", [Unit(1, "control")], []);
        string overlapping = Document(SourceA, "2026-07-03", "2026-07-14", [Unit(2, "control")], []);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => CanaryAggregate.Combine([first, overlapping]));

        Assert.Contains("overlap", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SameRandomizedUnitAcrossSources_MergesBeforeStatistics()
    {
        JsonObject firstUnit = Unit(1, "treatment", attributedSuccesses: 5);
        JsonObject secondUnit = Unit(1, "treatment", attributedSuccesses: 0);
        string first = Document(SourceA, "2026-07-01", "2026-07-07", [firstUnit], []);
        string second = Document(SourceB, "2026-07-01", "2026-07-07", [secondUnit], []);

        CanaryAggregateCohort cohort = Assert.Single(CanaryAggregate.Combine([first, second]).Cohorts);

        Assert.Equal(1, cohort.SuccessRate.TreatmentUnits);
        Assert.Equal(10, cohort.WarmLatencyScreen.TreatmentWarmRows);
    }

    [Fact]
    public void CompleteDifferentIdentities_NeverPool()
    {
        JsonObject firstUnit = Unit(1, "control");
        JsonObject secondUnit = Unit(2, "control");
        secondUnit["miller_version"] = "2.0.0+different";

        CanaryAggregateReport report = CanaryAggregate.Combine(
            [Document(SourceA, "2026-07-01", "2026-07-07", [firstUnit, secondUnit], [])]);

        Assert.Equal(2, report.Cohorts.Count);
        Assert.All(report.Cohorts, cohort => Assert.Equal(1, cohort.SuccessRate.ControlUnits));
    }

    [Fact]
    public void ConditionalRescueCounts_MayBeAbsentWithoutDroppingTheUnit()
    {
        JsonObject unit = Unit(1, "control");
        unit["rescue_kind_counts"] = new JsonObject();

        CanaryAggregateCohort cohort = Assert.Single(CanaryAggregate.Combine(
            [Document(SourceA, "2026-07-01", "2026-07-07", [unit], [])]).Cohorts);

        Assert.Equal(1, cohort.SuccessRate.ControlUnits);
        Assert.Empty(cohort.ControlDiagnostics.RescueKindCounts);
    }

    [Fact]
    public void V3WriterOutput_IsAcceptedWithoutShapeTranslation()
    {
        string temp = Path.Combine(Path.GetTempPath(), "miller-canary-aggregate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string db = Path.Combine(temp, "telemetry.db");
        try
        {
            using (var seeder = new CanarySeeder(db))
            {
                for (int i = 0; i < 5; i++)
                {
                    seeder.InsertCanary(
                        "ws-a", "2026-07-03", "prose", "control", bucket: 23,
                        encoderFingerprint: "encoder-v1", storageSchema: "vec0-int8-256-cosine-v1",
                        corpusGeneration: "cards-v1-chunks-v1", fusionProfile: "fusion-v1",
                        contractVersion: CanaryContractProfile.V3ContractVersion);
                }
            }

            string document = CanaryExport.BuildJson(
                db,
                new DateOnly(2026, 7, 1),
                new DateOnly(2026, 7, 7),
                new DateTimeOffset(2026, 7, 8, 0, 0, 0, TimeSpan.Zero),
                CanaryContractProfile.V3ContractVersion,
                SourceA);

            CanaryAggregateCohort cohort = Assert.Single(CanaryAggregate.Combine([document]).Cohorts);
            Assert.Equal(1, cohort.SuccessRate.ControlUnits);
            Assert.Empty(cohort.ControlDiagnostics.RescueKindCounts);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public void MalformedOrInconsistentV3Document_FailsClosed(string document, string expectedMessage)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => CanaryAggregate.Combine([document]));

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConflictingCopiesOfOneUnitAcrossSources_AreRejected()
    {
        JsonObject firstUnit = Unit(1, "control");
        JsonObject secondUnit = Unit(1, "treatment");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => CanaryAggregate.Combine(
            [
                Document(SourceA, "2026-07-01", "2026-07-07", [firstUnit], []),
                Document(SourceB, "2026-07-01", "2026-07-07", [secondUnit], []),
            ]));

        Assert.Contains("unit_id", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Renderers_ExposeOnlyAggregateCountersAndLabelLatencyAsAScreen()
    {
        const string secretPath = "/private/repositories/customer-one";
        CanaryAggregateReport report = CanaryAggregate.Combine(
            [Document(SourceA, "2026-07-01", "2026-07-07", [Unit(1, "control")], [])]);

        string json = CanaryAggregate.Render(report, json: true);
        string human = CanaryAggregate.Render(report, json: false);

        Assert.Contains("\"kind\":\"screen\"", json, StringComparison.Ordinal);
        Assert.Contains("\"treatment_diagnostics\"", json, StringComparison.Ordinal);
        Assert.Contains("\"fallback_reason_counts\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("gate_passes", json, StringComparison.Ordinal);
        Assert.DoesNotContain("duration_ms", json, StringComparison.Ordinal);
        Assert.DoesNotContain("workspace_id", json, StringComparison.Ordinal);
        Assert.DoesNotContain(SourceA, json, StringComparison.Ordinal);
        Assert.DoesNotContain(secretPath, json, StringComparison.Ordinal);
        Assert.Contains("screen only", human, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SourceA, human, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> InvalidDocuments()
    {
        yield return Invalid(root => root["schema_version"] = 2, "schema_version");
        yield return Invalid(root => root["canary_contract_version"] = 2, "canary_contract_version");
        yield return Invalid(root => root["export_source_id"] = SourceA.ToUpperInvariant(), "export_source_id");
        yield return Invalid(root => root["experiment_id"] = "unknown", "experiment_id");
        yield return Invalid(root => root["workspace_id"] = "secret", "unknown field");
        yield return Invalid(root => root["window"]!["from_utc"] = "2026/07/01", "from_utc");
        yield return Invalid(root => root["units"]![0]!["utc_date"] = "2026-08-01", "window");
        yield return Invalid(root => root["units"]![0]!["unit_id"] = "XYZ", "unit_id");
        yield return Invalid(root => root["units"]![0]!["miller_version"] = null, "miller_version");
        yield return Invalid(root => root["units"]![0]!["calls"] = -1, "calls");
        yield return Invalid(root => root["units"]![0]!["ok_calls"] = 4, "outcome counts");
        yield return Invalid(root => root["units"]![0]!["bucket"] = 99, "arm");
        yield return Invalid(root => root["units"]![0]!["fallback_reason_counts"]!["invented"] = 5, "fallback_reason_counts");
        yield return Invalid(root => root["units"]![0]!["total_latency_bucket_counts"]!["lt_10"] = 4, "total_latency_bucket_counts");
        yield return Invalid(root =>
        {
            JsonObject shadow = ShadowUnit(9);
            shadow["shadow_status_counts"] = CountMap("ok", 4);
            root["shadow_units"] = new JsonArray(shadow);
        }, "shadow_status_counts");
    }

    private static object[] Invalid(Action<JsonObject> mutation)
    {
        JsonObject root = JsonNode.Parse(Document(
            SourceA,
            "2026-07-01",
            "2026-07-07",
            [Unit(1, "control")],
            []))!.AsObject();
        mutation(root);
        return [root.ToJsonString(), mutation.Method.Name.Contains("generated", StringComparison.Ordinal) ? "generated" : ""];
    }

    private static object[] Invalid(Action<JsonObject> mutation, string expectedMessage)
    {
        object[] row = Invalid(mutation);
        row[1] = expectedMessage;
        return row;
    }

    private static string Document(
        string sourceId,
        string from,
        string to,
        JsonArray units,
        JsonArray shadows,
        int suppressed = 0)
    {
        var root = new JsonObject
        {
            ["schema_version"] = 3,
            ["canary_contract_version"] = 3,
            ["export_source_id"] = sourceId,
            ["experiment_id"] = "semantic_hybrid_search_v1",
            ["generated_at_utc"] = "2026-08-01T00:00:00Z",
            ["window"] = new JsonObject
            {
                ["from_utc"] = from,
                ["to_utc"] = to,
            },
            ["suppressed_unit_count"] = suppressed,
            ["units"] = units,
            ["shadow_units"] = shadows,
        };
        return root.ToJsonString();
    }

    private static JsonObject Unit(int id, string arm, int attributedSuccesses = 0, int calls = 5)
    {
        bool treatment = arm == "treatment";
        var unit = new JsonObject
        {
            ["unit_id"] = id.ToString("x12"),
            ["utc_date"] = "2026-07-03",
            ["query_class"] = "prose",
            ["arm"] = arm,
            ["bucket"] = treatment ? 73 : 23,
            ["calls"] = calls,
            ["ok_calls"] = calls,
            ["empty_calls"] = 0,
            ["error_calls"] = 0,
            ["attributed_success_calls"] = attributedSuccesses,
            ["semantic_contribution_calls"] = treatment ? calls : 0,
            ["miller_version"] = "1.14.0+abc1234",
            ["encoder_fingerprint"] = "encoder-v1",
            ["storage_schema"] = "vec0-int8-256-cosine-v1",
            ["corpus_generation"] = "cards-v1-chunks-v1",
            ["fusion_profile"] = "fusion-v1",
            ["policy_version"] = 1,
            ["fallback_reason_counts"] = CountMap("none", calls),
            ["rescue_kind_counts"] = CountMap("none", calls),
            ["backend_counts"] = CountMap(treatment ? "cpu" : "none", calls),
            ["embed_warmth_counts"] = CountMap(treatment ? "warm" : "none", calls),
            ["embed_latency_bucket_counts"] = CountMap(treatment ? "lt_10" : "none", calls),
            ["knn_latency_bucket_counts"] = CountMap(treatment ? "lt_10" : "none", calls),
            ["total_latency_bucket_counts"] = CountMap("lt_100", calls),
        };
        if (treatment)
            unit["warm_total_latency_bucket_counts"] = CountMap("lt_100", calls);
        return unit;
    }

    private static JsonObject ShadowUnit(int id, int calls = 5, int? okCalls = null)
    {
        int ok = okCalls ?? calls;
        int skipped = calls - ok;
        var status = new JsonObject { ["ok"] = ok };
        if (skipped > 0)
            status["skipped"] = skipped;
        return new JsonObject
        {
            ["unit_id"] = id.ToString("x12"),
            ["utc_date"] = "2026-07-03",
            ["query_class"] = "identifier",
            ["miller_version"] = "1.14.0+abc1234",
            ["encoder_fingerprint"] = "encoder-v1",
            ["storage_schema"] = "vec0-int8-256-cosine-v1",
            ["corpus_generation"] = "cards-v1-chunks-v1",
            ["fusion_profile"] = "fusion-v1",
            ["policy_version"] = 1,
            ["calls"] = calls,
            ["shadow_status_counts"] = status,
            ["top1_changed_calls"] = 0,
            ["overlap_at_10_histogram"] = CountMap("10", ok),
            ["lexical_top1_rank_histogram"] = CountMap("1", ok),
        };
    }

    private static JsonObject CountMap(string key, int value) => new() { [key] = value };
}
