using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Core.Graph;

namespace Miller.Indexing;

internal static class TestLinkageReader
{
    private static readonly string[] LinkageKeys = ["test_linkage", "test_coverage"];

    public static IReadOnlyList<GraphEdge> Read(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT symbol_id, metadata_json
            FROM symbols
            WHERE is_test = 1
              AND metadata_json IS NOT NULL
            ORDER BY symbol_id;
            """;
        using var reader = command.ExecuteReader();
        var edges = new List<GraphEdge>();
        while (reader.Read())
        {
            string testSymbolId = reader.GetString(0);
            string metadata = reader.GetString(1);
            AppendEdges(testSymbolId, metadata, edges);
        }
        return edges;
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
