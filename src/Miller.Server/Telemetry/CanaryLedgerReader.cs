using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Miller.Server.Telemetry;

/// <summary>One canary/shadow <c>tool_telemetry</c> row with its reused columns and parsed <c>canary_*</c> metadata.</summary>
public sealed record CanaryRow(
    string Id,
    string Ts,
    string? WorkspaceId,
    string? Op,
    long DurationMs,
    string Outcome,
    int ResultCount,
    string? MillerVersion,
    int? ContractVersion,
    string? ExperimentId,
    string? Arm,
    string? Eligibility,
    string? QueryClass,
    int? Bucket,
    int? PolicyVersion,
    string? EmbedWarmth,
    string? FallbackReason,
    string? Backend,
    string? RescueKind,
    string? EmbedLatencyBucket,
    string? KnnLatencyBucket,
    int? SemanticContributionCount,
    string? EncoderFingerprint,
    string? StorageSchema,
    string? CorpusGeneration,
    string? FusionProfile,
    IReadOnlyList<string> ResultNameHashes,
    IReadOnlyList<string> ResultPathHashes,
    IReadOnlyList<string> ResultQualifiedHashes,
    string? ShadowStatus,
    int? ShadowOverlapAt10,
    bool? ShadowTop1Changed,
    int? ShadowLexicalTop1Rank,
    long Sequence)
{
    /// <summary>The <c>YYYY-MM-DD</c> UTC prefix of <see cref="Ts"/> — the assignment unit's date component.</summary>
    public string UtcDate => Ts.Length >= 10 ? Ts[..10] : Ts;
}

/// <summary>A follow-up candidate row for attribution: an <c>inspect</c> or <c>content read</c> that succeeded.</summary>
public sealed record CanaryFollowUp(string Ts, string? WorkspaceId, string TargetHash, long Sequence);

/// <summary>
/// Reads the machine-global ledger for the canary export and gate. Yields the canary/shadow rows (columns plus
/// parsed <c>canary_*</c> metadata, including the three served-result hash arrays) and the follow-up rows the
/// attribution rule joins against. Opens <c>Mode=ReadOnly</c> like <see cref="TelemetryExportReader"/>; a missing
/// DB or table yields an empty list rather than throwing.
/// </summary>
/// <summary>Both sides of the canary success join, read under one ledger snapshot.</summary>
public sealed record CanaryLedgerSnapshot(
    IReadOnlyList<CanaryRow> CanaryRows,
    IReadOnlyList<CanaryFollowUp> FollowUps)
{
    public static CanaryLedgerSnapshot Empty { get; } = new([], []);
}

public static class CanaryLedgerReader
{
    private const string CanaryColumns =
        "id, ts, workspace_id, op, duration_ms, outcome, result_count, miller_version, metadata_json";

    /// <summary>
    /// Read the canary rows and their candidate follow-ups under ONE consistent snapshot.
    /// </summary>
    /// <remarks>
    /// The success definition joins canary rows to follow-up rows, so reading the two sides on separate
    /// connections lets a concurrent writer land a follow-up between them: the join is then computed over two
    /// different ledger states and the gate verdict depends on write timing. A single read transaction pins one
    /// snapshot for both SELECTs.
    /// </remarks>
    public static CanaryLedgerSnapshot ReadSnapshot(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        if (!File.Exists(dbPath))
            return CanaryLedgerSnapshot.Empty;

        using SqliteConnection connection = OpenReadOnly(dbPath);
        if (!TableExists(connection, "tool_telemetry"))
            return CanaryLedgerSnapshot.Empty;

        using SqliteTransaction snapshot = connection.BeginTransaction();
        IReadOnlyList<CanaryRow> rows = ReadCanaryRows(connection, snapshot);
        IReadOnlyList<CanaryFollowUp> followUps =
            ReadFollowUps(connection, snapshot, CandidateTargets(rows));
        snapshot.Commit();
        return new CanaryLedgerSnapshot(rows, followUps);
    }

    public static IReadOnlyList<CanaryRow> ReadCanaryRows(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        if (!File.Exists(dbPath))
            return [];

        using SqliteConnection connection = OpenReadOnly(dbPath);
        if (!TableExists(connection, "tool_telemetry"))
            return [];

        return ReadCanaryRows(connection, transaction: null);
    }

    private static IReadOnlyList<CanaryRow> ReadCanaryRows(
        SqliteConnection connection, SqliteTransaction? transaction)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            $"SELECT {CanaryColumns}, rowid FROM tool_telemetry " +
            "WHERE metadata_json LIKE '%canary_contract_version%' ORDER BY ts ASC, rowid ASC;";

        var rows = new List<CanaryRow>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            CanaryRow? row = Parse(reader);
            if (row is not null)
                rows.Add(row);
        }
        return rows;
    }

    public static IReadOnlyList<CanaryFollowUp> ReadFollowUps(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        if (!File.Exists(dbPath))
            return [];

        using SqliteConnection connection = OpenReadOnly(dbPath);
        if (!TableExists(connection, "tool_telemetry"))
            return [];

        using SqliteTransaction snapshot = connection.BeginTransaction();
        IReadOnlyList<CanaryFollowUp> followUps = ReadFollowUps(
            connection, snapshot, CandidateTargets(ReadCanaryRows(connection, snapshot)));
        snapshot.Commit();
        return followUps;
    }

    /// <summary>
    /// The <c>(workspace_id, target_hash)</c> pairs a follow-up must carry to be creditable at all — the exact
    /// key set <see cref="AttributedRowIds"/> looks up. Derived here so the read and the join cannot drift apart.
    /// </summary>
    public static IReadOnlySet<(string Workspace, string Hash)> CandidateTargets(
        IReadOnlyList<CanaryRow> canaryRows)
    {
        ArgumentNullException.ThrowIfNull(canaryRows);

        var candidates = new HashSet<(string Workspace, string Hash)>();
        foreach (CanaryRow row in canaryRows)
        {
            if (row.WorkspaceId is null || !TryParseTs(row.Ts, out _))
                continue;

            foreach (string hash in row.ResultNameHashes
                .Concat(row.ResultPathHashes).Concat(row.ResultQualifiedHashes))
            {
                candidates.Add((row.WorkspaceId, hash));
            }
        }
        return candidates;
    }

    /// <summary>
    /// Reads only the follow-ups that could be credited. The old query pulled every successful
    /// <c>inspect</c>/<c>content read</c> row the ledger had ever recorded, which on a working ledger dwarfs the
    /// canary population and grows without bound; a follow-up whose <c>(workspace_id, target_hash)</c> matches
    /// no canary row is dropped by the join anyway, so pushing that set into SQL is exact, not a heuristic.
    /// </summary>
    private static IReadOnlyList<CanaryFollowUp> ReadFollowUps(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlySet<(string Workspace, string Hash)> candidates)
    {
        if (candidates.Count == 0)
            return [];

        // The temp database is writable even when main is opened read-only.
        using (SqliteCommand create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText =
                "CREATE TEMP TABLE IF NOT EXISTS canary_candidate_target " +
                "(workspace_id TEXT NOT NULL, target_hash TEXT NOT NULL, " +
                "PRIMARY KEY (workspace_id, target_hash)) WITHOUT ROWID;" +
                "DELETE FROM canary_candidate_target;";
            create.ExecuteNonQuery();
        }

        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT OR IGNORE INTO canary_candidate_target (workspace_id, target_hash) " +
                "VALUES ($ws, $hash);";
            SqliteParameter workspace = insert.Parameters.Add("$ws", SqliteType.Text);
            SqliteParameter hash = insert.Parameters.Add("$hash", SqliteType.Text);
            foreach ((string candidateWorkspace, string candidateHash) in candidates)
            {
                workspace.Value = candidateWorkspace;
                hash.Value = candidateHash;
                insert.ExecuteNonQuery();
            }
        }

        using SqliteCommand cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            "SELECT t.ts, t.workspace_id, t.target_hash, t.rowid FROM tool_telemetry AS t " +
            "JOIN canary_candidate_target AS c " +
            "  ON c.workspace_id = t.workspace_id AND c.target_hash = t.target_hash " +
            "WHERE t.outcome = 'ok' AND t.target_hash IS NOT NULL " +
            "AND (t.tool = 'inspect' OR (t.tool = 'content' AND t.op = 'read')) " +
            "ORDER BY t.ts ASC, t.rowid ASC;";

        var rows = new List<CanaryFollowUp>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new CanaryFollowUp(
                Ts: reader.GetString(0),
                WorkspaceId: reader.IsDBNull(1) ? null : reader.GetString(1),
                TargetHash: reader.GetString(2),
                Sequence: reader.GetInt64(3)));
        }
        return rows;
    }

    /// <summary>
    /// The frozen attribution join (§Matching rule): the ids of canary rows credited a follow-up. A follow-up
    /// <c>F</c> is credited to the <em>latest</em> preceding canary row that served its <c>target_hash</c> in the
    /// same workspace within the 600-second window; a canary row is attributed once at least one follow-up lands
    /// on it (conversion is binary). Only rows carrying served-result hashes can be candidates.
    /// </summary>
    public static IReadOnlySet<string> AttributedRowIds(
        IReadOnlyList<CanaryRow> canaryRows, IReadOnlyList<CanaryFollowUp> followUps)
    {
        ArgumentNullException.ThrowIfNull(canaryRows);
        ArgumentNullException.ThrowIfNull(followUps);

        var byWorkspaceHash = new Dictionary<
            (string Workspace, string Hash),
            List<(DateTimeOffset Ts, long Sequence, string Id)>>();
        foreach (CanaryRow row in canaryRows)
        {
            if (row.WorkspaceId is null || !TryParseTs(row.Ts, out DateTimeOffset ts))
                continue;

            foreach (string hash in row.ResultNameHashes
                .Concat(row.ResultPathHashes).Concat(row.ResultQualifiedHashes).Distinct(StringComparer.Ordinal))
            {
                var key = (row.WorkspaceId, hash);
                if (!byWorkspaceHash.TryGetValue(
                    key,
                    out List<(DateTimeOffset Ts, long Sequence, string Id)>? bucket))
                    byWorkspaceHash[key] = bucket = [];
                bucket.Add((ts, row.Sequence, row.Id));
            }
        }

        var attributed = new HashSet<string>(StringComparer.Ordinal);
        foreach (CanaryFollowUp followUp in followUps)
        {
            if (followUp.WorkspaceId is null || !TryParseTs(followUp.Ts, out DateTimeOffset followUpTs))
                continue;
            if (!byWorkspaceHash.TryGetValue(
                (followUp.WorkspaceId, followUp.TargetHash),
                out List<(DateTimeOffset Ts, long Sequence, string Id)>? candidates))
                continue;

            string? latestId = null;
            DateTimeOffset latestTs = DateTimeOffset.MinValue;
            long latestSequence = long.MinValue;
            foreach ((DateTimeOffset ts, long sequence, string id) in candidates)
            {
                double seconds = (followUpTs - ts).TotalSeconds;
                if (seconds < 0.0 || seconds > 600.0 ||
                    (seconds == 0.0 && followUp.Sequence <= sequence))
                    continue;
                if (latestId is null || ts > latestTs || (ts == latestTs && sequence > latestSequence))
                {
                    latestTs = ts;
                    latestSequence = sequence;
                    latestId = id;
                }
            }

            if (latestId is not null)
                attributed.Add(latestId);
        }

        return attributed;
    }

    private static bool TryParseTs(string ts, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(
            ts,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out value);

    private static CanaryRow? Parse(SqliteDataReader reader)
    {
        string metadataJson = reader.IsDBNull(8) ? "{}" : reader.GetString(8);
        JsonElement meta;
        try
        {
            using JsonDocument document = JsonDocument.Parse(metadataJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            meta = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        if (Int(meta, "canary_contract_version") is null)
            return null;

        return new CanaryRow(
            Id: reader.GetString(0),
            Ts: reader.GetString(1),
            WorkspaceId: reader.IsDBNull(2) ? null : reader.GetString(2),
            Op: reader.IsDBNull(3) ? null : reader.GetString(3),
            DurationMs: reader.GetInt64(4),
            Outcome: reader.GetString(5),
            ResultCount: reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
            MillerVersion: reader.IsDBNull(7) ? null : reader.GetString(7),
            ContractVersion: Int(meta, "canary_contract_version"),
            ExperimentId: Str(meta, "canary_experiment_id"),
            Arm: Str(meta, "canary_arm"),
            Eligibility: Str(meta, "canary_eligibility"),
            QueryClass: Str(meta, "canary_query_class"),
            Bucket: Int(meta, "canary_bucket"),
            PolicyVersion: Int(meta, "canary_policy_version"),
            EmbedWarmth: Str(meta, "canary_embed_warmth"),
            FallbackReason: Str(meta, "canary_fallback_reason"),
            Backend: Str(meta, "canary_backend"),
            RescueKind: Str(meta, "canary_rescue_kind"),
            EmbedLatencyBucket: Str(meta, "canary_embed_latency_bucket"),
            KnnLatencyBucket: Str(meta, "canary_knn_latency_bucket"),
            SemanticContributionCount: Int(meta, "canary_semantic_contribution_count"),
            EncoderFingerprint: Str(meta, "canary_encoder_fingerprint"),
            StorageSchema: Str(meta, "canary_storage_schema"),
            CorpusGeneration: Str(meta, "canary_corpus_generation"),
            FusionProfile: Str(meta, "canary_fusion_profile"),
            ResultNameHashes: StrArray(meta, "canary_result_name_hashes"),
            ResultPathHashes: StrArray(meta, "canary_result_path_hashes"),
            ResultQualifiedHashes: StrArray(meta, "canary_result_qualified_hashes"),
            ShadowStatus: Str(meta, "canary_shadow_status"),
            ShadowOverlapAt10: Int(meta, "canary_shadow_overlap_at_10"),
            ShadowTop1Changed: Bool(meta, "canary_shadow_top1_changed"),
            ShadowLexicalTop1Rank: Int(meta, "canary_shadow_lexical_top1_rank"),
            Sequence: reader.GetInt64(9));
    }

    private static string? Str(JsonElement o, string key) =>
        o.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement o, string key) =>
        o.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static bool? Bool(JsonElement o, string key) =>
        o.TryGetProperty(key, out JsonElement v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    private static IReadOnlyList<string> StrArray(JsonElement o, string key)
    {
        if (!o.TryGetProperty(key, out JsonElement v) || v.ValueKind != JsonValueKind.Array)
            return [];

        var values = new List<string>(v.GetArrayLength());
        foreach (JsonElement element in v.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String && element.GetString() is { } s)
                values.Add(s);
        }
        return values;
    }

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(dbPath),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static bool TableExists(SqliteConnection connection, string name)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", name);
        return cmd.ExecuteScalar() is not null;
    }
}
