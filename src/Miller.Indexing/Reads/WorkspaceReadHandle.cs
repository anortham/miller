using Microsoft.Data.Sqlite;

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

    public TResult Read<TResult>(Func<SqliteConnection, TResult> query) => _session.Read(query);

    public void Dispose() => _session.Dispose();

    public static implicit operator WorkspaceReadHandle(string databasePath) =>
        new(LegacyArtifactReadSession.CreateDeferred(databasePath));

    public static implicit operator WorkspaceReadHandle(LegacyArtifactReadSession session) => new(session);
}
