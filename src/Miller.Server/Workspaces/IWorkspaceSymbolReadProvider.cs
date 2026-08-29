namespace Miller.Server.Workspaces;

public interface IWorkspaceSymbolReadProvider
{
    WorkspaceSymbolReadContext ResolveSymbolRead(string? workspaceId, WorkspaceRefreshMode refresh);

    WorkspaceSymbolReadContext ResolveCompleteCurrentSymbolRead() =>
        ResolveSymbolRead(null, WorkspaceRefreshMode.None);
}
