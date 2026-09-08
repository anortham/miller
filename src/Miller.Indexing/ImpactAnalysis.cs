using Miller.Core.Graph;
using Miller.Core.References;

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
        int limit) =>
        Compute(index, graph, seedIds, maxDepth, limit, view: "all", testsLimit: null, symbolsLimit: null);

    public static ImpactAnalysisResult Compute(
        ISymbolLookupIndex index,
        ISymbolGraphReachability graph,
        IReadOnlyList<string> seedIds,
        int maxDepth,
        int limit,
        string view = "all",
        int? testsLimit = null,
        int? symbolsLimit = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(seedIds);

        maxDepth = NormalizeDepth(maxDepth);
        limit = NormalizeLimit(limit);
        bool isTestsOnly = string.Equals(view, "tests", StringComparison.OrdinalIgnoreCase);
        int effectiveTestsLimit = testsLimit.HasValue ? NormalizeLimit(testsLimit.Value) : limit;
        int effectiveSymbolsLimit = isTestsOnly ? 0 : (symbolsLimit.HasValue ? NormalizeLimit(symbolsLimit.Value) : limit);

        int candidateLimitBase = Math.Max(limit, Math.Max(effectiveTestsLimit, effectiveSymbolsLimit));
        int traversalCandidateLimit = RankingCandidateLimit(candidateLimitBase);
        GraphReachResult graphResult =
            graph.ReachWithEvidence(seedIds, maxDepth, traversalCandidateLimit, Direction.Reverse);

        IReadOnlyDictionary<string, IndexedSymbol> seeds =
            SymbolLookupBatch.FindBySymbolIds(index, seedIds);

        var changedTestNodes = new List<ReachedNode>();
        foreach (IndexedSymbol seed in seeds.Values
                     .OrderBy(static symbol => symbol.FilePath, StringComparer.Ordinal)
                     .ThenBy(static symbol => symbol.StartLine)
                     .ThenBy(static symbol => symbol.SymbolId, StringComparer.Ordinal))
        {
            if (seed.IsTest && !seed.TestLifecycle)
            {
                changedTestNodes.Add(new ReachedNode(
                    seed.SymbolId,
                    Hop: 0,
                    ReachedVia: null,
                    EdgeKind: "changed_test",
                    EdgeConfidence: 1.0,
                    EdgeSource: "seed",
                    PathCertainty: ReferenceResolutionStatus.Exact,
                    Visibility: seed.Visibility));
            }
        }

        var combinedGraphNodes = changedTestNodes.Concat(graphResult.Nodes).ToList();
        HeuristicExpansion expansion = AddHeuristicTestCandidates(
            index, seedIds, combinedGraphNodes, combinedGraphNodes.Count + effectiveTestsLimit, seeds);
        var symbolsById =
            SymbolLookupBatch.FindBySymbolIds(index, expansion.Nodes.Select(static node => node.Id));

        var allSignals = expansion.Nodes
            .Where(node => symbolsById.ContainsKey(node.Id))
            .Select(node =>
            {
                IndexedSymbol symbol = symbolsById[node.Id];
                return (Signal: new ImpactRankSignal(node, symbol.FilePath, symbol.StartLine, symbol.Name, symbol.SymbolId), Symbol: symbol);
            })
            .ToList();

        bool useDedicatedBudgets = isTestsOnly || testsLimit.HasValue || symbolsLimit.HasValue;
        ImpactRankSignal[] selectedTests;
        ImpactRankSignal[] selectedSymbols;
        var impacted = new List<ImpactSymbolHit>();
        var tests = new List<ImpactSymbolHit>();

        if (useDedicatedBudgets)
        {
            var testSignals = allSignals.Where(x => x.Symbol.IsTest).Select(x => x.Signal);
            var symbolSignals = allSignals.Where(x => !x.Symbol.IsTest).Select(x => x.Signal);

            selectedTests = ImpactRanker.Rank(testSignals)
                .Take(effectiveTestsLimit)
                .ToArray();

            selectedSymbols = isTestsOnly
                ? Array.Empty<ImpactRankSignal>()
                : ImpactRanker.Rank(symbolSignals)
                    .Take(effectiveSymbolsLimit)
                    .ToArray();

            foreach (ImpactRankSignal candidate in selectedTests)
            {
                IndexedSymbol symbol = symbolsById[candidate.SymbolId];
                tests.Add(new ImpactSymbolHit(symbol, candidate.Evidence));
            }

            foreach (ImpactRankSignal candidate in selectedSymbols)
            {
                IndexedSymbol symbol = symbolsById[candidate.SymbolId];
                impacted.Add(new ImpactSymbolHit(symbol, candidate.Evidence));
            }
        }
        else
        {
            ImpactRankSignal[] selected = ImpactRanker.Rank(allSignals.Select(x => x.Signal))
                .Take(limit)
                .ToArray();

            var selectedTestList = new List<ImpactRankSignal>();
            var selectedSymbolList = new List<ImpactRankSignal>();
            foreach (ImpactRankSignal candidate in selected)
            {
                IndexedSymbol symbol = symbolsById[candidate.SymbolId];
                if (symbol.IsTest)
                {
                    selectedTestList.Add(candidate);
                    tests.Add(new ImpactSymbolHit(symbol, candidate.Evidence));
                }
                else
                {
                    selectedSymbolList.Add(candidate);
                    impacted.Add(new ImpactSymbolHit(symbol, candidate.Evidence));
                }
            }
            selectedTests = selectedTestList.ToArray();
            selectedSymbols = selectedSymbolList.ToArray();
        }

        int returnedTestCandidateCount = selectedTests.Count(static candidate =>
            string.Equals(candidate.Evidence.EdgeSource, "filename_role", StringComparison.Ordinal));
        int returnedGraphTestCount = selectedTests.Length - returnedTestCandidateCount - changedTestNodes.Count;
        int returnedGraphCount = selectedSymbols.Length + Math.Max(0, returnedGraphTestCount);
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
        int limit,
        IReadOnlyDictionary<string, IndexedSymbol> seeds)
    {
        var combined = new List<ReachedNode>(graphNodes);
        if (limit <= graphNodes.Count)
            return new(combined, 0, false);

        int candidateCount = 0;
        var seen = new HashSet<string>(
            seedIds.Concat(graphNodes.Select(static node => node.Id)),
            StringComparer.Ordinal);
        var seenStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IndexedSymbol seed in seeds.Values
                     .OrderBy(static symbol => symbol.FilePath, StringComparer.Ordinal)
                     .ThenBy(static symbol => symbol.StartLine)
                     .ThenBy(static symbol => symbol.SymbolId, StringComparer.Ordinal))
        {
            if (seed.IsTest)
                continue;

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
                    Visibility: candidate.Visibility,
                    PathCertainty: ReferenceResolutionStatus.Heuristic));
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
