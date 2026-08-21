namespace Miller.Server.Workspaces;

public interface IWorkspaceArtifactProvider
{
    WorkspaceArtifactContext ResolveArtifact(string? workspaceId, WorkspaceRefreshMode refresh);
}
