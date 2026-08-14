using Microsoft.Data.Sqlite;
using Miller.Core.References;

namespace Miller.Indexing;

public static partial class ReferenceEvidenceReader
{
    private static bool IsFamilyStoreResolutionProjection(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM temp.sqlite_schema WHERE name='_miller_session')
               AND EXISTS(SELECT 1 FROM pragma_database_list WHERE name='resolution_base');
            """;
        return Convert.ToInt64(
            command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static ReferenceEvidenceBundle ReadForSymbolFromFamilyStore(
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
        List<ReferenceEvidence> inboundExactRows = ReadFamilyStoreExact(connection, symbolId);
        List<ReferenceEvidence> inboundFallbackRows = ReadFamilyStoreFallback(connection, symbolId, targetName);
        int sameNameDefinitionCount = CountDefinitions(connection, symbolId, targetName);
        List<OutgoingReferenceEvidence> outgoingExactRows = ReadFamilyStoreOutgoingExact(connection, symbolId);
        List<OutgoingReferenceEvidence> outgoingFallbackRows = ReadFamilyStoreOutgoingFallback(connection, symbolId);
        ReferenceEvidenceSnapshot snapshot = ReadSnapshot(connection);
        ReferenceKind[] distinctKinds = kinds.Distinct().ToArray();

        return new ReferenceEvidenceBundle(
            BuildInboundSet(
                inboundExactRows,
                inboundFallbackRows,
                inboundQuery,
                sameNameDefinitionCount,
                snapshot),
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

    private static ReferenceEvidenceSet ReadInboundFromFamilyStore(
        SqliteConnection connection,
        string targetSymbolId,
        ReferenceEvidenceQuery query)
    {
        JulieSchemaGate.Verify(connection);
        RequireResolutionTables(connection);
        string targetName = ReadTargetName(connection, targetSymbolId);
        return BuildInboundSet(
            ReadFamilyStoreExact(connection, targetSymbolId),
            ReadFamilyStoreFallback(connection, targetSymbolId, targetName),
            query,
            CountDefinitions(connection, targetSymbolId, targetName),
            ReadSnapshot(connection));
    }

    private static OutgoingReferenceEvidenceSet ReadOutgoingFromFamilyStore(
        SqliteConnection connection,
        string containingSymbolId,
        ReferenceEvidenceQuery query)
    {
        JulieSchemaGate.Verify(connection);
        RequireResolutionTables(connection);
        RequireSymbol(connection, containingSymbolId);
        return BuildOutgoingSet(
            ReadFamilyStoreOutgoingExact(connection, containingSymbolId),
            ReadFamilyStoreOutgoingFallback(connection, containingSymbolId),
            query,
            ReadSnapshot(connection));
    }

    private static IReadOnlyDictionary<string, ReferenceEvidenceBundle> ReadManyFromFamilyStore(
        SqliteConnection connection,
        IReadOnlyList<string> orderedIds,
        ReferenceEvidenceQuery query,
        ReferenceEvidenceObservationOptions? observationOptions)
    {
        JulieSchemaGate.Verify(connection);
        RequireResolutionTables(connection);
        var targetInfo = new Dictionary<string, ReferenceEvidenceTargetInfo>(
            orderedIds.Count,
            StringComparer.Ordinal);
        foreach (IReadOnlyList<string> chunk in Chunk(orderedIds))
            MergeTargetInfo(targetInfo, ReadFamilyStoreTargetInfoMany(connection, chunk, observationOptions));
        EnsureAllTargetsKnown(orderedIds, targetInfo);

        var inboundExact = new Dictionary<string, List<ReferenceEvidence>>(StringComparer.Ordinal);
        var inboundFallback = new Dictionary<string, List<ReferenceEvidence>>(StringComparer.Ordinal);
        var outgoingExact = new Dictionary<string, List<OutgoingReferenceEvidence>>(StringComparer.Ordinal);
        var outgoingFallback = new Dictionary<string, List<OutgoingReferenceEvidence>>(StringComparer.Ordinal);
        foreach (IReadOnlyList<string> chunk in Chunk(orderedIds))
        {
            MergeRows(inboundExact, ReadFamilyStoreExactMany(connection, chunk, observationOptions));
            MergeRows(inboundFallback, ReadFamilyStoreFallbackMany(connection, chunk, observationOptions));
            MergeRows(outgoingExact, ReadFamilyStoreOutgoingExactMany(connection, chunk, observationOptions));
            MergeRows(outgoingFallback, ReadFamilyStoreOutgoingFallbackMany(connection, chunk, observationOptions));
        }

        ReferenceEvidenceSnapshot snapshot = ReadSnapshot(connection);
        var result = new Dictionary<string, ReferenceEvidenceBundle>(orderedIds.Count, StringComparer.Ordinal);
        foreach (string symbolId in orderedIds)
        {
            ReferenceEvidenceTargetInfo target = targetInfo[symbolId];
            result.Add(
                symbolId,
                new ReferenceEvidenceBundle(
                    BuildInboundSet(
                        GetRows(inboundExact, symbolId),
                        GetRows(inboundFallback, symbolId),
                        query,
                        target.SameNameDefinitionCount,
                        snapshot),
                    BuildOutgoingSet(
                        GetRows(outgoingExact, symbolId),
                        GetRows(outgoingFallback, symbolId),
                        query,
                        snapshot),
                    new Dictionary<ReferenceKind, ReferenceEvidenceSet>(),
                    new Dictionary<ReferenceKind, OutgoingReferenceEvidenceSet>()));
        }

        return result;
    }

    private static Dictionary<string, ReferenceEvidenceTargetInfo> ReadFamilyStoreTargetInfoMany(
        SqliteConnection connection,
        IReadOnlyList<string> symbolIds,
        ReferenceEvidenceObservationOptions? observationOptions)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            WITH requested(symbol_id) AS ({ValuesRelation(symbolIds)}),
            targets AS MATERIALIZED (
              SELECT target.symbol_id,target.name
              FROM main.symbols AS target
              JOIN requested AS q ON q.symbol_id=target.symbol_id
              JOIN _miller_visible_entries AS e ON e.version_id=target.version_id
            )
            SELECT target.symbol_id,
                   target.name,
                   (
                     SELECT COUNT(*)
                     FROM main.symbols AS same
                     JOIN _miller_visible_entries AS e ON e.version_id=same.version_id
                     WHERE same.name=target.name
                       AND (same.symbol_id=target.symbol_id OR same.kind NOT IN ('constructor','import'))
                   ) AS same_name_definition_count
            FROM targets AS target;
            """;
        AddParameters(command, symbolIds);
        return ExecuteObserved(
            command,
            ReferenceEvidenceReadPhase.TargetInfo,
            symbolIds.Count,
            observationOptions,
            reader =>
            {
                var result = new Dictionary<string, ReferenceEvidenceTargetInfo>(StringComparer.Ordinal);
                int rawRowCount = 0;
                while (reader.Read())
                {
                    rawRowCount++;
                    result.Add(
                        reader.GetString(0),
                        new ReferenceEvidenceTargetInfo(reader.GetString(1), reader.GetInt32(2)));
                }

                return (result, rawRowCount);
            });
    }

    private static Dictionary<string, List<ReferenceEvidence>> ReadFamilyStoreExactMany(
        SqliteConnection connection,
        IReadOnlyList<string> symbolIds,
        ReferenceEvidenceObservationOptions? observationOptions)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            WITH requested(symbol_id) AS ({ValuesRelation(symbolIds)}),
            targets AS MATERIALIZED (
              SELECT s.symbol_id,s.version_id
              FROM main.symbols AS s
              JOIN requested AS q ON q.symbol_id=s.symbol_id
              JOIN _miller_visible_entries AS e ON e.version_id=s.version_id
            ),
            identifier_resolution AS MATERIALIZED (
              SELECT b.version_id,b.identifier_id,b.target_version_id,b.target_symbol_id,
                     b.tier,b.confidence
              FROM targets AS t
              CROSS JOIN resolution_base.identifier_resolutions AS b
                ON b.target_version_id=t.version_id AND b.target_symbol_id=t.symbol_id
              WHERE NOT EXISTS (
                SELECT 1 FROM main.resolution_identifier_deltas AS d
                WHERE d.view_id=(SELECT view_id FROM _miller_session)
                  AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                  AND d.version_id=b.version_id AND d.identifier_id=b.identifier_id)
              UNION ALL
              SELECT d.version_id,d.identifier_id,d.target_version_id,d.target_symbol_id,
                     d.tier,d.confidence
              FROM targets AS t
              JOIN main.resolution_identifier_deltas AS d
                ON d.target_version_id=t.version_id AND d.target_symbol_id=t.symbol_id
              WHERE d.view_id=(SELECT view_id FROM _miller_session)
                AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
            ),
            pending_resolution AS MATERIALIZED (
              SELECT b.version_id,b.pending_relationship_id,b.target_version_id,b.target_symbol_id,
                     b.tier,b.confidence
              FROM targets AS t
              CROSS JOIN resolution_base.pending_resolutions AS b
                ON b.target_version_id=t.version_id AND b.target_symbol_id=t.symbol_id
              WHERE NOT EXISTS (
                SELECT 1 FROM main.resolution_pending_deltas AS d
                WHERE d.view_id=(SELECT view_id FROM _miller_session)
                  AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                  AND d.version_id=b.version_id
                  AND d.pending_relationship_id=b.pending_relationship_id)
              UNION ALL
              SELECT d.version_id,d.pending_relationship_id,d.target_version_id,d.target_symbol_id,
                     d.tier,d.confidence
              FROM targets AS t
              JOIN main.resolution_pending_deltas AS d
                ON d.target_version_id=t.version_id AND d.target_symbol_id=t.symbol_id
              WHERE d.view_id=(SELECT view_id FROM _miller_session)
                AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                AND d.operation='replace'
            )
            SELECT ir.target_symbol_id AS requested_symbol_id,
                   s.containing_symbol_id,s.path,s.start_line,s.start_column,s.end_line,s.end_column,
                   s.start_byte,s.end_byte,i.kind,COALESCE(ir.confidence,i.confidence),
                   'identifier_resolution' AS source,ir.tier,s.language,
                   s.reference_site_id,s.is_exact,s.provenance
            FROM identifier_resolution AS ir
            CROSS JOIN main.identifiers AS i
              ON i.version_id=ir.version_id AND i.identifier_id=ir.identifier_id
            JOIN main.reference_sites AS s
              ON s.version_id=i.version_id AND s.reference_site_id=i.reference_site_id
            JOIN _miller_visible_entries AS e ON e.version_id=i.version_id
            UNION ALL
            SELECT t.symbol_id AS requested_symbol_id,
                   s.containing_symbol_id,s.path,s.start_line,s.start_column,s.end_line,s.end_column,
                   s.start_byte,s.end_byte,r.kind,r.confidence,'relationship' AS source,
                   NULL AS tier,s.language,s.reference_site_id,s.is_exact,s.provenance
            FROM targets AS t
            JOIN main.relationships AS r ON r.version_id=t.version_id AND r.to_symbol_id=t.symbol_id
            JOIN main.reference_sites AS s
              ON s.version_id=r.version_id AND s.reference_site_id=r.reference_site_id
            JOIN _miller_visible_entries AS e ON e.version_id=r.version_id
            UNION ALL
            SELECT pr.target_symbol_id AS requested_symbol_id,
                   s.containing_symbol_id,s.path,s.start_line,s.start_column,s.end_line,s.end_column,
                   s.start_byte,s.end_byte,p.kind,MIN(p.confidence,pr.confidence),
                   'pending_resolution' AS source,pr.tier,s.language,
                   s.reference_site_id,s.is_exact,s.provenance
            FROM pending_resolution AS pr
            CROSS JOIN main.pending_relationships AS p
              ON p.version_id=pr.version_id
             AND p.pending_relationship_id=pr.pending_relationship_id
            JOIN main.reference_sites AS s
              ON s.version_id=p.version_id AND s.reference_site_id=p.reference_site_id
            JOIN _miller_visible_entries AS e ON e.version_id=p.version_id
            ORDER BY 3,8,4,10,12,15;
            """;
        AddParameters(command, symbolIds);
        return ReadRowsBySymbol(
            command,
            ReferenceResolutionStatus.Exact,
            ReferenceEvidenceReadPhase.InboundExact,
            symbolIds.Count,
            observationOptions);
    }

    private static Dictionary<string, List<ReferenceEvidence>> ReadFamilyStoreFallbackMany(
        SqliteConnection connection,
        IReadOnlyList<string> symbolIds,
        ReferenceEvidenceObservationOptions? observationOptions)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            WITH requested(symbol_id) AS ({ValuesRelation(symbolIds)}),
            targets AS MATERIALIZED (
              SELECT target.symbol_id,target.name
              FROM main.symbols AS target
              JOIN requested AS q ON q.symbol_id=target.symbol_id
              JOIN _miller_visible_entries AS e ON e.version_id=target.version_id
            )
            SELECT target.symbol_id AS requested_symbol_id,
                   s.containing_symbol_id,s.path,s.start_line,s.start_column,s.end_line,s.end_column,
                   s.start_byte,s.end_byte,i.kind,MIN(i.confidence,0.5),'name_fallback' AS source,
                   NULL AS tier,s.language,s.reference_site_id,s.is_exact,s.provenance
            FROM targets AS target
            CROSS JOIN main.identifiers AS i ON i.name=target.name
            JOIN main.reference_sites AS s
              ON s.version_id=i.version_id AND s.reference_site_id=i.reference_site_id
            JOIN _miller_visible_entries AS e ON e.version_id=i.version_id
            WHERE NOT EXISTS (
                SELECT 1 FROM main.resolution_identifier_deltas AS d
                WHERE d.view_id=(SELECT view_id FROM _miller_session)
                  AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                  AND d.version_id=i.version_id AND d.identifier_id=i.identifier_id
                  AND d.target_symbol_id IS NOT NULL)
              AND NOT EXISTS (
                SELECT 1 FROM resolution_base.identifier_resolutions AS b
                WHERE b.version_id=i.version_id AND b.identifier_id=i.identifier_id
                  AND b.target_symbol_id IS NOT NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM main.resolution_identifier_deltas AS d
                    WHERE d.view_id=(SELECT view_id FROM _miller_session)
                      AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                      AND d.version_id=i.version_id AND d.identifier_id=i.identifier_id))
            ORDER BY 3,8,4,10,15;
            """;
        AddParameters(command, symbolIds);
        return ReadRowsBySymbol(
            command,
            ReferenceResolutionStatus.Fallback,
            ReferenceEvidenceReadPhase.InboundFallback,
            symbolIds.Count,
            observationOptions);
    }

    private static Dictionary<string, List<OutgoingReferenceEvidence>> ReadFamilyStoreOutgoingExactMany(
        SqliteConnection connection,
        IReadOnlyList<string> symbolIds,
        ReferenceEvidenceObservationOptions? observationOptions)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            WITH requested(symbol_id) AS ({ValuesRelation(symbolIds)}),
            candidate_identifiers AS MATERIALIZED (
              SELECT i.*
              FROM main.identifiers AS i
              JOIN requested AS q ON q.symbol_id=i.containing_symbol_id
              JOIN _miller_visible_entries AS e ON e.version_id=i.version_id
            ),
            identifier_resolution AS MATERIALIZED (
              SELECT b.version_id,b.identifier_id,b.target_version_id,b.target_symbol_id,
                     b.tier,b.confidence
              FROM candidate_identifiers AS i
              JOIN resolution_base.identifier_resolutions AS b
                ON b.version_id=i.version_id AND b.identifier_id=i.identifier_id
              WHERE NOT EXISTS (
                SELECT 1 FROM main.resolution_identifier_deltas AS d
                WHERE d.view_id=(SELECT view_id FROM _miller_session)
                  AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                  AND d.version_id=b.version_id AND d.identifier_id=b.identifier_id)
                AND b.target_symbol_id IS NOT NULL
              UNION ALL
              SELECT d.version_id,d.identifier_id,d.target_version_id,d.target_symbol_id,
                     d.tier,d.confidence
              FROM candidate_identifiers AS i
              JOIN main.resolution_identifier_deltas AS d
                ON d.version_id=i.version_id AND d.identifier_id=i.identifier_id
              WHERE d.view_id=(SELECT view_id FROM _miller_session)
                AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                AND d.target_symbol_id IS NOT NULL
            ),
            candidate_pending AS MATERIALIZED (
              SELECT p.*
              FROM main.pending_relationships AS p
              JOIN requested AS q ON q.symbol_id=COALESCE(p.caller_scope_symbol_id,p.from_symbol_id)
              JOIN _miller_visible_entries AS e ON e.version_id=p.version_id
            ),
            pending_resolution AS MATERIALIZED (
              SELECT b.version_id,b.pending_relationship_id,b.target_version_id,b.target_symbol_id,
                     b.tier,b.confidence
              FROM candidate_pending AS p
              JOIN resolution_base.pending_resolutions AS b
                ON b.version_id=p.version_id
               AND b.pending_relationship_id=p.pending_relationship_id
              WHERE NOT EXISTS (
                SELECT 1 FROM main.resolution_pending_deltas AS d
                WHERE d.view_id=(SELECT view_id FROM _miller_session)
                  AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                  AND d.version_id=b.version_id
                  AND d.pending_relationship_id=b.pending_relationship_id)
              UNION ALL
              SELECT d.version_id,d.pending_relationship_id,d.target_version_id,d.target_symbol_id,
                     d.tier,d.confidence
              FROM candidate_pending AS p
              JOIN main.resolution_pending_deltas AS d
                ON d.version_id=p.version_id
               AND d.pending_relationship_id=p.pending_relationship_id
              WHERE d.view_id=(SELECT view_id FROM _miller_session)
                AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                AND d.operation='replace'
            )
            SELECT i.containing_symbol_id AS requested_symbol_id,
                   ir.target_symbol_id,target.name,s.path,
                   s.start_line,s.start_column,s.end_line,s.end_column,
                   s.start_byte,s.end_byte,i.kind,COALESCE(ir.confidence,i.confidence),
                   'identifier_resolution' AS source,ir.tier,s.language,
                   s.reference_site_id,s.is_exact,s.provenance
            FROM identifier_resolution AS ir
            JOIN candidate_identifiers AS i
              ON i.version_id=ir.version_id AND i.identifier_id=ir.identifier_id
            JOIN main.symbols AS target
              ON target.version_id=ir.target_version_id AND target.symbol_id=ir.target_symbol_id
            JOIN main.reference_sites AS s
              ON s.version_id=i.version_id AND s.reference_site_id=i.reference_site_id
            JOIN _miller_visible_entries AS target_entry ON target_entry.version_id=target.version_id
            UNION ALL
            SELECT r.from_symbol_id AS requested_symbol_id,
                   r.to_symbol_id,target.name,s.path,
                   s.start_line,s.start_column,s.end_line,s.end_column,
                   s.start_byte,s.end_byte,r.kind,r.confidence,'relationship' AS source,
                   NULL AS tier,s.language,s.reference_site_id,s.is_exact,s.provenance
            FROM main.relationships AS r
            JOIN requested AS q ON q.symbol_id=r.from_symbol_id
            JOIN _miller_visible_entries AS e ON e.version_id=r.version_id
            JOIN main.symbols AS target
              ON target.version_id=r.version_id AND target.symbol_id=r.to_symbol_id
            JOIN main.reference_sites AS s
              ON s.version_id=r.version_id AND s.reference_site_id=r.reference_site_id
            UNION ALL
            SELECT COALESCE(p.caller_scope_symbol_id,p.from_symbol_id) AS requested_symbol_id,
                   pr.target_symbol_id,target.name,s.path,
                   s.start_line,s.start_column,s.end_line,s.end_column,
                   s.start_byte,s.end_byte,p.kind,MIN(p.confidence,pr.confidence),
                   'pending_resolution' AS source,pr.tier,s.language,
                   s.reference_site_id,s.is_exact,s.provenance
            FROM pending_resolution AS pr
            JOIN candidate_pending AS p
              ON p.version_id=pr.version_id
             AND p.pending_relationship_id=pr.pending_relationship_id
            JOIN main.symbols AS target
              ON target.version_id=pr.target_version_id AND target.symbol_id=pr.target_symbol_id
            JOIN main.reference_sites AS s
              ON s.version_id=p.version_id AND s.reference_site_id=p.reference_site_id
            JOIN _miller_visible_entries AS target_entry ON target_entry.version_id=target.version_id
            ORDER BY 4,9,5,11,13,3,2,16;
            """;
        AddParameters(command, symbolIds);
        return ReadOutgoingRowsBySymbol(
            command,
            ReferenceResolutionStatus.Exact,
            ReferenceEvidenceReadPhase.OutgoingExact,
            symbolIds.Count,
            observationOptions);
    }

    private static Dictionary<string, List<OutgoingReferenceEvidence>> ReadFamilyStoreOutgoingFallbackMany(
        SqliteConnection connection,
        IReadOnlyList<string> symbolIds,
        ReferenceEvidenceObservationOptions? observationOptions)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            WITH requested(symbol_id) AS ({ValuesRelation(symbolIds)})
            SELECT i.containing_symbol_id AS requested_symbol_id,
                   NULL AS target_symbol_id,i.name,s.path,
                   s.start_line,s.start_column,s.end_line,s.end_column,
                   s.start_byte,s.end_byte,i.kind,MIN(i.confidence,0.5),
                   'name_fallback' AS source,NULL AS tier,s.language,
                   s.reference_site_id,s.is_exact,s.provenance
            FROM main.identifiers AS i
            JOIN requested AS q ON q.symbol_id=i.containing_symbol_id
            JOIN main.reference_sites AS s
              ON s.version_id=i.version_id AND s.reference_site_id=i.reference_site_id
            JOIN _miller_visible_entries AS e ON e.version_id=i.version_id
            WHERE NOT EXISTS (
                SELECT 1 FROM main.resolution_identifier_deltas AS d
                WHERE d.view_id=(SELECT view_id FROM _miller_session)
                  AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                  AND d.version_id=i.version_id AND d.identifier_id=i.identifier_id
                  AND d.target_symbol_id IS NOT NULL)
              AND NOT EXISTS (
                SELECT 1 FROM resolution_base.identifier_resolutions AS b
                WHERE b.version_id=i.version_id AND b.identifier_id=i.identifier_id
                  AND b.target_symbol_id IS NOT NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM main.resolution_identifier_deltas AS d
                    WHERE d.view_id=(SELECT view_id FROM _miller_session)
                      AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                      AND d.version_id=i.version_id AND d.identifier_id=i.identifier_id))
            UNION ALL
            SELECT COALESCE(p.caller_scope_symbol_id,p.from_symbol_id) AS requested_symbol_id,
                   NULL AS target_symbol_id,p.target_display_name,s.path,
                   s.start_line,s.start_column,s.end_line,s.end_column,
                   s.start_byte,s.end_byte,p.kind,MIN(p.confidence,0.5),
                   'name_fallback' AS source,NULL AS tier,s.language,
                   s.reference_site_id,s.is_exact,s.provenance
            FROM main.pending_relationships AS p
            JOIN requested AS q ON q.symbol_id=COALESCE(p.caller_scope_symbol_id,p.from_symbol_id)
            JOIN main.reference_sites AS s
              ON s.version_id=p.version_id AND s.reference_site_id=p.reference_site_id
            JOIN _miller_visible_entries AS e ON e.version_id=p.version_id
            WHERE NOT EXISTS (
                SELECT 1 FROM main.resolution_pending_deltas AS d
                WHERE d.view_id=(SELECT view_id FROM _miller_session)
                  AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                  AND d.version_id=p.version_id
                  AND d.pending_relationship_id=p.pending_relationship_id
                  AND d.operation='replace')
              AND NOT EXISTS (
                SELECT 1 FROM resolution_base.pending_resolutions AS b
                WHERE b.version_id=p.version_id
                  AND b.pending_relationship_id=p.pending_relationship_id
                  AND NOT EXISTS (
                    SELECT 1 FROM main.resolution_pending_deltas AS d
                    WHERE d.view_id=(SELECT view_id FROM _miller_session)
                      AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                      AND d.version_id=p.version_id
                      AND d.pending_relationship_id=p.pending_relationship_id))
            ORDER BY 4,9,5,11,3,16;
            """;
        AddParameters(command, symbolIds);
        return ReadOutgoingRowsBySymbol(
            command,
            ReferenceResolutionStatus.Fallback,
            ReferenceEvidenceReadPhase.OutgoingFallback,
            symbolIds.Count,
            observationOptions);
    }

    private static List<ReferenceEvidence> ReadFamilyStoreExact(
        SqliteConnection connection,
        string targetSymbolId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            WITH target AS MATERIALIZED (
              SELECT s.version_id
              FROM main.symbols AS s
              JOIN _miller_visible_entries AS e ON e.version_id=s.version_id
              WHERE s.symbol_id=$target
              LIMIT 1
            ),
            identifier_resolution AS MATERIALIZED (
              SELECT b.version_id,b.identifier_id,b.target_version_id,b.target_symbol_id,
                     b.tier,b.confidence
              FROM resolution_base.identifier_resolutions AS b
              WHERE b.target_version_id=(SELECT version_id FROM target)
                AND b.target_symbol_id=$target
                AND NOT EXISTS (
                SELECT 1 FROM main.resolution_identifier_deltas AS d
                WHERE d.view_id=(SELECT view_id FROM _miller_session)
                  AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                  AND d.version_id=b.version_id AND d.identifier_id=b.identifier_id)
              UNION ALL
              SELECT d.version_id,d.identifier_id,d.target_version_id,d.target_symbol_id,
                     d.tier,d.confidence
              FROM main.resolution_identifier_deltas AS d
              WHERE d.target_version_id=(SELECT version_id FROM target)
                AND d.target_symbol_id=$target
                AND d.view_id=(SELECT view_id FROM _miller_session)
                AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
            ),
            pending_resolution AS MATERIALIZED (
              SELECT b.version_id,b.pending_relationship_id,b.target_version_id,b.target_symbol_id,
                     b.tier,b.confidence
              FROM resolution_base.pending_resolutions AS b
              WHERE b.target_version_id=(SELECT version_id FROM target)
                AND b.target_symbol_id=$target
                AND NOT EXISTS (
                SELECT 1 FROM main.resolution_pending_deltas AS d
                WHERE d.view_id=(SELECT view_id FROM _miller_session)
                  AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                  AND d.version_id=b.version_id
                  AND d.pending_relationship_id=b.pending_relationship_id)
              UNION ALL
              SELECT d.version_id,d.pending_relationship_id,d.target_version_id,d.target_symbol_id,
                     d.tier,d.confidence
              FROM main.resolution_pending_deltas AS d
              WHERE d.target_version_id=(SELECT version_id FROM target)
                AND d.target_symbol_id=$target
                AND d.view_id=(SELECT view_id FROM _miller_session)
                AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                AND d.operation='replace'
            )
            SELECT s.containing_symbol_id,s.path,s.start_line,s.start_column,s.end_line,s.end_column,
                   s.start_byte,s.end_byte,i.kind,COALESCE(ir.confidence,i.confidence),
                   'identifier_resolution' AS source,ir.tier,s.language,
                   s.reference_site_id,s.is_exact,s.provenance
            FROM identifier_resolution AS ir
            CROSS JOIN main.identifiers AS i
              ON i.version_id=ir.version_id AND i.identifier_id=ir.identifier_id
            JOIN main.reference_sites AS s
              ON s.version_id=i.version_id AND s.reference_site_id=i.reference_site_id
            JOIN _miller_visible_entries AS e ON e.version_id=i.version_id
            UNION ALL
            SELECT s.containing_symbol_id,s.path,s.start_line,s.start_column,s.end_line,s.end_column,
                   s.start_byte,s.end_byte,r.kind,r.confidence,'relationship' AS source,
                   NULL AS tier,s.language,s.reference_site_id,s.is_exact,s.provenance
            FROM main.relationships AS r
            JOIN main.reference_sites AS s
              ON s.version_id=r.version_id AND s.reference_site_id=r.reference_site_id
            JOIN _miller_visible_entries AS e ON e.version_id=r.version_id
            WHERE r.version_id=(SELECT version_id FROM target)
              AND r.to_symbol_id=$target
            UNION ALL
            SELECT s.containing_symbol_id,s.path,s.start_line,s.start_column,s.end_line,s.end_column,
                   s.start_byte,s.end_byte,p.kind,MIN(p.confidence,pr.confidence),
                   'pending_resolution' AS source,pr.tier,s.language,
                   s.reference_site_id,s.is_exact,s.provenance
            FROM pending_resolution AS pr
            CROSS JOIN main.pending_relationships AS p
              ON p.version_id=pr.version_id
             AND p.pending_relationship_id=pr.pending_relationship_id
            JOIN main.reference_sites AS s
              ON s.version_id=p.version_id AND s.reference_site_id=p.reference_site_id
            JOIN _miller_visible_entries AS e ON e.version_id=p.version_id
            ORDER BY 2,7,3,9,11,14;
            """;
        command.Parameters.AddWithValue("$target", targetSymbolId);
        return ReadRows(command, targetSymbolId, ReferenceResolutionStatus.Exact);
    }

    private static List<ReferenceEvidence> ReadFamilyStoreFallback(
        SqliteConnection connection,
        string targetSymbolId,
        string targetName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.containing_symbol_id,s.path,s.start_line,s.start_column,s.end_line,s.end_column,
                   s.start_byte,s.end_byte,i.kind,MIN(i.confidence,0.5),'name_fallback' AS source,
                   NULL AS tier,s.language,s.reference_site_id,s.is_exact,s.provenance
            FROM main.identifiers AS i
            JOIN main.reference_sites AS s
              ON s.version_id=i.version_id AND s.reference_site_id=i.reference_site_id
            JOIN _miller_visible_entries AS e ON e.version_id=i.version_id
            WHERE i.name=$name
              AND NOT EXISTS (
                SELECT 1 FROM main.resolution_identifier_deltas AS d
                WHERE d.view_id=(SELECT view_id FROM _miller_session)
                  AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                  AND d.version_id=i.version_id AND d.identifier_id=i.identifier_id
                  AND d.target_symbol_id IS NOT NULL)
              AND NOT EXISTS (
                SELECT 1 FROM resolution_base.identifier_resolutions AS b
                WHERE b.version_id=i.version_id AND b.identifier_id=i.identifier_id
                  AND b.target_symbol_id IS NOT NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM main.resolution_identifier_deltas AS d
                    WHERE d.view_id=(SELECT view_id FROM _miller_session)
                      AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                      AND d.version_id=i.version_id AND d.identifier_id=i.identifier_id))
            ORDER BY s.path,s.start_byte,s.start_line,i.kind,s.reference_site_id;
            """;
        command.Parameters.AddWithValue("$name", targetName);
        return ReadRows(command, targetSymbolId, ReferenceResolutionStatus.Fallback);
    }

    private static List<OutgoingReferenceEvidence> ReadFamilyStoreOutgoingExact(
        SqliteConnection connection,
        string containingSymbolId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            WITH candidate_identifiers AS MATERIALIZED (
              SELECT i.*
              FROM main.identifiers AS i
              JOIN _miller_visible_entries AS e ON e.version_id=i.version_id
              WHERE i.containing_symbol_id=$containing
            ),
            identifier_resolution AS MATERIALIZED (
              SELECT b.version_id,b.identifier_id,b.target_version_id,b.target_symbol_id,
                     b.tier,b.confidence
              FROM candidate_identifiers AS i
              JOIN resolution_base.identifier_resolutions AS b
                ON b.version_id=i.version_id AND b.identifier_id=i.identifier_id
              WHERE NOT EXISTS (
                SELECT 1 FROM main.resolution_identifier_deltas AS d
                WHERE d.view_id=(SELECT view_id FROM _miller_session)
                  AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                  AND d.version_id=b.version_id AND d.identifier_id=b.identifier_id)
                AND b.target_symbol_id IS NOT NULL
              UNION ALL
              SELECT d.version_id,d.identifier_id,d.target_version_id,d.target_symbol_id,
                     d.tier,d.confidence
              FROM candidate_identifiers AS i
              JOIN main.resolution_identifier_deltas AS d
                ON d.version_id=i.version_id AND d.identifier_id=i.identifier_id
              WHERE d.view_id=(SELECT view_id FROM _miller_session)
                AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                AND d.target_symbol_id IS NOT NULL
            ),
            candidate_pending AS MATERIALIZED (
              SELECT p.*
              FROM main.pending_relationships AS p
              JOIN _miller_visible_entries AS e ON e.version_id=p.version_id
              WHERE COALESCE(p.caller_scope_symbol_id,p.from_symbol_id)=$containing
            ),
            pending_resolution AS MATERIALIZED (
              SELECT b.version_id,b.pending_relationship_id,b.target_version_id,b.target_symbol_id,
                     b.tier,b.confidence
              FROM candidate_pending AS p
              JOIN resolution_base.pending_resolutions AS b
                ON b.version_id=p.version_id
               AND b.pending_relationship_id=p.pending_relationship_id
              WHERE NOT EXISTS (
                SELECT 1 FROM main.resolution_pending_deltas AS d
                WHERE d.view_id=(SELECT view_id FROM _miller_session)
                  AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                  AND d.version_id=b.version_id
                  AND d.pending_relationship_id=b.pending_relationship_id)
              UNION ALL
              SELECT d.version_id,d.pending_relationship_id,d.target_version_id,d.target_symbol_id,
                     d.tier,d.confidence
              FROM candidate_pending AS p
              JOIN main.resolution_pending_deltas AS d
                ON d.version_id=p.version_id
               AND d.pending_relationship_id=p.pending_relationship_id
              WHERE d.view_id=(SELECT view_id FROM _miller_session)
                AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                AND d.operation='replace'
            )
            SELECT ir.target_symbol_id,target.name,s.path,
                   s.start_line,s.start_column,s.end_line,s.end_column,
                   s.start_byte,s.end_byte,i.kind,COALESCE(ir.confidence,i.confidence),
                   'identifier_resolution' AS source,ir.tier,s.language,
                   s.reference_site_id,s.is_exact,s.provenance
            FROM identifier_resolution AS ir
            JOIN candidate_identifiers AS i
              ON i.version_id=ir.version_id AND i.identifier_id=ir.identifier_id
            JOIN main.symbols AS target
              ON target.version_id=ir.target_version_id AND target.symbol_id=ir.target_symbol_id
            JOIN main.reference_sites AS s
              ON s.version_id=i.version_id AND s.reference_site_id=i.reference_site_id
            JOIN _miller_visible_entries AS target_entry ON target_entry.version_id=target.version_id
            UNION ALL
            SELECT r.to_symbol_id,target.name,s.path,
                   s.start_line,s.start_column,s.end_line,s.end_column,
                   s.start_byte,s.end_byte,r.kind,r.confidence,'relationship' AS source,
                   NULL AS tier,s.language,s.reference_site_id,s.is_exact,s.provenance
            FROM main.relationships AS r
            JOIN _miller_visible_entries AS e ON e.version_id=r.version_id
            JOIN main.symbols AS target
              ON target.version_id=r.version_id AND target.symbol_id=r.to_symbol_id
            JOIN main.reference_sites AS s
              ON s.version_id=r.version_id AND s.reference_site_id=r.reference_site_id
            WHERE r.from_symbol_id=$containing
            UNION ALL
            SELECT pr.target_symbol_id,target.name,s.path,
                   s.start_line,s.start_column,s.end_line,s.end_column,
                   s.start_byte,s.end_byte,p.kind,MIN(p.confidence,pr.confidence),
                   'pending_resolution' AS source,pr.tier,s.language,
                   s.reference_site_id,s.is_exact,s.provenance
            FROM pending_resolution AS pr
            JOIN candidate_pending AS p
              ON p.version_id=pr.version_id
             AND p.pending_relationship_id=pr.pending_relationship_id
            JOIN main.symbols AS target
              ON target.version_id=pr.target_version_id AND target.symbol_id=pr.target_symbol_id
            JOIN main.reference_sites AS s
              ON s.version_id=p.version_id AND s.reference_site_id=p.reference_site_id
            JOIN _miller_visible_entries AS target_entry ON target_entry.version_id=target.version_id
            ORDER BY 3,8,4,10,12,2,1,15;
            """;
        command.Parameters.AddWithValue("$containing", containingSymbolId);
        return ReadOutgoingRows(command, containingSymbolId, ReferenceResolutionStatus.Exact);
    }

    private static List<OutgoingReferenceEvidence> ReadFamilyStoreOutgoingFallback(
        SqliteConnection connection,
        string containingSymbolId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT NULL AS target_symbol_id,i.name,s.path,
                   s.start_line,s.start_column,s.end_line,s.end_column,
                   s.start_byte,s.end_byte,i.kind,MIN(i.confidence,0.5),
                   'name_fallback' AS source,NULL AS tier,s.language,
                   s.reference_site_id,s.is_exact,s.provenance
            FROM main.identifiers AS i
            JOIN main.reference_sites AS s
              ON s.version_id=i.version_id AND s.reference_site_id=i.reference_site_id
            JOIN _miller_visible_entries AS e ON e.version_id=i.version_id
            WHERE i.containing_symbol_id=$containing
              AND NOT EXISTS (
                SELECT 1 FROM main.resolution_identifier_deltas AS d
                WHERE d.view_id=(SELECT view_id FROM _miller_session)
                  AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                  AND d.version_id=i.version_id AND d.identifier_id=i.identifier_id
                  AND d.target_symbol_id IS NOT NULL)
              AND NOT EXISTS (
                SELECT 1 FROM resolution_base.identifier_resolutions AS b
                WHERE b.version_id=i.version_id AND b.identifier_id=i.identifier_id
                  AND b.target_symbol_id IS NOT NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM main.resolution_identifier_deltas AS d
                    WHERE d.view_id=(SELECT view_id FROM _miller_session)
                      AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                      AND d.version_id=i.version_id AND d.identifier_id=i.identifier_id))
            UNION ALL
            SELECT NULL AS target_symbol_id,p.target_display_name,s.path,
                   s.start_line,s.start_column,s.end_line,s.end_column,
                   s.start_byte,s.end_byte,p.kind,MIN(p.confidence,0.5),
                   'name_fallback' AS source,NULL AS tier,s.language,
                   s.reference_site_id,s.is_exact,s.provenance
            FROM main.pending_relationships AS p
            JOIN main.reference_sites AS s
              ON s.version_id=p.version_id AND s.reference_site_id=p.reference_site_id
            JOIN _miller_visible_entries AS e ON e.version_id=p.version_id
            WHERE COALESCE(p.caller_scope_symbol_id,p.from_symbol_id)=$containing
              AND NOT EXISTS (
                SELECT 1 FROM main.resolution_pending_deltas AS d
                WHERE d.view_id=(SELECT view_id FROM _miller_session)
                  AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                  AND d.version_id=p.version_id
                  AND d.pending_relationship_id=p.pending_relationship_id
                  AND d.operation='replace')
              AND NOT EXISTS (
                SELECT 1 FROM resolution_base.pending_resolutions AS b
                WHERE b.version_id=p.version_id
                  AND b.pending_relationship_id=p.pending_relationship_id
                  AND NOT EXISTS (
                    SELECT 1 FROM main.resolution_pending_deltas AS d
                    WHERE d.view_id=(SELECT view_id FROM _miller_session)
                      AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                      AND d.version_id=p.version_id
                      AND d.pending_relationship_id=p.pending_relationship_id))
            ORDER BY 3,8,4,10,2,15;
            """;
        command.Parameters.AddWithValue("$containing", containingSymbolId);
        return ReadOutgoingRows(command, containingSymbolId, ReferenceResolutionStatus.Fallback);
    }
}
