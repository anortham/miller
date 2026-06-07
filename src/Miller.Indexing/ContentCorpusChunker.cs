using System.Text;
using Miller.Core.Search;
using Miller.Core.Tokenization;

namespace Miller.Indexing;

public static class ContentCorpusChunker
{
    public const int DefaultChunkLines = 160;
    public const int DefaultOverlapLines = 20;

    public static IReadOnlyList<TextContentDocument> Chunk(
        string sourceId,
        string contentKind,
        string? path,
        string? url,
        string displayPath,
        string language,
        string text,
        long sourceBytes,
        bool isTest,
        int chunkLines = DefaultChunkLines,
        int overlapLines = DefaultOverlapLines,
        IReadOnlyList<ContentCorpusSymbolSpan>? containingSymbols = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayPath);
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(text);
        if (chunkLines <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkLines), "chunkLines must be > 0.");
        if (overlapLines < 0 || overlapLines >= chunkLines)
            throw new ArgumentOutOfRangeException(nameof(overlapLines), "overlapLines must be >= 0 and smaller than chunkLines.");

        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        long[] starts = LineByteStarts(lines);
        var chunks = new List<TextContentDocument>();
        var tokens = new List<string>(128);
        int start = 0;

        while (start < lines.Length)
        {
            int endExclusive = Math.Min(lines.Length, start + chunkLines);
            string chunkText = string.Join('\n', lines, start, endExclusive - start);
            int lineStart = start + 1;
            int lineEnd = endExclusive;
            long byteStart = starts[start];
            long byteEnd = endExclusive >= lines.Length
                ? Encoding.UTF8.GetByteCount(normalized)
                : starts[endExclusive] - 1; // exclude the separator before the next line
            ContentCorpusSymbolSpan? symbol = BestSymbolForLine(path, lineStart, containingSymbols);

            tokens.Clear();
            CodeTokenizer.Tokenize(chunkText, tokens);
            chunks.Add(new TextContentDocument(
                sourceId,
                ChunkId(sourceId, lineStart, byteStart),
                contentKind,
                path,
                url,
                displayPath,
                language,
                lineStart,
                lineEnd,
                byteStart,
                byteEnd,
                chunkText,
                tokens.Count,
                isTest,
                sourceBytes,
                symbol?.SymbolId,
                symbol?.Name));

            if (endExclusive >= lines.Length)
                break;

            start = Math.Max(start + 1, endExclusive - overlapLines);
        }

        return chunks;
    }

    public static int CountLines(string text)
    {
        if (text.Length == 0)
            return 1;

        int count = 1;
        foreach (char ch in text)
            if (ch == '\n')
                count++;
        return count;
    }

    private static long[] LineByteStarts(string[] lines)
    {
        var starts = new long[lines.Length];
        long offset = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            starts[i] = offset;
            offset += Encoding.UTF8.GetByteCount(lines[i]);
            if (i < lines.Length - 1)
                offset++;
        }

        return starts;
    }

    private static ContentCorpusSymbolSpan? BestSymbolForLine(
        string? path,
        int line,
        IReadOnlyList<ContentCorpusSymbolSpan>? symbols)
    {
        if (symbols is null || symbols.Count == 0 || string.IsNullOrWhiteSpace(path))
            return null;

        ContentCorpusSymbolSpan? best = null;
        int bestWidth = int.MaxValue;
        foreach (ContentCorpusSymbolSpan symbol in symbols)
        {
            if (!string.Equals(symbol.Path, path, StringComparison.Ordinal))
                continue;
            int end = symbol.EndLine <= 0 ? symbol.StartLine : symbol.EndLine;
            if (line < symbol.StartLine || line > end)
                continue;
            int width = end - symbol.StartLine;
            if (width < bestWidth)
            {
                best = symbol;
                bestWidth = width;
            }
        }

        return best;
    }

    private static string ChunkId(string sourceId, int lineStart, long byteStart) =>
        sourceId + "#" + lineStart.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        ":" + byteStart.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
