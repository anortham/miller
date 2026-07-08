using Miller.Indexing;

namespace Miller.Server.Workspaces;

/// <summary>
/// Registry GC: remove rows whose <c>canonical_root</c> no longer exists on disk. Composes
/// <see cref="WorkspaceRegistry.List"/> + <see cref="Directory.Exists"/> + <see cref="WorkspaceRegistry.Remove"/>
/// without opening any index artifact or spawning julie-extract.
/// </summary>
public static class WorkspaceRegistryPrune
{
    public sealed record Entry(string WorkspaceId, string DisplayId, string Root);

    public sealed record Result(bool DryRun, IReadOnlyList<Entry> Pruned, int Kept);

    public static Result Run(WorkspaceRegistry registry, string? protectedWorkspaceId, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var pruned = new List<Entry>();
        int kept = 0;
        foreach (WorkspaceRegistryRow row in registry.List())
        {
            if (!string.IsNullOrWhiteSpace(protectedWorkspaceId) &&
                string.Equals(row.WorkspaceId, protectedWorkspaceId, StringComparison.Ordinal))
            {
                kept++;
                continue;
            }

            if (Directory.Exists(row.CanonicalRoot))
            {
                kept++;
                continue;
            }

            pruned.Add(new Entry(row.WorkspaceId, row.DisplayId, row.CanonicalRoot));
            if (!dryRun)
                registry.Remove(row.WorkspaceId);
        }

        return new Result(dryRun, pruned, kept);
    }
}
