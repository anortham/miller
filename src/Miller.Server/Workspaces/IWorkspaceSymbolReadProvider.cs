namespace Miller.Server.Workspaces;

public interface IWorkspaceSymbolReadProvider
{
    WorkspaceSymbolReadContext ResolveSymbolRead(string? workspaceId, WorkspaceRefreshMode refresh);

    WorkspaceSymbolReadContext ResolveCompleteCurrentSymbolRead() =>
        ResolveSymbolRead(null, WorkspaceRefreshMode.None);

    /// <summary>
    /// The complete-recall read an edit needs: the session projection rather than the recall-limited search
    /// sidecar, for the current workspace or for an explicitly named one.
    /// </summary>
    WorkspaceSymbolReadContext ResolveCompleteSymbolRead(string? workspaceId, WorkspaceRefreshMode refresh) =>
        workspaceId is null
            ? ResolveCompleteCurrentSymbolRead()
            : ResolveSymbolRead(workspaceId, refresh);
}
