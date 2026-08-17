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
    public void ImportWithoutWorkspaceCorpus_ReportsImportsOnlyInsteadOfUnreadable()
    {
        string logPath = Path.Combine(_dir, "imports-only.log");
        File.WriteAllText(logPath, "Imported content remains searchable.");
        var store = new ContentCorpusExternalStore();
        store.Import(_contentDbPath, logPath);

        var sidecar = new ContentCorpusSidecar();
        ContentCorpusFacts facts = sidecar.Inspect(
            Path.Combine(_dir, ".miller", "symbols.db"),
            expectedRevision: 1);

        Assert.Equal("imports_only", facts.State);
        Assert.Null(facts.WorkspaceRevision);
        Assert.Equal(1, facts.SourceCount);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FtsTextContentSearchIndex.Open(_contentDbPath, expectedRevision: 1));
        Assert.Contains("imports only", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("workspace refresh", ex.Message, StringComparison.OrdinalIgnoreCase);
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
    public void Import_WithRaisedMaxBytes_StreamsAndPreservesHashChunksAndLineWindows()
    {
        string logPath = Path.Combine(_dir, "streamed.log");
        string content = string.Join('\n', Enumerable.Range(1, 500).Select(static line => $"line {line:D3}"));
        File.WriteAllText(logPath, content);
        var store = new ContentCorpusExternalStore(defaultMaxImportBytes: 64);

        ExternalContentImportResult imported = store.Import(
            _contentDbPath,
            logPath,
            maxBytes: new FileInfo(logPath).Length);

        Assert.Equal("blake3:" + ContentHasher.Blake3FileHex(logPath), imported.ContentHash);
        Assert.True(imported.ChunkCount > 1);
        ExternalContentReadResult first = store.ReadWindow(_contentDbPath, imported.SourceId, line: 1, contextLines: 0);
        ExternalContentReadResult middle = store.ReadWindow(_contentDbPath, imported.SourceId, line: 250, contextLines: 0);
        ExternalContentReadResult last = store.ReadWindow(_contentDbPath, imported.SourceId, line: 500, contextLines: 0);
        Assert.Equal("line 001", Assert.Single(first.Lines).Text);
        Assert.Equal("line 250", Assert.Single(middle.Lines).Text);
        Assert.Equal("line 500", Assert.Single(last.Lines).Text);
        Assert.Equal(500, last.SourceLineCount);
    }

    [Fact]
    public void Import_NonStreamingAndStreamingUseNormalizedLfByteOffsetsForCrLfInput()
    {
        string lfPath = Path.Combine(_dir, "lf.log");
        string crlfPath = Path.Combine(_dir, "crlf.log");
        string normalized = string.Join(
            '\n',
            Enumerable.Range(1, ContentCorpusChunker.DefaultChunkLines + 40)
                .Select(static line => $"líne {line:D3}"));
        File.WriteAllText(lfPath, normalized);
        File.WriteAllText(crlfPath, normalized.Replace("\n", "\r\n", StringComparison.Ordinal));
        var nonStreamingStore = new ContentCorpusExternalStore();
        var streamingStore = new ContentCorpusExternalStore(defaultMaxImportBytes: 64);

        ExternalContentImportResult lf = nonStreamingStore.Import(_contentDbPath, lfPath);
        ExternalContentImportResult crlf = streamingStore.Import(
            _contentDbPath,
            crlfPath,
            maxBytes: new FileInfo(crlfPath).Length);

        Assert.True(crlf.SourceBytes > lf.SourceBytes);
        using var connection = new SqliteConnection($"Data Source={_contentDbPath};Mode=ReadOnly");
        connection.Open();
        IReadOnlyList<(int LineStart, int LineEnd, long ByteStart, long ByteEnd, string Text)> ReadChunks(
            string sourceId)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT line_start, line_end, byte_start, byte_end, raw_text
                FROM content_chunks
                WHERE source_id = $source
                ORDER BY line_start, chunk_id;
                """;
            command.Parameters.AddWithValue("$source", sourceId);
            var chunks = new List<(int, int, long, long, string)>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                chunks.Add((
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetString(4)));
            }
            return chunks;
        }

        Assert.Equal(ReadChunks(lf.SourceId), ReadChunks(crlf.SourceId));
        Assert.Equal(
            System.Text.Encoding.UTF8.GetByteCount(normalized),
            ReadChunks(lf.SourceId)[^1].ByteEnd);
    }

    [Fact]
    public void Import_WithRaisedMaxBytes_RejectsOverlongLogicalLineWithoutPersistingPartialChunks()
    {
        string logPath = Path.Combine(_dir, "overlong-line.log");
        File.WriteAllText(logPath, new string('x', ContentCorpusExternalStore.MaxStreamingLineChars + 1));
        var store = new ContentCorpusExternalStore(defaultMaxImportBytes: 64);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            store.Import(_contentDbPath, logPath, maxBytes: new FileInfo(logPath).Length));

        Assert.Contains("logical line", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.List(_contentDbPath));
        Assert.Empty(store.Search(_contentDbPath, "xxxxx", limit: 5));
    }

    [Fact]
    public void Import_WithRaisedMaxBytes_BoundsPersistedChunksAndPreservesReadAndHashContracts()
    {
        string logPath = Path.Combine(_dir, "bounded-chunks.log");
        string line = new('x', 60_000);
        File.WriteAllText(logPath, string.Join('\n', Enumerable.Repeat(line, 20)));
        var store = new ContentCorpusExternalStore(defaultMaxImportBytes: 64);

        ExternalContentImportResult imported = store.Import(
            _contentDbPath,
            logPath,
            maxBytes: new FileInfo(logPath).Length);

        Assert.Equal("blake3:" + ContentHasher.Blake3FileHex(logPath), imported.ContentHash);
        Assert.Equal(line, Assert.Single(store.ReadWindow(
            _contentDbPath,
            imported.SourceId,
            line: 20,
            contextLines: 0).Lines).Text);
        using var connection = new SqliteConnection($"Data Source={_contentDbPath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(length(raw_text)) FROM content_chunks;";
        Assert.InRange(
            Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture),
            1,
            ContentCorpusExternalStore.MaxStreamingChunkChars);
    }

    [Fact]
    public void Import_WithRaisedMaxBytes_InvalidUtf8RollsBackChunksWrittenBeforeDecodeFailure()
    {
        string logPath = Path.Combine(_dir, "invalid-streamed.log");
        File.WriteAllText(logPath, "OriginalRollbackMarker\n");
        var store = new ContentCorpusExternalStore(defaultMaxImportBytes: 64);
        ExternalContentImportResult original = store.Import(_contentDbPath, logPath);
        byte[] validPrefix = System.Text.Encoding.UTF8.GetBytes(
            string.Concat(Enumerable.Repeat(
                "valid searchable line " + new string('v', 96) + "\n",
                ContentCorpusChunker.DefaultChunkLines + 40)));
        Assert.True(validPrefix.Length > 16 * 1024);
        Assert.True(validPrefix.Count(static value => value == (byte)'\n') > ContentCorpusChunker.DefaultChunkLines);
        File.WriteAllBytes(logPath, [.. validPrefix, 0xC3, 0x28]);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            store.Import(_contentDbPath, logPath, maxBytes: new FileInfo(logPath).Length));

        Assert.Contains("UTF-8", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original.SourceId, Assert.Single(store.List(_contentDbPath)).SourceId);
        Assert.Single(store.Search(_contentDbPath, "OriginalRollbackMarker", limit: 5));
        Assert.Empty(store.Search(_contentDbPath, "searchable", limit: 5));
    }

    [Fact]
    public void Import_WithRaisedMaxBytes_SizeDriftRollsBackChunksWrittenBeforeLengthCheck()
    {
        string logPath = Path.Combine(_dir, "size-drift-streamed.log");
        File.WriteAllText(logPath, "OriginalSizeMarker\n");
        var store = new ContentCorpusExternalStore(defaultMaxImportBytes: 4);
        ExternalContentImportResult original = store.Import(
            _contentDbPath,
            logPath,
            maxBytes: new FileInfo(logPath).Length);
        string replacement = string.Concat(Enumerable.Repeat(
            "replacement searchable line " + new string('r', 96) + "\n",
            ContentCorpusChunker.DefaultChunkLines + 40));
        byte[] bytesBeforeGrowth = System.Text.Encoding.UTF8.GetBytes(replacement);
        Assert.True(bytesBeforeGrowth.Length > 16 * 1024);
        Assert.True(bytesBeforeGrowth.Count(static value => value == (byte)'\n') > ContentCorpusChunker.DefaultChunkLines);
        File.WriteAllText(logPath, replacement);
        byte[] bytesAfterGrowth = System.Text.Encoding.UTF8.GetBytes(replacement + "concurrent growth\n");
        var driftingStore = new ContentCorpusExternalStore(
            defaultMaxImportBytes: 4,
            writeLockTimeout: null,
            _ => new MemoryStream(bytesAfterGrowth, writable: false));

        var ex = Assert.Throws<IOException>(() =>
            driftingStore.Import(
                _contentDbPath,
                logPath,
                maxBytes: bytesAfterGrowth.Length + 1L));

        Assert.Contains("changed while it was being imported", ex.Message, StringComparison.Ordinal);
        Assert.Equal(original.SourceId, Assert.Single(store.List(_contentDbPath)).SourceId);
        Assert.Single(store.Search(_contentDbPath, "OriginalSizeMarker", limit: 5));
        Assert.Empty(store.Search(_contentDbPath, "replacement", limit: 5));
    }

    [Fact]
    public void Import_WithRaisedMaxBytes_RejectsGrowthBeforeReadingTheWholeStream()
    {
        string logPath = Path.Combine(_dir, "growing-streamed.log");
        File.WriteAllText(logPath, new string('a', 512));
        byte[] grownBytes = System.Text.Encoding.UTF8.GetBytes(
            string.Concat(Enumerable.Repeat("grown line\n", 100_000)));
        var store = new ContentCorpusExternalStore(
            defaultMaxImportBytes: 4,
            writeLockTimeout: null,
            _ => new ThrowAfterPositionStream(grownBytes, 20 * 1024));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            store.Import(_contentDbPath, logPath, maxBytes: 1024));

        Assert.Contains("exceeds max_bytes 1024", ex.Message, StringComparison.Ordinal);
        Assert.Empty(store.List(_contentDbPath));
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

    [Fact]
    public void Import_WhileWriterHoldsReadWriteShare_SucceedsThroughProductionOpen()
    {
        string logPath = Path.Combine(_dir, "live.log");
        using var writer = OpenLiveWriter(logPath, "LiveImportMarker from a write-shared handle.\n");
        var store = new ContentCorpusExternalStore();

        ExternalContentImportResult imported = store.Import(_contentDbPath, logPath);

        Assert.True(imported.SourceBytes > 0);
        TextContentSearchHit hit = Assert.Single(store.Search(_contentDbPath, "LiveImportMarker", limit: 5));
        Assert.Equal(imported.SourceId, hit.SourceId);
        Assert.Contains("LiveImportMarker", hit.Snippet);
    }

    [Fact]
    public void Import_StreamingWhileWriterHoldsReadWriteShare_SucceedsThroughProductionOpen()
    {
        string logPath = Path.Combine(_dir, "live-streamed.log");
        string content = string.Join(
            '\n',
            Enumerable.Range(1, 80).Select(static line => $"live line {line:D3} LiveStreamMarker"));
        using var writer = OpenLiveWriter(logPath, content);
        var store = new ContentCorpusExternalStore(defaultMaxImportBytes: 64);

        ExternalContentImportResult imported = store.Import(
            _contentDbPath,
            logPath,
            maxBytes: new FileInfo(logPath).Length);

        Assert.True(imported.SourceBytes > 64);
        TextContentSearchHit hit = Assert.Single(store.Search(_contentDbPath, "LiveStreamMarker", limit: 5));
        Assert.Equal(imported.SourceId, hit.SourceId);
        Assert.Contains("LiveStreamMarker", hit.Snippet);
    }

    [Fact]
    public void Import_MissingFile_ThrowsFileNotFound()
    {
        string missing = Path.Combine(_dir, "missing.log");
        var store = new ContentCorpusExternalStore();

        var ex = Assert.Throws<FileNotFoundException>(() => store.Import(_contentDbPath, missing));

        Assert.Equal(missing, ex.FileName);
        Assert.Contains(missing, ex.Message, StringComparison.Ordinal);
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(_contentDbPath));
    }

    [Fact]
    public void Import_Directory_ThrowsFileNotFound()
    {
        string directory = Path.Combine(_dir, "not-a-file");
        Directory.CreateDirectory(directory);
        var store = new ContentCorpusExternalStore();

        var ex = Assert.Throws<FileNotFoundException>(() => store.Import(_contentDbPath, directory));

        Assert.Equal(directory, ex.FileName);
        Assert.Contains(directory, ex.Message, StringComparison.Ordinal);
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(_contentDbPath));
    }

    // Serilog shared:true + size/daily roll opens the live log with write and delete share.
    private static FileStream OpenLiveWriter(string path, string content)
    {
        var writer = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        try
        {
            using var text = new StreamWriter(writer, leaveOpen: true);
            text.Write(content);
            text.Flush();
            return writer;
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    private sealed class ThrowAfterPositionStream(byte[] bytes, long limit) : MemoryStream(bytes, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position >= limit)
                throw new InvalidOperationException("Import continued reading after the fail-fast threshold.");
            return base.Read(buffer, offset, (int)Math.Min(count, limit - Position));
        }

        public override int Read(Span<byte> buffer)
        {
            if (Position >= limit)
                throw new InvalidOperationException("Import continued reading after the fail-fast threshold.");
            return base.Read(buffer[..(int)Math.Min(buffer.Length, limit - Position)]);
        }
    }
}
