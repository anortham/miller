using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Miller.Testing;
using Miller.Testing.Providers.Shared;

namespace Miller.Testing.Providers.Jvm;

internal sealed record SbtWorkspaceShadowResult(
    string WorkspaceCandidateRoot,
    string DependencyCandidateRoot,
    string ShadowRoot,
    string ShadowProjectPath,
    int EntriesScanned,
    int EntriesCopied,
    int EntriesUpdated,
    int EntriesDeleted,
    long BytesCopied,
    int HashFallbacks,
    TimeSpan Elapsed,
    long WorkspaceCandidateBytes,
    long DependencyCandidateBytes);

internal static class SbtWorkspaceShadow
{
    private const string WorkspaceCacheName = "sbt-workspace";
    private const string DependencyCacheName = "sbt-deps";
    private const string ShadowDirectoryName = "build";
    private const string ManifestFileName = "manifest.json";
    private const string GitDirectoryName = ".git";
    private const int CopyAttempts = 3;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SyncGates = new(StringComparer.Ordinal);

    internal static SbtWorkspaceShadowResult Sync(
        ContinuousTestWorkspace workspace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        cancellationToken.ThrowIfCancellationRequested();
        string workspaceCandidateRoot = CtGenerationPaths.CacheDirectory(workspace, WorkspaceCacheName);
        SemaphoreSlim gate = SyncGates.GetOrAdd(workspaceCandidateRoot, static _ => new SemaphoreSlim(1, 1));
        gate.Wait(cancellationToken);
        try
        {
            return SyncCore(workspace, workspaceCandidateRoot, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private static SbtWorkspaceShadowResult SyncCore(
        ContinuousTestWorkspace workspace,
        string workspaceCandidateRoot,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        string sourceRoot = JvmTestTooling.ProjectRoot(workspace);
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"sbt build root does not exist: {sourceRoot}");

        string dependencyCandidateRoot = CtGenerationPaths.CacheDirectory(workspace, DependencyCacheName);
        string shadowRoot = Path.Combine(workspaceCandidateRoot, ShadowDirectoryName);

        List<SourceEntry> sourceEntries = EnumerateSourceEntries(sourceRoot, cancellationToken).ToList();
        ValidateCaseCollisions(sourceEntries);
        ValidateLinkCycles(sourceRoot, sourceEntries);
        ValidatePathBudget(shadowRoot, sourceEntries);
        ValidateSourceBudget(sourceEntries);

        Directory.CreateDirectory(shadowRoot);
        Directory.CreateDirectory(dependencyCandidateRoot);
        CreateGitBarrier(shadowRoot);

        Dictionary<string, ManifestEntry> previousEntries = ReadManifest(workspaceCandidateRoot);
        List<ManifestEntry> entries = [];
        HashSet<string> seenEntries = new(StringComparer.Ordinal);
        int entriesScanned = 0;
        int entriesCopied = 0;
        int entriesUpdated = 0;
        int entriesDeleted = 0;
        int hashFallbacks = 0;
        long bytesCopied = 0;

        foreach (SourceEntry sourceEntry in sourceEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = sourceEntry.RelativePath;
            string destinationPath = Path.Combine(shadowRoot, relativePath);
            EnsureDestinationDirectory(destinationPath);
            ManifestEntry entry = CaptureEntry(sourceEntry);
            bool existed = previousEntries.ContainsKey(relativePath);
            if (!EntryMatches(entry, destinationPath, out bool hashFallback))
            {
                bool copied = false;
                for (int attempt = 0; attempt < CopyAttempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ManifestEntry before = CaptureEntry(sourceEntry);
                    ReplaceDestination(sourceEntry, destinationPath, cancellationToken);
                    ManifestEntry after = CaptureEntry(sourceEntry);
                    if (SourceSnapshotsMatch(before, after))
                    {
                        entry = after;
                        ApplyMetadata(entry, destinationPath);
                        copied = true;
                        break;
                    }
                }

                if (!copied)
                    throw new IOException($"sbt source changed during sync: '{relativePath}'");

                if (sourceEntry.Kind != EntryKind.Directory)
                {
                    if (existed)
                        entriesUpdated++;
                    else
                        entriesCopied++;
                    bytesCopied += entry.Length;
                }
            }
            if (hashFallback)
                hashFallbacks++;

            entries.Add(entry);
            seenEntries.Add(relativePath);
            entriesScanned++;
        }

        foreach (string relativePath in previousEntries.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (seenEntries.Contains(relativePath)
                || IsAncestorOfSeenEntry(relativePath, seenEntries)
                || IsBuildOwned(relativePath))
                continue;

            string destinationPath = Path.Combine(shadowRoot, relativePath);
            if (RemoveStaleEntry(destinationPath, shadowRoot, cancellationToken))
                entriesDeleted++;
        }

        WriteManifest(workspaceCandidateRoot, entries);
        stopwatch.Stop();

        return new SbtWorkspaceShadowResult(
            workspaceCandidateRoot,
            dependencyCandidateRoot,
            shadowRoot,
            Path.Combine(shadowRoot, Path.GetRelativePath(sourceRoot, Path.GetFullPath(workspace.ProjectPath))),
            entriesScanned,
            entriesCopied,
            entriesUpdated,
            entriesDeleted,
            bytesCopied,
            hashFallbacks,
            stopwatch.Elapsed,
            DirectoryBytes(workspaceCandidateRoot),
            DirectoryBytes(dependencyCandidateRoot));
    }

    private static IEnumerable<SourceEntry> EnumerateSourceEntries(
        string sourceRoot,
        CancellationToken cancellationToken)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(sourceRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = Path.GetRelativePath(sourceRoot, path);
            if (IsExcluded(relativePath))
                continue;

            FileSystemInfo info = Directory.Exists(path)
                ? new DirectoryInfo(path)
                : new FileInfo(path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                string linkTarget = info.LinkTarget
                    ?? throw new IOException($"unsupported reparse point at '{relativePath}'");
                string resolvedTarget = ResolveLinkTarget(sourceRoot, path, linkTarget, relativePath);
                yield return new SourceEntry(
                    path,
                    relativePath,
                    EntryKind.SymbolicLink,
                    linkTarget,
                    Directory.Exists(resolvedTarget));
                continue;
            }

            if (info is DirectoryInfo)
            {
                yield return new SourceEntry(path, relativePath, EntryKind.Directory, null, false);
                foreach (SourceEntry child in EnumerateSourceEntries(path, cancellationToken))
                {
                    yield return child with { RelativePath = Path.Combine(relativePath, child.RelativePath) };
                }
            }
            else
            {
                if (!IsRegularFile(path))
                    throw new IOException($"unsupported special file '{relativePath}'");
                yield return new SourceEntry(path, relativePath, EntryKind.File, null, false);
            }
        }
    }

    private static bool IsRegularFile(string path)
    {
        if (OperatingSystem.IsWindows())
            return true;

        IntPtr statBuffer = Marshal.AllocHGlobal(512);
        try
        {
            if (LStat(path, statBuffer) != 0)
                throw new IOException($"could not inspect source entry '{path}'",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            uint mode = unchecked((uint)Marshal.ReadInt32(statBuffer, 24));
            return (mode & UnixFileTypeMask) == UnixRegularFileType;
        }
        finally
        {
            Marshal.FreeHGlobal(statBuffer);
        }
    }

    private static bool IsExcluded(string relativePath)
    {
        foreach (string segment in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.Equals(segment, ".miller", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "target", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string ResolveLinkTarget(
        string sourceRoot,
        string sourcePath,
        string linkTarget,
        string relativePath)
    {
        if (Path.IsPathRooted(linkTarget))
            throw new IOException($"unsafe symbolic link '{relativePath}' must use a relative target");
        string resolvedTarget = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(sourcePath)!, linkTarget));
        string rootWithSeparator = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot))
            + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!resolvedTarget.StartsWith(rootWithSeparator, comparison)
            && !string.Equals(resolvedTarget, Path.GetFullPath(sourceRoot), comparison))
            throw new IOException($"unsafe symbolic link '{relativePath}' targets outside the sbt build root");
        return resolvedTarget;
    }

    private static void ValidateCaseCollisions(IReadOnlyList<SourceEntry> sourceEntries)
    {
        Dictionary<string, string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (SourceEntry entry in sourceEntries)
        {
            if (paths.TryGetValue(entry.RelativePath, out string? existing)
                && !string.Equals(existing, entry.RelativePath, StringComparison.Ordinal))
                throw new IOException(
                    $"case-colliding sbt build entries '{existing}' and '{entry.RelativePath}'");
            paths[entry.RelativePath] = entry.RelativePath;
        }
    }

    private static void ValidateLinkCycles(string sourceRoot, IReadOnlyList<SourceEntry> sourceEntries)
    {
        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        Dictionary<string, SourceEntry> links = sourceEntries
            .Where(entry => entry.Kind == EntryKind.SymbolicLink)
            .ToDictionary(entry => Path.GetFullPath(entry.SourcePath), comparer);

        foreach (SourceEntry link in links.Values)
        {
            HashSet<string> visited = new(comparer);
            SourceEntry current = link;
            while (true)
            {
                string currentPath = Path.GetFullPath(current.SourcePath);
                if (!visited.Add(currentPath))
                    throw new IOException($"looping symbolic link '{current.RelativePath}'");
                if (current.LinkTarget is null)
                    break;
                string resolvedTarget = ResolveLinkTarget(
                    sourceRoot,
                    current.SourcePath,
                    current.LinkTarget,
                    current.RelativePath);
                if (!links.TryGetValue(resolvedTarget, out current!))
                    break;
            }
        }
    }

    private static void ValidatePathBudget(string shadowRoot, IReadOnlyList<SourceEntry> sourceEntries)
    {
        foreach (SourceEntry entry in sourceEntries)
        {
            string destinationPath = Path.Combine(shadowRoot, entry.RelativePath);
            if (destinationPath.Length > ContinuousTestProjectInventory.WindowsPathBudget)
                throw new IOException(
                    $"sbt build entry '{entry.RelativePath}' exceeds the Windows path budget");
        }
    }

    private static void ValidateSourceBudget(IReadOnlyList<SourceEntry> sourceEntries)
    {
        long totalBytes = 0;
        foreach (SourceEntry entry in sourceEntries)
        {
            if (entry.Kind != EntryKind.File)
                continue;
            long length = new FileInfo(entry.SourcePath).Length;
            totalBytes = checked(totalBytes + length);
            if (totalBytes > ContinuousTestCoordinatorOptions.DefaultBuildCacheBudgetBytes)
                throw new IOException(
                    $"sbt build entry '{entry.RelativePath}' exceeds the workspace build-cache budget");
        }
    }

    private static bool IsBuildOwned(string relativePath) =>
        relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => string.Equals(segment, "target", StringComparison.OrdinalIgnoreCase));

    private static bool RemoveStaleEntry(
        string destinationPath,
        string shadowRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(destinationPath) || IsReparsePoint(destinationPath))
        {
            if (!Directory.Exists(Path.GetDirectoryName(destinationPath)))
                return false;
            try
            {
                File.Delete(destinationPath);
                return true;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return false;
            }
        }
        if (!Directory.Exists(destinationPath))
            return false;

        foreach (string childPath in Directory.EnumerateFileSystemEntries(destinationPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string childRelativePath = Path.GetRelativePath(shadowRoot, childPath);
            if (IsBuildOwned(childRelativePath))
                continue;
            RemoveStaleEntry(childPath, shadowRoot, cancellationToken);
        }

        if (!Directory.EnumerateFileSystemEntries(destinationPath).Any())
        {
            Directory.Delete(destinationPath);
            return true;
        }

        return false;
    }

    private static bool IsAncestorOfSeenEntry(string relativePath, IEnumerable<string> seenEntries)
    {
        string prefix = relativePath + Path.DirectorySeparatorChar;
        return seenEntries.Any(entry => entry.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static void EnsureDestinationDirectory(string destinationPath)
    {
        string directoryPath = Path.GetDirectoryName(destinationPath)!;
        if (File.Exists(directoryPath))
            File.Delete(directoryPath);
        Directory.CreateDirectory(directoryPath);
    }

    private static ManifestEntry CaptureEntry(SourceEntry sourceEntry)
    {
        FileSystemInfo sourceInfo = sourceEntry.Kind == EntryKind.Directory
            ? new DirectoryInfo(sourceEntry.SourcePath)
            : new FileInfo(sourceEntry.SourcePath);
        return new ManifestEntry(
            sourceEntry.RelativePath,
            sourceEntry.Kind,
            sourceEntry.Kind == EntryKind.File ? ((FileInfo)sourceInfo).Length : 0,
            sourceInfo.LastWriteTimeUtc.Ticks,
            sourceEntry.LinkTarget,
            sourceEntry.Kind == EntryKind.File ? ComputeHash(sourceEntry.SourcePath) : null,
            ReadUnixMode(sourceEntry.SourcePath, sourceEntry.Kind),
            sourceInfo.Attributes.HasFlag(FileAttributes.ReadOnly));
    }

    private static bool EntryMatches(ManifestEntry entry, string destinationPath, out bool hashFallback)
    {
        hashFallback = false;
        if (!PathExists(destinationPath))
            return false;

        FileSystemInfo destinationInfo = entry.Kind == EntryKind.Directory
            ? new DirectoryInfo(destinationPath)
            : new FileInfo(destinationPath);
        if (entry.Kind == EntryKind.SymbolicLink)
            return destinationInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)
                && string.Equals(destinationInfo.LinkTarget, entry.LinkTarget, StringComparison.Ordinal);
        if (destinationInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return false;
        if (entry.Kind == EntryKind.Directory)
            return Directory.Exists(destinationPath);

        FileInfo destinationFile = (FileInfo)destinationInfo;
        if (!File.Exists(destinationPath)
            || destinationFile.Length != entry.Length
            || destinationFile.LastWriteTimeUtc.Ticks != entry.LastWriteTimeUtcTicks)
            return false;

        if (entry.UnixMode.HasValue && ReadUnixMode(destinationPath, entry.Kind) != entry.UnixMode)
            return false;
        if (entry.IsReadOnly != destinationInfo.Attributes.HasFlag(FileAttributes.ReadOnly))
            return false;

        hashFallback = true;
        return string.Equals(ComputeHash(destinationPath), entry.Hash, StringComparison.Ordinal);
    }

    private static string ComputeHash(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool SourceSnapshotsMatch(ManifestEntry before, ManifestEntry after) =>
        before.Kind == after.Kind
        && before.Length == after.Length
        && before.LastWriteTimeUtcTicks == after.LastWriteTimeUtcTicks
        && string.Equals(before.LinkTarget, after.LinkTarget, StringComparison.Ordinal)
        && before.Hash == after.Hash
        && before.UnixMode == after.UnixMode
        && before.IsReadOnly == after.IsReadOnly;

    private static void ReplaceDestination(
        SourceEntry sourceEntry,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        DeleteDestination(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();
        switch (sourceEntry.Kind)
        {
            case EntryKind.Directory:
                Directory.CreateDirectory(destinationPath);
                break;
            case EntryKind.SymbolicLink:
                if (sourceEntry.IsDirectoryLink)
                    Directory.CreateSymbolicLink(destinationPath, sourceEntry.LinkTarget!);
                else
                    File.CreateSymbolicLink(destinationPath, sourceEntry.LinkTarget!);
                break;
            default:
                CopyFileAtomically(sourceEntry.SourcePath, destinationPath, cancellationToken);
                break;
        }
    }

    private static void DeleteDestination(string destinationPath)
    {
        if (File.Exists(destinationPath) || IsReparsePoint(destinationPath))
            File.Delete(destinationPath);
        else if (Directory.Exists(destinationPath))
            Directory.Delete(destinationPath, recursive: true);
    }

    private static void ApplyMetadata(ManifestEntry entry, string destinationPath)
    {
        if (entry.Kind == EntryKind.SymbolicLink)
            return;
        File.SetLastWriteTimeUtc(destinationPath, new DateTime(entry.LastWriteTimeUtcTicks, DateTimeKind.Utc));
        if (entry.UnixMode.HasValue && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(destinationPath, (UnixFileMode)entry.UnixMode.Value);
        FileAttributes attributes = File.GetAttributes(destinationPath);
        File.SetAttributes(
            destinationPath,
            entry.IsReadOnly ? attributes | FileAttributes.ReadOnly : attributes & ~FileAttributes.ReadOnly);
    }

    private static int? ReadUnixMode(string path, EntryKind kind)
    {
        if (OperatingSystem.IsWindows() || kind == EntryKind.SymbolicLink)
            return null;
        return (int)File.GetUnixFileMode(path);
    }

    private static void CopyFileAtomically(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        string tempPath = $"{destinationPath}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
        try
        {
            using (FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream destination = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    destination.Write(buffer, 0, read);
                }
                destination.Flush(flushToDisk: true);
            }
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static bool PathExists(string path) =>
        File.Exists(path) || Directory.Exists(path) || IsReparsePoint(path);

    private static bool IsReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            try
            {
                return new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return false;
            }
        }

        return new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int LStat(string path, IntPtr statBuffer);

    private const uint UnixFileTypeMask = 0xF000;
    private const uint UnixRegularFileType = 0x8000;

    private static Dictionary<string, ManifestEntry> ReadManifest(string workspaceCandidateRoot)
    {
        string manifestPath = Path.Combine(workspaceCandidateRoot, ManifestFileName);
        if (!File.Exists(manifestPath))
            return new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);

        try
        {
            List<ManifestEntry>? manifestEntries = JsonSerializer.Deserialize<List<ManifestEntry>>(
                File.ReadAllText(manifestPath));
            Dictionary<string, ManifestEntry> entries = new(StringComparer.Ordinal);
            if (manifestEntries is null)
                return entries;
            string shadowRoot = Path.Combine(workspaceCandidateRoot, ShadowDirectoryName);
            foreach (ManifestEntry entry in manifestEntries)
            {
                string normalizedPath = NormalizeManifestPath(entry.Path, shadowRoot);
                entries.Add(normalizedPath, entry with { Path = normalizedPath });
            }

            return entries;
        }
        catch (JsonException)
        {
            return RecoverManifestEntries(workspaceCandidateRoot);
        }
        catch (NotSupportedException)
        {
            return RecoverManifestEntries(workspaceCandidateRoot);
        }
    }

    private static string NormalizeManifestPath(string path, string shadowRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            throw new IOException($"unsafe sbt shadow manifest path '{path}'");
        string normalizedInput = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        string[] segments = normalizedInput.Split(Path.DirectorySeparatorChar);
        if (segments.Any(segment => segment is "" or "." or ".."))
            throw new IOException($"unsafe sbt shadow manifest path '{path}'");

        string shadowRootFullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(shadowRoot));
        string fullPath = Path.GetFullPath(Path.Combine(shadowRootFullPath, normalizedInput));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(shadowRootFullPath + Path.DirectorySeparatorChar, comparison))
            throw new IOException($"unsafe sbt shadow manifest path '{path}'");
        return normalizedInput;
    }

    private static Dictionary<string, ManifestEntry> RecoverManifestEntries(string workspaceCandidateRoot)
    {
        string shadowRoot = Path.Combine(workspaceCandidateRoot, ShadowDirectoryName);
        Dictionary<string, ManifestEntry> entries = new(StringComparer.Ordinal);
        if (!Directory.Exists(shadowRoot))
            return entries;

        foreach (SourceEntry sourceEntry in EnumerateSourceEntries(shadowRoot, CancellationToken.None))
        {
            if (IsBuildOwned(sourceEntry.RelativePath))
                continue;
            ManifestEntry entry = CaptureEntry(sourceEntry);
            entries[entry.Path] = entry;
        }

        return entries;
    }

    private static void CreateGitBarrier(string shadowRoot)
    {
        string gitRoot = Path.Combine(shadowRoot, GitDirectoryName);
        Directory.CreateDirectory(gitRoot);
        string headPath = Path.Combine(gitRoot, "HEAD");
        if (!File.Exists(headPath))
            File.WriteAllText(headPath, "ref: refs/heads/miller-shadow\n");
        string configPath = Path.Combine(gitRoot, "config");
        if (!File.Exists(configPath))
            File.WriteAllText(
                configPath,
                "[core]\n\trepositoryformatversion = 0\n\tbare = false\n\tlogallrefupdates = false\n");
    }

    private static void WriteManifest(string workspaceCandidateRoot, IReadOnlyList<ManifestEntry> entries)
    {
        string manifestPath = Path.Combine(workspaceCandidateRoot, ManifestFileName);
        string tempPath = $"{manifestPath}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(entries));
        File.Move(tempPath, manifestPath, overwrite: true);
    }

    private static long DirectoryBytes(string path)
    {
        if (!Directory.Exists(path))
            return 0;

        long total = 0;
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            total += new FileInfo(file).Length;
        return total;
    }

    private enum EntryKind
    {
        File,
        Directory,
        SymbolicLink,
    }

    private sealed record SourceEntry(
        string SourcePath,
        string RelativePath,
        EntryKind Kind,
        string? LinkTarget,
        bool IsDirectoryLink);

    private sealed record ManifestEntry(
        string Path,
        EntryKind Kind,
        long Length,
        long LastWriteTimeUtcTicks,
        string? LinkTarget,
        string? Hash,
        int? UnixMode,
        bool IsReadOnly);
}
