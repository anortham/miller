namespace Miller.Server;

/// <summary>
/// The single workspace-root key every scan-governor publisher and reader must use. Publishers key by the
/// symlink-resolved <see cref="WorkspaceContext.CanonicalRoot"/>; readers that keyed by the unresolved
/// <see cref="WorkspaceContext.WorkspaceRoot"/> missed the lookup on every symlinked path (macOS
/// <c>/tmp</c> → <c>/private/tmp</c>, a symlinked <c>~/src</c>, a Windows junction) and rendered this process's
/// own lease as another process's. Deriving both from here makes that divergence impossible.
/// </summary>
internal static class ScanGovernorKey
{
    /// <summary>
    /// The key for <paramref name="workspace"/>: the canonical root when the bootstrap has resolved one, else
    /// the unresolved root. Null only when there is no workspace at all.
    /// </summary>
    internal static string? For(WorkspaceContext? workspace)
    {
        if (workspace is null)
            return null;
        return string.IsNullOrWhiteSpace(workspace.CanonicalRoot)
            ? NullIfBlank(workspace.WorkspaceRoot)
            : workspace.CanonicalRoot;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
