using Miller.Indexing;

namespace Miller.Server.Hosting;

internal sealed record IndexerWatcherCallbacks(
    FileSystemEventHandler FileChanged,
    RenamedEventHandler FileRenamed,
    ErrorEventHandler Error,
    FileSystemEventHandler DirectoryChanged,
    RenamedEventHandler DirectoryRenamed,
    FileSystemEventHandler HeadChanged,
    FileSystemEventHandler IgnorePolicyChanged);

internal sealed class IndexerWatcherSet : IDisposable
{
    private readonly IndexerWatcherCallbacks _callbacks;
    private FileSystemWatcher? _fileWatcher;
    private FileSystemWatcher? _directoryWatcher;
    private FileSystemWatcher? _gitHeadWatcher;
    private FileSystemWatcher? _generatedIgnorePolicyWatcher;
    private readonly List<FileSystemWatcher> _ancestorIgnorePolicyWatchers = new();

    private IndexerWatcherSet(IndexerWatcherCallbacks callbacks) => _callbacks = callbacks;

    public bool HasFileWatcher => _fileWatcher is not null;
    public bool HasDirectoryWatcher => _directoryWatcher is not null;
    public bool HasGitHeadWatcher => _gitHeadWatcher is not null;
    public bool HasGeneratedIgnorePolicyWatcher => _generatedIgnorePolicyWatcher is not null;

    /// <summary>
    /// The git directory whose <c>HEAD</c> this set watches, or null when no HEAD watcher is attached. For a
    /// linked worktree this is its own <c>.git/worktrees/&lt;name&gt;</c> admin directory, never the shared
    /// common dir.
    /// </summary>
    public string? GitHeadWatchDirectory => _gitHeadWatcher?.Path;

    public int AncestorIgnorePolicyWatcherCount => _ancestorIgnorePolicyWatchers.Count;

    public static IndexerWatcherSet Attach(string canonicalRoot, IndexerWatcherCallbacks callbacks)
        => Attach(canonicalRoot, callbacks, MillerHome.ResolveMillerDirectory());

    internal static IndexerWatcherSet Attach(
        string canonicalRoot, IndexerWatcherCallbacks callbacks, string millerDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);
        ArgumentNullException.ThrowIfNull(callbacks);
        ArgumentException.ThrowIfNullOrWhiteSpace(millerDirectory);

        var watchers = new IndexerWatcherSet(callbacks);
        watchers.AttachCore(canonicalRoot, millerDirectory);
        return watchers;
    }

    private void AttachCore(string canonicalRoot, string millerDirectory)
    {
        _fileWatcher = new FileSystemWatcher(canonicalRoot)
        {
            IncludeSubdirectories = true,
            // File changes stay on the per-file path. Directory-name changes use the separate watcher below:
            // subtree moves/deletes cannot be represented safely as a single update/delete --file.
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            InternalBufferSize = 64 * 1024, // largest the OS allows; overflow still self-heals via Error->scan
        };
        _fileWatcher.Created += _callbacks.FileChanged;
        _fileWatcher.Changed += _callbacks.FileChanged;
        _fileWatcher.Deleted += _callbacks.FileChanged;
        _fileWatcher.Renamed += _callbacks.FileRenamed;
        _fileWatcher.Error += _callbacks.Error;
        _fileWatcher.EnableRaisingEvents = true;

        _directoryWatcher = new FileSystemWatcher(canonicalRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.DirectoryName,
            InternalBufferSize = 64 * 1024,
        };
        _directoryWatcher.Created += _callbacks.DirectoryChanged;
        _directoryWatcher.Deleted += _callbacks.DirectoryChanged;
        _directoryWatcher.Renamed += _callbacks.DirectoryRenamed;
        _directoryWatcher.Error += _callbacks.Error;
        _directoryWatcher.EnableRaisingEvents = true;

        // A dedicated watch on this checkout's own HEAD: a branch switch/checkout flips HEAD once; we force ONE
        // scan reconcile instead of processing the thousands of per-file events a checkout produces. The git dir
        // is resolved through GitWorktreeLayout rather than assumed to be a `<root>/.git` DIRECTORY, because a
        // linked worktree's `.git` is a FILE — Directory.Exists on it is false, so every worktree in the fleet
        // ran with no HEAD watcher and paid the overflow-rescan storm this watch exists to prevent. The watched
        // dir is the PER-WORKTREE admin dir, not CommonDir: a linked worktree has its own HEAD, and watching the
        // shared one would report the main checkout's branch switches instead of this worktree's. WatchPathFilter
        // skips `.git` in the main watcher and a linked worktree's admin dir usually sits outside the root
        // entirely, so this stays the only HEAD signal either way.
        if (GitWorktreeLayout.Resolve(canonicalRoot)?.GitDir is { } gitDir && Directory.Exists(gitDir))
        {
            _gitHeadWatcher = new FileSystemWatcher(gitDir, "HEAD")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            _gitHeadWatcher.Changed += _callbacks.HeadChanged;
            _gitHeadWatcher.Created += _callbacks.HeadChanged;
            _gitHeadWatcher.Renamed += OnHeadRenamed;
            _gitHeadWatcher.EnableRaisingEvents = true;
        }

        string workspaceId = WorkspaceId.FromCanonicalRoot(Path.GetFullPath(canonicalRoot));
        string generatedPolicy = JulieIgnoreSeeder.GeneratedGlobalIgnorePathForWorkspaceId(
            workspaceId, millerDirectory);
        bool generatedEligible = !File.Exists(
                Path.Combine(canonicalRoot, JulieIgnoreSeeder.WorkspaceIgnoreFileName))
            && JulieIgnoreSeeder.ResolveInheritedIgnoreFile(canonicalRoot) is null;
        if (generatedEligible && Path.GetDirectoryName(generatedPolicy) is { } generatedDirectory)
        {
            try
            {
                Directory.CreateDirectory(generatedDirectory);
                _generatedIgnorePolicyWatcher = new FileSystemWatcher(
                    generatedDirectory, Path.GetFileName(generatedPolicy))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                };
                _generatedIgnorePolicyWatcher.Changed += _callbacks.IgnorePolicyChanged;
                _generatedIgnorePolicyWatcher.Created += _callbacks.IgnorePolicyChanged;
                _generatedIgnorePolicyWatcher.Deleted += _callbacks.IgnorePolicyChanged;
                _generatedIgnorePolicyWatcher.Renamed += OnIgnorePolicyRenamed;
                _generatedIgnorePolicyWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                   or NotSupportedException)
            {
                _generatedIgnorePolicyWatcher?.Dispose();
                _generatedIgnorePolicyWatcher = null;
            }
        }

        foreach (string ignoreFile in WorkspaceIgnorePolicy.AncestorGitignoreFilesOutsideRoot(canonicalRoot))
        {
            string? directory = Path.GetDirectoryName(ignoreFile);
            if (directory is null || !Directory.Exists(directory))
                continue;

            var watcher = new FileSystemWatcher(directory, ".gitignore")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            watcher.Changed += _callbacks.IgnorePolicyChanged;
            watcher.Created += _callbacks.IgnorePolicyChanged;
            watcher.Deleted += _callbacks.IgnorePolicyChanged;
            watcher.Renamed += OnIgnorePolicyRenamed;
            watcher.EnableRaisingEvents = true;
            _ancestorIgnorePolicyWatchers.Add(watcher);
        }
    }

    public void Dispose()
    {
        if (_fileWatcher is not null)
        {
            _fileWatcher.EnableRaisingEvents = false;
            _fileWatcher.Created -= _callbacks.FileChanged;
            _fileWatcher.Changed -= _callbacks.FileChanged;
            _fileWatcher.Deleted -= _callbacks.FileChanged;
            _fileWatcher.Renamed -= _callbacks.FileRenamed;
            _fileWatcher.Error -= _callbacks.Error;
            _fileWatcher.Dispose();
            _fileWatcher = null;
        }
        if (_directoryWatcher is not null)
        {
            _directoryWatcher.EnableRaisingEvents = false;
            _directoryWatcher.Created -= _callbacks.DirectoryChanged;
            _directoryWatcher.Deleted -= _callbacks.DirectoryChanged;
            _directoryWatcher.Renamed -= _callbacks.DirectoryRenamed;
            _directoryWatcher.Error -= _callbacks.Error;
            _directoryWatcher.Dispose();
            _directoryWatcher = null;
        }
        if (_gitHeadWatcher is not null)
        {
            _gitHeadWatcher.EnableRaisingEvents = false;
            _gitHeadWatcher.Changed -= _callbacks.HeadChanged;
            _gitHeadWatcher.Created -= _callbacks.HeadChanged;
            _gitHeadWatcher.Renamed -= OnHeadRenamed;
            _gitHeadWatcher.Dispose();
            _gitHeadWatcher = null;
        }
        if (_generatedIgnorePolicyWatcher is not null)
        {
            _generatedIgnorePolicyWatcher.EnableRaisingEvents = false;
            _generatedIgnorePolicyWatcher.Changed -= _callbacks.IgnorePolicyChanged;
            _generatedIgnorePolicyWatcher.Created -= _callbacks.IgnorePolicyChanged;
            _generatedIgnorePolicyWatcher.Deleted -= _callbacks.IgnorePolicyChanged;
            _generatedIgnorePolicyWatcher.Renamed -= OnIgnorePolicyRenamed;
            _generatedIgnorePolicyWatcher.Dispose();
            _generatedIgnorePolicyWatcher = null;
        }
        foreach (var watcher in _ancestorIgnorePolicyWatchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= _callbacks.IgnorePolicyChanged;
            watcher.Created -= _callbacks.IgnorePolicyChanged;
            watcher.Deleted -= _callbacks.IgnorePolicyChanged;
            watcher.Renamed -= OnIgnorePolicyRenamed;
            watcher.Dispose();
        }
        _ancestorIgnorePolicyWatchers.Clear();
    }

    private void OnHeadRenamed(object sender, RenamedEventArgs e) =>
        _callbacks.HeadChanged(sender, e);

    private void OnIgnorePolicyRenamed(object sender, RenamedEventArgs e) =>
        _callbacks.IgnorePolicyChanged(sender, e);
}
