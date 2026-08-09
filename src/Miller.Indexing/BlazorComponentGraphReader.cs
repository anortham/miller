using System.Text.Json;
using Miller.Core.Contracts;
using Miller.Core.Graph;
using Miller.Indexing.Reads;

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
        if (!ContainsComponentReference(facts))
            return [];

        using LegacyArtifactReadSession session = LegacyArtifactReadSession.Open(dbPath);
        return ReadSession(session, facts);
    }

    public static IReadOnlyList<GraphEdge> ReadSession(
        IWorkspaceReadSession session,
        IReadOnlyList<StructuralFactRecord> facts)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(facts);

        if (!ContainsComponentReference(facts))
            return [];

        var evidence = session.Read(ReadEvidence);
        var namespaces = BlazorNamespaceCatalog.Build(
            session.Snapshot.WorkspaceRoot,
            evidence.Components,
            evidence.Directives);
        var byName = evidence.Components
            .GroupBy(component => component.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var byQualifiedName = evidence.Components
            .SelectMany(component => namespaces.QualifiedNames(component).Select(name => (Name: name, Component: component)))
            .GroupBy(candidate => candidate.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(candidate => candidate.Component).DistinctBy(component => component.Id).ToArray(),
                StringComparer.Ordinal);
        var byPath = evidence.Components
            .GroupBy(component => component.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var edges = new List<GraphEdge>();
        foreach (var fact in facts)
        {
            if (!string.Equals(
                    fact.PatternId,
                    BridgeStructuralPatterns.BlazorComponentReference,
                    StringComparison.Ordinal)
                || !string.Equals(Path.GetExtension(fact.Path), ".razor", StringComparison.OrdinalIgnoreCase)
                || !fact.Metadata.TryGetValue("tag", out var tag)
                || string.IsNullOrWhiteSpace(tag)
                || !fact.Metadata.TryGetValue("containing_component", out var containingComponent)
                || string.IsNullOrWhiteSpace(containingComponent)
                || !TryResolveSource(byPath, namespaces, fact.Path, containingComponent, out var source)
                || !TryResolveTarget(fact, source, tag, byName, byQualifiedName, namespaces, out var target)
                || string.Equals(source.Id, target.Id, StringComparison.Ordinal))
            {
                continue;
            }

            edges.Add(new GraphEdge(source.Id, target.Id, "uses"));
        }

        return edges;
    }

    private static bool ContainsComponentReference(IReadOnlyList<StructuralFactRecord> facts) =>
        facts.Any(fact => string.Equals(
            fact.PatternId,
            BridgeStructuralPatterns.BlazorComponentReference,
            StringComparison.Ordinal));

    private static BlazorEvidence ReadEvidence(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT symbol_id, path, name, kind, metadata_json
            FROM symbols
            WHERE metadata_json IS NOT NULL AND kind IN ('class', 'import')
            ORDER BY path, start_line, symbol_id;
            """;

        var components = new List<BlazorComponentIdentity>();
        var directives = new List<RazorImportDirective>();
        using var reader = command.ExecuteReader();
        int idOrdinal = reader.GetOrdinal("symbol_id");
        int pathOrdinal = reader.GetOrdinal("path");
        int nameOrdinal = reader.GetOrdinal("name");
        int kindOrdinal = reader.GetOrdinal("kind");
        int metadataOrdinal = reader.GetOrdinal("metadata_json");
        while (reader.Read())
        {
            string? path = BlazorNamespaceCatalog.NormalizePath(reader.GetString(pathOrdinal));
            if (path is null)
                continue;

            string kind = reader.GetString(kindOrdinal);
            string metadataJson = reader.GetString(metadataOrdinal);
            if (string.Equals(kind, "class", StringComparison.Ordinal)
                && TryReadComponentQualifiedName(metadataJson, out var qualifiedName))
            {
                components.Add(new BlazorComponentIdentity(
                    reader.GetString(idOrdinal),
                    path,
                    reader.GetString(nameOrdinal),
                    qualifiedName));
            }
            else if (string.Equals(kind, "import", StringComparison.Ordinal)
                     && TryReadDirective(metadataJson, out var directiveName, out var directiveValue))
            {
                directives.Add(new RazorImportDirective(path, directiveName, directiveValue));
            }
        }

        return new BlazorEvidence(components, directives);
    }

    private static bool TryReadComponentQualifiedName(string metadataJson, out string qualifiedName)
    {
        qualifiedName = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
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

    private static bool TryReadDirective(
        string metadataJson,
        out string directiveName,
        out string directiveValue)
    {
        directiveName = string.Empty;
        directiveValue = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || !string.Equals(type.GetString(), "razor-directive", StringComparison.Ordinal)
                || !document.RootElement.TryGetProperty("directiveName", out var nameElement)
                || nameElement.ValueKind != JsonValueKind.String
                || !document.RootElement.TryGetProperty("directiveValue", out var valueElement)
                || valueElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            directiveName = nameElement.GetString() ?? string.Empty;
            directiveValue = valueElement.GetString() ?? string.Empty;
            return directiveName.Length > 0 && directiveValue.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryResolveSource(
        IReadOnlyDictionary<string, BlazorComponentIdentity[]> byPath,
        BlazorNamespaceCatalog namespaces,
        string path,
        string containingComponent,
        out BlazorComponentIdentity source)
    {
        source = default!;
        string? normalizedPath = BlazorNamespaceCatalog.NormalizePath(path);
        if (normalizedPath is null || !byPath.TryGetValue(normalizedPath, out var pathComponents))
            return false;

        var candidates = pathComponents
            .Where(component =>
                string.Equals(component.Name, containingComponent, StringComparison.Ordinal)
                || namespaces.QualifiedNames(component).Contains(containingComponent, StringComparer.Ordinal))
            .ToArray();
        if (candidates.Length != 1)
            return false;

        source = candidates[0];
        return true;
    }

    private static bool TryResolveTarget(
        StructuralFactRecord fact,
        BlazorComponentIdentity source,
        string tag,
        IReadOnlyDictionary<string, BlazorComponentIdentity[]> byName,
        IReadOnlyDictionary<string, BlazorComponentIdentity[]> byQualifiedName,
        BlazorNamespaceCatalog namespaces,
        out BlazorComponentIdentity target)
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

        var effectiveNamespaces = namespaces.EffectiveNamespaces(source, ReadNamespaceContext(fact));
        var contextualCandidates = candidates
            .Where(candidate => namespaces.QualifiedNames(candidate).Any(qualifiedName =>
                effectiveNamespaces.Any(ns =>
                    string.Equals(qualifiedName, ns + "." + tag, StringComparison.Ordinal))))
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

    private sealed record BlazorEvidence(
        IReadOnlyList<BlazorComponentIdentity> Components,
        IReadOnlyList<RazorImportDirective> Directives);
}
