using Microsoft.Data.Sqlite;
using Miller.Indexing.Reads;

namespace Miller.Indexing.Store;

public sealed class StoreArtifactVersionReadException(string message, Exception? innerException = null)
    : IOException(message, innerException);

/// <summary>Reads the producer version from the serving family store for leadership eligibility.</summary>
public static class StoreArtifactVersionReader
{
    public static string? TryRead(string? legacyDatabasePath)
    {
        return TryRead(legacyDatabasePath, out _);
    }

    public static string? TryReadOrFallback(
        string? legacyDatabasePath,
        Func<string?, string?> legacyVersionReader)
    {
        ArgumentNullException.ThrowIfNull(legacyVersionReader);
        string? storeVersion = TryRead(legacyDatabasePath, out bool pointerPresent);
        return pointerPresent ? storeVersion : legacyVersionReader(legacyDatabasePath);
    }

    public static string? ReadForLeadership(
        string? legacyDatabasePath,
        Func<string?, string?> legacyVersionReader)
    {
        ArgumentNullException.ThrowIfNull(legacyVersionReader);
        (string? storeVersion, bool pointerPresent, Exception? failure) = ReadCore(legacyDatabasePath);
        if (pointerPresent && failure is not null)
        {
            throw new StoreArtifactVersionReadException(
                "The active family-store version is unreadable; refusing to claim leadership.",
                failure);
        }

        return pointerPresent ? storeVersion : legacyVersionReader(legacyDatabasePath);
    }

    public static string? TryRead(string? legacyDatabasePath, out bool pointerPresent)
    {
        (string? version, bool foundPointer, _) = ReadCore(legacyDatabasePath);
        pointerPresent = foundPointer;
        return version;
    }

    private static (string? Version, bool PointerPresent, Exception? Failure) ReadCore(string? legacyDatabasePath)
    {
        bool pointerPresent = false;
        if (string.IsNullOrWhiteSpace(legacyDatabasePath))
            return (null, false, null);

        try
        {
            string millerDirectory = Path.GetDirectoryName(Path.GetFullPath(legacyDatabasePath))
                ?? throw new ArgumentException("The legacy artifact path has no parent directory.");
            string workspaceRoot = Directory.GetParent(millerDirectory)?.FullName
                ?? throw new ArgumentException("The legacy artifact path has no workspace root.");
            pointerPresent = true;
            if (!StoreWorkspacePointer.Exists(workspaceRoot))
            {
                pointerPresent = false;
                return (null, false, null);
            }
            StoreWorkspacePointerDocument? pointer = StoreWorkspacePointer.Read(workspaceRoot);
            if (pointer is null)
            {
                return pointerPresent
                    ? (null, true, new IOException("The active family-store pointer disappeared while it was read."))
                    : (null, false, null);
            }

            var binding = new StoreFamilyBinding(
                pointer.FamilyId,
                pointer.StoreRoot,
                pointer.ViewId,
                pointer.WorkspaceRoot,
                StoreBindingState.Ready);
            using FamilyStoreReadSession session = FamilyStoreReadSession.Open(binding);
            return (session.Visibility.BinaryVersion, true, null);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
                or NotSupportedException or FormatException or SqliteException)
        {
            return (null, pointerPresent, ex);
        }
    }
}
