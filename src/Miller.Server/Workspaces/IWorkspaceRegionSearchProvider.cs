namespace Miller.Server.Workspaces;

public interface IWorkspaceRegionSearchProvider
{
    WorkspaceRegionSearchContext ResolveRegionSearch(string? workspaceId, WorkspaceRefreshMode refresh);
}
