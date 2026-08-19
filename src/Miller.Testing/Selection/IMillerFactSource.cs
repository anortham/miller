using Miller.Indexing.Testing;

namespace Miller.Testing;

/// <summary>
/// Testing-facing alias of <see cref="ICtFactSource"/>. The implementation lives in Indexing so
/// Miller.Indexing never references Miller.Testing.
/// </summary>
public interface IMillerFactSource : ICtFactSource
{
    CtFreshnessKey Freshness => new(Current.IndexIdentity, Current.Revision);
}

/// <summary>Thin wrapper that exposes <see cref="CtFreshnessKey"/> over an Indexing fact source.</summary>
public sealed class MillerFactSource : IMillerFactSource
{
    private readonly ICtFactSource _inner;

    public MillerFactSource(ICtFactSource inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public CtIndexCursor Current => _inner.Current;

    public CtFreshnessKey Freshness => new(Current.IndexIdentity, Current.Revision);

    public IReadOnlyList<CtSymbolFact> SymbolsForChangedFiles(IReadOnlyList<string> changedPaths) =>
        _inner.SymbolsForChangedFiles(changedPaths);

    public IReadOnlyList<CtReferenceFact> ReferencesTo(IReadOnlyList<string> symbolIds) =>
        _inner.ReferencesTo(symbolIds);

    public IReadOnlyList<CtReferenceFact> IdentifierEvidenceTo(IReadOnlyList<string> symbolIds) =>
        _inner.IdentifierEvidenceTo(symbolIds);

    public CtImpactResult Impact(IReadOnlyList<string> seedSymbolIds, int maxDepth = 2, int limit = 100) =>
        _inner.Impact(seedSymbolIds, maxDepth, limit);
}

/// <summary>
/// Opens a fresh fact source for every read so a promoted generation is visible.
/// </summary>
public sealed class ReopeningMillerFactSource : IMillerFactSource
{
    private readonly Func<IMillerFactSource> _open;

    public ReopeningMillerFactSource(Func<IMillerFactSource> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        _open = open;
    }

    public CtIndexCursor Current => With(static facts => facts.Current);

    public CtFreshnessKey Freshness =>
        With(static facts => new CtFreshnessKey(facts.Current.IndexIdentity, facts.Current.Revision));

    public IReadOnlyList<CtSymbolFact> SymbolsForChangedFiles(IReadOnlyList<string> changedPaths) =>
        With(facts => facts.SymbolsForChangedFiles(changedPaths));

    public IReadOnlyList<CtReferenceFact> ReferencesTo(IReadOnlyList<string> symbolIds) =>
        With(facts => facts.ReferencesTo(symbolIds));

    public IReadOnlyList<CtReferenceFact> IdentifierEvidenceTo(IReadOnlyList<string> symbolIds) =>
        With(facts => facts.IdentifierEvidenceTo(symbolIds));

    public CtImpactResult Impact(IReadOnlyList<string> seedSymbolIds, int maxDepth = 2, int limit = 100) =>
        With(facts => facts.Impact(seedSymbolIds, maxDepth, limit));

    private T With<T>(Func<IMillerFactSource, T> read)
    {
        IMillerFactSource facts = _open();
        try
        {
            return read(facts);
        }
        finally
        {
            (facts as IDisposable)?.Dispose();
        }
    }
}
