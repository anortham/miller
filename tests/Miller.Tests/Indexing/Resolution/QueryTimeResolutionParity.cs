using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Core.Graph;
using Miller.Core.References;
using Miller.Core.Resolution;
using Miller.Indexing.Reads;
using Miller.Indexing.Resolution;
using Xunit;

namespace Miller.Tests.Indexing.Resolution;

internal sealed record StoredResolution(
    string Outcome,
    long? TargetVersionId,
    string? TargetSymbolId,
    int? Tier,
    double? Confidence,
    string? Method,
    int? Candidates);

internal sealed record QueryResolution(
    string Outcome,
    long? TargetVersionId,
    string? TargetSymbolId,
    int? Tier,
    double? Confidence,
    string? Method,
    int? Candidates);

internal sealed record ParityReport(
    int Compared,
    int Matched,
    int UnderResolved,
    IReadOnlyList<string> UnderResolvedSamples,
    IReadOnlyList<string> Divergences)
{
    public bool Passed => Divergences.Count == 0;
}

internal static class QueryTimeResolutionParity
{
    internal const string MillerSnapshotDirectory = "/tmp/qtr-spike-snapshot";
    internal const string AspnetSnapshotDirectory = "/tmp/qtr-aspnet-snapshot";
    internal const int WarmNameTopFanout = 40;
    internal const int WarmNameRandom = 120;
    internal const int SampleLimit = 20;

    internal static bool BinarySupportsResolve(string binary)
    {
        var start = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("store");
        start.ArgumentList.Add("--help");
        using Process process = Process.Start(start)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        string text = stdout + Environment.NewLine + stderr;
        return text.Contains("resolve", StringComparison.OrdinalIgnoreCase);
    }

    internal static SqliteConnection OpenRead(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    internal static StoreVisibility ReadExactVisibility(SqliteConnection connection, string storePath)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT v.view_id,v.root,v.current_generation,m.manifest_hash,
                   v.resolution_state,v.resolution_base_id,
                   v.resolution_delta_generation,v.resolution_exact_at
            FROM views AS v
            LEFT JOIN manifests AS m
              ON m.view_id=v.view_id AND m.generation=v.current_generation
            WHERE v.current_generation IS NOT NULL
            ORDER BY CASE v.resolution_state WHEN 'exact' THEN 0 ELSE 1 END,
                     v.current_generation DESC
            LIMIT 1
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read(), "The store has no view with a current generation.");
        string viewId = reader.GetString(0);
        string root = reader.GetString(1);
        long generation = reader.GetInt64(2);
        string hash = reader.IsDBNull(3) ? "snapshot-manifest" : reader.GetString(3);
        string state = reader.GetString(4);
        string? baseId = reader.IsDBNull(5) ? null : reader.GetString(5);
        long? delta = reader.IsDBNull(6) ? null : reader.GetInt64(6);
        long? exactAt = reader.IsDBNull(7) ? null : reader.GetInt64(7);
        reader.Close();

        string familyId = ReadMeta(connection, "family_id") ?? "snapshot";
        string binary = ReadMeta(connection, "binary_version") ?? "2.34.4";
        string storeDir = Path.GetDirectoryName(storePath) ?? storePath;
        return new StoreVisibility(
            familyId,
            storeDir,
            "gen-001",
            storePath,
            Path.Combine(storeDir, "coord.db"),
            viewId,
            root,
            generation,
            hash,
            state,
            baseId,
            delta,
            exactAt,
            generation,
            "full",
            binary,
            "snapshot",
            "1",
            "2",
            "3");
    }

    internal static string? LocateResolutionBase(SqliteConnection store, StoreVisibility visibility)
    {
        if (visibility.ResolutionBaseId is null)
            return SiblingBase(visibility.StoreDatabasePath);

        using SqliteCommand command = store.CreateCommand();
        command.CommandText = "SELECT relative_path FROM resolution_bases WHERE base_id=$id LIMIT 1";
        command.Parameters.AddWithValue("$id", visibility.ResolutionBaseId);
        string? relative = command.ExecuteScalar() as string;
        if (relative is { Length: > 0 })
        {
            string rooted = Path.Combine(visibility.StoreRoot, relative);
            if (File.Exists(rooted))
                return rooted;
            string besideGeneration = Path.Combine(
                Path.GetDirectoryName(visibility.StoreDatabasePath) ?? visibility.StoreRoot,
                relative);
            if (File.Exists(besideGeneration))
                return besideGeneration;
        }

        return SiblingBase(visibility.StoreDatabasePath);
    }

    internal static void AttachResolutionBase(SqliteConnection connection, string basePath)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "ATTACH DATABASE $path AS resolution_base";
        command.Parameters.AddWithValue("$path", basePath);
        command.ExecuteNonQuery();
    }

    internal static Dictionary<(long VersionId, string Id), StoredResolution> ReadStoredIdentifiers(
        SqliteConnection store,
        StoreVisibility visibility)
    {
        using SqliteCommand command = store.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText =
            """
            WITH visible AS (
              SELECT version_id
              FROM main.manifest_entries
              WHERE view_id=$view_id AND generation=$generation AND version_id IS NOT NULL
            ),
            delta_gen AS (
              SELECT MAX(delta_generation) AS g
              FROM main.resolution_identifier_deltas
              WHERE view_id=$view_id
            ),
            overlay AS (
              SELECT d.version_id,d.identifier_id,d.target_version_id,d.target_symbol_id,
                     d.tier,d.confidence,d.method,d.outcome,d.candidates
              FROM main.resolution_identifier_deltas AS d
              JOIN delta_gen ON d.delta_generation=delta_gen.g
              WHERE d.view_id=$view_id
            )
            SELECT r.version_id,r.identifier_id,
                   CASE WHEN o.identifier_id IS NOT NULL THEN o.outcome ELSE r.outcome END,
                   CASE WHEN o.identifier_id IS NOT NULL THEN o.target_version_id ELSE r.target_version_id END,
                   CASE WHEN o.identifier_id IS NOT NULL THEN o.target_symbol_id ELSE r.target_symbol_id END,
                   CASE WHEN o.identifier_id IS NOT NULL THEN o.tier ELSE r.tier END,
                   CASE WHEN o.identifier_id IS NOT NULL THEN o.confidence ELSE r.confidence END,
                   CASE WHEN o.identifier_id IS NOT NULL THEN o.method ELSE r.method END,
                   CASE WHEN o.identifier_id IS NOT NULL THEN o.candidates ELSE r.candidates END
            FROM resolution_base.identifier_resolutions AS r
            LEFT JOIN overlay AS o
              ON o.version_id=r.version_id AND o.identifier_id=r.identifier_id
            JOIN visible AS v ON v.version_id=r.version_id
            UNION ALL
            SELECT o.version_id,o.identifier_id,o.outcome,o.target_version_id,o.target_symbol_id,
                   o.tier,o.confidence,o.method,o.candidates
            FROM overlay AS o
            JOIN visible AS v ON v.version_id=o.version_id
            WHERE NOT EXISTS (
              SELECT 1 FROM resolution_base.identifier_resolutions AS r
              WHERE r.version_id=o.version_id AND r.identifier_id=o.identifier_id)
            """;
        BindVisibility(command, visibility);
        return ReadStoredMap(command);
    }

    internal static Dictionary<(long VersionId, string Id), StoredResolution> ReadStoredPendings(
        SqliteConnection store,
        StoreVisibility visibility)
    {
        using SqliteCommand command = store.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText =
            """
            WITH visible AS (
              SELECT version_id
              FROM main.manifest_entries
              WHERE view_id=$view_id AND generation=$generation AND version_id IS NOT NULL
            ),
            delta_gen AS (
              SELECT MAX(delta_generation) AS g
              FROM main.resolution_pending_deltas
              WHERE view_id=$view_id
            ),
            overlay AS (
              SELECT d.version_id,d.pending_relationship_id,d.operation,d.target_version_id,
                     d.target_symbol_id,d.tier,d.confidence,d.method
              FROM main.resolution_pending_deltas AS d
              JOIN delta_gen ON d.delta_generation=delta_gen.g
              WHERE d.view_id=$view_id
            )
            SELECT r.version_id,r.pending_relationship_id,'resolved',r.target_version_id,
                   r.target_symbol_id,r.tier,r.confidence,r.method,NULL
            FROM resolution_base.pending_resolutions AS r
            JOIN visible AS v ON v.version_id=r.version_id
            LEFT JOIN overlay AS o
              ON o.version_id=r.version_id AND o.pending_relationship_id=r.pending_relationship_id
            WHERE o.pending_relationship_id IS NULL
            UNION ALL
            SELECT o.version_id,o.pending_relationship_id,'resolved',o.target_version_id,
                   o.target_symbol_id,o.tier,o.confidence,o.method,NULL
            FROM overlay AS o
            JOIN visible AS v ON v.version_id=o.version_id
            WHERE o.operation='replace'
            """;
        BindVisibility(command, visibility);
        return ReadStoredMap(command);
    }

    internal static QueryResolution FromOutcome(ResolutionOutcome outcome) =>
        new(
            OutcomeName(outcome.Kind),
            outcome.Target?.VersionId,
            outcome.Target?.SymbolId,
            outcome.Tier,
            outcome.Confidence,
            outcome.Method,
            outcome.CandidateCount);

    internal static ResolutionOutcome ResolveIdentifier(
        QueryTimeResolver resolver,
        RevisionFactCache cache,
        IdentifierSite site,
        IReadOnlyDictionary<(long VersionId, string Id), PendingFact> pendings,
        IReadOnlyDictionary<(long VersionId, string Id), RelationshipFact> relationships)
    {
        if (cache.Propagation.TryGetOverride(site.VersionId, site.RowId, out PropagationSource source))
        {
            if (source.Origin == PropagationOrigin.Relationship
                && relationships.TryGetValue((site.VersionId, source.RowId), out RelationshipFact rel))
            {
                return ResolutionOutcome.Resolved(
                    new FactSymbolKey(rel.TargetVersionId, rel.TargetSymbolId),
                    1,
                    Math.Min(rel.Confidence, ResolutionPolicy.LocalConfidence),
                    ResolutionPolicy.LocalMethod);
            }

            if (source.Origin == PropagationOrigin.Pending
                && pendings.TryGetValue((site.VersionId, source.RowId), out PendingFact pending))
            {
                ResolutionOutcome pendingOutcome = ResolvePending(resolver, cache, pending);
                if (pendingOutcome.Kind == ResolutionOutcomeKind.Resolved)
                    return pendingOutcome;
            }
        }

        ResolutionRefKind? kind = ResolutionKinds.FromIdentifierKind(site.Kind);
        if (kind is null)
            return ResolutionOutcome.NoContext;
        string language = cache.Slice(site.VersionId)?.Language ?? string.Empty;
        return resolver.Resolve(new ResolutionInput(
            ResolutionOrigin.Identifier,
            kind.Value,
            language,
            site.VersionId,
            site.Name,
            site.Receiver,
            site.ReceiverQualifier,
            site.ContainingSymbolId,
            site.Confidence,
            ReceiverType: site.ReceiverType));
    }

    internal static ResolutionOutcome ResolvePending(
        QueryTimeResolver resolver,
        RevisionFactCache cache,
        PendingFact pending)
    {
        ResolutionRefKind? kind = ResolutionKinds.FromPendingKind(pending.Kind);
        if (kind is null)
            return ResolutionOutcome.NoContext;
        var slice = cache.Slice(pending.VersionId);
        string language = slice?.Language ?? pending.Language;
        string? path = slice?.Path;
        return resolver.Resolve(new ResolutionInput(
            ResolutionOrigin.Pending,
            kind.Value,
            language,
            pending.VersionId,
            pending.Name,
            pending.Receiver,
            pending.Qualifier,
            pending.CallerScopeSymbolId,
            pending.Confidence,
            path,
            ReceiverType: pending.ReceiverType));
    }

    internal static Dictionary<(long VersionId, string Id), PendingFact> ReadPendingFacts(
        SqliteConnection connection,
        StoreVisibility visibility)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText =
            """
            SELECT p.version_id,p.pending_relationship_id,p.from_symbol_id,p.caller_scope_symbol_id,
                   p.kind,p.target_terminal_name,p.target_receiver,p.target_namespace_json,
                   p.confidence,e.language,p.metadata_json
            FROM main.pending_relationships AS p
            JOIN main.manifest_entries AS e ON e.version_id=p.version_id
            WHERE e.view_id=$view_id AND e.generation=$generation
            """;
        BindVisibility(command, visibility);
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new Dictionary<(long VersionId, string Id), PendingFact>();
        while (reader.Read())
        {
            var key = (reader.GetInt64(0), reader.GetString(1));
            rows[key] = new PendingFact(
                key.Item1,
                key.Item2,
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                QualifierFromNamespaceJson(reader.IsDBNull(7) ? null : reader.GetString(7)),
                FactMetadataParser.ReceiverType(reader.IsDBNull(10) ? null : reader.GetString(10)),
                reader.GetDouble(8),
                reader.GetString(9));
        }

        return rows;
    }

    internal static Dictionary<(long VersionId, string Id), RelationshipFact> ReadRelationshipFacts(
        SqliteConnection connection,
        StoreVisibility visibility)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText =
            """
            SELECT r.version_id,r.relationship_id,t.version_id,r.to_symbol_id,r.confidence
            FROM main.relationships AS r
            JOIN main.manifest_entries AS e ON e.version_id=r.version_id
            JOIN main.symbols AS t ON t.symbol_id=r.to_symbol_id
            JOIN main.manifest_entries AS te
              ON te.version_id=t.version_id AND te.view_id=$view_id AND te.generation=$generation
            WHERE e.view_id=$view_id AND e.generation=$generation
              AND r.kind IN ('calls','instantiates','uses','extends','implements')
            """;
        BindVisibility(command, visibility);
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new Dictionary<(long VersionId, string Id), RelationshipFact>();
        while (reader.Read())
        {
            var key = (reader.GetInt64(0), reader.GetString(1));
            rows[key] = new RelationshipFact(reader.GetInt64(2), reader.GetString(3), reader.GetDouble(4));
        }

        return rows;
    }

    internal static ParityReport CompareIdentifiers(
        SqliteConnection store,
        StoreVisibility visibility,
        RevisionFactCache cache,
        QueryTimeResolver resolver,
        IReadOnlyDictionary<(long VersionId, string Id), StoredResolution> stored,
        IReadOnlyDictionary<(long VersionId, string Id), PendingFact> pendings,
        IReadOnlyDictionary<(long VersionId, string Id), RelationshipFact> relationships)
    {
        var divergences = new List<string>();
        var under = new List<string>();
        int compared = 0;
        int matched = 0;
        int underCount = 0;
        foreach (IdentifierSite site in IdentifierSiteReader.SitesAll(store, visibility))
        {
            compared++;
            QueryResolution query = FromOutcome(ResolveIdentifier(resolver, cache, site, pendings, relationships));
            stored.TryGetValue((site.VersionId, site.IdentifierId), out StoredResolution? truth);
            truth ??= MissingStore();
            if (Matches(truth, query))
            {
                matched++;
                continue;
            }

            if (IsProducerUnderResolution(truth, query))
            {
                underCount++;
                if (under.Count < SampleLimit)
                    under.Add(Describe("identifier", site.VersionId, site.IdentifierId, site.Name, truth, query));
                continue;
            }

            if (divergences.Count < SampleLimit)
                divergences.Add(Describe("identifier", site.VersionId, site.IdentifierId, site.Name, truth, query));
        }

        return new ParityReport(compared, matched, underCount, under, divergences);
    }

    internal static ParityReport ComparePendings(
        RevisionFactCache cache,
        QueryTimeResolver resolver,
        IReadOnlyDictionary<(long VersionId, string Id), StoredResolution> stored,
        IReadOnlyDictionary<(long VersionId, string Id), PendingFact> pendings)
    {
        var divergences = new List<string>();
        var under = new List<string>();
        int compared = 0;
        int matched = 0;
        int underCount = 0;
        foreach (PendingFact pending in pendings.Values)
        {
            compared++;
            QueryResolution query = FromOutcome(ResolvePending(resolver, cache, pending));
            stored.TryGetValue((pending.VersionId, pending.PendingId), out StoredResolution? truth);
            if (truth is null)
            {
                if (query.Outcome == "resolved")
                {
                    underCount++;
                    if (under.Count < SampleLimit)
                        under.Add(Describe("pending", pending.VersionId, pending.PendingId, pending.Name, MissingStore(), query));
                }
                else
                {
                    matched++;
                }

                continue;
            }

            if (Matches(truth, query))
            {
                matched++;
                continue;
            }

            if (IsProducerUnderResolution(truth, query))
            {
                underCount++;
                if (under.Count < SampleLimit)
                    under.Add(Describe("pending", pending.VersionId, pending.PendingId, pending.Name, truth, query));
                continue;
            }

            if (divergences.Count < SampleLimit)
                divergences.Add(Describe("pending", pending.VersionId, pending.PendingId, pending.Name, truth, query));
        }

        return new ParityReport(compared, matched, underCount, under, divergences);
    }

    internal static string[] SerializeGraph(
        QueryTimeResolutionReader reader,
        SqliteConnection connection,
        IReadOnlyList<string> candidates)
    {
        var rows = new List<string>();
        foreach (FamilyGraphResolutionEdge edge in reader.ReadResolutionEdges(
                     connection, candidates, Direction.Both, statementObserver: null))
        {
            rows.Add($"{edge.CurrentId}|{edge.FromId}|{edge.ToId}|{edge.Kind}|{Fmt(edge.Confidence)}|{edge.Source}");
        }

        foreach (FamilyGraphUnresolvedNameEdge edge in reader.ReadUnresolvedNameEdges(
                     connection, candidates, Direction.Both, statementObserver: null))
        {
            rows.Add($"{edge.CurrentId}|{edge.FromId}|{edge.ToId}|{edge.Kind}|{Fmt(edge.Confidence)}|{edge.Source}");
        }

        rows.Sort(StringComparer.Ordinal);
        return [.. rows];
    }

    internal static string[] SerializeEvidence(
        QueryTimeResolutionReader reader,
        SqliteConnection connection,
        IReadOnlyList<string> candidates)
    {
        var rows = new List<string>();
        Dictionary<string, List<ReferenceEvidence>> inboundExact = reader.ReadInboundExact(connection, candidates);
        Dictionary<string, List<ReferenceEvidence>> inboundFallback = reader.ReadInboundFallback(connection, candidates);
        Dictionary<string, List<OutgoingReferenceEvidence>> outgoingExact = reader.ReadOutgoingExact(connection, candidates);
        Dictionary<string, List<OutgoingReferenceEvidence>> outgoingFallback = reader.ReadOutgoingFallback(connection, candidates);
        foreach (string id in candidates.OrderBy(static value => value, StringComparer.Ordinal))
        {
            if (inboundExact.TryGetValue(id, out List<ReferenceEvidence>? exact))
            {
                foreach (ReferenceEvidence row in exact)
                    rows.Add(FormatInbound(id, row));
            }

            if (inboundFallback.TryGetValue(id, out List<ReferenceEvidence>? fallback))
            {
                foreach (ReferenceEvidence row in fallback)
                    rows.Add(FormatInbound(id, row));
            }

            if (outgoingExact.TryGetValue(id, out List<OutgoingReferenceEvidence>? outExact))
            {
                foreach (OutgoingReferenceEvidence row in outExact)
                    rows.Add(FormatOutgoing(row));
            }

            if (outgoingFallback.TryGetValue(id, out List<OutgoingReferenceEvidence>? outFallback))
            {
                foreach (OutgoingReferenceEvidence row in outFallback)
                    rows.Add(FormatOutgoing(row));
            }
        }

        rows.Sort(StringComparer.Ordinal);
        return [.. rows];
    }

    internal static string[] SerializeExport(QueryTimeResolutionReader reader, SqliteConnection connection)
    {
        var rows = new List<string>();
        foreach (QueryTimeExportEvidence row in reader.ReadExportEvidence(connection))
        {
            rows.Add(string.Join(
                '|',
                row.ReferenceSiteId,
                row.IsExact ? "1" : "0",
                row.SiteProvenance,
                row.Path,
                row.Language,
                row.ContainingSymbolId ?? string.Empty,
                FmtNullable(row.StartLine),
                FmtNullable(row.StartColumn),
                FmtNullable(row.EndLine),
                FmtNullable(row.EndColumn),
                FmtNullable(row.StartByte),
                FmtNullable(row.EndByte),
                row.CanonicalKind,
                row.TargetSymbolId ?? string.Empty,
                row.TargetName,
                row.TargetKind ?? string.Empty,
                row.EvidenceSource,
                Fmt(row.Confidence),
                row.ResolutionTier?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                row.SourceName ?? string.Empty,
                row.SourceKind ?? string.Empty));
        }

        rows.Sort(StringComparer.Ordinal);
        return [.. rows];
    }

    private static string FmtNullable(long? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    internal static string[] ReconstructGraphFromStore(
        SqliteConnection store,
        StoreVisibility visibility,
        RevisionFactCache cache,
        IReadOnlyList<string> candidates,
        IReadOnlyDictionary<(long VersionId, string Id), StoredResolution> storedIdentifiers,
        IReadOnlyDictionary<(long VersionId, string Id), StoredResolution> storedPendings,
        IReadOnlyDictionary<(long VersionId, string Id), PendingFact> pendings,
        IReadOnlyDictionary<(long VersionId, string Id), RelationshipFact> relationships)
    {
        Dictionary<string, string> uniqueNames = UniqueVisibleNames(store, visibility);
        IdentifierSite[] sites = [.. IdentifierSiteReader.SitesAll(store, visibility)];
        var rows = new List<string>();
        var candidateSet = new HashSet<string>(candidates, StringComparer.Ordinal);
        foreach (string candidateId in candidates)
        {
            foreach (IdentifierSite site in sites)
            {
                bool skip = cache.Propagation.TryGetOverride(site.VersionId, site.RowId, out _);
                storedIdentifiers.TryGetValue((site.VersionId, site.IdentifierId), out StoredResolution? stored);
                stored ??= MissingStore();
                bool resolved = stored.Outcome == "resolved" && stored.TargetSymbolId is not null;
                if (!skip && resolved && site.ContainingSymbolId is { } from
                    && !string.Equals(from, stored.TargetSymbolId, StringComparison.Ordinal))
                {
                    if (string.Equals(from, candidateId, StringComparison.Ordinal)
                        || string.Equals(stored.TargetSymbolId, candidateId, StringComparison.Ordinal))
                    {
                        rows.Add($"{candidateId}|{from}|{stored.TargetSymbolId}|{site.Kind}|{Fmt(stored.Confidence ?? site.Confidence)}|identifier_target");
                    }
                }

                if (!skip && !resolved && site.ContainingSymbolId is { } container
                    && uniqueNames.TryGetValue(site.Name, out string? unique)
                    && unique is not null
                    && !string.Equals(container, unique, StringComparison.Ordinal)
                    && (string.Equals(container, candidateId, StringComparison.Ordinal)
                        || string.Equals(unique, candidateId, StringComparison.Ordinal)))
                {
                    rows.Add($"{candidateId}|{container}|{unique}|{site.Kind}|{Fmt(site.Confidence * 0.5)}|identifier_name");
                }
            }

            foreach (PendingFact pending in pendings.Values)
            {
                if (!storedPendings.TryGetValue((pending.VersionId, pending.PendingId), out StoredResolution? stored)
                    || stored.TargetSymbolId is null)
                {
                    continue;
                }

                if (string.Equals(pending.FromSymbolId, stored.TargetSymbolId, StringComparison.Ordinal))
                    continue;
                if (string.Equals(pending.FromSymbolId, candidateId, StringComparison.Ordinal)
                    || string.Equals(stored.TargetSymbolId, candidateId, StringComparison.Ordinal))
                {
                    double confidence = Math.Min(pending.Confidence, stored.Confidence ?? pending.Confidence);
                    rows.Add($"{candidateId}|{pending.FromSymbolId}|{stored.TargetSymbolId}|{pending.Kind}|{Fmt(confidence)}|pending_resolution");
                }
            }

            _ = relationships;
            _ = candidateSet;
        }

        rows.Sort(StringComparer.Ordinal);
        return [.. rows];
    }

    internal static IReadOnlyList<string> WarmNameMix(SqliteConnection store, StoreVisibility visibility)
    {
        using SqliteCommand command = store.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText =
            """
            SELECT i.name, COUNT(*) AS n
            FROM main.identifiers AS i
            JOIN main.manifest_entries AS e ON e.version_id=i.version_id
            WHERE e.view_id=$view_id AND e.generation=$generation
            GROUP BY i.name
            ORDER BY n DESC, i.name
            """;
        BindVisibility(command, visibility);
        using SqliteDataReader reader = command.ExecuteReader();
        var ranked = new List<(string Name, long Count)>();
        while (reader.Read())
            ranked.Add((reader.GetString(0), reader.GetInt64(1)));

        var selected = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string name, long _) in ranked.Take(WarmNameTopFanout))
        {
            if (seen.Add(name))
                selected.Add(name);
        }

        var rng = new Random(18);
        List<(string Name, long Count)> rest = [.. ranked.Skip(WarmNameTopFanout)];
        while (selected.Count < WarmNameTopFanout + WarmNameRandom && rest.Count > 0)
        {
            int index = rng.Next(rest.Count);
            string name = rest[index].Name;
            rest.RemoveAt(index);
            if (seen.Add(name))
                selected.Add(name);
        }

        return selected;
    }

    internal static TimeSpan QueryName(
        SqliteConnection store,
        StoreVisibility visibility,
        RevisionFactCache cache,
        QueryTimeResolver resolver,
        string name)
    {
        long started = Stopwatch.GetTimestamp();
        foreach (IdentifierSite site in IdentifierSiteReader.SitesNamed(store, visibility, name))
        {
            ResolutionRefKind? kind = ResolutionKinds.FromIdentifierKind(site.Kind);
            if (kind is null)
                continue;
            string language = cache.Slice(site.VersionId)?.Language ?? string.Empty;
            _ = resolver.Resolve(new ResolutionInput(
                ResolutionOrigin.Identifier,
                kind.Value,
                language,
                site.VersionId,
                site.Name,
                site.Receiver,
                site.ReceiverQualifier,
                site.ContainingSymbolId,
                site.Confidence,
                ReceiverType: site.ReceiverType));
        }

        return Stopwatch.GetElapsedTime(started);
    }

    internal static long CurrentRss()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.WorkingSet64;
    }

    internal static long CurrentPss()
    {
        const string path = "/proc/self/smaps_rollup";
        if (!File.Exists(path))
            return CurrentRss();
        foreach (string line in File.ReadLines(path))
        {
            if (!line.StartsWith("Pss:", StringComparison.Ordinal))
                continue;
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && long.TryParse(parts[1], out long kib))
                return kib * 1024;
        }

        return CurrentRss();
    }

    internal static string FmtMs(TimeSpan value) =>
        value.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture);

    internal static string FmtMb(long bytes) =>
        (bytes / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture);

    internal readonly record struct PendingFact(
        long VersionId,
        string PendingId,
        string FromSymbolId,
        string? CallerScopeSymbolId,
        string Kind,
        string Name,
        string? Receiver,
        string? Qualifier,
        string? ReceiverType,
        double Confidence,
        string Language);

    internal readonly record struct RelationshipFact(long TargetVersionId, string TargetSymbolId, double Confidence);

    private static Dictionary<(long VersionId, string Id), StoredResolution> ReadStoredMap(SqliteCommand command)
    {
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new Dictionary<(long VersionId, string Id), StoredResolution>();
        while (reader.Read())
        {
            rows[(reader.GetInt64(0), reader.GetString(1))] = new StoredResolution(
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : Convert.ToInt32(reader.GetInt64(5), CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : reader.GetDouble(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : Convert.ToInt32(reader.GetInt64(8), CultureInfo.InvariantCulture));
        }

        return rows;
    }

    private static Dictionary<string, string> UniqueVisibleNames(SqliteConnection store, StoreVisibility visibility)
    {
        using SqliteCommand command = store.CreateCommand();
        command.CommandText =
            """
            SELECT s.name, MIN(s.symbol_id), COUNT(*)
            FROM main.symbols AS s
            JOIN main.manifest_entries AS e ON e.version_id=s.version_id
            WHERE e.view_id=$view_id AND e.generation=$generation
            GROUP BY s.name
            HAVING COUNT(*)=1
            """;
        BindVisibility(command, visibility);
        using SqliteDataReader reader = command.ExecuteReader();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
            names[reader.GetString(0)] = reader.GetString(1);
        return names;
    }

    private static bool Matches(StoredResolution stored, QueryResolution query)
    {
        if (!string.Equals(stored.Outcome, query.Outcome, StringComparison.Ordinal))
            return false;
        if (!string.Equals(stored.TargetSymbolId, query.TargetSymbolId, StringComparison.Ordinal))
            return false;
        if (stored.TargetVersionId != query.TargetVersionId)
            return false;
        if (stored.Tier != query.Tier)
            return false;
        if (!string.Equals(stored.Method, query.Method, StringComparison.Ordinal))
            return false;
        return Fmt(stored.Confidence) == Fmt(query.Confidence);
    }

    private static bool IsProducerUnderResolution(StoredResolution stored, QueryResolution query) =>
        stored.Outcome == "missing" && query.Outcome is "resolved" or "ambiguous";

    private static StoredResolution MissingStore() =>
        new("missing", null, null, null, null, null, null);

    private static string Describe(
        string kind,
        long versionId,
        string id,
        string name,
        StoredResolution stored,
        QueryResolution query) =>
        $"{kind} {versionId}/{id} name={name} store={Format(stored)} query={Format(query)}";

    private static string Format(StoredResolution row) =>
        $"{row.Outcome}/{row.TargetVersionId}/{row.TargetSymbolId}/{row.Tier}/{row.Method}/{Fmt(row.Confidence)}";

    private static string Format(QueryResolution row) =>
        $"{row.Outcome}/{row.TargetVersionId}/{row.TargetSymbolId}/{row.Tier}/{row.Method}/{Fmt(row.Confidence)}";

    private static string OutcomeName(ResolutionOutcomeKind kind) => kind switch
    {
        ResolutionOutcomeKind.Resolved => "resolved",
        ResolutionOutcomeKind.Ambiguous => "ambiguous",
        ResolutionOutcomeKind.Missing => "missing",
        _ => "no_context",
    };

    private static string FormatInbound(string current, ReferenceEvidence row) =>
        string.Join(
            '|',
            "in",
            current,
            row.ContainingSymbolId,
            row.SourceKind,
            SourceName(row.Source),
            Fmt(row.Confidence),
            row.ResolutionTier?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

    private static string FormatOutgoing(OutgoingReferenceEvidence row) =>
        string.Join(
            '|',
            "out",
            row.ContainingSymbolId,
            row.TargetSymbolId ?? row.TargetName,
            row.SourceKind,
            SourceName(row.Source),
            Fmt(row.Confidence),
            row.ResolutionTier?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

    private static string SourceName(ReferenceEvidenceSource source) => source switch
    {
        ReferenceEvidenceSource.IdentifierResolution => "identifier_resolution",
        ReferenceEvidenceSource.PendingResolution => "pending_resolution",
        ReferenceEvidenceSource.Relationship => "relationship",
        ReferenceEvidenceSource.NameFallback => "name_fallback",
        _ => source.ToString(),
    };

    private static string Fmt(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Fmt(double? value) => value is null ? string.Empty : Fmt(value.Value);

    private static string? SiblingBase(string storePath)
    {
        string? dir = Path.GetDirectoryName(storePath);
        if (dir is null)
            return null;
        string sibling = Path.Combine(dir, "base.db");
        return File.Exists(sibling) ? sibling : null;
    }

    private static string? ReadMeta(SqliteConnection connection, string key)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM store_meta WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static void BindVisibility(SqliteCommand command, StoreVisibility visibility)
    {
        command.Parameters.AddWithValue("$view_id", visibility.ViewId);
        command.Parameters.AddWithValue("$generation", visibility.ManifestGeneration);
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
}
