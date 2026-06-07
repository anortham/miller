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
}
