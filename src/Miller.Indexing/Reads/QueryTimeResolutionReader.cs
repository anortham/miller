using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Core.Graph;
using Miller.Core.References;
using Miller.Core.Resolution;
using Miller.Indexing.Resolution;
using ResolutionIdentifierSite = Miller.Indexing.Resolution.IdentifierSite;

namespace Miller.Indexing.Reads;

internal interface IQueryTimeResolutionHost
{
    QueryTimeResolutionReader Resolution { get; }
}

internal sealed record QueryTimeExportEvidence(
    string ReferenceSiteId,
    bool IsExact,
    string SiteProvenance,
    string Path,
    string Language,
    string? ContainingSymbolId,
    long? StartLine,
    long? StartColumn,
    long? EndLine,
    long? EndColumn,
    long? StartByte,
    long? EndByte,
    string CanonicalKind,
    string? TargetSymbolId,
    string TargetName,
    string? TargetKind,
    bool? TargetIsTest,
    long? ResolutionTier,
    double Confidence,
    string EvidenceSource,
    string? SourceName,
    string? SourceKind,
    bool? SourceIsTest);

internal sealed class QueryTimeResolutionCounters
{
    private int _resolvePasses;
    private int _identifierDetailCommands;
    private int _identifierDetailRows;

    internal int ResolvePasses => Volatile.Read(ref _resolvePasses);

    internal int IdentifierDetailCommands => Volatile.Read(ref _identifierDetailCommands);

    internal int IdentifierDetailRows => Volatile.Read(ref _identifierDetailRows);

    internal void RecordResolvePass() => Interlocked.Increment(ref _resolvePasses);

    internal void RecordIdentifierDetailCommand() => Interlocked.Increment(ref _identifierDetailCommands);

    internal void RecordIdentifierDetailRow() => Interlocked.Increment(ref _identifierDetailRows);
}

internal sealed class QueryTimeResolutionReader
{
    private const int IdChunkSize = 128;

    private readonly RevisionFactCache _cache;
    private readonly StoreVisibility? _visibility;
    private readonly QueryTimeResolver _resolver;
    private readonly object _scratchGate = new();
    private PendingScratch? _pendingScratch;

    internal QueryTimeResolutionReader(RevisionFactCache cache, StoreVisibility? visibility)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _visibility = visibility;
        _resolver = new QueryTimeResolver(cache);
    }

    internal RevisionFactCache Cache => _cache;

    internal QueryTimeResolutionCounters Counters { get; } = new();

    public IReadOnlyList<FamilyGraphResolutionEdge> ReadResolutionEdges(
        SqliteConnection connection,
        IReadOnlyList<string> candidateIds,
        Direction direction,
        Action<GraphStatementObservation>? statementObserver)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(candidateIds);
        if (candidateIds.Count == 0)
            return [];

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        QueryScratch scratch = ResolveGraphQuery(
            connection,
            candidateIds,
            direction,
            GraphReadKind.Resolution);
        var edges = new List<FamilyGraphResolutionEdge>();
        foreach (string candidateId in candidateIds)
        {
            if (!scratch.Candidates.TryGetValue(candidateId, out CandidateRecord candidate))
                continue;

            if (direction is Direction.Forward or Direction.Both)
            {
                foreach (ResolvedIdentifier identifier in scratch.IdentifiersByContainer(candidateId))
                {
                    if (identifier.Skip || identifier.Outcome.Kind != ResolutionOutcomeKind.Resolved)
                        continue;
                    string target = identifier.Outcome.Target!.Value.SymbolId;
                    if (string.Equals(identifier.ContainingSymbolId, target, StringComparison.Ordinal))
                        continue;
                    edges.Add(new FamilyGraphResolutionEdge(
                        candidateId,
                        identifier.ContainingSymbolId!,
                        target,
                        identifier.Kind,
                        identifier.Outcome.Confidence ?? identifier.Confidence,
                        "identifier_target"));
                }

                foreach (ResolvedPending pending in scratch.PendingsByFrom(candidateId))
                {
                    if (pending.Outcome.Kind != ResolutionOutcomeKind.Resolved)
                        continue;
                    string target = pending.Outcome.Target!.Value.SymbolId;
                    if (string.Equals(pending.FromSymbolId, target, StringComparison.Ordinal))
                        continue;
                    edges.Add(new FamilyGraphResolutionEdge(
                        candidateId,
                        pending.FromSymbolId,
                        target,
                        pending.Kind,
                        Math.Min(pending.Confidence, pending.Outcome.Confidence ?? pending.Confidence),
                        "pending_resolution"));
                }
            }

            if (direction is Direction.Reverse or Direction.Both)
            {
                foreach (ResolvedIdentifier identifier in scratch.IdentifiersNamed(candidate.Name))
                {
                    if (identifier.Skip
                        || identifier.Outcome.Kind != ResolutionOutcomeKind.Resolved
                        || identifier.ContainingSymbolId is null
                        || !string.Equals(identifier.Outcome.Target!.Value.SymbolId, candidateId, StringComparison.Ordinal)
                        || string.Equals(identifier.ContainingSymbolId, candidateId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    edges.Add(new FamilyGraphResolutionEdge(
                        candidateId,
                        identifier.ContainingSymbolId,
                        candidateId,
                        identifier.Kind,
                        identifier.Outcome.Confidence ?? identifier.Confidence,
                        "identifier_target"));
                }

                foreach (ResolvedPending pending in scratch.PendingsNamed(candidate.Name))
                {
                    if (pending.Outcome.Kind != ResolutionOutcomeKind.Resolved
                        || !string.Equals(pending.Outcome.Target!.Value.SymbolId, candidateId, StringComparison.Ordinal)
                        || string.Equals(pending.FromSymbolId, candidateId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    edges.Add(new FamilyGraphResolutionEdge(
                        candidateId,
                        pending.FromSymbolId,
                        candidateId,
                        pending.Kind,
                        Math.Min(pending.Confidence, pending.Outcome.Confidence ?? pending.Confidence),
                        "pending_resolution"));
                }
            }
        }

        statementObserver?.Invoke(GraphStatementObservation.Completed(
            GraphStatementPhase.FamilyResolution,
            edges.Count,
            System.Diagnostics.Stopwatch.GetElapsedTime(started),
            candidateIds));
        return edges;
    }

    public IReadOnlyList<FamilyGraphUnresolvedNameEdge> ReadUnresolvedNameEdges(
        SqliteConnection connection,
        IReadOnlyList<string> candidateIds,
        Direction direction,
        Action<GraphStatementObservation>? statementObserver)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(candidateIds);
        if (candidateIds.Count == 0)
            return [];

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        QueryScratch scratch = ResolveGraphQuery(
            connection,
            candidateIds,
            direction,
            GraphReadKind.Unresolved);
        var edges = new List<FamilyGraphUnresolvedNameEdge>();
        foreach (string candidateId in candidateIds)
        {
            if (!scratch.Candidates.TryGetValue(candidateId, out CandidateRecord candidate))
                continue;

            if (direction is Direction.Forward or Direction.Both)
            {
                foreach (ResolvedIdentifier identifier in scratch.IdentifiersByContainer(candidateId))
                {
                    if (!TryUniqueNameTarget(scratch, identifier, out string targetId))
                        continue;
                    edges.Add(new FamilyGraphUnresolvedNameEdge(
                        candidateId,
                        identifier.ContainingSymbolId!,
                        targetId,
                        identifier.Kind,
                        identifier.Confidence * 0.5,
                        "identifier_name"));
                }
            }

            if (direction is Direction.Reverse or Direction.Both)
            {
                foreach (ResolvedIdentifier identifier in scratch.IdentifiersNamed(candidate.Name))
                {
                    if (!TryUniqueNameTarget(scratch, identifier, out string targetId)
                        || !string.Equals(targetId, candidateId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    edges.Add(new FamilyGraphUnresolvedNameEdge(
                        candidateId,
                        identifier.ContainingSymbolId!,
                        candidateId,
                        identifier.Kind,
                        identifier.Confidence * 0.5,
                        "identifier_name"));
                }
            }
        }

        statementObserver?.Invoke(GraphStatementObservation.Completed(
            GraphStatementPhase.UnresolvedNameForward,
            edges.Count,
            System.Diagnostics.Stopwatch.GetElapsedTime(started),
            candidateIds));
        return edges;
    }

    internal Dictionary<string, List<ReferenceEvidence>> ReadInboundExact(
        SqliteConnection connection,
        IReadOnlyList<string> candidateIds)
    {
        QueryScratch scratch = ResolveQuery(connection, candidateIds);
        var rows = CreateListMap(candidateIds);
        foreach (string candidateId in candidateIds)
        {
            if (!scratch.Candidates.TryGetValue(candidateId, out CandidateRecord candidate))
                continue;

            foreach (ResolvedIdentifier identifier in scratch.IdentifiersNamed(candidate.Name))
            {
                if (!identifier.Details.HasSiteRow
                    || identifier.Outcome.Kind != ResolutionOutcomeKind.Resolved
                    || !string.Equals(identifier.Outcome.Target!.Value.SymbolId, candidateId, StringComparison.Ordinal))
                {
                    continue;
                }

                rows[candidateId].Add(ToInbound(
                    identifier,
                    candidateId,
                    ReferenceEvidenceSource.IdentifierResolution,
                    identifier.Outcome.Confidence ?? identifier.Confidence,
                    identifier.Outcome.Tier,
                    ReferenceResolutionStatus.Exact));
            }

            foreach (ResolvedPending pending in scratch.PendingsNamed(candidate.Name))
            {
                if (!pending.HasSiteRow
                    || pending.Outcome.Kind != ResolutionOutcomeKind.Resolved
                    || !string.Equals(pending.Outcome.Target!.Value.SymbolId, candidateId, StringComparison.Ordinal))
                {
                    continue;
                }

                rows[candidateId].Add(ToInbound(
                    pending,
                    candidateId,
                    ReferenceEvidenceSource.PendingResolution,
                    Math.Min(pending.Confidence, pending.Outcome.Confidence ?? pending.Confidence),
                    pending.Outcome.Tier,
                    ReferenceResolutionStatus.Exact));
            }

            foreach (RelationshipSite relationship in scratch.RelationshipsTo(candidateId))
            {
                rows[candidateId].Add(ToInbound(
                    relationship,
                    candidateId,
                    ReferenceEvidenceSource.Relationship,
                    relationship.Confidence,
                    tier: null,
                    ReferenceResolutionStatus.Exact));
            }
        }

        return rows;
    }

    internal Dictionary<string, List<ReferenceEvidence>> ReadInboundFallback(
        SqliteConnection connection,
        IReadOnlyList<string> candidateIds)
    {
        QueryScratch scratch = ResolveQuery(connection, candidateIds);
        var rows = CreateListMap(candidateIds);
        foreach (string candidateId in candidateIds)
        {
            if (!scratch.Candidates.TryGetValue(candidateId, out CandidateRecord candidate))
                continue;

            foreach (ResolvedIdentifier identifier in scratch.IdentifiersNamed(candidate.Name))
            {
                if (!identifier.Details.HasSiteRow || identifier.Outcome.Kind == ResolutionOutcomeKind.Resolved)
                    continue;
                rows[candidateId].Add(ToInbound(
                    identifier,
                    targetSymbolId: null,
                    ReferenceEvidenceSource.NameFallback,
                    Math.Min(identifier.Confidence, 0.5),
                    tier: null,
                    ReferenceResolutionStatus.Fallback));
            }
        }

        return rows;
    }

    internal Dictionary<string, List<OutgoingReferenceEvidence>> ReadOutgoingExact(
        SqliteConnection connection,
        IReadOnlyList<string> candidateIds)
    {
        QueryScratch scratch = ResolveQuery(connection, candidateIds);
        var rows = CreateOutgoingMap(candidateIds);
        foreach (string candidateId in candidateIds)
        {
            foreach (ResolvedIdentifier identifier in scratch.IdentifiersByContainer(candidateId))
            {
                if (!identifier.Details.HasSiteRow || identifier.Outcome.Kind != ResolutionOutcomeKind.Resolved)
                    continue;
                string target = identifier.Outcome.Target!.Value.SymbolId;
                rows[candidateId].Add(ToOutgoing(
                    identifier,
                    candidateId,
                    target,
                    TargetName(scratch, target, identifier.Name),
                    ReferenceEvidenceSource.IdentifierResolution,
                    identifier.Outcome.Confidence ?? identifier.Confidence,
                    identifier.Outcome.Tier,
                    ReferenceResolutionStatus.Exact));
            }

            foreach (ResolvedPending pending in scratch.PendingsByEvidenceContainer(candidateId))
            {
                if (!pending.HasSiteRow || pending.Outcome.Kind != ResolutionOutcomeKind.Resolved)
                    continue;
                string target = pending.Outcome.Target!.Value.SymbolId;
                rows[candidateId].Add(ToOutgoing(
                    pending,
                    candidateId,
                    target,
                    TargetName(scratch, target, pending.DisplayName),
                    ReferenceEvidenceSource.PendingResolution,
                    Math.Min(pending.Confidence, pending.Outcome.Confidence ?? pending.Confidence),
                    pending.Outcome.Tier,
                    ReferenceResolutionStatus.Exact));
            }

            foreach (RelationshipSite relationship in scratch.RelationshipsFrom(candidateId))
            {
                rows[candidateId].Add(ToOutgoing(
                    relationship,
                    candidateId,
                    relationship.ToSymbolId,
                    TargetName(scratch, relationship.ToSymbolId, relationship.ToSymbolId),
                    ReferenceEvidenceSource.Relationship,
                    relationship.Confidence,
                    tier: null,
                    ReferenceResolutionStatus.Exact));
            }
        }

        return rows;
    }

    internal Dictionary<string, List<OutgoingReferenceEvidence>> ReadOutgoingFallback(
        SqliteConnection connection,
        IReadOnlyList<string> candidateIds)
    {
        QueryScratch scratch = ResolveQuery(connection, candidateIds);
        var rows = CreateOutgoingMap(candidateIds);
        foreach (string candidateId in candidateIds)
        {
            foreach (ResolvedIdentifier identifier in scratch.IdentifiersByContainer(candidateId))
            {
                if (!identifier.Details.HasSiteRow || identifier.Outcome.Kind == ResolutionOutcomeKind.Resolved)
                    continue;
                rows[candidateId].Add(ToOutgoing(
                    identifier,
                    candidateId,
                    targetSymbolId: null,
                    identifier.Name,
                    ReferenceEvidenceSource.NameFallback,
                    Math.Min(identifier.Confidence, 0.5),
                    tier: null,
                    ReferenceResolutionStatus.Fallback));
            }

            foreach (ResolvedPending pending in scratch.PendingsByEvidenceContainer(candidateId))
            {
                if (!pending.HasSiteRow || pending.Outcome.Kind == ResolutionOutcomeKind.Resolved)
                    continue;
                rows[candidateId].Add(ToOutgoing(
                    pending,
                    candidateId,
                    targetSymbolId: null,
                    pending.DisplayName,
                    ReferenceEvidenceSource.NameFallback,
                    Math.Min(pending.Confidence, 0.5),
                    tier: null,
                    ReferenceResolutionStatus.Fallback));
            }
        }

        return rows;
    }

    internal IReadOnlyList<QueryTimeExportEvidence> ReadExportEvidence(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        QueryScratch scratch = ResolveAll(connection);
        var rows = new List<QueryTimeExportEvidence>();
        foreach (ResolvedIdentifier identifier in scratch.AllIdentifiers)
        {
            if (!identifier.Details.HasSiteRow)
                continue;
            if (identifier.Outcome.Kind == ResolutionOutcomeKind.Resolved)
            {
                string target = identifier.Outcome.Target!.Value.SymbolId;
                ExportSymbolFacts? symbol = ReadExportSymbol(connection, target);
                FactSymbol? cached = scratch.Symbol(target);
                rows.Add(ToExport(
                    identifier,
                    scratch,
                    connection,
                    target,
                    symbol?.Name ?? cached?.Name ?? identifier.Name,
                    symbol?.Kind ?? (cached is null ? null : KindString(cached.Kind)),
                    symbol?.IsTest ?? cached?.IsTest(),
                    identifier.Outcome.Tier,
                    identifier.Outcome.Confidence ?? identifier.Confidence,
                    "identifier_resolution"));
            }
            else
            {
                rows.Add(ToExport(
                    identifier,
                    scratch,
                    connection,
                    targetSymbolId: null,
                    identifier.Name,
                    targetKind: null,
                    targetIsTest: null,
                    tier: null,
                    identifier.Confidence,
                    "name_fallback"));
            }
        }

        foreach (ResolvedPending pending in scratch.AllPendings)
        {
            if (!pending.HasSiteRow)
                continue;
            if (pending.Outcome.Kind == ResolutionOutcomeKind.Resolved)
            {
                string target = pending.Outcome.Target!.Value.SymbolId;
                ExportSymbolFacts? symbol = ReadExportSymbol(connection, target);
                FactSymbol? cached = scratch.Symbol(target);
                rows.Add(ToExport(
                    pending,
                    scratch,
                    connection,
                    target,
                    symbol?.Name ?? cached?.Name ?? pending.DisplayName,
                    symbol?.Kind ?? (cached is null ? null : KindString(cached.Kind)),
                    symbol?.IsTest ?? cached?.IsTest(),
                    pending.Outcome.Tier,
                    Math.Min(pending.Confidence, pending.Outcome.Confidence ?? pending.Confidence),
                    "pending_resolution"));
            }
            else
            {
                rows.Add(ToExport(
                    pending,
                    scratch,
                    connection,
                    targetSymbolId: null,
                    pending.DisplayName,
                    targetKind: null,
                    targetIsTest: null,
                    tier: null,
                    pending.Confidence,
                    "name_fallback"));
            }
        }

        foreach (RelationshipSite relationship in scratch.AllRelationships)
        {
            ExportSymbolFacts? symbol = ReadExportSymbol(connection, relationship.ToSymbolId);
            FactSymbol? cachedTarget = scratch.Symbol(relationship.ToSymbolId);
            if (symbol is null && cachedTarget is null)
                continue;
            ExportSymbolFacts? source = relationship.SiteContainingSymbolId is null
                ? null
                : ReadExportSymbol(connection, relationship.SiteContainingSymbolId);
            FactSymbol? cachedSource = relationship.SiteContainingSymbolId is null
                ? null
                : scratch.Symbol(relationship.SiteContainingSymbolId);
            rows.Add(new QueryTimeExportEvidence(
                relationship.ReferenceSiteId,
                relationship.IsExact,
                relationship.SiteProvenance,
                relationship.Path,
                relationship.Language,
                relationship.SiteContainingSymbolId,
                relationship.StartLine,
                relationship.StartColumn,
                relationship.EndLine,
                relationship.EndColumn,
                relationship.StartByte,
                relationship.EndByte,
                CanonicalKind(relationship.Kind),
                relationship.ToSymbolId,
                symbol?.Name ?? cachedTarget!.Name,
                symbol?.Kind ?? (cachedTarget is null ? null : KindString(cachedTarget.Kind)),
                symbol?.IsTest ?? cachedTarget?.IsTest(),
                ResolutionTier: null,
                relationship.Confidence,
                "relationship",
                source?.Name ?? cachedSource?.Name,
                source?.Kind ?? (cachedSource is null ? null : KindString(cachedSource.Kind)),
                source?.IsTest ?? cachedSource?.IsTest()));
        }

        return rows;
    }

    private QueryScratch ResolveAll(SqliteConnection connection)
    {
        IReadOnlyList<string> allIds = ReadAllSymbolIds(connection);
        Dictionary<string, CandidateRecord> candidates = ReadCandidates(connection, allIds);
        var identifiers = new List<ResolvedIdentifier>();
        var seenIdentifiers = new HashSet<(long VersionId, long RowId)>();
        IEnumerable<ResolutionIdentifierSite> sites = _visibility is { } visibility
            ? IdentifierSiteReader.SitesAll(connection, visibility)
            : IdentifierSiteReader.SitesAll(connection);
        foreach (ResolutionIdentifierSite site in sites)
        {
            if (!seenIdentifiers.Add((site.VersionId, site.RowId)))
                continue;
            identifiers.Add(ResolveIdentifier(connection, site));
        }

        var pendings = new List<ResolvedPending>();
        var seenPendings = new HashSet<(long VersionId, string PendingId)>(PendingKeyComparer.Instance);
        foreach (PendingSite site in ReadAllPendings(connection))
        {
            if (!seenPendings.Add((site.VersionId, site.PendingId)))
                continue;
            pendings.Add(ResolvePending(site));
        }

        return new QueryScratch(_cache, candidates, identifiers, pendings, ReadAllRelationships(connection));
    }

    private QueryScratch ResolveGraphQuery(
        SqliteConnection connection,
        IReadOnlyList<string> candidateIds,
        Direction direction,
        GraphReadKind kind)
    {
        lock (_scratchGate)
        {
            if (_pendingScratch is { } pending
                && pending.Kind != kind
                && ReferenceEquals(pending.Connection, connection)
                && pending.Direction == direction
                && SameIds(pending.CandidateIds, candidateIds))
            {
                _pendingScratch = null;
                return pending.Scratch;
            }
        }

        QueryScratch scratch = ResolveQuery(connection, candidateIds);
        lock (_scratchGate)
            _pendingScratch = new PendingScratch(connection, candidateIds.ToArray(), direction, kind, scratch);

        return scratch;
    }

    private QueryScratch ResolveQuery(SqliteConnection connection, IReadOnlyList<string> candidateIds)
    {
        Counters.RecordResolvePass();
        Dictionary<string, CandidateRecord> candidates = ReadCandidates(connection, candidateIds);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (CandidateRecord candidate in candidates.Values)
            names.Add(candidate.Name);

        var identifierSites = new List<ResolutionIdentifierSite>();
        var seenIdentifiers = new HashSet<(long VersionId, long RowId)>();
        foreach (ResolutionIdentifierSite site in ReadIdentifierSites(connection, candidateIds, names))
        {
            if (seenIdentifiers.Add((site.VersionId, site.RowId)))
                identifierSites.Add(site);
        }

        var pendingSites = new List<PendingSite>();
        var seenPendings = new HashSet<(long VersionId, string PendingId)>(PendingKeyComparer.Instance);
        foreach (PendingSite site in ReadPendingSites(connection, candidateIds, names))
        {
            if (seenPendings.Add((site.VersionId, site.PendingId)))
                pendingSites.Add(site);
        }

        Dictionary<(long VersionId, long RowId), SiteDetails> details =
            ReadIdentifierDetailsBatch(connection, identifierSites);
        var identifiers = new List<ResolvedIdentifier>(identifierSites.Count);
        foreach (ResolutionIdentifierSite site in identifierSites)
        {
            if (!details.TryGetValue((site.VersionId, site.RowId), out SiteDetails siteDetails))
                siteDetails = MissingIdentifierDetails(site);
            identifiers.Add(ResolveIdentifier(site, siteDetails));
        }

        var pendings = new List<ResolvedPending>(pendingSites.Count);
        foreach (PendingSite site in pendingSites)
            pendings.Add(ResolvePending(site));

        IReadOnlyList<RelationshipSite> relationships = ReadRelationships(connection, candidateIds);
        return new QueryScratch(_cache, candidates, identifiers, pendings, relationships);
    }

    private static bool SameIds(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
            return false;
        for (int i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private ResolvedIdentifier ResolveIdentifier(SqliteConnection connection, ResolutionIdentifierSite site)
    {
        return ResolveIdentifier(site, ReadIdentifierDetails(connection, site));
    }

    private ResolvedIdentifier ResolveIdentifier(ResolutionIdentifierSite site, SiteDetails details)
    {
        bool skip = _cache.Propagation.TryGetOverride(site.VersionId, site.RowId, out PropagationSource source);
        ResolutionRefKind? kind = ResolutionKinds.FromIdentifierKind(site.Kind);
        if (kind is null)
            return new ResolvedIdentifier(site, details, ResolutionOutcome.NoContext, skip, source);

        string language = _cache.Slice(site.VersionId)?.Language ?? details.Language;
        ResolutionOutcome outcome = _resolver.Resolve(new ResolutionInput(
            ResolutionOrigin.Identifier,
            kind.Value,
            language,
            site.VersionId,
            site.Name,
            site.Receiver,
            site.ReceiverQualifier,
            site.ContainingSymbolId,
            site.Confidence));
        return new ResolvedIdentifier(site, details, outcome, skip, source);
    }

    private ResolvedPending ResolvePending(PendingSite site)
    {
        ResolutionRefKind? kind = ResolutionKinds.FromPendingKind(site.Kind);
        if (kind is null)
            return new ResolvedPending(site, ResolutionOutcome.NoContext);

        string language = _cache.Slice(site.VersionId)?.Language ?? site.Language;
        ResolutionOutcome outcome = _resolver.Resolve(new ResolutionInput(
            ResolutionOrigin.Pending,
            kind.Value,
            language,
            site.VersionId,
            site.Name,
            site.Receiver,
            site.ReceiverQualifier,
            site.CallerScopeSymbolId,
            site.Confidence));
        return new ResolvedPending(site, outcome);
    }

    private IEnumerable<ResolutionIdentifierSite> ReadIdentifierSites(
        SqliteConnection connection,
        IReadOnlyList<string> candidateIds,
        IReadOnlyCollection<string> names)
    {
        if (_visibility is { } visibility)
        {
            foreach (ResolutionIdentifierSite site in IdentifierSiteReader.SitesWithinSymbols(connection, visibility, candidateIds))
                yield return site;
            foreach (string name in names)
            {
                foreach (ResolutionIdentifierSite site in IdentifierSiteReader.SitesNamed(connection, visibility, name))
                    yield return site;
            }

            yield break;
        }

        foreach (ResolutionIdentifierSite site in IdentifierSiteReader.SitesWithinSymbols(connection, candidateIds))
            yield return site;
        foreach (string name in names)
        {
            foreach (ResolutionIdentifierSite site in IdentifierSiteReader.SitesNamed(connection, name))
                yield return site;
        }
    }

    private IEnumerable<PendingSite> ReadPendingSites(
        SqliteConnection connection,
        IReadOnlyList<string> candidateIds,
        IReadOnlyCollection<string> names)
    {
        foreach (PendingSite site in ReadPendingsByFrom(connection, candidateIds))
            yield return site;
        foreach (string name in names)
        {
            foreach (PendingSite site in ReadPendingsByName(connection, name))
                yield return site;
        }
    }

    private IEnumerable<PendingSite> ReadPendingsByFrom(SqliteConnection connection, IReadOnlyList<string> fromIds)
    {
        if (fromIds.Count == 0)
            yield break;
        foreach (string[] chunk in Chunk(fromIds, IdChunkSize))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = _visibility is null
                ? $"""
                    SELECT f.rowid,p.pending_relationship_id,p.from_symbol_id,p.caller_scope_symbol_id,
                           p.kind,p.target_terminal_name,p.target_display_name,p.target_receiver,p.target_namespace_json,
                           p.confidence,p.reference_site_id,COALESCE(s.path,p.path),COALESCE(s.language,f.language),
                           s.start_line,s.start_column,s.end_line,s.end_column,s.start_byte,s.end_byte,s.is_exact,s.provenance,s.containing_symbol_id
                    FROM pending_relationships AS p
                    JOIN files AS f ON f.file_id=p.file_id
                    LEFT JOIN reference_sites AS s ON s.reference_site_id=p.reference_site_id
                    WHERE p.from_symbol_id IN ({Placeholders(chunk.Length)})
                       OR COALESCE(p.caller_scope_symbol_id,p.from_symbol_id) IN ({Placeholders(chunk.Length)})
                    ORDER BY 1,p.pending_relationship_id
                    """
                : $"""
                    SELECT p.version_id,p.pending_relationship_id,p.from_symbol_id,p.caller_scope_symbol_id,
                           p.kind,p.target_terminal_name,p.target_display_name,p.target_receiver,p.target_namespace_json,
                           p.confidence,p.reference_site_id,COALESCE(s.path,p.path),COALESCE(s.language,e.language),
                           s.start_line,s.start_column,s.end_line,s.end_column,s.start_byte,s.end_byte,s.is_exact,s.provenance,s.containing_symbol_id
                    FROM main.pending_relationships AS p
                    JOIN main.manifest_entries AS e ON e.version_id=p.version_id
                    LEFT JOIN main.reference_sites AS s
                      ON s.version_id=p.version_id AND s.reference_site_id=p.reference_site_id
                    WHERE e.view_id=$view_id AND e.generation=$generation
                      AND (p.from_symbol_id IN ({Placeholders(chunk.Length)})
                           OR COALESCE(p.caller_scope_symbol_id,p.from_symbol_id) IN ({Placeholders(chunk.Length)}))
                    ORDER BY p.version_id,p.pending_relationship_id
                    """;
            BindVisibility(command);
            BindIds(command, chunk);
            foreach (PendingSite site in ReadPendings(command))
                yield return site;
        }
    }

    private IEnumerable<PendingSite> ReadPendingsByName(SqliteConnection connection, string name)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = _visibility is null
            ? """
                SELECT f.rowid,p.pending_relationship_id,p.from_symbol_id,p.caller_scope_symbol_id,
                       p.kind,p.target_terminal_name,p.target_display_name,p.target_receiver,p.target_namespace_json,
                       p.confidence,p.reference_site_id,COALESCE(s.path,p.path),COALESCE(s.language,f.language),
                       s.start_line,s.start_column,s.end_line,s.end_column,s.start_byte,s.end_byte,s.is_exact,s.provenance,s.containing_symbol_id
                FROM pending_relationships AS p
                JOIN files AS f ON f.file_id=p.file_id
                LEFT JOIN reference_sites AS s ON s.reference_site_id=p.reference_site_id
                WHERE p.target_terminal_name=$name
                ORDER BY 1,p.pending_relationship_id
                """
            : """
                SELECT p.version_id,p.pending_relationship_id,p.from_symbol_id,p.caller_scope_symbol_id,
                       p.kind,p.target_terminal_name,p.target_display_name,p.target_receiver,p.target_namespace_json,
                       p.confidence,p.reference_site_id,COALESCE(s.path,p.path),COALESCE(s.language,e.language),
                       s.start_line,s.start_column,s.end_line,s.end_column,s.start_byte,s.end_byte,s.is_exact,s.provenance,s.containing_symbol_id
                FROM main.pending_relationships AS p
                JOIN main.manifest_entries AS e ON e.version_id=p.version_id
                LEFT JOIN main.reference_sites AS s
                  ON s.version_id=p.version_id AND s.reference_site_id=p.reference_site_id
                WHERE e.view_id=$view_id AND e.generation=$generation AND p.target_terminal_name=$name
                ORDER BY p.version_id,p.pending_relationship_id
                """;
        BindVisibility(command);
        command.Parameters.AddWithValue("$name", name);
        foreach (PendingSite site in ReadPendings(command))
            yield return site;
    }

    private IEnumerable<PendingSite> ReadAllPendings(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = _visibility is null
            ? """
                SELECT f.rowid,p.pending_relationship_id,p.from_symbol_id,p.caller_scope_symbol_id,
                       p.kind,p.target_terminal_name,p.target_display_name,p.target_receiver,p.target_namespace_json,
                       p.confidence,p.reference_site_id,COALESCE(s.path,p.path),COALESCE(s.language,f.language),
                       s.start_line,s.start_column,s.end_line,s.end_column,s.start_byte,s.end_byte,s.is_exact,s.provenance,s.containing_symbol_id
                FROM pending_relationships AS p
                JOIN files AS f ON f.file_id=p.file_id
                LEFT JOIN reference_sites AS s ON s.reference_site_id=p.reference_site_id
                ORDER BY 1,p.pending_relationship_id
                """
            : """
                SELECT p.version_id,p.pending_relationship_id,p.from_symbol_id,p.caller_scope_symbol_id,
                       p.kind,p.target_terminal_name,p.target_display_name,p.target_receiver,p.target_namespace_json,
                       p.confidence,p.reference_site_id,COALESCE(s.path,p.path),COALESCE(s.language,e.language),
                       s.start_line,s.start_column,s.end_line,s.end_column,s.start_byte,s.end_byte,s.is_exact,s.provenance,s.containing_symbol_id
                FROM main.pending_relationships AS p
                JOIN main.manifest_entries AS e ON e.version_id=p.version_id
                LEFT JOIN main.reference_sites AS s
                  ON s.version_id=p.version_id AND s.reference_site_id=p.reference_site_id
                WHERE e.view_id=$view_id AND e.generation=$generation
                ORDER BY p.version_id,p.pending_relationship_id
                """;
        BindVisibility(command);
        return ReadPendings(command);
    }

    private IReadOnlyList<RelationshipSite> ReadAllRelationships(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = _visibility is null
            ? """
                SELECT COALESCE(f.rowid,-1),r.relationship_id,r.from_symbol_id,r.to_symbol_id,r.kind,r.confidence,
                       s.reference_site_id,s.path,COALESCE(NULLIF(s.language,''),'csharp'),s.start_line,s.start_column,s.end_line,s.end_column,
                       s.start_byte,s.end_byte,s.is_exact,s.provenance,s.containing_symbol_id
                FROM relationships AS r
                LEFT JOIN files AS f ON f.file_id=r.file_id
                JOIN reference_sites AS s ON s.reference_site_id=r.reference_site_id
                """
            : """
                SELECT r.version_id,r.relationship_id,r.from_symbol_id,r.to_symbol_id,r.kind,r.confidence,
                       s.reference_site_id,s.path,s.language,s.start_line,s.start_column,s.end_line,s.end_column,
                       s.start_byte,s.end_byte,s.is_exact,s.provenance,s.containing_symbol_id
                FROM main.relationships AS r
                JOIN main.manifest_entries AS e ON e.version_id=r.version_id
                JOIN main.reference_sites AS s
                  ON s.version_id=r.version_id AND s.reference_site_id=r.reference_site_id
                WHERE e.view_id=$view_id AND e.generation=$generation
                """;
        BindVisibility(command);
        var rows = new List<RelationshipSite>();
        var seen = new HashSet<(long VersionId, string RelationshipId)>(RelationshipKeyComparer.Instance);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = (reader.GetInt64(0), reader.GetString(1));
            if (!seen.Add(key))
                continue;
            rows.Add(new RelationshipSite(
                key.Item1,
                key.Item2,
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetDouble(5),
                reader.IsDBNull(6) ? key.Item2 : reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                ReadNullableInt64(reader, 9),
                ReadNullableInt64(reader, 10),
                ReadNullableInt64(reader, 11),
                ReadNullableInt64(reader, 12),
                ReadNullableInt64(reader, 13),
                ReadNullableInt64(reader, 14),
                reader.GetInt64(15) == 1,
                reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetString(17)));
        }

        return rows;
    }

    private IReadOnlyList<RelationshipSite> ReadRelationships(
        SqliteConnection connection,
        IReadOnlyList<string> candidateIds)
    {
        if (candidateIds.Count == 0)
            return [];

        var rows = new List<RelationshipSite>();
        var seen = new HashSet<(long VersionId, string RelationshipId)>(RelationshipKeyComparer.Instance);
        foreach (string[] chunk in Chunk(candidateIds, IdChunkSize))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = _visibility is null
                ? $"""
                    SELECT COALESCE(f.rowid,-1),r.relationship_id,r.from_symbol_id,r.to_symbol_id,r.kind,r.confidence,
                           s.reference_site_id,s.path,COALESCE(NULLIF(s.language,''),'csharp'),s.start_line,s.start_column,s.end_line,s.end_column,
                           s.start_byte,s.end_byte,s.is_exact,s.provenance,s.containing_symbol_id
                    FROM relationships AS r
                    LEFT JOIN files AS f ON f.file_id=r.file_id
                    JOIN reference_sites AS s ON s.reference_site_id=r.reference_site_id
                    WHERE r.from_symbol_id IN ({Placeholders(chunk.Length)})
                       OR r.to_symbol_id IN ({Placeholders(chunk.Length)})
                    """
                : $"""
                    SELECT r.version_id,r.relationship_id,r.from_symbol_id,r.to_symbol_id,r.kind,r.confidence,
                           s.reference_site_id,s.path,s.language,s.start_line,s.start_column,s.end_line,s.end_column,
                           s.start_byte,s.end_byte,s.is_exact,s.provenance,s.containing_symbol_id
                    FROM main.relationships AS r
                    JOIN main.manifest_entries AS e ON e.version_id=r.version_id
                    JOIN main.reference_sites AS s
                      ON s.version_id=r.version_id AND s.reference_site_id=r.reference_site_id
                    WHERE e.view_id=$view_id AND e.generation=$generation
                      AND (r.from_symbol_id IN ({Placeholders(chunk.Length)})
                           OR r.to_symbol_id IN ({Placeholders(chunk.Length)}))
                    """;
            BindVisibility(command);
            BindIds(command, chunk);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                var key = (reader.GetInt64(0), reader.GetString(1));
                if (!seen.Add(key))
                    continue;
                rows.Add(new RelationshipSite(
                    key.Item1,
                    key.Item2,
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetDouble(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    ReadNullableInt64(reader, 9),
                    ReadNullableInt64(reader, 10),
                    ReadNullableInt64(reader, 11),
                    ReadNullableInt64(reader, 12),
                    ReadNullableInt64(reader, 13),
                    ReadNullableInt64(reader, 14),
                    reader.GetInt64(15) == 1,
                    reader.GetString(16),
                    reader.IsDBNull(17) ? null : reader.GetString(17)));
            }
        }

        return rows;
    }

    private Dictionary<string, CandidateRecord> ReadCandidates(
        SqliteConnection connection,
        IReadOnlyList<string> candidateIds)
    {
        var candidates = new Dictionary<string, CandidateRecord>(StringComparer.Ordinal);
        if (candidateIds.Count == 0)
            return candidates;

        foreach (string[] chunk in Chunk(candidateIds, IdChunkSize))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = _visibility is null
                ? $"""
                    SELECT s.symbol_id,f.rowid,s.name
                    FROM symbols AS s
                    JOIN files AS f ON f.file_id=s.file_id
                    WHERE s.symbol_id IN ({Placeholders(chunk.Length)})
                    """
                : $"""
                    SELECT s.symbol_id,s.version_id,s.name
                    FROM main.symbols AS s
                    JOIN main.manifest_entries AS e ON e.version_id=s.version_id
                    WHERE e.view_id=$view_id AND e.generation=$generation
                      AND s.symbol_id IN ({Placeholders(chunk.Length)})
                    """;
            BindVisibility(command);
            BindIds(command, chunk);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string id = reader.GetString(0);
                candidates[id] = new CandidateRecord(id, reader.GetInt64(1), reader.GetString(2));
            }
        }

        return candidates;
    }

    private IReadOnlyList<string> ReadAllSymbolIds(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = _visibility is null
            ? "SELECT symbol_id FROM symbols"
            : """
                SELECT s.symbol_id
                FROM main.symbols AS s
                JOIN main.manifest_entries AS e ON e.version_id=s.version_id
                WHERE e.view_id=$view_id AND e.generation=$generation
                """;
        BindVisibility(command);
        using SqliteDataReader reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids;
    }

    private Dictionary<(long VersionId, long RowId), SiteDetails> ReadIdentifierDetailsBatch(
        SqliteConnection connection,
        IReadOnlyList<ResolutionIdentifierSite> sites)
    {
        var rows = new Dictionary<(long VersionId, long RowId), SiteDetails>();
        if (sites.Count == 0)
            return rows;

        var identifierIds = new Dictionary<(long VersionId, long RowId), string>();
        foreach (ResolutionIdentifierSite site in sites)
            identifierIds[(site.VersionId, site.RowId)] = site.IdentifierId;

        for (int offset = 0; offset < sites.Count; offset += IdChunkSize)
        {
            int count = Math.Min(IdChunkSize, sites.Count - offset);
            using SqliteCommand command = connection.CreateCommand();
            string values = ValuesClause(command, sites, offset, count);
            command.CommandText = _visibility is null
                ? $"""
                    WITH requested(version_id,row_id) AS (VALUES {values})
                    SELECT requested.version_id,requested.row_id,
                           COALESCE(s.path,i.path),COALESCE(s.language,i.language),i.reference_site_id,
                           s.start_line,s.start_column,s.end_line,s.end_column,s.start_byte,s.end_byte,
                           s.is_exact,s.provenance,s.containing_symbol_id
                    FROM requested
                    JOIN identifiers AS i ON i.rowid=requested.row_id
                    JOIN files AS f ON f.file_id=i.file_id AND f.rowid=requested.version_id
                    LEFT JOIN reference_sites AS s ON s.reference_site_id=i.reference_site_id
                    """
                : $"""
                    WITH requested(version_id,row_id) AS (VALUES {values})
                    SELECT requested.version_id,requested.row_id,
                           COALESCE(s.path,i.path),COALESCE(s.language,i.language),i.reference_site_id,
                           s.start_line,s.start_column,s.end_line,s.end_column,s.start_byte,s.end_byte,
                           s.is_exact,s.provenance,s.containing_symbol_id
                    FROM requested
                    JOIN main.identifiers AS i
                      ON i.version_id=requested.version_id AND i.rowid=requested.row_id
                    LEFT JOIN main.reference_sites AS s
                      ON s.version_id=i.version_id AND s.reference_site_id=i.reference_site_id
                    WHERE EXISTS (
                        SELECT 1
                        FROM main.manifest_entries AS e
                        WHERE e.version_id=i.version_id
                          AND e.view_id=$view_id AND e.generation=$generation)
                    """;
            BindVisibility(command);
            Counters.RecordIdentifierDetailCommand();
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                long versionId = reader.GetInt64(0);
                long rowId = reader.GetInt64(1);
                bool hasSite = !reader.IsDBNull(12);
                string key = identifierIds[(versionId, rowId)];
                rows[(versionId, rowId)] = new SiteDetails(
                    hasSite,
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? key : reader.GetString(4),
                    ReadNullableInt64(reader, 5),
                    ReadNullableInt64(reader, 6),
                    ReadNullableInt64(reader, 7),
                    ReadNullableInt64(reader, 8),
                    ReadNullableInt64(reader, 9),
                    ReadNullableInt64(reader, 10),
                    hasSite && reader.GetInt64(11) == 1,
                    hasSite ? reader.GetString(12) : string.Empty,
                    reader.IsDBNull(13) ? null : reader.GetString(13));
                Counters.RecordIdentifierDetailRow();
            }
        }

        return rows;
    }

    private SiteDetails MissingIdentifierDetails(ResolutionIdentifierSite site) =>
        new(
            HasSiteRow: false,
            string.Empty,
            _cache.Slice(site.VersionId)?.Language ?? string.Empty,
            site.IdentifierId,
            StartLine: null,
            StartColumn: null,
            EndLine: null,
            EndColumn: null,
            StartByte: null,
            EndByte: null,
            IsExact: false,
            string.Empty,
            SiteContainingSymbolId: null);

    private SiteDetails ReadIdentifierDetails(SqliteConnection connection, ResolutionIdentifierSite site)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = _visibility is null
            ? """
                SELECT COALESCE(s.path,i.path),COALESCE(s.language,i.language),i.reference_site_id,
                       s.start_line,s.start_column,s.end_line,s.end_column,s.start_byte,s.end_byte,
                       s.is_exact,s.provenance,s.containing_symbol_id
                FROM identifiers AS i
                LEFT JOIN reference_sites AS s ON s.reference_site_id=i.reference_site_id
                WHERE i.rowid=$rowid
                """
            : """
                SELECT COALESCE(s.path,i.path),COALESCE(s.language,i.language),i.reference_site_id,
                       s.start_line,s.start_column,s.end_line,s.end_column,s.start_byte,s.end_byte,
                       s.is_exact,s.provenance,s.containing_symbol_id
                FROM main.identifiers AS i
                LEFT JOIN main.reference_sites AS s
                  ON s.version_id=i.version_id AND s.reference_site_id=i.reference_site_id
                WHERE i.version_id=$version AND i.rowid=$rowid
                """;
        if (_visibility is { } visibility)
        {
            command.Parameters.AddWithValue("$version", site.VersionId);
        }

        command.Parameters.AddWithValue("$rowid", site.RowId);
        Counters.RecordIdentifierDetailCommand();
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return MissingIdentifierDetails(site);
        }

        Counters.RecordIdentifierDetailRow();
        bool hasSite = !reader.IsDBNull(10);
        return new SiteDetails(
            hasSite,
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? site.IdentifierId : reader.GetString(2),
            ReadNullableInt64(reader, 3),
            ReadNullableInt64(reader, 4),
            ReadNullableInt64(reader, 5),
            ReadNullableInt64(reader, 6),
            ReadNullableInt64(reader, 7),
            ReadNullableInt64(reader, 8),
            hasSite && reader.GetInt64(9) == 1,
            hasSite ? reader.GetString(10) : string.Empty,
            reader.IsDBNull(11) ? null : reader.GetString(11));
    }

    private void BindVisibility(SqliteCommand command)
    {
        if (_visibility is { } visibility)
        {
            command.Parameters.AddWithValue("$view_id", visibility.ViewId);
            command.Parameters.AddWithValue("$generation", visibility.ManifestGeneration);
        }
    }

    private static bool TryUniqueNameTarget(
        QueryScratch scratch,
        ResolvedIdentifier identifier,
        out string targetId)
    {
        targetId = string.Empty;
        if (identifier.Skip
            || identifier.Outcome.Kind == ResolutionOutcomeKind.Resolved
            || identifier.ContainingSymbolId is null)
        {
            return false;
        }

        string? unique = scratch.UniqueSymbolId(identifier.Name);
        if (unique is null || string.Equals(identifier.ContainingSymbolId, unique, StringComparison.Ordinal))
            return false;
        targetId = unique;
        return true;
    }

    private static string TargetName(QueryScratch scratch, string symbolId, string fallback) =>
        scratch.Symbol(symbolId)?.Name ?? fallback;

    private static Dictionary<string, List<ReferenceEvidence>> CreateListMap(IReadOnlyList<string> ids)
    {
        var map = new Dictionary<string, List<ReferenceEvidence>>(ids.Count, StringComparer.Ordinal);
        foreach (string id in ids)
            map[id] = [];
        return map;
    }

    private static Dictionary<string, List<OutgoingReferenceEvidence>> CreateOutgoingMap(IReadOnlyList<string> ids)
    {
        var map = new Dictionary<string, List<OutgoingReferenceEvidence>>(ids.Count, StringComparer.Ordinal);
        foreach (string id in ids)
            map[id] = [];
        return map;
    }

    private static ReferenceEvidence ToInbound(
        ResolvedIdentifier identifier,
        string? targetSymbolId,
        ReferenceEvidenceSource source,
        double confidence,
        int? tier,
        ReferenceResolutionStatus status) =>
        new(
            targetSymbolId,
            identifier.ContainingSymbolId,
            identifier.Details.Path,
            (int?)identifier.Details.StartLine,
            (int?)identifier.Details.StartColumn,
            (int?)identifier.Details.EndLine,
            (int?)identifier.Details.EndColumn,
            identifier.Details.StartByte,
            identifier.Details.EndByte,
            ReferenceEvidenceReader.NormalizeKind(identifier.Kind),
            identifier.Kind,
            source,
            tier,
            confidence,
            status,
            identifier.Details.Language,
            identifier.Details.ReferenceSiteId,
            identifier.Details.IsExact,
            identifier.Details.SiteProvenance);

    private static ReferenceEvidence ToInbound(
        ResolvedPending pending,
        string? targetSymbolId,
        ReferenceEvidenceSource source,
        double confidence,
        int? tier,
        ReferenceResolutionStatus status) =>
        new(
            targetSymbolId,
            pending.SiteContainingSymbolId,
            pending.Path,
            (int?)pending.StartLine,
            (int?)pending.StartColumn,
            (int?)pending.EndLine,
            (int?)pending.EndColumn,
            pending.StartByte,
            pending.EndByte,
            ReferenceEvidenceReader.NormalizeKind(pending.Kind),
            pending.Kind,
            source,
            tier,
            confidence,
            status,
            pending.Language,
            pending.ReferenceSiteId,
            pending.IsExact,
            pending.SiteProvenance);

    private static ReferenceEvidence ToInbound(
        RelationshipSite relationship,
        string targetSymbolId,
        ReferenceEvidenceSource source,
        double confidence,
        int? tier,
        ReferenceResolutionStatus status) =>
        new(
            targetSymbolId,
            relationship.SiteContainingSymbolId,
            relationship.Path,
            (int?)relationship.StartLine,
            (int?)relationship.StartColumn,
            (int?)relationship.EndLine,
            (int?)relationship.EndColumn,
            relationship.StartByte,
            relationship.EndByte,
            ReferenceEvidenceReader.NormalizeKind(relationship.Kind),
            relationship.Kind,
            source,
            tier,
            confidence,
            status,
            relationship.Language,
            relationship.ReferenceSiteId,
            relationship.IsExact,
            relationship.SiteProvenance);

    private static OutgoingReferenceEvidence ToOutgoing(
        ResolvedIdentifier identifier,
        string containingSymbolId,
        string? targetSymbolId,
        string targetName,
        ReferenceEvidenceSource source,
        double confidence,
        int? tier,
        ReferenceResolutionStatus status) =>
        new(
            containingSymbolId,
            targetSymbolId,
            targetName,
            identifier.Details.Path,
            (int?)identifier.Details.StartLine,
            (int?)identifier.Details.StartColumn,
            (int?)identifier.Details.EndLine,
            (int?)identifier.Details.EndColumn,
            identifier.Details.StartByte,
            identifier.Details.EndByte,
            ReferenceEvidenceReader.NormalizeKind(identifier.Kind),
            identifier.Kind,
            source,
            tier,
            confidence,
            status,
            identifier.Details.Language,
            identifier.Details.ReferenceSiteId,
            identifier.Details.IsExact,
            identifier.Details.SiteProvenance);

    private static OutgoingReferenceEvidence ToOutgoing(
        ResolvedPending pending,
        string containingSymbolId,
        string? targetSymbolId,
        string targetName,
        ReferenceEvidenceSource source,
        double confidence,
        int? tier,
        ReferenceResolutionStatus status) =>
        new(
            containingSymbolId,
            targetSymbolId,
            targetName,
            pending.Path,
            (int?)pending.StartLine,
            (int?)pending.StartColumn,
            (int?)pending.EndLine,
            (int?)pending.EndColumn,
            pending.StartByte,
            pending.EndByte,
            ReferenceEvidenceReader.NormalizeKind(pending.Kind),
            pending.Kind,
            source,
            tier,
            confidence,
            status,
            pending.Language,
            pending.ReferenceSiteId,
            pending.IsExact,
            pending.SiteProvenance);

    private static OutgoingReferenceEvidence ToOutgoing(
        RelationshipSite relationship,
        string containingSymbolId,
        string targetSymbolId,
        string targetName,
        ReferenceEvidenceSource source,
        double confidence,
        int? tier,
        ReferenceResolutionStatus status) =>
        new(
            containingSymbolId,
            targetSymbolId,
            targetName,
            relationship.Path,
            (int?)relationship.StartLine,
            (int?)relationship.StartColumn,
            (int?)relationship.EndLine,
            (int?)relationship.EndColumn,
            relationship.StartByte,
            relationship.EndByte,
            ReferenceEvidenceReader.NormalizeKind(relationship.Kind),
            relationship.Kind,
            source,
            tier,
            confidence,
            status,
            relationship.Language,
            relationship.ReferenceSiteId,
            relationship.IsExact,
            relationship.SiteProvenance);

    private QueryTimeExportEvidence ToExport(
        ResolvedIdentifier identifier,
        QueryScratch scratch,
        SqliteConnection connection,
        string? targetSymbolId,
        string targetName,
        string? targetKind,
        bool? targetIsTest,
        int? tier,
        double confidence,
        string source)
    {
        string? siteContaining = identifier.Details.SiteContainingSymbolId;
        ExportSymbolFacts? sourceFacts = siteContaining is null
            ? null
            : ReadExportSymbol(connection, siteContaining);
        FactSymbol? sourceSymbol = siteContaining is null
            ? null
            : scratch.Symbol(siteContaining);
        return new QueryTimeExportEvidence(
            identifier.Details.ReferenceSiteId,
            identifier.Details.IsExact,
            identifier.Details.SiteProvenance,
            identifier.Details.Path,
            identifier.Details.Language,
            siteContaining,
            identifier.Details.StartLine,
            identifier.Details.StartColumn,
            identifier.Details.EndLine,
            identifier.Details.EndColumn,
            identifier.Details.StartByte,
            identifier.Details.EndByte,
            CanonicalKind(identifier.Kind),
            targetSymbolId,
            targetName,
            targetKind,
            targetIsTest,
            tier,
            confidence,
            source,
            sourceFacts?.Name ?? sourceSymbol?.Name,
            sourceFacts?.Kind ?? (sourceSymbol is null ? null : KindString(sourceSymbol.Kind)),
            sourceFacts?.IsTest ?? sourceSymbol?.IsTest());
    }

    private QueryTimeExportEvidence ToExport(
        ResolvedPending pending,
        QueryScratch scratch,
        SqliteConnection connection,
        string? targetSymbolId,
        string targetName,
        string? targetKind,
        bool? targetIsTest,
        int? tier,
        double confidence,
        string source)
    {
        string? siteContaining = pending.SiteContainingSymbolId;
        ExportSymbolFacts? sourceFacts = siteContaining is null
            ? null
            : ReadExportSymbol(connection, siteContaining);
        FactSymbol? sourceSymbol = siteContaining is null
            ? null
            : scratch.Symbol(siteContaining);
        return new QueryTimeExportEvidence(
            pending.ReferenceSiteId,
            pending.IsExact,
            pending.SiteProvenance,
            pending.Path,
            pending.Language,
            siteContaining,
            pending.StartLine,
            pending.StartColumn,
            pending.EndLine,
            pending.EndColumn,
            pending.StartByte,
            pending.EndByte,
            CanonicalKind(pending.Kind),
            targetSymbolId,
            targetName,
            targetKind,
            targetIsTest,
            tier,
            confidence,
            source,
            sourceFacts?.Name ?? sourceSymbol?.Name,
            sourceFacts?.Kind ?? (sourceSymbol is null ? null : KindString(sourceSymbol.Kind)),
            sourceFacts?.IsTest ?? sourceSymbol?.IsTest());
    }

    private ExportSymbolFacts? ReadExportSymbol(SqliteConnection connection, string symbolId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = _visibility is null
            ? """
                SELECT name,kind,is_test
                FROM symbols
                WHERE symbol_id=$id
                LIMIT 1
                """
            : """
                SELECT s.name,s.kind,s.is_test
                FROM main.symbols AS s
                JOIN main.manifest_entries AS e ON e.version_id=s.version_id
                WHERE e.view_id=$view_id AND e.generation=$generation AND s.symbol_id=$id
                LIMIT 1
                """;
        BindVisibility(command);
        command.Parameters.AddWithValue("$id", symbolId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new ExportSymbolFacts(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2) != 0);
    }

    private readonly record struct ExportSymbolFacts(string Name, string Kind, bool? IsTest);

    private static string CanonicalKind(string kind) => kind switch
    {
        "calls" => "call",
        "imports" => "import",
        "references" => "reference",
        "uses" => "usage",
        _ => kind,
    };

    private static string KindString(FactSymbolKind kind) => kind switch
    {
        FactSymbolKind.Class => "class",
        FactSymbolKind.Interface => "interface",
        FactSymbolKind.Function => "function",
        FactSymbolKind.Method => "method",
        FactSymbolKind.Variable => "variable",
        FactSymbolKind.Constant => "constant",
        FactSymbolKind.Property => "property",
        FactSymbolKind.Enum => "enum",
        FactSymbolKind.EnumMember => "enum_member",
        FactSymbolKind.Module => "module",
        FactSymbolKind.Namespace => "namespace",
        FactSymbolKind.Type => "type",
        FactSymbolKind.Trait => "trait",
        FactSymbolKind.Struct => "struct",
        FactSymbolKind.Union => "union",
        FactSymbolKind.Field => "field",
        FactSymbolKind.Constructor => "constructor",
        FactSymbolKind.Destructor => "destructor",
        FactSymbolKind.Operator => "operator",
        FactSymbolKind.Import => "import",
        FactSymbolKind.Export => "export",
        FactSymbolKind.Event => "event",
        FactSymbolKind.Delegate => "delegate",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static List<PendingSite> ReadPendings(SqliteCommand command)
    {
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<PendingSite>();
        while (reader.Read())
        {
            bool hasSite = !reader.IsDBNull(20);
            rows.Add(new PendingSite(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                QualifierFromNamespaceJson(reader.IsDBNull(8) ? null : reader.GetString(8)),
                reader.GetDouble(9),
                reader.IsDBNull(10) ? reader.GetString(1) : reader.GetString(10),
                hasSite,
                reader.GetString(11),
                reader.GetString(12),
                ReadNullableInt64(reader, 13),
                ReadNullableInt64(reader, 14),
                ReadNullableInt64(reader, 15),
                ReadNullableInt64(reader, 16),
                ReadNullableInt64(reader, 17),
                ReadNullableInt64(reader, 18),
                hasSite && reader.GetInt64(19) == 1,
                hasSite ? reader.GetString(20) : string.Empty,
                reader.IsDBNull(21) ? null : reader.GetString(21)));
        }

        return rows;
    }

    private static string? QualifierFromNamespaceJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return null;
            var parts = new List<string>();
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String
                    && element.GetString() is { Length: > 0 } value)
                {
                    parts.Add(value);
                }
            }

            return parts.Count == 0 ? null : string.Join('.', parts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long? ReadNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static IEnumerable<string[]> Chunk(IReadOnlyList<string> ids, int size)
    {
        for (int offset = 0; offset < ids.Count; offset += size)
        {
            int take = Math.Min(size, ids.Count - offset);
            var chunk = new string[take];
            for (int i = 0; i < take; i++)
                chunk[i] = ids[offset + i];
            yield return chunk;
        }
    }

    private static string Placeholders(int count)
    {
        var parts = new string[count];
        for (int i = 0; i < count; i++)
            parts[i] = "$id" + i.ToString(CultureInfo.InvariantCulture);
        return string.Join(',', parts);
    }

    private static string ValuesClause(
        SqliteCommand command,
        IReadOnlyList<ResolutionIdentifierSite> sites,
        int offset,
        int count)
    {
        var rows = new string[count];
        for (int i = 0; i < count; i++)
        {
            int index = offset + i;
            rows[i] = $"($version{i.ToString(CultureInfo.InvariantCulture)},$row{i.ToString(CultureInfo.InvariantCulture)})";
            command.Parameters.AddWithValue(
                "$version" + i.ToString(CultureInfo.InvariantCulture),
                sites[index].VersionId);
            command.Parameters.AddWithValue(
                "$row" + i.ToString(CultureInfo.InvariantCulture),
                sites[index].RowId);
        }

        return string.Join(',', rows);
    }

    private static void BindIds(SqliteCommand command, string[] ids)
    {
        for (int i = 0; i < ids.Length; i++)
            command.Parameters.AddWithValue("$id" + i.ToString(CultureInfo.InvariantCulture), ids[i]);
    }

    private readonly record struct CandidateRecord(string Id, long VersionId, string Name);

    private sealed record PendingScratch(
        SqliteConnection Connection,
        string[] CandidateIds,
        Direction Direction,
        GraphReadKind Kind,
        QueryScratch Scratch);

    private enum GraphReadKind
    {
        Resolution,
        Unresolved,
    }

    private readonly record struct SiteDetails(
        bool HasSiteRow,
        string Path,
        string Language,
        string ReferenceSiteId,
        long? StartLine,
        long? StartColumn,
        long? EndLine,
        long? EndColumn,
        long? StartByte,
        long? EndByte,
        bool IsExact,
        string SiteProvenance,
        string? SiteContainingSymbolId);

    private sealed record PendingSite(
        long VersionId,
        string PendingId,
        string FromSymbolId,
        string? CallerScopeSymbolId,
        string Kind,
        string Name,
        string DisplayName,
        string? Receiver,
        string? ReceiverQualifier,
        double Confidence,
        string ReferenceSiteId,
        bool HasSiteRow,
        string Path,
        string Language,
        long? StartLine,
        long? StartColumn,
        long? EndLine,
        long? EndColumn,
        long? StartByte,
        long? EndByte,
        bool IsExact,
        string SiteProvenance,
        string? SiteContainingSymbolId)
    {
        internal string EvidenceContainerId => CallerScopeSymbolId ?? FromSymbolId;
    }

    private sealed record RelationshipSite(
        long VersionId,
        string RelationshipId,
        string FromSymbolId,
        string ToSymbolId,
        string Kind,
        double Confidence,
        string ReferenceSiteId,
        string Path,
        string Language,
        long? StartLine,
        long? StartColumn,
        long? EndLine,
        long? EndColumn,
        long? StartByte,
        long? EndByte,
        bool IsExact,
        string SiteProvenance,
        string? SiteContainingSymbolId);

    private sealed record ResolvedIdentifier(
        ResolutionIdentifierSite Site,
        SiteDetails Details,
        ResolutionOutcome Outcome,
        bool Skip,
        PropagationSource Override)
    {
        internal long VersionId => Site.VersionId;

        internal long RowId => Site.RowId;

        internal string Name => Site.Name;

        internal string Kind => Site.Kind;

        internal string? ContainingSymbolId => Site.ContainingSymbolId;

        internal double Confidence => Site.Confidence;

        internal long StartByte => Site.StartByte;

        internal long EndByte => Site.EndByte;

        internal long StartLine => Site.StartLine;
    }

    private sealed record ResolvedPending(PendingSite Site, ResolutionOutcome Outcome)
    {
        internal string PendingId => Site.PendingId;

        internal string FromSymbolId => Site.FromSymbolId;

        internal string EvidenceContainerId => Site.EvidenceContainerId;

        internal string Kind => Site.Kind;

        internal string Name => Site.Name;

        internal string DisplayName => Site.DisplayName;

        internal double Confidence => Site.Confidence;

        internal string Path => Site.Path;

        internal string Language => Site.Language;

        internal string ReferenceSiteId => Site.ReferenceSiteId;

        internal bool HasSiteRow => Site.HasSiteRow;

        internal long? StartByte => Site.StartByte;

        internal long? EndByte => Site.EndByte;

        internal long? StartLine => Site.StartLine;

        internal long? StartColumn => Site.StartColumn;

        internal long? EndLine => Site.EndLine;

        internal long? EndColumn => Site.EndColumn;

        internal bool IsExact => Site.IsExact;

        internal string SiteProvenance => Site.SiteProvenance;

        internal string? SiteContainingSymbolId => Site.SiteContainingSymbolId;
    }

    private sealed class QueryScratch
    {
        private readonly RevisionFactCache _cache;
        private readonly Dictionary<string, FactSymbol> _symbolsById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ResolvedIdentifier>> _identifiersByContainer;
        private readonly Dictionary<string, List<ResolvedIdentifier>> _identifiersByName;
        private readonly Dictionary<string, List<ResolvedPending>> _pendingsByFrom;
        private readonly Dictionary<string, List<ResolvedPending>> _pendingsByEvidence;
        private readonly Dictionary<string, List<ResolvedPending>> _pendingsByName;
        private readonly Dictionary<string, List<RelationshipSite>> _relationshipsFrom;
        private readonly Dictionary<string, List<RelationshipSite>> _relationshipsTo;

        internal QueryScratch(
            RevisionFactCache cache,
            Dictionary<string, CandidateRecord> candidates,
            List<ResolvedIdentifier> identifiers,
            List<ResolvedPending> pendings,
            IReadOnlyList<RelationshipSite> relationships)
        {
            _cache = cache;
            Candidates = candidates;
            AllIdentifiers = identifiers;
            AllPendings = pendings;
            AllRelationships = relationships;
            _identifiersByContainer = Group(identifiers, static row => row.ContainingSymbolId);
            _identifiersByName = Group(identifiers, static row => row.Name);
            _pendingsByFrom = Group(pendings, static row => row.FromSymbolId);
            _pendingsByEvidence = Group(pendings, static row => row.EvidenceContainerId);
            _pendingsByName = Group(pendings, static row => row.Name);
            _relationshipsFrom = Group(relationships, static row => row.FromSymbolId);
            _relationshipsTo = Group(relationships, static row => row.ToSymbolId);
            var versions = new HashSet<long>();
            foreach (CandidateRecord candidate in candidates.Values)
                versions.Add(candidate.VersionId);
            foreach (ResolvedIdentifier identifier in identifiers)
                versions.Add(identifier.VersionId);
            foreach (ResolvedPending pending in pendings)
                versions.Add(pending.Site.VersionId);
            foreach (long versionId in versions)
            {
                foreach (FactSymbol symbol in cache.SymbolsOfVersion(versionId))
                    _symbolsById[symbol.Key.SymbolId] = symbol;
            }
        }

        internal Dictionary<string, CandidateRecord> Candidates { get; }

        internal IReadOnlyList<ResolvedIdentifier> AllIdentifiers { get; }

        internal IReadOnlyList<ResolvedPending> AllPendings { get; }

        internal IReadOnlyList<RelationshipSite> AllRelationships { get; }

        internal IEnumerable<ResolvedIdentifier> IdentifiersByContainer(string id) =>
            _identifiersByContainer.TryGetValue(id, out List<ResolvedIdentifier>? rows) ? rows : [];

        internal IEnumerable<ResolvedIdentifier> IdentifiersNamed(string name) =>
            _identifiersByName.TryGetValue(name, out List<ResolvedIdentifier>? rows) ? rows : [];

        internal IEnumerable<ResolvedPending> PendingsByFrom(string id) =>
            _pendingsByFrom.TryGetValue(id, out List<ResolvedPending>? rows) ? rows : [];

        internal IEnumerable<ResolvedPending> PendingsByEvidenceContainer(string id) =>
            _pendingsByEvidence.TryGetValue(id, out List<ResolvedPending>? rows) ? rows : [];

        internal IEnumerable<ResolvedPending> PendingsNamed(string name) =>
            _pendingsByName.TryGetValue(name, out List<ResolvedPending>? rows) ? rows : [];

        internal IEnumerable<RelationshipSite> RelationshipsFrom(string id) =>
            _relationshipsFrom.TryGetValue(id, out List<RelationshipSite>? rows) ? rows : [];

        internal IEnumerable<RelationshipSite> RelationshipsTo(string id) =>
            _relationshipsTo.TryGetValue(id, out List<RelationshipSite>? rows) ? rows : [];

        internal FactSymbol? Symbol(string symbolId) =>
            _symbolsById.TryGetValue(symbolId, out FactSymbol? symbol) ? symbol : null;

        internal string? UniqueSymbolId(string name)
        {
            string? unique = null;
            foreach (FactSymbol symbol in _cache.SymbolsNamed(name))
            {
                if (unique is null)
                {
                    unique = symbol.Key.SymbolId;
                    continue;
                }

                if (!string.Equals(unique, symbol.Key.SymbolId, StringComparison.Ordinal))
                    return null;
            }

            return unique;
        }

        private static Dictionary<string, List<T>> Group<T>(IEnumerable<T> rows, Func<T, string?> key)
        {
            var map = new Dictionary<string, List<T>>(StringComparer.Ordinal);
            foreach (T row in rows)
            {
                if (key(row) is not { } id)
                    continue;
                if (!map.TryGetValue(id, out List<T>? list))
                {
                    list = [];
                    map[id] = list;
                }

                list.Add(row);
            }

            return map;
        }
    }

    private sealed class PendingKeyComparer : IEqualityComparer<(long VersionId, string PendingId)>
    {
        internal static readonly PendingKeyComparer Instance = new();

        public bool Equals((long VersionId, string PendingId) x, (long VersionId, string PendingId) y) =>
            x.VersionId == y.VersionId && string.Equals(x.PendingId, y.PendingId, StringComparison.Ordinal);

        public int GetHashCode((long VersionId, string PendingId) obj) =>
            HashCode.Combine(obj.VersionId, StringComparer.Ordinal.GetHashCode(obj.PendingId));
    }

    private sealed class RelationshipKeyComparer : IEqualityComparer<(long VersionId, string RelationshipId)>
    {
        internal static readonly RelationshipKeyComparer Instance = new();

        public bool Equals((long VersionId, string RelationshipId) x, (long VersionId, string RelationshipId) y) =>
            x.VersionId == y.VersionId && string.Equals(x.RelationshipId, y.RelationshipId, StringComparison.Ordinal);

        public int GetHashCode((long VersionId, string RelationshipId) obj) =>
            HashCode.Combine(obj.VersionId, StringComparer.Ordinal.GetHashCode(obj.RelationshipId));
    }
}

file static class QueryTimeFactSymbolExtensions
{
    internal static bool? IsTest(this FactSymbol _) => false;
}
