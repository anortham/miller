using Miller.Indexing.Testing;
using Miller.Testing.Providers.Jvm;

namespace Miller.Testing;

internal sealed class JvmNativeDeclarationResolver
{
    internal sealed record Binding(CtSymbolFact Declaration, IReadOnlyList<string> SymbolIds);

    internal static string ClassLeaf(string className) => className.Split('.', '$')[^1];

    private readonly IReadOnlyDictionary<string, CtSymbolFact[]> _types;
    private readonly IReadOnlyDictionary<string, CtSymbolFact[]> _members;
    private readonly IReadOnlyDictionary<string, CtFileFact> _files;

    internal JvmNativeDeclarationResolver(IReadOnlyList<CtSymbolFact> symbols, IReadOnlyList<CtFileFact> files)
    {
        var byId = symbols.ToDictionary(symbol => symbol.SymbolId, StringComparer.Ordinal);
        var packages = symbols.Where(symbol => symbol.Kind == "namespace")
            .GroupBy(symbol => symbol.FilePath, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(symbol => symbol.Name).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        _types = symbols.Where(symbol => IsType(symbol) && symbol.Language is "java" or "kotlin" or "scala")
            .Select(symbol => (Symbol: symbol, Qualified: QualifiedType(symbol, packages, byId)))
            .Where(row => row.Qualified is not null).GroupBy(row => row.Qualified!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(row => row.Symbol).ToArray(), StringComparer.Ordinal);
        _members = symbols.Select(symbol => (Symbol: symbol, Type: NearestType(symbol, byId)))
            .Where(row => row.Type is not null).GroupBy(row => row.Type!.SymbolId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(row => row.Symbol).ToArray(), StringComparer.Ordinal);
        _files = files.GroupBy(file => file.Path, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    internal Binding? Resolve(JvmTestCaseIdentity identity)
    {
        if (!_types.TryGetValue(identity.ClassName, out var types) || types.Length != 1) return null;
        CtSymbolFact type = types[0];
        if (!_files.TryGetValue(type.FilePath, out var file) || !file.EvidenceAvailable || file.HasParseDiagnostics
            || !string.Equals(file.Status, "indexed", StringComparison.OrdinalIgnoreCase)) return null;
        CtSymbolFact[] members = _members.GetValueOrDefault(type.SymbolId) ?? [];
        if (identity.MethodName == JvmTestBackendIds.ClassCaseSentinel)
            return new(type, new[] { type.SymbolId }.Concat(members.Where(member => member.IsTest)
                .Select(member => member.SymbolId)).ToArray());
        CtSymbolFact[] methods = members.Where(member => member.Name == identity.MethodName
            && member.Kind is "method" or "function" && member.IsTest).ToArray();
        return methods.Length == 1 ? new(methods[0], [methods[0].SymbolId]) : null;
    }

    private static bool IsType(CtSymbolFact symbol) => symbol.Kind is "class" or "interface" or "enum" or "struct";

    private static CtSymbolFact? NearestType(CtSymbolFact symbol, IReadOnlyDictionary<string, CtSymbolFact> byId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Count < 128 && symbol.ParentId is { } parentId && visited.Add(parentId) && byId.TryGetValue(parentId, out var parent))
        {
            if (parent.FilePath != symbol.FilePath) return null;
            if (IsType(parent)) return parent;
            symbol = parent;
        }
        return null;
    }

    private static string? QualifiedType(CtSymbolFact type, IReadOnlyDictionary<string, string[]> packages,
        IReadOnlyDictionary<string, CtSymbolFact> byId)
    {
        var classes = new List<string> { type.Name };
        var namespaces = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { type.SymbolId };
        CtSymbolFact current = type;
        while (current.ParentId is { } parentId)
        {
            if (visited.Count >= 128 || !visited.Add(parentId) || !byId.TryGetValue(parentId, out var parent) || parent.FilePath != type.FilePath)
                return null;
            if (IsType(parent)) classes.Add(parent.Name);
            else if (parent.Kind == "namespace") namespaces.Add(parent.Name);
            else return null;
            current = parent;
        }
        if (namespaces.Count == 0)
        {
            string[] declaredPackages = packages.GetValueOrDefault(type.FilePath) ?? [];
            if (declaredPackages.Length > 1) return null;
            namespaces.AddRange(declaredPackages);
        }
        classes.Reverse();
        namespaces.Reverse();
        string package = string.Join('.', namespaces);
        return (package.Length == 0 ? string.Empty : package + ".") + string.Join('$', classes);
    }
}
