using System.Security.Cryptography;
using Miller.Indexing.Testing;
using Miller.Testing.Parsing;

namespace Miller.Testing;

public sealed record CoverageArtifactImportRequest(
    string WorkspaceId,
    string WorkspaceRoot,
    string ArtifactPath,
    string IndexIdentity,
    long Revision,
    string Parser = "auto",
    string? ArtifactRoot = null,
    string? RunId = null,
    string? ProjectPath = null);

public sealed record CoverageArtifactImportReport(
    string Kind,
    string ArtifactId,
    string ArtifactPath,
    string Parser,
    string State,
    IReadOnlyDictionary<string, int> Counts);

public static class CoverageArtifactImporter
{
    private const string Kind = "coverage";

    public static CoverageArtifactImportReport Import(
        ContinuousTestStore store,
        CoverageArtifactImportRequest request,
        IMillerFactSource? facts = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        string root = Path.GetFullPath(request.ArtifactRoot ?? request.WorkspaceRoot);
        string artifactPath = ResolveInsideRoot(root, request.ArtifactPath);
        string relativePath = StoredRelativePath(root, artifactPath);
        string parser = ParserName(request.Parser, artifactPath);
        string artifactHash = Sha256(artifactPath);
        string artifactId = CtStableIds.StableId("run_artifact", request.WorkspaceId, Kind, parser, artifactHash);
        ParsedCoverageArtifactRun parsed = CoverageArtifactParser.Parse(artifactPath, parser);
        var counts = new Dictionary<string, int>
        {
            ["artifacts"] = 1,
            ["coverage_files"] = parsed.Files.Count,
            ["coverage_spans"] = parsed.Files.Sum(file => file.LineHits.Count),
        };

        var payload = new Dictionary<string, object?>
        {
            ["parser"] = parser,
            ["sha256"] = artifactHash,
            ["counts"] = counts,
            ["diagnostics"] = Array.Empty<object>(),
        };
        if (!string.IsNullOrWhiteSpace(request.RunId))
            payload["run_id"] = request.RunId;
        if (!string.IsNullOrWhiteSpace(request.ProjectPath))
            payload["project_path"] = Path.GetFullPath(request.ProjectPath);

        store.PutRunArtifact(new ContinuousTestRunArtifact(
            Id: artifactId,
            WorkspaceId: request.WorkspaceId,
            Kind: Kind,
            Path: relativePath,
            Payload: payload));

        foreach (ParsedCoverageArtifactFile parsedFile in parsed.Files)
        {
            PersistCoverageFile(store, request, facts, artifactId, artifactHash, parser, parsedFile);
        }

        return new CoverageArtifactImportReport(
            Kind: Kind,
            ArtifactId: artifactId,
            ArtifactPath: relativePath,
            Parser: parser,
            State: "imported",
            Counts: counts);
    }

    private static void PersistCoverageFile(
        ContinuousTestStore store,
        CoverageArtifactImportRequest request,
        IMillerFactSource? facts,
        string artifactId,
        string artifactHash,
        string parser,
        ParsedCoverageArtifactFile parsedFile)
    {
        IReadOnlyList<CtSymbolFact> symbols = facts?.SymbolsForChangedFiles([parsedFile.SourcePath]) ?? [];
        bool mapped = symbols.Count > 0;
        string sourceHash = symbols.Select(row => row.ContentHash).FirstOrDefault(static value => !string.IsNullOrEmpty(value))
            ?? artifactHash;
        string coverageFileId = CtStableIds.StableId("coverage_file", request.WorkspaceId, artifactId, parsedFile.SourcePath);
        ContinuousTestCase? testCase = TestCaseForCoverageName(store, request.WorkspaceId, parsedFile.TestName);

        store.PutCoverageFile(new CoverageFile(
            Id: coverageFileId,
            WorkspaceId: request.WorkspaceId,
            IndexIdentity: request.IndexIdentity,
            Revision: request.Revision,
            ArtifactId: artifactId,
            Format: parsedFile.Format,
            Path: parsedFile.SourcePath,
            Parser: parser,
            SourceHash: sourceHash,
            GeneratedAt: DateTimeOffset.UtcNow,
            Metadata: new Dictionary<string, object?> { ["mapped"] = mapped }));

        foreach (ParsedCoverageLineHit hit in parsedFile.LineHits)
        {
            CtSymbolFact? symbol = SymbolForLine(symbols, hit.LineNumber);
            var metadata = new Dictionary<string, object?> { ["artifact_id"] = artifactId };
            if (testCase is not null)
                metadata["test_case_id"] = testCase.Id;

            store.PutCoverageSpan(new CoverageSpan(
                Id: CtStableIds.StableId("coverage_span", coverageFileId, hit.LineNumber, testCase?.Id ?? "aggregate"),
                WorkspaceId: request.WorkspaceId,
                IndexIdentity: request.IndexIdentity,
                Revision: request.Revision,
                CoverageFileId: coverageFileId,
                FilePath: parsedFile.SourcePath,
                ContentHash: mapped ? sourceHash : null,
                SymbolName: symbol?.SymbolId,
                SymbolPath: symbol?.FilePath,
                StartLine: hit.LineNumber,
                EndLine: hit.LineNumber,
                Hits: hit.Hits,
                Metadata: metadata));
        }
    }

    private static ContinuousTestCase? TestCaseForCoverageName(
        ContinuousTestStore store,
        string workspaceId,
        string? testName)
    {
        if (string.IsNullOrWhiteSpace(testName))
            return null;

        foreach (ContinuousTestCase testCase in store.ListTestCases(workspaceId))
        {
            if (string.Equals(testCase.Selector, testName, StringComparison.Ordinal)
                || string.Equals(testCase.Name, testName, StringComparison.Ordinal)
                || string.Equals(testCase.QualifiedName, testName, StringComparison.Ordinal)
                || testCase.Selector.EndsWith($"::{testName}", StringComparison.Ordinal))
            {
                return testCase;
            }
        }

        return null;
    }

    private static CtSymbolFact? SymbolForLine(IReadOnlyList<CtSymbolFact> symbols, int lineNumber)
    {
        return symbols
            .Where(symbol => symbol.StartLine <= lineNumber && lineNumber <= symbol.EndLine)
            .OrderBy(symbol => symbol.EndLine - symbol.StartLine)
            .ThenBy(symbol => symbol.Name, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.SymbolId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static void ValidateRequest(CoverageArtifactImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.WorkspaceId))
            throw new ArgumentException("must not be empty", nameof(request.WorkspaceId));
        if (string.IsNullOrWhiteSpace(request.WorkspaceRoot))
            throw new ArgumentException("must not be empty", nameof(request.WorkspaceRoot));
        if (request.ArtifactRoot is not null && string.IsNullOrWhiteSpace(request.ArtifactRoot))
            throw new ArgumentException("must not be empty", nameof(request.ArtifactRoot));
        if (string.IsNullOrWhiteSpace(request.ArtifactPath))
            throw new ArgumentException("must not be empty", nameof(request.ArtifactPath));
        if (string.IsNullOrWhiteSpace(request.IndexIdentity))
            throw new ArgumentException("must not be empty", nameof(request.IndexIdentity));
        if (request.Revision < 0)
            throw new ArgumentOutOfRangeException(nameof(request.Revision), "must not be negative");
    }

    private static string ParserName(string parser, string artifactPath)
    {
        if (!string.IsNullOrWhiteSpace(parser) && !string.Equals(parser, "auto", StringComparison.OrdinalIgnoreCase))
            return parser;

        return string.Equals(Path.GetExtension(artifactPath), ".info", StringComparison.OrdinalIgnoreCase)
            ? "lcov"
            : "cobertura";
    }

    private static string ResolveInsideRoot(string root, string artifactPath)
    {
        string candidate = Path.IsPathRooted(artifactPath)
            ? Path.GetFullPath(artifactPath)
            : Path.GetFullPath(Path.Combine(root, artifactPath));
        string relative = Path.GetRelativePath(root, candidate);
        if (relative == "."
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", PathComparison)
            || Path.IsPathRooted(relative))
            throw new ArgumentException("artifact path must live inside the workspace root", nameof(artifactPath));
        return candidate;
    }

    private static string StoredRelativePath(string root, string artifactPath)
    {
        string relative = Path.GetRelativePath(root, artifactPath);
        return relative
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static string Sha256(string path)
    {
        byte[] hash = SHA256.HashData(File.ReadAllBytes(path));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
