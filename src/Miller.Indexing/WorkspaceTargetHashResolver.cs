using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

public sealed record TargetHashFrequency
{
    public TargetHashFrequency(string targetHash, long calls)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetHash);
        if (calls < 0)
            throw new ArgumentOutOfRangeException(nameof(calls));
        TargetHash = targetHash;
        Calls = calls;
    }

    public string TargetHash { get; }
    public long Calls { get; }
}

public sealed record RecoveredTargetHash(
    string Confidence,
    string? SymbolId,
    string? Name,
    string? Kind,
    string? Path,
    int? StartLine,
    long Calls,
    int CandidateCount);

public static class WorkspaceTargetHashResolver
{
    public static IReadOnlyList<RecoveredTargetHash> Resolve(
        string dbPath,
        IReadOnlyList<TargetHashFrequency> targetHashes,
        int limit = 10)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentNullException.ThrowIfNull(targetHashes);
        if (targetHashes.Count == 0 || !File.Exists(dbPath))
            return [];

        var wantedHashes = targetHashes.Select(static row => row.TargetHash).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, List<Candidate>> candidates = LoadCandidates(dbPath, wantedHashes);
        return targetHashes
            .OrderByDescending(static row => row.Calls)
            .ThenBy(static row => row.TargetHash, StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .Select(row => ResolveOne(row, candidates))
            .ToArray();
    }

    private static RecoveredTargetHash ResolveOne(
        TargetHashFrequency frequency,
        Dictionary<string, List<Candidate>> candidatesByHash)
    {
        if (!candidatesByHash.TryGetValue(frequency.TargetHash, out List<Candidate>? candidates) || candidates.Count == 0)
            return new RecoveredTargetHash(
                Confidence: "unresolved_hash",
                SymbolId: null,
                Name: null,
                Kind: null,
                Path: null,
                StartLine: null,
                Calls: frequency.Calls,
                CandidateCount: 0);

        Candidate winner = candidates
            .OrderBy(static row => row.Priority)
            .ThenBy(static row => row.Path, StringComparer.Ordinal)
            .ThenBy(static row => row.Name, StringComparer.Ordinal)
            .ThenBy(static row => row.SymbolId, StringComparer.Ordinal)
            .First();
        int candidateCount = candidates.Count(row => row.Confidence == winner.Confidence);
        return new RecoveredTargetHash(
            winner.Confidence,
            winner.SymbolId,
            winner.Name,
            winner.Kind,
            winner.Path,
            winner.StartLine,
            frequency.Calls,
            candidateCount);
    }

    private static Dictionary<string, List<Candidate>> LoadCandidates(string dbPath, HashSet<string> wantedHashes)
    {
        var candidates = new Dictionary<string, List<Candidate>>(StringComparer.Ordinal);
        if (wantedHashes.Count == 0)
            return candidates;

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(dbPath),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT symbol_id, name, kind, path, start_line
                FROM symbols
                ORDER BY path, start_line, name, symbol_id;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string symbolId = reader.GetString(0);
                string name = reader.GetString(1);
                string kind = reader.GetString(2);
                string path = reader.GetString(3);
                int? startLine = reader.IsDBNull(4) ? null : reader.GetInt32(4);

                var symbol = new Candidate(symbolId, name, kind, path, startLine, "symbol_id_hash", Priority: 0);
                AddIfWanted(candidates, wantedHashes, Hash(symbolId), symbol);
                AddIfWanted(candidates, wantedHashes, Hash(path + ":" + name), symbol with { Confidence = "scoped_symbol_hash", Priority = 1 });
                AddIfWanted(candidates, wantedHashes, Hash(name), symbol with { Confidence = "symbol_name_hash", Priority = 3 });
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT path FROM files ORDER BY path;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string path = reader.GetString(0);
                AddIfWanted(candidates, wantedHashes, Hash(path), new Candidate(
                    SymbolId: null,
                    Name: null,
                    Kind: "file",
                    Path: path,
                    StartLine: null,
                    Confidence: "file_path_hash",
                    Priority: 2));
            }
        }

        return candidates;
    }

    private static void AddIfWanted(
        Dictionary<string, List<Candidate>> candidates,
        HashSet<string> wantedHashes,
        string hash,
        Candidate candidate)
    {
        if (!wantedHashes.Contains(hash))
            return;

        if (!candidates.TryGetValue(hash, out List<Candidate>? list))
        {
            list = [];
            candidates.Add(hash, list);
        }
        list.Add(candidate);
    }

    private static string Hash(string raw) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private sealed record Candidate(
        string? SymbolId,
        string? Name,
        string? Kind,
        string? Path,
        int? StartLine,
        string Confidence,
        int Priority);
}
