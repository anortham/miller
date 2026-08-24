namespace Miller.Testing;

internal readonly record struct CtCacheReapDebt(string Path, long Bytes);

internal sealed record CtCacheMaintenanceResult(
    int DeletedCount,
    long DeletedBytes,
    int FailedCount,
    long RemainingBytes,
    long ProtectedBytes,
    bool ProtectedOverBudget,
    bool MachineLockContended,
    int LockedRootCount,
    int SkippedRecentRootCount,
    int SkippedAmbiguousRootCount,
    IReadOnlyList<CtCacheReapDebt> Debts,
    IReadOnlyList<string> RemovedPaths)
{
    internal static CtCacheMaintenanceResult Empty =>
        new(0, 0, 0, 0, 0, false, false, 0, 0, 0, [], []);
}

internal sealed class CtBuildCacheJanitor
{
    internal const long DefaultWorkspaceBudgetBytes = 2L * 1024 * 1024 * 1024;
    internal const long DefaultMachineBudgetBytes = 8L * 1024 * 1024 * 1024;
    internal static readonly TimeSpan DefaultInactivity = TimeSpan.FromDays(7);

    private readonly string _machineBuildRoot;
    private readonly string _machineLockRoot;
    private readonly long _workspaceBudgetBytes;
    private readonly long _machineBudgetBytes;
    private readonly TimeSpan _inactivity;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<string, long?> _measureDirectoryBytes;
    private readonly Func<string, CtReapOutcome> _reap;
    private readonly Action<string>? _report;

    internal CtBuildCacheJanitor(
        string machineBuildRoot,
        long workspaceBudgetBytes = DefaultWorkspaceBudgetBytes,
        long machineBudgetBytes = DefaultMachineBudgetBytes,
        TimeSpan? inactivity = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<string, long?>? measureDirectoryBytes = null,
        Action<string>? report = null,
        Func<string, CtReapOutcome>? reap = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineBuildRoot);
        if (workspaceBudgetBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(workspaceBudgetBytes));
        if (machineBudgetBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(machineBudgetBytes));

        _machineBuildRoot = Path.GetFullPath(machineBuildRoot);
        _machineLockRoot = Path.GetDirectoryName(_machineBuildRoot) ?? _machineBuildRoot;
        _workspaceBudgetBytes = workspaceBudgetBytes;
        _machineBudgetBytes = machineBudgetBytes;
        _inactivity = inactivity ?? DefaultInactivity;
        if (_inactivity <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(inactivity));
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
        _measureDirectoryBytes = measureDirectoryBytes ?? ContinuousTestCoordinator.MeasureTreeBytes;
        _reap = reap ?? CtGenerationPaths.TryReapDetailed;
        _report = report;
    }

    internal CtCacheMaintenanceResult EnforceWorkspace(
        string buildOutputRoot,
        bool operationLockHeld = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildOutputRoot);
        string root = Path.GetFullPath(buildOutputRoot);
        if (!operationLockHeld)
        {
            CtOperationLockState state = CtBuildRootOperationLease.Probe(root);
            if (state is not CtOperationLockState.Available)
            {
                return state is CtOperationLockState.Held
                    ? EmptyWith(lockedRootCount: 1)
                    : EmptyWith(skippedAmbiguousRootCount: 1);
            }
        }

        RootSnapshot? snapshot = ReadRoot(root);
        if (snapshot is null)
            return EmptyWith(skippedAmbiguousRootCount: 1);

        MutableResult result = new(snapshot.TotalBytes, snapshot.ProtectedBytes);
        DateTimeOffset cutoff = _utcNow() - _inactivity;
        var candidates = snapshot.Candidates.ToList();
        foreach (CacheCandidate candidate in candidates.Where(candidate => candidate.LastUsedUtc < cutoff).ToArray())
            PruneCandidate(candidate, result, "workspace");

        foreach (CacheCandidate candidate in candidates
                     .Where(candidate => !result.RemovedPaths.Contains(candidate.Path))
                     .OrderBy(candidate => candidate.LastUsedUtc)
                     .ThenBy(candidate => candidate.Path, StringComparer.Ordinal))
        {
            if (result.RemainingBytes <= _workspaceBudgetBytes)
                break;
            PruneCandidate(candidate, result, "workspace");
        }

        return result.ToResult(_workspaceBudgetBytes, _report, "workspace", root);
    }

    internal bool IsMachineOwnedBuildRoot(string buildOutputRoot) =>
        IsCanonicalBuildRoot(Path.GetFullPath(buildOutputRoot));

    internal CtCacheMaintenanceResult EnforceMachine(string? currentBuildRoot = null)
    {
        using CtMachineBuildJanitorLease? machineLease = CtMachineBuildJanitorLease.TryAcquire(_machineLockRoot);
        if (machineLease is null)
            return EmptyWith(machineLockContended: true);

        DateTimeOffset cutoff = _utcNow() - _inactivity;
        var roots = new List<RootSnapshot>();
        var eligibleRoots = new List<RootSnapshot>();
        int lockedRoots = 0;
        int recentRoots = 0;
        int ambiguousRoots = 0;
        foreach (string root in EnumerateCanonicalRoots())
        {
            RootReadResult read = ReadMachineRoot(root, cutoff, currentBuildRoot);
            switch (read.Kind)
            {
                case RootReadKind.Locked:
                    lockedRoots++;
                    if (read.Snapshot is not null)
                        roots.Add(read.Snapshot);
                    break;
                case RootReadKind.Recent:
                    recentRoots++;
                    if (read.Snapshot is not null)
                        roots.Add(read.Snapshot);
                    break;
                case RootReadKind.Ambiguous:
                    ambiguousRoots++;
                    if (read.Snapshot is not null)
                        roots.Add(read.Snapshot);
                    break;
                case RootReadKind.Complete:
                    if (read.Snapshot is not null)
                    {
                        roots.Add(read.Snapshot);
                        eligibleRoots.Add(read.Snapshot);
                    }
                    break;
            }
        }

        var result = new MutableResult(
            roots.Sum(root => root.TotalBytes),
            roots.Sum(root => root.ProtectedBytes));
        result.LockedRootCount = lockedRoots;
        result.SkippedRecentRootCount = recentRoots;
        result.SkippedAmbiguousRootCount = ambiguousRoots;
        var candidates = eligibleRoots
            .SelectMany(root => root.Candidates)
            .ToList();
        foreach (CacheCandidate candidate in candidates.Where(candidate => candidate.LastUsedUtc < cutoff)
                     .OrderBy(candidate => candidate.LastUsedUtc)
                     .ThenBy(candidate => candidate.Path, StringComparer.Ordinal)
                     .ToArray())
        {
            if (result.RemainingBytes <= _machineBudgetBytes)
                break;
            PruneCandidate(candidate, result, "machine");
        }

        foreach (CacheCandidate candidate in candidates
                     .Where(candidate => !result.RemovedPaths.Contains(candidate.Path))
                     .OrderBy(candidate => candidate.LastUsedUtc)
                     .ThenBy(candidate => candidate.Path, StringComparer.Ordinal))
        {
            if (result.RemainingBytes <= _machineBudgetBytes)
                break;
            PruneCandidate(candidate, result, "machine");
        }

        return result.ToResult(
            _machineBudgetBytes,
            _report,
            "machine",
            _machineBuildRoot);
    }

    private RootReadResult ReadMachineRoot(
        string root,
        DateTimeOffset cutoff,
        string? currentBuildRoot)
    {
        if (currentBuildRoot is not null
            && string.Equals(Path.GetFullPath(currentBuildRoot), root, PathComparison))
            return new RootReadResult(RootReadKind.Locked, ReadRoot(root));

        CtOperationLockState lockState = CtBuildRootOperationLease.Probe(root);
        if (lockState is CtOperationLockState.Held)
            return new RootReadResult(RootReadKind.Locked, ReadRoot(root));

        RootSnapshot? snapshot = ReadRoot(root);
        if (snapshot is null || lockState is not CtOperationLockState.Available)
            return new RootReadResult(RootReadKind.Ambiguous, snapshot);

        if (snapshot.HasRecentActivity(cutoff))
            return new RootReadResult(RootReadKind.Recent, snapshot);
        return new RootReadResult(RootReadKind.Complete, snapshot);
    }

    private RootSnapshot? ReadRoot(string root)
    {
        IReadOnlyList<string>? directories = TryDirectoryNames(root);
        if (directories is null)
            return null;

        foreach (string name in directories)
        {
            if (CtGenerationPaths.IsGenerationId(name)
                || CtGenerationPaths.IsCacheRootDirectoryName(name)
                || name.Contains(CtGenerationPaths.ReapSuffixPrefix, StringComparison.Ordinal))
            {
                if (IsReparsePoint(Path.Combine(root, name)))
                    return null;
                continue;
            }

            return null;
        }

        long? total = _measureDirectoryBytes(root);
        if (total is null)
            return null;

        List<CacheCandidate>? candidates = ReadCandidates(root);
        if (candidates is null)
            return null;
        long candidatesBytes = candidates.Sum(candidate => candidate.Bytes);
        return new RootSnapshot(root, total.Value, total.Value - candidatesBytes, candidates);
    }

    private List<CacheCandidate>? ReadCandidates(string root)
    {
        string cacheRoot = Path.Combine(root, CtGenerationPaths.CacheRootDirectoryName);
        IReadOnlyList<string>? names = TryDirectoryNames(cacheRoot);
        if (names is null)
            return Directory.Exists(cacheRoot) ? null : [];

        var candidates = new List<CacheCandidate>();
        foreach (string name in names)
        {
            string path = Path.Combine(cacheRoot, name);
            if (IsReparsePoint(path))
                return null;
            long? bytes = _measureDirectoryBytes(path);
            if (bytes is null)
                return null;
            DateTimeOffset? lastUsed = TryLastWriteUtc(path);
            if (lastUsed is null)
                return null;
            candidates.Add(new CacheCandidate(path, bytes.Value, lastUsed.Value));
        }

        return candidates;
    }

    private void PruneCandidate(CacheCandidate candidate, MutableResult result, string scope)
    {
        if (!result.AttemptedPaths.Add(candidate.Path))
            return;

        CtReapOutcome outcome;
        try
        {
            outcome = _reap(candidate.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            outcome = CtReapOutcome.RenameFailed;
        }

        switch (outcome)
        {
            case CtReapOutcome.Missing:
            case CtReapOutcome.Deleted:
                result.DeletedCount++;
                result.DeletedBytes += candidate.Bytes;
                result.RemainingBytes -= candidate.Bytes;
                result.RemovedPaths.Add(candidate.Path);
                break;
            case CtReapOutcome.RenameFailed:
            case CtReapOutcome.DeleteFailed:
                result.FailedCount++;
                result.Debts.Add(new CtCacheReapDebt(candidate.Path, candidate.Bytes));
                Report($"cache_reap_failed scope={scope} path={candidate.Path} bytes={candidate.Bytes}");
                break;
        }
    }

    private IEnumerable<string> EnumerateCanonicalRoots()
    {
        IReadOnlyList<string>? workspaceDirectories = TryDirectoryNames(_machineBuildRoot);
        if (workspaceDirectories is null)
            yield break;

        foreach (string workspaceName in workspaceDirectories.OrderBy(value => value, StringComparer.Ordinal))
        {
            string workspacePath = Path.Combine(_machineBuildRoot, workspaceName);
            if (!IsHashSegment(workspaceName) || IsReparsePoint(workspacePath))
                continue;
            IReadOnlyList<string>? projectDirectories = TryDirectoryNames(workspacePath);
            if (projectDirectories is null)
                continue;
            foreach (string projectName in projectDirectories.OrderBy(value => value, StringComparer.Ordinal))
            {
                if (!IsHashSegment(projectName))
                    continue;
                string projectPath = Path.Combine(workspacePath, projectName);
                if (!IsReparsePoint(projectPath))
                    yield return projectPath;
            }
        }
    }

    private bool IsCanonicalBuildRoot(string buildOutputRoot)
    {
        string relative = Path.GetRelativePath(_machineBuildRoot, buildOutputRoot);
        string[] segments = relative.Split(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 2
            && IsHashSegment(segments[0])
            && IsHashSegment(segments[1]);
    }

    private static bool IsHashSegment(string value)
    {
        if (value.Length != 12)
            return false;
        foreach (char c in value)
        {
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }

        return true;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static DateTimeOffset? TryLastWriteUtc(string path)
    {
        try
        {
            return new DateTimeOffset(Directory.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string>? TryDirectoryNames(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory)
                .Select(Path.GetFileName)
                .OfType<string>()
                .ToArray();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Report(string message)
    {
        try { _report?.Invoke(message); } catch (Exception) { }
    }

    private static CtCacheMaintenanceResult EmptyWith(
        bool machineLockContended = false,
        int lockedRootCount = 0,
        int skippedRecentRootCount = 0,
        int skippedAmbiguousRootCount = 0) =>
        new(
            0,
            0,
            0,
            0,
            0,
            false,
            machineLockContended,
            lockedRootCount,
            skippedRecentRootCount,
            skippedAmbiguousRootCount,
            [],
            []);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record RootSnapshot(
        string Root,
        long TotalBytes,
        long ProtectedBytes,
        IReadOnlyList<CacheCandidate> Candidates)
    {
        public bool HasRecentActivity(DateTimeOffset cutoff) =>
            Candidates.Any(candidate => candidate.LastUsedUtc >= cutoff)
            || GenerationDirectories().Any(directory =>
                TryLastWriteUtc(directory) is { } lastUsed && lastUsed >= cutoff);

        private IEnumerable<string> GenerationDirectories() =>
            TryDirectoryNames(Root)?
                .Where(CtGenerationPaths.IsGenerationId)
                .Select(name => Path.Combine(Root, name))
            ?? [];
    }

    private sealed record CacheCandidate(string Path, long Bytes, DateTimeOffset LastUsedUtc);

    private sealed record RootReadResult(RootReadKind Kind, RootSnapshot? Snapshot);

    private enum RootReadKind
    {
        Complete,
        Locked,
        Recent,
        Ambiguous,
    }

    private sealed class MutableResult
    {
        public MutableResult(long remainingBytes, long protectedBytes)
        {
            RemainingBytes = remainingBytes;
            ProtectedBytes = protectedBytes;
        }

        public int DeletedCount { get; set; }

        public long DeletedBytes { get; set; }

        public int FailedCount { get; set; }

        public long RemainingBytes { get; set; }

        public long ProtectedBytes { get; }

        public int LockedRootCount { get; set; }

        public int SkippedRecentRootCount { get; set; }

        public int SkippedAmbiguousRootCount { get; set; }

        public List<CtCacheReapDebt> Debts { get; } = [];

        public HashSet<string> RemovedPaths { get; } = new(PathComparer);

        public HashSet<string> AttemptedPaths { get; } = new(PathComparer);

        public CtCacheMaintenanceResult ToResult(
            long budget,
            Action<string>? report,
            string scope,
            string root)
        {
            bool overBudget = RemainingBytes > budget;
            if (DeletedCount > 0)
            {
                try
                {
                    report?.Invoke(
                        $"cache_pruned scope={scope} root={root} count={DeletedCount} bytes={DeletedBytes}");
                }
                catch (Exception)
                {
                }
            }
            if (overBudget)
            {
                try
                {
                    report?.Invoke(
                        $"cache_protected_over_budget scope={scope} root={root} "
                        + $"bytes={ProtectedBytes} remaining={RemainingBytes} budget={budget}");
                }
                catch (Exception)
                {
                }
            }

            return new CtCacheMaintenanceResult(
                DeletedCount,
                DeletedBytes,
                FailedCount,
                RemainingBytes,
                ProtectedBytes,
                overBudget,
                false,
                LockedRootCount,
                SkippedRecentRootCount,
                SkippedAmbiguousRootCount,
                Debts,
                RemovedPaths.ToArray());
        }

        private static StringComparer PathComparer =>
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    }
}
