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
