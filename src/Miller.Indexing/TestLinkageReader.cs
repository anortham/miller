using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Core.Graph;

namespace Miller.Indexing;

/// <summary>The linkage edges, plus whether the metadata scan actually ran.</summary>
internal readonly record struct TestLinkageReadResult(IReadOnlyList<GraphEdge> Edges, bool Scanned);

internal static class TestLinkageReader
{
    private static readonly string[] LinkageKeys = ["test_linkage", "test_coverage"];

    public static IReadOnlyList<GraphEdge> Read(SqliteConnection connection) =>
        ReadWithProbe(connection).Edges;

    /// <summary>
    /// Reads test-linkage edges, but only after a costless <c>LIMIT 1</c> probe proves at least one test symbol
    /// carries a linkage key. No store julie-extract has ever written carries <c>test_linkage</c> or
    /// <c>test_coverage</c> (probe over five store families, 2026-08-21), so the probe short-circuits every real
    /// call today: the unprobed scan read 32,436 metadata blobs out of Miller's own store and parsed each one to
    /// produce ZERO edges, at 2,978 ms per graph load against 206 ms for the probe. The reader stays intact for
    /// the day the extractor emits linkage metadata.
    /// </summary>
    internal static TestLinkageReadResult ReadWithProbe(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!HasLinkageMetadata(connection))
            return new TestLinkageReadResult([], Scanned: false);

        using var command = connection.CreateCommand();
        // No ORDER BY: the sort forced the whole family symbols table through a sort step (2,978 ms measured
        // against Miller's live store, versus 220 ms for the same result set unsorted). Order does not reach
        // any consumer — both SymbolGraph.Build and SqliteSymbolGraphIndex fold these edges into a per-neighbour
        // dictionary under a total tie-break over (kind, source, confidence) and then emit neighbours sorted by
        // id, so two edges that a different row order could swap are byte-identical in the output.
        command.CommandText =
            """
            SELECT symbol_id, metadata_json
            FROM symbols
            WHERE is_test = 1
              AND metadata_json IS NOT NULL;
            """;
        using var reader = command.ExecuteReader();
        var edges = new List<GraphEdge>();
        while (reader.Read())
        {
            string testSymbolId = reader.GetString(0);
            string metadata = reader.GetString(1);
            AppendEdges(testSymbolId, metadata, edges);
        }
        return new TestLinkageReadResult(edges, Scanned: true);
    }

    /// <summary>
    /// True when at least one visible test symbol's metadata mentions a linkage key. The text match is a
    /// SUPERSET of what <see cref="AppendEdges"/> accepts (it matches the key anywhere in the blob, and SQLite's
    /// LIKE is ASCII case-insensitive), so the probe can only ever fail open: it never hides an edge the scan
    /// would have produced.
    /// </summary>
    internal static bool HasLinkageMetadata(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM symbols
            WHERE is_test = 1
              AND metadata_json IS NOT NULL
              AND (metadata_json LIKE '%"test_linkage"%' OR metadata_json LIKE '%"test_coverage"%')
            LIMIT 1;
            """;
        return command.ExecuteScalar() is not null;
    }

    private static void AppendEdges(string testSymbolId, string metadata, List<GraphEdge> edges)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(metadata);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return;

            foreach (string key in LinkageKeys)
            {
                if (!document.RootElement.TryGetProperty(key, out JsonElement value))
                    continue;

                double confidence = ReadConfidence(value);
                foreach (string targetId in ReadTargetIds(value))
                {
                    if (!string.Equals(testSymbolId, targetId, StringComparison.Ordinal))
                        edges.Add(new GraphEdge(testSymbolId, targetId, key, confidence, key));
                }
            }
        }
    }

    private static double ReadConfidence(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("confidence", out JsonElement confidence) &&
            confidence.TryGetDouble(out double parsed))
            return Math.Clamp(parsed, 0, 1);
        return 1.0;
    }

    private static IEnumerable<string> ReadTargetIds(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            string? id = value.GetString();
            if (!string.IsNullOrWhiteSpace(id))
                yield return id;
            yield break;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                foreach (string id in ReadTargetIds(item))
                    yield return id;
            }
            yield break;
        }

        if (value.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (string key in new[] { "symbol_id", "target_symbol_id", "source_symbol_id" })
        {
            if (value.TryGetProperty(key, out JsonElement idValue) &&
                idValue.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(idValue.GetString()))
                yield return idValue.GetString()!;
        }

        if (value.TryGetProperty("symbol_ids", out JsonElement ids))
        {
            foreach (string id in ReadTargetIds(ids))
                yield return id;
        }
    }
}
