using System.Text;
using Miller.Core.Search;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class ContentCorpusChunkerTests
{
    [Fact]
    public void Chunk_SplitsByLines_WithConfiguredOverlapAndByteRanges()
    {
        string text = "one\nKnownSourceError two\nthree\nfour";

        IReadOnlyList<TextContentDocument> chunks = ContentCorpusChunker.Chunk(
            sourceId: "workspace:src/App.cs",
            contentKind: TextContentKind.WorkspaceSource,
            path: "src/App.cs",
            url: null,
            displayPath: "src/App.cs",
            language: "csharp",
            text,
            sourceBytes: Encoding.UTF8.GetByteCount(text),
            isTest: false,
            chunkLines: 2,
            overlapLines: 1,
            containingSymbols: Array.Empty<ContentCorpusSymbolSpan>());

        Assert.Equal(3, chunks.Count);
        Assert.Equal([(1, 2), (2, 3), (3, 4)], chunks.Select(static c => (c.LineStart, c.LineEnd)).ToArray());
        Assert.Equal("one\nKnownSourceError two", chunks[0].Text);
        Assert.Equal("KnownSourceError two\nthree", chunks[1].Text);
        Assert.Equal("three\nfour", chunks[2].Text);
        Assert.Equal(0, chunks[0].ByteStart);
        Assert.True(chunks[0].ByteEnd > chunks[0].ByteStart);
        Assert.Equal(chunks[0].ByteEnd - Encoding.UTF8.GetByteCount("KnownSourceError two"), chunks[1].ByteStart);
    }

    [Fact]
    public void Chunk_AttachesContainingSymbolForChunkStartLine_WhenAvailable()
    {
        string text = "public class Api\n{\n  void Handle() {}\n}";

        IReadOnlyList<TextContentDocument> chunks = ContentCorpusChunker.Chunk(
            sourceId: "workspace:src/Api.cs",
            contentKind: TextContentKind.WorkspaceSource,
            path: "src/Api.cs",
            url: null,
            displayPath: "src/Api.cs",
            language: "csharp",
            text,
            sourceBytes: Encoding.UTF8.GetByteCount(text),
            isTest: false,
            chunkLines: 2,
            overlapLines: 0,
            containingSymbols:
            [
                new ContentCorpusSymbolSpan("sym-api", "Api", "src/Api.cs", 1, 4),
                new ContentCorpusSymbolSpan("sym-handle", "Handle", "src/Api.cs", 3, 3),
            ]);

        Assert.Equal("sym-api", chunks[0].ContainingSymbolId);
        Assert.Equal("Api", chunks[0].ContainingSymbolName);
        Assert.Equal("sym-handle", chunks[1].ContainingSymbolId);
        Assert.Equal("Handle", chunks[1].ContainingSymbolName);
    }
}
