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
