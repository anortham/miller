using Miller.Core.Contracts;

namespace Miller.Core.Resolver;

/// <summary>The outcome status of resolving a type name to a symbol.</summary>
public enum ResolutionStatus
{
    /// <summary>No candidate matched the name — the edge is dropped (no symbol to point at).</summary>
    Unresolved,

    /// <summary>Exactly one candidate matched (directly, or after a tie-break) — the edge resolves.</summary>
    Resolved,

    /// <summary>&gt;1 candidate matched with no usable tie-break — the caller lowers/drops; the edge is NEVER High.</summary>
    Ambiguous,
}

/// <summary>
/// The result of resolving a type name. Carries the <see cref="Status"/>, the chosen <see cref="SymbolId"/> (only when
/// <see cref="ResolutionStatus.Resolved"/>; null otherwise — an ambiguous resolution NEVER picks one), and the
/// <see cref="MatchCount"/> of name candidates considered (it backs the scorer's <c>NameResolution</c> signal so the
/// ambiguous-name-never-High rule is decidable from the candidate payload alone).
/// </summary>
/// <param name="Status">The resolution outcome.</param>
/// <param name="SymbolId">The resolved symbol id, or null when unresolved/ambiguous.</param>
/// <param name="MatchCount">How many candidates matched the leaf name (0 unresolved, 1 resolved, ≥2 before tie-break).</param>
public sealed record NameResolution(ResolutionStatus Status, string? SymbolId, int MatchCount);

/// <summary>
/// Resolves a <c>type_name</c> to a symbol by NAME over an in-memory symbol set (design §3). julie ships
/// <c>target_symbol_id</c> NULL at extract (verified 0/1797 type_args, 0/24830 identifiers), so every cross-file leg
/// (CreateMap, DbSet entity, response/request DTO) must do string-name resolution here. Pure: the symbol set is passed
/// in; no DB, no I/O.
///
/// <para><b>Ambiguity policy (load-bearing precision guard):</b> a unique name resolves; a namespace or file/project
/// hint can break a tie; but &gt;1 match with no usable hint is <see cref="ResolutionStatus.Ambiguous"/> — the resolver
/// returns no symbol and the caller drops the edge or lowers its confidence, NEVER High. It never arbitrarily picks one
/// of several same-named types.</para>
/// </summary>
public sealed class SymbolResolver
{
    // Leaf-name → candidates. Matching is by the type's simple (leaf) name; the qualifier disambiguates a tie.
    private readonly IReadOnlyDictionary<string, List<SymbolDetail>> _byLeafName;

    /// <summary>Build a resolver over <paramref name="symbols"/> (the resolvable type symbols of one workspace index).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="symbols"/> is null.</exception>
    public SymbolResolver(IReadOnlyCollection<SymbolDetail> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        var index = new Dictionary<string, List<SymbolDetail>>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (string.IsNullOrWhiteSpace(symbol.Name))
                continue;
            if (!index.TryGetValue(symbol.Name, out var bucket))
            {
                bucket = [];
                index[symbol.Name] = bucket;
            }
            bucket.Add(symbol);
        }

        // Deterministic candidate order within each bucket (by id) so a tie-break narrowing is stable.
        foreach (var bucket in index.Values)
            bucket.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));

        _byLeafName = index;
    }

    /// <summary>
    /// Resolve <paramref name="typeName"/> (possibly namespace-qualified, e.g. <c>Core.Reporting.Data.Account</c>) to a
    /// symbol. Optional hints break a tie when several types share the leaf name: <paramref name="preferNamespace"/>
    /// (the use-site's namespace) and <paramref name="preferFile"/> (the use-site's file, for a shared path-prefix
    /// preference). A namespace embedded in a qualified <paramref name="typeName"/> is itself used as a hint.
    /// </summary>
    /// <param name="typeName">The type name to resolve; blank yields <see cref="ResolutionStatus.Unresolved"/>.</param>
    /// <param name="preferNamespace">An optional namespace hint for tie-breaking, or null.</param>
    /// <param name="preferFile">An optional use-site file hint for tie-breaking (shared leading path segment), or null.</param>
    public NameResolution Resolve(string typeName, string? preferNamespace = null, string? preferFile = null)
    {
        ArgumentNullException.ThrowIfNull(typeName);

        if (string.IsNullOrWhiteSpace(typeName))
            return new NameResolution(ResolutionStatus.Unresolved, null, 0);

        var (leaf, qualifier) = SplitQualified(typeName.Trim());

        if (!_byLeafName.TryGetValue(leaf, out var candidates) || candidates.Count == 0)
            return new NameResolution(ResolutionStatus.Unresolved, null, 0);

        int matchCount = candidates.Count;
        if (matchCount == 1)
            return new NameResolution(ResolutionStatus.Resolved, candidates[0].Id, matchCount);

        // >1 candidate: try the namespace hint (from the qualified type name first, then the explicit hint), then the
        // file hint. A hint that narrows to exactly one wins; anything else stays ambiguous.
        var narrowed = TieBreakByNamespace(candidates, qualifier)
                       ?? TieBreakByNamespace(candidates, preferNamespace)
                       ?? TieBreakByFile(candidates, preferFile);

        if (narrowed is not null)
            return new NameResolution(ResolutionStatus.Resolved, narrowed.Id, matchCount);

        return new NameResolution(ResolutionStatus.Ambiguous, null, matchCount);
    }

    /// <summary>Split a possibly-qualified type name into its leaf name and the namespace qualifier (or null).</summary>
    private static (string Leaf, string? Qualifier) SplitQualified(string typeName)
    {
        int dot = typeName.LastIndexOf('.');
        if (dot <= 0 || dot >= typeName.Length - 1)
            return (typeName, null);
        return (typeName[(dot + 1)..], typeName[..dot]);
    }

    /// <summary>Narrow to the single candidate whose namespace equals the hint, or null if 0 or &gt;1 remain.</summary>
    private static SymbolDetail? TieBreakByNamespace(List<SymbolDetail> candidates, string? ns)
    {
        if (string.IsNullOrEmpty(ns))
            return null;

        SymbolDetail? only = null;
        foreach (var c in candidates)
        {
            if (c.Namespace is not null && string.Equals(c.Namespace, ns, StringComparison.Ordinal))
            {
                if (only is not null)
                    return null; // >1 namespace match: still ambiguous, do not pick by id
                only = c;
            }
        }
        return only;
    }

    /// <summary>
    /// Narrow by the use-site file's leading path segment: prefer the single candidate sharing the first path segment
    /// (the project/service root) with <paramref name="preferFile"/>. Null if 0 or &gt;1 candidates share it.
    /// </summary>
    private static SymbolDetail? TieBreakByFile(List<SymbolDetail> candidates, string? preferFile)
    {
        if (string.IsNullOrEmpty(preferFile))
            return null;

        var hintRoot = FirstSegment(preferFile);
        if (hintRoot.Length == 0)
            return null;

        SymbolDetail? only = null;
        foreach (var c in candidates)
        {
            if (string.Equals(FirstSegment(c.FilePath), hintRoot, StringComparison.Ordinal))
            {
                if (only is not null)
                    return null; // >1 share the project root: ambiguous
                only = c;
            }
        }
        return only;
    }

    /// <summary>The first path segment of a workspace-relative path (its project/service root), or empty.</summary>
    private static string FirstSegment(string path)
    {
        var p = path.Replace('\\', '/').TrimStart('/');
        int slash = p.IndexOf('/');
        return slash < 0 ? p : p[..slash];
    }
}
