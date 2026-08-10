using Miller.Core.Diff;
using Miller.Indexing;
using Miller.Indexing.Reads;

namespace Miller.Server.Git;

public static class GitChurnAnalyzer
{
    public static ChurnReport Read(
        string symbolsDbPath,
        string workspaceRoot,
        string range,
        int limit,
        bool includeCommits,
        IGitHistoryReader historyReader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolsDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(range);
        ArgumentNullException.ThrowIfNull(historyReader);
        if (limit < 1)
            limit = 1;

        GitHistoryResult history = historyReader.Read(new GitHistoryRequest(workspaceRoot, range));
        if (!history.Success)
            throw new InvalidOperationException(history.Error ?? "git history read failed.");

        MillerRepositoryIndex index = MillerRepositoryIndex.Build(SqliteSymbolReader.Read(symbolsDbPath));
        return ReadCore(history, range, limit, includeCommits, index);
    }

    public static ChurnReport Read(
        IWorkspaceReadSession session,
        string workspaceRoot,
        string range,
        int limit,
        bool includeCommits,
        IGitHistoryReader historyReader)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(range);
        ArgumentNullException.ThrowIfNull(historyReader);
        if (limit < 1)
            limit = 1;

        GitHistoryResult history = historyReader.Read(new GitHistoryRequest(workspaceRoot, range));
        if (!history.Success)
            throw new InvalidOperationException(history.Error ?? "git history read failed.");

        MillerRepositoryIndex index = MillerRepositoryIndex.Build(SqliteSymbolReader.ReadSession(session));
        return ReadCore(history, range, limit, includeCommits, index);
    }

    private static ChurnReport ReadCore(
        GitHistoryResult history,
        string range,
        int limit,
        bool includeCommits,
        MillerRepositoryIndex index)
    {
        var aggregates = new Dictionary<ChurnKey, ChurnAccumulator>();
        var changedPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (GitHistoryCommit commit in history.Commits)
        {
            foreach (DiffFile file in DiffTargets.Parse(commit.Diff))
            {
                changedPaths.Add(file.Path);
                IReadOnlyList<IndexedSymbol> fileSymbols = index.FindByFilePath(file.Path);
                bool matchedAnySymbol = false;
                int changedLines = file.Changed.Sum(static range => Math.Max(1, range.EndLine - range.StartLine + 1));

                foreach (IndexedSymbol symbol in SymbolsIntersecting(fileSymbols, file.Changed))
                {
                    matchedAnySymbol = true;
                    ChurnKey key = ChurnKey.ForSymbol(symbol);
                    Accumulate(key, symbol, file.Path, commit, changedLines);
                }

                if (!matchedAnySymbol)
                {
                    ChurnKey key = ChurnKey.ForFile(file.Path);
                    Accumulate(key, symbol: null, file.Path, commit, changedLines);
                }
            }
        }

        ChurnRow[] rows = aggregates.Values
            .Select(accumulator => accumulator.ToRow(includeCommits))
            .OrderByDescending(static row => row.CommitCount)
            .ThenByDescending(static row => row.ChangedLines)
            .ThenByDescending(static row => row.LastCommitAtUtc)
            .ThenBy(static row => row.Path, StringComparer.Ordinal)
            .ThenBy(static row => row.SymbolName, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();

        return new ChurnReport(range, rows, changedPaths.Count);

        void Accumulate(
            ChurnKey key,
            IndexedSymbol? symbol,
            string path,
            GitHistoryCommit commit,
            int changedLinesForFile)
        {
            if (!aggregates.TryGetValue(key, out ChurnAccumulator? accumulator))
            {
                accumulator = ChurnAccumulator.Create(key, symbol, path);
                aggregates.Add(key, accumulator);
            }
            accumulator.Add(commit, changedLinesForFile);
        }
    }

    private static IEnumerable<IndexedSymbol> SymbolsIntersecting(
        IReadOnlyList<IndexedSymbol> symbols,
        IReadOnlyList<LineRange> ranges)
    {
        if (symbols.Count == 0 || ranges.Count == 0)
            yield break;

        IndexedSymbol[] ordered = symbols
            .Where(static symbol => symbol.StartLine > 0)
            .OrderBy(static symbol => symbol.StartLine)
            .ThenBy(static symbol => symbol.Name, StringComparer.Ordinal)
            .ToArray();

        for (var i = 0; i < ordered.Length; i++)
        {
            IndexedSymbol symbol = ordered[i];
            int endLine = ImpliedEndLine(ordered, i);
            if (ranges.Any(range => Intersects(symbol.StartLine, endLine, range)))
                yield return symbol;
        }
    }

    private static int ImpliedEndLine(IReadOnlyList<IndexedSymbol> ordered, int index)
    {
        IndexedSymbol symbol = ordered[index];
        if (symbol.EndLine > 0)
            return symbol.EndLine;

        for (int i = index + 1; i < ordered.Count; i++)
        {
            int nextStart = ordered[i].StartLine;
            if (nextStart > symbol.StartLine)
                return nextStart - 1;
        }

        return symbol.StartLine;
    }

    private static bool Intersects(int startLine, int endLine, LineRange range)
    {
        return startLine <= range.EndLine && range.StartLine <= endLine;
    }

    private readonly record struct ChurnKey(string MappingBasis, string Id)
    {
        public static ChurnKey ForSymbol(IndexedSymbol symbol) => new("current_index", symbol.SymbolId);

        public static ChurnKey ForFile(string path) => new("file_only", path);
    }

    private sealed class ChurnAccumulator
    {
        private readonly HashSet<string> _commits = new(StringComparer.Ordinal);
        private readonly List<string> _orderedCommits = [];

        private ChurnAccumulator(
            string mappingBasis,
            string? symbolId,
            string? symbolName,
            string? symbolKind,
            string path,
            int? line)
        {
            MappingBasis = mappingBasis;
            SymbolId = symbolId;
            SymbolName = symbolName;
            SymbolKind = symbolKind;
            Path = path;
            Line = line;
        }

        private string MappingBasis { get; }

        private string? SymbolId { get; }

        private string? SymbolName { get; }

        private string? SymbolKind { get; }

        private string Path { get; }

        private int? Line { get; }

        private int ChangedLines { get; set; }

        private DateTimeOffset LastCommitAtUtc { get; set; }

        public static ChurnAccumulator Create(ChurnKey key, IndexedSymbol? symbol, string path) =>
            new(
                key.MappingBasis,
                symbol?.SymbolId,
                symbol?.Name,
                symbol?.Kind,
                path,
                symbol?.StartLine);

        public void Add(GitHistoryCommit commit, int changedLines)
        {
            if (_commits.Add(commit.Commit))
                _orderedCommits.Add(commit.Commit);
            ChangedLines += changedLines;
            if (commit.AuthorTimeUtc > LastCommitAtUtc)
                LastCommitAtUtc = commit.AuthorTimeUtc;
        }

        public ChurnRow ToRow(bool includeCommits) =>
            new(
                MappingBasis,
                SymbolId,
                SymbolName,
                SymbolKind,
                Path,
                Line,
                _commits.Count,
                ChangedLines,
                LastCommitAtUtc,
                includeCommits ? _orderedCommits : []);
    }
}

/// <param name="TotalFilesChanged">
/// EXACT count of distinct file paths changed in the range, computed before <c>Rows</c> is truncated to the
/// display limit. This is the value metric-history snapshots record as <c>churn_files_changed</c>: it must stay
/// limit-independent so the report arm (section limit 10) and the churn arm (limit 50) write comparable points
/// under one metric name.
/// </param>
public sealed record ChurnReport(string Range, IReadOnlyList<ChurnRow> Rows, int TotalFilesChanged);

public sealed record ChurnRow(
    string MappingBasis,
    string? SymbolId,
    string? SymbolName,
    string? SymbolKind,
    string Path,
    int? Line,
    int CommitCount,
    int ChangedLines,
    DateTimeOffset LastCommitAtUtc,
    IReadOnlyList<string> Commits);
