namespace Miller.Server.Workspaces;

public interface IWorkspaceSearchProvider
{
    WorkspaceSymbolSearchContext ResolveSymbolSearch(string? workspaceId, WorkspaceRefreshMode refresh);
}
