using Miller.Indexing;

namespace Miller.Server.Hosting;

/// <summary>
/// The production rebuild factory for <see cref="FreshnessService"/> (decision-5): a full re-read of the
/// extract DB (<see cref="SqliteSymbolReader.Read"/>) followed by a fresh <see cref="MillerRepositoryIndex.Build"/>.
/// Each call produces a brand-new immutable index so the holder's atomic reference swap is torn-state-free and
/// structurally satisfies the symbol-ID-churn rule (the whole resolved index is replaced — no stale link keyed
/// on a churned id survives). Incremental rebuild is the measured-latency-gated optimization (decision-5); this
/// is the correct, simple default.
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
    /// Read the extract DB afresh and build a new immutable index over its current contents.
    /// </summary>
    /// <exception cref="FileNotFoundException">The DB file does not exist.</exception>
    public MillerRepositoryIndex Rebuild() => MillerRepositoryIndex.Build(SqliteSymbolReader.Read(_dbPath));
}
