using Microsoft.Data.Sqlite;
using Miller.Indexing.Reads;

namespace Miller.Indexing.Store;

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

    public static string? TryRead(string? legacyDatabasePath, out bool pointerPresent)
    {
        pointerPresent = false;
        if (string.IsNullOrWhiteSpace(legacyDatabasePath))
            return null;

        try
        {
            string millerDirectory = Path.GetDirectoryName(Path.GetFullPath(legacyDatabasePath))
                ?? throw new ArgumentException("The legacy artifact path has no parent directory.");
            string workspaceRoot = Directory.GetParent(millerDirectory)?.FullName
                ?? throw new ArgumentException("The legacy artifact path has no workspace root.");
            pointerPresent = StoreWorkspacePointer.Exists(workspaceRoot);
            StoreWorkspacePointerDocument? pointer = StoreWorkspacePointer.Read(workspaceRoot);
            if (pointer is null)
                return null;
            pointerPresent = true;

            var binding = new StoreFamilyBinding(
                pointer.FamilyId,
                pointer.StoreRoot,
                pointer.ViewId,
                pointer.WorkspaceRoot,
                StoreBindingState.Ready);
            using FamilyStoreReadSession session = FamilyStoreReadSession.Open(binding);
            return session.Visibility.BinaryVersion;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
                or NotSupportedException or FormatException or SqliteException)
        {
            return null;
        }
    }
}
