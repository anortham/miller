using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>
/// Whether an extract artifact on disk describes a given workspace root. v1 stores no <c>workspace_id</c>, so
/// artifact identity IS the canonical <c>root_path</c> the artifact was extracted from (reconciliation #14).
/// </summary>
public static class ArtifactRootIdentity
{
    /// <summary>
    /// Whether the artifact's recorded <c>root_path</c> identifies the same workspace root Miller is indexing.
    /// Both julie (when it writes the artifact) and Miller (<see cref="PathCanonicalizer.CanonicalizeRoot"/>)
    /// record an absolute, symlink-resolved canonical root, but they do NOT spell it identically on Windows:
    /// Rust's <c>std::fs::canonicalize</c> emits the extended-length verbatim prefix (<c>\\?\C:\repo</c>) and
    /// reflects the on-disk casing, while Miller's canonical root strips that prefix and preserves the
    /// as-launched casing. So BOTH operands are normalized — verbatim prefix stripped, then compared
    /// case-insensitively on Windows and default macOS, case-sensitively on Linux/POSIX. The normalization is
    /// pure string work: the recorded root is NOT re-canonicalized against the filesystem (it may not exist on
    /// this machine, e.g. a copied DB). A missing/empty recorded root (a pre-v1 artifact) never matches.
    /// </summary>
    public static bool Matches(string? recordedRootPath, string canonicalRoot)
    {
        if (string.IsNullOrEmpty(recordedRootPath))
            return false;

        string recorded = PathCanonicalizer.StripWindowsVerbatimPrefix(recordedRootPath);
        string current = PathCanonicalizer.StripWindowsVerbatimPrefix(canonicalRoot);
        return string.Equals(recorded, current, ComparisonFor(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS()));
    }

    /// <summary>The root-path comparison this platform uses.</summary>
    public static StringComparison ComparisonFor(bool isWindows, bool isMacOS) =>
        isWindows || isMacOS ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Whether the artifact at <paramref name="dbPath"/> can be SERVED for <paramref name="canonicalRoot"/> right
    /// now: it exists, this build can read its schema, and it records this root. This is the condition a
    /// <see cref="Miller.Core.Freshness.ScanIntent.UserFullRebuild"/> retry needs before it may downgrade to a
    /// delta reconcile — a downgrade against an unreadable or foreign artifact would produce a wrong index that
    /// looks fresh. Any probe failure answers false; it never throws.
    /// </summary>
    public static bool ServableFor(string dbPath, string canonicalRoot)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || string.IsNullOrWhiteSpace(canonicalRoot))
            return false;

        try
        {
            if (!File.Exists(dbPath))
                return false;
            if (!Matches(ExtractReader.ReadRootPath(dbPath), canonicalRoot))
                return false;
            SqliteSymbolReader.VerifyCompatible(dbPath);
            return true;
        }
        catch (Exception ex) when (
            ex is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or IncompatibleExtractException)
        {
            return false;
        }
    }
}
