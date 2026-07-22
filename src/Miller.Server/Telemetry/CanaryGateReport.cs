using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Miller.Core.Telemetry;

namespace Miller.Server.Telemetry;

public enum CanaryClauseVerdict
{
    Pass,
    Fail,
    Underpowered,
    Indeterminate,
}

public sealed record CanarySuccessRateClause(
    CanaryClauseVerdict Verdict,
    int ControlUnits,
    int TreatmentUnits,
    double? Effect,
    double? Lower,
    double? Upper);

public sealed record CanaryWarmLatencyClause(
    CanaryClauseVerdict Verdict,
    int TreatmentWarmRows,
    int ControlRows,
    long? P95TreatmentWarm,
    long? P95Control,
    double? Ratio);

public sealed record CanaryShadowClause(
    CanaryClauseVerdict Verdict,
    int ShadowUnits,
    double? Top1ChangedUpper,
    double? OverlapAt10Lower);

public sealed record CanaryCohortGate(
    string MillerVersion,
    CanarySuccessRateClause SuccessRate,
    CanaryWarmLatencyClause WarmLatency,
    CanaryShadowClause Shadow)
{
    public string? EncoderFingerprint { get; init; }
    public string? StorageSchema { get; init; }
    public string? CorpusGeneration { get; init; }
    public string? FusionProfile { get; init; }
    public int? PolicyVersion { get; init; }

    public bool GatePasses =>
        SuccessRate.Verdict == CanaryClauseVerdict.Pass
        && WarmLatency.Verdict == CanaryClauseVerdict.Pass
        && Shadow.Verdict == CanaryClauseVerdict.Pass;
}

public sealed record CanaryGate(IReadOnlyList<CanaryCohortGate> Cohorts);

/// <summary>
/// The contract-selected local-authoritative canary gate. Reads raw <c>tool_telemetry</c> rows, computes
/// attribution, per-unit success rates, and the warm latency and identifier-shadow clauses — each within one
/// complete semantic-identity cohort. Renders a per-clause human verdict or JSON.
/// </summary>
public static class CanaryGateReport
{
    private const int MinCallsPerUnit = 5;
    private const int MinUnitsPerArm = 30;
    private const int MinLatencyRows = 100;
    private const int MinShadowUnits = 30;
    private const double WarmLatencyThreshold = 1.20;
    private const double Top1ChangedMargin = 0.05;
    private const double OverlapFloor = 8.0;

    public static string Render(string dbPath, bool json) =>
        Render(dbPath, json, CanaryContractProfile.V2ContractVersion);

    public static string Render(string dbPath, bool json, int contractVersion)
    {
        CanaryGate gate = Compute(dbPath, contractVersion);
        return json ? RenderJson(gate, contractVersion) : RenderHuman(gate);
    }

    public static CanaryGate Compute(string dbPath) =>
        Compute(dbPath, CanaryContractProfile.V2ContractVersion);

    public static CanaryGate Compute(string dbPath, int contractVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ValidateContractVersion(contractVersion);

        IReadOnlyList<CanaryRow> allRows = CanaryLedgerReader.ReadCanaryRows(dbPath);
        IReadOnlyList<CanaryFollowUp> followUps = CanaryLedgerReader.ReadFollowUps(dbPath);
        IReadOnlySet<string> attributed = CanaryLedgerReader.AttributedRowIds(allRows, followUps);

        List<CanaryRow> contractRows = allRows
            .Where(r => r.ContractVersion == contractVersion && r.MillerVersion is not null)
            .ToList();

        var cohorts = new List<CanaryCohortGate>();
        foreach (IGrouping<CanaryCohortIdentity, CanaryRow> group in contractRows
            .GroupBy(CanaryCohortIdentity.From)
            .OrderBy(g => g.Key.MillerVersion, StringComparer.Ordinal)
            .ThenBy(g => g.Key.EncoderFingerprint, StringComparer.Ordinal)
            .ThenBy(g => g.Key.StorageSchema, StringComparer.Ordinal)
            .ThenBy(g => g.Key.CorpusGeneration, StringComparer.Ordinal)
            .ThenBy(g => g.Key.FusionProfile, StringComparer.Ordinal)
            .ThenBy(g => g.Key.PolicyVersion))
        {
            CanaryCohortIdentity identity = group.Key;
            List<CanaryRow> cohortRows = [.. group];
            cohorts.Add(new CanaryCohortGate(
                identity.MillerVersion,
                SuccessRateClause(cohortRows, attributed),
                WarmLatencyClause(cohortRows),
                ShadowClause(cohortRows))
            {
                EncoderFingerprint = identity.EncoderFingerprint,
                StorageSchema = identity.StorageSchema,
                CorpusGeneration = identity.CorpusGeneration,
                FusionProfile = identity.FusionProfile,
                PolicyVersion = identity.PolicyVersion,
            });
        }

        return new CanaryGate(cohorts);
    }

    private static void ValidateContractVersion(int contractVersion)
    {
        if (contractVersion is not (CanaryContractProfile.V2ContractVersion or CanaryContractProfile.V3ContractVersion))
            throw new ArgumentOutOfRangeException(nameof(contractVersion), contractVersion, "Canary contract must be 2 or 3.");
    }

    private static CanarySuccessRateClause SuccessRateClause(IReadOnlyList<CanaryRow> cohortRows, IReadOnlySet<string> attributed)
    {
        List<double> controlRates = [];
        List<double> treatmentRates = [];

        IEnumerable<CanaryRow> eligible = cohortRows.Where(r =>
            r.Eligibility == CanaryEligibility.Eligible
            && r.ExperimentId == CanaryAssignment.HybridExperimentId
            && r.Arm is CanaryArm.Control or CanaryArm.Treatment);

        foreach (var unit in eligible
            .GroupBy(r => (r.WorkspaceId, r.UtcDate, r.QueryClass)))
        {
            List<CanaryRow> rows = [.. unit];
            if (rows.Count < MinCallsPerUnit)
                continue;

            int successes = rows.Count(r => r.Outcome == "ok" && r.ResultCount > 0 && attributed.Contains(r.Id));
            double rate = (double)successes / rows.Count;
            if (rows[0].Arm == CanaryArm.Treatment)
                treatmentRates.Add(rate);
            else
                controlRates.Add(rate);
        }

        if (controlRates.Count < MinUnitsPerArm || treatmentRates.Count < MinUnitsPerArm)
            return new CanarySuccessRateClause(
                CanaryClauseVerdict.Underpowered, controlRates.Count, treatmentRates.Count, null, null, null);

        (double lower, double upper, double effect) = CanaryGateMath.WelchInterval(treatmentRates, controlRates);
        CanaryClauseVerdict verdict = lower > 0.0 ? CanaryClauseVerdict.Pass : CanaryClauseVerdict.Fail;
        return new CanarySuccessRateClause(verdict, controlRates.Count, treatmentRates.Count, effect, lower, upper);
    }

    private static CanaryWarmLatencyClause WarmLatencyClause(IReadOnlyList<CanaryRow> cohortRows)
    {
        IEnumerable<CanaryRow> eligible = cohortRows.Where(r =>
            r.Eligibility == CanaryEligibility.Eligible
            && r.ExperimentId == CanaryAssignment.HybridExperimentId);

        long[] treatmentWarm = eligible
            .Where(r => r.Arm == CanaryArm.Treatment && r.EmbedWarmth == "warm")
            .Select(r => r.DurationMs).Order().ToArray();
        long[] control = eligible
            .Where(r => r.Arm == CanaryArm.Control)
            .Select(r => r.DurationMs).Order().ToArray();

        if (treatmentWarm.Length < MinLatencyRows || control.Length < MinLatencyRows)
            return new CanaryWarmLatencyClause(
                CanaryClauseVerdict.Indeterminate, treatmentWarm.Length, control.Length, null, null, null);

        long p95Treatment = CanaryGateMath.NearestRankP95(treatmentWarm);
        long p95Control = CanaryGateMath.NearestRankP95(control);
        double ratio = p95Control == 0 ? double.PositiveInfinity : (double)p95Treatment / p95Control;
        CanaryClauseVerdict verdict = p95Treatment <= WarmLatencyThreshold * p95Control
            ? CanaryClauseVerdict.Pass
            : CanaryClauseVerdict.Fail;
        return new CanaryWarmLatencyClause(
            verdict, treatmentWarm.Length, control.Length, p95Treatment, p95Control, ratio);
    }

    private static CanaryShadowClause ShadowClause(IReadOnlyList<CanaryRow> cohortRows)
    {
        List<double> top1ChangedRates = [];
        List<double> overlapMeans = [];

        IEnumerable<CanaryRow> shadow = cohortRows.Where(r =>
            r.Arm == CanaryArm.Shadow && r.ShadowStatus == "ok");

        foreach (var unit in shadow
            .GroupBy(r => (r.WorkspaceId, r.UtcDate, r.QueryClass)))
        {
            List<CanaryRow> rows = [.. unit];
            if (rows.Count < MinCallsPerUnit)
                continue;

            top1ChangedRates.Add((double)rows.Count(r => r.ShadowTop1Changed == true) / rows.Count);
            overlapMeans.Add(rows.Average(r => (double)(r.ShadowOverlapAt10 ?? 0)));
        }

        if (top1ChangedRates.Count < MinShadowUnits)
            return new CanaryShadowClause(CanaryClauseVerdict.Underpowered, top1ChangedRates.Count, null, null);

        (_, double top1Upper, _) = CanaryGateMath.OneSampleInterval(top1ChangedRates);
        (double overlapLower, _, _) = CanaryGateMath.OneSampleInterval(overlapMeans);
        CanaryClauseVerdict verdict = top1Upper <= Top1ChangedMargin && overlapLower >= OverlapFloor
            ? CanaryClauseVerdict.Pass
            : CanaryClauseVerdict.Fail;
        return new CanaryShadowClause(verdict, top1ChangedRates.Count, top1Upper, overlapLower);
    }

    private static string RenderJson(CanaryGate gate, int contractVersion)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("experiment_id", CanaryAssignment.HybridExperimentId);
            w.WriteNumber("canary_contract_version", contractVersion);
            w.WriteStartArray("cohorts");
            foreach (CanaryCohortGate cohort in gate.Cohorts)
            {
                w.WriteStartObject();
                w.WriteString("miller_version", cohort.MillerVersion);
                WriteNullableString(w, "encoder_fingerprint", cohort.EncoderFingerprint);
                WriteNullableString(w, "storage_schema", cohort.StorageSchema);
                WriteNullableString(w, "corpus_generation", cohort.CorpusGeneration);
                WriteNullableString(w, "fusion_profile", cohort.FusionProfile);
                WriteNullableNumber(w, "policy_version", cohort.PolicyVersion);
                w.WriteBoolean("gate_passes", cohort.GatePasses);

                w.WriteStartObject("success_rate");
                w.WriteString("verdict", Label(cohort.SuccessRate.Verdict));
                w.WriteNumber("control_units", cohort.SuccessRate.ControlUnits);
                w.WriteNumber("treatment_units", cohort.SuccessRate.TreatmentUnits);
                w.WriteNumber("min_units_per_arm", MinUnitsPerArm);
                WriteOptionalNumber(w, "effect", cohort.SuccessRate.Effect);
                WriteOptionalNumber(w, "ci_lower", cohort.SuccessRate.Lower);
                WriteOptionalNumber(w, "ci_upper", cohort.SuccessRate.Upper);
                w.WriteEndObject();

                w.WriteStartObject("warm_latency");
                w.WriteString("verdict", Label(cohort.WarmLatency.Verdict));
                w.WriteNumber("treatment_warm_rows", cohort.WarmLatency.TreatmentWarmRows);
                w.WriteNumber("control_rows", cohort.WarmLatency.ControlRows);
                w.WriteNumber("min_rows", MinLatencyRows);
                w.WriteNumber("threshold_ratio", WarmLatencyThreshold);
                WriteOptionalNumber(w, "p95_treatment_warm_ms", cohort.WarmLatency.P95TreatmentWarm);
                WriteOptionalNumber(w, "p95_control_ms", cohort.WarmLatency.P95Control);
                WriteOptionalNumber(w, "ratio", cohort.WarmLatency.Ratio);
                w.WriteEndObject();

                w.WriteStartObject("identifier_shadow");
                w.WriteString("verdict", Label(cohort.Shadow.Verdict));
                w.WriteNumber("shadow_units", cohort.Shadow.ShadowUnits);
                w.WriteNumber("min_units", MinShadowUnits);
                w.WriteNumber("top1_changed_margin", Top1ChangedMargin);
                w.WriteNumber("overlap_at_10_floor", OverlapFloor);
                WriteOptionalNumber(w, "top1_changed_ci_upper", cohort.Shadow.Top1ChangedUpper);
                WriteOptionalNumber(w, "overlap_at_10_ci_lower", cohort.Shadow.OverlapAt10Lower);
                w.WriteEndObject();

                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderHuman(CanaryGate gate)
    {
        if (gate.Cohorts.Count == 0)
            return "canary gate: no canary rows in the ledger.";

        var sb = new StringBuilder();
        sb.Append("canary gate (").Append(CanaryAssignment.HybridExperimentId)
            .Append(") — local, authoritative. Reported per complete semantic-identity cohort.");

        foreach (CanaryCohortGate cohort in gate.Cohorts)
        {
            sb.Append('\n').Append("cohort ").Append(cohort.MillerVersion)
                .Append(" [encoder=").Append(CohortValue(cohort.EncoderFingerprint))
                .Append(" schema=").Append(CohortValue(cohort.StorageSchema))
                .Append(" corpus=").Append(CohortValue(cohort.CorpusGeneration))
                .Append(" fusion=").Append(CohortValue(cohort.FusionProfile))
                .Append(" policy=").Append(cohort.PolicyVersion?.ToString(CultureInfo.InvariantCulture) ?? "null")
                .Append(']')
                .Append(": ").Append(cohort.GatePasses ? "PASS" : "not a pass");

            CanarySuccessRateClause s = cohort.SuccessRate;
            sb.Append('\n').Append("  success-rate: ").Append(Label(s.Verdict))
                .Append(" — units control=").Append(s.ControlUnits).Append('/').Append(MinUnitsPerArm)
                .Append(" treatment=").Append(s.TreatmentUnits).Append('/').Append(MinUnitsPerArm);
            if (s.Effect is not null)
                sb.Append(" · effect=").Append(Fmt(s.Effect)).Append(" 95% CI=[").Append(Fmt(s.Lower))
                    .Append(", ").Append(Fmt(s.Upper)).Append("] · pass rule: lower > 0");

            CanaryWarmLatencyClause l = cohort.WarmLatency;
            sb.Append('\n').Append("  warm-latency: ").Append(Label(l.Verdict))
                .Append(" — rows treatment_warm=").Append(l.TreatmentWarmRows).Append('/').Append(MinLatencyRows)
                .Append(" control=").Append(l.ControlRows).Append('/').Append(MinLatencyRows);
            if (l.P95TreatmentWarm is not null)
                sb.Append(" · p95 treatment=").Append(l.P95TreatmentWarm).Append("ms control=")
                    .Append(l.P95Control).Append("ms ratio=").Append(Fmt(l.Ratio))
                    .Append(" · pass rule: ≤ ").Append(Fmt(WarmLatencyThreshold)).Append("×");

            CanaryShadowClause sh = cohort.Shadow;
            sb.Append('\n').Append("  identifier-shadow: ").Append(Label(sh.Verdict))
                .Append(" — units=").Append(sh.ShadowUnits).Append('/').Append(MinShadowUnits);
            if (sh.Top1ChangedUpper is not null)
                sb.Append(" · top1_changed upper=").Append(Fmt(sh.Top1ChangedUpper))
                    .Append(" (≤ ").Append(Fmt(Top1ChangedMargin)).Append(") · overlap@10 lower=")
                    .Append(Fmt(sh.OverlapAt10Lower)).Append(" (≥ ").Append(Fmt(OverlapFloor)).Append(')');
        }

        return sb.ToString();
    }

    private static void WriteNullableString(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null)
            w.WriteNull(name);
        else
            w.WriteString(name, value);
    }

    private static void WriteNullableNumber(Utf8JsonWriter w, string name, int? value)
    {
        if (value is null)
            w.WriteNull(name);
        else
            w.WriteNumber(name, value.Value);
    }

    private static void WriteOptionalNumber(Utf8JsonWriter w, string name, double? value)
    {
        if (value is { } v && !double.IsNaN(v) && !double.IsInfinity(v))
            w.WriteNumber(name, v);
    }

    private static void WriteOptionalNumber(Utf8JsonWriter w, string name, long? value)
    {
        if (value is { } v)
            w.WriteNumber(name, v);
    }

    private static string Label(CanaryClauseVerdict verdict) => verdict switch
    {
        CanaryClauseVerdict.Pass => "pass",
        CanaryClauseVerdict.Fail => "fail",
        CanaryClauseVerdict.Underpowered => "underpowered",
        _ => "indeterminate",
    };

    private static string Fmt(double? value) =>
        value is { } v ? v.ToString("0.###", CultureInfo.InvariantCulture) : "n/a";

    private static string CohortValue(string? value) => value ?? "null";

    private sealed record CanaryCohortIdentity(
        string MillerVersion,
        string? EncoderFingerprint,
        string? StorageSchema,
        string? CorpusGeneration,
        string? FusionProfile,
        int? PolicyVersion)
    {
        public static CanaryCohortIdentity From(CanaryRow row) => new(
            row.MillerVersion!,
            row.EncoderFingerprint,
            row.StorageSchema,
            row.CorpusGeneration,
            row.FusionProfile,
            row.PolicyVersion);
    }
}
