# Phase 3: MCP Server Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Create an MCP server exposing search and index tools for AI assistants.

**Architecture:** .NET MCP server using official C# SDK (ModelContextProtocol). Tools call through UniFFI bindings to Rust search engine. File watcher provides incremental index updates.

**Tech Stack:** ModelContextProtocol 0.6.0+, Microsoft.Extensions.Hosting, FileSystemWatcher

---

## Prerequisites

Phase 2 + 2.5 complete with:
- Rust engine with search methods (vector, text, hybrid, boosted)
- UniFFI bindings exposing search to .NET
- Tree-sitter extractors for 31 languages
- `index_file()` method in codesearch-core

---

### Task 1: Add MCP SDK Dependencies

**Files:**
- Modify: `src/Codesearch.Server/Codesearch.Server.csproj`

**Step 1: Add NuGet packages**

Update `src/Codesearch.Server/Codesearch.Server.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="ModelContextProtocol" Version="0.6.0-preview.1" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="9.0.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Codesearch.Interop\Codesearch.Interop.csproj" />
    <ProjectReference Include="..\Codesearch.Embeddings\Codesearch.Embeddings.csproj" />
  </ItemGroup>
</Project>
```

**Step 2: Restore packages**

Run: `dotnet restore src/Codesearch.Server`
Expected: Success

**Step 3: Commit**

```bash
git add src/Codesearch.Server/Codesearch.Server.csproj
git commit -m "feat(server): add MCP SDK dependencies"
```

---

### Task 2: Create MCP Server Entry Point

**Files:**
- Modify: `src/Codesearch.Server/Program.cs`

**Step 1: Create MCP server host**

Replace `src/Codesearch.Server/Program.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Codesearch.Server.Services;

var builder = Host.CreateApplicationBuilder(args);

// Log to stderr (MCP uses stdout for protocol)
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Register services
builder.Services.AddSingleton<SearchService>();
builder.Services.AddSingleton<IndexService>();

// Configure MCP server
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "codesearch",
            Version = "0.1.0"
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
```

**Step 2: Verify it compiles**

Run: `dotnet build src/Codesearch.Server`
Expected: Fails (missing Services - that's expected)

**Step 3: Commit**

```bash
git add src/Codesearch.Server/Program.cs
git commit -m "feat(server): add MCP server entry point"
```

---

### Task 3: Create SearchService

**Files:**
- Create: `src/Codesearch.Server/Services/SearchService.cs`

**Step 1: Create SearchService**

Create `src/Codesearch.Server/Services/SearchService.cs`:

```csharp
using uniffi.codesearch_ffi;

namespace Codesearch.Server.Services;

/// <summary>
/// Service wrapping the Rust search engine.
/// </summary>
public class SearchService : IDisposable
{
    private readonly CodeSearchEngine _engine;
    private readonly string _dbPath;
    private bool _disposed;

    public SearchService()
    {
        // Default to .codesearch in current directory
        var workspaceRoot = Environment.CurrentDirectory;
        var codesearchDir = Path.Combine(workspaceRoot, ".codesearch");
        Directory.CreateDirectory(codesearchDir);

        _dbPath = Path.Combine(codesearchDir, "index.lance");
        _engine = new CodeSearchEngine(_dbPath);
    }

    public SearchService(string dbPath)
    {
        _dbPath = dbPath;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        _engine = new CodeSearchEngine(dbPath);
    }

    public string DbPath => _dbPath;

    public bool HealthCheck() => _engine.HealthCheck();

    public ulong SymbolCount() => _engine.SymbolCount();

    public List<SearchResultOutput> SearchText(string query, uint limit = 20)
    {
        return _engine.SearchTextBoosted(query, limit);
    }

    public List<SearchResultOutput> SearchVector(List<float> queryVector, uint limit = 20)
    {
        return _engine.SearchVector(queryVector, limit);
    }

    public List<SearchResultOutput> SearchHybrid(string query, List<float> queryVector, uint limit = 20)
    {
        return _engine.SearchHybridBoosted(query, queryVector, limit);
    }

    public void CreateFtsIndex()
    {
        _engine.CreateFtsIndex();
    }

    public ulong AddSymbols(List<SymbolInput> symbols, List<List<float>> vectors)
    {
        return _engine.AddSymbols(symbols, vectors);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _engine.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
```

**Step 2: Verify it compiles**

Run: `dotnet build src/Codesearch.Server`
Expected: Fails (missing IndexService)

**Step 3: Commit**

```bash
git add src/Codesearch.Server/Services/SearchService.cs
git commit -m "feat(server): add SearchService wrapping Rust engine"
```

---

### Task 4: Create IndexService

**Files:**
- Create: `src/Codesearch.Server/Services/IndexService.cs`

**Step 1: Create IndexService**

Create `src/Codesearch.Server/Services/IndexService.cs`:

```csharp
using System.Security.Cryptography;
using uniffi.codesearch_ffi;

namespace Codesearch.Server.Services;

/// <summary>
/// Service for managing the code index.
/// </summary>
public class IndexService
{
    private readonly SearchService _searchService;
    private readonly Dictionary<string, string> _fileHashes = new();
    private readonly string _workspaceRoot;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".rs", ".py", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs",
        ".go", ".java", ".cs", ".c", ".cpp", ".cc", ".cxx", ".h", ".hpp",
        ".rb", ".php", ".swift", ".kt", ".kts", ".dart", ".lua", ".r",
        ".sh", ".bash", ".ps1", ".zig", ".html", ".css", ".vue", ".qml",
        ".gd", ".cshtml", ".razor", ".sql", ".md", ".json", ".toml", ".yaml", ".yml"
    };

    public IndexService(SearchService searchService)
    {
        _searchService = searchService;
        _workspaceRoot = Environment.CurrentDirectory;
    }

    /// <summary>
    /// Get index status.
    /// </summary>
    public IndexStatus GetStatus()
    {
        return new IndexStatus
        {
            SymbolCount = _searchService.SymbolCount(),
            DbPath = _searchService.DbPath,
            WorkspaceRoot = _workspaceRoot,
            IsHealthy = _searchService.HealthCheck()
        };
    }

    /// <summary>
    /// Refresh index - update only stale files.
    /// </summary>
    public async Task<IndexResult> RefreshAsync(string? path = null, CancellationToken ct = default)
    {
        var targetPath = path ?? _workspaceRoot;
        var files = GetIndexableFiles(targetPath);

        var indexed = 0;
        var skipped = 0;
        var errors = new List<string>();

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var hash = await ComputeFileHashAsync(file);
                var relativePath = Path.GetRelativePath(_workspaceRoot, file);

                if (_fileHashes.TryGetValue(relativePath, out var existingHash) && existingHash == hash)
                {
                    skipped++;
                    continue;
                }

                await IndexFileAsync(file, relativePath);
                _fileHashes[relativePath] = hash;
                indexed++;
            }
            catch (Exception ex)
            {
                errors.Add($"{file}: {ex.Message}");
            }
        }

        // Ensure FTS index exists
        if (indexed > 0)
        {
            _searchService.CreateFtsIndex();
        }

        return new IndexResult
        {
            FilesIndexed = indexed,
            FilesSkipped = skipped,
            Errors = errors
        };
    }

    /// <summary>
    /// Full reindex - rebuild from scratch.
    /// </summary>
    public async Task<IndexResult> FullIndexAsync(string? path = null, CancellationToken ct = default)
    {
        _fileHashes.Clear();
        return await RefreshAsync(path, ct);
    }

    private IEnumerable<string> GetIndexableFiles(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return Enumerable.Empty<string>();
        }

        return Directory.EnumerateFiles(rootPath, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true
        })
        .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
        .Where(f => !IsIgnoredPath(f));
    }

    private bool IsIgnoredPath(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p is ".git" or "node_modules" or "target" or "bin" or "obj"
                           or ".codesearch" or "__pycache__" or ".venv" or "venv");
    }

    private async Task<string> ComputeFileHashAsync(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }

    private async Task IndexFileAsync(string absolutePath, string relativePath)
    {
        var content = await File.ReadAllTextAsync(absolutePath);
        var extension = Path.GetExtension(relativePath).TrimStart('.');

        // Create symbol with placeholder vector (real embeddings would come from ONNX model)
        var symbol = new SymbolInput(
            id: $"file_{Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(relativePath)))[..16]}",
            name: Path.GetFileName(relativePath),
            kind: "file",
            language: extension,
            filePath: relativePath.Replace('\\', '/'),
            signature: null,
            docComment: null,
            startLine: 1,
            endLine: content.Split('\n').Length,
            content: content.Length > 4096 ? content[..4096] : content
        );

        // Placeholder vector (768 zeros)
        var vector = Enumerable.Repeat(0.0f, 768).ToList();

        _searchService.AddSymbols(new List<SymbolInput> { symbol }, new List<List<float>> { vector });
    }
}

public record IndexStatus
{
    public required ulong SymbolCount { get; init; }
    public required string DbPath { get; init; }
    public required string WorkspaceRoot { get; init; }
    public required bool IsHealthy { get; init; }
}

public record IndexResult
{
    public required int FilesIndexed { get; init; }
    public required int FilesSkipped { get; init; }
    public required List<string> Errors { get; init; }
}
```

**Step 2: Verify it compiles**

Run: `dotnet build src/Codesearch.Server`
Expected: Success

**Step 3: Commit**

```bash
git add src/Codesearch.Server/Services/IndexService.cs
git commit -m "feat(server): add IndexService for workspace indexing"
```

---

### Task 5: Create Search Tool

**Files:**
- Create: `src/Codesearch.Server/Tools/SearchTool.cs`

**Step 1: Create SearchTool**

Create `src/Codesearch.Server/Tools/SearchTool.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Codesearch.Server.Services;

namespace Codesearch.Server.Tools;

[McpServerToolType]
public static class SearchTool
{
    [McpServerTool]
    [Description("Search code and project knowledge. Returns symbols matching the query.")]
    public static string Search(
        SearchService searchService,
        [Description("Search query (natural language or code pattern)")] string query,
        [Description("Search method: auto, text, semantic, hybrid, pattern")] string method = "auto",
        [Description("Filter by symbol kind (function, class, etc.)")] string? kind = null,
        [Description("Filter by language")] string? language = null,
        [Description("Maximum results")] int limit = 20)
    {
        // Auto-detect method based on query
        var effectiveMethod = method == "auto" ? DetectMethod(query) : method;

        List<SearchResultOutput> results;

        switch (effectiveMethod)
        {
            case "text":
            case "pattern":
                results = searchService.SearchText(query, (uint)limit);
                break;

            case "semantic":
                // For semantic-only, we'd need embeddings
                // Fall back to hybrid for now
                results = searchService.SearchText(query, (uint)limit);
                break;

            case "hybrid":
            default:
                // Hybrid needs vector - use text for now until embeddings integrated
                results = searchService.SearchText(query, (uint)limit);
                break;
        }

        // Apply filters
        if (!string.IsNullOrEmpty(kind))
        {
            results = results.Where(r => r.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (!string.IsNullOrEmpty(language))
        {
            results = results.Where(r => r.Language.Equals(language, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Format results
        return FormatResults(results);
    }

    private static string DetectMethod(string query)
    {
        // Pattern indicators: special characters common in code
        if (query.Contains(':') || query.Contains('<') || query.Contains('>') ||
            query.Contains('[') || query.Contains(']') || query.Contains('(') ||
            query.Contains('{') || query.Contains("=>") || query.Contains("?."))
        {
            return "pattern";
        }

        // Default to hybrid for natural language
        return "hybrid";
    }

    private static string FormatResults(List<SearchResultOutput> results)
    {
        if (results.Count == 0)
        {
            return "No results found.";
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Found {results.Count} result(s):\n");

        foreach (var r in results)
        {
            sb.AppendLine($"**{r.Kind}**: `{r.Name}`");
            sb.AppendLine($"  File: {r.FilePath}:{r.StartLine}");
            if (!string.IsNullOrEmpty(r.Signature))
            {
                sb.AppendLine($"  Signature: `{r.Signature}`");
            }
            if (!string.IsNullOrEmpty(r.DocComment))
            {
                sb.AppendLine($"  Doc: {r.DocComment.Split('\n')[0]}");
            }
            sb.AppendLine($"  Score: {r.Score:F3}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
```

**Step 2: Verify it compiles**

Run: `dotnet build src/Codesearch.Server`
Expected: Success

**Step 3: Commit**

```bash
git add src/Codesearch.Server/Tools/SearchTool.cs
git commit -m "feat(server): add MCP search tool"
```

---

### Task 6: Create Index Tool

**Files:**
- Create: `src/Codesearch.Server/Tools/IndexTool.cs`

**Step 1: Create IndexTool**

Create `src/Codesearch.Server/Tools/IndexTool.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Codesearch.Server.Services;

namespace Codesearch.Server.Tools;

[McpServerToolType]
public static class IndexTool
{
    [McpServerTool]
    [Description("Manage workspace index. Operations: status (check health), refresh (update stale), full (rebuild).")]
    public static async Task<string> Index(
        IndexService indexService,
        [Description("Operation: status, refresh, or full")] string operation = "status",
        [Description("Specific path to index (optional)")] string? path = null)
    {
        switch (operation.ToLowerInvariant())
        {
            case "status":
                return FormatStatus(indexService.GetStatus());

            case "refresh":
                var refreshResult = await indexService.RefreshAsync(path);
                return FormatResult("Refresh", refreshResult);

            case "full":
                var fullResult = await indexService.FullIndexAsync(path);
                return FormatResult("Full index", fullResult);

            default:
                return $"Unknown operation: {operation}. Use: status, refresh, or full.";
        }
    }

    private static string FormatStatus(IndexStatus status)
    {
        return $"""
            ## Index Status

            - **Symbols**: {status.SymbolCount:N0}
            - **Database**: {status.DbPath}
            - **Workspace**: {status.WorkspaceRoot}
            - **Health**: {(status.IsHealthy ? "OK" : "ERROR")}
            """;
    }

    private static string FormatResult(string operation, IndexResult result)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## {operation} Complete");
        sb.AppendLine();
        sb.AppendLine($"- **Files indexed**: {result.FilesIndexed}");
        sb.AppendLine($"- **Files skipped**: {result.FilesSkipped}");

        if (result.Errors.Count > 0)
        {
            sb.AppendLine($"- **Errors**: {result.Errors.Count}");
            foreach (var error in result.Errors.Take(5))
            {
                sb.AppendLine($"  - {error}");
            }
            if (result.Errors.Count > 5)
            {
                sb.AppendLine($"  - ... and {result.Errors.Count - 5} more");
            }
        }

        return sb.ToString();
    }
}
```

**Step 2: Verify it compiles**

Run: `dotnet build src/Codesearch.Server`
Expected: Success

**Step 3: Commit**

```bash
git add src/Codesearch.Server/Tools/IndexTool.cs
git commit -m "feat(server): add MCP index tool"
```

---

### Task 7: Add File Watcher Service

**Files:**
- Create: `src/Codesearch.Server/Services/FileWatcherService.cs`
- Modify: `src/Codesearch.Server/Program.cs`

**Step 1: Create FileWatcherService**

Create `src/Codesearch.Server/Services/FileWatcherService.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Codesearch.Server.Services;

/// <summary>
/// Background service that watches for file changes and triggers reindexing.
/// </summary>
public class FileWatcherService : BackgroundService
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
```

**Step 2: Register in Program.cs**

Update `src/Codesearch.Server/Program.cs` to add the hosted service:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Codesearch.Server.Services;

var builder = Host.CreateApplicationBuilder(args);

// Log to stderr (MCP uses stdout for protocol)
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Register services
builder.Services.AddSingleton<SearchService>();
builder.Services.AddSingleton<IndexService>();
builder.Services.AddHostedService<FileWatcherService>();

// Configure MCP server
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "codesearch",
            Version = "0.1.0"
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
```

**Step 3: Verify it compiles**

Run: `dotnet build src/Codesearch.Server`
Expected: Success

**Step 4: Commit**

```bash
git add src/Codesearch.Server/Services/FileWatcherService.cs src/Codesearch.Server/Program.cs
git commit -m "feat(server): add file watcher for incremental indexing"
```

---

### Task 8: Create MCP Configuration

**Files:**
- Create: `mcp-config.json`

**Step 1: Create example MCP configuration**

Create `mcp-config.json` at project root:

```json
{
  "mcpServers": {
    "codesearch": {
      "command": "dotnet",
      "args": ["run", "--project", "src/Codesearch.Server"],
      "env": {}
    }
  }
}
```

**Step 2: Add to .gitignore note**

The config file should be customized per-user, but we include an example.

**Step 3: Commit**

```bash
git add mcp-config.json
git commit -m "docs: add example MCP server configuration"
```

---

### Task 9: Integration Test

**Files:**
- Create: `tests/Codesearch.Tests/McpServerTests.cs`

**Step 1: Create MCP integration test**

Create `tests/Codesearch.Tests/McpServerTests.cs`:

```csharp
using Codesearch.Server.Services;

namespace Codesearch.Tests;

public class McpServerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SearchService _searchService;
    private readonly IndexService _indexService;

    public McpServerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"codesearch_mcp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var dbPath = Path.Combine(_tempDir, "test.lance");
        _searchService = new SearchService(dbPath);
        _indexService = new IndexService(_searchService);
    }

    public void Dispose()
    {
        _searchService.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void SearchService_HealthCheck_ReturnsTrue()
    {
        Assert.True(_searchService.HealthCheck());
    }

    [Fact]
    public void IndexService_GetStatus_ReturnsValidStatus()
    {
        var status = _indexService.GetStatus();

        Assert.NotNull(status);
        Assert.True(status.IsHealthy);
        Assert.Equal(0UL, status.SymbolCount);
    }

    [Fact]
    public async Task IndexService_FullIndex_IndexesFiles()
    {
        // Create a test file
        var testFile = Path.Combine(_tempDir, "test.rs");
        await File.WriteAllTextAsync(testFile, "pub fn hello() {}");

        // Run full index
        var result = await _indexService.FullIndexAsync(_tempDir);

        Assert.Equal(1, result.FilesIndexed);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void SearchService_SearchText_ReturnsResults()
    {
        // Add a test symbol
        var symbol = new uniffi.codesearch_ffi.SymbolInput(
            id: "test1",
            name: "test_function",
            kind: "function",
            language: "rust",
            filePath: "test.rs",
            signature: "fn test_function()",
            docComment: null,
            startLine: 1,
            endLine: 5,
            content: null
        );
        var vector = Enumerable.Repeat(0.0f, 768).ToList();
        _searchService.AddSymbols(
            new List<uniffi.codesearch_ffi.SymbolInput> { symbol },
            new List<List<float>> { vector }
        );
        _searchService.CreateFtsIndex();

        // Search
        var results = _searchService.SearchText("test_function", 10);

        Assert.NotEmpty(results);
        Assert.Equal("test_function", results[0].Name);
    }
}
```

**Step 2: Add project reference**

Update `tests/Codesearch.Tests/Codesearch.Tests.csproj`:

```xml
<ItemGroup>
  <ProjectReference Include="..\..\src\Codesearch.Server\Codesearch.Server.csproj" />
</ItemGroup>
```

**Step 3: Run tests**

Run: `dotnet test`
Expected: All tests pass

**Step 4: Commit**

```bash
git add tests/Codesearch.Tests/McpServerTests.cs tests/Codesearch.Tests/Codesearch.Tests.csproj
git commit -m "test: add MCP server integration tests"
```

---

### Task 10: Manual Test

**Step 1: Build and run**

```bash
dotnet build src/Codesearch.Server
dotnet run --project src/Codesearch.Server
```

The server should start and wait for MCP protocol messages on stdin.

**Step 2: Test with Claude Code (optional)**

Add to your Claude Code MCP settings:

```json
{
  "mcpServers": {
    "codesearch": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/codesearch/src/Codesearch.Server"]
    }
  }
}
```

**Step 3: Commit final**

```bash
git add -A
git commit -m "feat(server): complete MCP server with search and index tools"
```

---

## Phase 3 Complete

At this point you have:
- MCP server using official C# SDK
- `search` tool - text search with filtering
- `index` tool - status, refresh, full operations
- File watcher for automatic reindexing
- Integration tests

**Next Phase (4):** Memory System - remember/recall operations with markdown files.
