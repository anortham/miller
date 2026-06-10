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

        // Rule 1: explicit path separators → file (language-agnostic).
        if (target.Contains('/') || target.Contains('\\'))
            return new TargetResolution.File(Index.ResolveIndexedFilePath(target) ?? target);

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
        // as=symbol: an id shape is still used directly; anything else is a NAME lookup (bypassing the
        // file-path heuristic so a path-shaped string is treated as a name).
        if (LooksLikeSymbolId(target))
        {
            var byId = Index.FindBySymbolId(target);
            return byId is not null
                ? new TargetResolution.Symbol(byId)
                : new TargetResolution.NotFound(target);
        }
        return ResolveByName(target, scope);
    }

    private TargetResolution ResolveByName(string name, string? scope)
    {
        IReadOnlyList<IndexedSymbol> byName = Index.FindByName(name);
        IReadOnlyList<IndexedSymbol> matches = ScopeMatches(byName, scope);

        if (matches.Count == 0)
            matches = ResolveQualifiedMember(name, scope);

        if (matches.Count == 1)
            return new TargetResolution.Symbol(matches[0]);
        if (matches.Count > 1)
            return new TargetResolution.Candidates(matches);

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

    /// <summary>
    /// Up to <see cref="MaxSuggestions"/> near-miss symbols for a truly-unresolvable name. Recall comes from
    /// the search index (word/component arms in-memory; + the collapsed-trigram arm on the FTS5 sidecar);
    /// only genuinely CLOSE names survive — a case-insensitive exact match first, then names containing the
    /// target or its last dot segment. A BM25 hit that merely shares a token is noise here, not a suggestion.
    /// </summary>
    private IReadOnlyList<IndexedSymbol> NearMisses(string name)
    {
        var hits = Index.Search(name, MaxSuggestions * 4);
        if (hits.Count == 0)
            return [];

        string tail = name[(name.LastIndexOf('.') + 1)..]; // == name when there is no dot
        return hits
            .Select(h => Index.Resolve(h.Document.DocId))
            .Where(s => IsCloseName(s.Name, name, tail))
            .DistinctBy(s => (s.Name, s.FilePath))
            .OrderByDescending(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            .Take(MaxSuggestions)
            .ToList();

        static bool IsCloseName(string candidate, string target, string tail) =>
            candidate.Contains(target, StringComparison.OrdinalIgnoreCase)
            || (tail.Length > 0 && candidate.Contains(tail, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<IndexedSymbol> ResolveQualifiedMember(string name, string? scope)
    {
        int lastDot = name.LastIndexOf('.');
        if (lastDot <= 0 || lastDot >= name.Length - 1)
            return [];

        string parentName = name[..lastDot];
        string memberName = name[(lastDot + 1)..];
        string expectedParent = parentName.Contains('.')
            ? parentName[(parentName.LastIndexOf('.') + 1)..]
            : parentName;

        var members = ScopeMatches(Index.FindByName(memberName), scope);
        return members
            .Where(s => s.ParentId is { } parentId
                        && Index.FindBySymbolId(parentId) is { } parent
                        && string.Equals(parent.Name, expectedParent, StringComparison.Ordinal))
            .ToList();
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
