using Miller.Indexing;

namespace Miller.Server.Hosting;

/// <summary>
/// The production rebuild factory for <see cref="FreshnessService"/> (decision-5): a full re-read of the
/// extract DB (symbols + edges) followed by a fresh build, via the single production path
/// <see cref="RepositoryIndexLoader.Load"/> (M5 D9). Each call produces a brand-new immutable index — with its
/// dependency graph attached — so the holder's atomic reference swap is torn-state-free and structurally
/// satisfies the symbol-ID-churn rule (the whole resolved index + graph is replaced — no stale link keyed on a
/// churned id survives). Routing through the loader keeps the rebuild and the bootstrap identical (both get the
/// graph). Incremental rebuild is the measured-latency-gated optimization (decision-5); this is the correct,
/// simple default.
/// </summary>
public sealed class IndexRebuilder
{
    private readonly string _dbPath;

    /// <summary>The extract DB this rebuilder reads on each <see cref="Rebuild"/>.</summary>
    public string DbPath => _dbPath;

    /// <summary>Bind the rebuilder to the Miller-owned extract DB path.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="dbPath"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="dbPath"/> is empty/whitespace.</exception>
    public IndexRebuilder(string dbPath)
    {
        ArgumentNullException.ThrowIfNull(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = dbPath;
    }

    /// <summary>
    /// Read the extract DB afresh (symbols + edges) and build a new immutable index — with its dependency graph
    /// — over its current contents, via the single production path <see cref="RepositoryIndexLoader.Load"/>.
    /// </summary>
    /// <exception cref="FileNotFoundException">The DB file does not exist.</exception>
    public MillerRepositoryIndex Rebuild() => RepositoryIndexLoader.Load(_dbPath);
}
