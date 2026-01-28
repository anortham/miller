using uniffi.codesearch_ffi;

namespace Codesearch.Server.Services;

/// <summary>
/// Service wrapping the Rust search engine.
/// </summary>
internal class SearchService : IDisposable
{
    private readonly CodeSearchEngine _engine;
    private readonly string _dbPath;
    private bool _disposed;

    public SearchService()
    {
        // Default to .codesearch in current directory
        var workspaceRoot = Environment.CurrentDirectory;
        var codesearchDir = Path.Combine(workspaceRoot, ".codesearch");
        Directory.CreateDirectory(codesearchDir);

        _dbPath = Path.Combine(codesearchDir, "index.lance");
        _engine = new CodeSearchEngine(_dbPath);
    }

    public SearchService(string dbPath)
    {
        _dbPath = dbPath;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        _engine = new CodeSearchEngine(dbPath);
    }

    public string DbPath => _dbPath;

    public bool HealthCheck() => _engine.HealthCheck();

    public ulong SymbolCount() => _engine.SymbolCount();

    public List<SearchResultOutput> SearchText(string query, uint limit = 20)
    {
        return _engine.SearchTextBoosted(query, limit);
    }

    public List<SearchResultOutput> SearchVector(List<float> queryVector, uint limit = 20)
    {
        return _engine.SearchVector(queryVector, limit);
    }

    public List<SearchResultOutput> SearchHybrid(string query, List<float> queryVector, uint limit = 20)
    {
        return _engine.SearchHybridBoosted(query, queryVector, limit);
    }

    public void CreateFtsIndex()
    {
        _engine.CreateFtsIndex();
    }

    public ulong AddSymbols(List<SymbolInput> symbols, List<List<float>> vectors)
    {
        return _engine.AddSymbols(symbols, vectors);
    }

    /// <summary>
    /// Add relationships to the database.
    /// </summary>
    public ulong AddRelationships(List<uniffi.codesearch_ffi.RelationshipInput> relationships)
    {
        return _engine.AddRelationships(relationships);
    }

    /// <summary>
    /// Add identifiers to the database.
    /// </summary>
    public ulong AddIdentifiers(List<uniffi.codesearch_ffi.IdentifierInput> identifiers)
    {
        return _engine.AddIdentifiers(identifiers);
    }

    /// <summary>
    /// Get identifier count.
    /// </summary>
    public ulong IdentifierCount()
    {
        return _engine.IdentifierCount();
    }

    /// <summary>
    /// Get relationship count.
    /// </summary>
    public ulong RelationshipCount()
    {
        return _engine.RelationshipCount();
    }

    /// <summary>
    /// Get symbols that call the given symbol.
    /// </summary>
    public List<uniffi.codesearch_ffi.RelationshipResult> GetCallers(string symbolId, uint limit = 50)
    {
        return _engine.GetCallers(symbolId, limit);
    }

    /// <summary>
    /// Get symbols that the given symbol calls.
    /// </summary>
    public List<uniffi.codesearch_ffi.RelationshipResult> GetCallees(string symbolId, uint limit = 50)
    {
        return _engine.GetCallees(symbolId, limit);
    }

    /// <summary>
    /// Get all relationships for a symbol.
    /// </summary>
    public List<uniffi.codesearch_ffi.RelationshipResult> GetRelationships(string symbolId, uint limit = 100)
    {
        return _engine.GetRelationships(symbolId, limit);
    }

    /// <summary>
    /// Clear reachability table.
    /// </summary>
    public void ClearReachability()
    {
        _engine.ClearReachability();
    }

    /// <summary>
    /// Add reachability entries in batch.
    /// </summary>
    public ulong AddReachabilityBatch(List<uniffi.codesearch_ffi.ReachabilityEntry> entries)
    {
        return _engine.AddReachabilityBatch(entries);
    }

    /// <summary>
    /// Get all relationships of a specific kind.
    /// </summary>
    /// <remarks>
    /// Note: This is a stub that returns empty - full implementation requires engine-level support
    /// for querying all relationships without a specific symbol ID.
    /// </remarks>
    public List<uniffi.codesearch_ffi.RelationshipResult> GetAllRelationshipsByKind(string kind, int limit = 100000)
    {
        // Query relationships filtering by kind
        // Note: This may need engine-level support. For now, return empty.
        return new List<uniffi.codesearch_ffi.RelationshipResult>();
    }

    /// <summary>
    /// Get impacted symbols (what breaks if I change this?).
    /// </summary>
    public List<uniffi.codesearch_ffi.ImpactResult> GetImpacted(string symbolId, uint maxDistance = 10)
    {
        return _engine.GetImpacted(symbolId, maxDistance);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _engine.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
