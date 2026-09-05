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
            if (LeadershipEligibility.TryParseTriple(familyVersion) is null)
            {
                // LeadershipEligibility deliberately reads an unparseable ARTIFACT version as eligible: a
                // downgrade cannot be proven, and a lenient read path is right for display and for a legacy
                // artifact whose version string predates the contract. A WRITER gate into a SHARED family is
                // the opposite case — an unparseable floor there would silently remove the floor. Refuse, and
                // leave every read path lenient.
                unreadable = new FamilyStoreReadException(
                    FamilyStoreReadFailure.Corrupt,
                    $"The family store reports binary_version '{familyVersion}', which is not a version this "
                    + "Miller can compare. Refusing to import rather than write without a floor.");
                familyVersion = null;
                return false;
            }

            return true;
        }
        catch (FamilyStoreReadException ex) when (
            ex.Failure is FamilyStoreReadFailure.CurrentMissing
                or FamilyStoreReadFailure.GenerationMissing
                or FamilyStoreReadFailure.StoreMissing
                or FamilyStoreReadFailure.CoordinatorMissing)
        {
            // These say "the serving generation is not readable", which is NOT the same as "nothing was ever
            // written here". A family whose CURRENT was lost, or whose serving generation was deleted, can
            // still hold a store.db a NEWER extractor produced. Reading that as a blank floor would let an
            // older extractor write into it and take store_meta.binary_version backwards for every member
            // view. So prove it: a blank floor is granted only when no store.db exists anywhere under the
            // family root. That keeps a genuine first import working — it has no store.db to find — without
            // granting one to a damaged family.
            if (AnyStoreDatabaseExists(binding.StoreRoot))
            {
                familyVersion = null;
                unreadable = ex;
                return false;
            }

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

    /// <summary>
    /// Does the family root hold ANY generation database? A missing root, an empty root, or a root that
    /// holds only planning state answers no, and that is what makes a first import safe. Any I/O failure
    /// answers YES: the writer gate must fail closed when it cannot see what is there.
    /// </summary>
    private static bool AnyStoreDatabaseExists(string storeRoot)
    {
        try
        {
            return Directory.EnumerateFiles(storeRoot, "store.db", SearchOption.AllDirectories).Any();
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
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

            // This is family-wide compatibility preflight, not a serving freshness read. It must work
            // before the requested view or reader catalogue exists; admission would create a bootstrap
            // cycle. The metadata path validates family/schema/floor but never serves view facts.
            var binding = new StoreFamilyBinding(
                pointer.FamilyId,
                pointer.StoreRoot,
                pointer.ViewId,
                pointer.WorkspaceRoot,
                StoreBindingState.Ready);
            return new StoreVersionRead(
                FamilyStoreReadSession.ReadFamilyBinaryVersion(binding),
                PointerPresent: true,
                Failure: null,
                MissingStoreRoot: false);
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
