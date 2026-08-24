namespace Miller.Core.Resolution;

/// <summary>Pure in-memory facts the resolver reads. The caller owns I/O and indexing.</summary>
public interface IResolutionFacts
{
    IEnumerable<FactSymbol> SymbolsNamed(string name);
    FactSymbol? Symbol(FactSymbolKey key);
    IReadOnlyList<FactSymbol> ChildrenOf(FactSymbolKey parent);
    IReadOnlyList<FactSymbol> TopLevelOf(long versionId);
    IReadOnlyList<FactTypeFact> TypeFactsOf(FactSymbolKey symbol);
    IReadOnlyList<ImportBinding> ImportsOf(long versionId);
    IReadOnlyList<QmlVisibleType> QmlTypesVisibleTo(long versionId) => [];
}
