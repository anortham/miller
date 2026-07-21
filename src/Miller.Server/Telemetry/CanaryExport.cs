using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Miller.Core.Telemetry;

namespace Miller.Server.Telemetry;

/// <summary>
/// Builds the frozen aggregate envelope of <c>canary-telemetry-v1</c> §Aggregate Export — the only sanctioned way
/// canary data leaves a machine. Counters and enums only: no hashes, no <c>workspace_id</c>, no paths, no raw
/// <c>duration_ms</c> (bucketed <c>total_latency_bucket_counts</c> instead). Units are ordered by
/// <c>(utc_date, query_class, unit_id)</c> so an unchanged window re-exports byte-identically.
/// </summary>
public static class CanaryExport
{
    public const int SchemaVersion = 1;

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
            .Where(r => r.ContractVersion == 1
                && string.CompareOrdinal(r.UtcDate, fromText) >= 0
                && string.CompareOrdinal(r.UtcDate, toText) <= 0)
            .ToList();

        int suppressed = 0;

        var units = new List<ExperimentUnit>();
        foreach (var group in windowed
            .Where(r => r.ExperimentId == CanaryAssignment.HybridExperimentId
                && r.Arm is CanaryArm.Control or CanaryArm.Treatment)
            .GroupBy(r => (r.WorkspaceId, r.UtcDate, r.QueryClass)))
        {
            List<CanaryRow> rows = [.. group];
            if (rows.Count < 5)
            {
                suppressed++;
                continue;
            }
            units.Add(BuildExperimentUnit(rows, attributed));
        }

        var shadowUnits = new List<ShadowUnit>();
        foreach (var group in windowed
            .Where(r => r.ExperimentId == CanaryAssignment.IdentifierExperimentId && r.Arm == CanaryArm.Shadow)
            .GroupBy(r => (r.WorkspaceId, r.UtcDate, r.QueryClass)))
        {
            List<CanaryRow> rows = [.. group];
            if (rows.Count < 5)
            {
                suppressed++;
                continue;
            }
            shadowUnits.Add(BuildShadowUnit(rows));
        }

        units.Sort(static (x, y) => CompareUnitOrder(x.UtcDate, x.QueryClass, x.UnitId, y.UtcDate, y.QueryClass, y.UnitId));
        shadowUnits.Sort(static (x, y) => CompareUnitOrder(x.UtcDate, x.QueryClass, x.UnitId, y.UtcDate, y.QueryClass, y.UnitId));

        return Render(from, to, generatedAt, suppressed, units, shadowUnits);
    }

    private static ExperimentUnit BuildExperimentUnit(List<CanaryRow> rows, IReadOnlySet<string> attributed)
    {
        CanaryRow first = rows[0];
        string unitId = UnitId(first.ExperimentId!, first.WorkspaceId!, first.UtcDate, first.QueryClass!);

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
            EncoderFingerprint: FirstNonNull(rows, r => r.EncoderFingerprint),
            StorageSchema: FirstNonNull(rows, r => r.StorageSchema),
            CorpusGeneration: FirstNonNull(rows, r => r.CorpusGeneration),
            FusionProfile: FirstNonNull(rows, r => r.FusionProfile),
            PolicyVersion: first.PolicyVersion ?? 0,
            MillerVersions: [.. rows.Select(r => r.MillerVersion).Where(v => v is not null).Distinct().OrderBy(v => v, StringComparer.Ordinal)!],
            FallbackReasonCounts: Counts(rows, r => r.FallbackReason),
            RescueKindCounts: Counts(rows, r => r.RescueKind),
            BackendCounts: Counts(rows, r => r.Backend),
            EmbedWarmthCounts: Counts(rows, r => r.EmbedWarmth),
            EmbedLatencyBucketCounts: Counts(rows, r => r.EmbedLatencyBucket),
            KnnLatencyBucketCounts: Counts(rows, r => r.KnnLatencyBucket),
            TotalLatencyBucketCounts: Counts(rows, r => CanaryLatencyBucket.For(r.DurationMs)));
    }

    private static ShadowUnit BuildShadowUnit(List<CanaryRow> rows)
    {
        CanaryRow first = rows[0];
        string unitId = UnitId(first.ExperimentId!, first.WorkspaceId!, first.UtcDate, first.QueryClass!);

        return new ShadowUnit(
            UnitId: unitId,
            UtcDate: first.UtcDate,
            QueryClass: first.QueryClass!,
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
        WriteOptionalString(w, "encoder_fingerprint", unit.EncoderFingerprint);
        WriteOptionalString(w, "storage_schema", unit.StorageSchema);
        WriteOptionalString(w, "corpus_generation", unit.CorpusGeneration);
        WriteOptionalString(w, "fusion_profile", unit.FusionProfile);
        w.WriteNumber("policy_version", unit.PolicyVersion);
        w.WriteStartArray("miller_versions");
        foreach (string version in unit.MillerVersions)
            w.WriteStringValue(version);
        w.WriteEndArray();
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
        w.WriteNumber("calls", unit.Calls);
        WriteCountMap(w, "shadow_status_counts", unit.ShadowStatusCounts, CanaryShadowStatus.All);
        w.WriteNumber("top1_changed_calls", unit.Top1ChangedCalls);
        WriteHistogram(w, "overlap_at_10_histogram", unit.OverlapAt10Histogram);
        WriteHistogram(w, "lexical_top1_rank_histogram", unit.LexicalTop1RankHistogram);
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
        foreach (KeyValuePair<string, int> pair in counts
            .Where(p => p.Value > 0 && !order.Contains(p.Key))
            .OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            w.WriteNumber(pair.Key, pair.Value);
        }
        w.WriteEndObject();
    }

    private static void WriteHistogram(Utf8JsonWriter w, string name, IReadOnlyDictionary<int, int> histogram)
    {
        w.WriteStartObject(name);
        foreach (KeyValuePair<int, int> pair in histogram.OrderBy(p => p.Key))
        {
            if (pair.Value > 0)
                w.WriteNumber(pair.Key.ToString(CultureInfo.InvariantCulture), pair.Value);
        }
        w.WriteEndObject();
    }

    private static void WriteOptionalString(Utf8JsonWriter w, string name, string? value)
    {
        if (value is not null)
            w.WriteString(name, value);
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

    private static string? FirstNonNull(IReadOnlyList<CanaryRow> rows, Func<CanaryRow, string?> selector)
    {
        foreach (CanaryRow row in rows)
        {
            if (selector(row) is { } value)
                return value;
        }
        return null;
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

    private static string UnitId(string experimentId, string workspaceId, string utcDate, string queryClass)
    {
        string key = $"{experimentId}|{CanaryAssignment.AssignmentVersion}|{workspaceId}|{utcDate}|{queryClass}";
        string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return digest[..12];
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
        string? EncoderFingerprint,
        string? StorageSchema,
        string? CorpusGeneration,
        string? FusionProfile,
        int PolicyVersion,
        IReadOnlyList<string> MillerVersions,
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
        int Calls,
        IReadOnlyDictionary<string, int> ShadowStatusCounts,
        int Top1ChangedCalls,
        IReadOnlyDictionary<int, int> OverlapAt10Histogram,
        IReadOnlyDictionary<int, int> LexicalTop1RankHistogram);
}
