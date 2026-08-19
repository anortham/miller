using Microsoft.Data.Sqlite;
using Miller.Core.Graph;
using Miller.Core.References;
using Miller.Indexing.Reads;
using Miller.Indexing.Resolution;

namespace Miller.Indexing.Testing;

/// <summary>
/// Typed Miller fact reads for continuous testing. Lives in Indexing so it can load
/// <see cref="RevisionFactCache"/> without <c>InternalsVisibleTo</c>.
/// </summary>
public sealed class CtFactAdapter : ICtFactSource, IDisposable
{
    private readonly IWorkspaceReadSession _session;
    private readonly bool _ownsSession;
    private readonly object _gate = new();
    private QueryTimeResolutionReader? _resolution;
    private MillerRepositoryIndex? _index;
    private bool _disposed;

    public CtFactAdapter(IWorkspaceReadSession session)
        : this(session, ownsSession: false)
    {
    }

    private CtFactAdapter(IWorkspaceReadSession session, bool ownsSession)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _ownsSession = ownsSession;
    }

    public static CtFactAdapter OpenArtifact(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        return new CtFactAdapter(LegacyArtifactReadSession.Open(dbPath), ownsSession: true);
    }

    public CtIndexCursor Current
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            WorkspaceReadSnapshot snapshot = _session.Snapshot;
            long revision = snapshot.Mode == WorkspaceReadMode.FamilyStore
                ? snapshot.Freshness.StoreLogSequence ?? snapshot.Freshness.Revision
                : snapshot.Freshness.Revision;
            return new CtIndexCursor(snapshot.IndexIdentity, revision);
        }
    }

    public IReadOnlyList<CtSymbolFact> SymbolsForChangedFiles(IReadOnlyList<string> changedPaths)
    {
        ArgumentNullException.ThrowIfNull(changedPaths);
        ObjectDisposedException.ThrowIf(_disposed, this);
        IReadOnlyList<string> paths = NormalizeChangedPaths(changedPaths);
        if (paths.Count == 0)
            return [];

        IReadOnlyList<IndexedSymbol> symbols = SqliteSymbolReader.ReadForPaths(_session, paths);
        IReadOnlyDictionary<string, string?> hashes = ReadContentHashes(paths);
        return symbols
            .Select(symbol => ToSymbolFact(symbol, hashes.GetValueOrDefault(symbol.FilePath)))
            .ToArray();
    }

    public IReadOnlyList<CtReferenceFact> ReferencesTo(IReadOnlyList<string> symbolIds)
    {
        ArgumentNullException.ThrowIfNull(symbolIds);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ReadInbound(symbolIds, identifierOnly: false);
    }

    public IReadOnlyList<CtReferenceFact> IdentifierEvidenceTo(IReadOnlyList<string> symbolIds)
    {
        ArgumentNullException.ThrowIfNull(symbolIds);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ReadInbound(symbolIds, identifierOnly: true);
    }

    public CtImpactResult Impact(IReadOnlyList<string> seedSymbolIds, int maxDepth = 2, int limit = 100)
    {
        ArgumentNullException.ThrowIfNull(seedSymbolIds);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (seedSymbolIds.Count == 0)
            return new CtImpactResult([], [], 0, false, false);

        MillerRepositoryIndex index = Index();
        ImpactAnalysisResult computed = ImpactAnalysis.Compute(
            index,
            index.Graph,
            seedSymbolIds,
            maxDepth,
            limit);
        return new CtImpactResult(
            computed.Impacted.Select(ToImpacted).ToArray(),
            computed.Tests.Select(ToImpacted).ToArray(),
            computed.Graph.ReachedCount,
            computed.Graph.TruncatedByDepth,
            computed.Graph.TruncatedByLimit);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsSession)
            _session.Dispose();
    }

    private IReadOnlyList<CtReferenceFact> ReadInbound(IReadOnlyList<string> symbolIds, bool identifierOnly)
    {
        string[] ids = symbolIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
            return [];

        return _session.Read(connection =>
        {
            QueryTimeResolutionReader reader = Resolution(connection);
            Dictionary<string, List<ReferenceEvidence>> inbound = reader.ReadInboundExact(connection, ids);
            var rows = new List<CtReferenceFact>();
            foreach (string id in ids)
            {
                if (!inbound.TryGetValue(id, out List<ReferenceEvidence>? evidence))
                    continue;
                foreach (ReferenceEvidence row in evidence)
                {
                    if (identifierOnly && !IsIdentifierSource(row.Source))
                        continue;
                    rows.Add(ToReferenceFact(row));
                }
            }

            return rows;
        });
    }

    private QueryTimeResolutionReader Resolution(SqliteConnection connection)
    {
        if (_session is WorkspaceReadHandle handle && handle.ResolutionReader is { } fromHandle)
            return fromHandle;
        if (_session is IQueryTimeResolutionHost host)
            return host.Resolution;

        lock (_gate)
        {
            return _resolution ??= new QueryTimeResolutionReader(
                RevisionFactCache.LoadFromArtifact(connection),
                visibility: null);
        }
    }

    private MillerRepositoryIndex Index()
    {
        lock (_gate)
        {
            if (_index is not null)
                return _index;

            IReadOnlyList<IndexedSymbol> symbols = SqliteSymbolReader.ReadSession(_session);
            var nameToIds = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (IndexedSymbol symbol in symbols)
            {
                if (!nameToIds.TryGetValue(symbol.Name, out List<string>? ids))
                    nameToIds[symbol.Name] = ids = new List<string>(1);
                ids.Add(symbol.SymbolId);
            }

            IReadOnlyList<GraphEdge> edges = SymbolGraphReader.ReadSession(
                _session,
                name => nameToIds.TryGetValue(name, out List<string>? ids)
                    ? ids
                    : Array.Empty<string>());
            return _index = MillerRepositoryIndex.Build(symbols, edges);
        }
    }

    private IReadOnlyList<string> NormalizeChangedPaths(IReadOnlyList<string> changedPaths)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        string? root = _session.Snapshot.WorkspaceRoot;
        foreach (string raw in changedPaths)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            string unix = raw.Trim().Replace('\\', '/');
            paths.Add(unix);
            string? relative = Relativize(root, unix);
            if (relative is not null)
                paths.Add(relative);
        }

        return paths.Count == 0 ? [] : paths.Order(StringComparer.Ordinal).ToArray();
    }

    private IReadOnlyDictionary<string, string?> ReadContentHashes(IReadOnlyList<string> paths)
    {
        return _session.Read(connection =>
        {
            var hashes = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (!SqliteSchemaObjects.Exists(connection, "files") || paths.Count == 0)
                return hashes;

            using SqliteCommand command = connection.CreateCommand();
            var placeholders = new string[paths.Count];
            for (int i = 0; i < paths.Count; i++)
            {
                string name = "$p" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                placeholders[i] = name;
                command.Parameters.AddWithValue(name, paths[i]);
            }

            command.CommandText =
                $"SELECT path, content_hash FROM files WHERE path IN ({string.Join(", ", placeholders)})";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string path = reader.GetString(0);
                hashes[path] = reader.IsDBNull(1) ? null : reader.GetString(1);
            }

            return hashes;
        });
    }

    private static string? Relativize(string? workspaceRoot, string path)
    {
        if (string.IsNullOrEmpty(workspaceRoot) || !Path.IsPathRooted(path))
            return null;

        string root = Path.GetFullPath(workspaceRoot).Replace('\\', '/').TrimEnd('/');
        string abs = Path.GetFullPath(path).Replace('\\', '/');
        if (abs.Length > root.Length
            && abs.StartsWith(root, StringComparison.Ordinal)
            && abs[root.Length] == '/')
        {
            return abs[(root.Length + 1)..];
        }

        return null;
    }

    private static CtSymbolFact ToSymbolFact(IndexedSymbol symbol, string? contentHash) =>
        new(
            symbol.SymbolId,
            symbol.Name,
            symbol.Kind,
            symbol.Language,
            symbol.FilePath,
            contentHash,
            symbol.StartLine,
            symbol.EndLine,
            symbol.ParentId,
            symbol.IsTest,
            symbol.Signature);

    private static CtReferenceFact ToReferenceFact(ReferenceEvidence row) =>
        new(
            row.ContainingSymbolId,
            row.TargetSymbolId ?? string.Empty,
            row.SourceKind,
            row.Confidence,
            ProvenanceName(row.Source),
            row.FilePath,
            row.StartLine,
            row.ResolutionStatus);

    private static CtImpactedSymbol ToImpacted(ImpactSymbolHit hit) =>
        new(
            hit.Symbol.SymbolId,
            hit.Symbol.Name,
            hit.Symbol.Kind,
            hit.Symbol.FilePath,
            hit.Symbol.IsTest,
            hit.Evidence.Hop,
            hit.Evidence.EdgeKind,
            hit.Evidence.EdgeSource);

    private static bool IsIdentifierSource(ReferenceEvidenceSource source) =>
        source is ReferenceEvidenceSource.IdentifierResolution
            or ReferenceEvidenceSource.IdentifierDirect
            or ReferenceEvidenceSource.PendingResolution;

    private static string ProvenanceName(ReferenceEvidenceSource source) => source switch
    {
        ReferenceEvidenceSource.IdentifierResolution => "identifier_resolution",
        ReferenceEvidenceSource.IdentifierDirect => "identifier_direct",
        ReferenceEvidenceSource.PendingResolution => "pending_resolution",
        ReferenceEvidenceSource.Relationship => "relationship",
        ReferenceEvidenceSource.NameFallback => "name_fallback",
        _ => source.ToString(),
    };
}
