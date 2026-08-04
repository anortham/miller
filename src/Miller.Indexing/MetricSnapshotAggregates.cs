using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Miller.Core.Search;

namespace Miller.Indexing;

/// <summary>
/// The metric-history "cheap arm": a pure, read-only aggregate reader over a workspace <c>symbols.db</c> plus the
/// single best-effort call the leader's converge path makes to record a <c>source='converge'</c> snapshot. Every
/// metric is a language-agnostic rollup of already-shipped julie facts — no new extraction, no git, no file scan —
/// so it is safe to run inside the leader's ops gate as long as it never throws or blocks (see
/// <see cref="RecordConverge"/>). Design: docs/plans/2026-07-07-metric-history-design.md ("Cheap arm").
///
/// <para>The absent-vs-zero rule (design amendment #1) is load-bearing: a metric whose SOURCE is unavailable is an
/// ABSENT row, never a 0. Complexity facts empty for this workspace ⟹ no complexity metrics. A count that genuinely
/// evaluates to 0 (zero clone groups or zero producer-owned marker facts) IS recorded — the source was available
/// and the answer was 0.</para>
/// </summary>
public static class MetricSnapshotAggregates
{
    /// <summary>The <c>snapshots.source</c> value for the leader's automatic converge snapshot.</summary>
    public const string ConvergeSource = "converge";

    // Canonical metric names — shared with the CLI/dashboard read surfaces so producer and consumer never drift.
    public const string SymbolCount = "symbol_count";
    public const string FileCount = "file_count";
    public const string LanguageCount = "language_count";
    public const string ComplexityP50 = "complexity_p50";
    public const string ComplexityP90 = "complexity_p90";
    public const string ComplexityMax = "complexity_max";
    public const string CloneGroupCount = "clone_group_count";
    public const string MarkerTotal = "marker_total";

    private static readonly string[] MarkerNames = { "TODO", "FIXME", "HACK", "XXX" };
    /// <summary>
    /// Read the converge metric set from <paramref name="symbolsDbPath"/>, including marker counts from the
    /// producer-owned <c>code.marker.v1</c> structural facts — except against a symbols-level artifact, whose
    /// <c>structural_facts</c> table has not been extracted yet and so has no marker answer to record.
    /// </summary>
    public static IReadOnlyList<MetricHistoryPoint> ReadConvergeMetrics(
        string symbolsDbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolsDbPath);

        var metrics = new List<MetricHistoryPoint>();
        using (SqliteConnection connection = SqliteReadOnlyAccess.Open(symbolsDbPath))
        {
            JulieSchemaGate.Verify(connection);
            AddSymbolCounts(connection, metrics);
            AddCloneGroupCount(connection, metrics);
            AddComplexityPercentiles(connection, metrics);
            // Facts-derived metrics have no source at symbols level: `structural_facts` is EMPTY there, so a marker
            // count reads 0 for "not extracted yet" rather than "no markers". history.db is APPEND-ONLY, so that 0
            // would stay a fabricated trend point long after the artifact upgraded — a gap the reader renders as
            // `-` is the honest answer (metrics-history-v1: absent, never a fabricated 0). Fails open to full like
            // every other level read.
            if (!IndexLevels.IsSymbolsLevel(ExtractIndexLevelReader.Read(connection)))
                AddMarkerCounts(connection, metrics);
        }

        return metrics;
    }

    /// <summary>
    /// The leader converge arm's single call site: read the artifact identity + aggregates and append one
    /// <c>source='converge'</c> snapshot to <c>&lt;.miller&gt;/history.db</c>. Best-effort by contract — this NEVER
    /// throws and NEVER blocks (the store's <see cref="MetricHistoryStore.RecordConverge"/> is skip-on-busy), so the
    /// caller may invoke it inside the ops gate without a guard. Returns the write result, or <c>null</c> when
    /// nothing was recorded (no revision/workspace/artifact identity, no metrics, or a caught failure). The optional
    /// <paramref name="onError"/> receives any swallowed exception for logging; <paramref name="recordedAtUtc"/> is a
    /// test seam.
    /// </summary>
    public static MetricHistoryWriteResult? RecordConverge(
        string symbolsDbPath,
        string? workspaceId,
        long revision,
        string millerVersion,
        Action<Exception>? onError = null,
        DateTime? recordedAtUtc = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(symbolsDbPath)
                || revision <= 0
                || string.IsNullOrWhiteSpace(workspaceId))
                return null;

            (string? artifactId, string? extractorVersion) = ReadIdentity(symbolsDbPath);
            if (string.IsNullOrWhiteSpace(artifactId))
                return null; // no stable artifact identity ⟹ nothing to key/dedup this snapshot on.

            IReadOnlyList<MetricHistoryPoint> metrics = ReadConvergeMetrics(symbolsDbPath);
            if (metrics.Count == 0)
                return null;

            var snapshot = new MetricHistorySnapshot(
                WorkspaceId: workspaceId!,
                ArtifactId: artifactId!,
                Revision: revision,
                ExtractorVersion: extractorVersion ?? string.Empty,
                MillerVersion: millerVersion ?? string.Empty,
                Source: ConvergeSource,
                Metrics: metrics);

            return MetricHistoryStore.RecordConverge(HistoryDbPathFor(symbolsDbPath), snapshot, recordedAtUtc);
        }
        catch (Exception ex)
        {
            // Telemetry is never a freshness invariant: swallow EVERYTHING so a history hiccup can never delay or
            // fail indexing. A skipped snapshot is an absent trend point, which the read side already tolerates.
            onError?.Invoke(ex);
            return null;
        }
    }

    /// <summary>The <c>history.db</c> path sibling to <paramref name="symbolsDbPath"/> inside the same <c>.miller</c>.</summary>
    public static string HistoryDbPathFor(string symbolsDbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolsDbPath);
        string full = Path.GetFullPath(symbolsDbPath);
        string dir = Path.GetDirectoryName(full)
            ?? throw new ArgumentException($"Path has no directory: {symbolsDbPath}", nameof(symbolsDbPath));
        return Path.Combine(dir, MetricHistoryStore.HistoryDbFileName);
    }

    // ---- internals -------------------------------------------------------------------------------------------

    // artifact_id (dedup identity) + binary_version (extractor version) from artifact_metadata, one shared read-only
    // connection. Mirrors FreshnessReader.ArtifactId / ExtractBinaryVersionReader; both are null on a legacy/absent
    // table, which RecordConverge treats as "cannot attribute" (artifact_id) or "unknown" (extractor version).
    private static (string? ArtifactId, string? ExtractorVersion) ReadIdentity(string symbolsDbPath)
    {
        using SqliteConnection connection = SqliteReadOnlyAccess.Open(symbolsDbPath);
        string? artifactId = ReadMetaValue(connection, "artifact_id");
        string? extractorVersion = ExtractBinaryVersionReader.TryRead(connection);
        return (artifactId, extractorVersion);
    }

    private static string? ReadMetaValue(SqliteConnection connection, string key)
    {
        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM artifact_metadata WHERE key = $k;";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            return null; // pre-v1 artifact without artifact_metadata.
        }
    }

    // symbol_count / file_count / language_count — the same COUNT shape WorkspaceIndexFactsReader.ReadSymbolCounts
    // uses (name IS NOT NULL, DISTINCT path/language). Always emitted: a 0 here is a genuine value, not absence.
    private static void AddSymbolCounts(SqliteConnection connection, List<MetricHistoryPoint> metrics)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*), COUNT(DISTINCT path), COUNT(DISTINCT language)
            FROM symbols WHERE name IS NOT NULL;
            """;
        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
            return;

        metrics.Add(new MetricHistoryPoint(SymbolCount, reader.GetInt64(0), null));
        metrics.Add(new MetricHistoryPoint(FileCount, reader.GetInt64(1), null));
        metrics.Add(new MetricHistoryPoint(LanguageCount, reader.GetInt64(2), null));
    }

    // clone_group_count — count of body_hash groups with >= 2 members, the same grouping CloneGroupReader.Read uses
    // (non-empty body_hash, minCount=2). Always emitted; 0 clone groups is a real value.
    private static void AddCloneGroupCount(SqliteConnection connection, List<MetricHistoryPoint> metrics)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM (
                SELECT body_hash
                FROM symbols
                WHERE body_hash IS NOT NULL AND body_hash != ''
                GROUP BY body_hash
                HAVING COUNT(*) >= 2
            );
            """;
        long count = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
        metrics.Add(new MetricHistoryPoint(CloneGroupCount, count, null));
    }

    // complexity_p50 / p90 / max over complexity_metrics.decision_count (the cyclomatic-style scalar the complexity
    // command ranks by). ABSENT when the table has no rows (or is missing on a legacy artifact) — per the
    // absent-vs-zero rule, empty complexity facts emit no complexity metrics rather than three zeros.
    private static void AddComplexityPercentiles(SqliteConnection connection, List<MetricHistoryPoint> metrics)
    {
        var decisions = new List<int>();
        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT decision_count FROM complexity_metrics ORDER BY decision_count;";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                    decisions.Add(reader.GetInt32(0));
            }
        }
        catch (SqliteException)
        {
            return; // no complexity_metrics table (older artifact) ⟹ absent, not zero.
        }

        if (decisions.Count == 0)
            return;

        metrics.Add(new MetricHistoryPoint(ComplexityP50, Percentile(decisions, 0.50), null));
        metrics.Add(new MetricHistoryPoint(ComplexityP90, Percentile(decisions, 0.90), null));
        metrics.Add(new MetricHistoryPoint(ComplexityMax, decisions[^1], null));
    }

    // Linear-interpolation percentile (type-7 / Excel PERCENTILE.INC) over an ASCENDING-sorted list.
    private static double Percentile(IReadOnlyList<int> sortedAsc, double p)
    {
        int n = sortedAsc.Count;
        if (n == 1)
            return sortedAsc[0];

        double rank = (n - 1) * p;
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        if (lo == hi)
            return sortedAsc[lo];
        return sortedAsc[lo] + ((sortedAsc[hi] - sortedAsc[lo]) * (rank - lo));
    }

    private static void AddMarkerCounts(SqliteConnection connection, List<MetricHistoryPoint> metrics)
    {
        var perMarker = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (string marker in MarkerNames)
            perMarker[marker] = 0;

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT UPPER(json_extract(metadata_json, '$.marker')), COUNT(*)
            FROM structural_facts
            WHERE pattern_id = $pattern
              AND json_valid(metadata_json)
              AND json_type(metadata_json, '$.marker') = 'text'
              AND UPPER(json_extract(metadata_json, '$.marker')) IN ('TODO', 'FIXME', 'HACK', 'XXX')
            GROUP BY UPPER(json_extract(metadata_json, '$.marker'));
            """;
        command.Parameters.AddWithValue("$pattern", MarkerFactReader.PatternId);
        using SqliteDataReader reader = command.ExecuteReader();
        long total = 0;
        while (reader.Read())
        {
            string marker = reader.GetString(0);
            long count = reader.GetInt64(1);
            perMarker[marker] = count;
            total += count;
        }

        metrics.Add(new MetricHistoryPoint(MarkerTotal, total, BuildMarkerDetailJson(perMarker)));
    }

    private static string BuildMarkerDetailJson(IReadOnlyDictionary<string, long> perMarker)
    {
        // Fixed, alnum-only marker keys ⟹ no JSON escaping needed; emit in canonical MarkerNames order.
        var sb = new StringBuilder("{");
        for (int i = 0; i < MarkerNames.Length; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append('"').Append(MarkerNames[i]).Append("\":")
                .Append(perMarker[MarkerNames[i]].ToString(CultureInfo.InvariantCulture));
        }
        return sb.Append('}').ToString();
    }

    // Whole-word marker match (mirror of MarkerSearch.ContainsMarker): rejects substrings like "TODOLIST".
    private static bool ContainsMarker(string text, string marker)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        int start = 0;
        while (start < text.Length)
        {
            int index = text.IndexOf(marker, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;

            int before = index - 1;
            int after = index + marker.Length;
            bool leftBoundary = before < 0 || !IsMarkerWordChar(text[before]);
            bool rightBoundary = after >= text.Length || !IsMarkerWordChar(text[after]);
            if (leftBoundary && rightBoundary)
                return true;

            start = index + marker.Length;
        }
        return false;
    }

    private static bool IsMarkerWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
