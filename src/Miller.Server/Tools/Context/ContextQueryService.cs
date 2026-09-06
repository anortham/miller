using System.Diagnostics;
using Miller.Core.Graph;
using Miller.Core.References;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Semantic;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Workspaces;

namespace Miller.Server.Tools.Context;

internal sealed class ContextQueryService
{
    internal const int ReferenceRowsPerSymbol = 12;
    internal const int ContentChunksPerSymbol = 2;

    private readonly IWorkspaceIndexProvider _workspaceProvider;
    private readonly ISemanticTextArm? _semanticArm;
    private readonly VectorSidecar? _semanticSidecar;
    private readonly Action<string>? _phaseObserver;
    private readonly Action<ContextLookupPhaseObservation>? _lookupPhaseObserver;

    internal ContextQueryService(
        IWorkspaceIndexProvider workspaceProvider,
        ISemanticTextArm? semanticArm,
        VectorSidecar? semanticSidecar,
        Action<string>? phaseObserver,
        Action<ContextLookupPhaseObservation>? lookupPhaseObserver)
    {
        _workspaceProvider = workspaceProvider;
        _semanticArm = semanticArm;
        _semanticSidecar = semanticSidecar;
        _phaseObserver = phaseObserver;
        _lookupPhaseObserver = lookupPhaseObserver;
    }

    internal string Execute(ContextQueryRequest request)
    {
        TelemetryScope? telemetry = TelemetryContext.Current;
        long phaseStart = Stopwatch.GetTimestamp();
        bool json = string.Equals(request.Format, "json", StringComparison.OrdinalIgnoreCase);
        try
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            if (request.TokenBudget <= 0)
                return string.Empty;

            WorkspaceRefreshMode refresh = ReadToolWorkspaceRouting.ResolveRefreshMode(
                request.WorkspaceId,
                request.EnsureFresh);
            int effectiveTokenBudget = Math.Min(request.TokenBudget, ToolOutputBudget.ContextMcpMaxTokens);
            using WorkspaceReadContext context = _workspaceProvider.Resolve(request.WorkspaceId, refresh);
            using IDisposable? searchTelemetry = context.ReadTelemetry?.ActivateSearchTelemetry();
            ISymbolLookupIndex contextIndex = new ContextSearchCacheLookupIndex(context.Index);
            var contextResolver = new SmartTargetResolver(contextIndex);
            CompletePhase("resolve", telemetry, ref phaseStart);
            string? compactBanner = ReadToolWorkspaceRouting.CompactBanner(context, request.WorkspaceId, json);
            int bundleTokenBudget = Math.Max(
                0,
                effectiveTokenBudget -
                (compactBanner is null ? 0 : (int)TokenEstimator.Count(compactBanner + '\n')));
            ContextReferenceMode referenceMode = ParseReferenceMode(request.ReferenceMode);
            bool rescueExcludeTests = referenceMode == ContextReferenceMode.Usage
                ? request.ExcludeTests
                : SearchTool.ResolveExcludeTests(null, request.Query, SearchToolMode.Symbol);
            var retrieval = new ContextQueryRetrieval(contextIndex);

            request.CancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ContextSemanticSeed> semanticSeeds = ContextBundleBuilder.LoadSemanticSeeds(
                _semanticSidecar,
                _semanticArm,
                context.WorkspaceRoot,
                contextIndex,
                request.Query,
                referenceMode == ContextReferenceMode.Usage && request.ExcludeTests,
                retrieval);
            CompletePhase("semantic_seeds", telemetry, ref phaseStart);
            request.CancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ContextSourceSeed> sourceSeeds = LoadSourceRescueSeeds(
                contextIndex,
                TryResolveTextContentIndex(request.WorkspaceId, refresh),
                request.Query,
                rescueExcludeTests);
            CompletePhase("source_rescue", telemetry, ref phaseStart, context.ReadTelemetry);
            request.CancellationToken.ThrowIfCancellationRequested();

            Func<IReadOnlyList<string>, IReadOnlyDictionary<string, OutgoingReferenceEvidenceSet>>?
                readOutgoingMany = null;
            if (referenceMode == ContextReferenceMode.Off &&
                !string.IsNullOrWhiteSpace(request.Query) &&
                !ContextBundleBuilder.HasTestOrDefIntent(request.Query))
            {
                if (context.ReadSession.ResolutionFactsWarm)
                {
                    readOutgoingMany = symbolIds => ReadOutgoingBatch(context.ReadSession, symbolIds);
                }
                else
                {
                    telemetry?.SetMetadata("term_rescue", "skipped_cold_facts");
                    context.ReadSession.WarmResolutionFactsInBackground().ContinueWith(
                        static warm => Serilog.Log.Warning(
                            warm.Exception?.GetBaseException(),
                            "Background fact-cache warm for context term-rescue failed."),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default);
                }
            }

            ContextResolvedQueryResult result = ExecuteResolved(
                new ContextResolvedQueryRequest(
                    contextIndex,
                    context.Graph,
                    contextResolver,
                    request.Query,
                    bundleTokenBudget,
                    request.MaxHops,
                    request.EntrySymbols,
                    request.EditedFiles,
                    request.FailingTest,
                    request.StackTrace,
                    referenceMode,
                    request.ReferenceDepth,
                    request.ExcludeTests,
                    json,
                    semanticSeeds,
                    sourceSeeds,
                    symbol => ReadPivotBody(context.ReadSession, context.WorkspaceRoot, symbol),
                    readOutgoingMany,
                    symbol => ReferenceEvidenceReader.Read(
                        context.ReadSession,
                        symbol.SymbolId,
                        new ReferenceEvidenceBounds(
                            ReferenceRowsPerSymbol,
                            ReferenceRowsPerSymbol)),
                    symbol => ReferenceEvidenceReader.ReadOutgoing(
                        context.ReadSession,
                        symbol.SymbolId,
                        new ReferenceEvidenceBounds(
                            ReferenceRowsPerSymbol,
                            ReferenceRowsPerSymbol)),
                    (symbols, excludeTests) => ReadContentChunks(context.ReadSession, symbols, excludeTests),
                    symbols => ReferenceEvidenceReader.ReadMany(
                        context.ReadSession,
                        symbols.Select(static symbol => symbol.SymbolId).ToArray(),
                        new ReferenceEvidenceQuery(
                            new ReferenceEvidenceBounds(
                                ReferenceRowsPerSymbol,
                                ReferenceRowsPerSymbol))),
                    request.CancellationToken,
                    (phase, counts) => CompletePhase(
                        phase,
                        telemetry,
                        ref phaseStart,
                        context.ReadTelemetry,
                        counts),
                    retrieval));

            string output = ReadToolWorkspaceRouting.PrefixCompact(result.Output, compactBanner);
            ToolDiagnostic? diagnostic = null;
            if (result.SelectedCount == 0)
            {
                diagnostic = EmptyDiagnostic(
                    request.Query,
                    effectiveTokenBudget,
                    result.CandidatesExamined,
                    request.EntrySymbols,
                    ToolOutputBudget.ContextMcpMaxTokens);
            }
            else if (IndexLevelGuard.ReferenceLayerConverging(context.IndexLevel))
            {
                IndexLevelGuard.MarkDegraded(telemetry, "reference_layer_converging");
                diagnostic = IndexLevelGuard.Converging(
                    "the bundle carries no usage or outgoing-reference evidence pending identifier extraction.");
            }

            if (telemetry is not null)
            {
                ReadToolWorkspaceRouting.ApplyTelemetry(telemetry, context);
                telemetry.Op = referenceMode == ContextReferenceMode.Usage ? "usage" : "off";
                telemetry.SetTarget(request.Query);
                telemetry.ResultCount = result.SelectedCount;
                telemetry.BytesExamined = result.CandidatesExamined;
                telemetry.Outcome = diagnostic is null ? TelemetryOutcome.Ok : TelemetryOutcome.Empty;
                telemetry.SetMetadata("format", json ? "json" : "compact");
                telemetry.SetMetadata("token_budget_bucket", TokenBudgetBucket(effectiveTokenBudget));
                telemetry.SetMetadata("max_hops_bucket", HopsBucket(request.MaxHops));
                telemetry.SetMetadata("has_entry_symbols", request.EntrySymbols is { Length: > 0 });
                telemetry.SetMetadata("has_failing_test", !string.IsNullOrWhiteSpace(request.FailingTest));
                telemetry.SetMetadata("has_stack_trace", !string.IsNullOrWhiteSpace(request.StackTrace));
                telemetry.SetMetadata("has_edited_files", request.EditedFiles is { Length: > 0 });
                telemetry.SetMetadata("semantic_seed_count", semanticSeeds.Count);
                telemetry.SetMetadata("source_rescue_seed_count", sourceSeeds.Count);
                telemetry.SetMetadata("reference_depth_bucket", HopsBucket(request.ReferenceDepth));
                telemetry.SetMetadata("exclude_tests", request.ExcludeTests);
            }
            if (diagnostic is not null)
                output = ToolDiagnosticRenderer.Attach("context", output, diagnostic, json, telemetry);

            string finalOutput = ContextBundleRenderer.BoundFinalOutput(output, effectiveTokenBudget, json);
            CompletePhase("final_render", telemetry, ref phaseStart);
            return finalOutput;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ToolDiagnostic diagnostic = ToolDiagnostic.FromException(ex);
            if (diagnostic.Outcome == ToolDiagnosticOutcome.Error)
                telemetry?.SetError(ex);
            string output = ToolDiagnosticRenderer.Render("context", diagnostic, json, telemetry);
            return ContextBundleRenderer.BoundFinalOutput(
                output,
                Math.Min(request.TokenBudget, ToolOutputBudget.ContextMcpMaxTokens),
                json);
        }
    }

    internal static ContextResolvedQueryResult ExecuteResolved(ContextResolvedQueryRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        ContextResolvedQueryResult result = request.ReferenceMode switch
        {
            ContextReferenceMode.Off => ExecuteOrdinary(request),
            ContextReferenceMode.Usage => ExecuteReferenceAware(request),
            _ => throw new ArgumentOutOfRangeException(nameof(request.ReferenceMode)),
        };

        request.PhaseObserver?.Invoke(
            request.ReferenceMode == ContextReferenceMode.Usage ? "bundle_remainder" : "bundle",
            null);
        return result;
    }

    private static ContextResolvedQueryResult ExecuteOrdinary(ContextResolvedQueryRequest request)
    {
        ContextBundleBuildResult built = ContextBundleBuilder.BuildActionable(
            request.Index,
            request.Graph,
            request.Resolver,
            request.Query,
            request.TokenBudget,
            request.MaxHops,
            request.EntrySymbols,
            request.EditedFiles,
            request.FailingTest,
            request.StackTrace,
            request.SemanticSeeds,
            request.SourceSeeds,
            request.ReadBody,
            request.ReadOutgoingMany,
            request.CancellationToken,
            phase => request.PhaseObserver?.Invoke(phase, null),
            request.Retrieval);
        if (built.Candidates.Count == 0)
        {
            return new ContextResolvedQueryResult(
                ContextBundleRenderer.RenderNoPivots(
                    built.AnchorDiagnostics,
                    request.TokenBudget,
                    request.Json),
                SelectedCount: 0,
                built.CandidatesExamined);
        }

        IReadOnlyList<Candidate> selected = ContextBundleRenderer.SelectOrdinary(
            built.Candidates,
            request.TokenBudget,
            request.CancellationToken);
        request.PhaseObserver?.Invoke("candidate_pack", null);
        string output = ContextBundleRenderer.RenderOrdinary(
            selected,
            built.AnchorDiagnostics,
            request.Query,
            request.TokenBudget,
            request.Json,
            out int selectedCount,
            request.CancellationToken);
        request.PhaseObserver?.Invoke("bounded_render", null);
        return new ContextResolvedQueryResult(output, selectedCount, built.CandidatesExamined);
    }

    private static ContextResolvedQueryResult ExecuteReferenceAware(ContextResolvedQueryRequest request)
    {
        ContextReferenceBuildResult built = ContextBundleBuilder.BuildReferenceAware(
            request.Index,
            request.Graph,
            request.Resolver,
            request.Query,
            request.TokenBudget,
            request.MaxHops,
            request.EntrySymbols,
            request.EditedFiles,
            request.FailingTest,
            request.StackTrace,
            request.SemanticSeeds,
            request.SourceSeeds,
            request.ReadBody,
            request.ReferenceDepth,
            request.ExcludeTests,
            request.ReadReferenceEvidence ?? throw MissingReader(nameof(request.ReadReferenceEvidence)),
            request.ReadOutgoingEvidence ?? throw MissingReader(nameof(request.ReadOutgoingEvidence)),
            request.ReadContentChunks ?? throw MissingReader(nameof(request.ReadContentChunks)),
            request.ReadMany,
            (items, diagnostics, query) => ContextBundleRenderer.EstimateReferenceTokens(
                items,
                diagnostics,
                query,
                request.Json),
            request.CancellationToken,
            phase => request.PhaseObserver?.Invoke(phase, null),
            request.Retrieval);
        if (built.Candidates.Count == 0)
        {
            return new ContextResolvedQueryResult(
                ContextBundleRenderer.RenderNoPivots(
                    built.AnchorDiagnostics,
                    request.TokenBudget,
                    request.Json),
                SelectedCount: 0,
                built.CandidatesExamined);
        }

        request.PhaseObserver?.Invoke("reference_items", built.ReadCounts);
        IReadOnlyList<ReferenceContextItem> selected = ContextBundleRenderer.SelectReference(
            built.Items,
            request.TokenBudget,
            request.CancellationToken);
        request.PhaseObserver?.Invoke("candidate_pack", null);
        string output = ContextBundleRenderer.RenderReference(
            selected,
            built.AnchorDiagnostics,
            request.Query,
            request.TokenBudget,
            request.Json,
            out int selectedCount,
            request.CancellationToken);
        request.PhaseObserver?.Invoke("bounded_render", null);
        return new ContextResolvedQueryResult(output, selectedCount, built.CandidatesExamined);
    }

    internal static ContextReferenceMode ParseReferenceMode(string? mode) =>
        mode?.ToLowerInvariant() switch
        {
            null or "" or "off" => ContextReferenceMode.Off,
            "usage" => ContextReferenceMode.Usage,
            _ => throw new ArgumentException("reference_mode must be off or usage."),
        };

    internal static IReadOnlyList<ContextSourceSeed> LoadSourceRescueSeeds(
        ISymbolLookupIndex index,
        ITextContentSearchIndex? contentIndex,
        string query,
        bool excludeTests) =>
        ContextBundleBuilder.LoadSourceRescueSeeds(index, contentIndex, query, excludeTests);

    internal static ToolDiagnostic EmptyDiagnostic(
        string query,
        int tokenBudget,
        int candidatesExamined,
        IReadOnlyList<string>? entrySymbols,
        int maxTokenBudget)
    {
        string recoveryQuery = entrySymbols?
            .FirstOrDefault(static entry => !string.IsNullOrWhiteSpace(entry)) ?? query;
        if (candidatesExamined == 0)
        {
            return ToolDiagnostic.ExpectedEmpty(
                "no_context_symbols",
                "No context symbols matched the supplied evidence.",
                [new ToolDiagnosticAction(
                    $"search(query=\"{ToolDiagnosticText.EscapeCallArgument(recoveryQuery)}\")",
                    "find a concrete entry symbol")]);
        }

        long requestedNextBudget = Math.Max((long)tokenBudget + 256, (long)tokenBudget * 2);
        int nextBudget = (int)Math.Min(maxTokenBudget, requestedNextBudget);
        return ToolDiagnostic.ExpectedEmpty(
            "context_budget_exhausted",
            "Context candidates matched, but none fit token_budget.",
            [
                new ToolDiagnosticAction(
                    $"context(query=\"{ToolDiagnosticText.EscapeCallArgument(query)}\", token_budget={nextBudget})",
                    "retry with more room"),
                new ToolDiagnosticAction(
                    $"search(query=\"{ToolDiagnosticText.EscapeCallArgument(recoveryQuery)}\", mode=\"symbol\")",
                    "narrow to one exact entry symbol"),
            ]);
    }

    internal static ExtractReader.BodyReadResult ReadPivotBody(
        WorkspaceReadHandle readSession,
        string workspaceRoot,
        IndexedSymbol symbol)
    {
        try
        {
            return ExtractReader.ReadBody(readSession, workspaceRoot, symbol);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return ExtractReader.BodyReadResult.Unavailable(
                ExtractReader.BodyUnavailableReason.FileHashUnavailable);
        }
    }

    private static InvalidOperationException MissingReader(string name) =>
        new($"Reference mode usage requires {name}.");

    private ITextContentSearchIndex? TryResolveTextContentIndex(
        string? workspaceId,
        WorkspaceRefreshMode refresh)
    {
        if (_workspaceProvider is not IWorkspaceTextContentSearchProvider textProvider)
            return null;

        try
        {
            return textProvider.ResolveTextContentSearch(workspaceId, refresh).Index;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void CompletePhase(
        string phase,
        TelemetryScope? telemetry,
        ref long phaseStart,
        ReadPhaseTelemetry? readTelemetry = null,
        ContextReferenceReadCounts? referenceReadCounts = null)
    {
        long elapsedMs = Math.Max(0, (long)Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds);
        phaseStart = Stopwatch.GetTimestamp();
        telemetry?.SetMetadata("context_phase", phase);
        telemetry?.SetMetadata("context_phase_elapsed_ms", elapsedMs);
        if (referenceReadCounts is { } counts)
        {
            telemetry?.SetMetadata("reference_candidates_read", counts.CandidatesRead);
            telemetry?.SetMetadata("reference_candidates_skipped", counts.CandidatesSkipped);
        }
        _phaseObserver?.Invoke(phase);
        ContextLookupPhase? lookupPhase = phase switch
        {
            "source_rescue" => ContextLookupPhase.SourceRescue,
            "query_retrieval" => ContextLookupPhase.QueryRetrieval,
            "term_retrieval" => ContextLookupPhase.TermRetrieval,
            "anchor_resolution" => ContextLookupPhase.AnchorResolution,
            "graph_reach" => ContextLookupPhase.GraphReach,
            "symbol_hydration" => ContextLookupPhase.SymbolHydration,
            "file_neighbours" => ContextLookupPhase.FileNeighbours,
            "candidate_ordering" => ContextLookupPhase.CandidateOrdering,
            _ => null,
        };
        if (lookupPhase is { } completedLookupPhase && readTelemetry is not null)
        {
            ContextLookupPhaseObservation observation = readTelemetry.CompleteLookupPhase(completedLookupPhase);
            _lookupPhaseObserver?.Invoke(observation);
            Serilog.Log.Information(
                "Context lookup phase {ContextLookupPhase} on lookup backend {ContextLookupBackend} " +
                "completed with delta {@ContextLookupDelta} and total {@ContextLookupTotal}, " +
                "search delta {@ContextSearchDelta}, search total {@ContextSearchTotal}, " +
                "FTS search delta {@ContextFtsSearchDelta}, FTS search total {@ContextFtsSearchTotal}, " +
                "content FTS search delta {@ContextFtsTextSearchDelta}, " +
                "content FTS search total {@ContextFtsTextSearchTotal}, content index resolve delta " +
                "{@ContextTextContentIndexResolveDelta}, and content index resolve total " +
                "{@ContextTextContentIndexResolveTotal} for cid {CorrelationId}",
                completedLookupPhase,
                SymbolLookupBackends.Name(observation.LookupBackend),
                observation.Delta,
                observation.Total,
                observation.SearchDelta,
                observation.SearchTotal,
                observation.FtsSearchDelta,
                observation.FtsSearchTotal,
                observation.FtsTextSearchDelta,
                observation.FtsTextSearchTotal,
                observation.TextContentIndexResolveDelta,
                observation.TextContentIndexResolveTotal,
                telemetry?.CorrelationId ?? "unmeasured");
        }
        if (referenceReadCounts is { } readCounts)
        {
            Serilog.Log.Information(
                "Context phase {ContextPhase} completed in {ContextPhaseElapsedMs} ms after reading evidence " +
                "for {ContextReferenceCandidatesRead} candidates and skipping " +
                "{ContextReferenceCandidatesSkipped} beyond the token budget for cid {CorrelationId}",
                phase,
                elapsedMs,
                readCounts.CandidatesRead,
                readCounts.CandidatesSkipped,
                telemetry?.CorrelationId ?? "unmeasured");
            return;
        }

        Serilog.Log.Information(
            "Context phase {ContextPhase} completed in {ContextPhaseElapsedMs} ms for cid {CorrelationId}",
            phase,
            elapsedMs,
            telemetry?.CorrelationId ?? "unmeasured");
    }

    private static IReadOnlyDictionary<string, OutgoingReferenceEvidenceSet> ReadOutgoingBatch(
        WorkspaceReadHandle readSession,
        IReadOnlyList<string> symbolIds) =>
        ReferenceEvidenceReader.ReadOutgoingMany(
            readSession,
            symbolIds,
            new ReferenceEvidenceQuery(
                new ReferenceEvidenceBounds(
                    ReferenceRowsPerSymbol,
                    ReferenceRowsPerSymbol)));

    private static IReadOnlyList<TextContentSearchHit> ReadContentChunks(
        WorkspaceReadHandle readSession,
        IReadOnlyList<IndexedSymbol> symbols,
        bool excludeTests) => readSession.Snapshot.Mode == WorkspaceReadMode.FamilyStore
        ? ContentCorpusContextReader.ReadContainingSymbolChunks(
            readSession.FamilyStoreRoot!,
            readSession.Snapshot,
            symbols,
            excludeTests,
            ContentChunksPerSymbol)
        : ContentCorpusContextReader.ReadContainingSymbolChunks(
            ContentCorpusSidecar.ContentDbPathFor(RequiredLegacyArtifactPath(readSession)),
            RequiredLegacyArtifactPath(readSession),
            symbols,
            excludeTests,
            ContentChunksPerSymbol);

    private static string RequiredLegacyArtifactPath(WorkspaceReadHandle readSession) =>
        readSession.LegacyArtifactPath
        ?? throw new InvalidOperationException("The content sidecar has not been migrated to family-store reads.");

    private static string TokenBudgetBucket(int tokenBudget) => tokenBudget switch
    {
        <= 0 => "0",
        <= 1000 => "1-1000",
        <= 4000 => "1001-4000",
        <= 8000 => "4001-8000",
        _ => "8001+",
    };

    private static string HopsBucket(int hops) => hops switch
    {
        <= 0 => "0",
        1 => "1",
        2 => "2",
        _ => "3+",
    };
}

internal sealed record ContextQueryRequest(
    string Query,
    int TokenBudget,
    int MaxHops,
    string[]? EntrySymbols,
    string? FailingTest,
    string? StackTrace,
    string Format,
    string ReferenceMode,
    int ReferenceDepth,
    bool ExcludeTests,
    string? WorkspaceId,
    bool? EnsureFresh,
    string[]? EditedFiles,
    CancellationToken CancellationToken);

internal sealed record ContextResolvedQueryRequest(
    ISymbolLookupIndex Index,
    ISymbolGraphReachability Graph,
    SmartTargetResolver Resolver,
    string Query,
    int TokenBudget,
    int MaxHops,
    string[]? EntrySymbols,
    string[]? EditedFiles,
    string? FailingTest,
    string? StackTrace,
    ContextReferenceMode ReferenceMode,
    int ReferenceDepth,
    bool ExcludeTests,
    bool Json,
    IReadOnlyList<ContextSemanticSeed>? SemanticSeeds,
    IReadOnlyList<ContextSourceSeed>? SourceSeeds,
    Func<IndexedSymbol, ExtractReader.BodyReadResult>? ReadBody,
    Func<IReadOnlyList<string>, IReadOnlyDictionary<string, OutgoingReferenceEvidenceSet>>? ReadOutgoingMany,
    Func<IndexedSymbol, ReferenceEvidenceSet>? ReadReferenceEvidence,
    Func<IndexedSymbol, OutgoingReferenceEvidenceSet>? ReadOutgoingEvidence,
    Func<IReadOnlyList<IndexedSymbol>, bool, IReadOnlyList<TextContentSearchHit>>? ReadContentChunks,
    Func<IReadOnlyList<IndexedSymbol>, IReadOnlyDictionary<string, ReferenceEvidenceBundle>>? ReadMany,
    CancellationToken CancellationToken,
    Action<string, ContextReferenceReadCounts?>? PhaseObserver,
    ContextQueryRetrieval? Retrieval = null);

internal sealed record ContextResolvedQueryResult(string Output, int SelectedCount, int CandidatesExamined);

internal enum ContextReferenceMode
{
    Off,
    Usage,
}
