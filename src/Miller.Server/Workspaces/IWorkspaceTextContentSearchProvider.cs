namespace Miller.Server.Workspaces;

public interface IWorkspaceTextContentSearchProvider
{
    WorkspaceTextContentSearchContext ResolveTextContentSearch(string? workspaceId, bool ensureFresh);
}
