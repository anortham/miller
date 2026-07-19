using System.Text.Json;

namespace RetrievalEval;

/// <summary>Line-delimited JSON reading. Blank lines and `#` comment lines are skipped.</summary>
public static class Jsonl
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
    };

    public static List<T> ReadAll<T>(string path)
    {
        var rows = new List<T>();
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            try
            {
                var row = JsonSerializer.Deserialize<T>(trimmed, Options)
                    ?? throw new InvalidDataException("row deserialized to null");
                rows.Add(row);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                throw new InvalidDataException($"{path}:{lineNumber}: {ex.Message}", ex);
            }
        }

        return rows;
    }
}
