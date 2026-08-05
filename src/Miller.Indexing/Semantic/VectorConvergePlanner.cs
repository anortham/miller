using System.Globalization;

namespace Miller.Indexing.Semantic;

/// <summary>What the drain loop should do with the span it was asked to converge.</summary>
public enum VectorConvergeDecision
{
    /// <summary>Re-embed the planned units in place and advance the cursor with the staged batch.</summary>
    Incremental,

    /// <summary>Hand the span to a shadow full rebuild. Execution lands in B5; this decision only names it.</summary>
    ShadowRebuild,
}

/// <summary>The five escalation triggers of vectors-v1 §Escalation to shadow full rebuild.</summary>
public enum VectorEscalationTrigger
{
    None,

    /// <summary><c>revision_file_changes</c> cannot explain the span from completed to target.</summary>
    DeltaHistoryMissing,

    /// <summary>The changed-vector ratio exceeds the escalation threshold (bulk refactors, mass renames).</summary>
    ChangedRatioAboveThreshold,

    /// <summary><c>encoder_fingerprint</c> or <c>storage_schema</c> changed.</summary>
    IdentityChanged,

    /// <summary>A full rebuild was promoted underneath us — <c>artifact_metadata.artifact_id</c> changed.</summary>
    ArtifactIdChanged,

    /// <summary>The per-revision commit would be too large for one short transaction.</summary>
    BatchTooLarge,

    /// <summary>The indexer signalled a full rebuild, independent of any identity comparison.</summary>
    FullRebuildSignalled,

    /// <summary>The live revision moved BACKWARDS under the stored cursor — only a promote does that.</summary>
    RevisionRegressed,
}

/// <summary>One live corpus unit as the writer currently constructs it.</summary>
public sealed record VectorCorpusUnit(string UnitId, string Path, string Text, string SymbolKind, bool IsTest);

/// <summary>One stored mapping row, as far as planning cares: what was embedded for this unit last time.</summary>
public sealed record VectorUnitState(string UnitId, string Path, string EmbedTextHash);

/// <summary>A unit the plan says must be embedded, carrying everything the commit needs.</summary>
public sealed record VectorWorkUnit(
    string UnitId,
    string Path,
    string Text,
    string EmbedTextHash,
    string SymbolKind,
    bool IsTest);

/// <summary>
/// The inputs of one cursor's convergence span. <see cref="Candidates"/> and <see cref="Stored"/> are scoped to
/// the changed paths; <see cref="TotalStoredUnits"/> is whole-corpus and exists only to compute the escalation
/// ratio.
/// </summary>
public sealed record VectorConvergeRequest
{
    public required VectorUnitKind Kind { get; init; }

    public required long CompletedRevision { get; init; }

    public required long TargetRevision { get; init; }

    public required IReadOnlyList<VectorCorpusUnit> Candidates { get; init; }

    public required IReadOnlyList<VectorUnitState> Stored { get; init; }

    public required int TotalStoredUnits { get; init; }

    /// <summary>False when <c>revision_file_changes</c> cannot explain the whole span.</summary>
    public bool DeltaHistoryComplete { get; init; } = true;

    /// <summary>True when the <c>symbols.db</c> artifact identity moved under the stored generation.</summary>
    public bool ArtifactIdChanged { get; init; }

    /// <summary>
    /// True when the indexer reported that this target came from a full rebuild. Independent of
    /// <see cref="ArtifactIdChanged"/> on purpose: the identity comparison is one reading of the artifact and
    /// can be defeated by an unreadable or coincidentally-equal id, whereas this is the writer stating what it
    /// just did.
    /// </summary>
    public bool FullRebuildSignalled { get; init; }

    /// <summary>The invalidation matrix's verdict on the stored generation identity versus this writer's.</summary>
    public InvalidationAction IdentityAction { get; init; } = InvalidationAction.None;

    /// <summary>Paths whose units may not be committed this drain (chunk-cursor rule 4). Their presence holds
    /// the cursor even when every other planned unit commits cleanly.</summary>
    public IReadOnlyList<string> DeferredPaths { get; init; } = [];
}

/// <summary>The hash-gated work for one span, plus where the cursor may land after it commits.</summary>
public sealed record VectorConvergePlan(
    VectorConvergeDecision Decision,
    VectorEscalationTrigger Trigger,
    IReadOnlyList<VectorWorkUnit> ReEmbed,
    IReadOnlyList<string> Delete,
    long AdvanceTo,
    string? HoldReason);

/// <summary>One workspace-derived <c>content_sources</c> row and the <c>symbols.db</c> hash for the same path.</summary>
public sealed record ChunkSourceHash(string Path, string ContentHash, string? SymbolsContentHash);

/// <summary>Everything the four chunk-cursor preconditions of vectors-v1 §Cursors are decided from.</summary>
public sealed record ChunkCursorFacts
{
    public required string SymbolsArtifactId { get; init; }

    public required string VectorsArtifactId { get; init; }

    public required string ChunkSourceArtifactId { get; init; }

    public required int ContentSchemaVersion { get; init; }

    public required int RecordedChunkSchemaVersion { get; init; }

    public required string ContentChunkerVersion { get; init; }

    public required string CorpusGeneration { get; init; }

    public required long ContentWorkspaceRevision { get; init; }

    public required long TargetRevision { get; init; }

    public required IReadOnlyList<ChunkSourceHash> Sources { get; init; }
}

/// <summary>
/// The chunk cursor's verdict. <see cref="ResetCursor"/> is rule 1's restamp — completed and target go to zero
/// and the recorded source artifact id is rewritten BEFORE any comparison against the target revision, so a
/// reset cursor can never accept a stale higher revision from a superseded artifact.
/// </summary>
public sealed record ChunkCursorDecision(
    bool CanAdvance,
    bool ResetCursor,
    string? Reason,
    IReadOnlyList<string> DeferredPaths);

/// <summary>
/// Pure convergence planning for the <c>vectors.db</c> write path: which units a span must re-embed
/// (hash-gated on <c>embed_text_hash</c>), which stored units vanished, whether the span escalates to a shadow
/// rebuild, and whether the chunk cursor is allowed to advance at all. No I/O, no encoder, no SQLite — every
/// rule here is decided from facts the caller has already read.
/// </summary>
public static class VectorConvergePlanner
{
    /// <summary>Above this share of the stored corpus, a targeted re-embed is more expensive than a rebuild.</summary>
    public const double EscalationChangedRatio = 0.5;

    /// <summary>Above this many units, the commit stops being one short transaction.</summary>
    public const int MaxUnitsPerTransaction = 2000;

    /// <summary>The chunk cursor's over-cap hold: the drain commits this bounded batch without advancing and
    /// immediately replans, so an arbitrarily large span converges within one wake.</summary>
    public const string BoundedBatchHoldReason =
        "the span exceeds one transaction; converging in bounded batches";

    /// <summary>The stable fragment a chunk-cursor hold carries while the symbol cursor's shadow rebuild is
    /// pending. Status rendering keys on it to hint <c>ready (rebuilding)</c>, so its value is a contract.</summary>
    public const string ShadowRebuildPendingMarker = "the symbol cursor's shadow rebuild";

    private const string ChunkerComponentPrefix = "chunks-v";

    /// <summary>Hash-gates one cursor's span and classifies whether it escalates. Only the symbol cursor may
    /// escalate: a shadow rebuild rebuilds symbol cards, so a chunk-side trigger holds for that rebuild and an
    /// over-cap chunk span converges in bounded batches instead.</summary>
    public static VectorConvergePlan Plan(VectorConvergeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Escalation(request) is var trigger and not VectorEscalationTrigger.None)
            return request.Kind is VectorUnitKind.Chunk ? ChunkHold(trigger) : Escalated(trigger);

        if (request.TargetRevision <= request.CompletedRevision)
        {
            return new VectorConvergePlan(
                VectorConvergeDecision.Incremental,
                VectorEscalationTrigger.None,
                [],
                [],
                request.CompletedRevision,
                null);
        }

        IReadOnlyList<VectorWorkUnit> reEmbed = HashGate(request.Candidates, request.Stored);

        IReadOnlyList<string> delete = RebuildDeleteList(request.Candidates, request.Stored);

        if (reEmbed.Count > MaxUnitsPerTransaction)
        {
            if (request.Kind is VectorUnitKind.Chunk)
            {
                return new VectorConvergePlan(
                    VectorConvergeDecision.Incremental,
                    VectorEscalationTrigger.None,
                    [.. reEmbed.Take(MaxUnitsPerTransaction)],
                    delete,
                    0,
                    BoundedBatchHoldReason);
            }

            return Escalated(VectorEscalationTrigger.BatchTooLarge);
        }

        if (request.Kind is not VectorUnitKind.Chunk
            && ExceedsChangedRatio(request.TotalStoredUnits, reEmbed.Count + delete.Count))
        {
            return Escalated(VectorEscalationTrigger.ChangedRatioAboveThreshold);
        }

        bool holds = request.DeferredPaths.Count > 0;
        return new VectorConvergePlan(
            VectorConvergeDecision.Incremental,
            VectorEscalationTrigger.None,
            reEmbed,
            delete,
            holds ? 0 : request.TargetRevision,
            holds
                ? $"{request.DeferredPaths.Count.ToString(CultureInfo.InvariantCulture)} source(s) deferred for hash disagreement"
                : null);
    }

    /// <summary>
    /// The four chunk-cursor preconditions, evaluated in the order vectors-v1 §Cursors fixes: artifact binding
    /// first (so a resettable revision counter can never be compared across an artifact swap), then schema and
    /// chunker agreement, then ordering within the bound artifact, then per-source hash agreement.
    /// </summary>
    public static ChunkCursorDecision EvaluateChunkCursor(ChunkCursorFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (!Same(facts.SymbolsArtifactId, facts.VectorsArtifactId)
            || !Same(facts.SymbolsArtifactId, facts.ChunkSourceArtifactId))
        {
            return Hold(
                "the symbols.db artifact identity changed; the chunk cursor was reset before any revision comparison",
                reset: true);
        }

        if (facts.ContentSchemaVersion != facts.RecordedChunkSchemaVersion)
        {
            return Hold(
                $"content.db schema_version {Number(facts.ContentSchemaVersion)} does not match the recorded "
                + $"chunk schema version {Number(facts.RecordedChunkSchemaVersion)}");
        }

        if (ChunkerVersionFor(facts.CorpusGeneration) is not { } expectedChunker)
        {
            return Hold(
                $"corpus_generation '{facts.CorpusGeneration}' encodes no chunker version this writer knows");
        }

        if (!Same(expectedChunker, facts.ContentChunkerVersion))
        {
            return Hold(
                $"content.db chunker_version '{facts.ContentChunkerVersion}' does not match the "
                + $"'{expectedChunker}' chunker encoded in corpus_generation");
        }

        if (facts.ContentWorkspaceRevision < facts.TargetRevision)
        {
            return Hold(
                $"content.db is at revision {Number(facts.ContentWorkspaceRevision)}, behind the target "
                + $"revision {Number(facts.TargetRevision)}");
        }

        List<string> deferred = [.. facts.Sources
            .Where(static source => !HashesAgree(source.ContentHash, source.SymbolsContentHash))
            .Select(static source => source.Path)];

        if (deferred.Count > 0)
        {
            return new ChunkCursorDecision(
                CanAdvance: false,
                ResetCursor: false,
                $"{Number(deferred.Count)} content source(s) disagree with the symbols.db content hash",
                deferred);
        }

        return new ChunkCursorDecision(CanAdvance: true, ResetCursor: false, Reason: null, DeferredPaths: []);
    }

    /// <summary>
    /// The <c>content.db</c> chunker version a <c>corpus_generation</c> string demands. A
    /// <c>corpus_generation</c> naming a chunker this writer does not know is a hold, never a silent accept.
    /// </summary>
    public static string? ChunkerVersionFor(string? corpusGeneration)
    {
        if (string.IsNullOrWhiteSpace(corpusGeneration))
            return null;

        int marker = corpusGeneration.IndexOf(ChunkerComponentPrefix, StringComparison.Ordinal);
        if (marker < 0)
            return null;

        string component = corpusGeneration[marker..];
        return string.Equals(component, ChunkerComponentPrefix + "1", StringComparison.Ordinal)
            ? ContentCorpusSchema.ChunkerVersion
            : null;
    }

    /// <summary>
    /// The whole-corpus work list of a shadow rebuild, hash-gated against what the shadow already stores but
    /// never size-capped: the rebuild commits in bounded slices, so the transaction cap does not apply here.
    /// </summary>
    public static IReadOnlyList<VectorWorkUnit> RebuildWorkList(
        IReadOnlyList<VectorCorpusUnit> candidates,
        IReadOnlyList<VectorUnitState> stored)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(stored);

        return HashGate(candidates, stored);
    }

    /// <summary>The stored units absent from a whole-corpus rebuild candidate set.</summary>
    public static IReadOnlyList<string> RebuildDeleteList(
        IReadOnlyList<VectorCorpusUnit> candidates,
        IReadOnlyList<VectorUnitState> stored)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(stored);

        var live = candidates.Select(static candidate => candidate.UnitId).ToHashSet(StringComparer.Ordinal);
        return
        [
            .. stored
                .Select(static unit => unit.UnitId)
                .Where(id => !live.Contains(id))
                .Distinct(StringComparer.Ordinal),
        ];
    }

    private static List<VectorWorkUnit> HashGate(
        IReadOnlyList<VectorCorpusUnit> candidates,
        IReadOnlyList<VectorUnitState> storedStates)
    {
        var stored = storedStates.ToDictionary(
            static s => s.UnitId, static s => s.EmbedTextHash, StringComparer.Ordinal);

        List<VectorWorkUnit> reEmbed = [];
        foreach (VectorCorpusUnit candidate in candidates)
        {
            string hash = SymbolCardBuilder.EmbedTextHash(candidate.Text);
            if (stored.TryGetValue(candidate.UnitId, out string? storedHash)
                && string.Equals(storedHash, hash, StringComparison.Ordinal))
            {
                continue;
            }

            reEmbed.Add(new VectorWorkUnit(
                candidate.UnitId,
                candidate.Path,
                candidate.Text,
                hash,
                candidate.SymbolKind,
                candidate.IsTest));
        }

        return reEmbed;
    }

    private static VectorEscalationTrigger Escalation(VectorConvergeRequest request)
    {
        if (!request.DeltaHistoryComplete)
            return VectorEscalationTrigger.DeltaHistoryMissing;
        if (request.ArtifactIdChanged)
            return VectorEscalationTrigger.ArtifactIdChanged;
        if (request.FullRebuildSignalled)
            return VectorEscalationTrigger.FullRebuildSignalled;
        // A promote restarts julie's revision counter, so the live revision can land BELOW the stored cursor.
        // Without this the planner sees TargetRevision <= CompletedRevision, plans nothing, and the corpus stays
        // pinned to a generation that no longer exists.
        if (request.TargetRevision < request.CompletedRevision)
            return VectorEscalationTrigger.RevisionRegressed;
        if (request.IdentityAction is InvalidationAction.ShadowRebuild)
            return VectorEscalationTrigger.IdentityChanged;
        return VectorEscalationTrigger.None;
    }

    // An empty stored corpus is the initial build, not a bulk refactor: everything is "changed" by definition,
    // and escalating there would send every first build through the shadow path for no reason.
    private static bool ExceedsChangedRatio(int totalStoredUnits, int changedUnits) =>
        totalStoredUnits > 0 && changedUnits / (double)totalStoredUnits > EscalationChangedRatio;

    private static VectorConvergePlan Escalated(VectorEscalationTrigger trigger) =>
        new(VectorConvergeDecision.ShadowRebuild, trigger, [], [], 0, trigger.ToString());

    private static VectorConvergePlan ChunkHold(VectorEscalationTrigger trigger) =>
        new(
            VectorConvergeDecision.Incremental,
            trigger,
            [],
            [],
            0,
            $"the chunk cursor holds while {ShadowRebuildPendingMarker} ({trigger}) is pending");

    private static ChunkCursorDecision Hold(string reason, bool reset = false) =>
        new(CanAdvance: false, ResetCursor: reset, reason, DeferredPaths: []);

    private static bool HashesAgree(string contentHash, string? symbolsContentHash) =>
        symbolsContentHash is not null && Same(Normalize(contentHash), Normalize(symbolsContentHash));

    private static string Normalize(string hash) => hash.Trim().ToLowerInvariant();

    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
