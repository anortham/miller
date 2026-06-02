namespace Miller.Indexing;

public static class SymbolSearchProjectionLoader
{
    public static SymbolSearchProjection Load(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        return SymbolSearchProjection.Build(SqliteSymbolReader.Read(dbPath));
    }
}
