using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>
/// Shared <c>Mode=ReadOnly</c> open discipline for julie WAL extract DBs (the M1 D4 rules), reused by every
/// reader (<see cref="SqliteSymbolReader"/>'s one-shot startup pass and <see cref="FreshnessReader"/>'s
/// long-lived poll connection).
///
/// <para>The WAL trap (D4): a <c>Mode=ReadOnly</c> reader of a WAL DB still needs to write the wal-index
/// sidecar (<c>-shm</c>) into the DB's directory. A read-only directory makes Open()/first read throw
/// SQLITE_READONLY (error code 8) mid-stream. We deliberately do NOT use <c>immutable=1</c> (it silently drops
/// uncheckpointed <c>-wal</c> rows under a live julie writer). Instead we probe the directory's writability up
/// front and surface a clear <see cref="InvalidOperationException"/> — Miller owns these directories, so a
/// non-writable one is a configuration error, not a runtime surprise.</para>
/// </summary>
internal static class SqliteReadOnlyAccess
{
    /// <summary>
    /// Resolve <paramref name="dbPath"/> to absolute, verify the file exists and its directory is writable
    /// (WAL sidecar), open a <c>Mode=ReadOnly</c> connection, and map a SQLITE_READONLY open failure to a clear
    /// <see cref="InvalidOperationException"/>. The returned connection is OPEN and owned by the caller (who
    /// must dispose it).
    /// </summary>
    /// <exception cref="FileNotFoundException">The DB file does not exist.</exception>
    /// <exception cref="InvalidOperationException">The DB's directory is not writable (WAL sidecar trap).</exception>
    public static SqliteConnection Open(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        string absDbPath = Path.GetFullPath(dbPath);
        if (!File.Exists(absDbPath))
            throw new FileNotFoundException(
                $"julie extract DB not found at '{absDbPath}'. Run `julie-server extract ... scan` first " +
                "(see scripts/restore-julie-server.sh to obtain the binary).", absDbPath);

        string? dir = Path.GetDirectoryName(absDbPath);
        if (string.IsNullOrEmpty(dir))
            throw new InvalidOperationException($"Cannot determine the directory of DB path '{absDbPath}'.");
        EnsureDirectoryWritable(dir, absDbPath);

        var connectionString =
            new SqliteConnectionStringBuilder { DataSource = absDbPath, Mode = SqliteOpenMode.ReadOnly }
                .ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 8 /* SQLITE_READONLY */)
        {
            connection.Dispose();
            throw new InvalidOperationException(
                $"Cannot open '{absDbPath}' read-only: the DB directory '{dir}' must be writable for the WAL " +
                "wal-index sidecar. Move the extract under a Miller-owned writable directory.", ex);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return connection;
    }

    // Probe writability by creating + deleting a temp file in the DB directory. A pure FileMode check is
    // insufficient (ACLs, read-only mounts); the create+delete round-trip is the honest test.
    private static void EnsureDirectoryWritable(string dir, string absDbPath)
    {
        string probe = Path.Combine(dir, ".miller-write-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (File.Create(probe)) { }
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                $"The directory '{dir}' of julie extract DB '{absDbPath}' is not writable. A Mode=ReadOnly " +
                "reader of a WAL DB still needs to write the wal-index sidecar there; move the extract under a " +
                "Miller-owned writable directory.", ex);
        }
    }
}
