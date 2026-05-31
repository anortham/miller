namespace Miller.Server.Workspaces;

public interface IWorkspaceIndexProvider
{
    WorkspaceReadContext Resolve(string? workspaceId, bool ensureFresh);
}
