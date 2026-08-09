using Microsoft.Data.Sqlite;

namespace Miller.Indexing.Reads;

public interface IWorkspaceReadSession : IDisposable
{
    WorkspaceReadSnapshot Snapshot { get; }

    TResult Read<TResult>(Func<SqliteConnection, TResult> query);
}
