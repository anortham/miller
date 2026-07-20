using Miller.Core.Search;
using Miller.Indexing;
using Miller.Indexing.Semantic;

namespace Miller.Server.Tools;

internal sealed record SearchRouteExecutionRequest(
    string Query,
    int Limit,
    bool Json,
    bool? ExcludeTests,
    string? CompactBanner = null,
    string? FilePattern = null,
    string? Language = null,
    Func<IReadOnlyCollection<string>, IReadOnlySet<string>>? HasDocLookup = null,
    Func<string, IReadOnlyList<IndexedSymbol>>? SuggestionLookup = null,
    ISymbolFusionArm? FusionArm = null);

/// <summary>
/// Everything an additional retrieval arm may see about the lexical stage: the query, the ranking it already
/// produced, the page size, and the visibility predicate that ranking was filtered by.
/// </summary>
/// <param name="Allows">
/// The lexical arm's own visibility rules. An arm that admits a symbol this rejects would surface a test
/// symbol or an out-of-filter file the same query answered lexically would have hidden.
/// </param>
public sealed record SymbolFusionRequest(
    string Query,
    IReadOnlyList<SymbolCandidate> Candidates,
    int Limit,
    Func<IndexedSymbol, bool> Allows);

/// <summary>
/// An additional retrieval arm offered the lexical candidate list. Returning <c>null</c> means "this query is
/// not mine" — the pre-existing lexical path then runs untouched, which is how off, shadow, an unready
/// artifact, a lexical-only route, and any arm failure all resolve to byte-identical lexical output.
/// </summary>
public interface ISymbolFusionArm
{
    IReadOnlyList<FusedCandidate>? Fuse(ISymbolLookupIndex index, SymbolFusionRequest request);
}

internal sealed record SearchRouteExecutionResult(string Output, int Count, long SourceBytes = 0);

internal static class SearchRouteExecutor
{
    /// <summary>
    /// Candidate generation for the symbols route: the single seam between ranking and rendering. An
    /// additional retrieval arm fuses into the returned candidate list here, and
    /// <see cref="RunSymbols"/> renders whatever it is handed.
    /// </summary>
    public static SymbolCandidateSet CollectSymbolCandidates(
        ISymbolLookupIndex index,
        SearchRoute route,
        SearchRouteExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(request);
        EnsureKind(route, SearchRouteKind.Symbols);

        return SearchTool.CollectSymbolCandidates(
            index,
            request.Query,
            route.Mode,
            request.Limit,
            request.ExcludeTests,
            request.FilePattern,
            request.Language);
    }

    public static SearchRouteExecutionResult RunSymbols(
        ISymbolLookupIndex index,
        SearchRoute route,
        SearchRouteExecutionRequest request)
    {
        SymbolCandidateSet candidates = CollectSymbolCandidates(index, route, request);
        IReadOnlyDictionary<string, FusedCandidate>? fusion = null;

        if (request.FusionArm is { } arm &&
            !candidates.FileMode &&
            arm.Fuse(index, FusionRequestFor(candidates, request)) is { Count: > 0 } fused)
        {
            candidates = candidates with { Candidates = [.. fused.Select(static row => row.Candidate)] };
            fusion = fused.ToDictionary(static row => row.Candidate.SymbolId, StringComparer.Ordinal);
        }

        string output = SearchTool.RenderSymbolCandidates(
            candidates,
            request.Query,
            route.Mode,
            request.Limit,
            request.Json,
            out int count,
            request.CompactBanner,
            request.HasDocLookup,
            fusion);

        return new SearchRouteExecutionResult(output, count);
    }

    public static SearchRouteExecutionResult RunContent(
        ITextContentSearchIndex index,
        SearchRoute route,
        SearchRouteExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(request);
        EnsureKind(route, SearchRouteKind.Content);

        string output = SearchTool.RunContentCorpus(
            index,
            request.Query,
            request.Limit,
            request.Json,
            out int count,
            out long sourceBytes,
            request.CompactBanner,
            request.FilePattern,
            request.Language,
            request.SuggestionLookup);

        return new SearchRouteExecutionResult(output, count, sourceBytes);
    }

    public static SearchRouteExecutionResult RunTextContent(
        ITextContentSearchIndex index,
        SearchRoute route,
        SearchRouteExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(request);
        EnsureKind(route, SearchRouteKind.TextContent);

        bool hideTests = SearchTool.ResolveExcludeTests(request.ExcludeTests, request.Query, route.Mode);
        string output = SearchTool.RunTextContent(
            index,
            request.Query,
            route.ContentKinds!,
            request.Limit,
            hideTests,
            request.Json,
            out int count,
            out long sourceBytes,
            request.CompactBanner,
            request.FilePattern,
            request.Language,
            request.SuggestionLookup);

        return new SearchRouteExecutionResult(output, count, sourceBytes);
    }

    public static SearchRouteExecutionResult RunRegions(
        IRegionSearchIndex index,
        SearchRoute route,
        SearchRouteExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(request);
        EnsureKind(route, SearchRouteKind.Regions);

        bool hideTests = SearchTool.ResolveExcludeTests(request.ExcludeTests, request.Query, route.Mode);
        string output = SearchTool.RunRegions(
            index,
            request.Query,
            route.RegionKinds!,
            request.Limit,
            hideTests,
            request.Json,
            out int count,
            request.CompactBanner,
            route.ModeNote,
            request.FilePattern,
            request.Language);

        return new SearchRouteExecutionResult(output, count);
    }

    public static SearchRouteExecutionResult RunMarkers(
        IRegionSearchIndex index,
        SearchRoute route,
        SearchRouteExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(request);
        EnsureKind(route, SearchRouteKind.Markers);

        bool hideTests = request.ExcludeTests ?? false;
        IReadOnlyList<string> markers = MarkerSearch.ParseMarkers(request.Query);
        string output = MarkerSearch.Run(
            index,
            markers,
            request.Limit,
            hideTests,
            request.Json,
            request.CompactBanner,
            request.FilePattern,
            request.Language,
            out int count);

        return new SearchRouteExecutionResult(output, count);
    }

    private static SymbolFusionRequest FusionRequestFor(
        SymbolCandidateSet candidates,
        SearchRouteExecutionRequest request) =>
        new(
            request.Query,
            candidates.Candidates,
            request.Limit,
            candidates.Visibility is { } visibility ? visibility.Allows : static _ => true);

    private static void EnsureKind(SearchRoute route, SearchRouteKind expected)
    {
        if (route.Kind != expected)
            throw new InvalidOperationException($"Search route {route.Kind} cannot run as {expected}.");
    }
}

/// <summary>
/// The local semantic arm offered to the symbol route (ADR-0003): route the query, retrieve neighbours the
/// lexical filters would also have admitted, and fuse the two rankings under the frozen
/// <see cref="RrfFusion.FusionProfile"/> weights.
/// </summary>
/// <remarks>
/// <para>Every abstention returns <c>null</c> rather than an empty list, because an empty list is a real
/// retrieval answer while <c>null</c> means the lexical bytes must be handed back untouched. Only
/// <see cref="SemanticMode.On"/> fuses: under <c>shadow</c> vectors are built and evaluated but never served,
/// and under <c>off</c> nothing is asked at all.</para>
/// <para>The allow predicate re-applies the lexical arm's own visibility rules, so the semantic arm cannot
/// surface a test symbol or an out-of-filter file the same query would have hidden — and because the arm
/// answers a rejecting filter by fetching deeper, filtering here costs recall rather than buying it.</para>
/// </remarks>
internal sealed class SemanticSymbolFusionArm(SemanticMode mode, Func<SemanticSearchArm> openArm) : ISymbolFusionArm
{
    private const int MinimumRecall = 10;

    public SemanticSymbolFusionArm(SemanticMode mode, SemanticSearchArm arm)
        : this(mode, () => arm)
    {
    }

    public IReadOnlyList<FusedCandidate>? Fuse(ISymbolLookupIndex index, SymbolFusionRequest request)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(request);

        if (mode is not SemanticMode.On)
            return null;

        SemanticQueryRoute route = SemanticQueryPolicy.Route(request.Query, EvidenceFrom(request.Candidates));
        if (!route.IsHybrid)
            return null;

        int k = Math.Clamp(request.Limit * 2, MinimumRecall, SemanticSearchArm.MaxCandidates);
        SemanticQueryResult result = openArm()
            .QuerySymbolsAsync(request.Query, k, match => Admits(index, request, match))
            .GetAwaiter()
            .GetResult();

        if (!result.Served || result.Hits.Count == 0)
            return null;

        var semantic = new List<SemanticRankedCandidate>(result.Hits.Count);
        foreach (SemanticHit hit in result.Hits)
        {
            if (hit.SymbolId is { } symbolId && index.FindBySymbolId(symbolId) is { } symbol)
                semantic.Add(new SemanticRankedCandidate(SearchTool.ToCandidate(symbol, score: 0), hit.Rank));
        }

        return semantic.Count == 0
            ? null
            : RrfFusion.Fuse(request.Candidates, semantic, RrfFusion.WeightsFor(route.HybridClass));
    }

    private static bool Admits(ISymbolLookupIndex index, SymbolFusionRequest request, VectorMatch match) =>
        index.FindBySymbolId(match.UnitId) is { } symbol && request.Allows(symbol);

    /// <summary>
    /// The lexical arm's own confidence, which the policy consults only for shape-ambiguous queries: the top
    /// two scores of the ranking that already ran, so no extra retrieval happens to produce it.
    /// </summary>
    private static LexicalEvidence EvidenceFrom(IReadOnlyList<SymbolCandidate> candidates) => candidates.Count switch
    {
        0 => LexicalEvidence.None,
        1 => new LexicalEvidence(1, candidates[0].Score, 0),
        _ => new LexicalEvidence(candidates.Count, candidates[0].Score, candidates[1].Score),
    };
}
