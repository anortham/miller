using Microsoft.Data.Sqlite;
using Miller.Indexing.Reads;

namespace Miller.Indexing.Store;

public sealed class StoreArtifactVersionReadException(string message, Exception? innerException = null)
    : IOException(message, innerException);

/// <summary>Reads the producer version from the serving family store for leadership eligibility.</summary>
public static class StoreArtifactVersionReader
{
    private readonly record struct StoreVersionRead(
        string? Version,
        bool PointerPresent,
        Exception? Failure,
        bool MissingStoreRoot);

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
        StoreVersionRead read = ReadCore(legacyDatabasePath);
        if (read.PointerPresent && read.Failure is not null)
        {
            if (read.MissingStoreRoot)
                return legacyVersionReader(legacyDatabasePath);
            throw new StoreArtifactVersionReadException(
                "The active family-store version is unreadable; refusing to claim leadership.",
                read.Failure);
        }

        return read.PointerPresent ? read.Version : legacyVersionReader(legacyDatabasePath);
    }

    public static bool RequiresRootRebind(
        string? legacyDatabasePath,
        bool unreadableStoreRecoveryAllowed = false)
    {
        StoreVersionRead read = ReadCore(legacyDatabasePath);
        return read.PointerPresent
            && (read.MissingStoreRoot || (unreadableStoreRecoveryAllowed && read.Failure is not null));
    }

    /// <summary>
    /// The family producer version to compare BEFORE an import into an existing family. Returns false when the
    /// store is unreadable for a reason that is NOT "no serving generation yet" — the caller must then refuse.
    /// Returns true with a null <paramref name="familyVersion"/> when the family carries no comparable version
    /// at all (no CURRENT, no generation, no store.db, no coord.db): that is a genuine first import, and
    /// nothing can go backwards from it.
    /// </summary>
    public static bool TryReadFamilyWriterFloor(
        StoreFamilyBinding binding,
        out string? familyVersion,
        out FamilyStoreReadException? unreadable)
    {
        ArgumentNullException.ThrowIfNull(binding);
        unreadable = null;
        try
        {
            familyVersion = FamilyStoreReadSession.ReadFamilyBinaryVersion(binding);
            return true;
        }
        catch (FamilyStoreReadException ex) when (
            ex.Failure is FamilyStoreReadFailure.CurrentMissing
                or FamilyStoreReadFailure.GenerationMissing
                or FamilyStoreReadFailure.StoreMissing
                or FamilyStoreReadFailure.CoordinatorMissing)
        {
            familyVersion = null;
            return true;
        }
        catch (FamilyStoreReadException ex)
        {
            // SchemaIncompatible, ReaderFloorIncompatible, FamilyMismatch, CurrentMalformed, Corrupt. Each one
            // says the store is beyond this Miller or is damaged. Do NOT read them as "no version".
            familyVersion = null;
            unreadable = ex;
            return false;
        }
    }

    public static string? TryRead(string? legacyDatabasePath, out bool pointerPresent)
    {
        StoreVersionRead read = ReadCore(legacyDatabasePath);
        pointerPresent = read.PointerPresent;
        return read.Version;
    }

    private static StoreVersionRead ReadCore(string? legacyDatabasePath)
    {
        bool pointerPresent = false;
        if (string.IsNullOrWhiteSpace(legacyDatabasePath))
            return new StoreVersionRead(null, false, null, false);

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
                return new StoreVersionRead(null, false, null, false);
            }
            StoreWorkspacePointerDocument? pointer = StoreWorkspacePointer.Read(workspaceRoot);
            if (pointer is null)
            {
                return pointerPresent
                    ? new StoreVersionRead(
                        null,
                        true,
                        new IOException("The active family-store pointer disappeared while it was read."),
                        false)
                    : new StoreVersionRead(null, false, null, false);
            }

            if (StoreRootIsMissing(pointer.StoreRoot))
                return new StoreVersionRead(null, true, new DirectoryNotFoundException(pointer.StoreRoot), true);

            // The pointer round trip is lossy: it records no binding state, so every rebuild here hard-codes
            // Ready. That stays honest because the per-view read is TRIED first and only the typed
            // ViewNotFound falls back below.
            var binding = new StoreFamilyBinding(
                pointer.FamilyId,
                pointer.StoreRoot,
                pointer.ViewId,
                pointer.WorkspaceRoot,
                StoreBindingState.Ready);
            try
            {
                WorkspaceFreshnessProbe probe = FamilyStoreReadSession.Probe(binding);
                return new StoreVersionRead(
                    probe.BinaryVersion ?? throw new FamilyStoreReadException(
                        FamilyStoreReadFailure.Corrupt,
                        "The family-store freshness probe omitted binary_version."),
                    PointerPresent: true,
                    Failure: null,
                    MissingStoreRoot: false);
            }
            catch (FamilyStoreReadException ex) when (ex.Failure == FamilyStoreReadFailure.ViewNotFound)
            {
                // The pointer names a view the serving generation does not carry: never imported, or lost. Do
                // NOT report "no artifact version" — that would let an OLDER extractor claim leadership and
                // write into a family whose store was produced by a NEWER one. store_meta.binary_version is
                // family-wide, so the family's version is the correct comparison target here.
                return new StoreVersionRead(
                    FamilyStoreReadSession.ReadFamilyBinaryVersion(binding),
                    PointerPresent: true,
                    Failure: null,
                    MissingStoreRoot: false);
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
                or NotSupportedException or FormatException or SqliteException)
        {
            return new StoreVersionRead(null, pointerPresent, ex, false);
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
