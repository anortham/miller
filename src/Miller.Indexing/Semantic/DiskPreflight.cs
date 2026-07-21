using System.Globalization;

namespace Miller.Indexing.Semantic;

/// <summary>
/// A pure disk-space verdict for a shadow vector build or the model download: whether the free space under a
/// target directory can hold the projected artifact, carrying the free and required byte facts so the refusal
/// can be surfaced with numbers rather than a bare "no space".
/// </summary>
public readonly record struct DiskPreflightVerdict(bool Ok, long FreeBytes, long RequiredBytes)
{
    /// <summary>A short, path-free reason naming both the free and required byte facts, ready to embed in a
    /// stored pause reason or a CLI refusal.</summary>
    public string Reason =>
        $"{Describe(FreeBytes)} free, {Describe(RequiredBytes)} required";

    private static string Describe(long bytes)
    {
        if (bytes < 0)
            return "unknown space";

        const double gib = 1024d * 1024d * 1024d;
        const double mib = 1024d * 1024d;
        if (bytes >= (long)gib)
            return (bytes / gib).ToString("0.0", CultureInfo.InvariantCulture) + " GiB";
        if (bytes >= (long)mib)
            return (bytes / mib).ToString("0", CultureInfo.InvariantCulture) + " MiB";
        return bytes.ToString(CultureInfo.InvariantCulture) + " bytes";
    }
}

/// <summary>
/// The disk preflight the vector drain runs before starting a shadow rebuild and at each bounded slice as the
/// shadow grows, and that <c>miller semantic prepare</c> reuses before a model download. The verdict logic is
/// pure — the only I/O is the injected free-space probe, so the fast suite never touches a real disk. A probe
/// that cannot determine free space returns a negative value, which is treated as "unknown" and never blocks a
/// consented build.
/// </summary>
public sealed class DiskPreflight
{
    /// <summary>The conservative floor: even a tiny work list reserves this much, so a nearly-full disk is caught
    /// before a build that would fail mid-write with a corrupt half-artifact.</summary>
    public const long MinimumRequiredBytes = 256L * 1024 * 1024;

    /// <summary>The per-unit footprint assumed when there is no current artifact to observe (the initial build).
    /// A quantized card plus its mapping row is well under this; the margin keeps the estimate conservative.</summary>
    internal const long FallbackBytesPerUnit = 8L * 1024;

    private readonly Func<string, long> _freeSpaceProbe;

    /// <summary>Creates a preflight over an injected free-space probe. The default probe walks up to the nearest
    /// existing ancestor of the target path and reads the free space on the volume that actually contains it —
    /// the directory itself on Unix (its mount point) and its drive letter on Windows — returning a negative
    /// value on any probe fault.</summary>
    public DiskPreflight(Func<string, long>? freeSpaceProbe = null) =>
        _freeSpaceProbe = freeSpaceProbe ?? ProbeAvailableFreeBytes;

    /// <summary>Probes the free space under <paramref name="path"/> and decides against
    /// <paramref name="requiredBytes"/>. Pure decision logic over the probe result: available when free space is
    /// unknown (negative) or at least the required bytes; blocked otherwise.</summary>
    public DiskPreflightVerdict Check(string path, long requiredBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        long free = _freeSpaceProbe(path);
        bool ok = free < 0 || free >= requiredBytes;
        return new DiskPreflightVerdict(ok, free, requiredBytes);
    }

    /// <summary>
    /// The stated (non-contractual) required-bytes heuristic: the work-list size times the bytes-per-unit
    /// observed on the current artifact, clamped up to <see cref="MinimumRequiredBytes"/>. With no artifact to
    /// observe — the initial build — a conservative fallback per-unit footprint is used.
    /// </summary>
    public static long EstimateRequiredBytes(int workUnits, long currentArtifactBytes, int currentStoredUnits)
    {
        long perUnit = currentStoredUnits > 0 && currentArtifactBytes > 0
            ? Math.Max(1L, currentArtifactBytes / currentStoredUnits)
            : FallbackBytesPerUnit;

        long units = Math.Max(0L, workUnits);
        long projected = perUnit * units;
        return Math.Max(MinimumRequiredBytes, projected);
    }

    private static long ProbeAvailableFreeBytes(string path)
    {
        try
        {
            string? probe = path;
            while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe))
                probe = Path.GetDirectoryName(probe);

            if (string.IsNullOrEmpty(probe))
                return -1;

            string fullProbe = Path.GetFullPath(probe);
            string mount = OperatingSystem.IsWindows()
                ? Path.GetPathRoot(fullProbe) ?? fullProbe
                : fullProbe;
            return new DriveInfo(mount).AvailableFreeSpace;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or DriveNotFoundException)
        {
            return -1;
        }
    }
}
