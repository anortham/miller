using Microsoft.Data.Sqlite;
using Miller.Indexing.Store;

namespace Miller.Indexing.Reads;

public sealed class WorkspaceReadHandle : IWorkspaceReadSession
{
    private readonly IWorkspaceReadSession _session;

    public WorkspaceReadHandle(IWorkspaceReadSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    public WorkspaceReadSnapshot Snapshot => _session.Snapshot;

    public string? LegacyArtifactPath =>
        (_session as LegacyArtifactReadSession)?.DatabasePath;

    public string? FamilyStoreRoot =>
        (_session as FamilyStoreReadSession)?.Visibility.StoreRoot;

    public TResult Read<TResult>(Func<SqliteConnection, TResult> query) => _session.Read(query);

    public void Dispose() => _session.Dispose();

    public static implicit operator WorkspaceReadHandle(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string absolutePath = Path.GetFullPath(databasePath);
        string? workspaceRoot = WorkspaceRootFor(absolutePath);
        if (workspaceRoot is not null)
        {
            bool storeEnabled = WorkspaceReadSessionFactory.StoreEnabledFromEnvironment();
            if (storeEnabled || StoreWorkspacePointer.Read(workspaceRoot) is not null)
            {
                return WorkspaceReadSessionFactory.Open(
                    absolutePath,
                    workspaceRoot,
                    workspaceId: null,
                    storeEnabled);
            }
        }

        return new(File.Exists(absolutePath)
            ? LegacyArtifactReadSession.Open(absolutePath)
            : LegacyArtifactReadSession.CreateDeferred(absolutePath));
    }

    public static implicit operator WorkspaceReadHandle(LegacyArtifactReadSession session) => new(session);

    private static string? WorkspaceRootFor(string databasePath)
    {
        string? millerDirectory = Path.GetDirectoryName(databasePath);
        if (!string.Equals(
                Path.GetFileName(millerDirectory),
                ".miller",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetDirectoryName(millerDirectory);
    }
}
