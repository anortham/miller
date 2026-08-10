using Microsoft.Data.Sqlite;
using Miller.Core.References;
using Miller.Indexing.Reads;

namespace Miller.Indexing;

/// <summary>Inbound, outgoing, and kind-partitioned evidence read from one artifact snapshot.</summary>
public sealed record ReferenceEvidenceBundle(
    ReferenceEvidenceSet Inbound,
    OutgoingReferenceEvidenceSet Outgoing,
    IReadOnlyDictionary<ReferenceKind, ReferenceEvidenceSet> InboundKinds,
    IReadOnlyDictionary<ReferenceKind, OutgoingReferenceEvidenceSet> OutgoingKinds);

/// <summary>Reads bounded, normalized reference evidence keyed by resolved symbol IDs.</summary>
public static class ReferenceEvidenceReader
{
    private static readonly string[] RequiredResolutionTables =
        ["reference_sites", "identifier_resolutions", "pending_resolutions", "pending_relationships"];

    public static ReferenceEvidenceBundle ReadForSymbol(
        IWorkspaceReadSession session,
        string symbolId,
        ReferenceEvidenceQuery inboundQuery,
        ReferenceEvidenceQuery outgoingQuery,
        ReferenceEvidenceBounds kindBounds,
        IReadOnlyList<ReferenceKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolId);
        inboundQuery.Validate();
        outgoingQuery.Validate();
        kindBounds.Validate();
        ArgumentNullException.ThrowIfNull(kinds);
        if (kinds.Count == 0)
            throw new ArgumentException("At least one reference kind is required.", nameof(kinds));

        return session.Read(connection => ReadForSymbol(
            connection,
            symbolId,
            inboundQuery,
            outgoingQuery,
            kindBounds,
            kinds));
    }

    /// <summary>Read inbound, outgoing, and selected relationship kinds for one symbol from one snapshot.</summary>
    public static ReferenceEvidenceBundle ReadForSymbol(
        string dbPath,
        string symbolId,
        ReferenceEvidenceQuery inboundQuery,
        ReferenceEvidenceQuery outgoingQuery,
        ReferenceEvidenceBounds kindBounds,
        IReadOnlyList<ReferenceKind> kinds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolId);
        inboundQuery.Validate();
        outgoingQuery.Validate();
        kindBounds.Validate();
        ArgumentNullException.ThrowIfNull(kinds);
        if (kinds.Count == 0)
            throw new ArgumentException("At least one reference kind is required.", nameof(kinds));

        using var connection = SqliteReadOnlyAccess.Open(dbPath);
        JulieSchemaGate.Verify(connection);
        RequireResolutionTables(connection);

        string targetName = ReadTargetName(connection, symbolId);
        List<ReferenceEvidence> inboundExactRows = ReadExact(connection, symbolId);
        List<ReferenceEvidence> inboundFallbackRows = ReadFallback(connection, symbolId, targetName);
        int sameNameDefinitionCount = CountDefinitions(connection, symbolId, targetName);
        List<OutgoingReferenceEvidence> outgoingExactRows = ReadOutgoingExact(connection, symbolId);
        List<OutgoingReferenceEvidence> outgoingFallbackRows = ReadOutgoingFallback(connection, symbolId);
        ReferenceEvidenceSnapshot snapshot = ReadSnapshot(connection);
        ReferenceKind[] distinctKinds = kinds.Distinct().ToArray();

        return new ReferenceEvidenceBundle(
            BuildInboundSet(
                inboundExactRows,
                inboundFallbackRows,
                inboundQuery,
                sameNameDefinitionCount,
                snapshot),
            BuildOutgoingSet(
                outgoingExactRows,
                outgoingFallbackRows,
                outgoingQuery,
                snapshot),
            distinctKinds.ToDictionary(
                static kind => kind,
                kind => BuildInboundSet(
                    inboundExactRows,
                    inboundFallbackRows,
                    new ReferenceEvidenceQuery(kindBounds, kind),
                    sameNameDefinitionCount,
                    snapshot)),
            distinctKinds.ToDictionary(
                static kind => kind,
                kind => BuildOutgoingSet(
                    outgoingExactRows,
                    outgoingFallbackRows,
                    new ReferenceEvidenceQuery(kindBounds, kind),
                    snapshot)));
    }

    /// <summary>Read exact inbound sites and separately typed fallback candidates for one symbol.</summary>
    public static ReferenceEvidenceSet Read(
        string dbPath,
        string targetSymbolId,
        ReferenceEvidenceBounds bounds) =>
        Read(dbPath, targetSymbolId, new ReferenceEvidenceQuery(bounds));

    /// <summary>Read one filtered, stateless inbound evidence page.</summary>
    public static ReferenceEvidenceSet Read(
        string dbPath,
        string targetSymbolId,
        ReferenceEvidenceQuery query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSymbolId);
        query.Validate();

        using var connection = SqliteReadOnlyAccess.Open(dbPath);
        JulieSchemaGate.Verify(connection);
        RequireResolutionTables(connection);

        string targetName = ReadTargetName(connection, targetSymbolId);
        List<ReferenceEvidence> exactRows = ReadExact(connection, targetSymbolId);
        List<ReferenceEvidence> fallbackRows = ReadFallback(connection, targetSymbolId, targetName);
        int sameNameDefinitionCount = CountDefinitions(connection, targetSymbolId, targetName);
        return BuildInboundSet(
            exactRows,
            fallbackRows,
            query,
            sameNameDefinitionCount,
            ReadSnapshot(connection));
    }

    public static ReferenceEvidenceSet Read(
        IWorkspaceReadSession session,
        string targetSymbolId,
        ReferenceEvidenceBounds bounds) =>
        Read(session, targetSymbolId, new ReferenceEvidenceQuery(bounds));

    public static ReferenceEvidenceSet Read(
        IWorkspaceReadSession session,
        string targetSymbolId,
        ReferenceEvidenceQuery query)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSymbolId);
        query.Validate();
        return session.Read(connection => ReadInbound(connection, targetSymbolId, query));
    }

    /// <summary>Read several independently bounded inbound relationship kinds from one artifact snapshot.</summary>
    public static IReadOnlyDictionary<ReferenceKind, ReferenceEvidenceSet> ReadKinds(
        string dbPath,
        string targetSymbolId,
        ReferenceEvidenceBounds bounds,
        IReadOnlyList<ReferenceKind> kinds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSymbolId);
        bounds.Validate();
        ArgumentNullException.ThrowIfNull(kinds);
        if (kinds.Count == 0)
            throw new ArgumentException("At least one reference kind is required.", nameof(kinds));

        using var connection = SqliteReadOnlyAccess.Open(dbPath);
        JulieSchemaGate.Verify(connection);
        RequireResolutionTables(connection);

        string targetName = ReadTargetName(connection, targetSymbolId);
        List<ReferenceEvidence> exactRows = ReadExact(connection, targetSymbolId);
        List<ReferenceEvidence> fallbackRows = ReadFallback(connection, targetSymbolId, targetName);
        int sameNameDefinitionCount = CountDefinitions(connection, targetSymbolId, targetName);
        ReferenceEvidenceSnapshot snapshot = ReadSnapshot(connection);
        return kinds
            .Distinct()
            .ToDictionary(
                static kind => kind,
                kind => BuildInboundSet(
                    exactRows,
                    fallbackRows,
                    new ReferenceEvidenceQuery(bounds, kind),
                    sameNameDefinitionCount,
                    snapshot));
    }

    private static ReferenceEvidenceSet BuildInboundSet(
        List<ReferenceEvidence> allExactRows,
        List<ReferenceEvidence> allFallbackRows,
        ReferenceEvidenceQuery query,
        int sameNameDefinitionCount,
        ReferenceEvidenceSnapshot snapshot)
    {
        var exactRows = FilterKind(allExactRows, query.Kind);
        var exact = Deduplicate(exactRows);
        int exactAvailable = exact.Count;
        var boundedExact = exact.Skip(query.ExactOffset).Take(query.Bounds.ExactLimit).ToArray();

        var fallbackRows = FilterKind(allFallbackRows, query.Kind);
        var fallbackCandidates = Deduplicate(fallbackRows);
        IReadOnlyList<ReferenceEvidence> fallback;
        int fallbackAvailable = fallbackCandidates.Count;
        ReferenceFallbackStatus fallbackStatus;
        if (sameNameDefinitionCount > 1)
        {
            fallback = Array.Empty<ReferenceEvidence>();
            fallbackStatus = ReferenceFallbackStatus.SuppressedAmbiguousName;
        }
        else
        {
            fallback = fallbackCandidates
                .Skip(query.FallbackOffset)
                .Take(query.Bounds.FallbackLimit)
                .ToArray();
            fallbackStatus = fallbackCandidates.Count == 0
                ? ReferenceFallbackStatus.NoCandidates
                : ReferenceFallbackStatus.Available;
        }

        return new ReferenceEvidenceSet(
            boundedExact,
            fallback,
            new ReferenceEvidenceCoverage(
                exactRows.Count,
                exactAvailable,
                boundedExact.Length,
                fallbackAvailable,
                fallback.Count,
                sameNameDefinitionCount,
                exactAvailable > query.ExactOffset + boundedExact.Length,
                fallbackStatus == ReferenceFallbackStatus.Available &&
                fallbackAvailable > query.FallbackOffset + fallback.Count,
                fallbackStatus),
            snapshot)
        {
            ExactCallerSymbolIds = ExactContainingSymbolIds(exact, callLike: true),
            ExactReferencedBySymbolIds = ExactContainingSymbolIds(exact, callLike: false),
        };
    }

    /// <summary>Read resolved outgoing sites and separately typed unresolved fallbacks for one symbol.</summary>
    public static OutgoingReferenceEvidenceSet ReadOutgoing(
        string dbPath,
        string containingSymbolId,
        ReferenceEvidenceBounds bounds) =>
        ReadOutgoing(dbPath, containingSymbolId, new ReferenceEvidenceQuery(bounds));

    /// <summary>Read one filtered, stateless outgoing evidence page.</summary>
    public static OutgoingReferenceEvidenceSet ReadOutgoing(
        string dbPath,
        string containingSymbolId,
        ReferenceEvidenceQuery query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(containingSymbolId);
        query.Validate();

        using var connection = SqliteReadOnlyAccess.Open(dbPath);
        JulieSchemaGate.Verify(connection);
        RequireResolutionTables(connection);
        RequireSymbol(connection, containingSymbolId);

        List<OutgoingReferenceEvidence> exactRows =
            ReadOutgoingExact(connection, containingSymbolId);
        List<OutgoingReferenceEvidence> fallbackRows =
            ReadOutgoingFallback(connection, containingSymbolId);
        return BuildOutgoingSet(
            exactRows,
            fallbackRows,
            query,
            ReadSnapshot(connection));
    }

    public static OutgoingReferenceEvidenceSet ReadOutgoing(
        IWorkspaceReadSession session,
        string containingSymbolId,
        ReferenceEvidenceBounds bounds) =>
        ReadOutgoing(session, containingSymbolId, new ReferenceEvidenceQuery(bounds));

    public static OutgoingReferenceEvidenceSet ReadOutgoing(
        IWorkspaceReadSession session,
        string containingSymbolId,
        ReferenceEvidenceQuery query)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(containingSymbolId);
        query.Validate();
        return session.Read(connection => ReadOutgoing(connection, containingSymbolId, query));
    }

    /// <summary>Read several independently bounded outgoing relationship kinds from one artifact snapshot.</summary>
    public static IReadOnlyDictionary<ReferenceKind, OutgoingReferenceEvidenceSet> ReadOutgoingKinds(
        string dbPath,
        string containingSymbolId,
        ReferenceEvidenceBounds bounds,
        IReadOnlyList<ReferenceKind> kinds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(containingSymbolId);
        bounds.Validate();
        ArgumentNullException.ThrowIfNull(kinds);
        if (kinds.Count == 0)
            throw new ArgumentException("At least one reference kind is required.", nameof(kinds));

        using var connection = SqliteReadOnlyAccess.Open(dbPath);
        JulieSchemaGate.Verify(connection);
        RequireResolutionTables(connection);
        RequireSymbol(connection, containingSymbolId);

        List<OutgoingReferenceEvidence> exactRows =
            ReadOutgoingExact(connection, containingSymbolId);
        List<OutgoingReferenceEvidence> fallbackRows =
            ReadOutgoingFallback(connection, containingSymbolId);
        ReferenceEvidenceSnapshot snapshot = ReadSnapshot(connection);
        return kinds
            .Distinct()
            .ToDictionary(
                static kind => kind,
                kind => BuildOutgoingSet(
                    exactRows,
                    fallbackRows,
                    new ReferenceEvidenceQuery(bounds, kind),
                    snapshot));
    }

    private static OutgoingReferenceEvidenceSet BuildOutgoingSet(
        List<OutgoingReferenceEvidence> allExactRows,
        List<OutgoingReferenceEvidence> allFallbackRows,
        ReferenceEvidenceQuery query,
        ReferenceEvidenceSnapshot snapshot)
    {
        var exactRows = FilterOutgoingKind(allExactRows, query.Kind);
        var exact = DeduplicateOutgoing(exactRows);
        var fallbackRows = FilterOutgoingKind(allFallbackRows, query.Kind);
        var fallback = DeduplicateOutgoing(fallbackRows);
        var boundedExact = exact.Skip(query.ExactOffset).Take(query.Bounds.ExactLimit).ToArray();
        var boundedFallback = fallback
            .Skip(query.FallbackOffset)
            .Take(query.Bounds.FallbackLimit)
            .ToArray();

        return new OutgoingReferenceEvidenceSet(
            boundedExact,
            boundedFallback,
            new OutgoingReferenceEvidenceCoverage(
                exactRows.Count,
                exact.Count,
                boundedExact.Length,
                fallback.Count,
                boundedFallback.Length,
                exact.Count > query.ExactOffset + boundedExact.Length,
                fallback.Count > query.FallbackOffset + boundedFallback.Length),
            snapshot);
    }

    /// <summary>Read the current extractor artifact identity used by stateless reference continuations.</summary>
    public static ReferenceEvidenceSnapshot ReadSnapshot(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        using var connection = SqliteReadOnlyAccess.Open(dbPath);
        JulieSchemaGate.Verify(connection);
        return ReadSnapshot(connection);
    }

    public static ReferenceEvidenceSnapshot ReadSnapshot(IWorkspaceReadSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Read(connection =>
        {
            JulieSchemaGate.Verify(connection);
            return ReadSnapshot(connection);
        });
    }

    private static ReferenceEvidenceBundle ReadForSymbol(
        SqliteConnection connection,
        string symbolId,
        ReferenceEvidenceQuery inboundQuery,
        ReferenceEvidenceQuery outgoingQuery,
        ReferenceEvidenceBounds kindBounds,
        IReadOnlyList<ReferenceKind> kinds)
    {
        JulieSchemaGate.Verify(connection);
        RequireResolutionTables(connection);
        string targetName = ReadTargetName(connection, symbolId);
        List<ReferenceEvidence> inboundExactRows = ReadExact(connection, symbolId);
        List<ReferenceEvidence> inboundFallbackRows = ReadFallback(connection, symbolId, targetName);
        int sameNameDefinitionCount = CountDefinitions(connection, symbolId, targetName);
        List<OutgoingReferenceEvidence> outgoingExactRows = ReadOutgoingExact(connection, symbolId);
        List<OutgoingReferenceEvidence> outgoingFallbackRows = ReadOutgoingFallback(connection, symbolId);
        ReferenceEvidenceSnapshot snapshot = ReadSnapshot(connection);
        ReferenceKind[] distinctKinds = kinds.Distinct().ToArray();
        return new ReferenceEvidenceBundle(
            BuildInboundSet(inboundExactRows, inboundFallbackRows, inboundQuery, sameNameDefinitionCount, snapshot),
            BuildOutgoingSet(outgoingExactRows, outgoingFallbackRows, outgoingQuery, snapshot),
            distinctKinds.ToDictionary(
                static kind => kind,
                kind => BuildInboundSet(
                    inboundExactRows,
                    inboundFallbackRows,
                    new ReferenceEvidenceQuery(kindBounds, kind),
                    sameNameDefinitionCount,
                    snapshot)),
            distinctKinds.ToDictionary(
                static kind => kind,
                kind => BuildOutgoingSet(
                    outgoingExactRows,
                    outgoingFallbackRows,
                    new ReferenceEvidenceQuery(kindBounds, kind),
                    snapshot)));
    }

    private static ReferenceEvidenceSet ReadInbound(
        SqliteConnection connection,
        string targetSymbolId,
        ReferenceEvidenceQuery query)
    {
        JulieSchemaGate.Verify(connection);
        RequireResolutionTables(connection);
        string targetName = ReadTargetName(connection, targetSymbolId);
        List<ReferenceEvidence> exactRows = ReadExact(connection, targetSymbolId);
        List<ReferenceEvidence> fallbackRows = ReadFallback(connection, targetSymbolId, targetName);
        int sameNameDefinitionCount = CountDefinitions(connection, targetSymbolId, targetName);
        return BuildInboundSet(
            exactRows,
            fallbackRows,
            query,
            sameNameDefinitionCount,
            ReadSnapshot(connection));
    }

    private static OutgoingReferenceEvidenceSet ReadOutgoing(
        SqliteConnection connection,
        string containingSymbolId,
        ReferenceEvidenceQuery query)
    {
        JulieSchemaGate.Verify(connection);
        RequireResolutionTables(connection);
        RequireSymbol(connection, containingSymbolId);
        return BuildOutgoingSet(
            ReadOutgoingExact(connection, containingSymbolId),
            ReadOutgoingFallback(connection, containingSymbolId),
            query,
            ReadSnapshot(connection));
    }

    private static ReferenceEvidenceSnapshot ReadSnapshot(SqliteConnection connection)
    {
        using var artifact = connection.CreateCommand();
        artifact.CommandText =
            "SELECT value FROM artifact_metadata WHERE key = 'artifact_id' LIMIT 1;";
        string artifactId = artifact.ExecuteScalar() as string
            ?? throw new IncompatibleExtractException(
                "Reference evidence requires artifact_metadata.artifact_id.");

        using var revision = connection.CreateCommand();
        revision.CommandText = "SELECT COALESCE(MAX(revision_id), 0) FROM extraction_revisions;";
        long revisionId = Convert.ToInt64(
            revision.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);
        return new ReferenceEvidenceSnapshot(artifactId, revisionId);
    }

    private static string ReadTargetName(SqliteConnection connection, string targetSymbolId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM symbols WHERE symbol_id = $target;";
        command.Parameters.AddWithValue("$target", targetSymbolId);
        return command.ExecuteScalar() as string
            ?? throw new ArgumentException($"Unknown symbol ID '{targetSymbolId}'.", nameof(targetSymbolId));
    }

    private static void RequireSymbol(SqliteConnection connection, string symbolId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM symbols WHERE symbol_id = $symbol;";
        command.Parameters.AddWithValue("$symbol", symbolId);
        if (command.ExecuteScalar() is null)
            throw new ArgumentException($"Unknown symbol ID '{symbolId}'.", nameof(symbolId));
    }

    private static void RequireResolutionTables(SqliteConnection connection)
    {
        foreach (string table in RequiredResolutionTables)
        {
            if (!SqliteSchemaObjects.Exists(connection, table))
                throw new IncompatibleExtractException(
                    $"Reference evidence requires the '{table}' table. Restore the pinned julie-extract artifact.");
        }
    }

    private static int CountDefinitions(SqliteConnection connection, string targetSymbolId, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM symbols
            WHERE name = $name
              AND (symbol_id = $target OR kind NOT IN ('constructor', 'import'));
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$target", targetSymbolId);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static List<ReferenceEvidence> ReadExact(SqliteConnection connection, string targetSymbolId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.containing_symbol_id, s.path, s.start_line, s.start_column, s.end_line, s.end_column,
                   s.start_byte, s.end_byte, i.kind, COALESCE(ir.confidence, i.confidence),
                   'identifier_resolution' AS source, ir.tier, s.language,
                   s.reference_site_id, s.is_exact, s.provenance
            FROM identifier_resolutions ir
            JOIN identifiers i ON i.identifier_id = ir.identifier_id
            JOIN reference_sites s ON s.reference_site_id = i.reference_site_id
            WHERE ir.target_symbol_id = $target
            UNION ALL
            SELECT s.containing_symbol_id, s.path, s.start_line, s.start_column, s.end_line, s.end_column,
                   s.start_byte, s.end_byte, r.kind, r.confidence, 'relationship' AS source,
                   NULL AS tier, s.language, s.reference_site_id, s.is_exact, s.provenance
            FROM relationships r
            JOIN reference_sites s ON s.reference_site_id = r.reference_site_id
            WHERE r.to_symbol_id = $target
            UNION ALL
            SELECT s.containing_symbol_id, s.path, s.start_line, s.start_column, s.end_line, s.end_column,
                   s.start_byte, s.end_byte, p.kind, MIN(p.confidence, pr.confidence),
                   'pending_resolution' AS source, pr.tier, s.language,
                   s.reference_site_id, s.is_exact, s.provenance
            FROM pending_resolutions pr
            JOIN pending_relationships p ON p.pending_relationship_id = pr.pending_relationship_id
            JOIN reference_sites s ON s.reference_site_id = p.reference_site_id
            WHERE pr.target_symbol_id = $target
            ORDER BY 2, 7, 3, 9, 11, 14;
            """;
        command.Parameters.AddWithValue("$target", targetSymbolId);
        return ReadRows(command, targetSymbolId, ReferenceResolutionStatus.Exact);
    }

    private static List<ReferenceEvidence> ReadFallback(
        SqliteConnection connection,
        string targetSymbolId,
        string targetName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.containing_symbol_id, s.path, s.start_line, s.start_column, s.end_line, s.end_column,
                   s.start_byte, s.end_byte, i.kind, MIN(i.confidence, 0.5), 'name_fallback' AS source,
                   NULL AS tier, s.language, s.reference_site_id, s.is_exact, s.provenance
            FROM identifiers i
            JOIN reference_sites s ON s.reference_site_id = i.reference_site_id
            WHERE i.name = $name
              AND NOT EXISTS (
                  SELECT 1 FROM identifier_resolutions ir
                  WHERE ir.identifier_id = i.identifier_id
                    AND ir.target_symbol_id IS NOT NULL)
            ORDER BY s.path, s.start_byte, s.start_line, i.kind, s.reference_site_id;
            """;
        command.Parameters.AddWithValue("$name", targetName);
        return ReadRows(command, targetSymbolId, ReferenceResolutionStatus.Fallback);
    }

    private static List<OutgoingReferenceEvidence> ReadOutgoingExact(
        SqliteConnection connection,
        string containingSymbolId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ir.target_symbol_id, target.name, s.path,
                   s.start_line, s.start_column, s.end_line, s.end_column,
                   s.start_byte, s.end_byte, i.kind, COALESCE(ir.confidence, i.confidence),
                   'identifier_resolution' AS source, ir.tier, s.language,
                   s.reference_site_id, s.is_exact, s.provenance
            FROM identifier_resolutions ir
            JOIN identifiers i ON i.identifier_id = ir.identifier_id
            JOIN symbols target ON target.symbol_id = ir.target_symbol_id
            JOIN reference_sites s ON s.reference_site_id = i.reference_site_id
            WHERE i.containing_symbol_id = $containing
            UNION ALL
            SELECT r.to_symbol_id, target.name, s.path,
                   s.start_line, s.start_column, s.end_line, s.end_column,
                   s.start_byte, s.end_byte, r.kind, r.confidence,
                   'relationship' AS source, NULL AS tier, s.language,
                   s.reference_site_id, s.is_exact, s.provenance
            FROM relationships r
            JOIN symbols target ON target.symbol_id = r.to_symbol_id
            JOIN reference_sites s ON s.reference_site_id = r.reference_site_id
            WHERE r.from_symbol_id = $containing
            UNION ALL
            SELECT pr.target_symbol_id, target.name, s.path,
                   s.start_line, s.start_column, s.end_line, s.end_column,
                   s.start_byte, s.end_byte, p.kind, MIN(p.confidence, pr.confidence),
                   'pending_resolution' AS source, pr.tier, s.language,
                   s.reference_site_id, s.is_exact, s.provenance
            FROM pending_resolutions pr
            JOIN pending_relationships p ON p.pending_relationship_id = pr.pending_relationship_id
            JOIN symbols target ON target.symbol_id = pr.target_symbol_id
            JOIN reference_sites s ON s.reference_site_id = p.reference_site_id
            WHERE COALESCE(p.caller_scope_symbol_id, p.from_symbol_id) = $containing
            ORDER BY 3, 8, 4, 10, 12, 2, 1, 15;
            """;
        command.Parameters.AddWithValue("$containing", containingSymbolId);
        return ReadOutgoingRows(command, containingSymbolId, ReferenceResolutionStatus.Exact);
    }

    private static List<OutgoingReferenceEvidence> ReadOutgoingFallback(
        SqliteConnection connection,
        string containingSymbolId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT NULL AS target_symbol_id, i.name, s.path,
                   s.start_line, s.start_column, s.end_line, s.end_column,
                   s.start_byte, s.end_byte, i.kind, MIN(i.confidence, 0.5),
                   'name_fallback' AS source, NULL AS tier, s.language,
                   s.reference_site_id, s.is_exact, s.provenance
            FROM identifiers i
            JOIN reference_sites s ON s.reference_site_id = i.reference_site_id
            WHERE i.containing_symbol_id = $containing
              AND NOT EXISTS (
                  SELECT 1 FROM identifier_resolutions ir
                  WHERE ir.identifier_id = i.identifier_id
                    AND ir.target_symbol_id IS NOT NULL)
            UNION ALL
            SELECT NULL AS target_symbol_id, p.target_display_name, s.path,
                   s.start_line, s.start_column, s.end_line, s.end_column,
                   s.start_byte, s.end_byte, p.kind, MIN(p.confidence, 0.5),
                   'name_fallback' AS source, NULL AS tier, s.language,
                   s.reference_site_id, s.is_exact, s.provenance
            FROM pending_relationships p
            JOIN reference_sites s ON s.reference_site_id = p.reference_site_id
            WHERE COALESCE(p.caller_scope_symbol_id, p.from_symbol_id) = $containing
              AND NOT EXISTS (
                  SELECT 1 FROM pending_resolutions pr
                  WHERE pr.pending_relationship_id = p.pending_relationship_id)
            ORDER BY 3, 8, 4, 10, 2, 15;
            """;
        command.Parameters.AddWithValue("$containing", containingSymbolId);
        return ReadOutgoingRows(command, containingSymbolId, ReferenceResolutionStatus.Fallback);
    }

    private static List<ReferenceEvidence> Deduplicate(IEnumerable<ReferenceEvidence> rows) =>
        WithoutRedundantSpanlessRows(
            rows.GroupBy(SiteKey)
                .Select(group => group
                    .OrderBy(row => SourcePrecedence(row.Source))
                    .ThenByDescending(row => row.Confidence)
                    .ThenBy(row => row.ContainingSymbolId, StringComparer.Ordinal)
                    .First()),
            row => row.IsExact,
            row => new SpanlessCoverageKey(row.FilePath, row.ContainingSymbolId, row.TargetSymbolId, row.Kind))
            .OrderBy(row => row.FilePath, StringComparer.Ordinal)
            .ThenBy(row => row.StartByte ?? long.MaxValue)
            .ThenBy(row => row.StartLine ?? int.MaxValue)
            .ThenBy(row => row.StartColumn ?? int.MaxValue)
            .ThenBy(row => row.ContainingSymbolId, StringComparer.Ordinal)
            .ThenBy(row => row.Kind)
            .ToList();

    private static List<ReferenceEvidence> FilterKind(
        List<ReferenceEvidence> rows,
        ReferenceKind? kind) =>
        kind is null ? rows : rows.Where(row => row.Kind == kind.Value).ToList();

    private static IReadOnlyList<string> ExactContainingSymbolIds(
        IReadOnlyList<ReferenceEvidence> rows,
        bool callLike) =>
        rows.Where(row =>
                row.ContainingSymbolId is not null &&
                IsCallLike(row.Kind) == callLike)
            .Select(row => row.ContainingSymbolId!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(symbolId => symbolId, StringComparer.Ordinal)
            .ToArray();

    private static bool IsCallLike(ReferenceKind kind) =>
        kind is ReferenceKind.Call or ReferenceKind.Instantiation;

    private static List<OutgoingReferenceEvidence> DeduplicateOutgoing(
        IEnumerable<OutgoingReferenceEvidence> rows) =>
        WithoutRedundantSpanlessRows(
            rows.GroupBy(OutgoingSiteKey)
                .Select(group => group
                    .OrderBy(row => SourcePrecedence(row.Source))
                    .ThenByDescending(row => row.Confidence)
                    .ThenBy(row => row.TargetSymbolId, StringComparer.Ordinal)
                    .ThenBy(row => row.TargetName, StringComparer.Ordinal)
                    .First()),
            row => row.IsExact,
            row => new SpanlessCoverageKey(row.FilePath, row.ContainingSymbolId, row.TargetSymbolId, row.Kind))
            .OrderBy(row => row.FilePath, StringComparer.Ordinal)
            .ThenBy(row => row.StartByte ?? long.MaxValue)
            .ThenBy(row => row.StartLine ?? int.MaxValue)
            .ThenBy(row => row.StartColumn ?? int.MaxValue)
            .ThenBy(row => row.Kind)
            .ThenBy(row => row.TargetName, StringComparer.Ordinal)
            .ThenBy(row => row.TargetSymbolId, StringComparer.Ordinal)
            .ToList();

    private static List<OutgoingReferenceEvidence> FilterOutgoingKind(
        List<OutgoingReferenceEvidence> rows,
        ReferenceKind? kind) =>
        kind is null ? rows : rows.Where(row => row.Kind == kind.Value).ToList();

    /// <summary>
    /// Drops spanless rows that only restate a binding a spanned row already covers, after site-identity
    /// deduplication has run. Available counts and the rows themselves are one logical reference per occurrence;
    /// <c>ExactObserved</c> keeps the raw row total.
    /// </summary>
    /// <remarks>
    /// julie-extract emits a schema-5 spanless <c>pending_resolutions</c> row alongside the spanned identifier
    /// row for the same occurrence, under its own <c>reference_site_spanless-</c> identity, so site-identity
    /// deduplication cannot collapse the pair and every consumer counted one call twice. A spanless row with no
    /// spanned row at the same file, containing symbol, target, and kind is the only evidence that occurrence
    /// has, so it is kept and still reported as spanless.
    /// </remarks>
    private static IEnumerable<T> WithoutRedundantSpanlessRows<T>(
        IEnumerable<T> deduplicated,
        Func<T, bool> isSpanned,
        Func<T, SpanlessCoverageKey> coverageKey)
    {
        var rows = deduplicated as IReadOnlyList<T> ?? deduplicated.ToArray();
        var covered = rows.Where(isSpanned).Select(coverageKey).ToHashSet();
        return rows.Where(row => isSpanned(row) || !covered.Contains(coverageKey(row)));
    }

    private static ReferenceSiteKey SiteKey(ReferenceEvidence row) => new(row.ReferenceSiteId, row.TargetSymbolId, row.Kind);

    private static OutgoingReferenceSiteKey OutgoingSiteKey(OutgoingReferenceEvidence row) => new(row.ReferenceSiteId, row.TargetSymbolId, row.TargetName, row.Kind);

    private static int SourcePrecedence(ReferenceEvidenceSource source) => source switch
    {
        ReferenceEvidenceSource.IdentifierDirect => 0,
        ReferenceEvidenceSource.IdentifierResolution => 1,
        ReferenceEvidenceSource.Relationship => 2,
        ReferenceEvidenceSource.PendingResolution => 3,
        ReferenceEvidenceSource.NameFallback => 4,
        _ => int.MaxValue,
    };

    private static List<ReferenceEvidence> ReadRows(
        SqliteCommand command,
        string targetSymbolId,
        ReferenceResolutionStatus resolutionStatus)
    {
        using var reader = command.ExecuteReader();
        var rows = new List<ReferenceEvidence>();
        while (reader.Read())
        {
            string sourceKind = reader.GetString(8);
            string source = reader.GetString(10);
            rows.Add(new ReferenceEvidence(
                resolutionStatus == ReferenceResolutionStatus.Exact ? targetSymbolId : null,
                ReadString(reader, 0),
                reader.GetString(1),
                ReadInt32(reader, 2),
                ReadInt32(reader, 3),
                ReadInt32(reader, 4),
                ReadInt32(reader, 5),
                ReadInt64(reader, 6),
                ReadInt64(reader, 7),
                NormalizeKind(sourceKind),
                sourceKind,
                ParseSource(source),
                ReadInt32(reader, 11),
                reader.GetDouble(9),
                resolutionStatus,
                ReadString(reader, 12),
                reader.GetString(13),
                reader.GetInt64(14) == 1,
                reader.GetString(15)));
        }

        return rows;
    }

    private static List<OutgoingReferenceEvidence> ReadOutgoingRows(
        SqliteCommand command,
        string containingSymbolId,
        ReferenceResolutionStatus resolutionStatus)
    {
        using var reader = command.ExecuteReader();
        var rows = new List<OutgoingReferenceEvidence>();
        while (reader.Read())
        {
            string sourceKind = reader.GetString(9);
            rows.Add(new OutgoingReferenceEvidence(
                containingSymbolId,
                ReadString(reader, 0),
                reader.GetString(1),
                reader.GetString(2),
                ReadInt32(reader, 3),
                ReadInt32(reader, 4),
                ReadInt32(reader, 5),
                ReadInt32(reader, 6),
                ReadInt64(reader, 7),
                ReadInt64(reader, 8),
                NormalizeKind(sourceKind),
                sourceKind,
                ParseSource(reader.GetString(11)),
                ReadInt32(reader, 12),
                reader.GetDouble(10),
                resolutionStatus,
                ReadString(reader, 13),
                reader.GetString(14),
                reader.GetInt64(15) == 1,
                reader.GetString(16)));
        }

        return rows;
    }

    private static ReferenceEvidenceSource ParseSource(string source) => source switch
    {
        "identifier_direct" => ReferenceEvidenceSource.IdentifierDirect,
        "identifier_resolution" => ReferenceEvidenceSource.IdentifierResolution,
        "relationship" => ReferenceEvidenceSource.Relationship,
        "pending_resolution" => ReferenceEvidenceSource.PendingResolution,
        "name_fallback" => ReferenceEvidenceSource.NameFallback,
        _ => throw new InvalidOperationException($"Unknown reference evidence source '{source}'."),
    };

    public static ReferenceKind NormalizeKind(string kind) => kind switch
    {
        "call" or "calls" => ReferenceKind.Call,
        "type_usage" => ReferenceKind.TypeUsage,
        "member_access" => ReferenceKind.MemberAccess,
        "variable_ref" => ReferenceKind.VariableReference,
        "instantiates" => ReferenceKind.Instantiation,
        "extends" => ReferenceKind.Inheritance,
        "implements" => ReferenceKind.Implementation,
        "import" or "imports" => ReferenceKind.Import,
        "references" => ReferenceKind.Reference,
        "uses" => ReferenceKind.Usage,
        _ => ReferenceKind.Unknown,
    };

    private static string? ReadString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? ReadInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static long? ReadInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private readonly record struct SpanlessCoverageKey(
        string FilePath,
        string? ContainingSymbolId,
        string? TargetSymbolId,
        ReferenceKind Kind);

    private readonly record struct ReferenceSiteKey(
        string ReferenceSiteId,
        string? TargetSymbolId,
        ReferenceKind Kind);

    private readonly record struct OutgoingReferenceSiteKey(
        string ReferenceSiteId,
        string? TargetSymbolId,
        string TargetName,
        ReferenceKind Kind);
}
