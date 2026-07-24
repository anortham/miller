namespace Miller.Server.Workspaces;

public interface IWorkspaceSymbolReadProvider
{
    WorkspaceSymbolReadContext ResolveSymbolRead(string? workspaceId, bool ensureFresh);
}
