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
