namespace Miller.Server.Workspaces;

public interface IWorkspaceContentSearchProvider
{
    WorkspaceContentSearchContext ResolveContentSearch(string? workspaceId, WorkspaceRefreshMode refresh);
}
