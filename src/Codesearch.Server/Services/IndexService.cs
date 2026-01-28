using System.Security.Cryptography;
using uniffi.codesearch_ffi;
using Codesearch.Server.Memory;

namespace Codesearch.Server.Services;

/// <summary>
/// Service for managing the code index.
/// </summary>
internal class IndexService
{
    private readonly SearchService _searchService;
    private readonly Dictionary<string, string> _fileHashes = new();
    private readonly string _workspaceRoot;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".rs", ".py", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs",
        ".go", ".java", ".cs", ".c", ".cpp", ".cc", ".cxx", ".h", ".hpp",
        ".rb", ".php", ".swift", ".kt", ".kts", ".dart", ".lua", ".r",
        ".sh", ".bash", ".ps1", ".zig", ".html", ".css", ".vue", ".qml",
        ".gd", ".cshtml", ".razor", ".sql", ".md", ".json", ".toml", ".yaml", ".yml"
    };

    private static bool IsMemoryFile(string path)
    {
        return path.Contains(".memories") && path.EndsWith(".md");
    }

    public IndexService(SearchService searchService)
    {
        _searchService = searchService;
        _workspaceRoot = Environment.CurrentDirectory;
    }

    /// <summary>
    /// Get index status.
    /// </summary>
    public IndexStatus GetStatus()
    {
        return new IndexStatus
        {
            SymbolCount = _searchService.SymbolCount(),
            DbPath = _searchService.DbPath,
            WorkspaceRoot = _workspaceRoot,
            IsHealthy = _searchService.HealthCheck()
        };
    }

    /// <summary>
    /// Refresh index - update only stale files.
    /// </summary>
    public async Task<IndexResult> RefreshAsync(string? path = null, CancellationToken ct = default)
    {
        var targetPath = path ?? _workspaceRoot;
        var files = GetIndexableFiles(targetPath);

        var indexed = 0;
        var skipped = 0;
        var errors = new List<string>();

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var hash = await ComputeFileHashAsync(file);
                var relativePath = Path.GetRelativePath(_workspaceRoot, file);

                if (_fileHashes.TryGetValue(relativePath, out var existingHash) && existingHash == hash)
                {
                    skipped++;
                    continue;
                }

                await IndexFileAsync(file, relativePath);
                _fileHashes[relativePath] = hash;
                indexed++;
            }
            catch (Exception ex)
            {
                errors.Add($"{file}: {ex.Message}");
            }
        }

        // Ensure FTS index exists
        if (indexed > 0)
        {
            _searchService.CreateFtsIndex();
        }

        return new IndexResult
        {
            FilesIndexed = indexed,
            FilesSkipped = skipped,
            Errors = errors
        };
    }

    /// <summary>
    /// Full reindex - rebuild from scratch.
    /// </summary>
    public async Task<IndexResult> FullIndexAsync(string? path = null, CancellationToken ct = default)
    {
        _fileHashes.Clear();
        return await RefreshAsync(path, ct);
    }

    private IEnumerable<string> GetIndexableFiles(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return Enumerable.Empty<string>();
        }

        return Directory.EnumerateFiles(rootPath, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true
        })
        .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
        .Where(f => !IsIgnoredPath(f));
    }

    private bool IsIgnoredPath(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p is ".git" or "node_modules" or "target" or "bin" or "obj"
                           or ".codesearch" or "__pycache__" or ".venv" or "venv");
    }

    private async Task<string> ComputeFileHashAsync(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }

    private async Task IndexFileAsync(string absolutePath, string relativePath)
    {
        var content = await File.ReadAllTextAsync(absolutePath);
        var normalizedPath = relativePath.Replace('\\', '/');

        // Special handling for memory files - strip frontmatter, prepend tags
        if (IsMemoryFile(relativePath))
        {
            IndexMemoryFile(content, normalizedPath);
            return;
        }

        // Try extraction for supported languages
        var language = CodesearchFfiMethods.DetectLanguage(normalizedPath);
        if (language != null)
        {
            try
            {
                var results = CodesearchFfiMethods.ExtractFile(content, normalizedPath, _workspaceRoot);
                IndexExtractionResults(results, content);
                return;
            }
            catch
            {
                // Fall through to file-level indexing on extraction failure
            }
        }

        // Fallback: file-level indexing for unsupported languages or extraction failures
        IndexFileLevel(content, normalizedPath);
    }

    private void IndexMemoryFile(string content, string normalizedPath)
    {
        string embedContent;
        string name;
        string kind;

        try
        {
            var (metadata, body) = FrontmatterParser.Parse(content);
            var tagPrefix = metadata.Tags.Count > 0 ? string.Join(" ", metadata.Tags) + " " : "";
            embedContent = tagPrefix + body;
            name = Path.GetFileName(normalizedPath);
            kind = metadata.Type.ToString().ToLowerInvariant();
        }
        catch
        {
            // Fallback if parsing fails
            embedContent = content;
            name = Path.GetFileName(normalizedPath);
            kind = "memory";
        }

        // Truncate content for embedding
        if (embedContent.Length > 4096)
        {
            embedContent = embedContent[..4096];
        }

        var symbol = new SymbolInput(
            id: $"file_{Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedPath)))[..16]}",
            name: name,
            kind: kind,
            language: "md",
            filePath: normalizedPath,
            signature: null,
            docComment: null,
            startLine: 1,
            endLine: content.Split('\n').Length,
            content: embedContent
        );

        var vector = Enumerable.Repeat(0.0f, 768).ToList();
        _searchService.AddSymbols(new List<SymbolInput> { symbol }, new List<List<float>> { vector });
    }

    private void IndexExtractionResults(ExtractionResults results, string content)
    {
        // Convert extracted symbols to SymbolInput
        if (results.symbols.Count > 0)
        {
            var symbolInputs = new List<SymbolInput>();
            var vectors = new List<List<float>>();

            foreach (var sym in results.symbols)
            {
                // Extract symbol content from source
                var symbolContent = ExtractSymbolContent(content, (int)sym.startLine, (int)sym.endLine);
                if (symbolContent.Length > 4096)
                {
                    symbolContent = symbolContent[..4096];
                }

                var input = new SymbolInput(
                    id: sym.id,
                    name: sym.name,
                    kind: sym.kind,
                    language: sym.language,
                    filePath: sym.filePath,
                    signature: sym.signature,
                    docComment: sym.docComment,
                    startLine: (int)sym.startLine,
                    endLine: (int)sym.endLine,
                    content: symbolContent
                );

                symbolInputs.Add(input);
                vectors.Add(Enumerable.Repeat(0.0f, 768).ToList());
            }

            _searchService.AddSymbols(symbolInputs, vectors);
        }

        // Convert and add identifiers
        if (results.identifiers.Count > 0)
        {
            var identifierInputs = results.identifiers.Select(id => new IdentifierInput(
                name: id.name,
                kind: id.kind,
                filePath: id.filePath,
                lineNumber: id.lineNumber,
                column: id.column,
                sourceSymbolId: id.sourceSymbolId,
                targetSymbolId: id.targetSymbolId
            )).ToList();

            _searchService.AddIdentifiers(identifierInputs);
        }

        // Convert and add relationships
        if (results.relationships.Count > 0)
        {
            var relationshipInputs = results.relationships.Select(rel => new RelationshipInput(
                fromSymbolId: rel.fromSymbolId,
                toSymbolId: rel.toSymbolId,
                kind: rel.kind,
                filePath: rel.filePath,
                lineNumber: rel.lineNumber,
                confidence: rel.confidence
            )).ToList();

            _searchService.AddRelationships(relationshipInputs);
        }
    }

    private static string ExtractSymbolContent(string content, int startLine, int endLine)
    {
        var lines = content.Split('\n');
        var start = Math.Max(0, startLine - 1);
        var end = Math.Min(lines.Length, endLine);
        return string.Join('\n', lines.Skip(start).Take(end - start));
    }

    private void IndexFileLevel(string content, string normalizedPath)
    {
        var extension = Path.GetExtension(normalizedPath).TrimStart('.');
        var embedContent = content;

        // Truncate content for embedding
        if (embedContent.Length > 4096)
        {
            embedContent = embedContent[..4096];
        }

        var symbol = new SymbolInput(
            id: $"file_{Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedPath)))[..16]}",
            name: Path.GetFileName(normalizedPath),
            kind: "file",
            language: extension,
            filePath: normalizedPath,
            signature: null,
            docComment: null,
            startLine: 1,
            endLine: content.Split('\n').Length,
            content: embedContent
        );

        var vector = Enumerable.Repeat(0.0f, 768).ToList();
        _searchService.AddSymbols(new List<SymbolInput> { symbol }, new List<List<float>> { vector });
    }
}

internal record IndexStatus
{
    public required ulong SymbolCount { get; init; }
    public required string DbPath { get; init; }
    public required string WorkspaceRoot { get; init; }
    public required bool IsHealthy { get; init; }
}

internal record IndexResult
{
    public required int FilesIndexed { get; init; }
    public required int FilesSkipped { get; init; }
    public required List<string> Errors { get; init; }
}
