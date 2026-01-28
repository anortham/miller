# Phase 4: Memory System Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement project memory system with remember/recall operations stored as markdown files with frontmatter.

**Architecture:** Memories stored in `.memories/` as markdown with YAML frontmatter. MemoryService handles CRUD operations. Memories indexed alongside code in LanceDB for unified search. Git context captured automatically.

**Tech Stack:** YamlDotNet for frontmatter, System.Security.Cryptography for ID generation, LibGit2Sharp for git context

---

## Prerequisites

Phase 3 complete with:
- MCP server with search/index tools
- SearchService and IndexService
- File watcher for incremental updates

---

### Task 1: Add YamlDotNet Dependency

**Files:**
- Modify: `src/Codesearch.Server/Codesearch.Server.csproj`

**Step 1: Add NuGet package**

Add to `src/Codesearch.Server/Codesearch.Server.csproj`:

```xml
<PackageReference Include="YamlDotNet" Version="16.3.0" />
```

**Step 2: Restore packages**

Run: `dotnet restore src/Codesearch.Server`
Expected: Success

**Step 3: Commit**

```bash
git add src/Codesearch.Server/Codesearch.Server.csproj
git commit -m "feat(memory): add YamlDotNet dependency"
```

---

### Task 2: Create Memory Models

**Files:**
- Create: `src/Codesearch.Server/Memory/MemoryModels.cs`

**Step 1: Create memory type models**

Create `src/Codesearch.Server/Memory/MemoryModels.cs`:

```csharp
namespace Codesearch.Server.Memory;

/// <summary>
/// Types of memories that can be stored.
/// </summary>
internal enum MemoryType
{
    Checkpoint,
    Plan,
    Decision,
    Learning
}

/// <summary>
/// Git context captured when creating a memory.
/// </summary>
internal record GitContext
{
    public string? Branch { get; init; }
    public string? Commit { get; init; }
    public bool Dirty { get; init; }
    public List<string> FilesChanged { get; init; } = new();
}

/// <summary>
/// Memory metadata stored in frontmatter.
/// </summary>
internal record MemoryMetadata
{
    public required string Id { get; init; }
    public required MemoryType Type { get; init; }
    public required long Timestamp { get; init; }
    public List<string> Tags { get; init; } = new();
    public GitContext? Git { get; init; }

    // Plan-specific
    public string? Title { get; init; }
    public string? Status { get; init; }  // pending, in_progress, completed

    // Decision-specific
    public List<string>? Options { get; init; }
    public string? Chosen { get; init; }
}

/// <summary>
/// Complete memory entry with metadata and content.
/// </summary>
internal record MemoryEntry
{
    public required MemoryMetadata Metadata { get; init; }
    public required string Content { get; init; }
    public required string FilePath { get; init; }
}

/// <summary>
/// Result of a recall operation.
/// </summary>
internal record RecallResult
{
    public required List<MemoryEntry> Entries { get; init; }
    public required int TotalCount { get; init; }
}
```

**Step 2: Verify it compiles**

Run: `dotnet build src/Codesearch.Server`
Expected: Success

**Step 3: Commit**

```bash
git add src/Codesearch.Server/Memory/MemoryModels.cs
git commit -m "feat(memory): add memory type models"
```

---

### Task 3: Create Frontmatter Parser

**Files:**
- Create: `src/Codesearch.Server/Memory/FrontmatterParser.cs`

**Step 1: Create frontmatter parser**

Create `src/Codesearch.Server/Memory/FrontmatterParser.cs`:

```csharp
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Codesearch.Server.Memory;

/// <summary>
/// Parses and writes YAML frontmatter in markdown files.
/// </summary>
internal static class FrontmatterParser
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    /// <summary>
    /// Parse a memory file into metadata and content.
    /// </summary>
    public static (MemoryMetadata Metadata, string Content) Parse(string fileContent)
    {
        if (!fileContent.StartsWith("---"))
        {
            throw new FormatException("Invalid memory file: missing frontmatter");
        }

        var endMarker = fileContent.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endMarker == -1)
        {
            throw new FormatException("Invalid memory file: unclosed frontmatter");
        }

        var frontmatterText = fileContent[4..endMarker]; // Skip opening ---\n
        var content = fileContent[(endMarker + 5)..].Trim(); // Skip closing ---\n

        var rawMetadata = Deserializer.Deserialize<Dictionary<string, object?>>(frontmatterText);
        var metadata = ParseMetadata(rawMetadata);

        return (metadata, content);
    }

    /// <summary>
    /// Write a memory file with frontmatter and content.
    /// </summary>
    public static string Write(MemoryMetadata metadata, string content)
    {
        var frontmatterDict = BuildFrontmatterDict(metadata);
        var frontmatter = Serializer.Serialize(frontmatterDict);
        return $"---\n{frontmatter}---\n\n{content}\n";
    }

    private static MemoryMetadata ParseMetadata(Dictionary<string, object?> raw)
    {
        var id = raw.GetValueOrDefault("id")?.ToString() ?? throw new FormatException("Missing id");
        var typeStr = raw.GetValueOrDefault("type")?.ToString() ?? "checkpoint";
        var timestamp = Convert.ToInt64(raw.GetValueOrDefault("timestamp") ?? 0);

        var tags = new List<string>();
        if (raw.GetValueOrDefault("tags") is IEnumerable<object> tagList)
        {
            tags.AddRange(tagList.Select(t => t.ToString() ?? ""));
        }

        GitContext? git = null;
        if (raw.GetValueOrDefault("git") is Dictionary<object, object> gitDict)
        {
            var filesChanged = new List<string>();
            if (gitDict.GetValueOrDefault("files_changed") is IEnumerable<object> files)
            {
                filesChanged.AddRange(files.Select(f => f.ToString() ?? ""));
            }

            git = new GitContext
            {
                Branch = gitDict.GetValueOrDefault("branch")?.ToString(),
                Commit = gitDict.GetValueOrDefault("commit")?.ToString(),
                Dirty = Convert.ToBoolean(gitDict.GetValueOrDefault("dirty") ?? false),
                FilesChanged = filesChanged
            };
        }

        return new MemoryMetadata
        {
            Id = id,
            Type = Enum.TryParse<MemoryType>(typeStr, ignoreCase: true, out var t) ? t : MemoryType.Checkpoint,
            Timestamp = timestamp,
            Tags = tags,
            Git = git,
            Title = raw.GetValueOrDefault("title")?.ToString(),
            Status = raw.GetValueOrDefault("status")?.ToString(),
            Options = (raw.GetValueOrDefault("options") as IEnumerable<object>)?.Select(o => o.ToString() ?? "").ToList(),
            Chosen = raw.GetValueOrDefault("chosen")?.ToString()
        };
    }

    private static Dictionary<string, object?> BuildFrontmatterDict(MemoryMetadata metadata)
    {
        var dict = new Dictionary<string, object?>
        {
            ["id"] = metadata.Id,
            ["type"] = metadata.Type.ToString().ToLowerInvariant(),
            ["timestamp"] = metadata.Timestamp
        };

        if (metadata.Tags.Count > 0)
        {
            dict["tags"] = metadata.Tags;
        }

        if (metadata.Git != null)
        {
            dict["git"] = new Dictionary<string, object?>
            {
                ["branch"] = metadata.Git.Branch,
                ["commit"] = metadata.Git.Commit,
                ["dirty"] = metadata.Git.Dirty,
                ["files_changed"] = metadata.Git.FilesChanged
            };
        }

        if (metadata.Title != null) dict["title"] = metadata.Title;
        if (metadata.Status != null) dict["status"] = metadata.Status;
        if (metadata.Options != null) dict["options"] = metadata.Options;
        if (metadata.Chosen != null) dict["chosen"] = metadata.Chosen;

        return dict;
    }
}
```

**Step 2: Verify it compiles**

Run: `dotnet build src/Codesearch.Server`
Expected: Success

**Step 3: Commit**

```bash
git add src/Codesearch.Server/Memory/FrontmatterParser.cs
git commit -m "feat(memory): add frontmatter parser"
```

---

### Task 4: Create MemoryService

**Files:**
- Create: `src/Codesearch.Server/Memory/MemoryService.cs`

**Step 1: Create MemoryService**

Create `src/Codesearch.Server/Memory/MemoryService.cs`:

```csharp
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Codesearch.Server.Memory;

/// <summary>
/// Service for managing project memories.
/// </summary>
internal partial class MemoryService
{
    private readonly string _workspaceRoot;
    private readonly string _memoriesDir;

    public MemoryService()
    {
        _workspaceRoot = Environment.CurrentDirectory;
        _memoriesDir = Path.Combine(_workspaceRoot, ".memories");
    }

    public MemoryService(string workspaceRoot)
    {
        _workspaceRoot = workspaceRoot;
        _memoriesDir = Path.Combine(workspaceRoot, ".memories");
    }

    /// <summary>
    /// Create a new memory checkpoint.
    /// </summary>
    public async Task<MemoryEntry> RememberAsync(
        string content,
        MemoryType type = MemoryType.Checkpoint,
        List<string>? tags = null,
        string? title = null)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var id = GenerateMemoryId(type);
        var normalizedTags = NormalizeTags(tags ?? new List<string>());
        var gitContext = await GetGitContextAsync();

        var metadata = new MemoryMetadata
        {
            Id = id,
            Type = type,
            Timestamp = timestamp,
            Tags = normalizedTags,
            Git = gitContext,
            Title = title,
            Status = type == MemoryType.Plan ? "pending" : null
        };

        var filePath = GetMemoryFilePath(type, timestamp, title);
        var fileContent = FrontmatterParser.Write(metadata, content);

        var fullPath = Path.Combine(_workspaceRoot, filePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(fullPath, fileContent);

        return new MemoryEntry
        {
            Metadata = metadata,
            Content = content,
            FilePath = filePath
        };
    }

    /// <summary>
    /// Recall memories matching the criteria.
    /// </summary>
    public async Task<RecallResult> RecallAsync(
        MemoryType? type = null,
        List<string>? tags = null,
        int? days = null,
        DateTimeOffset? since = null,
        DateTimeOffset? until = null,
        int limit = 20)
    {
        var entries = new List<MemoryEntry>();

        if (!Directory.Exists(_memoriesDir))
        {
            return new RecallResult { Entries = entries, TotalCount = 0 };
        }

        // Calculate time filter
        var sinceTimestamp = since?.ToUnixTimeSeconds() ??
            (days.HasValue ? DateTimeOffset.UtcNow.AddDays(-days.Value).ToUnixTimeSeconds() : 0);
        var untilTimestamp = until?.ToUnixTimeSeconds() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Scan memory files
        var memoryFiles = Directory.EnumerateFiles(_memoriesDir, "*.md", SearchOption.AllDirectories)
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f));

        foreach (var file in memoryFiles)
        {
            try
            {
                var content = await File.ReadAllTextAsync(file);
                var (metadata, body) = FrontmatterParser.Parse(content);

                // Apply filters
                if (type.HasValue && metadata.Type != type.Value) continue;
                if (metadata.Timestamp < sinceTimestamp || metadata.Timestamp > untilTimestamp) continue;
                if (tags != null && tags.Count > 0)
                {
                    var normalizedTags = NormalizeTags(tags);
                    if (!normalizedTags.Any(t => metadata.Tags.Contains(t))) continue;
                }

                var relativePath = Path.GetRelativePath(_workspaceRoot, file).Replace('\\', '/');
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

    /// <summary>
    /// Read a specific memory file.
    /// </summary>
    public async Task<MemoryEntry?> ReadMemoryAsync(string filePath)
    {
        var fullPath = Path.Combine(_workspaceRoot, filePath);
        if (!File.Exists(fullPath)) return null;

        var content = await File.ReadAllTextAsync(fullPath);
        var (metadata, body) = FrontmatterParser.Parse(content);

        return new MemoryEntry
        {
            Metadata = metadata,
            Content = body,
            FilePath = filePath
        };
    }

    private string GenerateMemoryId(MemoryType type)
    {
        var rand1 = RandomNumberGenerator.GetHexString(8);
        var rand2 = RandomNumberGenerator.GetHexString(6);
        return $"{type.ToString().ToLowerInvariant()}_{rand1}_{rand2}";
    }

    private string GetMemoryFilePath(MemoryType type, long timestamp, string? title)
    {
        var dt = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime;

        if (type == MemoryType.Plan && !string.IsNullOrEmpty(title))
        {
            var slug = Slugify(title);
            return $".memories/plans/{slug}.md";
        }

        var dateDir = dt.ToString("yyyy-MM-dd");
        var timeStr = dt.ToString("HHmmss");
        var suffix = RandomNumberGenerator.GetHexString(4);
        return $".memories/{dateDir}/{timeStr}_{suffix}.md";
    }

    private static List<string> NormalizeTags(List<string> tags)
    {
        return tags
            .Select(t => TagNormalizeRegex().Replace(t.ToLowerInvariant().Replace('_', '-').Replace(' ', '-'), ""))
            .Select(t => MultiHyphenRegex().Replace(t, "-").Trim('-'))
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct()
            .ToList();
    }

    private static string Slugify(string title)
    {
        var slug = title.ToLowerInvariant()
            .Replace(' ', '-')
            .Replace('_', '-');
        slug = SlugifyRegex().Replace(slug, "");
        slug = MultiHyphenRegex().Replace(slug, "-").Trim('-');
        return slug;
    }

    private async Task<GitContext?> GetGitContextAsync()
    {
        try
        {
            var gitDir = Path.Combine(_workspaceRoot, ".git");
            if (!Directory.Exists(gitDir)) return null;

            // Simple git context extraction without LibGit2Sharp dependency
            // Read HEAD for branch
            var headPath = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(headPath)) return null;

            var headContent = await File.ReadAllTextAsync(headPath);
            string? branch = null;
            string? commit = null;

            if (headContent.StartsWith("ref: refs/heads/"))
            {
                branch = headContent["ref: refs/heads/".Length..].Trim();
                var refPath = Path.Combine(gitDir, "refs", "heads", branch);
                if (File.Exists(refPath))
                {
                    commit = (await File.ReadAllTextAsync(refPath)).Trim()[..7];
                }
            }
            else
            {
                commit = headContent.Trim()[..7];
            }

            return new GitContext
            {
                Branch = branch,
                Commit = commit,
                Dirty = false,  // Would need git status to determine
                FilesChanged = new List<string>()
            };
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex("[^a-z0-9-]")]
    private static partial Regex TagNormalizeRegex();

    [GeneratedRegex("-+")]
    private static partial Regex MultiHyphenRegex();

    [GeneratedRegex("[^a-z0-9-]")]
    private static partial Regex SlugifyRegex();
}
```

**Step 2: Register in Program.cs**

Add to `src/Codesearch.Server/Program.cs` after other service registrations:

```csharp
using Codesearch.Server.Memory;
```

And add the service registration:

```csharp
builder.Services.AddSingleton<MemoryService>();
```

**Step 3: Verify it compiles**

Run: `dotnet build src/Codesearch.Server`
Expected: Success

**Step 4: Commit**

```bash
git add src/Codesearch.Server/Memory/MemoryService.cs src/Codesearch.Server/Program.cs
git commit -m "feat(memory): add MemoryService for remember/recall operations"
```

---

### Task 5: Create Memory Tool

**Files:**
- Create: `src/Codesearch.Server/Tools/MemoryTool.cs`

**Step 1: Create MemoryTool**

Create `src/Codesearch.Server/Tools/MemoryTool.cs`:

```csharp
using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Codesearch.Server.Memory;

namespace Codesearch.Server.Tools;

[McpServerToolType]
internal static class MemoryTool
{
    [McpServerTool]
    [Description("Remember and recall project knowledge. Operations: remember (create checkpoint/plan/decision/learning), recall (search memories), status (show memory stats).")]
    internal static async Task<string> Memory(
        MemoryService memoryService,
        [Description("Operation: remember, recall, or status")] string operation,
        [Description("Content to remember (for remember operation)")] string? content = null,
        [Description("Memory type: checkpoint, plan, decision, learning")] string type = "checkpoint",
        [Description("Tags for categorization (comma-separated)")] string? tags = null,
        [Description("Title (for plans/decisions)")] string? title = null,
        [Description("Search query (for recall)")] string? query = null,
        [Description("Time range in days (for recall)")] int days = 7,
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
                return await RecallAsync(memoryService, memoryType, tagList, days, limit, query);

            case "status":
                return await GetStatusAsync(memoryService);

            default:
                return $"Unknown operation: {operation}. Use: remember, recall, or status.";
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
        // For now, use filesystem recall (semantic search requires embeddings integration)
        var result = await memoryService.RecallAsync(
            type: null,  // Don't filter by type unless specifically requested
            tags: tags.Count > 0 ? tags : null,
            days: days,
            limit: limit);

        if (result.Entries.Count == 0)
        {
            return "No memories found matching the criteria.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"## Found {result.Entries.Count} Memory/Memories");
        sb.AppendLine();

        foreach (var entry in result.Entries)
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

            // Show first 500 chars of content
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
}
```

**Step 2: Verify it compiles**

Run: `dotnet build src/Codesearch.Server`
Expected: Success

**Step 3: Commit**

```bash
git add src/Codesearch.Server/Tools/MemoryTool.cs
git commit -m "feat(memory): add MCP memory tool"
```

---

### Task 6: Add Memory Tests

**Files:**
- Create: `tests/Codesearch.Tests/MemoryTests.cs`

**Step 1: Create memory tests**

Create `tests/Codesearch.Tests/MemoryTests.cs`:

```csharp
using Xunit;
using Codesearch.Server.Memory;

namespace Codesearch.Tests;

public class MemoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly MemoryService _memoryService;

    public MemoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"codesearch_memory_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _memoryService = new MemoryService(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Remember_CreatesCheckpoint()
    {
        var entry = await _memoryService.RememberAsync(
            "Test checkpoint content",
            MemoryType.Checkpoint,
            new List<string> { "test", "unit-test" });

        Assert.NotNull(entry);
        Assert.StartsWith("checkpoint_", entry.Metadata.Id);
        Assert.Equal(MemoryType.Checkpoint, entry.Metadata.Type);
        Assert.Contains("test", entry.Metadata.Tags);
        Assert.True(File.Exists(Path.Combine(_tempDir, entry.FilePath)));
    }

    [Fact]
    public async Task Remember_CreatesPlan()
    {
        var entry = await _memoryService.RememberAsync(
            "Plan content here",
            MemoryType.Plan,
            new List<string> { "feature" },
            title: "My Feature Plan");

        Assert.NotNull(entry);
        Assert.StartsWith("plan_", entry.Metadata.Id);
        Assert.Equal(MemoryType.Plan, entry.Metadata.Type);
        Assert.Contains("plans/my-feature-plan.md", entry.FilePath);
    }

    [Fact]
    public async Task Recall_ReturnsRecentMemories()
    {
        // Create some memories
        await _memoryService.RememberAsync("First checkpoint", MemoryType.Checkpoint);
        await _memoryService.RememberAsync("Second checkpoint", MemoryType.Checkpoint);
        await _memoryService.RememberAsync("A learning", MemoryType.Learning);

        var result = await _memoryService.RecallAsync(days: 1);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(3, result.Entries.Count);
    }

    [Fact]
    public async Task Recall_FiltersByType()
    {
        await _memoryService.RememberAsync("Checkpoint", MemoryType.Checkpoint);
        await _memoryService.RememberAsync("Learning", MemoryType.Learning);

        var result = await _memoryService.RecallAsync(type: MemoryType.Learning, days: 1);

        Assert.Single(result.Entries);
        Assert.Equal(MemoryType.Learning, result.Entries[0].Metadata.Type);
    }

    [Fact]
    public async Task Recall_FiltersByTags()
    {
        await _memoryService.RememberAsync("Tagged", MemoryType.Checkpoint, new List<string> { "important" });
        await _memoryService.RememberAsync("Not tagged", MemoryType.Checkpoint);

        var result = await _memoryService.RecallAsync(tags: new List<string> { "important" }, days: 1);

        Assert.Single(result.Entries);
        Assert.Contains("important", result.Entries[0].Metadata.Tags);
    }

    [Fact]
    public void FrontmatterParser_ParsesValidFile()
    {
        var content = """
            ---
            id: checkpoint_12345678_abcdef
            type: checkpoint
            timestamp: 1700000000
            tags:
              - test
              - example
            ---

            ## Test Content

            This is the body.
            """;

        var (metadata, body) = FrontmatterParser.Parse(content);

        Assert.Equal("checkpoint_12345678_abcdef", metadata.Id);
        Assert.Equal(MemoryType.Checkpoint, metadata.Type);
        Assert.Equal(1700000000, metadata.Timestamp);
        Assert.Equal(2, metadata.Tags.Count);
        Assert.Contains("## Test Content", body);
    }

    [Fact]
    public void FrontmatterParser_WritesValidFile()
    {
        var metadata = new MemoryMetadata
        {
            Id = "test_id_123456",
            Type = MemoryType.Learning,
            Timestamp = 1700000000,
            Tags = new List<string> { "learning", "test" }
        };

        var result = FrontmatterParser.Write(metadata, "Body content here");

        Assert.StartsWith("---\n", result);
        Assert.Contains("id: test_id_123456", result);
        Assert.Contains("type: learning", result);
        Assert.Contains("Body content here", result);
    }

    [Fact]
    public async Task ReadMemory_ReturnsExistingFile()
    {
        var entry = await _memoryService.RememberAsync("Test content", MemoryType.Checkpoint);

        var read = await _memoryService.ReadMemoryAsync(entry.FilePath);

        Assert.NotNull(read);
        Assert.Equal(entry.Metadata.Id, read.Metadata.Id);
        Assert.Equal("Test content", read.Content);
    }
}
```

**Step 2: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~MemoryTests"`
Expected: All tests pass

**Step 3: Commit**

```bash
git add tests/Codesearch.Tests/MemoryTests.cs
git commit -m "test: add memory system tests"
```

---

### Task 7: Integrate Memory Files into Indexing

**Files:**
- Modify: `src/Codesearch.Server/Services/IndexService.cs`

**Step 1: Update IndexService to handle memory files**

Add memory file handling to `IndexService.cs`. After the `SupportedExtensions` HashSet, add:

```csharp
private static bool IsMemoryFile(string path)
{
    return path.Contains(".memories") && path.EndsWith(".md");
}
```

Update `IndexFileAsync` to handle memory files specially:

```csharp
private async Task IndexFileAsync(string absolutePath, string relativePath)
{
    var content = await File.ReadAllTextAsync(absolutePath);
    var extension = Path.GetExtension(relativePath).TrimStart('.');

    string embedContent;
    string name;
    string kind;

    // Special handling for memory files - strip frontmatter, prepend tags
    if (IsMemoryFile(relativePath))
    {
        try
        {
            var (metadata, body) = Memory.FrontmatterParser.Parse(content);
            var tagPrefix = metadata.Tags.Count > 0 ? string.Join(" ", metadata.Tags) + " " : "";
            embedContent = tagPrefix + body;
            name = Path.GetFileName(relativePath);
            kind = metadata.Type.ToString().ToLowerInvariant();
        }
        catch
        {
            // Fallback if parsing fails
            embedContent = content;
            name = Path.GetFileName(relativePath);
            kind = "memory";
        }
    }
    else
    {
        embedContent = content;
        name = Path.GetFileName(relativePath);
        kind = "file";
    }

    // Truncate content for embedding
    if (embedContent.Length > 4096)
    {
        embedContent = embedContent[..4096];
    }

    var symbol = new SymbolInput(
        id: $"file_{Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(relativePath)))[..16]}",
        name: name,
        kind: kind,
        language: extension,
        filePath: relativePath.Replace('\\', '/'),
        signature: null,
        docComment: null,
        startLine: 1,
        endLine: content.Split('\n').Length,
        content: embedContent
    );

    // Placeholder vector (768 zeros)
    var vector = Enumerable.Repeat(0.0f, 768).ToList();

    _searchService.AddSymbols(new List<SymbolInput> { symbol }, new List<List<float>> { vector });
}
```

Also update `IsIgnoredPath` to NOT ignore `.memories`:

```csharp
private bool IsIgnoredPath(string path)
{
    var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return parts.Any(p => p is ".git" or "node_modules" or "target" or "bin" or "obj"
                       or ".codesearch" or "__pycache__" or ".venv" or "venv");
    // Note: .memories is NOT ignored - we want to index memory files
}
```

**Step 2: Add using directive**

Add at the top of `IndexService.cs`:

```csharp
using Codesearch.Server.Memory;
```

**Step 3: Verify it compiles**

Run: `dotnet build src/Codesearch.Server`
Expected: Success

**Step 4: Commit**

```bash
git add src/Codesearch.Server/Services/IndexService.cs
git commit -m "feat(memory): integrate memory files into indexing"
```

---

### Task 8: Update File Watcher for Memory Files

**Files:**
- Modify: `src/Codesearch.Server/Services/FileWatcherService.cs`

**Step 1: Add .md to watched extensions**

In `FileWatcherService.cs`, ensure `.md` is in the `WatchedExtensions` set (it should already be there, verify):

```csharp
private static readonly HashSet<string> WatchedExtensions = new(StringComparer.OrdinalIgnoreCase)
{
    ".rs", ".py", ".ts", ".tsx", ".js", ".jsx", ".go", ".java", ".cs",
    ".c", ".cpp", ".h", ".hpp", ".rb", ".php", ".swift", ".kt", ".md"
};
```

**Step 2: Update ShouldWatch to include .memories**

Verify `.memories` is NOT in the ignored list in `ShouldWatch`:

```csharp
private bool ShouldWatch(string path)
{
    var ext = Path.GetExtension(path);
    if (!WatchedExtensions.Contains(ext)) return false;

    // Ignore common build/dependency directories, but NOT .memories
    var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return !parts.Any(p => p is ".git" or "node_modules" or "target" or "bin" or "obj" or ".codesearch");
}
```

**Step 3: Verify it compiles**

Run: `dotnet build src/Codesearch.Server`
Expected: Success

**Step 4: Commit**

```bash
git add src/Codesearch.Server/Services/FileWatcherService.cs
git commit -m "feat(memory): update file watcher to include memory files"
```

---

### Task 9: Manual Test and Final Verification

**Step 1: Run all tests**

Run: `dotnet test`
Expected: All tests pass

**Step 2: Build and run server**

Run: `dotnet build src/Codesearch.Server`
Expected: Success

**Step 3: Verify memory directory structure**

After creating memories via the MCP tool, verify:
- `.memories/YYYY-MM-DD/` directories are created
- Files have proper frontmatter
- Plans go to `.memories/plans/`

**Step 4: Final commit**

```bash
git add -A
git commit -m "feat(memory): complete memory system implementation"
```

---

## Phase 4 Complete

At this point you have:
- Memory file format with YAML frontmatter
- MemoryService for remember/recall operations
- MCP `memory` tool with remember, recall, status operations
- Memory files indexed alongside code
- File watcher triggers reindex on memory file changes
- Comprehensive tests

**Next Phase (5):** Cross-Project System - central registry, cross-project aggregation, standup operation.
