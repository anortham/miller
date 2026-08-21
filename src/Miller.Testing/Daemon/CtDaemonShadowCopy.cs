using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Miller.Indexing;

namespace Miller.Testing;

/// <summary>
/// The image a spawn hands the daemon: the executable to launch, whether it is a private copy, and
/// the reason a copy did not happen. <paramref name="Reason"/> is null on the normal path.
/// </summary>
public sealed record CtDaemonImage(string Executable, bool IsShadowCopy, string? Reason);

/// <summary>
/// One file the copy would carry: its path relative to the source directory, its length, and its
/// last-write time. The build key is the digest of this whole set, so any file the daemon can load
/// moves the key when it is rebuilt.
/// </summary>
internal readonly record struct CtDaemonCopyEntry(string RelativePath, long Length, DateTime LastWriteUtc);

/// <summary>
/// A per-build private copy of the Miller binaries that the CT daemon runs from, so a live daemon
/// never holds the INSTALL or BUILD OUTPUT directory open.
///
/// <para>Why: a Windows process keeps its own image and every loaded DLL locked for its whole life.
/// A daemon launched straight from <c>bin/Release/net10.0/miller.exe</c> therefore made the next
/// <c>dotnet build</c> fail with MSB3027, and made a plugin upgrade fail to overwrite the installed
/// binary. The user's only recovery was Task Manager. Copying first removes the lock at its source:
/// the daemon holds files under <c>~/.miller/ct-daemon/&lt;key&gt;</c>, which nothing builds into and
/// nothing installs over.</para>
///
/// <para>The copy leaves out <c>.tools</c> — 164 MB of julie-extract, the semantic sidecar, and
/// vec0. The daemon needs none of it: <c>TestsCoreRequest</c> carries no tools root, the daemon
/// reads the index through <c>WorkspaceReadSessionFactory</c> (which names neither vec0 nor the
/// tools directory), and every test provider it spawns is found on PATH.</para>
///
/// <para>Every failure here falls back to the in-place spawn with a stated reason. A daemon that
/// starts and locks the install directory is worse than one that does not start only in a way the
/// user can fix; a daemon that does not start at all is worse than both.</para>
/// </summary>
public static class CtDaemonShadowCopy
{
    /// <summary>The directory under <c>~/.miller</c> that holds every build's copy.</summary>
    public const string DirectoryName = "ct-daemon";

    /// <summary>Written LAST inside a copy. Its absence means the copy is partial and unusable.</summary>
    internal const string ReadyMarkerName = ".ready";

    /// <summary>
    /// Holds each writer's in-progress copy. A leading dot keeps it out of the build-key namespace,
    /// so cleanup never considers it and a concurrent writer's staging is never deleted.
    /// </summary>
    internal const string StagingDirectoryName = ".staging";

    /// <summary>Not copied. See the type remarks: the daemon never reads it.</summary>
    private const string ExcludedDirectory = ".tools";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public static string RootDirectory() =>
        Path.Combine(MillerHome.ResolveMillerDirectory(), DirectoryName);

    /// <summary>
    /// The image to launch for <paramref name="executable"/>, materializing the copy when needed.
    /// Never throws: an unusable copy answers with the original executable and a reason.
    /// </summary>
    public static CtDaemonImage Resolve(string executable, string? version) =>
        Resolve(executable, version, RootDirectory());

    /// <summary>
    /// The image with no copy at all. Used when the caller supplies its own process starter: that
    /// starter decides what actually runs, so copying the output directory would buy nothing and
    /// cost a full tree copy inside every test that fakes a spawn.
    /// </summary>
    public static CtDaemonImage InPlace(string executable, string? version)
    {
        _ = version;
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        return new CtDaemonImage(executable, false, "the caller supplied its own process starter");
    }

    internal static CtDaemonImage Resolve(string executable, string? version, string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        try
        {
            var source = new FileInfo(executable);
            if (!source.Exists)
                return new CtDaemonImage(executable, false, "the executable does not exist");

            string sourceDirectory = source.DirectoryName
                ?? throw new IOException($"'{executable}' has no parent directory.");
            string key = BuildKey(version, sourceDirectory, EnumerateCopySet(sourceDirectory));
            string target = Path.Combine(root, key);
            string targetExecutable = Path.Combine(target, source.Name);

            if (!IsReady(target) || !File.Exists(targetExecutable))
                Materialize(root, sourceDirectory, target, key);

            if (!File.Exists(targetExecutable))
                return new CtDaemonImage(executable, false, "the private copy has no executable");

            Cleanup(root, key, source.Name);
            return new CtDaemonImage(targetExecutable, true, null);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException
                or ArgumentException or System.Security.SecurityException)
        {
            return new CtDaemonImage(executable, false, ex.Message);
        }
    }

    /// <summary>
    /// The copy's directory name: a digest of the WHOLE file set the copy would carry, plus the
    /// source directory.
    ///
    /// <para>The version string alone is NOT enough. It carries the release version plus the git
    /// short SHA, which does not move between commits, so every rebuild inside one commit — the
    /// whole inner development loop — would reuse a copy of the PREVIOUS build and run stale code.
    /// </para>
    ///
    /// <para>The EXECUTABLE alone is not enough either, and that is the trap this key fell into
    /// first. .NET does not re-stamp the apphost when only a referenced project rebuilds: after an
    /// edit to <c>Miller.Testing</c>, <c>Miller.Testing.dll</c> is rewritten while <c>miller.exe</c>
    /// keeps its old length and last-write time to the tick (measured on Windows, 2026-08-21). The
    /// daemon's own logic lives in that DLL, so a key built from the apphost stood still on exactly
    /// the rebuild it exists to notice. Every file's relative path, length and last-write time
    /// therefore enter the digest — the same set <see cref="CopyTree"/> copies, so a file that
    /// cannot reach the copy cannot move the key either.</para>
    ///
    /// <para>The source directory joins them because two installs of the same build must not share
    /// one copy.</para>
    /// </summary>
    internal static string BuildKey(
        string? version,
        string sourceDirectory,
        IReadOnlyList<CtDaemonCopyEntry> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentNullException.ThrowIfNull(files);

        var material = new StringBuilder(KeyText(Normalize(sourceDirectory)));
        // Sorted HERE, not only at the enumeration: the filesystem does not promise an enumeration
        // order, and two orders of one build must not key two copies.
        foreach (CtDaemonCopyEntry file in files.OrderBy(entry => KeyText(entry.RelativePath), StringComparer.Ordinal))
        {
            material.Append(
                System.Globalization.CultureInfo.InvariantCulture,
                $"|{KeyText(file.RelativePath)}|{file.Length}|{file.LastWriteUtc.Ticks}");
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()));
        string stamp = Convert.ToHexStringLower(digest.AsSpan(0, 6));
        string label = SanitizeVersion(version);
        return label.Length == 0 ? stamp : $"{label}-{stamp}";
    }

    /// <summary>
    /// Every file the copy would carry, in a stable order. This is the key's material AND the copy's
    /// work list, so the two can never disagree about what a build is.
    /// </summary>
    internal static IReadOnlyList<CtDaemonCopyEntry> EnumerateCopySet(string sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);

        string source = Path.GetFullPath(sourceDirectory);
        var entries = new List<CtDaemonCopyEntry>();
        foreach (FileInfo file in new DirectoryInfo(source).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file.FullName);
            if (IsExcluded(relative))
                continue;

            entries.Add(new CtDaemonCopyEntry(relative, file.Length, file.LastWriteTimeUtc));
        }

        entries.Sort((left, right) =>
            string.CompareOrdinal(KeyText(left.RelativePath), KeyText(right.RelativePath)));
        return entries;
    }

    /// <summary>
    /// Path text as the key sees it: one separator shape, and case folded where the filesystem folds
    /// it. Without this a rebuild that only changed a path's case would key a second copy of the
    /// same build on Windows.
    /// </summary>
    private static string KeyText(string path)
    {
        string normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return PathComparison == StringComparison.OrdinalIgnoreCase
            ? normalized.ToLowerInvariant()
            : normalized;
    }

    /// <summary>
    /// Copies that may be deleted: every entry that is neither the build about to launch nor a
    /// build a live daemon runs from. The current key is excluded first and unconditionally — a
    /// probe that fails to see our own daemon must still never delete the copy we just made.
    /// </summary>
    internal static IReadOnlyList<string> SelectRemovableCopies(
        IReadOnlyList<string> entries,
        string currentKey,
        IReadOnlyCollection<string> inUseKeys)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentKey);
        ArgumentNullException.ThrowIfNull(inUseKeys);

        var removable = new List<string>();
        foreach (string entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            // A dotted entry is control state (the staging area), never a build copy.
            if (entry.StartsWith('.'))
                continue;
            if (string.Equals(entry, currentKey, PathComparison))
                continue;
            if (inUseKeys.Any(key => string.Equals(key, entry, PathComparison)))
                continue;
            removable.Add(entry);
        }

        return removable;
    }

    /// <summary>
    /// The copy key an executable path belongs to, or null when the path is not inside
    /// <paramref name="root"/>. This is how a live daemon's process image is mapped back to the
    /// copy that must survive cleanup.
    /// </summary>
    internal static string? KeyForExecutablePath(string root, string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (string.IsNullOrWhiteSpace(executablePath))
            return null;

        string normalizedRoot = Normalize(root);
        string normalizedPath = Normalize(executablePath);
        string prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!normalizedPath.StartsWith(prefix, PathComparison))
            return null;

        string relative = normalizedPath[prefix.Length..];
        int separator = relative.IndexOf(Path.DirectorySeparatorChar);
        string key = separator < 0 ? relative : relative[..separator];
        return key.Length == 0 || key.StartsWith('.') ? null : key;
    }

    internal static bool IsExcluded(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        string first = relativePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        return string.Equals(first, ExcludedDirectory, PathComparison);
    }

    internal static bool IsReady(string copyDirectory) =>
        File.Exists(Path.Combine(copyDirectory, ReadyMarkerName));

    /// <summary>
    /// Stages the copy under a private name, then MOVES it into place, so a reader never sees a
    /// half-copied directory as a usable build. The ready marker is written before the move for the
    /// same reason: the move is the single instant at which the copy becomes launchable.
    /// </summary>
    internal static void Materialize(string root, string sourceDirectory, string target, string key)
    {
        string staging = Path.Combine(
            root,
            StagingDirectoryName,
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{Environment.ProcessId}-{Environment.CurrentManagedThreadId}"));
        TryDeleteDirectory(staging);
        Directory.CreateDirectory(staging);
        CopyTree(sourceDirectory, staging);
        File.WriteAllText(Path.Combine(staging, ReadyMarkerName), key);

        if (Directory.Exists(target))
        {
            // Another process finished the same build while this one copied. Its copy is as good as
            // ours, and it may already be running from it.
            if (IsReady(target))
            {
                TryDeleteDirectory(staging);
                return;
            }

            TryDeleteDirectory(target);
        }

        Directory.CreateDirectory(root);
        try
        {
            Directory.Move(staging, target);
        }
        catch (IOException) when (IsReady(target))
        {
            TryDeleteDirectory(staging);
        }
    }

    internal static void CopyTree(string sourceDirectory, string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        string source = Path.GetFullPath(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        // The SAME set the key digests. One enumeration serves both jobs, so a build identity and
        // the files a daemon can load cannot drift apart.
        foreach (CtDaemonCopyEntry entry in EnumerateCopySet(source))
        {
            string destination = Path.Combine(destinationDirectory, entry.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(source, entry.RelativePath), destination, overwrite: true);
        }
    }

    /// <summary>
    /// Removes copies of other builds. Best effort by design: on Windows a running daemon's image
    /// is locked, so the delete simply fails and the copy stays — the OS enforces the same rule the
    /// process probe expresses.
    ///
    /// <para>Three jobs, because each answers a different way for a copy to be left behind. The
    /// staging sweep reclaims the in-progress copy of a writer that crashed — nothing else ever
    /// looks at that directory, and each orphan holds a full copy (about 83 MB). The process probe
    /// is the precise answer and runs whenever it can. When it CANNOT — an elevated miller instance,
    /// or one that exits mid-probe, makes it inconclusive — the round used to delete nothing at all,
    /// so copies grew without limit on any machine where the probe keeps failing. The age
    /// sweep is the floor under that case: only copies older than <see cref="StaleCopyAge"/>, and
    /// only ones whose own executable opens for writing, which proves no process has that image
    /// mapped.</para>
    /// </summary>
    internal static void Cleanup(string root, string currentKey, string executableName)
    {
        if (!Directory.Exists(root))
            return;

        SweepStaging(root, DateTime.UtcNow);
        if (TryCollectInUseKeys(root, out HashSet<string> inUse))
        {
            CleanupWith(root, currentKey, inUse);
            return;
        }

        SweepStaleCopies(root, currentKey, executableName, DateTime.UtcNow);
    }

    /// <summary>
    /// How old an unused copy must be before an INCONCLUSIVE process probe may remove it. Generous
    /// on purpose: this path has no proof that a daemon is idle beyond the file lock, so it waits
    /// out any plausible run first.
    /// </summary>
    internal static readonly TimeSpan StaleCopyAge = TimeSpan.FromDays(3);

    /// <summary>How old a staging directory must be before it counts as a crashed writer's orphan.</summary>
    internal static readonly TimeSpan StagingOrphanAge = TimeSpan.FromDays(1);

    /// <summary>
    /// Copies old enough for the age sweep to consider: not the current one, not control state, and
    /// last written longer ago than <paramref name="maxAge"/>. Whether each one is IDLE is a
    /// separate question, answered per copy by <see cref="IsIdleCopy"/>.
    /// </summary>
    internal static IReadOnlyList<string> SelectStaleCopies(
        IReadOnlyList<(string Name, DateTime LastWriteUtc)> entries,
        string currentKey,
        DateTime nowUtc,
        TimeSpan maxAge)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentKey);

        var stale = new List<string>();
        foreach ((string name, DateTime lastWriteUtc) in entries)
        {
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.'))
                continue;
            if (string.Equals(name, currentKey, PathComparison))
                continue;
            if (nowUtc - lastWriteUtc <= maxAge)
                continue;

            stale.Add(name);
        }

        return stale;
    }

    /// <summary>
    /// Whether a copy has no process running from it. A live process keeps its own image open, so an
    /// executable that opens for WRITING is one nobody is running. A copy whose executable is gone is
    /// unusable anyway and counts as idle.
    ///
    /// <para>This probe never modifies the file: the mode is Open, so nothing is created and nothing
    /// is truncated.</para>
    /// </summary>
    internal static bool IsIdleCopy(string copyDirectory, string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(copyDirectory);
        if (string.IsNullOrWhiteSpace(executableName))
            return false;

        string image = Path.Combine(copyDirectory, executableName);
        try
        {
            if (!File.Exists(image))
                return true;

            using (new FileStream(image, FileMode.Open, FileAccess.Write, FileShare.None))
            {
            }

            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    private static void SweepStaleCopies(
        string root,
        string currentKey,
        string executableName,
        DateTime nowUtc)
    {
        foreach (string entry in SelectStaleCopies(ReadEntryAges(root), currentKey, nowUtc, StaleCopyAge))
        {
            string copy = Path.Combine(root, entry);
            if (IsIdleCopy(copy, executableName))
                TryDeleteDirectory(copy);
        }
    }

    /// <summary>
    /// Reclaims the in-progress copies of writers that died. <see cref="Materialize"/> deletes only
    /// the staging path of THIS process and thread, and <see cref="SelectRemovableCopies"/> skips
    /// every dotted entry, so without this sweep nothing on any path ever removed a dead one.
    /// </summary>
    internal static void SweepStaging(string root, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        string staging = Path.Combine(root, StagingDirectoryName);
        if (!Directory.Exists(staging))
            return;

        foreach ((string name, DateTime lastWriteUtc) in ReadEntryAges(staging))
        {
            if (nowUtc - lastWriteUtc > StagingOrphanAge)
                TryDeleteDirectory(Path.Combine(staging, name));
        }
    }

    private static IReadOnlyList<(string Name, DateTime LastWriteUtc)> ReadEntryAges(string directory)
    {
        try
        {
            return new DirectoryInfo(directory)
                .EnumerateDirectories()
                .Select(entry => (entry.Name, entry.LastWriteTimeUtc))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The cleanup with the in-use set supplied. Separate from <see cref="Cleanup"/> because the
    /// production probe reads every live <c>miller</c> process on the machine, which a test must
    /// never depend on.
    /// </summary>
    internal static void CleanupWith(string root, string currentKey, IReadOnlyCollection<string> inUseKeys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(inUseKeys);
        if (!Directory.Exists(root))
            return;

        string[] entries;
        try
        {
            entries = Directory.EnumerateDirectories(root).Select(Path.GetFileName).ToArray()!;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (string entry in SelectRemovableCopies(entries, currentKey, inUseKeys))
            TryDeleteDirectory(Path.Combine(root, entry));
    }

    private static bool TryCollectInUseKeys(string root, out HashSet<string> keys)
    {
        keys = new HashSet<string>(
            PathComparison == StringComparison.OrdinalIgnoreCase
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("miller");
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return false;
        }

        try
        {
            foreach (Process process in processes)
            {
                string? image;
                try
                {
                    image = process.MainModule?.FileName;
                }
                catch (Exception)
                {
                    // Access denied, or the process exited mid-probe. Either way this round cannot
                    // prove which copies are idle, so it deletes nothing.
                    return false;
                }

                if (image is null)
                    return false;
                if (KeyForExecutablePath(root, image) is { } key)
                    keys.Add(key);
            }

            return true;
        }
        finally
        {
            foreach (Process process in processes)
                process.Dispose();
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string SanitizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return string.Empty;

        var builder = new StringBuilder(version.Length);
        foreach (char c in version)
        {
            builder.Append(
                char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'
                    ? c
                    : '-');
            if (builder.Length == 40)
                break;
        }

        return builder.ToString().Trim('-');
    }

    private static string Normalize(string path)
    {
        string full = Path.GetFullPath(path);
        string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? full : trimmed;
    }
}
