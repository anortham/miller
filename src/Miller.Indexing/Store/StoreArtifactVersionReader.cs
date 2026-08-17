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

    /// <summary>
    /// Artifact extractor version for both status display and leadership Evaluate.
    /// The same string must feed <c>artifact_extractor_version</c> and
    /// <see cref="Miller.Indexing.LeadershipEligibility.Evaluate"/> so the reason names the displayed token.
    /// </summary>
    public static string? ReadForEligibility(
        string? legacyDatabasePath,
        Func<string?, string?> legacyVersionReader) =>
        ReadForLeadership(legacyDatabasePath, legacyVersionReader);

    public static string? ReadForLeadership(
        string? legacyDatabasePath,
        Func<string?, string?> legacyVersionReader)
    {
        ArgumentNullException.ThrowIfNull(legacyVersionReader);
        (string? storeVersion, bool pointerPresent, Exception? failure, bool missingStoreRoot) =
            ReadCore(legacyDatabasePath);
        if (pointerPresent && failure is not null)
        {
            if (missingStoreRoot)
                return legacyVersionReader(legacyDatabasePath);
            throw new StoreArtifactVersionReadException(
                "The active family-store version is unreadable; refusing to claim leadership.",
                failure);
        }

        return pointerPresent ? storeVersion : legacyVersionReader(legacyDatabasePath);
    }

    public static bool RequiresRootRebind(
        string? legacyDatabasePath,
        bool unreadableStoreRecoveryAllowed = false)
    {
        (_, bool pointerPresent, Exception? failure, bool missingStoreRoot) = ReadCore(legacyDatabasePath);
        return pointerPresent
            && (missingStoreRoot || (unreadableStoreRecoveryAllowed && failure is not null));
    }

    public static string? TryRead(string? legacyDatabasePath, out bool pointerPresent)
    {
        (string? version, bool foundPointer, _, _) = ReadCore(legacyDatabasePath);
        pointerPresent = foundPointer;
        return version;
    }

    private static (string? Version, bool PointerPresent, Exception? Failure, bool MissingStoreRoot) ReadCore(
        string? legacyDatabasePath)
    {
        bool pointerPresent = false;
        if (string.IsNullOrWhiteSpace(legacyDatabasePath))
            return (null, false, null, false);

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
                return (null, false, null, false);
            }
            StoreWorkspacePointerDocument? pointer = StoreWorkspacePointer.Read(workspaceRoot);
            if (pointer is null)
            {
                return pointerPresent
                    ? (null, true, new IOException("The active family-store pointer disappeared while it was read."), false)
                    : (null, false, null, false);
            }

            if (StoreRootIsMissing(pointer.StoreRoot))
                return (null, true, new DirectoryNotFoundException(pointer.StoreRoot), true);

            var binding = new StoreFamilyBinding(
                pointer.FamilyId,
                pointer.StoreRoot,
                pointer.ViewId,
                pointer.WorkspaceRoot,
                StoreBindingState.Ready);
            WorkspaceFreshnessProbe probe = FamilyStoreReadSession.Probe(binding);
            return (probe.BinaryVersion ?? throw new FamilyStoreReadException(
                FamilyStoreReadFailure.Corrupt,
                "The family-store freshness probe omitted binary_version."), true, null, false);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
                or NotSupportedException or FormatException or SqliteException)
        {
            return (null, pointerPresent, ex, false);
        }
    }

    private static bool StoreRootIsMissing(string storeRoot)
    {
        try
        {
            using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(storeRoot).GetEnumerator();
            _ = entries.MoveNext();
            return false;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
    }
}
