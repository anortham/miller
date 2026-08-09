using Miller.Indexing.Reads;

namespace Miller.Indexing;

public static class SymbolSearchProjectionLoader
{
    public static SymbolSearchProjection Load(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        return SymbolSearchProjection.Build(SqliteSymbolReader.Read(dbPath));
    }

    public static SymbolSearchProjection LoadSession(IWorkspaceReadSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return SymbolSearchProjection.Build(SqliteSymbolReader.ReadSession(session));
    }
}
