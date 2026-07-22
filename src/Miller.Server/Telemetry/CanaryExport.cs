using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Miller.Core.Telemetry;

namespace Miller.Server.Telemetry;

/// <summary>
/// Builds the frozen aggregate envelope of <c>canary-telemetry-v2</c> §Aggregate Export — the only sanctioned way
/// canary data leaves a machine. Counters and enums only: no hashes, no <c>workspace_id</c>, no paths, no raw
/// <c>duration_ms</c> (bucketed <c>total_latency_bucket_counts</c> instead). Units are ordered by
/// <c>(utc_date, query_class, unit_id)</c> so an unchanged window re-exports byte-identically.
/// </summary>
public static class CanaryExport
{
    public const int SchemaVersion = 2;

    private static readonly IReadOnlyList<string> LatencyOrder =
        [.. CanaryGateMath.LatencyLadder, CanaryLatencyBucket.None];

    public static string BuildJson(string dbPath, DateOnly from, DateOnly to, DateTimeOffset generatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        IReadOnlyList<CanaryRow> allRows = CanaryLedgerReader.ReadCanaryRows(dbPath);
        IReadOnlyList<CanaryFollowUp> followUps = CanaryLedgerReader.ReadFollowUps(dbPath);
        IReadOnlySet<string> attributed = CanaryLedgerReader.AttributedRowIds(allRows, followUps);

        string fromText = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string toText = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        List<CanaryRow> windowed = allRows
            .Where(r => r.ContractVersion == CanaryTelemetry.ContractVersion
                && string.CompareOrdinal(r.UtcDate, fromText) >= 0
                && string.CompareOrdinal(r.UtcDate, toText) <= 0)
            .ToList();

        int suppressed = 0;

        var units = new List<ExperimentUnit>();
        foreach (var group in windowed
            .Where(r => r.ExperimentId == CanaryAssignment.HybridExperimentId
                && r.Arm is CanaryArm.Control or CanaryArm.Treatment)
            .GroupBy(AnalysisUnitKey.From))
        {
            List<CanaryRow> rows = [.. group];
            if (rows.Count < 5)
            {
                suppressed++;
                continue;
            }
            units.Add(BuildExperimentUnit(rows, attributed, group.Key));
        }

        var shadowUnits = new List<ShadowUnit>();
        foreach (var group in windowed
            .Where(r => r.ExperimentId == CanaryAssignment.IdentifierExperimentId && r.Arm == CanaryArm.Shadow)
            .GroupBy(AnalysisUnitKey.From))
        {
            List<CanaryRow> rows = [.. group];
            if (rows.Count < 5)
            {
                suppressed++;
                continue;
            }
            shadowUnits.Add(BuildShadowUnit(rows, group.Key));
        }

        units.Sort(static (x, y) => CompareUnitOrder(x.UtcDate, x.QueryClass, x.UnitId, y.UtcDate, y.QueryClass, y.UnitId));
        shadowUnits.Sort(static (x, y) => CompareUnitOrder(x.UtcDate, x.QueryClass, x.UnitId, y.UtcDate, y.QueryClass, y.UnitId));

        return Render(from, to, generatedAt, suppressed, units, shadowUnits);
    }

    private static ExperimentUnit BuildExperimentUnit(
        List<CanaryRow> rows,
        IReadOnlySet<string> attributed,
        AnalysisUnitKey key)
    {
        CanaryRow first = rows[0];
        string unitId = UnitId(first.ExperimentId!, key);

        return new ExperimentUnit(
            UnitId: unitId,
            UtcDate: first.UtcDate,
            QueryClass: first.QueryClass!,
            Arm: first.Arm!,
            Bucket: first.Bucket ?? 0,
            Calls: rows.Count,
            OkCalls: rows.Count(r => r.Outcome == "ok"),
            EmptyCalls: rows.Count(r => r.Outcome == "empty"),
            ErrorCalls: rows.Count(r => r.Outcome == "error"),
            AttributedSuccessCalls: rows.Count(r => r.Outcome == "ok" && r.ResultCount > 0 && attributed.Contains(r.Id)),
            SemanticContributionCalls: rows.Count(r => r.SemanticContributionCount.GetValueOrDefault() > 0),
            MillerVersion: key.MillerVersion,
            EncoderFingerprint: key.EncoderFingerprint,
            StorageSchema: key.StorageSchema,
            CorpusGeneration: key.CorpusGeneration,
            FusionProfile: key.FusionProfile,
            PolicyVersion: key.PolicyVersion,
            FallbackReasonCounts: Counts(rows, r => r.FallbackReason),
            RescueKindCounts: Counts(rows, r => r.RescueKind),
            BackendCounts: Counts(rows, r => r.Backend),
            EmbedWarmthCounts: Counts(rows, r => r.EmbedWarmth),
            EmbedLatencyBucketCounts: Counts(rows, r => r.EmbedLatencyBucket),
            KnnLatencyBucketCounts: Counts(rows, r => r.KnnLatencyBucket),
            TotalLatencyBucketCounts: Counts(rows, r => CanaryLatencyBucket.For(r.DurationMs)));
    }

    private static ShadowUnit BuildShadowUnit(List<CanaryRow> rows, AnalysisUnitKey key)
    {
        CanaryRow first = rows[0];
        string unitId = UnitId(first.ExperimentId!, key);

        return new ShadowUnit(
            UnitId: unitId,
            UtcDate: first.UtcDate,
            QueryClass: first.QueryClass!,
            MillerVersion: key.MillerVersion,
            EncoderFingerprint: key.EncoderFingerprint,
            StorageSchema: key.StorageSchema,
            CorpusGeneration: key.CorpusGeneration,
            FusionProfile: key.FusionProfile,
            PolicyVersion: key.PolicyVersion,
            Calls: rows.Count,
            ShadowStatusCounts: Counts(rows, r => r.ShadowStatus),
            Top1ChangedCalls: rows.Count(r => r.ShadowTop1Changed == true),
            OverlapAt10Histogram: Histogram(rows, r => r.ShadowOverlapAt10),
            LexicalTop1RankHistogram: Histogram(rows, r => r.ShadowLexicalTop1Rank));
    }

    private static string Render(
        DateOnly from, DateOnly to, DateTimeOffset generatedAt, int suppressed,
        List<ExperimentUnit> units, List<ShadowUnit> shadowUnits)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema_version", SchemaVersion);
            w.WriteNumber("canary_contract_version", CanaryTelemetry.ContractVersion);
            w.WriteString("experiment_id", CanaryAssignment.HybridExperimentId);
            w.WriteString("generated_at_utc", generatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            w.WriteStartObject("window");
            w.WriteString("from_utc", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            w.WriteString("to_utc", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            w.WriteEndObject();
            w.WriteNumber("suppressed_unit_count", suppressed);

            w.WriteStartArray("units");
            foreach (ExperimentUnit unit in units)
                WriteExperimentUnit(w, unit);
            w.WriteEndArray();

            w.WriteStartArray("shadow_units");
            foreach (ShadowUnit unit in shadowUnits)
                WriteShadowUnit(w, unit);
            w.WriteEndArray();

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteExperimentUnit(Utf8JsonWriter w, ExperimentUnit unit)
    {
        w.WriteStartObject();
        w.WriteString("unit_id", unit.UnitId);
        w.WriteString("utc_date", unit.UtcDate);
        w.WriteString("query_class", unit.QueryClass);
        w.WriteString("arm", unit.Arm);
        w.WriteNumber("bucket", unit.Bucket);
        w.WriteNumber("calls", unit.Calls);
        w.WriteNumber("ok_calls", unit.OkCalls);
        w.WriteNumber("empty_calls", unit.EmptyCalls);
        w.WriteNumber("error_calls", unit.ErrorCalls);
        w.WriteNumber("attributed_success_calls", unit.AttributedSuccessCalls);
        w.WriteNumber("semantic_contribution_calls", unit.SemanticContributionCalls);
        WriteNullableString(w, "miller_version", unit.MillerVersion);
        WriteNullableString(w, "encoder_fingerprint", unit.EncoderFingerprint);
        WriteNullableString(w, "storage_schema", unit.StorageSchema);
        WriteNullableString(w, "corpus_generation", unit.CorpusGeneration);
        WriteNullableString(w, "fusion_profile", unit.FusionProfile);
        WriteNullableNumber(w, "policy_version", unit.PolicyVersion);
        WriteCountMap(w, "fallback_reason_counts", unit.FallbackReasonCounts, CanaryFallbackReason.All);
        WriteCountMap(w, "rescue_kind_counts", unit.RescueKindCounts, CanaryRescueKind.All);
        WriteCountMap(w, "backend_counts", unit.BackendCounts, CanaryBackend.All);
        WriteCountMap(w, "embed_warmth_counts", unit.EmbedWarmthCounts, CanaryEmbedWarmth.All);
        WriteCountMap(w, "embed_latency_bucket_counts", unit.EmbedLatencyBucketCounts, LatencyOrder);
        WriteCountMap(w, "knn_latency_bucket_counts", unit.KnnLatencyBucketCounts, LatencyOrder);
        WriteCountMap(w, "total_latency_bucket_counts", unit.TotalLatencyBucketCounts, LatencyOrder);
        w.WriteEndObject();
    }

    private static void WriteShadowUnit(Utf8JsonWriter w, ShadowUnit unit)
    {
        w.WriteStartObject();
        w.WriteString("unit_id", unit.UnitId);
        w.WriteString("utc_date", unit.UtcDate);
        w.WriteString("query_class", unit.QueryClass);
        WriteNullableString(w, "miller_version", unit.MillerVersion);
        WriteNullableString(w, "encoder_fingerprint", unit.EncoderFingerprint);
        WriteNullableString(w, "storage_schema", unit.StorageSchema);
        WriteNullableString(w, "corpus_generation", unit.CorpusGeneration);
        WriteNullableString(w, "fusion_profile", unit.FusionProfile);
        WriteNullableNumber(w, "policy_version", unit.PolicyVersion);
        w.WriteNumber("calls", unit.Calls);
        WriteCountMap(w, "shadow_status_counts", unit.ShadowStatusCounts, CanaryShadowStatus.All);
        w.WriteNumber("top1_changed_calls", unit.Top1ChangedCalls);
        WriteHistogram(w, "overlap_at_10_histogram", unit.OverlapAt10Histogram, 0, 10);
        WriteHistogram(w, "lexical_top1_rank_histogram", unit.LexicalTop1RankHistogram, 0, 50);
        w.WriteEndObject();
    }

    private static void WriteCountMap(
        Utf8JsonWriter w, string name, IReadOnlyDictionary<string, int> counts, IReadOnlyList<string> order)
    {
        w.WriteStartObject(name);
        foreach (string key in order)
        {
            if (counts.TryGetValue(key, out int count) && count > 0)
                w.WriteNumber(key, count);
        }
        w.WriteEndObject();
    }

    private static void WriteHistogram(
        Utf8JsonWriter w, string name, IReadOnlyDictionary<int, int> histogram, int minKey, int maxKey)
    {
        w.WriteStartObject(name);
        foreach (KeyValuePair<int, int> pair in histogram.OrderBy(p => p.Key))
        {
            if (pair.Value > 0 && pair.Key >= minKey && pair.Key <= maxKey)
                w.WriteNumber(pair.Key.ToString(CultureInfo.InvariantCulture), pair.Value);
        }
        w.WriteEndObject();
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

    private static Dictionary<string, int> Counts(IReadOnlyList<CanaryRow> rows, Func<CanaryRow, string?> selector)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (CanaryRow row in rows)
        {
            if (selector(row) is { } value)
                counts[value] = counts.TryGetValue(value, out int existing) ? existing + 1 : 1;
        }
        return counts;
    }

    private static Dictionary<int, int> Histogram(IReadOnlyList<CanaryRow> rows, Func<CanaryRow, int?> selector)
    {
        var histogram = new Dictionary<int, int>();
        foreach (CanaryRow row in rows)
        {
            if (selector(row) is { } value)
                histogram[value] = histogram.TryGetValue(value, out int existing) ? existing + 1 : 1;
        }
        return histogram;
    }

    private static int CompareUnitOrder(
        string dateX, string classX, string idX, string dateY, string classY, string idY)
    {
        int byDate = string.CompareOrdinal(dateX, dateY);
        if (byDate != 0)
            return byDate;
        int byClass = string.CompareOrdinal(classX, classY);
        return byClass != 0 ? byClass : string.CompareOrdinal(idX, idY);
    }

    private static string UnitId(string experimentId, AnalysisUnitKey key)
    {
        string material = string.Concat(
            Part(experimentId),
            Part(CanaryAssignment.AssignmentVersion.ToString(CultureInfo.InvariantCulture)),
            Part(key.WorkspaceId),
            Part(key.UtcDate),
            Part(key.QueryClass),
            Part(key.Arm),
            Part(key.Bucket?.ToString(CultureInfo.InvariantCulture)),
            Part(key.MillerVersion),
            Part(key.EncoderFingerprint),
            Part(key.StorageSchema),
            Part(key.CorpusGeneration),
            Part(key.FusionProfile),
            Part(key.PolicyVersion?.ToString(CultureInfo.InvariantCulture)));
        string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        return digest[..12];
    }

    private static string Part(string? value) => value is null ? "-1:" : $"{value.Length}:{value}";

    private sealed record AnalysisUnitKey(
        string? WorkspaceId,
        string UtcDate,
        string? QueryClass,
        string? Arm,
        int? Bucket,
        string? MillerVersion,
        string? EncoderFingerprint,
        string? StorageSchema,
        string? CorpusGeneration,
        string? FusionProfile,
        int? PolicyVersion)
    {
        public static AnalysisUnitKey From(CanaryRow row) => new(
            row.WorkspaceId,
            row.UtcDate,
            row.QueryClass,
            row.Arm,
            row.Bucket,
            row.MillerVersion,
            row.EncoderFingerprint,
            row.StorageSchema,
            row.CorpusGeneration,
            row.FusionProfile,
            row.PolicyVersion);
    }

    private sealed record ExperimentUnit(
        string UnitId,
        string UtcDate,
        string QueryClass,
        string Arm,
        int Bucket,
        int Calls,
        int OkCalls,
        int EmptyCalls,
        int ErrorCalls,
        int AttributedSuccessCalls,
        int SemanticContributionCalls,
        string? MillerVersion,
        string? EncoderFingerprint,
        string? StorageSchema,
        string? CorpusGeneration,
        string? FusionProfile,
        int? PolicyVersion,
        IReadOnlyDictionary<string, int> FallbackReasonCounts,
        IReadOnlyDictionary<string, int> RescueKindCounts,
        IReadOnlyDictionary<string, int> BackendCounts,
        IReadOnlyDictionary<string, int> EmbedWarmthCounts,
        IReadOnlyDictionary<string, int> EmbedLatencyBucketCounts,
        IReadOnlyDictionary<string, int> KnnLatencyBucketCounts,
        IReadOnlyDictionary<string, int> TotalLatencyBucketCounts);

    private sealed record ShadowUnit(
        string UnitId,
        string UtcDate,
        string QueryClass,
        string? MillerVersion,
        string? EncoderFingerprint,
        string? StorageSchema,
        string? CorpusGeneration,
        string? FusionProfile,
        int? PolicyVersion,
        int Calls,
        IReadOnlyDictionary<string, int> ShadowStatusCounts,
        int Top1ChangedCalls,
        IReadOnlyDictionary<int, int> OverlapAt10Histogram,
        IReadOnlyDictionary<int, int> LexicalTop1RankHistogram);
}
