using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>
/// Advisory reads over Miller's content corpus source chunks. This is not an edit buffer: chunk text may be
/// normalized and overlapping, so callers use it only to explain what the index currently knows.
/// </summary>
public sealed class IndexedSourceTextReader
{
    public IndexedSourceTextMatch? FindLiteral(string symbolsDbPath, string relativePath, string literal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolsDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(literal);

        if (literal.Length == 0)
            return null;

        string contentDbPath = ContentCorpusSidecar.ContentDbPathFor(symbolsDbPath);
        // A hit from a superseded generation would claim the indexed source "still contains" text at a line that
        // belongs to a rebuild the workspace has moved past, and the caller turns that claim into a stale-target
        // diagnostic and a converge retry — so an unprovable generation must produce no hit at all.
        if (!ContentCorpusSidecar.GenerationAgrees(contentDbPath, symbolsDbPath))
            return null;

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = contentDbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT c.line_start, c.line_end, c.raw_text
                FROM content_sources s
                JOIN content_chunks c ON c.source_id = s.source_id
                WHERE s.content_kind = $kind
                  AND s.path = $path
                  AND s.status = 'active'
                  AND instr(c.raw_text, $literal) > 0
                ORDER BY c.line_start, c.byte_start
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$kind", TextContentKind.WorkspaceSource);
            command.Parameters.AddWithValue("$path", relativePath);
            command.Parameters.AddWithValue("$literal", literal);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;

            int chunkLineStart = reader.GetInt32(0);
            int chunkLineEnd = reader.GetInt32(1);
            string rawText = reader.GetString(2);
            int line = LineForMatch(rawText, literal, chunkLineStart);
            return new IndexedSourceTextMatch(relativePath, line, chunkLineEnd);
        }
        catch (SqliteException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int LineForMatch(string rawText, string literal, int chunkLineStart)
    {
        int match = rawText.IndexOf(literal, StringComparison.Ordinal);
        if (match <= 0)
            return chunkLineStart;

        int line = chunkLineStart;
        for (int i = 0; i < match; i++)
            if (rawText[i] == '\n')
                line++;
        return line;
    }
}

public sealed record IndexedSourceTextMatch(string Path, int Line, int ChunkLineEnd);
