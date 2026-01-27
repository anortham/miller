using System.Security.Cryptography;
using uniffi.codesearch_ffi;

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
        var extension = Path.GetExtension(relativePath).TrimStart('.');

        // Create symbol with placeholder vector (real embeddings would come from ONNX model)
        var symbol = new SymbolInput(
            id: $"file_{Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(relativePath)))[..16]}",
            name: Path.GetFileName(relativePath),
            kind: "file",
            language: extension,
            filePath: relativePath.Replace('\\', '/'),
            signature: null,
            docComment: null,
            startLine: 1,
            endLine: content.Split('\n').Length,
            content: content.Length > 4096 ? content[..4096] : content
        );

        // Placeholder vector (768 zeros)
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
