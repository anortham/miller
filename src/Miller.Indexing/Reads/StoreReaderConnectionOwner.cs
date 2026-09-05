using System.Data;
using Microsoft.Data.Sqlite;

namespace Miller.Indexing.Reads;

// The registration captures this owner until every connection has positively closed. Its lock never
// calls back into the session gate, so registry cleanup cannot invert the session/registration locks.
internal sealed class StoreReaderConnectionOwner(Func<string, SqliteConnection> createRead)
{
    private readonly object _gate = new();
    private readonly List<SqliteConnection> _connections = [];

    internal SqliteConnection OpenRead(string path)
    {
        lock (_gate)
        {
            _connections.RemoveAll(connection => connection.State == ConnectionState.Closed);
            SqliteConnection connection = createRead(path);
            _connections.Add(connection);
            try
            {
                connection.Open();
                return connection;
            }
            catch
            {
                try { connection.Dispose(); } catch { /* The final-release guard still owns it. */ }
                throw;
            }
        }
    }

    internal bool TryCloseAll()
    {
        lock (_gate)
        {
            foreach (SqliteConnection connection in _connections)
            {
                if (connection.State == ConnectionState.Closed) continue;
                try { connection.Dispose(); } catch { /* Retry only while closure remains unproved. */ }
            }
            _connections.RemoveAll(connection => connection.State == ConnectionState.Closed);
            return _connections.Count == 0;
        }
    }
}
