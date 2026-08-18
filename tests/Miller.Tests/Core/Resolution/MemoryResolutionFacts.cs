using Miller.Core.Resolution;

namespace Miller.Tests.Core.Resolution;

internal static class ResolutionCases
{
    public static ResolutionInput Ident(
        ResolutionRefKind kind,
        string name,
        string language = "csharp",
        long version = 1,
        string? receiver = null,
        string? qualifier = null,
        string? scope = null,
        double confidence = 1.0) =>
        new(ResolutionOrigin.Identifier, kind, language, version, name, receiver, qualifier, scope, confidence);

    public static ResolutionInput Pend(
        ResolutionRefKind kind,
        string name,
        string language = "csharp",
        long version = 1,
        string? receiver = null,
        string? qualifier = null,
        string? scope = null,
        double confidence = 1.0) =>
        new(ResolutionOrigin.Pending, kind, language, version, name, receiver, qualifier, scope, confidence);
}

internal sealed class MemoryResolutionFacts : IResolutionFacts
{
    private readonly Dictionary<FactSymbolKey, FactSymbol> _byKey = [];
    private readonly Dictionary<string, List<FactSymbol>> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<FactSymbolKey, List<FactSymbol>> _children = [];
    private readonly Dictionary<long, List<FactSymbol>> _topLevel = [];
    private readonly Dictionary<FactSymbolKey, List<FactTypeFact>> _typeFacts = [];
    private readonly Dictionary<long, List<ImportBinding>> _imports = [];

    public FactSymbol Add(
        string id,
        string name,
        FactSymbolKind kind,
        string language = "csharp",
        long version = 1,
        string? parentId = null,
        string? signature = null,
        string? visibility = null,
        bool? isStatic = null)
    {
        var key = new FactSymbolKey(version, id);
        FactSymbolKey? parent = parentId is null ? null : new FactSymbolKey(version, parentId);
        var symbol = new FactSymbol(key, name, kind, language, parent, signature, visibility, isStatic);
        _byKey[key] = symbol;
        GetList(_byName, name).Add(symbol);
        if (parent is { } parentKey)
            GetList(_children, parentKey).Add(symbol);
        else
            GetList(_topLevel, version).Add(symbol);
        return symbol;
    }

    public void AddTypeFact(string symbolId, string resolvedType, bool inferred = false, long version = 1)
    {
        GetList(_typeFacts, new FactSymbolKey(version, symbolId)).Add(new FactTypeFact(resolvedType, inferred));
    }

    public void AddImport(ImportBinding import, long version = 1)
    {
        GetList(_imports, version).Add(import);
    }

    public IEnumerable<FactSymbol> SymbolsNamed(string name) =>
        _byName.TryGetValue(name, out List<FactSymbol>? list) ? list : [];

    public FactSymbol? Symbol(FactSymbolKey key) =>
        _byKey.TryGetValue(key, out FactSymbol? symbol) ? symbol : null;

    public IReadOnlyList<FactSymbol> ChildrenOf(FactSymbolKey parent) =>
        _children.TryGetValue(parent, out List<FactSymbol>? list) ? list : [];

    public IReadOnlyList<FactSymbol> TopLevelOf(long versionId) =>
        _topLevel.TryGetValue(versionId, out List<FactSymbol>? list) ? list : [];

    public IReadOnlyList<FactTypeFact> TypeFactsOf(FactSymbolKey symbol) =>
        _typeFacts.TryGetValue(symbol, out List<FactTypeFact>? list) ? list : [];

    public IReadOnlyList<ImportBinding> ImportsOf(long versionId) =>
        _imports.TryGetValue(versionId, out List<ImportBinding>? list) ? list : [];

    private static List<TValue> GetList<TKey, TValue>(Dictionary<TKey, List<TValue>> map, TKey key)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out List<TValue>? list))
        {
            list = [];
            map[key] = list;
        }

        return list;
    }
}
