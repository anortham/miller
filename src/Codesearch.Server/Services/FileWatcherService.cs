using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Codesearch.Server.Services;

/// <summary>
/// Background service that watches for file changes and triggers reindexing.
/// </summary>
internal class FileWatcherService : BackgroundService
{
    private readonly IndexService _indexService;
    private readonly ILogger<FileWatcherService> _logger;
    private FileSystemWatcher? _watcher;
    private readonly HashSet<string> _pendingChanges = new();
    private readonly object _lock = new();
    private Timer? _debounceTimer;
    private const int DebounceMs = 500;

    private static readonly HashSet<string> WatchedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".rs", ".py", ".ts", ".tsx", ".js", ".jsx", ".go", ".java", ".cs",
        ".c", ".cpp", ".h", ".hpp", ".rb", ".php", ".swift", ".kt", ".md"
    };

    public FileWatcherService(IndexService indexService, ILogger<FileWatcherService> logger)
    {
        _indexService = indexService;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workspaceRoot = Environment.CurrentDirectory;

        _watcher = new FileSystemWatcher(workspaceRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime
        };

        _watcher.Created += OnFileChanged;
        _watcher.Changed += OnFileChanged;
        _watcher.Deleted += OnFileChanged;
        _watcher.Renamed += OnFileRenamed;
        _watcher.Error += OnError;

        _watcher.EnableRaisingEvents = true;

        _logger.LogInformation("File watcher started for {Path}", workspaceRoot);

        return Task.CompletedTask;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!ShouldWatch(e.FullPath)) return;

        lock (_lock)
        {
            _pendingChanges.Add(e.FullPath);
            ResetDebounceTimer();
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (ShouldWatch(e.OldFullPath))
        {
            lock (_lock)
            {
                _pendingChanges.Add(e.OldFullPath);
            }
        }

        if (ShouldWatch(e.FullPath))
        {
            lock (_lock)
            {
                _pendingChanges.Add(e.FullPath);
                ResetDebounceTimer();
            }
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger.LogError(e.GetException(), "File watcher error");
    }

    private bool ShouldWatch(string path)
    {
        var ext = Path.GetExtension(path);
        if (!WatchedExtensions.Contains(ext)) return false;

        // Ignore common build/dependency directories
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !parts.Any(p => p is ".git" or "node_modules" or "target" or "bin" or "obj" or ".codesearch");
    }

    private void ResetDebounceTimer()
    {
        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(ProcessPendingChanges, null, DebounceMs, Timeout.Infinite);
    }

    private async void ProcessPendingChanges(object? state)
    {
        List<string> changes;
        lock (_lock)
        {
            changes = _pendingChanges.ToList();
            _pendingChanges.Clear();
        }

        if (changes.Count == 0) return;

        _logger.LogInformation("Processing {Count} file change(s)", changes.Count);

        try
        {
            await _indexService.RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing file changes");
        }
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
        base.Dispose();
    }
}
