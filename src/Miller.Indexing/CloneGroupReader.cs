using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

public static class CloneGroupReader
{
    public const int DefaultSymbolsPerGroup = 25;
    public const int MaxSymbolsPerGroup = 500;

    public static IReadOnlyList<CloneGroup> Read(
        string symbolsDbPath,
        int limit = 50,
        int minCount = 2,
        int symbolsPerGroup = DefaultSymbolsPerGroup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolsDbPath);
        if (limit < 1)
            limit = 1;
        if (minCount < 2)
            minCount = 2;
        symbolsPerGroup = Math.Clamp(symbolsPerGroup, 1, MaxSymbolsPerGroup);

        using SqliteConnection connection = SqliteReadOnlyAccess.Open(symbolsDbPath);
        JulieSchemaGate.Verify(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            WITH clone_hashes AS (
                SELECT body_hash, COUNT(*) AS clone_count
                FROM symbols
                WHERE body_hash IS NOT NULL AND body_hash != ''
                GROUP BY body_hash
                HAVING COUNT(*) >= $min_count
                ORDER BY clone_count DESC, body_hash
                LIMIT $limit
            ),
            ranked_symbols AS (
                SELECT h.body_hash, h.clone_count,
                       s.symbol_id, s.name, s.kind, s.language, s.path,
                       s.start_line, s.is_test,
                       ROW_NUMBER() OVER (
                           PARTITION BY h.body_hash
                           ORDER BY s.path, s.start_line, s.symbol_id
                       ) AS symbol_rank
                FROM clone_hashes h
                JOIN symbols s ON s.body_hash = h.body_hash
            )
            SELECT h.body_hash, h.clone_count,
                   h.symbol_id, h.name, h.kind, h.language, h.path,
                   h.start_line, h.is_test
            FROM ranked_symbols h
            WHERE h.symbol_rank <= $symbols_per_group
            ORDER BY h.clone_count DESC, h.body_hash, h.path, h.start_line, h.symbol_id;
            """;
        command.Parameters.AddWithValue("$min_count", minCount);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$symbols_per_group", symbolsPerGroup);

        var groups = new List<CloneGroup>();
        string? currentHash = null;
        int currentCount = 0;
        List<CloneSymbol>? currentSymbols = null;

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string bodyHash = reader.GetString(0);
            if (!string.Equals(currentHash, bodyHash, StringComparison.Ordinal))
            {
                Flush();
                currentHash = bodyHash;
                currentCount = checked((int)reader.GetInt64(1));
                currentSymbols = [];
            }

            currentSymbols!.Add(new CloneSymbol(
                SymbolId: reader.GetString(2),
                Name: reader.GetString(3),
                Kind: reader.GetString(4),
                Language: reader.GetString(5),
                Path: reader.GetString(6),
                Line: reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                IsTest: !reader.IsDBNull(8) && reader.GetInt64(8) != 0));
        }

        Flush();
        return groups;

        void Flush()
        {
            if (currentHash is not null && currentSymbols is not null)
                groups.Add(new CloneGroup(currentHash, currentCount, currentSymbols));
        }
    }
}

public sealed record CloneGroup(string BodyHash, int Count, IReadOnlyList<CloneSymbol> Symbols);

public sealed record CloneSymbol(
    string SymbolId,
    string Name,
    string Kind,
    string Language,
    string Path,
    int Line,
    bool IsTest);
