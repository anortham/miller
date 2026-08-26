using System.Text.RegularExpressions;
using Miller.Indexing;

namespace Miller.Server.Resolution;

/// <summary>
/// Resolves a single <c>target</c>/<c>query</c> string to a file, a symbol, candidates, or not-found
/// (miller-toolbox.md L47-56). The agent types a NAME or a PATH (never an opaque MD5 id by hand), so the
/// shapes are distinguished by cheap structural rules; ids appear only when chained from a prior call.
///
/// <b>Cross-language file detection (M2 §3 decision-4):</b> "is this a file?" is DERIVED from the indexed
/// data — an exact/unique indexed path, or an extension julie actually emitted for this repo
/// (<see cref="MillerRepositoryIndex.KnownExtensions"/>) — NOT a hardcoded code-extension whitelist. That set
/// is precisely the languages julie supports here, all-language and self-updating, honouring the principle
/// that a feature scopes to every capable language rather than a hand-picked few.
///
/// Overrides: <c>scope</c> constrains a name to a file before disambiguating; <c>as</c> forces the kind.
/// Pure over the in-memory index (no I/O) — a process-wide singleton.
/// </summary>
public sealed partial class SmartTargetResolver
{
    // Live resolvers read the index PER CALL through a holder-backed delegate (M3 step 10), so a freshness Swap is
    // observed on the next Resolve without reconstructing the resolver. Projection-specific callers use a fixed
    // lookup delegate over the symbol projection.
    private readonly Func<ISymbolLookupIndex> _index;

    private ISymbolLookupIndex Index => _index();

    /// <summary>Resolve against whatever index <paramref name="holder"/> currently holds (live, per call).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="holder"/> is null.</exception>
    public SmartTargetResolver(IndexHolder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        _index = () => holder.Current;
    }

    /// <summary>Resolve against a fixed repository index. For tests / single-index callers.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="index"/> is null.</exception>
    public SmartTargetResolver(MillerRepositoryIndex index)
        : this((ISymbolLookupIndex)(index ?? throw new ArgumentNullException(nameof(index))))
    {
    }

    /// <summary>Resolve against a fixed lookup projection. For projection-specific read paths.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="index"/> is null.</exception>
    public SmartTargetResolver(ISymbolLookupIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        _index = () => index;
    }

    /// <summary>
    /// Resolve <paramref name="target"/>. <paramref name="scope"/> (a file path) constrains a name lookup to
    /// that file before disambiguating; <paramref name="asKind"/> forces FILE or SYMBOL interpretation.
    ///
    /// <para>A <see cref="TargetResolution.File"/> asserts the SHAPE of the target, never that the path is
    /// indexed: rules 1 and 4 both answer File for a path this index has never seen, so a caller that renders an
    /// empty result must decide "indexed but empty" versus "not indexed" from the artifact's <c>files</c> table
    /// itself. Do NOT settle that here with a filesystem existence check — a path can exist on disk and still be
    /// excluded from the index by <c>.julieignore</c>, which is exactly the case a disk probe answers wrongly.</para>
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="target"/> is null/empty/whitespace.</exception>
    public TargetResolution Resolve(string target, string? scope = null, TargetKind asKind = TargetKind.Auto)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        switch (asKind)
        {
            case TargetKind.File:
                // Forced file: canonicalize a bare basename to its indexed path when unambiguous.
                return new TargetResolution.File(Index.ResolveIndexedFilePath(target) ?? target);
            case TargetKind.Symbol:
                return ResolveAsSymbol(target, scope);
            default:
                break; // Auto — infer below.
        }

        // A '::' target with a path-shaped head ("src/Foo.cs::Method") is a file-scoped member lookup, tried
        // before rule 1 turns the whole string into a path. Safe ahead of the '::' id shape (rule 2): no
        // indexed symbol name carries both '::' and a separator (Rust '::' paths and CSS pseudo-elements are
        // slash-free), and a slash-free head must carry an indexed extension to qualify.
        if (TryParseFileScopedMember(target, out string fileScope, out string memberTarget))
        {
            TargetResolution scoped = ResolveByName(memberTarget, fileScope);
            if (scoped is not TargetResolution.NotFound)
                return scoped;
        }

        // Rule 1: explicit path separators → file (language-agnostic).
        if (target.Contains('/') || target.Contains('\\'))
            return new TargetResolution.File(Index.ResolveIndexedFilePath(target) ?? target);

        if (TryNormalizeColonQualifiedMember(target, out string qualifiedTarget))
        {
            TargetResolution qualified = ResolveByName(qualifiedTarget, scope);
            if (qualified is not TargetResolution.NotFound)
                return qualified;
        }

        // Rule 2: id shape (32-hex MD5 | contains '::' | starts 'file_') → use directly, no name search.
        if (LooksLikeSymbolId(target))
        {
            var byId = Index.FindBySymbolId(target);
            return byId is not null
                ? new TargetResolution.Symbol(byId)
                : new TargetResolution.NotFound(target);
        }

        // Rule 3: a bare string that names an indexed file (exact, or unique basename) → that file.
        string? indexedPath = Index.ResolveIndexedFilePath(target);
        if (indexedPath is not null)
            return new TargetResolution.File(indexedPath);

        // Rule 4: an extension julie actually indexed → a (possibly not-yet-indexed) file, not a name.
        if (HasKnownExtension(target))
            return new TargetResolution.File(target);

        // Rule 5: otherwise a symbol NAME.
        return ResolveByName(target, scope);
    }

    private TargetResolution ResolveAsSymbol(string target, string? scope)
    {
        if (TryNormalizeColonQualifiedMember(target, out string qualifiedTarget))
        {
            TargetResolution qualified = ResolveByName(qualifiedTarget, scope);
            if (qualified is not TargetResolution.NotFound)
                return qualified;
        }

        if (LooksLikeSymbolId(target))
        {
            var byId = Index.FindBySymbolId(target);
            return byId is not null
                ? new TargetResolution.Symbol(byId)
                : new TargetResolution.NotFound(target);
        }
        return ResolveByName(target, scope);
    }

    private bool TryParseFileScopedMember(string target, out string fileScope, out string memberTarget)
    {
        fileScope = string.Empty;
        memberTarget = string.Empty;
        if (!target.Contains("::", StringComparison.Ordinal))
            return false;

        string[] parts = target.Split("::", StringSplitOptions.None);
        if (parts.Length < 2 || parts.Any(string.IsNullOrWhiteSpace))
            return false;

        string head = parts[0];
        if (!head.Contains('/') && !head.Contains('\\') && !HasKnownExtension(head))
            return false;

        fileScope = Index.ResolveIndexedFilePath(head) ?? head;
        memberTarget = string.Join('.', parts[1..]);
        return true;
    }

    private static bool TryNormalizeColonQualifiedMember(string target, out string normalized)
    {
        normalized = target;
        if (!target.Contains("::", StringComparison.Ordinal))
            return false;

        string[] parts = target.Split("::", StringSplitOptions.None);
        if (parts.Length < 2 || parts.Any(string.IsNullOrWhiteSpace))
            return false;

        normalized = string.Join('.', parts);
        return true;
    }

    private TargetResolution ResolveByName(string name, string? scope)
    {
        IReadOnlyList<IndexedSymbol> byName = Index.FindByName(name);
        IReadOnlyList<IndexedSymbol> matches = ScopeMatches(byName, scope);

        if (matches.Count == 0)
            matches = ResolveQualifiedMember(name, scope);

        if (matches.Count == 1)
            return new TargetResolution.Symbol(matches[0]);
        if (string.IsNullOrWhiteSpace(scope) && matches.Count > 1 && PreferredDefinitionCandidate(matches) is { } symbol)
            return new TargetResolution.Symbol(symbol);
        if (matches.Count > 1)
            return new TargetResolution.Candidates(RankCandidatesForDisplay(matches));

        // A wrong scope (wrong file, absolute path, separator the normalization could not bridge) must never
        // mask a resolvable name as a bare not-found: surface the out-of-scope matches as suggestions so the
        // agent self-corrects in one turn (Windows dogfood, 2026-06).
        if (!string.IsNullOrWhiteSpace(scope))
        {
            IReadOnlyList<IndexedSymbol> unscoped =
                byName.Count > 0 ? byName : ResolveQualifiedMember(name, scope: null);
            if (unscoped.Count > 0)
                return new TargetResolution.NotFound(name, Cap(unscoped), scope);
        }

        return new TargetResolution.NotFound(name, NearMisses(name));
    }

    // Suggestion budget: enough to self-correct in one turn, small enough to stay a one-line note.
    private const int MaxSuggestions = 3;

    private static IReadOnlyList<IndexedSymbol> Cap(IReadOnlyList<IndexedSymbol> matches) =>
        matches.Count <= MaxSuggestions ? matches : matches.Take(MaxSuggestions).ToList();

    private static IndexedSymbol? PreferredDefinitionCandidate(IReadOnlyList<IndexedSymbol> matches)
    {
        var definitions = matches
            .Where(s => !IsNameLookupNoise(s.Kind))
            .ToArray();

        if (definitions.Length == 1)
            return definitions[0];
        if (definitions.Length == 0)
            return null;

        var scored = definitions
            .Select(s => (Symbol: s, Score: DefinitionPreferenceScore(s)))
            .OrderByDescending(s => s.Score)
            .ToArray();
        int topScore = scored[0].Score;
        return scored.Count(s => s.Score == topScore) == 1 ? scored[0].Symbol : null;
    }

    private static IReadOnlyList<IndexedSymbol> RankCandidatesForDisplay(IReadOnlyList<IndexedSymbol> matches) =>
        matches
            .Select((symbol, index) => (Symbol: symbol, Score: DefinitionPreferenceScore(symbol), Index: index))
            .OrderByDescending(row => row.Score)
            .ThenBy(row => row.Index)
            .Select(row => row.Symbol)
            .ToList();

    private static int DefinitionPreferenceScore(IndexedSymbol symbol)
    {
        int score = 0;
        if (IsNameLookupNoise(symbol.Kind))
            score -= 1000;
        score += IsTestPath.IsTest(symbol) ? -500 : 500;

        string[] segments = PathSegments(symbol.FilePath);
        if (segments.Any(IsPreferredSourceSegment))
            score += 100;
        if (segments.Any(IsAuxiliaryCodeSegment))
            score -= 300;
        score += VersionSegmentScore(segments);
        return score;
    }

    private static bool IsNameLookupNoise(string kind) =>
        string.Equals(kind, "constructor", StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, "import", StringComparison.OrdinalIgnoreCase);

    private static string[] PathSegments(string path) =>
        path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

    private static bool IsPreferredSourceSegment(string segment) =>
        segment.Equals("src", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("lib", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("app", StringComparison.OrdinalIgnoreCase);

    private static bool IsAuxiliaryCodeSegment(string segment) =>
        segment.Equals("example", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("examples", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("sample", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("samples", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("fixture", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("fixtures", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("bench", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("benches", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("benchmark", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("benchmarks", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("coverage", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("generated", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("dist", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("build", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("vendor", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("vendors", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("third_party", StringComparison.OrdinalIgnoreCase);

    private static int VersionSegmentScore(IEnumerable<string> segments)
    {
        int max = 0;
        foreach (string segment in segments)
        {
            if (segment.Length < 2 || segment[0] is not ('v' or 'V'))
                continue;
            if (int.TryParse(segment[1..], out int version) && version > max)
                max = version;
        }
        return Math.Min(max, 50);
    }

    private IReadOnlyList<IndexedSymbol> NearMisses(string name) =>
        SymbolSuggestionEngine.Suggest(Index, name, MaxSuggestions);

    private IReadOnlyList<IndexedSymbol> ResolveQualifiedMember(string name, string? scope)
    {
        int lastDot = name.LastIndexOf('.');
        if (lastDot <= 0 || lastDot >= name.Length - 1)
            return [];

        string parentName = name[..lastDot];
        string memberName = name[(lastDot + 1)..];
        string[] expectedAncestors = parentName.Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var members = ScopeMatches(Index.FindByName(memberName), scope);
        return members
            .Where(s => s.ParentId is { } parentId
                        && AncestorPathMatches(parentId, expectedAncestors))
            .ToList();
    }

    private bool AncestorPathMatches(string parentId, IReadOnlyList<string> expectedAncestors)
    {
        string currentId = parentId;
        for (int index = expectedAncestors.Count - 1; index >= 0; index--)
        {
            if (Index.FindBySymbolId(currentId) is not { } ancestor
                || !string.Equals(ancestor.Name, expectedAncestors[index], StringComparison.Ordinal))
                return false;

            if (index > 0)
            {
                if (ancestor.ParentId is not { } ancestorParentId)
                    return true;

                currentId = ancestorParentId;
            }
        }

        return expectedAncestors.Count > 0;
    }

    private static IReadOnlyList<IndexedSymbol> ScopeMatches(IReadOnlyList<IndexedSymbol> matches, string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return matches;

        // Constrain to the scoped file before counting. Separators are normalized ('\' → '/', the repo's
        // path-normalization convention — see ToolSearchFilters.NormalizePath) and casing is ignored so a
        // Windows-shaped scope still narrows instead of silently zeroing out an otherwise-resolvable name.
        // Worst case for a case-only collision on a case-sensitive filesystem is an extra candidate, never a
        // wrong silent pick.
        string normalizedScope = NormalizeScopePath(scope);
        return matches
            .Where(s => string.Equals(NormalizeScopePath(s.FilePath), normalizedScope, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static string NormalizeScopePath(string path) => path.Replace('\\', '/').Trim();

    // A non-empty extension (beyond the dot) that appears among the indexed file paths. Derived, not hardcoded.
    private bool HasKnownExtension(string s)
    {
        string ext = Path.GetExtension(s);
        return ext.Length > 1 && Index.KnownExtensions.Contains(ext);
    }

    private static bool LooksLikeSymbolId(string s)
    {
        if (s.StartsWith("file_", StringComparison.Ordinal))
            return true;
        if (s.Contains("::", StringComparison.Ordinal))
            return true;
        return Md5HexPattern().IsMatch(s);
    }

    // julie symbol ids are 32 lowercase hex chars (MD5). Anchored so a longer/shorter string is not an id.
    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex Md5HexPattern();
}
