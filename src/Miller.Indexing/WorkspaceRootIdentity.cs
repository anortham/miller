using System.Runtime.InteropServices;

namespace Miller.Indexing;

/// <summary>
/// Which checkout currently occupies a workspace root, as far as the git administrative layout can prove it.
///
/// <para>Miller's stable <c>workspace_id</c> is SHA-256 of the canonical ROOT PATH, so a worktree that is removed
/// and re-created at the same path is indistinguishable by identity alone — <c>git worktree remove wt &amp;&amp;
/// git worktree add wt other-branch</c> yields the same id, the same registry row, and an artifact whose recorded
/// <c>root_path</c> still matches. Something outside the path has to carry the generation, and git provides one:
/// the per-checkout administrative directory (<c>.git/worktrees/&lt;name&gt;</c> for a linked worktree,
/// <c>&lt;root&gt;/.git</c> for a normal checkout) is DELETED and re-created by that pair of commands, so its path
/// plus its creation timestamp identify the occupant.</para>
///
/// <para><b>What this cannot detect.</b> A workspace with no resolvable git layout has no generation to compare,
/// and so reads as <see cref="IsKnown"/> false and never counts as a replacement — missing evidence must not cost
/// a whole-repo rebuild. Two checkouts created at the same path within the filesystem's timestamp resolution
/// collide. A filesystem that records no birth time leaves the identity unknown. And a
/// plain <c>git checkout other-branch</c> inside the SAME worktree is deliberately NOT a replacement: the content
/// changed but the checkout did not, which is the <c>HEAD</c> watch's job, not this one.</para>
/// </summary>
/// <param name="GitDir">
/// The resolved administrative directory for the checkout at the root, or null when no git layout resolved.
/// </param>
/// <param name="GitDirCreatedAtUtc">
/// When that directory was created, or null when the platform reported no usable timestamp.
/// </param>
public readonly partial record struct WorkspaceRootIdentity(string? GitDir, DateTimeOffset? GitDirCreatedAtUtc)
{
    /// <summary>The identity of a root whose git layout could not be read at all.</summary>
    public static WorkspaceRootIdentity Unknown => default;

    /// <summary>Whether both halves of the generation were observed, so a comparison can mean anything.</summary>
    public bool IsKnown => GitDir is not null && GitDirCreatedAtUtc is not null;

    /// <summary>
    /// Read the current occupant's identity from disk. Never throws: an absent, unreadable, or malformed layout
    /// resolves to <see cref="Unknown"/> so a poll on a half-created directory degrades instead of failing.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="workspaceRoot"/> is null or blank.</exception>
    public static WorkspaceRootIdentity Capture(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        if (GitWorktreeLayout.Resolve(workspaceRoot)?.GitDir is not { } gitDir)
            return Unknown;

        return new WorkspaceRootIdentity(gitDir, CaptureDirectoryCreationTime(gitDir));
    }

    /// <summary>Reads stable directory creation evidence, or null when the platform cannot provide it.</summary>
    public static DateTimeOffset? CaptureDirectoryCreationTime(string? path)
    {
        if (path is null)
            return null;
        if (OperatingSystem.IsLinux())
            return CaptureLinuxBirthTime(path);

        try
        {
            DateTime created = new DirectoryInfo(path).CreationTimeUtc;
            return created > UnsetCreationTimeUtc ? created : null;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static DateTimeOffset? CaptureLinuxBirthTime(string path)
    {
        try
        {
            if (Statx(AtCurrentWorkingDirectory, path, 0, StatxBirthTime, out LinuxStatx result) != 0 ||
                (result.Mask & StatxBirthTime) == 0 ||
                result.BirthTime.Nanoseconds >= NanosecondsPerSecond)
            {
                return null;
            }

            return DateTimeOffset.FromUnixTimeSeconds(result.BirthTime.Seconds)
                .AddTicks(result.BirthTime.Nanoseconds / NanosecondsPerTick);
        }
        catch (Exception ex) when (
            ex is DllNotFoundException or EntryPointNotFoundException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private const int AtCurrentWorkingDirectory = -100;
    private const uint StatxBirthTime = 0x00000800;
    private const uint NanosecondsPerSecond = 1_000_000_000;
    private const uint NanosecondsPerTick = 100;

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(
        int directoryFileDescriptor,
        string path,
        int flags,
        uint mask,
        out LinuxStatx result);

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)]
        public uint Mask;

        // Linux fixes stx_btime at byte 80 in the 256-byte statx ABI on every supported architecture.
        [FieldOffset(80)]
        public LinuxStatxTimestamp BirthTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStatxTimestamp
    {
        public long Seconds;
        public uint Nanoseconds;
        private readonly int _reserved;
    }

    // .NET reports a missing entry (and a platform with no usable creation time) as the FILETIME epoch rather
    // than by throwing, so the sentinel has to be filtered here or an absent directory would compare equal to
    // another absent one and read as "same checkout".
    private static readonly DateTime UnsetCreationTimeUtc = DateTime.FromFileTimeUtc(0);

    /// <summary>
    /// Whether <paramref name="after"/> is positive evidence that a DIFFERENT checkout now occupies the root that
    /// <paramref name="before"/> described. Both sides must be known: an unreadable layout on either end answers
    /// false, because a re-bootstrap is a whole-repo rebuild and must be driven by evidence, never by its absence.
    /// </summary>
    public static bool IsReplacement(WorkspaceRootIdentity before, WorkspaceRootIdentity after)
    {
        if (!before.IsKnown || !after.IsKnown)
            return false;

        return !string.Equals(
                   Path.TrimEndingDirectorySeparator(before.GitDir!),
                   Path.TrimEndingDirectorySeparator(after.GitDir!),
                   ArtifactRootIdentity.ComparisonFor(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS()))
               || before.GitDirCreatedAtUtc != after.GitDirCreatedAtUtc;
    }
}
