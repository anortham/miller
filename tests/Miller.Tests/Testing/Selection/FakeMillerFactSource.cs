using Miller.Core.References;
using Miller.Indexing.Testing;
using Miller.Testing;

namespace Miller.Tests.Testing.Selection;

internal sealed class FakeMillerFactSource : IMillerFactSource
{
    public CtIndexCursor Current { get; init; } = new("gen-1", 1);

    public List<CtSymbolFact> Symbols { get; } = [];

    public List<CtReferenceFact> References { get; } = [];

    public List<CtReferenceFact> Identifiers { get; } = [];

    public List<CtImpactedSymbol> Impacted { get; } = [];

    public List<CtImpactedSymbol> Tests { get; } = [];

    /// <summary>When set, <see cref="Impact"/> reports a truncated read (an incomplete blast radius).</summary>
    public bool ImpactTruncatedByLimit { get; set; }

    public IReadOnlyList<CtSymbolFact> SymbolsForChangedFiles(IReadOnlyList<string> changedPaths)
    {
        HashSet<string> paths = changedPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Normalize)
            .ToHashSet(PathComparer);
        return Symbols.Where(symbol => paths.Contains(Normalize(symbol.FilePath))).ToArray();
    }

    public IReadOnlyList<CtReferenceFact> ReferencesTo(IReadOnlyList<string> symbolIds)
    {
        HashSet<string> ids = symbolIds.ToHashSet(StringComparer.Ordinal);
        return References.Where(row => ids.Contains(row.TargetSymbolId)).ToArray();
    }

    public IReadOnlyList<CtReferenceFact> IdentifierEvidenceTo(IReadOnlyList<string> symbolIds)
    {
        HashSet<string> ids = symbolIds.ToHashSet(StringComparer.Ordinal);
        return Identifiers.Where(row => ids.Contains(row.TargetSymbolId)).ToArray();
    }

    public CtImpactResult Impact(IReadOnlyList<string> seedSymbolIds, int maxDepth = 2, int limit = 100)
    {
        if (seedSymbolIds.Count == 0)
            return new CtImpactResult([], [], 0, false, false);

        return new CtImpactResult(
            Impacted, Tests, Impacted.Count + Tests.Count, false, ImpactTruncatedByLimit);
    }

    public static CtSymbolFact Symbol(
        string id,
        string name,
        string path,
        bool isTest = false,
        string language = "csharp",
        string kind = "function") =>
        new(id, name, kind, language, path, "blake3:" + id, 1, 2, null, isTest, null);

    public static CtImpactedSymbol Hit(
        string id,
        string name,
        string path,
        bool isTest,
        string? edgeKind = null,
        string? edgeSource = null,
        string kind = "function") =>
        new(id, name, kind, path, isTest, 1, edgeKind, edgeSource);

    public static CtReferenceFact Identifier(string sourceId, string targetId, string path) =>
        new(
            sourceId,
            targetId,
            "call",
            1.0,
            "identifier_resolution",
            path,
            1,
            ReferenceResolutionStatus.Exact);

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

internal sealed class FakeCtFactSource : ICtFactSource
{
    private readonly FakeMillerFactSource _inner = new();

    public FakeMillerFactSource Inner => _inner;

    public CtIndexCursor Current => _inner.Current;

    public IReadOnlyList<CtSymbolFact> SymbolsForChangedFiles(IReadOnlyList<string> changedPaths) =>
        _inner.SymbolsForChangedFiles(changedPaths);

    public IReadOnlyList<CtReferenceFact> ReferencesTo(IReadOnlyList<string> symbolIds) =>
        _inner.ReferencesTo(symbolIds);

    public IReadOnlyList<CtReferenceFact> IdentifierEvidenceTo(IReadOnlyList<string> symbolIds) =>
        _inner.IdentifierEvidenceTo(symbolIds);

    public CtImpactResult Impact(IReadOnlyList<string> seedSymbolIds, int maxDepth = 2, int limit = 100) =>
        _inner.Impact(seedSymbolIds, maxDepth, limit);
}

internal sealed class FakeCoverageFactSource : ICtCoverageFactSource
{
    public List<CtCoverageSpanFact> Spans { get; } = [];

    public IReadOnlyList<CtCoverageSpanFact> SpansCovering(
        string workspaceId,
        IReadOnlyList<string> symbolIds,
        IReadOnlyList<string> filePaths)
    {
        HashSet<string> ids = symbolIds.ToHashSet(StringComparer.Ordinal);
        HashSet<string> paths = filePaths
            .Select(path => path.Replace('\\', '/').Trim('/'))
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        return Spans
            .Where(span =>
                (span.SymbolId is not null && ids.Contains(span.SymbolId))
                || paths.Contains(span.Path.Replace('\\', '/').Trim('/')))
            .ToArray();
    }
}
