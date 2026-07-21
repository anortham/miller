using System.Text.Json;
using System.Text.Json.Serialization;

namespace FusionArm;

/// <summary>One row of a per-query arm input file. Lexical files omit <see cref="Rank"/> (rank is array order);
/// semantic files carry an explicit 1-based <see cref="Rank"/>.</summary>
public sealed record ArmInputRow
{
    [JsonPropertyName("symbol_id")] public string SymbolId { get; init; } = "";
    [JsonPropertyName("doc_id")] public string DocId { get; init; } = "";
    [JsonPropertyName("score")] public double Score { get; init; }
    [JsonPropertyName("rank")] public int? Rank { get; init; }
}

/// <summary>The query-set fields the fusion arm reads: the id it keys input/output files on and the literal text
/// handed to <c>SemanticQueryPolicy.Route</c>.</summary>
public sealed record QueryRow
{
    [JsonPropertyName("query_id")] public string QueryId { get; init; } = "";
    [JsonPropertyName("query")] public string Query { get; init; } = "";
}

/// <summary>One line of the retrieval-eval results contract.</summary>
public sealed record FusedResultRow
{
    [JsonPropertyName("query_id")] public string QueryId { get; init; } = "";
    [JsonPropertyName("ranked")] public IReadOnlyList<string> Ranked { get; init; } = [];
}

/// <summary>JSON reading/writing for the arm's file formats. The results writer emits compact single-line JSONL so
/// the retrieval-eval reader consumes it one row per line.</summary>
public static class Json
{
    static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    public static List<QueryRow> ReadQuerySet(string path)
    {
        var rows = new List<QueryRow>();
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            try
            {
                rows.Add(JsonSerializer.Deserialize<QueryRow>(trimmed)
                    ?? throw new InvalidDataException("row deserialized to null"));
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{path}:{lineNumber}: {ex.Message}", ex);
            }
        }

        return rows;
    }

    public static IReadOnlyList<ArmInputRow> ReadArmFile(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ArmInputRow>>(File.ReadAllText(path)) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path}: {ex.Message}", ex);
        }
    }

    public static string SerializeRow(FusedResultRow row) => JsonSerializer.Serialize(row, Compact);
}
