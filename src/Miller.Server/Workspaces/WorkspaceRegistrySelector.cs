using Miller.Indexing;
using Miller.Server.Tools;

namespace Miller.Server.Workspaces;

/// <summary>
/// What the resolved row is for. A read may break an otherwise ambiguous match by root presence; a mutation
/// may not. <c>workspace remove</c> resolves the same selectors, and there the dead row is what the caller
/// means — breaking the tie toward the live root would delete the wrong workspace's index and store view.
///
/// <para>There is deliberately NO default. A caller that says nothing would inherit <see cref="Read"/>, and a
/// mutating caller that inherits it silently opts its writes into the guess — which is how a destructive path
/// once reached the tie-break by pre-resolving its row before the routine that guards it.</para>
/// </summary>
internal enum WorkspaceSelectorIntent
{
    Read,
    Mutate,
}

internal static class WorkspaceRegistrySelector
{
    public static WorkspaceRegistryRow Resolve(
        WorkspaceRegistry registry,
        string selector,
        WorkspaceSelectorIntent intent)
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
            return SingleLiveMatch(exactDisplayMatches, intent) ?? throw Ambiguous(trimmed, exactDisplayMatches);

        if (Path.IsPathRooted(trimmed))
        {
            var pathMatches = rows
                .Where(row => WorkspaceSafety.IsLiveWorkspace(trimmed, row.CanonicalRoot))
                .GroupBy(row => row.WorkspaceId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            if (pathMatches.Length == 1)
                return pathMatches[0];
            if (pathMatches.Length > 1)
                throw Ambiguous(trimmed, pathMatches);
        }

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
            return SingleLiveMatch(prefixMatches, intent) ?? throw Ambiguous(trimmed, prefixMatches);

        throw new KeyNotFoundException(
            $"unknown workspace selector '{trimmed}'. Use workspace(operation=\"list\") to see display IDs; " +
            "selectors accept display_id, unique prefix, full workspace_id, registered root path, current, or primary.");
    }

    /// <summary>
    /// Breaks an otherwise ambiguous match set by root presence, and returns null unless exactly one candidate
    /// root is present on disk. Null keeps the caller on the ambiguity it would have reported anyway, so a tie
    /// between live roots and a tie between dead roots both behave as they always have. A mutation never breaks
    /// the tie: <c>Directory.Exists</c> answers false on a permission fault as well as on absence, and picking a
    /// delete target from that guess is exactly the mistake the ambiguity refusal exists to prevent.
    /// </summary>
    private static WorkspaceRegistryRow? SingleLiveMatch(
        IReadOnlyList<WorkspaceRegistryRow> matches,
        WorkspaceSelectorIntent intent)
    {
        if (intent != WorkspaceSelectorIntent.Read)
            return null;

        WorkspaceRegistryRow? live = null;
        foreach (WorkspaceRegistryRow row in matches)
        {
            if (!Directory.Exists(row.CanonicalRoot))
                continue;
            if (live is not null)
                return null;
            live = row;
        }

        return live;
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
