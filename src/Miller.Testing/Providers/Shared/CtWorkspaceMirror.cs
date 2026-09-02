using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Miller.Testing;

namespace Miller.Testing.Providers.Shared;

internal enum CtWorkspaceMirrorIntegrity
{
    StrictHash,
    MetadataFastPath,
}

internal sealed record CtWorkspaceMirrorPolicy
{
    public string ProviderName { get; }
    public string CacheName { get; }
    public string MirrorDirectoryName { get; }
    public IReadOnlySet<string> ExcludedEntryNames { get; }
    public IReadOnlySet<string> BuildOwnedEntryNames { get; }
    public bool CreateGitBarrier { get; }
    public CtWorkspaceMirrorIntegrity Integrity { get; }

    public CtWorkspaceMirrorPolicy(
        string ProviderName,
        string CacheName,
        string MirrorDirectoryName,
        IEnumerable<string> ExcludedEntryNames,
        IEnumerable<string> BuildOwnedEntryNames,
        bool CreateGitBarrier,
        CtWorkspaceMirrorIntegrity Integrity)
    {
        if (string.IsNullOrWhiteSpace(ProviderName))
            throw new ArgumentException("must not be blank", nameof(ProviderName));
        if (string.IsNullOrWhiteSpace(CacheName))
            throw new ArgumentException("must not be blank", nameof(CacheName));
        if (string.IsNullOrWhiteSpace(MirrorDirectoryName))
            throw new ArgumentException("must not be blank", nameof(MirrorDirectoryName));

        this.ProviderName = ProviderName;
        this.CacheName = CacheName;
        this.MirrorDirectoryName = MirrorDirectoryName;
        this.ExcludedEntryNames = new HashSet<string>(ExcludedEntryNames ?? throw new ArgumentNullException(nameof(ExcludedEntryNames)), StringComparer.OrdinalIgnoreCase);
        this.BuildOwnedEntryNames = new HashSet<string>(BuildOwnedEntryNames ?? throw new ArgumentNullException(nameof(BuildOwnedEntryNames)), StringComparer.OrdinalIgnoreCase);
        this.CreateGitBarrier = CreateGitBarrier;
        this.Integrity = Integrity;
    }
}

internal sealed record CtWorkspaceMirrorResult(
    string CandidateRoot,
    string MirrorRoot,
    int EntriesScanned,
    int EntriesCopied,
    int EntriesUpdated,
    int EntriesDeleted,
    long BytesCopied,
    int HashFallbacks,
    TimeSpan Elapsed,
    long CandidateBytes,
    long FilesHashed,
    long BytesHashed,
    string SourceMetadataDigest,
    bool SourceOwnedStateChanged);

internal static class CtWorkspaceMirror
{
    private const string ManifestFileName = "manifest.json";
    private const string GitDirectoryName = ".git";
    private const int CopyAttempts = 3;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SyncGates = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    internal static CtWorkspaceMirrorResult Sync(
        ContinuousTestWorkspace workspace,
        string sourceRoot,
        CtWorkspaceMirrorPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(policy);
        cancellationToken.ThrowIfCancellationRequested();
        string candidateRoot = CtGenerationPaths.CacheDirectory(workspace, policy.CacheName);
        SemaphoreSlim gate = SyncGates.GetOrAdd(candidateRoot, static _ => new SemaphoreSlim(1, 1));
        gate.Wait(cancellationToken);
        try
        {
            return SyncCore(workspace, Path.GetFullPath(sourceRoot), candidateRoot, policy, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    internal static long MeasureCandidateBytes(string candidateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateRoot);
        return DirectoryBytes(Path.GetFullPath(candidateRoot));
    }

    internal static void EnsurePathHasNoReparsePoint(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string current = Path.GetFullPath(path);
        while (true)
        {
            if (PathExists(current) && IsReparsePoint(current))
                throw new IOException($"mirror path is a reparse point: '{current}'");
            string? parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, StringComparison.Ordinal))
                return;
            current = parent;
        }
    }

    internal static string SourceMetadataDigest(
        string sourceRoot,
        CtWorkspaceMirrorPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        cancellationToken.ThrowIfCancellationRequested();
        string fullSourceRoot = Path.GetFullPath(sourceRoot);
        if (!Directory.Exists(fullSourceRoot))
            throw new DirectoryNotFoundException($"{policy.ProviderName} source root does not exist: {fullSourceRoot}");

        List<SourceEntry> entries = EnumerateSourceEntries(fullSourceRoot, policy, cancellationToken).ToList();
        ValidateCaseCollisions(entries, policy);
        ValidateLinkCycles(fullSourceRoot, entries, policy);
        return ComputeMetadataDigest(entries, cancellationToken);
    }

    internal static bool IsRegularFile(string path)
    {
        if (OperatingSystem.IsWindows())
            return true;
        if (OperatingSystem.IsLinux())
        {
            if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)
                || !TryReadLinuxFileMode(path, out uint mode))
                return false;
            return IsRegularFileType(mode);
        }
        if (OperatingSystem.IsMacOS())
        {
            if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)
                || !TryReadMacOsFileMode(path, out uint mode))
                return false;
            return IsRegularFileType(mode);
        }
        return false;
    }

    internal static bool IsRegularFileType(uint mode) => (mode & UnixFileTypeMask) == UnixRegularFileType;

    private static CtWorkspaceMirrorResult SyncCore(
        ContinuousTestWorkspace workspace,
        string sourceRoot,
        string candidateRoot,
        CtWorkspaceMirrorPolicy policy,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"{policy.ProviderName} source root does not exist: {sourceRoot}");

        string mirrorRoot = Path.Combine(candidateRoot, policy.MirrorDirectoryName);
        List<SourceEntry> sourceEntries = EnumerateSourceEntries(sourceRoot, policy, cancellationToken).ToList();
        ValidateCaseCollisions(sourceEntries, policy);
        ValidateLinkCycles(sourceRoot, sourceEntries, policy);
        ValidatePathBudget(mirrorRoot, sourceEntries, policy);
        ValidateSourceBudget(sourceEntries, policy, cancellationToken);
        string sourceMetadataDigest = ComputeMetadataDigest(sourceEntries, cancellationToken);

        EnsurePathHasNoReparsePoint(candidateRoot);
        EnsurePathHasNoReparsePoint(mirrorRoot);
        Directory.CreateDirectory(mirrorRoot);
        if (policy.CreateGitBarrier)
            CreateGitBarrier(mirrorRoot);

        Dictionary<string, ManifestEntry> previousEntries = ReadManifest(candidateRoot, mirrorRoot, policy);
        List<ManifestEntry> entries = [];
        HashSet<string> seenEntries = new(StringComparer.Ordinal);
        int entriesScanned = 0;
        int entriesCopied = 0;
        int entriesUpdated = 0;
        int entriesDeleted = 0;
        int hashFallbacks = 0;
        long bytesCopied = 0;
        long filesHashed = 0;
        long bytesHashed = 0;
        bool sourceOwnedStateChanged = false;

        foreach (SourceEntry sourceEntry in sourceEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = sourceEntry.RelativePath;
            string destinationPath = Path.Combine(mirrorRoot, relativePath);
            EnsureDestinationDirectory(destinationPath, mirrorRoot);
            ManifestEntry entry = CaptureEntry(
                sourceEntry,
                policy.Integrity == CtWorkspaceMirrorIntegrity.StrictHash,
                ref filesHashed,
                ref bytesHashed);
            bool existed = previousEntries.ContainsKey(relativePath);
            if (!existed || !EntryMatches(
                    entry,
                    destinationPath,
                    policy.Integrity,
                    ref hashFallbacks,
                    ref filesHashed,
                    ref bytesHashed))
            {
                bool copied = false;
                for (int attempt = 0; attempt < CopyAttempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ManifestEntry before = CaptureEntry(sourceEntry, true, ref filesHashed, ref bytesHashed);
                    ReplaceDestination(sourceEntry, destinationPath, cancellationToken);
                    ManifestEntry after = CaptureEntry(sourceEntry, true, ref filesHashed, ref bytesHashed);
                    if (SourceSnapshotsMatch(before, after))
                    {
                        entry = after;
                        ApplyMetadata(entry, destinationPath);
                        copied = true;
                        break;
                    }
                }

                if (!copied)
                    throw new IOException($"{policy.ProviderName} source changed during sync: '{relativePath}'");

                if (sourceEntry.Kind != EntryKind.Directory)
                {
                    if (existed)
                        entriesUpdated++;
                    else
                        entriesCopied++;
                    bytesCopied += entry.Length;
                }
                sourceOwnedStateChanged = true;
            }
            if (hashFallbacks > 0 && policy.Integrity == CtWorkspaceMirrorIntegrity.MetadataFastPath)
                throw new InvalidOperationException("metadata fast path unexpectedly hashed a destination");

            entries.Add(entry);
            seenEntries.Add(relativePath);
            entriesScanned++;
        }

        foreach (string relativePath in previousEntries.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (seenEntries.Contains(relativePath)
                || IsAncestorOfSeenEntry(relativePath, seenEntries)
                || IsBuildOwned(relativePath, policy))
                continue;

            string destinationPath = Path.Combine(mirrorRoot, relativePath);
            if (RemoveStaleEntry(destinationPath, mirrorRoot, policy, cancellationToken))
            {
                entriesDeleted++;
                sourceOwnedStateChanged = true;
            }
        }

        ValidateBuildOwnedPaths(mirrorRoot, policy);
        WriteManifest(candidateRoot, entries);
        stopwatch.Stop();

        return new CtWorkspaceMirrorResult(
            candidateRoot,
            mirrorRoot,
            entriesScanned,
            entriesCopied,
            entriesUpdated,
            entriesDeleted,
            bytesCopied,
            hashFallbacks,
            stopwatch.Elapsed,
            DirectoryBytes(candidateRoot),
            filesHashed,
            bytesHashed,
            sourceMetadataDigest,
            sourceOwnedStateChanged);
    }

    private static IEnumerable<SourceEntry> EnumerateSourceEntries(
        string sourceRoot,
        CtWorkspaceMirrorPolicy policy,
        CancellationToken cancellationToken)
        => EnumerateSourceEntries(sourceRoot, sourceRoot, policy, cancellationToken);

    private static IEnumerable<SourceEntry> EnumerateSourceEntries(
        string currentRoot,
        string sourceRoot,
        CtWorkspaceMirrorPolicy policy,
        CancellationToken cancellationToken)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(currentRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = Path.GetRelativePath(sourceRoot, path);
            if (IsExcluded(relativePath, policy))
                continue;

            FileSystemInfo info = Directory.Exists(path)
                ? new DirectoryInfo(path)
                : new FileInfo(path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                string linkTarget = info.LinkTarget
                    ?? throw new IOException($"unsupported reparse point at '{relativePath}'");
                string resolvedTarget = ResolveLinkTarget(sourceRoot, path, linkTarget, relativePath, policy);
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
                foreach (SourceEntry child in EnumerateSourceEntries(path, sourceRoot, policy, cancellationToken))
                    yield return child;
            }
            else
            {
                if (!IsRegularFile(path))
                    throw new IOException($"unsupported special file '{relativePath}'");
                yield return new SourceEntry(path, relativePath, EntryKind.File, null, false);
            }
        }
    }

    private static bool IsExcluded(string relativePath, CtWorkspaceMirrorPolicy policy)
    {
        foreach (string segment in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (policy.ExcludedEntryNames.Contains(segment))
                return true;
        }

        return false;
    }

    private static string ResolveLinkTarget(
        string sourceRoot,
        string sourcePath,
        string linkTarget,
        string relativePath,
        CtWorkspaceMirrorPolicy policy)
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
            throw new IOException($"unsafe symbolic link '{relativePath}' targets outside the {policy.ProviderName} source root");
        return resolvedTarget;
    }

    private static void ValidateCaseCollisions(
        IReadOnlyList<SourceEntry> sourceEntries,
        CtWorkspaceMirrorPolicy policy)
    {
        Dictionary<string, string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (SourceEntry entry in sourceEntries)
        {
            if (paths.TryGetValue(entry.RelativePath, out string? existing)
                && !string.Equals(existing, entry.RelativePath, StringComparison.Ordinal))
                throw new IOException(
                    $"case-colliding {policy.ProviderName} entries '{existing}' and '{entry.RelativePath}'");
            paths[entry.RelativePath] = entry.RelativePath;
        }
    }

    private static void ValidateLinkCycles(
        string sourceRoot,
        IReadOnlyList<SourceEntry> sourceEntries,
        CtWorkspaceMirrorPolicy policy)
    {
        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
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
                    current.RelativePath,
                    policy);
                if (IsAncestorPath(resolvedTarget, currentPath, comparison))
                    throw new IOException($"looping symbolic link '{current.RelativePath}'");
                if (!links.TryGetValue(resolvedTarget, out current!))
                    break;
            }
        }
    }

    private static bool IsAncestorPath(string ancestorPath, string childPath, StringComparison comparison)
    {
        string normalizedAncestor = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ancestorPath));
        string normalizedChild = Path.GetFullPath(childPath);
        return normalizedChild.StartsWith(normalizedAncestor + Path.DirectorySeparatorChar, comparison);
    }

    private static void ValidatePathBudget(
        string mirrorRoot,
        IReadOnlyList<SourceEntry> sourceEntries,
        CtWorkspaceMirrorPolicy policy)
    {
        foreach (SourceEntry entry in sourceEntries)
        {
            string destinationPath = Path.Combine(mirrorRoot, entry.RelativePath);
            if (destinationPath.Length > ContinuousTestProjectInventory.WindowsPathBudget)
                throw new IOException(
                    $"{policy.ProviderName} entry '{entry.RelativePath}' exceeds the Windows path budget");
        }
    }

    private static void ValidateSourceBudget(
        IReadOnlyList<SourceEntry> sourceEntries,
        CtWorkspaceMirrorPolicy policy,
        CancellationToken cancellationToken)
    {
        long totalBytes = 0;
        foreach (SourceEntry entry in sourceEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Kind != EntryKind.File)
                continue;
            long length = new FileInfo(entry.SourcePath).Length;
            totalBytes = checked(totalBytes + length);
            if (totalBytes > ContinuousTestCoordinatorOptions.DefaultBuildCacheBudgetBytes)
                throw new IOException(
                    $"{policy.ProviderName} entry '{entry.RelativePath}' exceeds the workspace build-cache budget");
        }
    }

    private static bool IsBuildOwned(string relativePath, CtWorkspaceMirrorPolicy policy) =>
        relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => policy.BuildOwnedEntryNames.Contains(segment));

    private static void ValidateBuildOwnedPaths(string mirrorRoot, CtWorkspaceMirrorPolicy policy)
    {
        Stack<string> pendingDirectories = new();
        pendingDirectories.Push(mirrorRoot);
        while (pendingDirectories.Count > 0)
        {
            string currentDirectory = pendingDirectories.Pop();
            foreach (string childPath in Directory.EnumerateFileSystemEntries(currentDirectory))
            {
                string relativePath = Path.GetRelativePath(mirrorRoot, childPath);
                if (IsReparsePoint(childPath))
                {
                    if (IsBuildOwned(relativePath, policy))
                        throw new IOException($"build-owned mirror path is a reparse point: '{relativePath}'");
                    continue;
                }
                if (Directory.Exists(childPath))
                    pendingDirectories.Push(childPath);
            }
        }
    }

    private static bool RemoveStaleEntry(
        string destinationPath,
        string mirrorRoot,
        CtWorkspaceMirrorPolicy policy,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureContainedPath(destinationPath, mirrorRoot, policy);
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
            string childRelativePath = Path.GetRelativePath(mirrorRoot, childPath);
            if (IsBuildOwned(childRelativePath, policy))
                continue;
            RemoveStaleEntry(childPath, mirrorRoot, policy, cancellationToken);
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

    private static void EnsureDestinationDirectory(string destinationPath, string mirrorRoot)
    {
        string directoryPath = Path.GetDirectoryName(destinationPath)!;
        EnsureDirectoryChain(directoryPath, mirrorRoot);
    }

    private static void EnsureDirectoryChain(string directoryPath, string mirrorRoot)
    {
        string fullMirrorRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mirrorRoot));
        string fullDirectoryPath = Path.GetFullPath(directoryPath);
        EnsureContainedPath(fullDirectoryPath, fullMirrorRoot, null);
        string? current = fullDirectoryPath;
        Stack<string> missing = new();
        while (current is not null
            && !string.Equals(current, fullMirrorRoot, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
        {
            if (PathExists(current))
            {
                if (!Directory.Exists(current) || IsReparsePoint(current))
                    throw new IOException($"mirror path is not a directory: '{current}'");
                break;
            }
            missing.Push(current);
            current = Path.GetDirectoryName(current);
        }

        foreach (string path in missing)
            Directory.CreateDirectory(path);
    }

    private static ManifestEntry CaptureEntry(
        SourceEntry sourceEntry,
        bool hashFile,
        ref long filesHashed,
        ref long bytesHashed)
    {
        FileSystemInfo sourceInfo = sourceEntry.Kind == EntryKind.Directory
            ? new DirectoryInfo(sourceEntry.SourcePath)
            : new FileInfo(sourceEntry.SourcePath);
        string? hash = null;
        if (sourceEntry.Kind == EntryKind.File && hashFile)
        {
            hash = ComputeHash(sourceEntry.SourcePath, out long length);
            filesHashed++;
            bytesHashed += length;
        }
        return new ManifestEntry(
            sourceEntry.RelativePath,
            sourceEntry.Kind,
            sourceEntry.Kind == EntryKind.File ? ((FileInfo)sourceInfo).Length : 0,
            sourceInfo.LastWriteTimeUtc.Ticks,
            sourceEntry.LinkTarget,
            hash,
            ReadUnixMode(sourceEntry.SourcePath, sourceEntry.Kind),
            sourceInfo.Attributes.HasFlag(FileAttributes.ReadOnly));
    }

    private static bool EntryMatches(
        ManifestEntry entry,
        string destinationPath,
        CtWorkspaceMirrorIntegrity integrity,
        ref int hashFallbacks,
        ref long filesHashed,
        ref long bytesHashed)
    {
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

        if (integrity == CtWorkspaceMirrorIntegrity.MetadataFastPath)
            return true;

        hashFallbacks++;
        string destinationHash = ComputeHash(destinationPath, out long length);
        filesHashed++;
        bytesHashed += length;
        return string.Equals(destinationHash, entry.Hash, StringComparison.Ordinal);
    }

    private static string ComputeHash(string path, out long length)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        length = stream.Length;
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
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void EnsureContainedPath(
        string path,
        string root,
        CtWorkspaceMirrorPolicy? policy)
    {
        string fullPath = Path.GetFullPath(path);
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison)
            && !string.Equals(fullPath, fullRoot, comparison))
            throw new IOException($"unsafe {policy?.ProviderName ?? "mirror"} path '{path}'");
    }

    private static void CreateGitBarrier(string mirrorRoot)
    {
        string gitRoot = Path.Combine(mirrorRoot, GitDirectoryName);
        EnsureDirectoryChain(gitRoot, mirrorRoot);
        string headPath = Path.Combine(gitRoot, "HEAD");
        if (!File.Exists(headPath))
            File.WriteAllText(headPath, "ref: refs/heads/miller-shadow\n");
        string configPath = Path.Combine(gitRoot, "config");
        if (!File.Exists(configPath))
            File.WriteAllText(
                configPath,
                "[core]\n\trepositoryformatversion = 0\n\tbare = false\n\tlogallrefupdates = false\n");
    }

    private static Dictionary<string, ManifestEntry> ReadManifest(
        string candidateRoot,
        string mirrorRoot,
        CtWorkspaceMirrorPolicy policy)
    {
        string manifestPath = Path.Combine(candidateRoot, ManifestFileName);
        if (!File.Exists(manifestPath))
            return new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);

        try
        {
            List<ManifestEntry>? manifestEntries = JsonSerializer.Deserialize<List<ManifestEntry>>(
                File.ReadAllText(manifestPath), ManifestJsonOptions);
            Dictionary<string, ManifestEntry> entries = new(StringComparer.Ordinal);
            if (manifestEntries is null)
                return entries;
            foreach (ManifestEntry entry in manifestEntries)
            {
                string normalizedPath = NormalizeManifestPath(entry.Path, mirrorRoot, policy);
                entries.Add(normalizedPath, entry with { Path = normalizedPath });
            }

            return entries;
        }
        catch (JsonException)
        {
            return RecoverManifestEntries(mirrorRoot, policy);
        }
        catch (NotSupportedException)
        {
            return RecoverManifestEntries(mirrorRoot, policy);
        }
    }

    private static string NormalizeManifestPath(
        string path,
        string mirrorRoot,
        CtWorkspaceMirrorPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            throw new IOException($"unsafe {policy.ProviderName} shadow manifest path '{path}'");
        string normalizedInput = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        string[] segments = normalizedInput.Split(Path.DirectorySeparatorChar);
        if (segments.Any(segment => segment is "" or "." or ".."))
            throw new IOException($"unsafe {policy.ProviderName} shadow manifest path '{path}'");

        string mirrorRootFullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mirrorRoot));
        string fullPath = Path.GetFullPath(Path.Combine(mirrorRootFullPath, normalizedInput));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(mirrorRootFullPath + Path.DirectorySeparatorChar, comparison))
            throw new IOException($"unsafe {policy.ProviderName} shadow manifest path '{path}'");
        return normalizedInput;
    }

    private static Dictionary<string, ManifestEntry> RecoverManifestEntries(
        string mirrorRoot,
        CtWorkspaceMirrorPolicy policy)
    {
        Dictionary<string, ManifestEntry> entries = new(StringComparer.Ordinal);
        if (!Directory.Exists(mirrorRoot))
            return entries;

        foreach (SourceEntry sourceEntry in EnumerateSourceEntries(mirrorRoot, policy, CancellationToken.None))
        {
            if (IsBuildOwned(sourceEntry.RelativePath, policy))
                continue;
            long filesHashed = 0;
            long bytesHashed = 0;
            ManifestEntry entry = CaptureEntry(sourceEntry, false, ref filesHashed, ref bytesHashed);
            entries[entry.Path] = entry;
        }

        return entries;
    }

    private static void WriteManifest(
        string candidateRoot,
        IReadOnlyList<ManifestEntry> entries)
    {
        string manifestPath = Path.Combine(candidateRoot, ManifestFileName);
        string tempPath = $"{manifestPath}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(entries));
            File.Move(tempPath, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static string ComputeMetadataDigest(
        IReadOnlyList<SourceEntry> sourceEntries,
        CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (SourceEntry sourceEntry in sourceEntries.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            long filesHashed = 0;
            long bytesHashed = 0;
            ManifestEntry entry = CaptureEntry(sourceEntry, false, ref filesHashed, ref bytesHashed);
            AppendDigestPart(hash, entry.Path);
            AppendDigestPart(hash, entry.Kind.ToString());
            AppendDigestPart(hash, entry.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendDigestPart(hash, entry.LastWriteTimeUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendDigestPart(hash, entry.LinkTarget ?? string.Empty);
            AppendDigestPart(hash, entry.UnixMode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
            AppendDigestPart(hash, entry.IsReadOnly ? "1" : "0");
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendDigestPart(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private static long DirectoryBytes(string path)
    {
        if (!Directory.Exists(path) || IsReparsePoint(path))
            return 0;

        long total = 0;
        Stack<string> pendingDirectories = new();
        pendingDirectories.Push(path);
        while (pendingDirectories.Count > 0)
        {
            string currentDirectory = pendingDirectories.Pop();
            foreach (string childPath in Directory.EnumerateFileSystemEntries(currentDirectory))
            {
                FileSystemInfo childInfo = new FileInfo(childPath);
                if (childInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                if (Directory.Exists(childPath))
                    pendingDirectories.Push(childPath);
                else
                    total += new FileInfo(childPath).Length;
            }
        }

        return total;
    }

    private static bool TryReadLinuxFileMode(string path, out uint mode)
    {
        mode = 0;
        try
        {
            if (Statx(
                    AtCurrentWorkingDirectory,
                    path,
                    AtSymlinkNoFollow,
                    StatxType,
                    out LinuxStatx result) != 0)
            {
                throw new IOException($"could not inspect source entry '{path}'",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            }

            mode = result.Mode;
            return true;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool TryReadMacOsFileMode(string path, out uint mode)
    {
        mode = 0;
        if (LStat(path, out MacOsStat result) != 0)
            throw new IOException($"could not inspect source entry '{path}'",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        mode = result.Mode;
        return true;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Statx(
        int directoryFileDescriptor,
        string path,
        int flags,
        uint mask,
        out LinuxStatx result);

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int LStat(string path, out MacOsStat result);

    private const int AtCurrentWorkingDirectory = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxType = 0x00000001;
    private const uint UnixFileTypeMask = 0xF000;
    private const uint UnixRegularFileType = 0x8000;

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(28)]
        public ushort Mode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MacOsTimespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [StructLayout(LayoutKind.Sequential, Size = 144)]
    private struct MacOsStat
    {
        public int Device;
        public ushort Mode;
        public ushort LinkCount;
        public ulong Inode;
        public uint UserId;
        public uint GroupId;
        public uint DeviceType;
        public MacOsTimespec AccessTime;
        public MacOsTimespec ModificationTime;
        public MacOsTimespec ChangeTime;
        public MacOsTimespec BirthTime;
        public long Size;
        public long Blocks;
        public int BlockSize;
        public uint Flags;
        public uint Generation;
        public int Spare;
        public long Reserved0;
        public long Reserved1;
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
