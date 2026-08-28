using Miller.Core.Search;
using Miller.Indexing;
using Miller.Indexing.Reads;

namespace Miller.Server.Workspaces;

/// <summary>
/// The named-read route's guard for a search sidecar that is one or more store sequences BEHIND the live
/// generation. Search renders self-contained rows and can serve such a sidecar as it is. A named read cannot:
/// every consumer behind <c>WorkspaceReadContext</c> and <c>WorkspaceSymbolReadContext</c> JOINS on the symbol id
/// the lookup returned — the live <c>SqliteSymbolGraphIndex</c>, the live bridge graph, <c>ExtractReader</c>,
/// <c>ReferenceEvidenceReader</c>, and the impact seed set all read the LIVE artifact. An id the lagging sidecar
/// minted and the live generation no longer holds therefore comes back as "nothing depends on this symbol" or
/// "zero references" — a confident wrong answer, not an honest unavailable.
///
/// <para>So this wrapper keeps the sidecar as the RECALL surface and takes every fact from the live artifact: it
/// re-reads each returned row's file from the live session and answers with the LIVE row of the same symbol id.
/// A row whose id the live generation does not hold is DROPPED, so the read reports "not found" instead of
/// joining a dead id. Line spans come back live too, which matters because <c>ImpactTool.SeedFromDiff</c> picks
/// seeds by intersecting the working-tree diff against <c>IndexedSymbol.StartLine</c>/<c>EndLine</c>.</para>
///
/// <para>Ids are never rewritten — only verified — so a chained <c>FindChildren</c>/<c>FindBySymbolId</c> still
/// reaches the sidecar with an id it knows. Path-level answers (<c>KnownExtensions</c>, <c>IsIndexedFilePath</c>,
/// <c>ResolveIndexedFilePath</c>, <c>FindFilePathsByFragment</c>, <c>DocumentCount</c>) carry no id and pass
/// through; they can name a path the lag window deleted, exactly as search can today.</para>
///
/// <para>The live rows are read one batch per lookup call and cached per file for the life of this instance, so
/// the wrapper costs a bounded read over the files a read actually touched — not the whole-generation
/// <see cref="SymbolSearchProjection"/> rebuild the exact-stamp rule used to force.</para>
/// </summary>
internal sealed class LaggingSidecarSymbolLookup : ISymbolLookupIndex
{
    private readonly ISymbolLookupIndex _sidecar;
    private readonly IWorkspaceReadSession _live;
    private readonly Dictionary<string, IReadOnlyDictionary<string, IndexedSymbol>> _liveFiles =
        new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private LaggingSidecarSymbolLookup(ISymbolLookupIndex sidecar, IWorkspaceReadSession live)
    {
        _sidecar = sidecar;
        _live = live;
    }

    /// <summary>
    /// Verify against the live artifact only when the served sidecar LAGS it. A sidecar stamped at the live
    /// snapshot minted the same ids the consumers read, so the fresh case pays nothing.
    /// </summary>
    public static ISymbolLookupIndex Wrap(
        ISymbolLookupIndex sidecar,
        bool servedStampLagsLive,
        IWorkspaceReadSession live)
    {
        ArgumentNullException.ThrowIfNull(sidecar);
        ArgumentNullException.ThrowIfNull(live);
        return servedStampLagsLive ? new LaggingSidecarSymbolLookup(sidecar, live) : sidecar;
    }

    public int DocumentCount => _sidecar.DocumentCount;

    public IReadOnlySet<string> KnownExtensions => _sidecar.KnownExtensions;

    public IReadOnlyList<SearchHit> Search(string query, int limit = 10, SearchMode mode = SearchMode.Or)
    {
        IReadOnlyList<SearchHit> hits = _sidecar.Search(query, limit, mode);
        if (hits.Count == 0)
            return hits;

        var resolved = new IndexedSymbol[hits.Count];
        for (int i = 0; i < hits.Count; i++)
            resolved[i] = _sidecar.Resolve(hits[i].Document.DocId);
        EnsureLiveFiles(resolved.Select(static symbol => symbol.FilePath));

        var kept = new List<SearchHit>(hits.Count);
        for (int i = 0; i < hits.Count; i++)
        {
            if (LiveRow(resolved[i]) is not null)
                kept.Add(hits[i]);
        }
        return kept;
    }

    /// <summary>
    /// The one method that cannot report absence — its contract returns a row, not an option. It answers with the
    /// live row when the id is live and with the sidecar row otherwise, and the callers that reach it
    /// (<c>SymbolSuggestionEngine</c>, search rendering) show a name and a path rather than joining the id.
    /// <see cref="Search"/> already drops the hits whose rows are not live, so a caller that pairs the two never
    /// reaches a dead id through here.
    /// </summary>
    public IndexedSymbol Resolve(int docId)
    {
        IndexedSymbol row = _sidecar.Resolve(docId);
        return LiveRow(row) ?? row;
    }

    public IReadOnlyList<IndexedSymbol> FindByName(string name) => LiveRows(_sidecar.FindByName(name));

    public IndexedSymbol? FindBySymbolId(string symbolId) =>
        _sidecar.FindBySymbolId(symbolId) is { } row ? LiveRow(row) : null;

    public IReadOnlyList<IndexedSymbol> FindChildren(string parentId) =>
        LiveRows(_sidecar.FindChildren(parentId));

    public IReadOnlyList<IndexedSymbol> FindByFilePath(string filePath) =>
        LiveRows(_sidecar.FindByFilePath(filePath));

    public IReadOnlyList<IndexedSymbol> FindByFilePathFragment(string query, int limit) =>
        LiveRows(_sidecar.FindByFilePathFragment(query, limit));

    public IReadOnlyList<string> FindFilePathsByFragment(string query, int limit) =>
        _sidecar.FindFilePathsByFragment(query, limit);

    public bool IsIndexedFilePath(string path) => _sidecar.IsIndexedFilePath(path);

    public string? ResolveIndexedFilePath(string target) => _sidecar.ResolveIndexedFilePath(target);

    private IReadOnlyList<IndexedSymbol> LiveRows(IReadOnlyList<IndexedSymbol> rows)
    {
        if (rows.Count == 0)
            return rows;

        EnsureLiveFiles(rows.Select(static row => row.FilePath));
        var kept = new List<IndexedSymbol>(rows.Count);
        foreach (IndexedSymbol row in rows)
        {
            if (LiveRow(row) is { } live)
                kept.Add(live);
        }
        return kept;
    }

    private IndexedSymbol? LiveRow(IndexedSymbol row)
    {
        EnsureLiveFiles([row.FilePath]);
        IReadOnlyDictionary<string, IndexedSymbol> file;
        lock (_gate)
            file = _liveFiles[row.FilePath];
        return file.TryGetValue(row.SymbolId, out IndexedSymbol? live)
            ? live with { DocId = row.DocId }
            : null;
    }

    private void EnsureLiveFiles(IEnumerable<string> paths)
    {
        List<string>? missing = null;
        lock (_gate)
        {
            foreach (string path in paths)
            {
                if (!_liveFiles.ContainsKey(path))
                    (missing ??= []).Add(path);
            }
        }

        if (missing is null)
            return;

        string[] wanted = missing.Distinct(StringComparer.Ordinal).ToArray();
        ILookup<string, IndexedSymbol> live = SqliteSymbolReader
            .ReadForPaths(_live, wanted)
            .ToLookup(static row => row.FilePath, StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (string path in wanted)
            {
                _liveFiles[path] = live[path]
                    .ToDictionary(static row => row.SymbolId, StringComparer.Ordinal);
            }
        }
    }
}
