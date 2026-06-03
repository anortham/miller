using Miller.Indexing;

namespace Miller.Server.Workspaces;

internal static class WorkspaceRegistrySelector
{
    public static WorkspaceRegistryRow Resolve(WorkspaceRegistry registry, string selector)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        string trimmed = selector.Trim();
        if (registry.Get(trimmed) is { } exactId)
            return exactId;

        IReadOnlyList<WorkspaceRegistryRow> rows = registry.List();
        var exactDisplayMatches = rows
            .Where(row => string.Equals(row.DisplayId, trimmed, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (exactDisplayMatches.Length == 1)
            return exactDisplayMatches[0];
        if (exactDisplayMatches.Length > 1)
            throw Ambiguous(trimmed, exactDisplayMatches);

        var prefixMatches = rows
            .Where(row =>
                row.WorkspaceId.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase) ||
                row.DisplayId.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
            .GroupBy(row => row.WorkspaceId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (prefixMatches.Length == 1)
            return prefixMatches[0];
        if (prefixMatches.Length > 1)
            throw Ambiguous(trimmed, prefixMatches);

        throw new KeyNotFoundException(
            $"unknown workspace selector '{trimmed}'. Use workspace(operation=\"list\") to see display IDs; " +
            "selectors accept display_id, unique prefix, full workspace_id, current, or primary.");
    }

    private static KeyNotFoundException Ambiguous(string selector, IReadOnlyList<WorkspaceRegistryRow> matches)
    {
        string options = string.Join(", ", matches.Take(5).Select(row => row.DisplayId));
        if (matches.Count > 5)
            options += $", … {matches.Count - 5} more";
        return new KeyNotFoundException(
            $"ambiguous workspace selector '{selector}'. Matches: {options}. Use a longer display ID or full workspace_id.");
    }
}
