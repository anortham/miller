using Microsoft.Data.Sqlite;
using Miller.Core.Search;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class ContentCorpusExternalStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _contentDbPath;

    public ContentCorpusExternalStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-external-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _contentDbPath = Path.Combine(_dir, ".miller", "content.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void ImportSearchReadListAndRemove_WorkWithoutSymbolsDb()
    {
        string logPath = Path.Combine(_dir, "build.log");
        File.WriteAllText(logPath, """
            line one
            warmup complete
            KnownExternalError happened
            cleanup complete
            """);
        var store = new ContentCorpusExternalStore();

        ExternalContentImportResult imported = store.Import(_contentDbPath, logPath);

        Assert.False(File.Exists(Path.Combine(_dir, ".miller", "symbols.db")));
        Assert.Equal(TextContentKind.ExternalFile, imported.ContentKind);
        Assert.Equal(logPath, imported.DisplayPath);
        Assert.StartsWith("external_file:", imported.SourceId, StringComparison.Ordinal);
        Assert.StartsWith("blake3:", imported.ContentHash, StringComparison.Ordinal);
        Assert.Equal(1, imported.ChunkCount);
        Assert.True(imported.SourceBytes > 0);

        TextContentSearchHit hit = Assert.Single(store.Search(_contentDbPath, "KnownExternalError", limit: 5));
        Assert.Equal(imported.SourceId, hit.SourceId);
        Assert.Equal(TextContentKind.ExternalFile, hit.ContentKind);
        Assert.Equal(3, hit.Line);
        Assert.Contains("KnownExternalError", hit.Snippet);

        ExternalContentReadResult read = store.ReadWindow(_contentDbPath, imported.SourceId, line: 3, contextLines: 1);
        Assert.Equal(2, read.LineStart);
        Assert.Equal(4, read.LineEnd);
        Assert.Equal(["warmup complete", "KnownExternalError happened", "cleanup complete"],
            read.Lines.Select(static line => line.Text).ToArray());

        ExternalContentSource listed = Assert.Single(store.List(_contentDbPath));
        Assert.Equal(imported.SourceId, listed.SourceId);
        Assert.Equal(logPath, listed.DisplayPath);
        Assert.Equal(1, listed.ChunkCount);

        ExternalContentRemoveResult removed = store.Remove(_contentDbPath, imported.SourceId);
        Assert.True(removed.Removed);
        Assert.Equal(1, removed.SourceCount);
        Assert.Empty(store.List(_contentDbPath));
        Assert.Empty(store.Search(_contentDbPath, "KnownExternalError", limit: 5));
    }

    [Fact]
    public void DefaultMaxImportBytes_IsTwentyFiveMiB()
    {
        Assert.Equal(25L * 1024 * 1024, ContentCorpusExternalStore.DefaultMaxImportBytes);
    }

    [Fact]
    public void Import_RejectsFilesOverDefaultCapUnlessMaxBytesAllowsThem()
    {
        string logPath = Path.Combine(_dir, "large.log");
        File.WriteAllText(logPath, "0123456789");
        var store = new ContentCorpusExternalStore(defaultMaxImportBytes: 5);

        var ex = Assert.Throws<InvalidOperationException>(() => store.Import(_contentDbPath, logPath));

        Assert.Contains("max_bytes", ex.Message, StringComparison.OrdinalIgnoreCase);
        ExternalContentImportResult imported = store.Import(_contentDbPath, logPath, maxBytes: 10);
        Assert.Equal(10, imported.SourceBytes);
    }

    [Fact]
    public void Import_WhenContentWriteLockIsHeld_TimesOutWithoutMutating()
    {
        string logPath = Path.Combine(_dir, "locked.log");
        File.WriteAllText(logPath, "LockedImportMarker should not be indexed.");
        using var held = ContentCorpusWriteLock.AcquireFor(_contentDbPath);
        var store = new ContentCorpusExternalStore(writeLockTimeout: TimeSpan.Zero);

        var ex = Assert.Throws<TimeoutException>(() => store.Import(_contentDbPath, logPath));

        Assert.Contains("content corpus write lock", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(_contentDbPath));
    }

    [Fact]
    public void Remove_WhenContentWriteLockIsHeld_TimesOutWithoutMutating()
    {
        string logPath = Path.Combine(_dir, "remove-locked.log");
        File.WriteAllText(logPath, "LockedRemoveMarker should stay indexed.");
        var store = new ContentCorpusExternalStore();
        ExternalContentImportResult imported = store.Import(_contentDbPath, logPath);
        using var held = ContentCorpusWriteLock.AcquireFor(_contentDbPath);
        var lockedStore = new ContentCorpusExternalStore(writeLockTimeout: TimeSpan.Zero);

        var ex = Assert.Throws<TimeoutException>(() => lockedStore.Remove(_contentDbPath, imported.SourceId));

        Assert.Contains("content corpus write lock", ex.Message, StringComparison.OrdinalIgnoreCase);
        TextContentSearchHit hit = Assert.Single(store.Search(_contentDbPath, "LockedRemoveMarker", limit: 5));
        Assert.Equal(imported.SourceId, hit.SourceId);
    }

    [Fact]
    public void ImportMarkdown_StoresWebContentWithUrlMetadata_AndKindScopedSearch()
    {
        string markdownPath = Path.Combine(_dir, "page.md");
        File.WriteAllText(markdownPath, """
            # Example Page

            WebResearchMarker appears in the body.
            """);
        string logPath = Path.Combine(_dir, "build.log");
        File.WriteAllText(logPath, "WebResearchMarker appears in an external log.");
        var store = new ContentCorpusExternalStore();

        store.Import(_contentDbPath, logPath);
        ExternalContentImportResult imported = store.ImportMarkdown(
            _contentDbPath,
            markdownPath,
            url: "https://example.test/page",
            displayPath: "Example Page");

        Assert.Equal(TextContentKind.Web, imported.ContentKind);
        Assert.Equal("https://example.test/page", imported.Url);
        Assert.Equal("Example Page", imported.DisplayPath);
        Assert.StartsWith("web:", imported.SourceId, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_dir, "docs", "web")));

        TextContentSearchHit hit = Assert.Single(store.Search(
            _contentDbPath,
            "WebResearchMarker",
            TextContentKind.Web,
            limit: 5));
        Assert.Equal(imported.SourceId, hit.SourceId);
        Assert.Equal(TextContentKind.Web, hit.ContentKind);
        Assert.Equal("https://example.test/page", hit.Url);
        Assert.Equal("Example Page", hit.DisplayPath);

        ExternalContentSource listed = Assert.Single(store.List(_contentDbPath, TextContentKind.Web));
        Assert.Equal(imported.SourceId, listed.SourceId);
        Assert.Equal("https://example.test/page", listed.Url);

        Assert.DoesNotContain(store.Search(_contentDbPath, "WebResearchMarker", TextContentKind.ExternalFile, limit: 5),
            static h => h.ContentKind == TextContentKind.Web);
    }
}
