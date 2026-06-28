using Miller.Indexing;
using Miller.Server.Tools;

namespace Miller.Server.Workspaces;

internal static class WorkspaceRegistryRootMatcher
{
    public static WorkspaceRegistryRow? FindByRoot(IReadOnlyList<WorkspaceRegistryRow> rows, string root) =>
        rows.FirstOrDefault(row => RootMatches(row, root));

    public static WorkspaceRegistryRow? FindByPossiblyMissingPath(
        IReadOnlyList<WorkspaceRegistryRow> rows, string path)
    {
        string fullPath = Path.GetFullPath(path);
        WorkspaceRegistryRow? direct = FindByRoot(rows, fullPath);
        if (direct is not null)
            return direct;

        string? resolved = TryCanonicalizePossiblyMissingPath(fullPath);
        return resolved is null ? null : FindByRoot(rows, resolved);
    }

    private static bool RootMatches(WorkspaceRegistryRow row, string root) =>
        string.Equals(row.CanonicalRoot, root, StringComparison.Ordinal)
        || WorkspaceSafety.IsLiveWorkspace(row.CanonicalRoot, root);

    private static string? TryCanonicalizePossiblyMissingPath(string fullPath)
    {
        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
            return null;

        try
        {
            return PathCanonicalizer.CanonicalizeFile(root, fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
