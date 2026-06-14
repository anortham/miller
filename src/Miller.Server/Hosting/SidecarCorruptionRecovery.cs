using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Miller.Indexing;

namespace Miller.Server.Hosting;

internal static class SidecarCorruptionRecovery
{
    // Sidecars are revision-keyed derived artifacts. When the existing artifact is corruption-shaped,
    // deleting it and rebuilding from symbols.db is safe; source of truth stays in symbols.db.
    public static bool TryRebuildCorruptSidecar(
        Exception failure,
        string artifactPath,
        Action rebuild,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        ArgumentNullException.ThrowIfNull(rebuild);
        ArgumentNullException.ThrowIfNull(logger);

        if (!IsSidecarCorruption(failure))
            return false;

        logger.LogWarning(failure,
            "Sidecar at {ArtifactPath} appears corrupt; deleting the derived artifact and rebuilding it from scratch.",
            artifactPath);
        try
        {
            // Release any pooled read handle so the delete is not blocked on Windows; readers self-heal by
            // reopening the rebuilt artifact (it is revision-keyed derived state, never source of truth).
            SqliteConnection.ClearAllPools();
            if (File.Exists(artifactPath))
                File.Delete(artifactPath);
            rebuild();
            logger.LogInformation("Rebuilt corrupt sidecar at {ArtifactPath}.", artifactPath);
            return true;
        }
        catch (Exception ex) when (
            ex is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or IncompatibleExtractException)
        {
            logger.LogWarning(ex,
                "Corrupt-sidecar rebuild at {ArtifactPath} failed; will retry on the next convergence.",
                artifactPath);
            return false;
        }
    }

    // Corruption-shaped: a SqliteException whose result code is SQLITE_CORRUPT (11) or SQLITE_NOTADB (26) -
    // primary or extended (e.g. SQLITE_CORRUPT_VTAB = 267, whose low byte is 11) - anywhere in the exception
    // chain, or a sidecar reader's malformed-meta error.
    private static bool IsSidecarCorruption(Exception exception)
    {
        for (Exception? ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is SqliteException sqlite
                && ((sqlite.SqliteErrorCode & 0xFF) is 11 or 26 || (sqlite.SqliteExtendedErrorCode & 0xFF) is 11 or 26))
            {
                return true;
            }

            if (ex is InvalidOperationException
                && ex.Message.Contains("has malformed meta", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
