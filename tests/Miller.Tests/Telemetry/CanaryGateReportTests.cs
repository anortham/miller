using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Server.Telemetry;
using Xunit;

namespace Miller.Tests.Telemetry;

public sealed class CanaryGateReportTests : IDisposable
{
    private const string SavePath = "src/Miller.Server/Telemetry/LedgerWriter.cs";

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "miller-canary-gate-" + Guid.NewGuid().ToString("N"));

    public CanaryGateReportTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
    }

    private string Db => Path.Combine(_temp, "telemetry.db");

    [Fact]
    public void Attribution_BareTargetMatchesThroughTheNameArray()
    {
        Assert.True(AttributesFollowUp(
            served: new Served([Digest("Save")], [Digest(SavePath)], [Digest("LedgerWriter.Save")]),
            followUpHash: Digest("Save")));
    }

    [Fact]
    public void Attribution_QualifiedTargetMatchesThroughTheQualifiedArray()
    {
        Assert.True(AttributesFollowUp(
            served: new Served([Digest("Save")], [Digest(SavePath)], [Digest("LedgerWriter.Save")]),
            followUpHash: Digest("LedgerWriter.Save")));
    }

    [Fact]
    public void Attribution_QualifiedTargetIsLostWhenTheQualifiedArrayIsOmitted()
    {
        Assert.False(AttributesFollowUp(
            served: new Served([Digest("Save")], [Digest(SavePath)], []),
            followUpHash: Digest("LedgerWriter.Save")));
    }

    [Fact]
    public void Attribution_PathTargetMatchesThroughThePathArrayForContentRead()
    {
        using var seeder = new CanarySeeder(Db);
        string canaryId = seeder.InsertCanary(
            "ws-a", "2026-07-14", "prose", "treatment", timeOfDay: "10:00:00.000",
            nameHashes: [Digest("Save")], pathHashes: [Digest(SavePath)], qualifiedHashes: [Digest("LedgerWriter.Save")]);
        seeder.InsertFollowUp("ws-a", "2026-07-14", Digest(SavePath), tool: "content", op: "read", timeOfDay: "10:01:00.000");

        Assert.Contains(canaryId, Attributed());
    }

    [Fact]
    public void Attribution_TopLevelResultMatchesThroughTheNameArrayWithNoQualifiedEntry()
    {
        Assert.True(AttributesFollowUp(
            served: new Served([Digest("LedgerWriter")], [Digest(SavePath)], []),
            followUpHash: Digest("LedgerWriter")));
    }

    [Fact]
    public void Attribution_DeeperSpellingDoesNotAttributeInV1()
    {
        Assert.False(AttributesFollowUp(
            served: new Served([Digest("Save")], [Digest(SavePath)], [Digest("LedgerWriter.Save")]),
            followUpHash: Digest("Miller.Server.Telemetry.LedgerWriter.Save")));
    }

    [Fact]
    public void Attribution_HashInTwoArraysIsCreditedOnceAndAtMostOneFollowUpPerRow()
    {
        using var seeder = new CanarySeeder(Db);
        string canaryId = seeder.InsertCanary(
            "ws-a", "2026-07-14", "prose", "treatment", timeOfDay: "10:00:00.000",
            nameHashes: [Digest("Save")], pathHashes: [Digest("Save")], qualifiedHashes: []);
        seeder.InsertFollowUp("ws-a", "2026-07-14", Digest("Save"), timeOfDay: "10:01:00.000");
        seeder.InsertFollowUp("ws-a", "2026-07-14", Digest("Save"), timeOfDay: "10:02:00.000");

        IReadOnlySet<string> attributed = Attributed();
        Assert.Equal([canaryId], attributed);
    }

    [Fact]
    public void Attribution_FollowUpOutsideTheTenMinuteWindowIsNotCredited()
    {
        using var seeder = new CanarySeeder(Db);
        seeder.InsertCanary(
            "ws-a", "2026-07-14", "prose", "treatment", timeOfDay: "10:00:00.000",
            nameHashes: [Digest("Save")]);
        seeder.InsertFollowUp("ws-a", "2026-07-14", Digest("Save"), timeOfDay: "10:11:00.000");

        Assert.Empty(Attributed());
    }

    [Fact]
    public void Attribution_SameTimestampUsesLedgerOrderToCreditTheLaterFollowUp()
    {
        using var seeder = new CanarySeeder(Db);
        string canaryId = seeder.InsertCanary(
            "ws-a", "2026-07-14", "prose", "treatment", timeOfDay: "10:00:00.000",
            nameHashes: [Digest("Save")]);
        seeder.InsertFollowUp(
            "ws-a", "2026-07-14", Digest("Save"), timeOfDay: "10:00:00.000");

        Assert.Contains(canaryId, Attributed());
    }

    [Fact]
    public void SuccessRate_UnderThirtyUnitsPerArmReportsUnderpoweredAndNeverPasses()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            SeedAttributedTreatmentUnits(seeder, units: 5);
            SeedControlUnits(seeder, units: 5);
        }

        CanaryCohortGate cohort = SingleCohort();
        Assert.Equal(CanaryClauseVerdict.Underpowered, cohort.SuccessRate.Verdict);
        Assert.False(cohort.GatePasses);
    }

    [Theory]
    [InlineData(CanaryClauseVerdict.Underpowered)]
    [InlineData(CanaryClauseVerdict.Fail)]
    public void OverallGate_NeverPassesWhenIdentifierShadowDoesNotPass(CanaryClauseVerdict shadowVerdict)
    {
        var cohort = new CanaryCohortGate(
            "1.14.0+abc1234",
            new CanarySuccessRateClause(CanaryClauseVerdict.Pass, 30, 30, 0.1, 0.01, 0.2),
            new CanaryWarmLatencyClause(CanaryClauseVerdict.Pass, 100, 100, 100, 100, 1.0),
            new CanaryShadowClause(shadowVerdict, 30, 0.01, 9.0));

        Assert.False(cohort.GatePasses);
    }

    [Fact]
    public void SuccessRate_SeparatedArmsWithEnoughUnitsPass()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            SeedAttributedTreatmentUnits(seeder, units: 30);
            SeedControlUnits(seeder, units: 30);
        }

        CanarySuccessRateClause clause = SingleCohort().SuccessRate;
        Assert.Equal(CanaryClauseVerdict.Pass, clause.Verdict);
        Assert.Equal(30, clause.ControlUnits);
        Assert.Equal(30, clause.TreatmentUnits);
        Assert.True(clause.Lower > 0);
        Assert.Equal(1.0, clause.Effect!.Value, 6);
    }

    [Fact]
    public void WarmLatency_UnderOneHundredRowsReportsIndeterminate()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            for (int i = 0; i < 40; i++)
                seeder.InsertCanary($"ws-t{i}", "2026-07-14", "prose", "treatment", embedWarmth: "warm", durationMs: 100);
            for (int i = 0; i < 40; i++)
                seeder.InsertCanary($"ws-c{i}", "2026-07-14", "prose", "control", durationMs: 100);
        }

        CanaryCohortGate cohort = SingleCohort();
        Assert.Equal(CanaryClauseVerdict.Indeterminate, cohort.WarmLatency.Verdict);
        Assert.False(cohort.GatePasses);
    }

    [Fact]
    public void WarmLatency_EqualP95PassesTheTwentyPercentThreshold()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            for (int i = 0; i < 120; i++)
                seeder.InsertCanary($"ws-t{i}", "2026-07-14", "prose", "treatment", embedWarmth: "warm", durationMs: 100);
            for (int i = 0; i < 120; i++)
                seeder.InsertCanary($"ws-c{i}", "2026-07-14", "prose", "control", durationMs: 100);
        }

        CanaryWarmLatencyClause clause = SingleCohort().WarmLatency;
        Assert.Equal(CanaryClauseVerdict.Pass, clause.Verdict);
        Assert.Equal(100, clause.P95TreatmentWarm);
        Assert.Equal(100, clause.P95Control);
    }

    [Fact]
    public void WarmLatency_TreatmentRegressionBeyondTwentyPercentFails()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            for (int i = 0; i < 120; i++)
                seeder.InsertCanary($"ws-t{i}", "2026-07-14", "prose", "treatment", embedWarmth: "warm", durationMs: 300);
            for (int i = 0; i < 120; i++)
                seeder.InsertCanary($"ws-c{i}", "2026-07-14", "prose", "control", durationMs: 100);
        }

        Assert.Equal(CanaryClauseVerdict.Fail, SingleCohort().WarmLatency.Verdict);
    }

    [Fact]
    public void Shadow_UnderThirtyUnitsReportsUnderpowered()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            for (int u = 0; u < 5; u++)
                for (int i = 0; i < 5; i++)
                    seeder.InsertShadow($"ws-s{u}", "2026-07-14", "ok", overlapAt10: 10, top1Changed: false, lexicalTop1Rank: 1);
        }

        Assert.Equal(CanaryClauseVerdict.Underpowered, SingleCohort().Shadow.Verdict);
    }

    [Fact]
    public void Shadow_HighOverlapAndLowTop1ChangePasses()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            for (int u = 0; u < 30; u++)
                for (int i = 0; i < 5; i++)
                    seeder.InsertShadow($"ws-s{u}", "2026-07-14", "ok", overlapAt10: 10, top1Changed: false, lexicalTop1Rank: 1);
        }

        CanaryShadowClause clause = SingleCohort().Shadow;
        Assert.Equal(CanaryClauseVerdict.Pass, clause.Verdict);
        Assert.True(clause.OverlapAt10Lower >= 8.0);
        Assert.True(clause.Top1ChangedUpper <= 0.05);
    }

    [Fact]
    public void Cohorts_AreSplitByExactMillerVersionString()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            seeder.InsertCanary("ws-a", "2026-07-14", "prose", "treatment", millerVersion: "1.14.0+aaa");
            seeder.InsertCanary("ws-a", "2026-07-14", "prose", "treatment", millerVersion: "1.14.0+bbb");
        }

        CanaryGate gate = CanaryGateReport.Compute(Db);
        Assert.Equal(["1.14.0+aaa", "1.14.0+bbb"], gate.Cohorts.Select(c => c.MillerVersion));
    }

    [Fact]
    public void Cohorts_AreSplitByCompleteSemanticIdentityWithinExactVersion()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            seeder.InsertCanary(
                "ws-a", "2026-07-14", "prose", "treatment",
                millerVersion: "1.14.0+aaa", encoderFingerprint: "encoder-a",
                storageSchema: "schema-a", corpusGeneration: "corpus-a",
                fusionProfile: "fusion-a", policyVersion: 1);
            seeder.InsertCanary(
                "ws-a", "2026-07-14", "prose", "treatment",
                millerVersion: "1.14.0+aaa", encoderFingerprint: "encoder-b",
                storageSchema: "schema-b", corpusGeneration: "corpus-b",
                fusionProfile: "fusion-b", policyVersion: 2);
        }

        using JsonDocument doc = JsonDocument.Parse(CanaryGateReport.Render(Db, json: true));
        JsonElement[] cohorts = doc.RootElement.GetProperty("cohorts").EnumerateArray().ToArray();

        Assert.Equal(2, cohorts.Length);
        Assert.Contains(cohorts, cohort =>
            cohort.GetProperty("miller_version").GetString() == "1.14.0+aaa"
            && cohort.GetProperty("encoder_fingerprint").GetString() == "encoder-a"
            && cohort.GetProperty("storage_schema").GetString() == "schema-a"
            && cohort.GetProperty("corpus_generation").GetString() == "corpus-a"
            && cohort.GetProperty("fusion_profile").GetString() == "fusion-a"
            && cohort.GetProperty("policy_version").GetInt32() == 1);
        Assert.Contains(cohorts, cohort =>
            cohort.GetProperty("miller_version").GetString() == "1.14.0+aaa"
            && cohort.GetProperty("encoder_fingerprint").GetString() == "encoder-b"
            && cohort.GetProperty("storage_schema").GetString() == "schema-b"
            && cohort.GetProperty("corpus_generation").GetString() == "corpus-b"
            && cohort.GetProperty("fusion_profile").GetString() == "fusion-b"
            && cohort.GetProperty("policy_version").GetInt32() == 2);
    }

    [Fact]
    public void Cohort_ComparesControlAndTreatmentTaggedWithTheSameConfiguredGeneration()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            for (int i = 0; i < 5; i++)
            {
                seeder.InsertCanary(
                    "ws-control", "2026-07-14", "prose", "control",
                    encoderFingerprint: "encoder-a", storageSchema: "schema-a",
                    corpusGeneration: "corpus-a", fusionProfile: "fusion-a", policyVersion: 1);
                seeder.InsertCanary(
                    "ws-treatment", "2026-07-14", "prose", "treatment",
                    encoderFingerprint: "encoder-a", storageSchema: "schema-a",
                    corpusGeneration: "corpus-a", fusionProfile: "fusion-a", policyVersion: 1);
            }
        }

        CanaryCohortGate cohort = Assert.Single(CanaryGateReport.Compute(Db).Cohorts);

        Assert.Equal(1, cohort.SuccessRate.ControlUnits);
        Assert.Equal(1, cohort.SuccessRate.TreatmentUnits);
        Assert.Equal("encoder-a", cohort.EncoderFingerprint);
        Assert.Equal("fusion-a", cohort.FusionProfile);
    }

    [Fact]
    public void RenderHuman_IdentifiesCompleteSemanticIdentityCohorts()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            seeder.InsertCanary(
                "ws-a", "2026-07-14", "prose", "treatment",
                millerVersion: "1.14.0+aaa", encoderFingerprint: "encoder-a",
                storageSchema: "schema-a", corpusGeneration: "corpus-a",
                fusionProfile: "fusion-a", policyVersion: 1);
            seeder.InsertCanary(
                "ws-a", "2026-07-14", "prose", "treatment",
                millerVersion: "1.14.0+aaa", encoderFingerprint: "encoder-b",
                storageSchema: "schema-b", corpusGeneration: "corpus-b",
                fusionProfile: "fusion-b", policyVersion: 2);
        }

        string text = CanaryGateReport.Render(Db, json: false);

        Assert.Contains(
            "cohort 1.14.0+aaa [encoder=encoder-a schema=schema-a corpus=corpus-a fusion=fusion-a policy=1]",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "cohort 1.14.0+aaa [encoder=encoder-b schema=schema-b corpus=corpus-b fusion=fusion-b policy=2]",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RowsWithoutMillerVersionOrWrongContractVersionAreExcludedFromEveryCohort()
    {
        using (var seeder = new CanarySeeder(Db))
        {
            seeder.InsertCanary("ws-a", "2026-07-14", "prose", "treatment", millerVersion: null);
            seeder.InsertCanary("ws-a", "2026-07-14", "prose", "treatment", contractVersion: 1);
        }

        Assert.Empty(CanaryGateReport.Compute(Db).Cohorts);
    }

    [Fact]
    public void RenderJson_CarriesTheThreeClauseVerdicts()
    {
        using (var seeder = new CanarySeeder(Db))
            seeder.InsertCanary("ws-a", "2026-07-14", "prose", "treatment");

        using JsonDocument doc = JsonDocument.Parse(CanaryGateReport.Render(Db, json: true));
        JsonElement cohort = doc.RootElement.GetProperty("cohorts")[0];
        Assert.Equal("underpowered", cohort.GetProperty("success_rate").GetProperty("verdict").GetString());
        Assert.Equal("indeterminate", cohort.GetProperty("warm_latency").GetProperty("verdict").GetString());
        Assert.Equal("underpowered", cohort.GetProperty("identifier_shadow").GetProperty("verdict").GetString());
        Assert.False(cohort.GetProperty("gate_passes").GetBoolean());
    }

    [Fact]
    public void RenderHuman_NamesEachClauseAndItsVerdict()
    {
        using (var seeder = new CanarySeeder(Db))
            seeder.InsertCanary("ws-a", "2026-07-14", "prose", "treatment");

        string text = CanaryGateReport.Render(Db, json: false);
        Assert.Contains("success-rate: underpowered", text, StringComparison.Ordinal);
        Assert.Contains("warm-latency: indeterminate", text, StringComparison.Ordinal);
        Assert.Contains("identifier-shadow: underpowered", text, StringComparison.Ordinal);
    }

    private sealed record Served(IReadOnlyList<string> Name, IReadOnlyList<string> Path, IReadOnlyList<string> Qualified);

    private bool AttributesFollowUp(Served served, string followUpHash)
    {
        using var seeder = new CanarySeeder(Db);
        string canaryId = seeder.InsertCanary(
            "ws-a", "2026-07-14", "prose", "treatment", timeOfDay: "10:00:00.000",
            nameHashes: served.Name, pathHashes: served.Path, qualifiedHashes: served.Qualified);
        seeder.InsertFollowUp("ws-a", "2026-07-14", followUpHash, timeOfDay: "10:01:00.000");
        return Attributed().Contains(canaryId);
    }

    private IReadOnlySet<string> Attributed()
    {
        SqliteConnection.ClearAllPools();
        IReadOnlyList<CanaryRow> rows = CanaryLedgerReader.ReadCanaryRows(Db);
        IReadOnlyList<CanaryFollowUp> followUps = CanaryLedgerReader.ReadFollowUps(Db);
        return CanaryLedgerReader.AttributedRowIds(rows, followUps);
    }

    private CanaryCohortGate SingleCohort()
    {
        SqliteConnection.ClearAllPools();
        return Assert.Single(CanaryGateReport.Compute(Db).Cohorts);
    }

    private void SeedAttributedTreatmentUnits(CanarySeeder seeder, int units)
    {
        for (int u = 0; u < units; u++)
        {
            for (int i = 0; i < 5; i++)
            {
                string hash = Digest($"T-{u}-{i}");
                seeder.InsertCanary($"ws-t{u}", "2026-07-14", "prose", "treatment", nameHashes: [hash]);
                seeder.InsertFollowUp($"ws-t{u}", "2026-07-14", hash);
            }
        }
    }

    private static void SeedControlUnits(CanarySeeder seeder, int units)
    {
        for (int u = 0; u < units; u++)
            for (int i = 0; i < 5; i++)
                seeder.InsertCanary($"ws-c{u}", "2026-07-14", "prose", "control");
    }

    private static string Digest(string value) => CanarySeeder.Digest(value);
}
