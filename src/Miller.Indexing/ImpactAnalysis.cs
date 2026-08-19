using Miller.Core.Graph;

namespace Miller.Indexing;

/// <summary>One reverse-reachability hit carrying the indexed symbol and hop evidence.</summary>
public sealed record ImpactSymbolHit(IndexedSymbol Symbol, ReachedNode Evidence);

/// <summary>Typed blast-radius result used by <c>impact</c> and by the CT fact adapter.</summary>
public sealed record ImpactAnalysisResult(
    IReadOnlyList<ImpactSymbolHit> Impacted,
    IReadOnlyList<ImpactSymbolHit> Tests,
    GraphReachResult Graph,
    int TraversalCandidateLimit,
    int GraphReachedCount,
    int HeuristicTestCandidateCount,
    int GraphDisplacementCount,
    bool TestCandidatesTruncated)
{
    public int SelectedCount => Impacted.Count + Tests.Count;
}

/// <summary>
/// Pure typed impact: reverse-reach dependents, rank them, and split tests from other symbols.
/// Rendering stays in the Server tool.
/// </summary>
public static class ImpactAnalysis
{
    public const int MaximumDepth = 5;
    public const int MaximumLimit = 1000;
    public const int MinimumRankingCandidates = 500;
    public const int MaximumRankingCandidates = 2000;
    public const int RankingCandidateMultiplier = 8;

    public static int NormalizeDepth(int depth) => Math.Clamp(depth, 1, MaximumDepth);

    public static int NormalizeLimit(int limit) => Math.Clamp(limit, 1, MaximumLimit);

    public static int RankingCandidateLimit(int limit)
    {
        long scaled = Math.Max(MinimumRankingCandidates, (long)limit * RankingCandidateMultiplier);
        return Math.Max(limit, (int)Math.Min(MaximumRankingCandidates, scaled));
    }

    public static ImpactAnalysisResult Compute(
        ISymbolLookupIndex index,
        ISymbolGraphReachability graph,
        IReadOnlyList<string> seedIds,
        int maxDepth,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(seedIds);

        maxDepth = NormalizeDepth(maxDepth);
        limit = NormalizeLimit(limit);
        int traversalCandidateLimit = RankingCandidateLimit(limit);
        GraphReachResult graphResult =
            graph.ReachWithEvidence(seedIds, maxDepth, traversalCandidateLimit, Direction.Reverse);
        HeuristicExpansion expansion = AddHeuristicTestCandidates(
            index, seedIds, graphResult.Nodes, graphResult.Nodes.Count + limit);
        var symbolsById =
            SymbolLookupBatch.FindBySymbolIds(index, expansion.Nodes.Select(static node => node.Id));
        ImpactRankSignal[] selected = ImpactRanker.Rank(expansion.Nodes
            .Where(node => symbolsById.ContainsKey(node.Id))
            .Select(node =>
            {
                IndexedSymbol symbol = symbolsById[node.Id];
                return new ImpactRankSignal(node, symbol.FilePath, symbol.StartLine, symbol.Name, symbol.SymbolId);
            }))
            .Take(limit)
            .ToArray();

        var impacted = new List<ImpactSymbolHit>();
        var tests = new List<ImpactSymbolHit>();
        foreach (ImpactRankSignal candidate in selected)
        {
            IndexedSymbol symbol = symbolsById[candidate.SymbolId];
            var hit = new ImpactSymbolHit(symbol, candidate.Evidence);
            if (symbol.IsTest)
                tests.Add(hit);
            else
                impacted.Add(hit);
        }

        int returnedTestCandidateCount = selected.Count(static candidate =>
            string.Equals(candidate.Evidence.EdgeSource, "filename_role", StringComparison.Ordinal));
        int returnedGraphCount = selected.Length - returnedTestCandidateCount;
        int resolvableGraphRows = graphResult.Nodes.Count(node => symbolsById.ContainsKey(node.Id));
        bool testCandidatesTruncated =
            expansion.Truncated || expansion.CandidateCount > returnedTestCandidateCount;
        GraphReachResult truthfulGraph = graphResult with
        {
            TruncatedByLimit =
                graphResult.TruncatedByLimit ||
                resolvableGraphRows > returnedGraphCount ||
                testCandidatesTruncated,
        };
        return new ImpactAnalysisResult(
            impacted,
            tests,
            truthfulGraph,
            traversalCandidateLimit,
            graphResult.Nodes.Count,
            expansion.CandidateCount,
            Math.Max(0, resolvableGraphRows - returnedGraphCount),
            testCandidatesTruncated);
    }

    private sealed record HeuristicExpansion(
        IReadOnlyList<ReachedNode> Nodes,
        int CandidateCount,
        bool Truncated);

    private static HeuristicExpansion AddHeuristicTestCandidates(
        ISymbolLookupIndex index,
        IReadOnlyList<string> seedIds,
        IReadOnlyList<ReachedNode> graphNodes,
        int limit)
    {
        var combined = graphNodes.ToList();
        int candidateCount = 0;
        var seen = new HashSet<string>(
            seedIds.Concat(graphNodes.Select(static node => node.Id)),
            StringComparer.Ordinal);
        var seenStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, IndexedSymbol> seeds =
            SymbolLookupBatch.FindBySymbolIds(index, seedIds);
        foreach (IndexedSymbol seed in seeds.Values
                     .OrderBy(static symbol => symbol.FilePath, StringComparer.Ordinal)
                     .ThenBy(static symbol => symbol.StartLine)
                     .ThenBy(static symbol => symbol.SymbolId, StringComparer.Ordinal))
        {
            string stem = Path.GetFileNameWithoutExtension(seed.FilePath);
            if (string.IsNullOrWhiteSpace(stem) || !seenStems.Add(stem))
                continue;

            IReadOnlyList<string> candidatePaths = index
                .FindFilePathsByFragment(stem, int.MaxValue)
                .Where(path => IsFilenameRoleCandidate(stem, path))
                .ToArray();
            foreach (IndexedSymbol candidate in candidatePaths
                         .SelectMany(index.FindByFilePath)
                         .Where(static symbol => symbol.IsTest)
                         .OrderBy(static symbol => symbol.FilePath, StringComparer.Ordinal)
                         .ThenBy(static symbol => symbol.StartLine)
                         .ThenBy(static symbol => symbol.SymbolId, StringComparer.Ordinal))
            {
                if (!seen.Add(candidate.SymbolId))
                    continue;
                if (combined.Count >= limit)
                    return new(combined, candidateCount, true);

                combined.Add(new ReachedNode(
                    candidate.SymbolId,
                    1,
                    seed.SymbolId,
                    "test_candidate",
                    0.35,
                    "filename_role",
                    Visibility: candidate.Visibility));
                candidateCount++;
            }
        }

        return new(combined, candidateCount, false);
    }

    private static bool IsFilenameRoleCandidate(string sourceStem, string candidatePath)
    {
        string candidateStem = Path.GetFileNameWithoutExtension(candidatePath);
        if (candidateStem.StartsWith(sourceStem, StringComparison.OrdinalIgnoreCase) &&
            IsTestRole(candidateStem[sourceStem.Length..].Trim('_', '.', '-')))
            return true;

        return candidateStem.EndsWith(sourceStem, StringComparison.OrdinalIgnoreCase) &&
               IsTestRole(candidateStem[..^sourceStem.Length].Trim('_', '.', '-'));
    }

    private static bool IsTestRole(string value) =>
        value.Equals("test", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("spec", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("specs", StringComparison.OrdinalIgnoreCase);
}
