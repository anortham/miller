using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>
/// Build-to-temp-then-promote for full (force) rebuilds of a julie extract DB. A force scan that merges
/// in-place into the LIVE served <c>symbols.db</c> is pathological at scale: every page read pays a
/// wal-index scan against a WAL that live readers keep from resetting, collapsing a ~90s bulk insert into a
/// multi-hour ~7KB/s merge (2026-06-11 Eros field report #2, openclaw). So
/// <see cref="JulieExtractRunner.Scan"/> points a force scan at <see cref="RebuildDbPathFor"/> — a fresh
/// sibling file, bulk-insert fast, invisible to readers — and this type promotes the finished artifact over
/// the live one with an overwrite-move (same directory ⟹ same filesystem ⟹ atomic on POSIX).
///
/// <para>Readers are already built for wholesale file replacement: every extract-DB open is
/// <c>Pooling=false</c> (<see cref="SqliteReadOnlyAccess"/>, the 2026-06-11 fleet finding), and a failed
/// promote leaves the live artifact untouched — strictly better than a failed in-place merge. The promoted
/// file must be SELF-CONTAINED: a leftover rebuild WAL is folded in first, and the live DB's old
/// <c>-wal</c>/<c>-shm</c> are removed so the new file can never pair with stale sidecars (cross-inode WAL
/// replay reads garbage pages). Callers must hold Miller's single-writer lock, exactly as for the scan itself.</para>
/// </summary>
public static class FullRebuildPromotion
{
    /// <summary>The sibling path a force scan extracts into: <c>&lt;absDbPath&gt;.rebuild</c>. Deterministic
    /// (no pid/timestamp) so crash debris is bounded to one trio and reclaimed by the next
    /// <see cref="PrepareRebuildTarget"/> under the same single-writer lock.</summary>
    public static string RebuildDbPathFor(string absDbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absDbPath);
        return absDbPath + ".rebuild";
    }

    /// <summary>
    /// Delete any stale rebuild trio (<c>.rebuild</c> + <c>-wal</c>/<c>-shm</c>) left by a crashed or killed
    /// earlier rebuild, so julie-extract starts from a genuinely fresh file (a leftover would turn the scan
    /// back into the in-place merge this type exists to avoid).
    /// </summary>
    public static void PrepareRebuildTarget(string absDbPath)
    {
        string rebuildDb = RebuildDbPathFor(absDbPath);
        DeleteWithRetry(rebuildDb);
        DeleteWithRetry(rebuildDb + "-wal");
        DeleteWithRetry(rebuildDb + "-shm");
    }

    /// <summary>
    /// Promote the finished rebuild artifact over the live DB: fold a leftover rebuild WAL into the main file
    /// (so nothing committed is lost when only the single file moves), remove the live DB's old
    /// <c>-wal</c>/<c>-shm</c>, then overwrite-move the rebuild file into place. Windows can transiently fail
    /// the delete/move while another miller briefly holds the live file open read-only (SQLite opens without
    /// FILE_SHARE_DELETE), so both retry briefly — per-operation readers are non-pooled and millisecond-scale.
    /// </summary>
    /// <exception cref="InvalidOperationException">No rebuild artifact exists at <see cref="RebuildDbPathFor"/>.</exception>
    /// <exception cref="IOException">The live file stayed locked past the bounded retry (the live artifact is
    /// untouched; the rebuild trio is left for the next <see cref="PrepareRebuildTarget"/> to reclaim).</exception>
    public static void Promote(string absDbPath)
    {
        string rebuildDb = RebuildDbPathFor(absDbPath);
        if (!File.Exists(rebuildDb))
            throw new InvalidOperationException(
                $"Cannot promote the full rebuild: no rebuilt artifact exists at '{rebuildDb}' " +
                "(the force scan did not produce a DB).");

        FoldWalIfPresent(rebuildDb);

        DeleteWithRetry(absDbPath + "-wal");
        DeleteWithRetry(absDbPath + "-shm");

        // Belt-and-braces against any pooled handle to the soon-unlinked live inode surviving in this
        // process (all Miller opens are Pooling=false, but the pool is process-global state).
        SqliteConnection.ClearAllPools();
        MoveWithRetry(rebuildDb, absDbPath);

        // The fold left the rebuild a single file; clear defensively in case a non-SQLite leftover snuck in.
        DeleteWithRetry(rebuildDb + "-wal");
        DeleteWithRetry(rebuildDb + "-shm");
    }

    // A clean julie-extract exit checkpoints and deletes its WAL on last close, so this is normally a no-op
    // stat. A kill-after-commit (or a PERSIST_WAL build) leaves committed frames only in the -wal; folding
    // them via TRUNCATE makes the main file complete, and closing this last connection deletes the sidecars.
    private static void FoldWalIfPresent(string rebuildDb)
    {
        if (!File.Exists(rebuildDb + "-wal"))
            return;

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = rebuildDb,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.ExecuteNonQuery();
    }

    private static void DeleteWithRetry(string path)
    {
        if (!File.Exists(path))
            return;
        for (int attempt = 1; ; attempt++)
        {
            try { File.Delete(path); break; }
            catch (IOException) when (attempt < 5) { Thread.Sleep(20 * attempt); }
            catch (UnauthorizedAccessException) when (attempt < 5) { Thread.Sleep(20 * attempt); }
        }
    }

    private static void MoveWithRetry(string source, string destination)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { File.Move(source, destination, overwrite: true); break; }
            catch (IOException) when (attempt < 5) { Thread.Sleep(20 * attempt); }
            catch (UnauthorizedAccessException) when (attempt < 5) { Thread.Sleep(20 * attempt); }
        }
    }
}
