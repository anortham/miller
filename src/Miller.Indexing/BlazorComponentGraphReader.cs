using System.Text.Json;
using Miller.Core.Contracts;
using Miller.Core.Graph;

namespace Miller.Indexing;

/// <summary>
/// Resolves Blazor component-reference facts to evidence-backed component dependency edges.
/// </summary>
public static class BlazorComponentGraphReader
{
    /// <summary>
    /// Reads Razor component identities from <paramref name="dbPath"/> and resolves component-reference facts.
    /// </summary>
    public static IReadOnlyList<GraphEdge> Read(
        string dbPath,
        IReadOnlyList<StructuralFactRecord> facts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentNullException.ThrowIfNull(facts);

        var components = ReadComponents(dbPath);
        var byName = components
            .GroupBy(component => component.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var byQualifiedName = components
            .GroupBy(component => component.QualifiedName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var byPath = components
            .GroupBy(component => component.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var edges = new List<GraphEdge>();
        foreach (var fact in facts)
        {
            if (!string.Equals(
                    fact.PatternId,
                    BridgeStructuralPatterns.BlazorComponentReference,
                    StringComparison.Ordinal)
                || !fact.Metadata.TryGetValue("tag", out var tag)
                || string.IsNullOrWhiteSpace(tag)
                || !fact.Metadata.TryGetValue("containing_component", out var containingComponent)
                || string.IsNullOrWhiteSpace(containingComponent)
                || !TryResolveSource(byPath, fact.Path, containingComponent, out var source)
                || !TryResolveTarget(fact, tag, byName, byQualifiedName, out var target)
                || string.Equals(source.Id, target.Id, StringComparison.Ordinal))
            {
                continue;
            }

            edges.Add(new GraphEdge(source.Id, target.Id, "uses"));
        }

        return edges;
    }

    private static IReadOnlyList<ComponentSymbol> ReadComponents(string dbPath)
    {
        using var connection = SqliteReadOnlyAccess.Open(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT symbol_id, path, name, metadata_json
            FROM symbols
            WHERE kind = 'class' AND metadata_json IS NOT NULL
            ORDER BY path, start_line, symbol_id;
            """;

        var components = new List<ComponentSymbol>();
        using var reader = command.ExecuteReader();
        int idOrdinal = reader.GetOrdinal("symbol_id");
        int pathOrdinal = reader.GetOrdinal("path");
        int nameOrdinal = reader.GetOrdinal("name");
        int metadataOrdinal = reader.GetOrdinal("metadata_json");
        while (reader.Read())
        {
            if (TryReadQualifiedName(reader.GetString(metadataOrdinal), out var qualifiedName))
            {
                components.Add(new ComponentSymbol(
                    reader.GetString(idOrdinal),
                    reader.GetString(pathOrdinal),
                    reader.GetString(nameOrdinal),
                    qualifiedName));
            }
        }

        return components;
    }

    private static bool TryReadQualifiedName(string metadataJson, out string qualifiedName)
    {
        qualifiedName = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("type", out var type)
                || !string.Equals(type.GetString(), "razor-component", StringComparison.Ordinal)
                || !document.RootElement.TryGetProperty("qualifiedName", out var qualifiedNameElement)
                || qualifiedNameElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            qualifiedName = qualifiedNameElement.GetString() ?? string.Empty;
            return qualifiedName.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryResolveSource(
        IReadOnlyDictionary<string, ComponentSymbol[]> byPath,
        string path,
        string containingComponent,
        out ComponentSymbol source)
    {
        source = default!;
        if (!byPath.TryGetValue(path, out var pathComponents))
            return false;

        var candidates = pathComponents
            .Where(component =>
                string.Equals(component.Name, containingComponent, StringComparison.Ordinal)
                || string.Equals(component.QualifiedName, containingComponent, StringComparison.Ordinal))
            .ToArray();
        if (candidates.Length != 1)
            return false;

        source = candidates[0];
        return true;
    }

    private static bool TryResolveTarget(
        StructuralFactRecord fact,
        string tag,
        IReadOnlyDictionary<string, ComponentSymbol[]> byName,
        IReadOnlyDictionary<string, ComponentSymbol[]> byQualifiedName,
        out ComponentSymbol target)
    {
        target = default!;
        if (tag.Contains('.', StringComparison.Ordinal))
        {
            if (!byQualifiedName.TryGetValue(tag, out var qualifiedCandidates)
                || qualifiedCandidates.Length != 1)
            {
                return false;
            }

            target = qualifiedCandidates[0];
            return true;
        }

        if (!byName.TryGetValue(tag, out var candidates))
            return false;

        if (candidates.Length == 1)
        {
            target = candidates[0];
            return true;
        }

        var namespaces = ReadNamespaceContext(fact);
        var contextualCandidates = candidates
            .Where(candidate => namespaces.Any(ns =>
                string.Equals(candidate.QualifiedName, ns + "." + tag, StringComparison.Ordinal)))
            .DistinctBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToArray();
        if (contextualCandidates.Length != 1)
            return false;

        target = contextualCandidates[0];
        return true;
    }

    private static IReadOnlyList<string> ReadNamespaceContext(StructuralFactRecord fact)
    {
        if (!fact.Metadata.TryGetValue("namespace_context", out var namespaceContext)
            || string.IsNullOrWhiteSpace(namespaceContext))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(namespaceContext);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            return document.RootElement
                .EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record ComponentSymbol(string Id, string Path, string Name, string QualifiedName);
}
