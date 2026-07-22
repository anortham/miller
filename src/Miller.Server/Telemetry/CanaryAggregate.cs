using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Miller.Core.Telemetry;

namespace Miller.Server.Telemetry;

public enum CanaryLatencyScreenVerdict
{
    NoHigherBucket,
    PossibleRegression,
    Underpowered,
}

public sealed record CanaryWarmLatencyScreen(
    CanaryLatencyScreenVerdict Verdict,
    long TreatmentWarmRows,
    long ControlRows,
    string? TreatmentMedianP95Bucket,
    string? ControlMedianP95Bucket);

public sealed record CanaryAggregateArmDiagnostics(
    int Units,
    long Calls,
    long AttributedSuccessCalls,
    long SemanticContributionCalls,
    IReadOnlyDictionary<string, long> FallbackReasonCounts,
    IReadOnlyDictionary<string, long> RescueKindCounts,
    IReadOnlyDictionary<string, long> BackendCounts,
    IReadOnlyDictionary<string, long> EmbedWarmthCounts,
    IReadOnlyDictionary<string, long> EmbedLatencyBucketCounts,
    IReadOnlyDictionary<string, long> KnnLatencyBucketCounts,
    IReadOnlyDictionary<string, long> TotalLatencyBucketCounts,
    IReadOnlyDictionary<string, long> WarmTotalLatencyBucketCounts);

public sealed record CanaryAggregateCohort(
    string MillerVersion,
    string EncoderFingerprint,
    string StorageSchema,
    string CorpusGeneration,
    string FusionProfile,
    int PolicyVersion,
    CanarySuccessRateClause SuccessRate,
    CanaryAggregateArmDiagnostics ControlDiagnostics,
    CanaryAggregateArmDiagnostics TreatmentDiagnostics,
    CanaryWarmLatencyScreen WarmLatencyScreen,
    CanaryShadowClause IdentifierShadow);

public sealed record CanaryAggregateReport(
    int InputDocuments,
    int UniqueDocuments,
    int DuplicateDocuments,
    int SourceCount,
    long SuppressedUnitCount,
    IReadOnlyList<CanaryAggregateCohort> Cohorts);

/// <summary>
/// Validates and combines privacy-safe v3 canary export documents. The aggregate reconstructs the success and
/// identifier-shadow clauses exactly. Bucketed latency remains a screen; it never becomes an aggregate gate.
/// </summary>
public static class CanaryAggregate
{
    private const int MinUnitsPerArm = 30;
    private const int MinLatencyRows = 100;
    private const int MinShadowUnits = 30;
    private const double Top1ChangedMargin = 0.05;
    private const double OverlapFloor = 8.0;

    private static readonly IReadOnlySet<string> TopLevelFields = Set(
        "schema_version", "canary_contract_version", "export_source_id", "experiment_id",
        "generated_at_utc", "window", "suppressed_unit_count", "units", "shadow_units");

    private static readonly IReadOnlySet<string> WindowFields = Set("from_utc", "to_utc");

    private static readonly IReadOnlySet<string> UnitFields = Set(
        "unit_id", "utc_date", "query_class", "arm", "bucket", "calls", "ok_calls", "empty_calls",
        "error_calls", "attributed_success_calls", "semantic_contribution_calls", "miller_version",
        "encoder_fingerprint", "storage_schema", "corpus_generation", "fusion_profile", "policy_version",
        "fallback_reason_counts", "rescue_kind_counts", "backend_counts", "embed_warmth_counts",
        "embed_latency_bucket_counts", "knn_latency_bucket_counts", "total_latency_bucket_counts",
        "warm_total_latency_bucket_counts");

    private static readonly IReadOnlySet<string> RequiredUnitFields = Set(
        "unit_id", "utc_date", "query_class", "arm", "bucket", "calls", "ok_calls", "empty_calls",
        "error_calls", "attributed_success_calls", "semantic_contribution_calls", "miller_version",
        "encoder_fingerprint", "storage_schema", "corpus_generation", "fusion_profile", "policy_version",
        "fallback_reason_counts", "rescue_kind_counts", "backend_counts", "embed_warmth_counts",
        "embed_latency_bucket_counts", "knn_latency_bucket_counts", "total_latency_bucket_counts");

    private static readonly IReadOnlySet<string> ShadowFields = Set(
        "unit_id", "utc_date", "query_class", "miller_version", "encoder_fingerprint", "storage_schema",
        "corpus_generation", "fusion_profile", "policy_version", "calls", "shadow_status_counts",
        "top1_changed_calls", "overlap_at_10_histogram", "lexical_top1_rank_histogram");

    private static readonly IReadOnlySet<string> EligibleQueryClasses = Set(
        CanaryQueryClass.Prose, CanaryQueryClass.DocsLike, CanaryQueryClass.Mixed);

    private static readonly IReadOnlySet<string> LatencyValues = Set(
        [.. CanaryGateMath.LatencyLadder, CanaryLatencyBucket.None]);

    private static readonly IReadOnlySet<string> WarmLatencyValues = Set([.. CanaryGateMath.LatencyLadder]);

    public static CanaryAggregateReport Combine(IReadOnlyList<string> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count == 0)
            throw new ArgumentException("At least one v3 canary export document is required.", nameof(documents));

        List<ExportDocument> parsed = [];
        for (int i = 0; i < documents.Count; i++)
            parsed.Add(ParseDocument(documents[i], i));

        List<ExportDocument> unique = Deduplicate(parsed, out int duplicates);
        ValidateWindows(unique);

        var experimentUnits = new Dictionary<string, MergedExperimentUnit>(StringComparer.Ordinal);
        var shadowUnits = new Dictionary<string, MergedShadowUnit>(StringComparer.Ordinal);
        foreach (ExportDocument document in unique)
        {
            foreach (ExperimentUnit unit in document.Units)
            {
                if (shadowUnits.ContainsKey(unit.UnitId))
                    throw Invalid($"unit_id '{unit.UnitId}' is used by both hybrid and shadow units.");
                if (!experimentUnits.TryGetValue(unit.UnitId, out MergedExperimentUnit? merged))
                    experimentUnits.Add(unit.UnitId, new MergedExperimentUnit(unit));
                else
                    merged.Add(unit);
            }

            foreach (ShadowUnit unit in document.ShadowUnits)
            {
                if (experimentUnits.ContainsKey(unit.UnitId))
                    throw Invalid($"unit_id '{unit.UnitId}' is used by both hybrid and shadow units.");
                if (!shadowUnits.TryGetValue(unit.UnitId, out MergedShadowUnit? merged))
                    shadowUnits.Add(unit.UnitId, new MergedShadowUnit(unit));
                else
                    merged.Add(unit);
            }
        }

        List<SemanticIdentity> identities = experimentUnits.Values.Select(static unit => unit.Identity)
            .Concat(shadowUnits.Values.Select(static unit => unit.Identity))
            .Distinct()
            .OrderBy(static identity => identity.MillerVersion, StringComparer.Ordinal)
            .ThenBy(static identity => identity.EncoderFingerprint, StringComparer.Ordinal)
            .ThenBy(static identity => identity.StorageSchema, StringComparer.Ordinal)
            .ThenBy(static identity => identity.CorpusGeneration, StringComparer.Ordinal)
            .ThenBy(static identity => identity.FusionProfile, StringComparer.Ordinal)
            .ThenBy(static identity => identity.PolicyVersion)
            .ToList();

        var cohorts = new List<CanaryAggregateCohort>(identities.Count);
        foreach (SemanticIdentity identity in identities)
        {
            List<MergedExperimentUnit> cohortUnits = experimentUnits.Values
                .Where(unit => unit.Identity == identity).ToList();
            List<MergedShadowUnit> cohortShadow = shadowUnits.Values
                .Where(unit => unit.Identity == identity).ToList();
            cohorts.Add(new CanaryAggregateCohort(
                identity.MillerVersion,
                identity.EncoderFingerprint,
                identity.StorageSchema,
                identity.CorpusGeneration,
                identity.FusionProfile,
                identity.PolicyVersion,
                SuccessRate(cohortUnits),
                ArmDiagnostics(cohortUnits, CanaryArm.Control),
                ArmDiagnostics(cohortUnits, CanaryArm.Treatment),
                WarmLatencyScreen(cohortUnits),
                IdentifierShadow(cohortShadow)));
        }

        return new CanaryAggregateReport(
            documents.Count,
            unique.Count,
            duplicates,
            unique.Select(static document => document.SourceId).Distinct(StringComparer.Ordinal).Count(),
            unique.Sum(static document => (long)document.SuppressedUnitCount),
            cohorts);
    }

    public static string Render(CanaryAggregateReport report, bool json)
    {
        ArgumentNullException.ThrowIfNull(report);
        return json ? RenderJson(report) : RenderHuman(report);
    }

    private static List<ExportDocument> Deduplicate(
        IReadOnlyList<ExportDocument> documents,
        out int duplicateCount)
    {
        var byWindow = new Dictionary<SourceWindow, ExportDocument>();
        var unique = new List<ExportDocument>();
        duplicateCount = 0;
        foreach (ExportDocument document in documents)
        {
            var key = new SourceWindow(document.SourceId, document.From, document.To);
            if (!byWindow.TryGetValue(key, out ExportDocument? existing))
            {
                byWindow.Add(key, document);
                unique.Add(document);
                continue;
            }

            if (!string.Equals(existing.Raw, document.Raw, StringComparison.Ordinal))
                throw Invalid("Documents for the same source and window have different content.");
            duplicateCount++;
        }
        return unique;
    }

    private static void ValidateWindows(IReadOnlyList<ExportDocument> documents)
    {
        foreach (IGrouping<string, ExportDocument> source in documents.GroupBy(
            static document => document.SourceId,
            StringComparer.Ordinal))
        {
            ExportDocument[] ordered = source.OrderBy(static document => document.From)
                .ThenBy(static document => document.To).ToArray();
            for (int i = 1; i < ordered.Length; i++)
            {
                if (ordered[i].From <= ordered[i - 1].To)
                    throw Invalid("Export windows from one source overlap; use disjoint windows or one final window.");
            }
        }
    }

    private static ExportDocument ParseDocument(string raw, int index)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw Invalid($"Document {index + 1} is empty.");

        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            JsonElement root = RequireObject(document.RootElement, "document");
            ValidateFields(root, TopLevelFields, TopLevelFields, "document");
            RequireExactInt(root, "schema_version", CanaryExport.V3SchemaVersion);
            RequireExactInt(root, "canary_contract_version", CanaryContractProfile.V3ContractVersion);

            string sourceId = RequireString(root, "export_source_id");
            if (!CanaryExport.IsValidSourceId(sourceId))
                throw Invalid("export_source_id must be exactly 32 lowercase hexadecimal characters.");
            if (!string.Equals(
                RequireString(root, "experiment_id"),
                CanaryAssignment.HybridExperimentId,
                StringComparison.Ordinal))
            {
                throw Invalid($"experiment_id must be '{CanaryAssignment.HybridExperimentId}'.");
            }

            string generated = RequireString(root, "generated_at_utc");
            if (!DateTimeOffset.TryParseExact(
                generated,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset generatedAt))
            {
                throw Invalid("generated_at_utc must be an exact UTC timestamp.");
            }

            JsonElement window = RequireObject(root.GetProperty("window"), "window");
            ValidateFields(window, WindowFields, WindowFields, "window");
            DateOnly from = RequireDate(window, "from_utc");
            DateOnly to = RequireDate(window, "to_utc");
            if (from > to)
                throw Invalid("window from_utc must not be after to_utc.");
            if (DateOnly.FromDateTime(generatedAt.UtcDateTime) < to)
                throw Invalid("generated_at_utc must not precede the export window.");

            int suppressed = RequireNonnegativeInt(root, "suppressed_unit_count");
            List<ExperimentUnit> units = ParseUnits(root.GetProperty("units"), from, to);
            List<ShadowUnit> shadows = ParseShadowUnits(root.GetProperty("shadow_units"), from, to);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (string unitId in units.Select(static unit => unit.UnitId)
                .Concat(shadows.Select(static unit => unit.UnitId)))
            {
                if (!ids.Add(unitId))
                    throw Invalid($"unit_id '{unitId}' is duplicated within one export document.");
            }

            return new ExportDocument(raw, sourceId, from, to, suppressed, units, shadows);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Document {index + 1} is not valid JSON.", ex);
        }
    }

    private static List<ExperimentUnit> ParseUnits(JsonElement element, DateOnly from, DateOnly to)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw Invalid("units must be an array.");

        var units = new List<ExperimentUnit>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            JsonElement unit = RequireObject(item, "unit");
            ValidateFields(unit, UnitFields, RequiredUnitFields, "unit");
            string unitId = RequireUnitId(unit);
            DateOnly date = RequireContainedDate(unit, from, to);
            string queryClass = RequireString(unit, "query_class");
            if (!EligibleQueryClasses.Contains(queryClass))
                throw Invalid("unit query_class is not eligible for the hybrid experiment.");
            string arm = RequireString(unit, "arm");
            if (arm is not (CanaryArm.Control or CanaryArm.Treatment))
                throw Invalid("unit arm must be control or treatment.");
            int bucket = RequireInt(unit, "bucket", 0, 99);
            if (CanaryAssignment.ResolveArm(bucket) != arm)
                throw Invalid("unit arm does not match its frozen assignment bucket.");

            int calls = RequireInt(unit, "calls", 5, int.MaxValue);
            int ok = RequireNonnegativeInt(unit, "ok_calls");
            int empty = RequireNonnegativeInt(unit, "empty_calls");
            int error = RequireNonnegativeInt(unit, "error_calls");
            if ((long)ok + empty + error != calls)
                throw Invalid("unit outcome counts must sum to calls.");
            int attributed = RequireNonnegativeInt(unit, "attributed_success_calls");
            if (attributed > ok)
                throw Invalid("attributed_success_calls cannot exceed ok_calls.");
            int semanticContribution = RequireNonnegativeInt(unit, "semantic_contribution_calls");
            if (semanticContribution > calls)
                throw Invalid("semantic_contribution_calls cannot exceed calls.");

            SemanticIdentity identity = RequireIdentity(unit);
            Dictionary<string, long> fallback = ReadCountMap(
                unit, "fallback_reason_counts", Set(CanaryFallbackReason.All), calls);
            Dictionary<string, long> rescue = ReadCountMap(
                unit, "rescue_kind_counts", Set(CanaryRescueKind.All), calls, requireExactTotal: false);
            Dictionary<string, long> backend = ReadCountMap(
                unit, "backend_counts", Set(CanaryBackend.All), calls);
            Dictionary<string, long> warmth = ReadCountMap(
                unit, "embed_warmth_counts", Set(CanaryEmbedWarmth.All), calls);
            Dictionary<string, long> embedLatency = ReadCountMap(
                unit, "embed_latency_bucket_counts", LatencyValues, calls);
            Dictionary<string, long> knnLatency = ReadCountMap(
                unit, "knn_latency_bucket_counts", LatencyValues, calls);
            Dictionary<string, long> totalLatency = ReadCountMap(
                unit, "total_latency_bucket_counts", LatencyValues, calls);

            Dictionary<string, long> warmTotal;
            if (arm == CanaryArm.Treatment)
            {
                if (!unit.TryGetProperty("warm_total_latency_bucket_counts", out _))
                    throw Invalid("treatment unit is missing warm_total_latency_bucket_counts.");
                long warmCalls = warmth.GetValueOrDefault(CanaryEmbedWarmth.All[0]);
                warmTotal = ReadCountMap(
                    unit, "warm_total_latency_bucket_counts", WarmLatencyValues, warmCalls);
            }
            else
            {
                if (unit.TryGetProperty("warm_total_latency_bucket_counts", out _))
                    throw Invalid("control unit must not contain warm_total_latency_bucket_counts.");
                warmTotal = new Dictionary<string, long>(StringComparer.Ordinal);
            }

            units.Add(new ExperimentUnit(
                unitId, date, queryClass, arm, bucket, calls, ok, empty, error, attributed,
                semanticContribution, identity, fallback, rescue, backend, warmth, embedLatency,
                knnLatency, totalLatency, warmTotal));
        }
        return units;
    }

    private static List<ShadowUnit> ParseShadowUnits(JsonElement element, DateOnly from, DateOnly to)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw Invalid("shadow_units must be an array.");

        var units = new List<ShadowUnit>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            JsonElement unit = RequireObject(item, "shadow unit");
            ValidateFields(unit, ShadowFields, ShadowFields, "shadow unit");
            string unitId = RequireUnitId(unit);
            DateOnly date = RequireContainedDate(unit, from, to);
            if (RequireString(unit, "query_class") != CanaryQueryClass.Identifier)
                throw Invalid("shadow unit query_class must be identifier.");
            SemanticIdentity identity = RequireIdentity(unit);
            int calls = RequireInt(unit, "calls", 5, int.MaxValue);
            Dictionary<string, long> statuses = ReadCountMap(
                unit, "shadow_status_counts", Set(CanaryShadowStatus.All), calls);
            long okCalls = statuses.GetValueOrDefault(CanaryShadowStatus.Ok);
            int top1Changed = RequireNonnegativeInt(unit, "top1_changed_calls");
            if (top1Changed > okCalls)
                throw Invalid("top1_changed_calls cannot exceed ok shadow calls.");
            Dictionary<int, long> overlap = ReadHistogram(
                unit, "overlap_at_10_histogram", 0, 10, okCalls);
            Dictionary<int, long> lexicalRank = ReadHistogram(
                unit, "lexical_top1_rank_histogram", 0, 50, okCalls);
            units.Add(new ShadowUnit(
                unitId, date, CanaryQueryClass.Identifier, identity, calls, statuses,
                top1Changed, overlap, lexicalRank));
        }
        return units;
    }

    private static CanarySuccessRateClause SuccessRate(IReadOnlyList<MergedExperimentUnit> units)
    {
        List<double> control = units.Where(static unit => unit.Arm == CanaryArm.Control)
            .Select(static unit => (double)unit.AttributedSuccessCalls / unit.Calls).ToList();
        List<double> treatment = units.Where(static unit => unit.Arm == CanaryArm.Treatment)
            .Select(static unit => (double)unit.AttributedSuccessCalls / unit.Calls).ToList();
        if (control.Count < MinUnitsPerArm || treatment.Count < MinUnitsPerArm)
            return new CanarySuccessRateClause(
                CanaryClauseVerdict.Underpowered, control.Count, treatment.Count, null, null, null);

        (double lower, double upper, double effect) = CanaryGateMath.WelchInterval(treatment, control);
        return new CanarySuccessRateClause(
            lower > 0 ? CanaryClauseVerdict.Pass : CanaryClauseVerdict.Fail,
            control.Count,
            treatment.Count,
            effect,
            lower,
            upper);
    }

    private static CanaryWarmLatencyScreen WarmLatencyScreen(IReadOnlyList<MergedExperimentUnit> units)
    {
        List<string> controlP95 = units.Where(static unit => unit.Arm == CanaryArm.Control)
            .Select(static unit => CanaryGateMath.BucketedP95(ToIntCounts(unit.TotalLatencyCounts), checked((int)unit.Calls)))
            .ToList();
        List<string> treatmentP95 = units.Where(static unit => unit.Arm == CanaryArm.Treatment && unit.WarmCalls > 0)
            .Select(static unit => CanaryGateMath.BucketedP95(ToIntCounts(unit.WarmTotalLatencyCounts), checked((int)unit.WarmCalls)))
            .ToList();
        long controlRows = units.Where(static unit => unit.Arm == CanaryArm.Control).Sum(static unit => unit.Calls);
        long treatmentRows = units.Where(static unit => unit.Arm == CanaryArm.Treatment).Sum(static unit => unit.WarmCalls);

        if (controlRows < MinLatencyRows || treatmentRows < MinLatencyRows)
            return new CanaryWarmLatencyScreen(
                CanaryLatencyScreenVerdict.Underpowered, treatmentRows, controlRows, null, null);

        string controlMedian = MedianBucket(controlP95);
        string treatmentMedian = MedianBucket(treatmentP95);
        CanaryLatencyScreenVerdict verdict = BucketIndex(treatmentMedian) > BucketIndex(controlMedian)
            ? CanaryLatencyScreenVerdict.PossibleRegression
            : CanaryLatencyScreenVerdict.NoHigherBucket;
        return new CanaryWarmLatencyScreen(
            verdict, treatmentRows, controlRows, treatmentMedian, controlMedian);
    }

    private static CanaryAggregateArmDiagnostics ArmDiagnostics(
        IReadOnlyList<MergedExperimentUnit> units,
        string arm)
    {
        List<MergedExperimentUnit> selected = units.Where(unit => unit.Arm == arm).ToList();
        return new CanaryAggregateArmDiagnostics(
            selected.Count,
            selected.Sum(static unit => unit.Calls),
            selected.Sum(static unit => unit.AttributedSuccessCalls),
            selected.Sum(static unit => unit.SemanticContributionCalls),
            SumMaps(selected.Select(static unit => unit.FallbackCounts)),
            SumMaps(selected.Select(static unit => unit.RescueCounts)),
            SumMaps(selected.Select(static unit => unit.BackendCounts)),
            SumMaps(selected.Select(static unit => unit.WarmthCounts)),
            SumMaps(selected.Select(static unit => unit.EmbedLatencyCounts)),
            SumMaps(selected.Select(static unit => unit.KnnLatencyCounts)),
            SumMaps(selected.Select(static unit => unit.TotalLatencyCounts)),
            SumMaps(selected.Select(static unit => unit.WarmTotalLatencyCounts)));
    }

    private static CanaryShadowClause IdentifierShadow(IReadOnlyList<MergedShadowUnit> units)
    {
        List<MergedShadowUnit> included = units.Where(static unit => unit.OkCalls >= 5).ToList();
        if (included.Count < MinShadowUnits)
            return new CanaryShadowClause(CanaryClauseVerdict.Underpowered, included.Count, null, null);

        List<double> top1ChangedRates = included
            .Select(static unit => (double)unit.Top1ChangedCalls / unit.OkCalls).ToList();
        List<double> overlapMeans = included
            .Select(static unit => unit.OverlapHistogram.Sum(static pair => (double)pair.Key * pair.Value) / unit.OkCalls)
            .ToList();
        (_, double top1Upper, _) = CanaryGateMath.OneSampleInterval(top1ChangedRates);
        (double overlapLower, _, _) = CanaryGateMath.OneSampleInterval(overlapMeans);
        CanaryClauseVerdict verdict = top1Upper <= Top1ChangedMargin && overlapLower >= OverlapFloor
            ? CanaryClauseVerdict.Pass
            : CanaryClauseVerdict.Fail;
        return new CanaryShadowClause(verdict, included.Count, top1Upper, overlapLower);
    }

    private static string RenderJson(CanaryAggregateReport report)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", 1);
            writer.WriteString("report_kind", "canary_v3_aggregate");
            writer.WriteNumber("canary_contract_version", CanaryContractProfile.V3ContractVersion);
            writer.WriteString("experiment_id", CanaryAssignment.HybridExperimentId);
            writer.WriteNumber("input_documents", report.InputDocuments);
            writer.WriteNumber("unique_documents", report.UniqueDocuments);
            writer.WriteNumber("duplicate_documents", report.DuplicateDocuments);
            writer.WriteNumber("source_count", report.SourceCount);
            writer.WriteNumber("suppressed_unit_count", report.SuppressedUnitCount);
            writer.WriteStartArray("cohorts");
            foreach (CanaryAggregateCohort cohort in report.Cohorts)
            {
                writer.WriteStartObject();
                WriteIdentity(writer, cohort);
                WriteSuccess(writer, cohort.SuccessRate);
                WriteDiagnostics(writer, "control_diagnostics", cohort.ControlDiagnostics);
                WriteDiagnostics(writer, "treatment_diagnostics", cohort.TreatmentDiagnostics);
                WriteLatency(writer, cohort.WarmLatencyScreen);
                WriteShadow(writer, cohort.IdentifierShadow);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderHuman(CanaryAggregateReport report)
    {
        var output = new StringBuilder()
            .Append("canary v3 aggregate — documents ").Append(report.UniqueDocuments)
            .Append('/').Append(report.InputDocuments)
            .Append(", sources ").Append(report.SourceCount)
            .Append(", suppressed units ").Append(report.SuppressedUnitCount)
            .Append(". Warm latency is a bucket screen only; local raw-row gates remain authoritative.");
        if (report.Cohorts.Count == 0)
            return output.Append("\nno complete semantic-identity cohorts.").ToString();

        foreach (CanaryAggregateCohort cohort in report.Cohorts)
        {
            output.Append("\ncohort ").Append(cohort.MillerVersion)
                .Append(" [encoder=").Append(cohort.EncoderFingerprint)
                .Append(" schema=").Append(cohort.StorageSchema)
                .Append(" corpus=").Append(cohort.CorpusGeneration)
                .Append(" fusion=").Append(cohort.FusionProfile)
                .Append(" policy=").Append(cohort.PolicyVersion).Append(']');
            output.Append("\n  success-rate: ").Append(ClauseLabel(cohort.SuccessRate.Verdict))
                .Append(" — control units=").Append(cohort.SuccessRate.ControlUnits)
                .Append('/').Append(MinUnitsPerArm)
                .Append(" treatment units=").Append(cohort.SuccessRate.TreatmentUnits)
                .Append('/').Append(MinUnitsPerArm);
            long nonNoneFallbacks = cohort.TreatmentDiagnostics.FallbackReasonCounts
                .Where(static pair => pair.Key != CanaryFallbackReason.None).Sum(static pair => pair.Value);
            output.Append("\n  treatment diagnostics: semantic contribution calls=")
                .Append(cohort.TreatmentDiagnostics.SemanticContributionCalls)
                .Append('/').Append(cohort.TreatmentDiagnostics.Calls)
                .Append(" non-none fallbacks=").Append(nonNoneFallbacks)
                .Append('/').Append(cohort.TreatmentDiagnostics.Calls);
            output.Append("\n  warm-latency screen: ").Append(ScreenLabel(cohort.WarmLatencyScreen.Verdict))
                .Append(" — control rows=").Append(cohort.WarmLatencyScreen.ControlRows)
                .Append('/').Append(MinLatencyRows)
                .Append(" treatment warm rows=").Append(cohort.WarmLatencyScreen.TreatmentWarmRows)
                .Append('/').Append(MinLatencyRows);
            if (cohort.WarmLatencyScreen.ControlMedianP95Bucket is not null)
            {
                output.Append(" control median p95 bucket=").Append(cohort.WarmLatencyScreen.ControlMedianP95Bucket)
                    .Append(" treatment=").Append(cohort.WarmLatencyScreen.TreatmentMedianP95Bucket);
            }
            output.Append("\n  identifier-shadow: ").Append(ClauseLabel(cohort.IdentifierShadow.Verdict))
                .Append(" — units=").Append(cohort.IdentifierShadow.ShadowUnits)
                .Append('/').Append(MinShadowUnits);
        }
        return output.ToString();
    }

    private static void WriteIdentity(Utf8JsonWriter writer, CanaryAggregateCohort cohort)
    {
        writer.WriteString("miller_version", cohort.MillerVersion);
        writer.WriteString("encoder_fingerprint", cohort.EncoderFingerprint);
        writer.WriteString("storage_schema", cohort.StorageSchema);
        writer.WriteString("corpus_generation", cohort.CorpusGeneration);
        writer.WriteString("fusion_profile", cohort.FusionProfile);
        writer.WriteNumber("policy_version", cohort.PolicyVersion);
    }

    private static void WriteSuccess(Utf8JsonWriter writer, CanarySuccessRateClause clause)
    {
        writer.WriteStartObject("success_rate");
        writer.WriteString("verdict", ClauseLabel(clause.Verdict));
        writer.WriteNumber("control_units", clause.ControlUnits);
        writer.WriteNumber("treatment_units", clause.TreatmentUnits);
        writer.WriteNumber("min_units_per_arm", MinUnitsPerArm);
        WriteOptional(writer, "effect", clause.Effect);
        WriteOptional(writer, "ci_lower", clause.Lower);
        WriteOptional(writer, "ci_upper", clause.Upper);
        writer.WriteEndObject();
    }

    private static void WriteLatency(Utf8JsonWriter writer, CanaryWarmLatencyScreen screen)
    {
        writer.WriteStartObject("warm_latency_screen");
        writer.WriteString("kind", "screen");
        writer.WriteBoolean("authoritative", false);
        writer.WriteString("verdict", ScreenLabel(screen.Verdict));
        writer.WriteNumber("treatment_warm_rows", screen.TreatmentWarmRows);
        writer.WriteNumber("control_rows", screen.ControlRows);
        writer.WriteNumber("min_rows", MinLatencyRows);
        WriteOptional(writer, "treatment_median_p95_bucket", screen.TreatmentMedianP95Bucket);
        WriteOptional(writer, "control_median_p95_bucket", screen.ControlMedianP95Bucket);
        writer.WriteEndObject();
    }

    private static void WriteDiagnostics(
        Utf8JsonWriter writer,
        string name,
        CanaryAggregateArmDiagnostics diagnostics)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("units", diagnostics.Units);
        writer.WriteNumber("calls", diagnostics.Calls);
        writer.WriteNumber("attributed_success_calls", diagnostics.AttributedSuccessCalls);
        writer.WriteNumber("semantic_contribution_calls", diagnostics.SemanticContributionCalls);
        WriteCountMap(writer, "fallback_reason_counts", diagnostics.FallbackReasonCounts);
        WriteCountMap(writer, "rescue_kind_counts", diagnostics.RescueKindCounts);
        WriteCountMap(writer, "backend_counts", diagnostics.BackendCounts);
        WriteCountMap(writer, "embed_warmth_counts", diagnostics.EmbedWarmthCounts);
        WriteCountMap(writer, "embed_latency_bucket_counts", diagnostics.EmbedLatencyBucketCounts);
        WriteCountMap(writer, "knn_latency_bucket_counts", diagnostics.KnnLatencyBucketCounts);
        WriteCountMap(writer, "total_latency_bucket_counts", diagnostics.TotalLatencyBucketCounts);
        WriteCountMap(writer, "warm_total_latency_bucket_counts", diagnostics.WarmTotalLatencyBucketCounts);
        writer.WriteEndObject();
    }

    private static void WriteCountMap(
        Utf8JsonWriter writer,
        string name,
        IReadOnlyDictionary<string, long> counts)
    {
        writer.WriteStartObject(name);
        foreach (KeyValuePair<string, long> pair in counts.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            writer.WriteNumber(pair.Key, pair.Value);
        writer.WriteEndObject();
    }

    private static void WriteShadow(Utf8JsonWriter writer, CanaryShadowClause clause)
    {
        writer.WriteStartObject("identifier_shadow");
        writer.WriteString("verdict", ClauseLabel(clause.Verdict));
        writer.WriteNumber("shadow_units", clause.ShadowUnits);
        writer.WriteNumber("min_units", MinShadowUnits);
        writer.WriteNumber("top1_changed_margin", Top1ChangedMargin);
        writer.WriteNumber("overlap_at_10_floor", OverlapFloor);
        WriteOptional(writer, "top1_changed_ci_upper", clause.Top1ChangedUpper);
        WriteOptional(writer, "overlap_at_10_ci_lower", clause.OverlapAt10Lower);
        writer.WriteEndObject();
    }

    private static SemanticIdentity RequireIdentity(JsonElement unit)
    {
        string millerVersion = RequireNonemptyIdentity(unit, "miller_version");
        string encoder = RequireNonemptyIdentity(unit, "encoder_fingerprint");
        string schema = RequireNonemptyIdentity(unit, "storage_schema");
        string corpus = RequireNonemptyIdentity(unit, "corpus_generation");
        string fusion = RequireNonemptyIdentity(unit, "fusion_profile");
        int policy = RequireInt(unit, "policy_version", 1, int.MaxValue);
        return new SemanticIdentity(millerVersion, encoder, schema, corpus, fusion, policy);
    }

    private static string RequireNonemptyIdentity(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Invalid($"{name} must be a complete nonempty semantic identity value.");
        }
        return value.GetString()!;
    }

    private static Dictionary<string, long> ReadCountMap(
        JsonElement parent,
        string name,
        IReadOnlySet<string> allowed,
        long expectedTotal,
        bool requireExactTotal = true)
    {
        JsonElement map = RequireObject(parent.GetProperty(name), name);
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        long total = 0;
        foreach (JsonProperty property in map.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw Invalid($"{name} contains unknown key '{property.Name}'.");
            if (!result.TryAdd(property.Name, ReadPositiveCount(property.Value, name)))
                throw Invalid($"{name} contains duplicate key '{property.Name}'.");
            total = AddCount(total, result[property.Name], name);
        }
        if (requireExactTotal ? total != expectedTotal : total > expectedTotal)
        {
            string requirement = requireExactTotal ? "must sum to" : "cannot exceed";
            throw Invalid($"{name} counts {requirement} {expectedTotal}.");
        }
        return result;
    }

    private static Dictionary<int, long> ReadHistogram(
        JsonElement parent,
        string name,
        int minKey,
        int maxKey,
        long expectedTotal)
    {
        JsonElement map = RequireObject(parent.GetProperty(name), name);
        var result = new Dictionary<int, long>();
        long total = 0;
        foreach (JsonProperty property in map.EnumerateObject())
        {
            if (!int.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int key)
                || key < minKey || key > maxKey)
            {
                throw Invalid($"{name} contains out-of-range key '{property.Name}'.");
            }
            if (!result.TryAdd(key, ReadPositiveCount(property.Value, name)))
                throw Invalid($"{name} contains duplicate key '{property.Name}'.");
            total = AddCount(total, result[key], name);
        }
        if (total != expectedTotal)
            throw Invalid($"{name} counts must sum to {expectedTotal}.");
        return result;
    }

    private static long ReadPositiveCount(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long count) || count <= 0)
            throw Invalid($"{name} values must be positive integers.");
        return count;
    }

    private static long AddCount(long left, long right, string field)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException($"{field} count total is too large.", ex);
        }
    }

    private static DateOnly RequireContainedDate(JsonElement unit, DateOnly from, DateOnly to)
    {
        DateOnly date = RequireDate(unit, "utc_date");
        if (date < from || date > to)
            throw Invalid("unit utc_date must be contained in its export window.");
        return date;
    }

    private static DateOnly RequireDate(JsonElement element, string name)
    {
        string text = RequireString(element, name);
        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
            throw Invalid($"{name} must be an ISO UTC date.");
        return date;
    }

    private static string RequireUnitId(JsonElement unit)
    {
        string value = RequireString(unit, "unit_id");
        if (value.Length != 12 || value.Any(static c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw Invalid("unit_id must be exactly 12 lowercase hexadecimal characters.");
        return value;
    }

    private static string RequireString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw Invalid($"{name} must be a string.");
        return value.GetString()!;
    }

    private static int RequireNonnegativeInt(JsonElement element, string name) =>
        RequireInt(element, name, 0, int.MaxValue);

    private static int RequireInt(JsonElement element, string name, int minimum, int maximum)
    {
        if (!element.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out int number)
            || number < minimum
            || number > maximum)
        {
            throw Invalid($"{name} must be an integer from {minimum} through {maximum}.");
        }
        return number;
    }

    private static void RequireExactInt(JsonElement element, string name, int expected)
    {
        if (RequireInt(element, name, int.MinValue, int.MaxValue) != expected)
            throw Invalid($"{name} must be {expected}.");
    }

    private static JsonElement RequireObject(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw Invalid($"{name} must be an object.");
        return element;
    }

    private static void ValidateFields(
        JsonElement element,
        IReadOnlySet<string> allowed,
        IReadOnlySet<string> required,
        string name)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw Invalid($"{name} contains unknown field '{property.Name}'.");
            if (!seen.Add(property.Name))
                throw Invalid($"{name} contains duplicate field '{property.Name}'.");
        }
        foreach (string field in required)
        {
            if (!seen.Contains(field))
                throw Invalid($"{name} is missing required field '{field}'.");
        }
    }

    private static IReadOnlySet<string> Set(params string[] values) =>
        new HashSet<string>(values, StringComparer.Ordinal);

    private static IReadOnlySet<string> Set(IEnumerable<string> values) =>
        new HashSet<string>(values, StringComparer.Ordinal);

    private static Dictionary<string, int> ToIntCounts(IReadOnlyDictionary<string, long> counts) =>
        counts.ToDictionary(static pair => pair.Key, static pair => checked((int)pair.Value), StringComparer.Ordinal);

    private static Dictionary<string, long> SumMaps(
        IEnumerable<IReadOnlyDictionary<string, long>> maps)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (IReadOnlyDictionary<string, long> map in maps)
            Merge(result, map, "aggregate diagnostics");
        return result;
    }

    private static string MedianBucket(IReadOnlyList<string> buckets)
    {
        string[] ordered = buckets.OrderBy(BucketIndex).ToArray();
        return ordered[(ordered.Length - 1) / 2];
    }

    private static int BucketIndex(string bucket)
    {
        for (int i = 0; i < CanaryGateMath.LatencyLadder.Count; i++)
        {
            if (CanaryGateMath.LatencyLadder[i] == bucket)
                return i;
        }
        throw Invalid($"Unknown latency bucket '{bucket}'.");
    }

    private static string ClauseLabel(CanaryClauseVerdict verdict) => verdict switch
    {
        CanaryClauseVerdict.Pass => "pass",
        CanaryClauseVerdict.Fail => "fail",
        CanaryClauseVerdict.Underpowered => "underpowered",
        _ => "indeterminate",
    };

    private static string ScreenLabel(CanaryLatencyScreenVerdict verdict) => verdict switch
    {
        CanaryLatencyScreenVerdict.NoHigherBucket => "no_higher_bucket",
        CanaryLatencyScreenVerdict.PossibleRegression => "possible_regression",
        _ => "underpowered",
    };

    private static void WriteOptional(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number && !double.IsNaN(number) && !double.IsInfinity(number))
            writer.WriteNumber(name, number);
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
            writer.WriteString(name, value);
    }

    private static InvalidDataException Invalid(string message) => new(message);

    private sealed record ExportDocument(
        string Raw,
        string SourceId,
        DateOnly From,
        DateOnly To,
        int SuppressedUnitCount,
        IReadOnlyList<ExperimentUnit> Units,
        IReadOnlyList<ShadowUnit> ShadowUnits);

    private sealed record SourceWindow(string SourceId, DateOnly From, DateOnly To);

    private sealed record SemanticIdentity(
        string MillerVersion,
        string EncoderFingerprint,
        string StorageSchema,
        string CorpusGeneration,
        string FusionProfile,
        int PolicyVersion);

    private sealed record ExperimentUnit(
        string UnitId,
        DateOnly UtcDate,
        string QueryClass,
        string Arm,
        int Bucket,
        long Calls,
        long OkCalls,
        long EmptyCalls,
        long ErrorCalls,
        long AttributedSuccessCalls,
        long SemanticContributionCalls,
        SemanticIdentity Identity,
        Dictionary<string, long> FallbackCounts,
        Dictionary<string, long> RescueCounts,
        Dictionary<string, long> BackendCounts,
        Dictionary<string, long> WarmthCounts,
        Dictionary<string, long> EmbedLatencyCounts,
        Dictionary<string, long> KnnLatencyCounts,
        Dictionary<string, long> TotalLatencyCounts,
        Dictionary<string, long> WarmTotalLatencyCounts);

    private sealed record ShadowUnit(
        string UnitId,
        DateOnly UtcDate,
        string QueryClass,
        SemanticIdentity Identity,
        long Calls,
        Dictionary<string, long> StatusCounts,
        long Top1ChangedCalls,
        Dictionary<int, long> OverlapHistogram,
        Dictionary<int, long> LexicalRankHistogram);

    private sealed class MergedExperimentUnit
    {
        public MergedExperimentUnit(ExperimentUnit unit)
        {
            UnitId = unit.UnitId;
            UtcDate = unit.UtcDate;
            QueryClass = unit.QueryClass;
            Arm = unit.Arm;
            Bucket = unit.Bucket;
            Identity = unit.Identity;
            FallbackCounts = Copy(unit.FallbackCounts);
            RescueCounts = Copy(unit.RescueCounts);
            BackendCounts = Copy(unit.BackendCounts);
            WarmthCounts = Copy(unit.WarmthCounts);
            EmbedLatencyCounts = Copy(unit.EmbedLatencyCounts);
            KnnLatencyCounts = Copy(unit.KnnLatencyCounts);
            TotalLatencyCounts = Copy(unit.TotalLatencyCounts);
            WarmTotalLatencyCounts = Copy(unit.WarmTotalLatencyCounts);
            Calls = unit.Calls;
            OkCalls = unit.OkCalls;
            EmptyCalls = unit.EmptyCalls;
            ErrorCalls = unit.ErrorCalls;
            AttributedSuccessCalls = unit.AttributedSuccessCalls;
            SemanticContributionCalls = unit.SemanticContributionCalls;
        }

        public string UnitId { get; }
        public DateOnly UtcDate { get; }
        public string QueryClass { get; }
        public string Arm { get; }
        public int Bucket { get; }
        public SemanticIdentity Identity { get; }
        public long Calls { get; private set; }
        public long OkCalls { get; private set; }
        public long EmptyCalls { get; private set; }
        public long ErrorCalls { get; private set; }
        public long AttributedSuccessCalls { get; private set; }
        public long SemanticContributionCalls { get; private set; }
        public Dictionary<string, long> FallbackCounts { get; }
        public Dictionary<string, long> RescueCounts { get; }
        public Dictionary<string, long> BackendCounts { get; }
        public Dictionary<string, long> WarmthCounts { get; }
        public Dictionary<string, long> EmbedLatencyCounts { get; }
        public Dictionary<string, long> KnnLatencyCounts { get; }
        public Dictionary<string, long> TotalLatencyCounts { get; }
        public Dictionary<string, long> WarmTotalLatencyCounts { get; }
        public long WarmCalls => WarmTotalLatencyCounts.Values.Sum();

        public void Add(ExperimentUnit unit)
        {
            if (UtcDate != unit.UtcDate || QueryClass != unit.QueryClass || Arm != unit.Arm
                || Bucket != unit.Bucket || Identity != unit.Identity)
            {
                throw Invalid($"unit_id '{UnitId}' has conflicting identity or assignment fields.");
            }
            Calls = AddCount(Calls, unit.Calls, "calls");
            OkCalls = AddCount(OkCalls, unit.OkCalls, "ok_calls");
            EmptyCalls = AddCount(EmptyCalls, unit.EmptyCalls, "empty_calls");
            ErrorCalls = AddCount(ErrorCalls, unit.ErrorCalls, "error_calls");
            AttributedSuccessCalls = AddCount(
                AttributedSuccessCalls, unit.AttributedSuccessCalls, "attributed_success_calls");
            SemanticContributionCalls = AddCount(
                SemanticContributionCalls, unit.SemanticContributionCalls, "semantic_contribution_calls");
            Merge(FallbackCounts, unit.FallbackCounts, "fallback_reason_counts");
            Merge(RescueCounts, unit.RescueCounts, "rescue_kind_counts");
            Merge(BackendCounts, unit.BackendCounts, "backend_counts");
            Merge(WarmthCounts, unit.WarmthCounts, "embed_warmth_counts");
            Merge(EmbedLatencyCounts, unit.EmbedLatencyCounts, "embed_latency_bucket_counts");
            Merge(KnnLatencyCounts, unit.KnnLatencyCounts, "knn_latency_bucket_counts");
            Merge(TotalLatencyCounts, unit.TotalLatencyCounts, "total_latency_bucket_counts");
            Merge(WarmTotalLatencyCounts, unit.WarmTotalLatencyCounts, "warm_total_latency_bucket_counts");
        }
    }

    private sealed class MergedShadowUnit
    {
        public MergedShadowUnit(ShadowUnit unit)
        {
            UnitId = unit.UnitId;
            UtcDate = unit.UtcDate;
            QueryClass = unit.QueryClass;
            Identity = unit.Identity;
            Calls = unit.Calls;
            StatusCounts = Copy(unit.StatusCounts);
            Top1ChangedCalls = unit.Top1ChangedCalls;
            OverlapHistogram = Copy(unit.OverlapHistogram);
            LexicalRankHistogram = Copy(unit.LexicalRankHistogram);
        }

        public string UnitId { get; }
        public DateOnly UtcDate { get; }
        public string QueryClass { get; }
        public SemanticIdentity Identity { get; }
        public long Calls { get; private set; }
        public Dictionary<string, long> StatusCounts { get; }
        public long Top1ChangedCalls { get; private set; }
        public Dictionary<int, long> OverlapHistogram { get; }
        public Dictionary<int, long> LexicalRankHistogram { get; }
        public long OkCalls => StatusCounts.GetValueOrDefault(CanaryShadowStatus.Ok);

        public void Add(ShadowUnit unit)
        {
            if (UtcDate != unit.UtcDate || QueryClass != unit.QueryClass || Identity != unit.Identity)
                throw Invalid($"unit_id '{UnitId}' has conflicting shadow identity fields.");
            Calls = AddCount(Calls, unit.Calls, "calls");
            Top1ChangedCalls = AddCount(Top1ChangedCalls, unit.Top1ChangedCalls, "top1_changed_calls");
            Merge(StatusCounts, unit.StatusCounts, "shadow_status_counts");
            Merge(OverlapHistogram, unit.OverlapHistogram, "overlap_at_10_histogram");
            Merge(LexicalRankHistogram, unit.LexicalRankHistogram, "lexical_top1_rank_histogram");
        }
    }

    private static Dictionary<TKey, long> Copy<TKey>(IReadOnlyDictionary<TKey, long> source)
        where TKey : notnull => source.ToDictionary(static pair => pair.Key, static pair => pair.Value);

    private static void Merge<TKey>(
        Dictionary<TKey, long> target,
        IReadOnlyDictionary<TKey, long> source,
        string field)
        where TKey : notnull
    {
        foreach (KeyValuePair<TKey, long> pair in source)
            target[pair.Key] = AddCount(target.GetValueOrDefault(pair.Key), pair.Value, field);
    }
}
