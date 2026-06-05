using System.Text;
using Miller.Core.Search;

namespace Miller.Indexing;

/// <summary>
/// Builds a <see cref="ContentSearchProjection"/> from a julie extract DB (phase 3). Reads the v1
/// <c>files</c> manifest and indexes only docs-like, <c>indexed</c>-status, in-size files, re-sourced from
/// disk under the workspace root and BLAKE3-verified against the stored <c>content_hash</c> (the §7 hard
/// freshness/trust invariant — a drifted, missing, oversize, out-of-scope, non-UTF-8, or unreadable file is
/// SKIPPED, never errored). Source files are excluded (<see cref="ContentFileClassifier.IsDocsLike"/>) because
/// symbol search already covers them.
/// </summary>
public static class ContentSearchProjectionLoader
{
    /// <summary>Per-file cap (1 MiB), matching the measured 2026-06-02 spike default.</summary>
    private const long MaxContentBytes = 1_048_576;

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Load the content projection for <paramref name="dbPath"/>, re-sourcing file content from disk under
    /// <paramref name="workspaceRoot"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Either argument is null/empty/whitespace.</exception>
    public static ContentSearchProjection Load(string dbPath, string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        var documents = new List<ContentDocument>();

        using (var connection = SqliteReadOnlyAccess.Open(dbPath))
        using (var command = connection.CreateCommand())
        {
            JulieSchemaGate.Verify(connection);

            command.CommandText =
                "SELECT path, language, content_hash, content_bytes, status FROM files ORDER BY path;";
            using var reader = command.ExecuteReader();
            int pathOrdinal = reader.GetOrdinal("path");
            int languageOrdinal = reader.GetOrdinal("language");
            int hashOrdinal = reader.GetOrdinal("content_hash");
            int bytesOrdinal = reader.GetOrdinal("content_bytes");
            int statusOrdinal = reader.GetOrdinal("status");

            while (reader.Read())
            {
                if (!string.Equals(reader.GetString(statusOrdinal), "indexed", StringComparison.Ordinal))
                    continue;

                string path = reader.GetString(pathOrdinal);
                string language = reader.GetString(languageOrdinal);
                if (!ContentFileClassifier.IsDocsLike(path, language))
                    continue;

                if (reader.GetInt64(bytesOrdinal) > MaxContentBytes)
                    continue;

                string? storedHash = reader.IsDBNull(hashOrdinal) ? null : reader.GetString(hashOrdinal);
                string? text = ReadVerifiedDocsText(workspaceRoot, path, storedHash);
                if (text is null)
                    continue;

                documents.Add(new ContentDocument(documents.Count, path, text, language));
            }
        }

        return ContentSearchProjection.Build(documents);
    }

    // Re-source the on-disk file under the workspace root, returning its text ONLY if its BLAKE3 still matches
    // the stored content_hash AND it decodes as UTF-8; otherwise null (skip). Path-safety is the shared
    // WorkspaceRelativePath trust boundary; the hash compare is blake3-only (NormalizeHash strips a blake3:
    // scheme token), the same hard freshness invariant ExtractReader.ReadBody enforces for body slices.
    private static string? ReadVerifiedDocsText(string workspaceRoot, string relPath, string? storedHash)
    {
        if (string.IsNullOrWhiteSpace(storedHash))
            return null;

        string? abs = WorkspaceRelativePath.ResolveUnderRoot(workspaceRoot, relPath);
        if (abs is null || !File.Exists(abs))
            return null;

        byte[] bytes;
        try
        {
            if (new FileInfo(abs).Length > MaxContentBytes)
                return null;

            bytes = File.ReadAllBytes(abs);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (bytes.LongLength > MaxContentBytes)
            return null;

        if (!StringComparer.OrdinalIgnoreCase.Equals(
                ContentHasher.Blake3Hex(bytes), ContentHasher.NormalizeHash(storedHash)))
            return null;

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
