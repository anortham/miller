# Phase 5: Cross-Project System Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Enable cross-project memory aggregation and standup reports across multiple workspaces.

**Architecture:** Central registry at `~/.codesearch/registry.json` tracks known projects. Projects register on MCP server startup. Cross-project recall fetches from all registered projects in parallel, merges by timestamp, applies global limit. Standup is recall with `workspace: "all"`.

**Tech Stack:** System.Text.Json for registry, parallel async for aggregation

---

## Prerequisites

Phase 4 complete with:
- MemoryService with remember/recall operations
- Memory tool with remember, recall, status operations
- Memory files indexed in LanceDB

---

### Task 1: Create Registry Models

**Files:**
- Create: `src/Codesearch.Server/Registry/RegistryModels.cs`

**Step 1: Create registry models**

Create `src/Codesearch.Server/Registry/RegistryModels.cs`:

```csharp
namespace Codesearch.Server.Registry;

/// <summary>
/// Entry for a registered project in the central registry.
/// </summary>
internal record ProjectEntry
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required DateTimeOffset LastActive { get; init; }
    public DateTimeOffset? IndexedAt { get; init; }
}

/// <summary>
/// Central registry of known projects.
/// </summary>
internal record ProjectRegistry
{
    public string Version { get; init; } = "1.0";
    public DateTimeOffset LastUpdated { get; init; } = DateTimeOffset.UtcNow;
    public Dictionary<string, ProjectEntry> Projects { get; init; } = new();
}

/// <summary>
/// Summary of a workspace for cross-project results.
/// </summary>
internal record WorkspaceSummary
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required int CheckpointCount { get; init; }
    public DateTimeOffset? LastActivity { get; init; }
}

/// <summary>
/// Result of a cross-project recall operation.
/// </summary>
internal record CrossProjectRecallResult
{
    public required List<Memory.MemoryEntry> Entries { get; init; }
    public required List<WorkspaceSummary> Workspaces { get; init; }
    public required int TotalCount { get; init; }
}
```

**Step 2: Verify it compiles**

Run: `dotnet build src/Codesearch.Server`
Expected: Success

**Step 3: Commit**

```bash
git add src/Codesearch.Server/Registry/RegistryModels.cs
git commit -m "feat(registry): add cross-project registry models"
```

---

### Task 2: Create RegistryService

**Files:**
- Create: `src/Codesearch.Server/Registry/RegistryService.cs`

**Step 1: Create RegistryService**

Create `src/Codesearch.Server/Registry/RegistryService.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codesearch.Server.Registry;

/// <summary>
/// Service for managing the central project registry.
/// </summary>
internal class RegistryService
{
    private static readonly string RegistryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codesearch");

    private static readonly string RegistryPath = Path.Combine(RegistryDir, "registry.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _lock = new();

    /// <summary>
    /// Register or update a project in the central registry.
    /// </summary>
    public void RegisterProject(string path, string? name = null)
    {
        lock (_lock)
        {
            var registry = LoadRegistry();

            var projectName = name ?? Path.GetFileName(path);
            var normalizedName = NormalizeName(projectName);

            registry.Projects[normalizedName] = new ProjectEntry
            {
                Name = projectName,
                Path = path,
                LastActive = DateTimeOffset.UtcNow,
                IndexedAt = null
            };

            SaveRegistry(registry with { LastUpdated = DateTimeOffset.UtcNow });
        }
    }

    /// <summary>
    /// Update the last active timestamp for a project.
    /// </summary>
    public void TouchProject(string path)
    {
        lock (_lock)
        {
            var registry = LoadRegistry();
            var normalizedName = NormalizeName(Path.GetFileName(path));

            if (registry.Projects.TryGetValue(normalizedName, out var entry))
            {
                registry.Projects[normalizedName] = entry with
                {
                    LastActive = DateTimeOffset.UtcNow
                };
                SaveRegistry(registry with { LastUpdated = DateTimeOffset.UtcNow });
            }
        }
    }

    /// <summary>
    /// Get all registered projects.
    /// </summary>
    public List<ProjectEntry> GetProjects()
    {
        var registry = LoadRegistry();
        return registry.Projects.Values
            .OrderByDescending(p => p.LastActive)
            .ToList();
    }

    /// <summary>
    /// Get projects that exist and have memory directories.
    /// </summary>
    public List<ProjectEntry> GetActiveProjects()
    {
        return GetProjects()
            .Where(p => Directory.Exists(p.Path))
            .Where(p => Directory.Exists(Path.Combine(p.Path, ".memories")))
            .ToList();
    }

    /// <summary>
    /// Remove projects that no longer exist.
    /// </summary>
    public int PruneStaleProjects()
    {
        lock (_lock)
        {
            var registry = LoadRegistry();
            var staleKeys = registry.Projects
                .Where(kv => !Directory.Exists(kv.Value.Path))
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in staleKeys)
            {
                registry.Projects.Remove(key);
            }

            if (staleKeys.Count > 0)
            {
                SaveRegistry(registry with { LastUpdated = DateTimeOffset.UtcNow });
            }

            return staleKeys.Count;
        }
    }

    private ProjectRegistry LoadRegistry()
    {
        if (!File.Exists(RegistryPath))
        {
            return new ProjectRegistry();
        }

        try
        {
            var json = File.ReadAllText(RegistryPath);
            return JsonSerializer.Deserialize<ProjectRegistry>(json, JsonOptions)
                ?? new ProjectRegistry();
        }
        catch
        {
            return new ProjectRegistry();
        }
    }

    private void SaveRegistry(ProjectRegistry registry)
    {
        Directory.CreateDirectory(RegistryDir);

        var json = JsonSerializer.Serialize(registry, JsonOptions);
        var tempPath = $"{RegistryPath}.tmp.{Environment.ProcessId}";

        File.WriteAllText(tempPath, json);
        File.Move(tempPath, RegistryPath, overwrite: true);
    }

    private static string NormalizeName(string name)
    {
        var normalized = name.ToLowerInvariant()
            .Replace(' ', '-')
            .Replace('_', '-');

        // Remove non-alphanumeric except hyphens
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, "[^a-z0-9-]", "");
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, "-+", "-");
        normalized = normalized.Trim('-');

        return string.IsNullOrEmpty(normalized) ? "default" : normalized;
    }
}
```

**Step 2: Register in Program.cs**

Add to `src/Codesearch.Server/Program.cs`:

```csharp
using Codesearch.Server.Registry;
```

And add service registration:

```csharp
builder.Services.AddSingleton<RegistryService>();
```

**Step 3: Verify it compiles**

Run: `dotnet build src/Codesearch.Server`
Expected: Success

**Step 4: Commit**

```bash
git add src/Codesearch.Server/Registry/RegistryService.cs src/Codesearch.Server/Program.cs
git commit -m "feat(registry): add RegistryService for project tracking"
```

---

### Task 3: Register Project on Startup

**Files:**
- Modify: `src/Codesearch.Server/Program.cs`

**Step 1: Add startup registration**

After building the host but before running, register the current project. Update `src/Codesearch.Server/Program.cs`:

```csharp
var host = builder.Build();

// Register current project in central registry
var registry = host.Services.GetRequiredService<RegistryService>();
registry.RegisterProject(Environment.CurrentDirectory);

await host.RunAsync();
```

Replace the existing `await builder.Build().RunAsync();` with the above.

**Step 2: Verify it compiles**

Run: `dotnet build src/Codesearch.Server`
Expected: Success

**Step 3: Commit**

```bash
git add src/Codesearch.Server/Program.cs
git commit -m "feat(registry): register project on MCP server startup"
```

---

### Task 4: Add Cross-Project Recall to MemoryService

**Files:**
- Modify: `src/Codesearch.Server/Memory/MemoryService.cs`

**Step 1: Add cross-project recall method**

Add to `MemoryService.cs`:

```csharp
/// <summary>
/// Recall memories from a specific project path.
/// </summary>
public async Task<RecallResult> RecallFromPathAsync(
    string projectPath,
    MemoryType? type = null,
    List<string>? tags = null,
    int? days = null,
    int limit = 20)
{
    var memoriesDir = Path.Combine(projectPath, ".memories");
    var entries = new List<MemoryEntry>();

    if (!Directory.Exists(memoriesDir))
    {
        return new RecallResult { Entries = entries, TotalCount = 0 };
    }

    var sinceTimestamp = days.HasValue
        ? DateTimeOffset.UtcNow.AddDays(-days.Value).ToUnixTimeSeconds()
        : 0;
    var untilTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    var memoryFiles = Directory.EnumerateFiles(memoriesDir, "*.md", SearchOption.AllDirectories)
        .OrderByDescending(f => File.GetLastWriteTimeUtc(f));

    foreach (var file in memoryFiles)
    {
        try
        {
            var content = await File.ReadAllTextAsync(file);
            var (metadata, body) = FrontmatterParser.Parse(content);

            if (type.HasValue && metadata.Type != type.Value) continue;
            if (metadata.Timestamp < sinceTimestamp || metadata.Timestamp > untilTimestamp) continue;
            if (tags != null && tags.Count > 0)
            {
                var normalizedTags = NormalizeTags(tags);
                if (!normalizedTags.Any(t => metadata.Tags.Contains(t))) continue;
            }

            var relativePath = Path.GetRelativePath(projectPath, file).Replace('\\', '/');
            entries.Add(new MemoryEntry
            {
                Metadata = metadata,
                Content = body,
                FilePath = relativePath
            });

            if (entries.Count >= limit) break;
        }
        catch
        {
            // Skip malformed files
        }
    }

    return new RecallResult
    {
        Entries = entries,
        TotalCount = entries.Count
    };
}
```

**Step 2: Verify it compiles**

Run: `dotnet build src/Codesearch.Server`
Expected: Success

**Step 3: Commit**

```bash
git add src/Codesearch.Server/Memory/MemoryService.cs
git commit -m "feat(memory): add cross-project recall method"
```

---

### Task 5: Create CrossProjectService

**Files:**
- Create: `src/Codesearch.Server/Registry/CrossProjectService.cs`

**Step 1: Create CrossProjectService**

Create `src/Codesearch.Server/Registry/CrossProjectService.cs`:

```csharp
using Codesearch.Server.Memory;

namespace Codesearch.Server.Registry;

/// <summary>
/// Service for cross-project memory aggregation.
/// </summary>
internal class CrossProjectService
{
    private readonly RegistryService _registryService;
    private readonly MemoryService _memoryService;

    public CrossProjectService(RegistryService registryService, MemoryService memoryService)
    {
        _registryService = registryService;
        _memoryService = memoryService;
    }

    /// <summary>
    /// Recall memories from all registered projects.
    /// </summary>
    public async Task<CrossProjectRecallResult> RecallAllAsync(
        MemoryType? type = null,
        List<string>? tags = null,
        int? days = null,
        int limit = 20)
    {
        var projects = _registryService.GetActiveProjects();

        if (projects.Count == 0)
        {
            return new CrossProjectRecallResult
            {
                Entries = new List<MemoryEntry>(),
                Workspaces = new List<WorkspaceSummary>(),
                TotalCount = 0
            };
        }

        // Fetch from all projects in parallel
        var tasks = projects.Select(async project =>
        {
            var result = await _memoryService.RecallFromPathAsync(
                project.Path,
                type,
                tags,
                days,
                limit: 9999  // Get all, apply global limit later
            );
            return (Project: project, Result: result);
        });

        var results = await Task.WhenAll(tasks);

        // Build combined results
        var allEntries = new List<MemoryEntry>();
        var workspaceSummaries = new List<WorkspaceSummary>();

        foreach (var (project, result) in results)
        {
            if (result.Entries.Count > 0)
            {
                // Tag entries with their source project
                foreach (var entry in result.Entries)
                {
                    allEntries.Add(entry with
                    {
                        FilePath = $"[{project.Name}] {entry.FilePath}"
                    });
                }

                workspaceSummaries.Add(new WorkspaceSummary
                {
                    Name = project.Name,
                    Path = project.Path,
                    CheckpointCount = result.Entries.Count,
                    LastActivity = result.Entries.Count > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(result.Entries.Max(e => e.Metadata.Timestamp))
                        : null
                });
            }
        }

        // Sort by timestamp (newest first) and apply global limit
        allEntries = allEntries
            .OrderByDescending(e => e.Metadata.Timestamp)
            .Take(limit)
            .ToList();

        return new CrossProjectRecallResult
        {
            Entries = allEntries,
            Workspaces = workspaceSummaries.OrderByDescending(w => w.LastActivity).ToList(),
            TotalCount = allEntries.Count
        };
    }

    /// <summary>
    /// Generate standup report from all projects.
    /// </summary>
    public async Task<CrossProjectRecallResult> StandupAsync(int days = 1, int limit = 50)
    {
        return await RecallAllAsync(days: days, limit: limit);
    }
}
```

**Step 2: Register in Program.cs**

Add to `src/Codesearch.Server/Program.cs`:

```csharp
builder.Services.AddSingleton<CrossProjectService>();
```

**Step 3: Verify it compiles**

Run: `dotnet build src/Codesearch.Server`
Expected: Success

**Step 4: Commit**

```bash
git add src/Codesearch.Server/Registry/CrossProjectService.cs src/Codesearch.Server/Program.cs
git commit -m "feat(registry): add CrossProjectService for aggregation"
```

---

### Task 6: Update Memory Tool for Cross-Project

**Files:**
- Modify: `src/Codesearch.Server/Tools/MemoryTool.cs`

**Step 1: Add workspace parameter and standup operation**

Update `MemoryTool.cs` to add workspace parameter and standup operation:

```csharp
using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Codesearch.Server.Memory;
using Codesearch.Server.Registry;

namespace Codesearch.Server.Tools;

[McpServerToolType]
internal static class MemoryTool
{
    [McpServerTool]
    [Description("Remember and recall project knowledge. Operations: remember, recall, standup, status.")]
    internal static async Task<string> Memory(
        MemoryService memoryService,
        CrossProjectService crossProjectService,
        [Description("Operation: remember, recall, standup, or status")] string operation,
        [Description("Content to remember (for remember operation)")] string? content = null,
        [Description("Memory type: checkpoint, plan, decision, learning")] string type = "checkpoint",
        [Description("Tags for categorization (comma-separated)")] string? tags = null,
        [Description("Title (for plans/decisions)")] string? title = null,
        [Description("Search query (for recall)")] string? query = null,
        [Description("Time range in days (for recall/standup)")] int days = 7,
        [Description("Workspace scope: current or all")] string workspace = "current",
        [Description("Maximum results")] int limit = 20)
    {
        var memoryType = type.ToLowerInvariant() switch
        {
            "plan" => MemoryType.Plan,
            "decision" => MemoryType.Decision,
            "learning" => MemoryType.Learning,
            _ => MemoryType.Checkpoint
        };

        var tagList = string.IsNullOrWhiteSpace(tags)
            ? new List<string>()
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        switch (operation.ToLowerInvariant())
        {
            case "remember":
                if (string.IsNullOrWhiteSpace(content))
                {
                    return "Error: content is required for remember operation.";
                }
                return await RememberAsync(memoryService, content, memoryType, tagList, title);

            case "recall":
                if (workspace.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    return await RecallAllAsync(crossProjectService, memoryType, tagList, days, limit);
                }
                return await RecallAsync(memoryService, memoryType, tagList, days, limit, query);

            case "standup":
                return await StandupAsync(crossProjectService, days, limit);

            case "status":
                return await GetStatusAsync(memoryService);

            default:
                return $"Unknown operation: {operation}. Use: remember, recall, standup, or status.";
        }
    }

    private static async Task<string> RememberAsync(
        MemoryService memoryService,
        string content,
        MemoryType type,
        List<string> tags,
        string? title)
    {
        var entry = await memoryService.RememberAsync(content, type, tags, title);

        var sb = new StringBuilder();
        sb.AppendLine($"## Memory Created");
        sb.AppendLine();
        sb.AppendLine($"- **ID**: `{entry.Metadata.Id}`");
        sb.AppendLine($"- **Type**: {entry.Metadata.Type}");
        sb.AppendLine($"- **File**: `{entry.FilePath}`");
        if (entry.Metadata.Tags.Count > 0)
        {
            sb.AppendLine($"- **Tags**: {string.Join(", ", entry.Metadata.Tags)}");
        }
        if (entry.Metadata.Git != null)
        {
            sb.AppendLine($"- **Git**: {entry.Metadata.Git.Branch} @ {entry.Metadata.Git.Commit}");
        }

        return sb.ToString();
    }

    private static async Task<string> RecallAsync(
        MemoryService memoryService,
        MemoryType type,
        List<string> tags,
        int days,
        int limit,
        string? query)
    {
        MemoryType? typeFilter = type != MemoryType.Checkpoint ? type : null;
        var result = await memoryService.RecallAsync(
            type: typeFilter,
            tags: tags.Count > 0 ? tags : null,
            days: days,
            limit: limit);

        if (result.Entries.Count == 0)
        {
            return "No memories found matching the criteria.";
        }

        return FormatMemories(result.Entries, "current workspace");
    }

    private static async Task<string> RecallAllAsync(
        CrossProjectService crossProjectService,
        MemoryType type,
        List<string> tags,
        int days,
        int limit)
    {
        MemoryType? typeFilter = type != MemoryType.Checkpoint ? type : null;
        var result = await crossProjectService.RecallAllAsync(
            type: typeFilter,
            tags: tags.Count > 0 ? tags : null,
            days: days,
            limit: limit);

        if (result.Entries.Count == 0)
        {
            return "No memories found across any registered projects.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"## Found {result.Entries.Count} Memory/Memories Across {result.Workspaces.Count} Project(s)");
        sb.AppendLine();

        // Show workspace summaries
        sb.AppendLine("### Workspaces");
        foreach (var ws in result.Workspaces)
        {
            var lastActive = ws.LastActivity?.ToString("yyyy-MM-dd HH:mm") ?? "unknown";
            sb.AppendLine($"- **{ws.Name}**: {ws.CheckpointCount} memories (last: {lastActive})");
        }
        sb.AppendLine();

        // Show memories
        sb.AppendLine("### Memories");
        foreach (var entry in result.Entries)
        {
            var timestamp = DateTimeOffset.FromUnixTimeSeconds(entry.Metadata.Timestamp);
            sb.AppendLine($"#### {entry.Metadata.Type}: {timestamp:yyyy-MM-dd HH:mm}");
            sb.AppendLine();
            sb.AppendLine($"**File**: `{entry.FilePath}`");
            if (entry.Metadata.Tags.Count > 0)
            {
                sb.AppendLine($"**Tags**: {string.Join(", ", entry.Metadata.Tags)}");
            }
            sb.AppendLine();

            var preview = entry.Content.Length > 500
                ? entry.Content[..500] + "..."
                : entry.Content;
            sb.AppendLine(preview);
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static async Task<string> StandupAsync(
        CrossProjectService crossProjectService,
        int days,
        int limit)
    {
        var result = await crossProjectService.StandupAsync(days, limit);

        if (result.Entries.Count == 0)
        {
            return $"No activity found in the last {days} day(s) across any registered projects.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"## Standup Report ({days} day{(days == 1 ? "" : "s")})");
        sb.AppendLine();
        sb.AppendLine($"**{result.Entries.Count}** memories across **{result.Workspaces.Count}** project(s)");
        sb.AppendLine();

        // Group by workspace
        foreach (var ws in result.Workspaces)
        {
            sb.AppendLine($"### {ws.Name}");
            sb.AppendLine();

            var wsEntries = result.Entries
                .Where(e => e.FilePath.StartsWith($"[{ws.Name}]"))
                .ToList();

            foreach (var entry in wsEntries)
            {
                var timestamp = DateTimeOffset.FromUnixTimeSeconds(entry.Metadata.Timestamp);
                var cleanPath = entry.FilePath.Replace($"[{ws.Name}] ", "");
                sb.AppendLine($"- **{timestamp:HH:mm}** [{entry.Metadata.Type}] {cleanPath}");

                // First line of content as summary
                var firstLine = entry.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrEmpty(firstLine))
                {
                    var summary = firstLine.Length > 100 ? firstLine[..100] + "..." : firstLine;
                    sb.AppendLine($"  {summary}");
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static async Task<string> GetStatusAsync(MemoryService memoryService)
    {
        var result = await memoryService.RecallAsync(days: 365, limit: 1000);

        var byType = result.Entries
            .GroupBy(e => e.Metadata.Type)
            .ToDictionary(g => g.Key, g => g.Count());

        var recent = result.Entries
            .Where(e => e.Metadata.Timestamp > DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeSeconds())
            .Count();

        var sb = new StringBuilder();
        sb.AppendLine("## Memory Status");
        sb.AppendLine();
        sb.AppendLine($"- **Total memories**: {result.TotalCount}");
        sb.AppendLine($"- **Last 7 days**: {recent}");
        sb.AppendLine();
        sb.AppendLine("### By Type");
        foreach (var (memType, count) in byType.OrderByDescending(kv => kv.Value))
        {
            sb.AppendLine($"- {memType}: {count}");
        }

        return sb.ToString();
    }

    private static string FormatMemories(List<MemoryEntry> entries, string scope)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Found {entries.Count} Memory/Memories ({scope})");
        sb.AppendLine();

        foreach (var entry in entries)
        {
            var timestamp = DateTimeOffset.FromUnixTimeSeconds(entry.Metadata.Timestamp);
            sb.AppendLine($"### {entry.Metadata.Type}: {timestamp:yyyy-MM-dd HH:mm}");
            sb.AppendLine();
            sb.AppendLine($"**File**: `{entry.FilePath}`");
            if (entry.Metadata.Tags.Count > 0)
            {
                sb.AppendLine($"**Tags**: {string.Join(", ", entry.Metadata.Tags)}");
            }
            sb.AppendLine();

            var preview = entry.Content.Length > 500
                ? entry.Content[..500] + "..."
                : entry.Content;
            sb.AppendLine(preview);
            sb.AppendLine();
            sb.AppendLine("---");
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
git add src/Codesearch.Server/Tools/MemoryTool.cs
git commit -m "feat(memory): add cross-project recall and standup operations"
```

---

### Task 7: Add Cross-Project Tests

**Files:**
- Create: `tests/Codesearch.Tests/RegistryTests.cs`

**Step 1: Create registry tests**

Create `tests/Codesearch.Tests/RegistryTests.cs`:

```csharp
using Xunit;
using Codesearch.Server.Registry;
using Codesearch.Server.Memory;

namespace Codesearch.Tests;

public class RegistryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _project1Dir;
    private readonly string _project2Dir;

    public RegistryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"codesearch_registry_{Guid.NewGuid():N}");
        _project1Dir = Path.Combine(_tempDir, "project1");
        _project2Dir = Path.Combine(_tempDir, "project2");

        Directory.CreateDirectory(_project1Dir);
        Directory.CreateDirectory(_project2Dir);
        Directory.CreateDirectory(Path.Combine(_project1Dir, ".memories"));
        Directory.CreateDirectory(Path.Combine(_project2Dir, ".memories"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void RegistryService_RegistersProject()
    {
        var service = new RegistryService();
        service.RegisterProject(_project1Dir, "Test Project 1");

        var projects = service.GetProjects();

        Assert.Contains(projects, p => p.Name == "Test Project 1");
    }

    [Fact]
    public void RegistryService_GetActiveProjects_OnlyReturnsExisting()
    {
        var service = new RegistryService();
        service.RegisterProject(_project1Dir, "Exists");
        service.RegisterProject("/nonexistent/path", "Gone");

        var activeProjects = service.GetActiveProjects();

        Assert.Single(activeProjects);
        Assert.Equal("Exists", activeProjects[0].Name);
    }

    [Fact]
    public async Task CrossProjectService_AggregatesFromMultipleProjects()
    {
        // Create memories in both projects
        var memory1 = new MemoryService(_project1Dir);
        var memory2 = new MemoryService(_project2Dir);

        await memory1.RememberAsync("Memory from project 1", MemoryType.Checkpoint);
        await memory2.RememberAsync("Memory from project 2", MemoryType.Checkpoint);

        // Register both projects
        var registry = new RegistryService();
        registry.RegisterProject(_project1Dir, "Project1");
        registry.RegisterProject(_project2Dir, "Project2");

        // Create cross-project service and recall
        var crossProject = new CrossProjectService(registry, new MemoryService(_tempDir));
        var result = await crossProject.RecallAllAsync(days: 1, limit: 10);

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(2, result.Workspaces.Count);
    }

    [Fact]
    public async Task CrossProjectService_Standup_GroupsByProject()
    {
        var memory1 = new MemoryService(_project1Dir);
        var memory2 = new MemoryService(_project2Dir);

        await memory1.RememberAsync("Work on feature A", MemoryType.Checkpoint);
        await memory2.RememberAsync("Fixed bug in B", MemoryType.Checkpoint);

        var registry = new RegistryService();
        registry.RegisterProject(_project1Dir, "ProjectA");
        registry.RegisterProject(_project2Dir, "ProjectB");

        var crossProject = new CrossProjectService(registry, new MemoryService(_tempDir));
        var result = await crossProject.StandupAsync(days: 1);

        Assert.Equal(2, result.TotalCount);
        Assert.Contains(result.Workspaces, w => w.Name == "ProjectA");
        Assert.Contains(result.Workspaces, w => w.Name == "ProjectB");
    }
}
```

**Step 2: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~RegistryTests"`
Expected: All tests pass

**Step 3: Commit**

```bash
git add tests/Codesearch.Tests/RegistryTests.cs
git commit -m "test: add cross-project registry tests"
```

---

### Task 8: Final Verification

**Step 1: Run all tests**

Run: `dotnet test`
Expected: All tests pass

**Step 2: Build and verify server starts**

Run: `dotnet build src/Codesearch.Server`
Expected: Success

**Step 3: Verify registry is created on startup**

Run server briefly, then check `~/.codesearch/registry.json` exists.

**Step 4: Final commit**

```bash
git add -A
git commit -m "feat(registry): complete cross-project system"
```

---

## Phase 5 Complete

At this point you have:
- Central registry at `~/.codesearch/registry.json`
- Projects auto-register on MCP server startup
- Cross-project recall with `workspace: "all"`
- Standup operation aggregating all projects
- Parallel fetching for performance
- Stale project pruning

**Next Phase (6):** Claude Code Integration - hooks, skills, documentation.
