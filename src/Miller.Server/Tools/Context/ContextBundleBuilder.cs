using System.Text;
using System.Text.RegularExpressions;
using Miller.Core.Graph;
using Miller.Core.References;
using Miller.Core.Search;
using Miller.Core.Tokenization;
using Miller.Indexing;
using Miller.Indexing.Semantic;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;

namespace Miller.Server.Tools.Context;

internal static partial class ContextBundleBuilder
{
    /// <summary>Rerank-only admission uses this narrow membership set; widening it admits additional semantic seeds.</summary>
    private const int SemanticSeedGateLimit = 2;
    private const int TermRescuePromotionReadLimit = 8;
    private const int TermRescueRetrievalLimit = 6;
    private const int SearchSeedLimit = 10;
    internal const int AnchorAmbiguousMatchLimit = 10;
    internal const int AnchorIdentifierTokenLimit = 24;
    internal const int AnchorMatchesPerToken = 6;
    internal const int AnchorStackFrameLimit = 24;
    private const int NoRetrievalRank = int.MaxValue;
    private const int ReachCap = 500;
    internal const string ReferenceEvidenceBatchEnvironmentVariable = "MILLER_CONTEXT_REFERENCE_BATCH";
    internal const int ReferenceReadOverscanFactor = 2;
    internal const int ReferenceReadChunkSize = 8;
    private const int IdentifierAllocationTier = 2;
    internal const int TermRescueStrengthCap = 18;
    internal const int SourceRescueStrength = 35;
    internal const int SemanticSeedStrength = 26;
    internal const int SourceRescueHitLimit = 6;
    internal const int SourceRescueSeedLimit = 3;
    private static readonly string[] SourceRescueContentKinds =
    [
        TextContentKind.WorkspaceSource,
        TextContentKind.WorkspaceDocs,
    ];
    private const int TaskQueryNameWeight = 12;
    private const int TaskQueryPathWeight = 8;
    private const int TaskQuerySignatureWeight = 5;
    private const int TaskQueryKindWeight = 15;
    private const int TaskQueryAffinityCap = 50;
    private static readonly HashSet<string> ContextQueryStopWords = new(
        [
            "assemble", "change", "context", "current", "does", "edit", "explain", "find", "give",
            "how", "implementation", "intended", "locate", "minimal", "needed", "propose", "recognized",
            "required", "selecting", "server", "target", "then", "used", "which", "with", "without",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static bool ReferenceEvidenceBatchEnabled
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable(ReferenceEvidenceBatchEnvironmentVariable);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "on", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static ContextBundleBuildResult BuildActionable(
        ISymbolLookupIndex index,
        ISymbolGraphReachability graph,
        SmartTargetResolver resolver,
        string query,
        int tokenBudget,
        int maxHops,
        IReadOnlyList<string>? entrySymbols,
        IReadOnlyList<string>? editedFiles,
        string? failingTest,
        string? stackTrace,
        IReadOnlyList<ContextSemanticSeed>? semanticSeeds,
        IReadOnlyList<ContextSourceSeed>? sourceSeeds,
        Func<IndexedSymbol, ExtractReader.BodyReadResult>? readBody,
        Func<IReadOnlyList<string>, IReadOnlyDictionary<string, OutgoingReferenceEvidenceSet>>? readOutgoingMany,
        CancellationToken cancellationToken,
        Action<string>? phaseObserver,
        ContextQueryRetrieval? retrieval)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<Candidate> candidates = BuildCandidates(
            index,
            graph,
            resolver,
            query,
            maxHops,
            entrySymbols,
            editedFiles,
            failingTest,
            stackTrace,
            semanticSeeds,
            sourceSeeds,
            readOutgoingMany,
            out IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
            out int candidatesExamined,
            cancellationToken,
            phaseObserver,
            retrieval);
        phaseObserver?.Invoke("candidate_build");
        candidates = AttachPivotBodies(candidates, tokenBudget, readBody, cancellationToken);
        phaseObserver?.Invoke("pivot_bodies");
        return new ContextBundleBuildResult(candidates, anchorDiagnostics, candidatesExamined);
    }

    internal static ContextReferenceBuildResult BuildReferenceAware(
        ISymbolLookupIndex index,
        ISymbolGraphReachability graph,
        SmartTargetResolver resolver,
        string query,
        int tokenBudget,
        int maxHops,
        IReadOnlyList<string>? entrySymbols,
        IReadOnlyList<string>? editedFiles,
        string? failingTest,
        string? stackTrace,
        IReadOnlyList<ContextSemanticSeed>? semanticSeeds,
        IReadOnlyList<ContextSourceSeed>? sourceSeeds,
        Func<IndexedSymbol, ExtractReader.BodyReadResult>? readBody,
        int referenceDepth,
        bool excludeTests,
        Func<IndexedSymbol, ReferenceEvidenceSet> readReferenceEvidence,
        Func<IndexedSymbol, OutgoingReferenceEvidenceSet> readOutgoingEvidence,
        Func<IReadOnlyList<IndexedSymbol>, bool, IReadOnlyList<TextContentSearchHit>> readContentChunks,
        Func<IReadOnlyList<IndexedSymbol>, IReadOnlyDictionary<string, ReferenceEvidenceBundle>>? readMany,
        Func<IReadOnlyList<ReferenceContextItem>, IReadOnlyList<ContextAnchorDiagnostic>, string, long>
            renderTokenEstimator,
        CancellationToken cancellationToken,
        Action<string>? phaseObserver,
        ContextQueryRetrieval? retrieval)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(readReferenceEvidence);
        ArgumentNullException.ThrowIfNull(readOutgoingEvidence);
        ArgumentNullException.ThrowIfNull(readContentChunks);
        ArgumentNullException.ThrowIfNull(renderTokenEstimator);
        referenceDepth = Math.Clamp(referenceDepth, 0, 1);

        IReadOnlyList<Candidate> candidates = BuildCandidates(
            index,
            graph,
            resolver,
            query,
            maxHops,
            entrySymbols,
            editedFiles,
            failingTest,
            stackTrace,
            semanticSeeds,
            sourceSeeds,
            symbolIds => ReadOutgoingForSymbols(
                index,
                symbolIds,
                readOutgoingEvidence,
                readMany,
                cancellationToken),
            out IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
            out int candidatesExamined,
            cancellationToken,
            phaseObserver,
            retrieval);
        phaseObserver?.Invoke("candidate_build");
        candidates = AttachPivotBodies(candidates, tokenBudget, readBody, cancellationToken);
        phaseObserver?.Invoke("pivot_bodies");
        if (candidates.Count == 0)
        {
            return new ContextReferenceBuildResult(
                candidates,
                [],
                anchorDiagnostics,
                candidatesExamined,
                default);
        }

        IReadOnlyList<ReferenceContextItem> items = BuildReferenceItems(
            candidates,
            tokenBudget,
            referenceDepth,
            excludeTests,
            readReferenceEvidence,
            readOutgoingEvidence,
            readContentChunks,
            readMany,
            anchorDiagnostics,
            query,
            renderTokenEstimator,
            out ContextReferenceReadCounts readCounts,
            cancellationToken);
        return new ContextReferenceBuildResult(
            candidates,
            items,
            anchorDiagnostics,
            candidatesExamined,
            readCounts);
    }

    internal static IReadOnlyList<ContextSemanticSeed> LoadSemanticSeeds(
        VectorSidecar? semanticSidecar,
        ISemanticTextArm? semanticArm,
        string workspaceRoot,
        ISymbolLookupIndex index,
        string query,
        bool excludeTests,
        ContextQueryRetrieval retrieval)
    {
        if (semanticSidecar is not { Mode: SemanticMode.On } ||
            semanticArm is null ||
            string.IsNullOrWhiteSpace(query) ||
            !SemanticQueryPolicy.Route(query).IsHybrid)
        {
            return [];
        }

        SymbolCandidateSet lexical = retrieval.Collect(query, SemanticSeedGateLimit, excludeTests);
        var evidence = new LexicalEvidence(
            lexical.Candidates.Count,
            lexical.Candidates.Count > 0 ? lexical.Candidates[0].Score : 0,
            lexical.Candidates.Count > 1 ? lexical.Candidates[1].Score : 0);
        SemanticCandidateAdmission admission = SemanticQueryPolicy.DecideAdmission(evidence);
        var lexicalIds = new HashSet<string>(
            lexical.Candidates.Select(static candidate => candidate.SymbolId),
            StringComparer.Ordinal);
        SemanticQueryResult result = semanticArm.QuerySymbols(workspaceRoot, query, SearchSeedLimit, allow: null);
        if (!result.Served)
            return [];

        var seeds = new List<ContextSemanticSeed>(result.Hits.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (SemanticHit hit in result.Hits.Take(SearchSeedLimit))
        {
            if (hit.SymbolId is not { } symbolId ||
                !admission.AllowsExpansion && !lexicalIds.Contains(symbolId) ||
                !seen.Add(symbolId) ||
                index.FindBySymbolId(symbolId) is not { } symbol ||
                excludeTests && (symbol.IsTest || IsTestPath.Check(symbol.FilePath)))
            {
                continue;
            }

            seeds.Add(new ContextSemanticSeed(symbol, hit.Rank, hit.Cosine));
        }
        return seeds;
    }

    internal static IReadOnlyList<ContextSourceSeed> LoadSourceRescueSeeds(
        ISymbolLookupIndex index,
        ITextContentSearchIndex? contentIndex,
        string query,
        bool excludeTests)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (contentIndex is null ||
            string.IsNullOrWhiteSpace(query) ||
            !IsNaturalLanguagePhrase(query))
        {
            return [];
        }

        IReadOnlyList<TextContentSearchHit> hits;
        try
        {
            hits = contentIndex.Search(query, SourceRescueContentKinds, SourceRescueHitLimit, excludeTests);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or FileNotFoundException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException
                or Microsoft.Data.Sqlite.SqliteException)
        {
            return [];
        }

        var seeds = new List<ContextSourceSeed>(SourceRescueSeedLimit);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int rank = 0;
        foreach (TextContentSearchHit hit in hits)
        {
            if (hit.ContainingSymbolId is not { Length: > 0 } symbolId ||
                index.FindBySymbolId(symbolId) is not { } symbol)
            {
                continue;
            }

            symbol = PreferDefinitionPivot(index, symbol);
            if (!IsQueryPivot(symbol) ||
                excludeTests && (symbol.IsTest || IsTestPath.Check(symbol.FilePath)) ||
                !seen.Add(symbol.SymbolId))
            {
                continue;
            }

            rank++;
            seeds.Add(new ContextSourceSeed(symbol, rank));
            if (seeds.Count >= SourceRescueSeedLimit)
                break;
        }

        return seeds;
    }

    /// <summary>
    /// A natural-language phrase is multiple whitespace-delimited words (mirrors SearchTool policy).
    /// </summary>
    private static bool IsNaturalLanguagePhrase(string query)
    {
        int words = query.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries).Length;
        return words >= 2;
    }


    private static IReadOnlyDictionary<string, OutgoingReferenceEvidenceSet> ReadOutgoingForSymbols(
        ISymbolLookupIndex index,
        IReadOnlyList<string> symbolIds,
        Func<IndexedSymbol, OutgoingReferenceEvidenceSet> readOutgoingEvidence,
        Func<IReadOnlyList<IndexedSymbol>, IReadOnlyDictionary<string, ReferenceEvidenceBundle>>? readMany,
        CancellationToken cancellationToken)
    {
        var symbols = new List<IndexedSymbol>(symbolIds.Count);
        foreach (string symbolId in symbolIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index.FindBySymbolId(symbolId) is { } symbol)
                symbols.Add(symbol);
        }

        var outgoing = new Dictionary<string, OutgoingReferenceEvidenceSet>(
            symbols.Count,
            StringComparer.Ordinal);
        if (symbols.Count == 0)
            return outgoing;

        if (readMany is not null && ReferenceEvidenceBatchEnabled)
        {
            foreach ((string symbolId, ReferenceEvidenceBundle bundle) in readMany(symbols))
                outgoing[symbolId] = bundle.Outgoing;
            return outgoing;
        }

        foreach (IndexedSymbol symbol in symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outgoing[symbol.SymbolId] = readOutgoingEvidence(symbol);
        }
        return outgoing;
    }


    private static IReadOnlyList<Candidate> BuildCandidates(
        ISymbolLookupIndex index, ISymbolGraphReachability graph, SmartTargetResolver resolver,
        string query, int maxHops, IReadOnlyList<string>? entrySymbols, string? failingTest, string? stackTrace,
        out int candidatesExamined) =>
        BuildCandidates(
            index,
            graph,
            resolver,
            query,
            maxHops,
            entrySymbols,
            editedFiles: null,
            failingTest,
            stackTrace,
            semanticSeeds: null,
            sourceSeeds: null,
            readOutgoingMany: null,
            out _,
            out candidatesExamined);

    private static IReadOnlyList<Candidate> BuildCandidates(
        ISymbolLookupIndex index,
        ISymbolGraphReachability graph,
        SmartTargetResolver resolver,
        string query,
        int maxHops,
        IReadOnlyList<string>? entrySymbols,
        IReadOnlyList<string>? editedFiles,
        string? failingTest,
        string? stackTrace,
        IReadOnlyList<ContextSemanticSeed>? semanticSeeds,
        out IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
        out int candidatesExamined) =>
        BuildCandidates(
            index,
            graph,
            resolver,
            query,
            maxHops,
            entrySymbols,
            editedFiles,
            failingTest,
            stackTrace,
            semanticSeeds,
            sourceSeeds: null,
            readOutgoingMany: null,
            out anchorDiagnostics,
            out candidatesExamined);

    private static IReadOnlyList<Candidate> BuildCandidates(
        ISymbolLookupIndex index,
        ISymbolGraphReachability graph,
        SmartTargetResolver resolver,
        string query,
        int maxHops,
        IReadOnlyList<string>? entrySymbols,
        IReadOnlyList<string>? editedFiles,
        string? failingTest,
        string? stackTrace,
        IReadOnlyList<ContextSemanticSeed>? semanticSeeds,
        IReadOnlyList<ContextSourceSeed>? sourceSeeds,
        out IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
        out int candidatesExamined) =>
        BuildCandidates(
            index,
            graph,
            resolver,
            query,
            maxHops,
            entrySymbols,
            editedFiles,
            failingTest,
            stackTrace,
            semanticSeeds,
            sourceSeeds,
            readOutgoingMany: null,
            out anchorDiagnostics,
            out candidatesExamined);

    private static IReadOnlyList<Candidate> BuildCandidates(
        ISymbolLookupIndex index,
        ISymbolGraphReachability graph,
        SmartTargetResolver resolver,
        string query,
        int maxHops,
        IReadOnlyList<string>? entrySymbols,
        IReadOnlyList<string>? editedFiles,
        string? failingTest,
        string? stackTrace,
        IReadOnlyList<ContextSemanticSeed>? semanticSeeds,
        IReadOnlyList<ContextSourceSeed>? sourceSeeds,
        Func<IReadOnlyList<string>, IReadOnlyDictionary<string, OutgoingReferenceEvidenceSet>>? readOutgoingMany,
        out IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
        out int candidatesExamined,
        CancellationToken cancellationToken = default,
        Action<string>? phaseObserver = null,
        ContextQueryRetrieval? retrieval = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        retrieval = ContextQueryRetrieval.For(index, retrieval);
        if (maxHops < 0) maxHops = 0;
        if (maxHops > 2) maxHops = 2;
        candidatesExamined = 0;
        var diagnostics = new List<ContextAnchorDiagnostic>();
        var signals = new List<ContextPivotSignal>();
        var symbols = new Dictionary<string, IndexedSymbol>(StringComparer.Ordinal);
        var reasons = new Dictionary<string, (int Strength, int Order, string Reason, int? Line)>(
            StringComparer.Ordinal);
        int anchorOrder = 0;
        IReadOnlyList<string> queryTerms = ContextQueryTerms(query);

        void AddSignal(
            IndexedSymbol symbol,
            int retrievalRank,
            double retrievalScore,
            int anchorStrength,
            string reason,
            int? anchorLine = null,
            int? lineDistance = null,
            bool pinned = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int order = anchorOrder++;
            symbols[symbol.SymbolId] = symbol;
            signals.Add(new ContextPivotSignal(
                symbol.SymbolId,
                retrievalRank,
                retrievalScore,
                anchorStrength,
                order,
                lineDistance,
                ContextPivotDiversityKey(symbol.Name),
                symbol.FilePath,
                symbol.IsTest || IsTestPath.Check(symbol.FilePath),
                pinned));
            if (!reasons.TryGetValue(symbol.SymbolId, out var existing) ||
                anchorStrength > existing.Strength ||
                anchorStrength == existing.Strength && order < existing.Order)
            {
                reasons[symbol.SymbolId] = (anchorStrength, order, reason, anchorLine);
            }
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Parent-query auto policy for both arms: one-word term rescue must not reintroduce
            // tests when the original natural-language query would hide them.
            bool excludeTests = SearchTool.ResolveExcludeTests(null, query, SearchToolMode.Symbol);
            SymbolCandidateSet retrieved = retrieval.Collect(query, SearchSeedLimit, excludeTests);
            cancellationToken.ThrowIfCancellationRequested();
            int retrievedCount = Math.Min(retrieved.Candidates.Count, SearchSeedLimit);
            for (int rank = 0; rank < retrievedCount; rank++)
            {
                SymbolCandidate candidate = retrieved.Candidates[rank];
                if (index.FindBySymbolId(candidate.SymbolId) is { } symbol && IsQueryPivot(symbol))
                {
                    symbol = PreferDefinitionPivot(index, symbol);
                    AddSignal(
                        symbol,
                        rank + 1,
                        candidate.Score,
                        TaskQueryAffinity(symbol, queryTerms),
                        $"query_rank_{rank + 1}");
                }
            }
            phaseObserver?.Invoke("query_retrieval");

            foreach (string term in queryTerms)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SymbolCandidateSet termCandidates =
                    retrieval.Collect(term, TermRescueRetrievalLimit, excludeTests);
                cancellationToken.ThrowIfCancellationRequested();
                int termCandidateCount = Math.Min(termCandidates.Candidates.Count, TermRescueRetrievalLimit);
                for (int rank = 0; rank < termCandidateCount; rank++)
                {
                    SymbolCandidate candidate = termCandidates.Candidates[rank];
                    if (index.FindBySymbolId(candidate.SymbolId) is { } symbol && IsQueryPivot(symbol))
                    {
                        symbol = PreferDefinitionPivot(index, symbol);
                        AddSignal(
                            symbol,
                            rank + 1,
                            candidate.Score,
                            Math.Min(TaskQueryAffinity(symbol, queryTerms), TermRescueStrengthCap),
                            $"query_term_{term}");
                    }
                }
            }
            phaseObserver?.Invoke("term_retrieval");

            if (readOutgoingMany is not null && !HasTestOrDefIntent(query))
            {
                PromoteTermRescueTestSubjects(
                    index,
                    queryTerms,
                    excludeTests,
                    readOutgoingMany,
                    retrieval,
                    signals,
                    symbols,
                    reasons,
                    (symbol, rank, score, strength, reason) =>
                        AddSignal(symbol, rank, score, strength, reason),
                    cancellationToken);
            }
        }

        if (entrySymbols is not null)
        {
            foreach (string entry in entrySymbols)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(entry))
                    continue;
                switch (resolver.Resolve(entry))
                {
                    case TargetResolution.Symbol resolved:
                        AddSignal(
                            resolved.Value,
                            NoRetrievalRank,
                            0,
                            100,
                            "entry_symbol",
                            pinned: true);
                        break;
                    case TargetResolution.Candidates ambiguous:
                        IndexedSymbol[] ambiguousMatches = ambiguous.Matches
                            .Take(AnchorAmbiguousMatchLimit + 1)
                            .ToArray();
                        diagnostics.Add(new ContextAnchorDiagnostic(
                            "entry_symbol",
                            entry,
                            ambiguousMatches.Length > AnchorAmbiguousMatchLimit
                                ? "ambiguous_truncated"
                                : "ambiguous"));
                        foreach (IndexedSymbol match in ambiguousMatches.Take(AnchorAmbiguousMatchLimit))
                            AddSignal(match, NoRetrievalRank, 0, 70, "ambiguous_entry_symbol");
                        break;
                    case TargetResolution.File file:
                        foreach (IndexedSymbol match in index.FindByFilePath(file.Path)
                                     .Where(static symbol => IsFilePivotKind(symbol.Kind))
                                     .Take(SearchSeedLimit))
                        {
                            AddSignal(match, NoRetrievalRank, 0, 65, "entry_file");
                        }
                        break;
                    case TargetResolution.NotFound:
                        diagnostics.Add(new ContextAnchorDiagnostic("entry_symbol", entry, "not_found"));
                        break;
                }
            }
        }

        if (editedFiles is not null)
        {
            foreach (string editedFile in editedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(editedFile))
                    continue;
                string? resolvedPath = index.ResolveIndexedFilePath(editedFile);
                IReadOnlyList<IndexedSymbol> matches = resolvedPath is null
                    ? index.FindByFilePathFragment(editedFile, SearchSeedLimit)
                    : index.FindByFilePath(resolvedPath);
                IndexedSymbol[] actionable = matches
                    .Where(static symbol => IsFilePivotKind(symbol.Kind))
                    .Take(SearchSeedLimit)
                    .ToArray();
                if (actionable.Length == 0)
                {
                    diagnostics.Add(new ContextAnchorDiagnostic("edited_file", editedFile, "not_indexed"));
                    continue;
                }
                foreach (IndexedSymbol match in actionable)
                    AddSignal(match, NoRetrievalRank, 0, 85, "edited_file");
            }
        }

        IReadOnlyList<IndexedSymbol> failingTestMatches =
            FindNamedAnchorCandidates(index, failingTest, out bool failingTestTruncated);
        bool failingTestMatched = failingTestMatches.Count > 0;
        foreach (IndexedSymbol match in failingTestMatches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddSignal(match, NoRetrievalRank, 0, 80, "failing_test");
        }
        if (failingTestTruncated)
        {
            diagnostics.Add(new ContextAnchorDiagnostic(
                "failing_test",
                failingTest!,
                failingTestMatched ? "truncated" : "no_symbol_match_truncated"));
        }
        else if (!string.IsNullOrWhiteSpace(failingTest) && !failingTestMatched)
        {
            diagnostics.Add(new ContextAnchorDiagnostic("failing_test", failingTest, "no_symbol_match"));
        }

        bool stackMatched = false;
        IReadOnlyList<(string File, int Line)> stackFrames =
            ParseStackFrames(stackTrace, out bool stackFramesTruncated);
        foreach ((string file, int line) in stackFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? resolvedPath = index.ResolveIndexedFilePath(file);
            IReadOnlyList<IndexedSymbol> matches = resolvedPath is null
                ? index.FindByFilePathFragment(file, SearchSeedLimit)
                : index.FindByFilePath(resolvedPath);
            foreach (IndexedSymbol match in matches
                         .Where(static symbol => IsQueryPivot(symbol))
                         .OrderBy(symbol => LineDistance(symbol, line))
                         .Take(2))
            {
                stackMatched = true;
                AddSignal(
                    match,
                    NoRetrievalRank,
                    0,
                    95,
                    "stack_frame",
                    anchorLine: line,
                    lineDistance: LineDistance(match, line));
            }
        }
        IReadOnlyList<IndexedSymbol> stackSymbolMatches =
            FindNamedAnchorCandidates(index, stackTrace, out bool stackSymbolsTruncated);
        foreach (IndexedSymbol match in stackSymbolMatches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stackMatched = true;
            AddSignal(match, NoRetrievalRank, 0, 90, "stack_symbol");
        }
        if (stackFramesTruncated)
        {
            diagnostics.Add(new ContextAnchorDiagnostic(
                "stack_trace",
                stackTrace!,
                stackMatched ? "frames_truncated" : "no_frame_match_truncated"));
        }
        if (stackSymbolsTruncated)
        {
            diagnostics.Add(new ContextAnchorDiagnostic(
                "stack_trace",
                stackTrace!,
                stackMatched ? "symbols_truncated" : "no_symbol_match_truncated"));
        }
        if (!string.IsNullOrWhiteSpace(stackTrace) &&
            !stackMatched &&
            !stackFramesTruncated &&
            !stackSymbolsTruncated)
        {
            diagnostics.Add(new ContextAnchorDiagnostic("stack_trace", stackTrace, "no_frame_match"));
        }

        if (sourceSeeds is not null)
        {
            foreach (ContextSourceSeed seed in sourceSeeds.Where(static seed => IsQueryPivot(seed.Symbol)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                IndexedSymbol symbol = PreferDefinitionPivot(index, seed.Symbol);
                AddSignal(
                    symbol,
                    SearchSeedLimit + seed.Rank,
                    retrievalScore: 0,
                    SourceRescueStrength,
                    $"source_rescue_{seed.Rank}");
            }
        }

        if (semanticSeeds is not null)
        {
            foreach (ContextSemanticSeed seed in semanticSeeds.Where(static seed => IsQueryPivot(seed.Symbol)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                IndexedSymbol symbol = PreferDefinitionPivot(index, seed.Symbol);
                AddSignal(
                    symbol,
                    SearchSeedLimit + seed.Rank,
                    seed.Score,
                    SemanticSeedStrength,
                    $"semantic_rank_{seed.Rank}");
            }
        }
        phaseObserver?.Invoke("anchor_resolution");

        anchorDiagnostics = diagnostics;
        IReadOnlyList<ContextPivot> pivots = ContextPivotRanker.Rank(signals, limit: 4);
        phaseObserver?.Invoke("pivot_ranking");
        if (pivots.Count == 0)
            return Array.Empty<Candidate>();

        string[] pivotIds = pivots.Select(static pivot => pivot.SymbolId).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ReachedNode> reached = graph.Reach(pivotIds, maxHops, ReachCap, Direction.Both);
        phaseObserver?.Invoke("graph_reach");
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = new List<Candidate>(pivotIds.Length + reached.Count);
        var symbolsById = SymbolLookupBatch.FindBySymbolIds(
            index,
            pivotIds.Concat(reached.Select(static node => node.Id)));
        phaseObserver?.Invoke("symbol_hydration");

        var pivotSymbols = new List<IndexedSymbol>(pivotIds.Length);
        foreach (string pivotId in pivotIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (symbolsById.TryGetValue(pivotId, out IndexedSymbol? symbol))
            {
                pivotSymbols.Add(symbol);
                var reason = reasons[pivotId];
                candidates.Add(new Candidate(
                    symbol,
                    Hop: 0,
                    Reason: reason.Reason,
                    IsPivot: true,
                    AnchorLine: reason.Line));
            }
        }

        NeighbourRelevanceScorer scorer = NeighbourRelevanceScorer.Build(query, pivotSymbols);
        var seenNeighbourIds = new HashSet<string>(pivotIds, StringComparer.Ordinal);
        var scoredReached = new List<(IndexedSymbol Symbol, int Hop, int Score, string Reason)>(
            reached.Count + (pivotSymbols.Count * 2));
        foreach (ReachedNode node in reached)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (seenNeighbourIds.Add(node.Id) &&
                symbolsById.TryGetValue(node.Id, out IndexedSymbol? symbol))
            {
                scoredReached.Add((symbol, node.Hop, scorer.Score(symbol), "graph_neighbor"));
            }
        }
        if (maxHops > 0)
        {
            foreach (IndexedSymbol pivot in pivotSymbols)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int added = 0;
                foreach (IndexedSymbol symbol in index.FindByFilePath(pivot.FilePath)
                             .Where(static symbol => IsQueryPivot(symbol))
                             .OrderByDescending(symbol => TaskQueryAffinity(symbol, queryTerms))
                             .ThenBy(symbol => LineDistance(symbol, pivot.StartLine))
                             .ThenBy(static symbol => symbol.SymbolId, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!seenNeighbourIds.Add(symbol.SymbolId))
                        continue;
                    scoredReached.Add((symbol, 1, scorer.Score(symbol), "file_neighbour"));
                    added++;
                    if (added == 2)
                        break;
                }
            }
        }
        phaseObserver?.Invoke("file_neighbours");
        foreach (var entry in scoredReached
                     .OrderBy(static candidate => candidate.Hop)
                     .ThenByDescending(static candidate => candidate.Score)
                     .ThenBy(static candidate => candidate.Symbol.SymbolId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates.Add(new Candidate(entry.Symbol, entry.Hop, entry.Reason));
        }
        phaseObserver?.Invoke("candidate_ordering");

        candidatesExamined = candidates.Count;
        return candidates;
    }

    /// <summary>
    /// When term rescue surfaces a test on a non-test-intent query, replace it with its sole
    /// exact non-test outgoing subject (discovery-tier <c>query_term_*_subject</c>).
    /// </summary>
    private static void PromoteTermRescueTestSubjects(
        ISymbolLookupIndex index,
        IReadOnlyList<string> queryTerms,
        bool excludeTests,
        Func<IReadOnlyList<string>, IReadOnlyDictionary<string, OutgoingReferenceEvidenceSet>> readOutgoingMany,
        ContextQueryRetrieval retrieval,
        List<ContextPivotSignal> signals,
        Dictionary<string, IndexedSymbol> symbols,
        Dictionary<string, (int Strength, int Order, string Reason, int? Line)> reasons,
        Action<IndexedSymbol, int, double, int, string> addSignal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hits = new Dictionary<string, TermRescueTestHit>(StringComparer.Ordinal);

        void Consider(
            IndexedSymbol testSymbol,
            string term,
            int retrievalRank,
            double retrievalScore,
            int strength)
        {
            if (!(testSymbol.IsTest || IsTestPath.Check(testSymbol.FilePath)))
                return;
            if (!IsQueryPivot(testSymbol))
                return;

            if (hits.TryGetValue(testSymbol.SymbolId, out TermRescueTestHit existing))
            {
                if (strength > existing.Strength ||
                    strength == existing.Strength && retrievalRank < existing.RetrievalRank)
                {
                    hits[testSymbol.SymbolId] = existing with
                    {
                        Term = term,
                        RetrievalRank = retrievalRank,
                        RetrievalScore = retrievalScore,
                        Strength = strength,
                    };
                }

                return;
            }

            hits[testSymbol.SymbolId] = new TermRescueTestHit(
                testSymbol,
                term,
                retrievalRank,
                retrievalScore,
                strength);
        }

        foreach ((string symbolId, (int Strength, int Order, string Reason, int? Line) reason) in reasons)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reason.Reason.StartsWith("query_term_", StringComparison.Ordinal) ||
                reason.Reason.EndsWith("_subject", StringComparison.Ordinal) ||
                !symbols.TryGetValue(symbolId, out IndexedSymbol? symbol))
            {
                continue;
            }

            string term = reason.Reason["query_term_".Length..];
            ContextPivotSignal? signal = null;
            foreach (ContextPivotSignal candidate in signals)
            {
                if (candidate.SymbolId == symbolId &&
                    candidate.AnchorStrength == reason.Strength)
                {
                    signal = candidate;
                    break;
                }
            }

            Consider(
                symbol,
                term,
                signal?.RetrievalRank ?? NoRetrievalRank,
                signal?.RetrievalScore ?? 0,
                reason.Strength);
        }

        if (excludeTests)
        {
            foreach (string term in queryTerms)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SymbolCandidateSet termCandidates = retrieval.Collect(
                    term,
                    TermRescueRetrievalLimit,
                    excludeTests: false);
                int termCandidateCount = Math.Min(termCandidates.Candidates.Count, TermRescueRetrievalLimit);
                for (int rank = 0; rank < termCandidateCount; rank++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SymbolCandidate candidate = termCandidates.Candidates[rank];
                    if (index.FindBySymbolId(candidate.SymbolId) is not { } symbol)
                        continue;
                    symbol = PreferDefinitionPivot(index, symbol);
                    if (!(symbol.IsTest || IsTestPath.Check(symbol.FilePath)))
                        continue;
                    Consider(
                        symbol,
                        term,
                        rank + 1,
                        candidate.Score,
                        Math.Min(TaskQueryAffinity(symbol, queryTerms), TermRescueStrengthCap));
                }
            }
        }

        TermRescueTestHit[] promotions = hits.Values
            .OrderByDescending(static hit => hit.Strength)
            .ThenBy(static hit => hit.RetrievalRank)
            .ThenBy(static hit => hit.Test.FilePath, StringComparer.Ordinal)
            .ThenBy(static hit => hit.Test.StartLine)
            .ThenBy(static hit => hit.Test.SymbolId, StringComparer.Ordinal)
            .Take(TermRescuePromotionReadLimit)
            .ToArray();
        if (promotions.Length == 0)
            return;

        // One read for the whole promotion set. Per-symbol reads paid the reference-resolution load once per
        // promoted test, and the first of them pulled it in even under reference_mode=off.
        IReadOnlyDictionary<string, OutgoingReferenceEvidenceSet> outgoingBySymbol;
        try
        {
            outgoingBySymbol = readOutgoingMany(
                Array.ConvertAll(promotions, static hit => hit.Test.SymbolId));
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException
                or Microsoft.Data.Sqlite.SqliteException)
        {
            // The batch is one read: a failure denies every promotion, as a per-symbol failure denied one.
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        foreach (TermRescueTestHit hit in promotions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!outgoingBySymbol.TryGetValue(hit.Test.SymbolId, out OutgoingReferenceEvidenceSet? outgoing))
                continue;

            if (outgoing.Coverage.ExactTruncated)
                continue;

            var subjectIds = new HashSet<string>(StringComparer.Ordinal);
            IndexedSymbol? soleSubject = null;
            foreach (OutgoingReferenceEvidence edge in outgoing.Exact)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(edge.TargetSymbolId) || !edge.IsExact)
                    continue;
                if (index.FindBySymbolId(edge.TargetSymbolId) is not { } target)
                    continue;
                if (target.IsTest || IsTestPath.Check(target.FilePath))
                    continue;
                if (!subjectIds.Add(target.SymbolId))
                    continue;
                if (subjectIds.Count > 1)
                {
                    soleSubject = null;
                    break;
                }

                soleSubject = target;
            }

            if (soleSubject is null || subjectIds.Count != 1)
                continue;

            IndexedSymbol subject = PreferContainerSubject(index, soleSubject);
            subject = PreferDefinitionPivot(index, subject);
            if (!IsQueryPivot(subject) || subject.IsTest || IsTestPath.Check(subject.FilePath))
                continue;

            signals.RemoveAll(signal => signal.SymbolId == hit.Test.SymbolId);
            symbols.Remove(hit.Test.SymbolId);
            reasons.Remove(hit.Test.SymbolId);

            addSignal(
                subject,
                hit.RetrievalRank,
                hit.RetrievalScore,
                hit.Strength,
                $"query_term_{hit.Term}_subject");
        }
    }

    private static IndexedSymbol PreferContainerSubject(
        ISymbolLookupIndex index,
        IndexedSymbol subject)
    {
        if (!IsMemberKind(subject.Kind) ||
            string.IsNullOrEmpty(subject.ParentId) ||
            index.FindBySymbolId(subject.ParentId) is not { } parent ||
            parent.IsTest ||
            IsTestPath.Check(parent.FilePath) ||
            !IsContainerKind(parent.Kind) ||
            !IsQueryPivot(parent))
        {
            return subject;
        }

        return parent;
    }

    private static bool IsMemberKind(string kind) =>
        kind is "method" or "function" or "property" or "field" or "constructor" or "constant";

    private static bool IsContainerKind(string kind) =>
        kind is "class" or "struct" or "interface" or "enum" or "record" or "type" or "module" or
            "namespace" or "trait" or "impl" or "object";

    /// <summary>Mirrors SearchTool test/def intent: whole words test/tests/spec/specs.</summary>
    internal static bool HasTestOrDefIntent(string query)
    {
        foreach (string word in query.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Equals("test", StringComparison.OrdinalIgnoreCase) ||
                word.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
                word.Equals("spec", StringComparison.OrdinalIgnoreCase) ||
                word.Equals("specs", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct TermRescueTestHit(
        IndexedSymbol Test,
        string Term,
        int RetrievalRank,
        double RetrievalScore,
        int Strength);

    private static bool IsPivotKind(string kind) =>
        kind is not (
            "variable" or "key" or "heading" or "section" or "document" or "table" or "list_item");

    private static bool IsFilePivotKind(string kind) =>
        IsPivotKind(kind) &&
        kind is not (
            "import" or "module" or "export" or "field" or "property" or "constant" or "parameter" or
            "constructor");

    private static bool IsQueryPivot(IndexedSymbol symbol) =>
        IsPivotKind(symbol.Kind) &&
        symbol.Kind is not ("import" or "module" or "field" or "parameter" or "constructor") &&
        !symbol.FilePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
        !symbol.FilePath.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) &&
        !symbol.FilePath.EndsWith(".rst", StringComparison.OrdinalIgnoreCase);

    private static IndexedSymbol PreferDefinitionPivot(
        ISymbolLookupIndex index,
        IndexedSymbol symbol)
    {
        if (symbol.Kind != "export")
            return symbol;

        return index.FindByName(symbol.Name)
            .Where(candidate =>
                candidate.SymbolId != symbol.SymbolId &&
                candidate.Kind != "export" &&
                IsQueryPivot(candidate) &&
                candidate.StartLine == symbol.StartLine &&
                string.Equals(candidate.FilePath, symbol.FilePath, StringComparison.Ordinal))
            .OrderBy(static candidate => candidate.SymbolId, StringComparer.Ordinal)
            .FirstOrDefault() ?? symbol;
    }

    private static IReadOnlyList<string> ContextQueryTerms(string? query)
    {
        var terms = new List<string>(12);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string token in ExtractIdentifierTokens(query))
        {
            string normalized = token.ToLowerInvariant();
            if (normalized.Length < 4 || ContextQueryStopWords.Contains(normalized))
                continue;
            Add(normalized);
            if (char.IsLower(token[0]))
                Add(ContextQueryStem(normalized));
            if (terms.Count >= 12)
                break;
        }
        return terms;

        void Add(string term)
        {
            if (term.Length >= 3 && seen.Add(term))
                terms.Add(term);
        }
    }

    private static string ContextQueryStem(string term)
    {
        if (term.EndsWith("nner", StringComparison.Ordinal) && term.Length > 5)
            return term[..^3];
        if (term.EndsWith("ing", StringComparison.Ordinal) && term.Length > 6)
            return term[..^3];
        if (term.EndsWith("ed", StringComparison.Ordinal) && term.Length > 5)
            return term[..^2];
        if (term.EndsWith('s') && term.Length > 4)
            return term[..^1];
        return term;
    }

    /// <summary>
    /// Lexical affinity of a symbol to task-query terms. Name weight is at least path weight so
    /// path-token matches cannot outrank pure name matches for the same term set.
    /// </summary>
    internal static int TaskQueryAffinity(IndexedSymbol symbol, IReadOnlyList<string> terms)
    {
        var nameTokens = new List<string>();
        var signatureTokens = new List<string>();
        var pathTokens = new List<string>();
        CodeTokenizer.Tokenize(symbol.Name, nameTokens);
        CodeTokenizer.Tokenize(symbol.Signature ?? string.Empty, signatureTokens);
        CodeTokenizer.Tokenize(symbol.FilePath, pathTokens);
        var names = new HashSet<string>(nameTokens, StringComparer.OrdinalIgnoreCase);
        var signatures = new HashSet<string>(signatureTokens, StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(pathTokens, StringComparer.OrdinalIgnoreCase);
        int score = 0;
        int matchedNameTerms = 0;
        foreach (string term in terms)
        {
            if (string.Equals(symbol.Kind, term, StringComparison.OrdinalIgnoreCase))
                score += TaskQueryKindWeight;
            else if (names.Contains(term))
            {
                score += TaskQueryNameWeight;
                matchedNameTerms++;
            }
            else if (signatures.Contains(term))
                score += TaskQuerySignatureWeight;
            else if (paths.Contains(term))
                score += TaskQueryPathWeight;
        }
        if (matchedNameTerms > 1)
            score += (matchedNameTerms - 1) * 10;
        return Math.Min(score, TaskQueryAffinityCap);
    }

    private static string ContextPivotDiversityKey(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char character in name)
        {
            if (char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    private static int LineDistance(IndexedSymbol symbol, int line)
    {
        if (symbol.StartLine <= line && line <= Math.Max(symbol.StartLine, symbol.EndLine))
            return 0;
        return Math.Min(
            Math.Abs(line - symbol.StartLine),
            Math.Abs(line - Math.Max(symbol.StartLine, symbol.EndLine)));
    }

    internal static IReadOnlyList<(string File, int Line)> ParseStackFrames(
        string? stackTrace,
        out bool truncated)
    {
        string text = stackTrace ?? string.Empty;
        Match[] frames = StackFramePattern().Matches(text).Cast<Match>()
            .Concat(PythonStackFramePattern().Matches(text).Cast<Match>())
            .OrderBy(static frame => frame.Index)
            .Take(AnchorStackFrameLimit + 1)
            .ToArray();
        truncated = frames.Length > AnchorStackFrameLimit;
        return frames
            .Take(AnchorStackFrameLimit)
            .Select(static frame => (
                frame.Groups["file"].Value,
                int.Parse(
                    frame.Groups["line"].Value,
                    System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();
    }

    private static IReadOnlyList<Candidate> AttachPivotBodies(
        IReadOnlyList<Candidate> candidates,
        int tokenBudget,
        Func<IndexedSymbol, ExtractReader.BodyReadResult>? readBody,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (readBody is null)
            return candidates;

        int pivotCount = candidates.Count(static candidate => candidate.IsPivot);
        if (pivotCount == 0)
            return candidates;

        int maxBodyChars = Math.Min(2400, Math.Max(80, tokenBudget * 2 / pivotCount));
        var enriched = new Candidate[candidates.Count];
        for (int index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Candidate candidate = candidates[index];
            if (!candidate.IsPivot)
            {
                enriched[index] = candidate;
                continue;
            }

            ExtractReader.BodyReadResult body = readBody(candidate.Symbol);
            cancellationToken.ThrowIfCancellationRequested();
            if (body.Text is { } text)
            {
                bool truncated = text.Length > maxBodyChars;
                enriched[index] = candidate with
                {
                    Body = truncated ? ContextTextBounds.Truncate(text, maxBodyChars) : text,
                    BodyTruncated = truncated,
                };
            }
            else
            {
                enriched[index] = candidate with
                {
                    BodyUnavailableReason = body.UnavailableReason?.ToString(),
                };
            }
        }

        return enriched;
    }

    private readonly struct NeighbourRelevanceScorer
    {
        private static readonly char[] PathSeparators = { '/', '\\' };
        private readonly string[] _tokens;
        private readonly HashSet<string> _pivotFiles;
        private readonly HashSet<string> _pivotDirectories;

        private NeighbourRelevanceScorer(
            string[] tokens,
            HashSet<string> pivotFiles,
            HashSet<string> pivotDirectories)
        {
            _tokens = tokens;
            _pivotFiles = pivotFiles;
            _pivotDirectories = pivotDirectories;
        }

        internal static NeighbourRelevanceScorer Build(string? query, IReadOnlyList<IndexedSymbol> pivots)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string token in ExtractIdentifierTokens(query))
                tokens.Add(token);
            var pivotFiles = new HashSet<string>(StringComparer.Ordinal);
            var pivotDirectories = new HashSet<string>(StringComparer.Ordinal);
            foreach (IndexedSymbol pivot in pivots)
            {
                if (!string.IsNullOrEmpty(pivot.Name))
                    tokens.Add(pivot.Name);
                pivotFiles.Add(pivot.FilePath);
                pivotDirectories.Add(DirectoryOf(pivot.FilePath));
            }

            return new NeighbourRelevanceScorer(tokens.ToArray(), pivotFiles, pivotDirectories);
        }

        internal int Score(IndexedSymbol neighbour)
        {
            int score = 0;
            foreach (string token in _tokens)
            {
                if (neighbour.Name.Contains(token, StringComparison.OrdinalIgnoreCase))
                    score += 2;
            }
            if (_pivotFiles.Contains(neighbour.FilePath))
                score += 1;
            if (_pivotDirectories.Contains(DirectoryOf(neighbour.FilePath)))
                score += 1;
            return score;
        }

        private static string DirectoryOf(string filePath)
        {
            int separator = filePath.LastIndexOfAny(PathSeparators);
            return separator < 0 ? string.Empty : filePath[..separator];
        }
    }


    private static IReadOnlyList<ReferenceContextItem> BuildReferenceItems(
        IReadOnlyList<Candidate> candidates,
        int tokenBudget,
        int referenceDepth,
        bool excludeTests,
        Func<IndexedSymbol, ReferenceEvidenceSet> readReferenceEvidence,
        Func<IndexedSymbol, OutgoingReferenceEvidenceSet> readOutgoingEvidence,
        Func<IReadOnlyList<IndexedSymbol>, bool, IReadOnlyList<TextContentSearchHit>> readContentChunks,
        Func<IReadOnlyList<IndexedSymbol>, IReadOnlyDictionary<string, ReferenceEvidenceBundle>>? readMany,
        IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
        string query,
        Func<IReadOnlyList<ReferenceContextItem>, IReadOnlyList<ContextAnchorDiagnostic>, string, long>
            renderTokenEstimator,
        out ContextReferenceReadCounts readCounts,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var items = new List<ReferenceContextItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var usableCandidates = candidates
            .Where(candidate =>
                !excludeTests ||
                !(candidate.Symbol.IsTest || IsTestPath.Check(candidate.Symbol.FilePath)))
            .ToArray();

        foreach (Candidate candidate in usableCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IndexedSymbol symbol = candidate.Symbol;
            AddItem(new ReferenceContextItem(
                ItemType: "symbol",
                Reason: candidate.Reason,
                Confidence: "exact",
                Name: symbol.Name,
                Kind: symbol.Kind,
                File: symbol.FilePath,
                Line: symbol.StartLine,
                Hop: candidate.Hop,
                Signature: symbol.Signature,
                SymbolId: symbol.SymbolId,
                Role: candidate.IsPivot ? "pivot" : "neighbour"));
            if (candidate.Body is not null)
            {
                AddItem(new ReferenceContextItem(
                    ItemType: "implementation",
                    Reason: "pivot_body",
                    Confidence: "exact",
                    Name: symbol.Name,
                    Kind: symbol.Kind,
                    File: symbol.FilePath,
                    Line: symbol.StartLine,
                    SymbolId: symbol.SymbolId,
                    Snippet: candidate.Body,
                    AnchorReason: candidate.Reason,
                    Role: "pivot_evidence"));
            }
        }

        IReadOnlyList<IndexedSymbol> symbols = usableCandidates.Select(static candidate => candidate.Symbol).ToArray();
        IReadOnlyList<TextContentSearchHit> contentChunks = readContentChunks(symbols, excludeTests);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (TextContentSearchHit hit in contentChunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (excludeTests && IsTestPath.Check(hit.Path ?? hit.DisplayPath))
                continue;
            AddItem(new ReferenceContextItem(
                ItemType: "content_chunk",
                Reason: "containing_chunk",
                Confidence: symbols.Any(symbol => string.Equals(symbol.SymbolId, hit.ContainingSymbolId, StringComparison.Ordinal))
                    ? "exact"
                    : "name_based",
                Name: hit.ContainingSymbolName ?? hit.DisplayPath,
                Kind: hit.ContentKind,
                File: hit.Path ?? hit.DisplayPath,
                Line: hit.Line,
                SourceId: hit.SourceId,
                ChunkId: hit.ChunkId,
                ContainingSymbolId: hit.ContainingSymbolId,
                LineStart: hit.LineStart,
                LineEnd: hit.LineEnd,
                Snippet: hit.Snippet));
        }

        ReferenceContextItem minimumIdentifier = new(
            "identifier", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 0);
        ReferenceContextItem[] fixedItems = items
            .Where(static item => ReferenceAllocationTier(item) <= 1)
            .ToArray();
        ReferenceContextItem[] minimumEvidenceItems = [.. fixedItems, minimumIdentifier];
        int renderBudget = tokenBudget >= 512
            ? Math.Max(1, tokenBudget * 3 / 4)
            : tokenBudget;
        bool evidenceFits = tokenBudget > 0 &&
            renderTokenEstimator(minimumEvidenceItems, anchorDiagnostics, query) <= renderBudget &&
            renderTokenEstimator(fixedItems, anchorDiagnostics, query) <= renderBudget;

        int candidatesRead = 0;
        int candidatesSkipped = 0;
        if (referenceDepth >= 1 && !evidenceFits)
        {
            // The budget cannot carry one identifier beside the fixed items, so every candidate's evidence is
            // skipped. That is a budget decision like the read window's, so it is reported the same way.
            candidatesSkipped = usableCandidates.Length;
        }
        else if (referenceDepth >= 1)
        {
            // The token budget bounds the WORK, not only the output. Evidence is read in candidate order, one
            // batched chunk at a time, and the loop stops once the material already built overscans the budget
            // by ReferenceReadOverscanFactor — the packer would drop that tail anyway, and reading it cost a
            // per-symbol round trip per candidate. The measurement counts only the tiers the window can still
            // add to: a non-pivot symbol item is built for every candidate without any read, so counting it
            // would let a wide candidate set close the window before a single identifier was read.
            long overscanCeiling = (long)tokenBudget * ReferenceReadOverscanFactor;
            long packableCost = 0;
            foreach (ReferenceContextItem built in items)
                packableCost += PackableCost(built);

            for (int start = 0; start < usableCandidates.Length; start += ReferenceReadChunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The first chunk always runs, so a bundle never renders with zero evidence.
                if (start > 0 && packableCost >= overscanCeiling)
                {
                    candidatesSkipped = usableCandidates.Length - start;
                    break;
                }

                int chunkLength = Math.Min(ReferenceReadChunkSize, usableCandidates.Length - start);
                int builtBeforeChunk = items.Count;
                IReadOnlyDictionary<string, ReferenceEvidenceBundle>? evidenceById = null;
                if (readMany is not null && ReferenceEvidenceBatchEnabled)
                {
                    var chunkSymbols = new IndexedSymbol[chunkLength];
                    for (int offset = 0; offset < chunkLength; offset++)
                        chunkSymbols[offset] = usableCandidates[start + offset].Symbol;
                    cancellationToken.ThrowIfCancellationRequested();
                    evidenceById = readMany(chunkSymbols);
                    ArgumentNullException.ThrowIfNull(evidenceById);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                for (int index = start; index < start + chunkLength; index++)
                {
                    Candidate candidate = usableCandidates[index];
                    cancellationToken.ThrowIfCancellationRequested();
                    IndexedSymbol symbol = candidate.Symbol;
                    OutgoingReferenceEvidenceSet outgoing;
                    ReferenceEvidenceSet inbound;
                    if (evidenceById is not null)
                    {
                        if (!evidenceById.TryGetValue(symbol.SymbolId, out ReferenceEvidenceBundle? evidence))
                            continue;
                        outgoing = evidence.Outgoing;
                        inbound = evidence.Inbound;
                    }
                    else
                    {
                        outgoing = readOutgoingEvidence(symbol);
                        inbound = readReferenceEvidence(symbol);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (OutgoingReferenceEvidence callee in outgoing.Exact)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (excludeTests && IsTestPath.Check(callee.FilePath))
                            continue;
                        AddItem(new ReferenceContextItem(
                            ItemType: "identifier",
                            Reason: IsCallLike(callee.Kind) ? "callee" : "dependency",
                            Confidence: "exact",
                            Name: callee.TargetName,
                            Kind: callee.SourceKind,
                            File: callee.FilePath,
                            Line: callee.StartLine ?? 0,
                            ContainingSymbolId: callee.ContainingSymbolId,
                            TargetSymbolId: callee.TargetSymbolId,
                            ResolutionStatus: "exact",
                            Provenance: EvidenceSourceLabel(callee.Source),
                            EvidenceConfidence: callee.Confidence));
                    }

                    foreach (OutgoingReferenceEvidence callee in outgoing.Fallback)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (excludeTests && IsTestPath.Check(callee.FilePath))
                            continue;
                        AddItem(new ReferenceContextItem(
                            ItemType: "identifier",
                            Reason: IsCallLike(callee.Kind) ? "unresolved_callee" : "unresolved_dependency",
                            Confidence: "fallback",
                            Name: callee.TargetName,
                            Kind: callee.SourceKind,
                            File: callee.FilePath,
                            Line: callee.StartLine ?? 0,
                            ContainingSymbolId: callee.ContainingSymbolId,
                            ResolutionStatus: "fallback",
                            Provenance: EvidenceSourceLabel(callee.Source),
                            EvidenceConfidence: callee.Confidence));
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (ReferenceEvidence reference in inbound.Exact)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (excludeTests && IsTestPath.Check(reference.FilePath))
                            continue;
                        AddItem(new ReferenceContextItem(
                            ItemType: "identifier",
                            Reason: "reference",
                            Confidence: "exact",
                            Name: symbol.Name,
                            Kind: reference.SourceKind,
                            File: reference.FilePath,
                            Line: reference.StartLine ?? 0,
                            ContainingSymbolId: reference.ContainingSymbolId,
                            TargetSymbolId: reference.TargetSymbolId,
                            ResolutionStatus: "exact",
                            Provenance: EvidenceSourceLabel(reference.Source),
                            EvidenceConfidence: reference.Confidence));
                    }

                    foreach (ReferenceEvidence reference in inbound.Fallback)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (excludeTests && IsTestPath.Check(reference.FilePath))
                            continue;
                        AddItem(new ReferenceContextItem(
                            ItemType: "identifier",
                            Reason: "possible_reference",
                            Confidence: "fallback",
                            Name: symbol.Name,
                            Kind: reference.SourceKind,
                            File: reference.FilePath,
                            Line: reference.StartLine ?? 0,
                            ContainingSymbolId: reference.ContainingSymbolId,
                            TargetSymbolId: reference.TargetSymbolId,
                            ResolutionStatus: "fallback",
                            Provenance: EvidenceSourceLabel(reference.Source),
                            EvidenceConfidence: reference.Confidence));
                    }
                }

                for (int built = builtBeforeChunk; built < items.Count; built++)
                    packableCost += PackableCost(items[built]);
                candidatesRead += chunkLength;
            }
        }

        readCounts = new ContextReferenceReadCounts(candidatesRead, candidatesSkipped);
        return items;

        void AddItem(ReferenceContextItem item)
        {
            string key = item.ItemType switch
            {
                "symbol" => "symbol:" + item.SymbolId,
                "implementation" => "implementation:" + item.SymbolId,
                "content_chunk" => "chunk:" + item.SourceId + ":" + item.ChunkId,
                "identifier" => "identifier:" + item.Reason + ":" + item.File + ":" + item.Line + ":" + item.Name + ":" + item.Kind + ":" + item.ContainingSymbolId + ":" + item.TargetSymbolId,
                _ => item.ItemType + ":" + item.File + ":" + item.Line + ":" + item.Name,
            };
            if (seen.Add(key))
                items.Add(item);
        }

        static long PackableCost(ReferenceContextItem item) =>
            ReferenceAllocationTier(item) <= IdentifierAllocationTier
                ? TokenEstimator.Count(ReferenceCostLine(item))
                : 0;
    }

    internal static IEnumerable<string> ExtractIdentifierTokens(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
            yield break;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in IdentifierPattern().Matches(hint))
        {
            string token = m.Value;
            if (token.Length >= 2 && seen.Add(token))
                yield return token;
        }
    }

    internal static IReadOnlyList<IndexedSymbol> FindNamedAnchorCandidates(
        ISymbolLookupIndex index,
        string? hint,
        out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(index);
        var matches = new List<IndexedSymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string[] tokens = ExtractIdentifierTokens(hint)
            .Take(AnchorIdentifierTokenLimit + 1)
            .ToArray();
        truncated = tokens.Length > AnchorIdentifierTokenLimit;
        foreach (string token in tokens.Take(AnchorIdentifierTokenLimit))
        {
            IndexedSymbol[] tokenMatches = index.FindByName(token)
                .Where(static symbol => IsQueryPivot(symbol))
                .Take(AnchorMatchesPerToken + 1)
                .ToArray();
            if (tokenMatches.Length > AnchorMatchesPerToken)
                truncated = true;
            foreach (IndexedSymbol match in tokenMatches.Take(AnchorMatchesPerToken))
            {
                if (seen.Add(match.SymbolId))
                    matches.Add(match);
            }
        }
        return matches;
    }

    [GeneratedRegex("[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(
        @"(?<file>(?:[A-Za-z]:)?[^()\s]+?\.[A-Za-z0-9]+):(?:line\s+)?(?<line>\d+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StackFramePattern();

    [GeneratedRegex(
        @"File\s+""(?<file>[^""]+)"",\s+line\s+(?<line>\d+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PythonStackFramePattern();


    private static string EvidenceSourceLabel(ReferenceEvidenceSource source) => source switch
    {
        ReferenceEvidenceSource.IdentifierDirect => "identifier_direct",
        ReferenceEvidenceSource.IdentifierResolution => "identifier_resolution",
        ReferenceEvidenceSource.Relationship => "relationship",
        ReferenceEvidenceSource.PendingResolution => "pending_resolution",
        ReferenceEvidenceSource.NameFallback => "name_fallback",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    private static bool IsCallLike(ReferenceKind kind) =>
        kind is ReferenceKind.Call or ReferenceKind.Instantiation;

    internal static string ReferenceCostLine(ReferenceContextItem item)
    {
        var sb = new StringBuilder();
        sb.Append(item.ItemType).Append(' ')
          .Append(item.Reason).Append(' ')
          .Append(item.Confidence).Append(' ')
          .Append(item.Name).Append(' ')
          .Append(item.Kind).Append(' ')
          .Append(item.File).Append(':').Append(item.Line);
        if (item.Hop is not null)
            sb.Append(" hop=").Append(item.Hop.Value);
        if (!string.IsNullOrEmpty(item.Signature))
            sb.Append(' ').Append(ContextTextBounds.Truncate(item.Signature!, ToolRenderLimits.SignatureMaxLength));
        if (!string.IsNullOrEmpty(item.Snippet))
            sb.Append(' ').Append(ContextTextBounds.Truncate(item.Snippet!, ToolRenderLimits.SignatureMaxLength));
        if (item.ResolutionStatus is not null)
            sb.Append(" resolution=").Append(item.ResolutionStatus);
        if (item.Provenance is not null)
            sb.Append(" source=").Append(item.Provenance);
        if (item.EvidenceConfidence is not null)
            sb.Append(" evidence_confidence=")
                .Append(item.EvidenceConfidence.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        if (item.AnchorReason is not null)
            sb.Append(" anchor=").Append(item.AnchorReason);
        if (item.Role is not null)
            sb.Append(" role=").Append(item.Role);
        return sb.ToString();
    }

    internal static int ReferenceAllocationTier(ReferenceContextItem item) =>
        item.ItemType switch
        {
            "implementation" => 0,
            "symbol" when item.Hop == 0 => 0,
            "content_chunk" => 1,
            "identifier" => IdentifierAllocationTier,
            _ => 3,
        };


    internal static ContextEvidenceDisposition DispositionFor(IReadOnlyList<Candidate> selected)
    {
        if (selected.Any(static candidate =>
                candidate.IsPivot &&
                candidate.Body is not null &&
                CarriesImplementation(candidate.Symbol) &&
                IsAuthoritativeImplementationReason(candidate.Reason)))
            return new ContextEvidenceDisposition("sufficient", "pivot_implementation_present");
        // Discovery-tier implementation bodies (source_rescue_*, semantic_rank_*, query_term_*_subject)
        // beat an authoritative value-declaration sibling for the reason label — they still cannot
        // authorize sufficient, but must not be masked as pivot_value_declaration_only.
        if (selected.Any(static candidate =>
                candidate.IsPivot &&
                candidate.Body is not null &&
                CarriesImplementation(candidate.Symbol)))
            return new ContextEvidenceDisposition("partial", "discovery_implementation_present");
        if (selected.Any(static candidate =>
                candidate.IsPivot &&
                candidate.Body is not null &&
                IsAuthoritativeImplementationReason(candidate.Reason)))
            return new ContextEvidenceDisposition("partial", "pivot_value_declaration_only");
        if (selected.Any(static candidate => candidate.IsPivot))
            return new ContextEvidenceDisposition("partial", "pivot_signature_only");
        return new ContextEvidenceDisposition("insufficient", "no_pivot_rendered");
    }

    /// <summary>
    /// Whether a rendered body is an implementation rather than a declared value. A constant, variable, field, or
    /// property body is the value it was assigned, so it can never be the implementation evidence
    /// <c>sufficient</c> attests to — and a top-ranked one must not tell the caller to stop looking.
    /// </summary>
    internal static bool CarriesImplementation(IndexedSymbol symbol) =>
        CarriesImplementationKind(symbol.Kind);

    internal static bool CarriesImplementationKind(string? kind) =>
        kind is not ("constant" or "variable" or "field" or "property");

    internal static ContextEvidenceDisposition DispositionForReference(
        IReadOnlyList<ReferenceContextItem> selected)
    {
        // Authoritative implementation body only — value declarations never authorize sufficient.
        if (selected.Any(static item =>
                item.ItemType == "implementation" &&
                CarriesImplementationKind(item.Kind) &&
                IsAuthoritativeImplementationReason(item.AnchorReason)))
            return new ContextEvidenceDisposition("sufficient", "pivot_implementation_present");
        // Exact containing chunks authorize sufficient only when the matched pivot is itself
        // authoritative (entry/edited/stack/full-query). Discovery pivots (source_rescue_*,
        // semantic_rank_*, query_term_*) must not complete via a free content-chunk ride-along.
        if (selected.Any(item =>
                item.ItemType == "content_chunk" &&
                item.Confidence == "exact" &&
                HasAuthoritativePivotForSymbol(selected, item.ContainingSymbolId)))
            return new ContextEvidenceDisposition("sufficient", "exact_containing_content_present");
        if (selected.Any(static item =>
                item.ItemType == "implementation" &&
                CarriesImplementationKind(item.Kind)))
            return new ContextEvidenceDisposition("partial", "discovery_implementation_present");
        if (selected.Any(static item =>
                item.ItemType == "implementation" &&
                IsAuthoritativeImplementationReason(item.AnchorReason)))
            return new ContextEvidenceDisposition("partial", "pivot_value_declaration_only");
        if (selected.Any(static item => item.ItemType == "symbol"))
            return new ContextEvidenceDisposition("partial", "symbol_and_relation_evidence_only");
        return new ContextEvidenceDisposition("insufficient", "no_pivot_rendered");
    }

    private static bool HasAuthoritativePivotForSymbol(
        IReadOnlyList<ReferenceContextItem> selected,
        string? symbolId)
    {
        if (string.IsNullOrEmpty(symbolId))
            return false;
        return selected.Any(item =>
            item.ItemType == "symbol" &&
            item.Role == "pivot" &&
            string.Equals(item.SymbolId, symbolId, StringComparison.Ordinal) &&
            IsAuthoritativeImplementationReason(item.Reason));
    }

    private static bool IsAuthoritativeImplementationReason(string? reason) =>
        reason is "entry_symbol" or
            "entry_file" or
            "edited_file" or
            "failing_test" or
            "stack_frame" or
            "stack_symbol" ||
        reason?.StartsWith("query_rank_", StringComparison.Ordinal) == true;


}
