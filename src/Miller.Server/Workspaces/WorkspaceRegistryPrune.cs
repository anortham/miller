using Miller.Indexing;

namespace Miller.Server.Workspaces;

/// <summary>
/// Registry GC: remove rows whose <c>canonical_root</c> no longer exists on disk. Composes
/// <see cref="WorkspaceRegistry.List"/> + <see cref="Directory.Exists"/> + <see cref="WorkspaceRegistry.Remove"/>
/// without opening any index artifact or spawning julie-extract.
///
/// <para>A pruned row that was a family-store member also owns per-view sidecar files, so the prune reclaims
/// them through <see cref="StoreSidecarReclaim"/>: Miller-owned files only, never <c>store.db</c> or
/// <c>coord.db</c>, and never a view another member still claims. Reclaim is best-effort — a busy lease or a
/// held file leaves the files in place and reports it; the registry row still goes.</para>
/// </summary>
public static class WorkspaceRegistryPrune
{
    public sealed record Entry(
        string WorkspaceId,
        string DisplayId,
        string Root,
        StoreSidecarReclaimResult SidecarReclaim = default);

    public sealed record Result(
        bool DryRun,
        IReadOnlyList<Entry> Pruned,
        int Kept,
        StoreSidecarReclaimResult SidecarReclaim = default);

    public static Result Run(
        WorkspaceRegistry registry,
        string? protectedWorkspaceId,
        bool dryRun,
        Func<string, IDisposable?>? acquireSidecarLease = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var pruned = new List<Entry>();
        var reclaimed = StoreSidecarReclaimResult.None;
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

            if (dryRun)
            {
                pruned.Add(new Entry(row.WorkspaceId, row.DisplayId, row.CanonicalRoot));
                continue;
            }

            // The view id lives in the store_members row, which the workspace delete cascades away. Capture it
            // FIRST, then delete, then reclaim — the reclaim re-reads the members table and spares any view a
            // surviving workspace still claims.
            StoreSidecarReclaimTarget? target = StoreSidecarReclaimTarget.Capture(registry, row.WorkspaceId);
            registry.Remove(row.WorkspaceId);
            StoreSidecarReclaimResult entryReclaim =
                StoreSidecarReclaim.Reclaim(registry, target, acquireSidecarLease);
            reclaimed = StoreSidecarReclaimResult.Combine(reclaimed, entryReclaim);
            pruned.Add(new Entry(row.WorkspaceId, row.DisplayId, row.CanonicalRoot, entryReclaim));
        }

        return new Result(dryRun, pruned, kept, reclaimed);
    }
}
