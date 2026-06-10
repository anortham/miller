using Miller.Indexing;

namespace Miller.Server.Resolution;

/// <summary>Explicit override for the kind of a smart-string target (the <c>as</c> param).</summary>
public enum TargetKind
{
    /// <summary>Infer from the string shape (the default — see <see cref="SmartTargetResolver"/>).</summary>
    Auto,

    /// <summary>Force FILE-path interpretation regardless of shape.</summary>
    File,

    /// <summary>Force SYMBOL interpretation (id-shape used directly, else a NAME lookup).</summary>
    Symbol,
}

/// <summary>
/// The discriminated outcome of resolving a smart-string target (miller-toolbox.md L47-56). A tool pattern-
/// matches on the concrete subtype rather than juggling nullable fields. Never throws for "not found" — that
/// is a first-class outcome the tool renders, not an exception.
/// </summary>
public abstract record TargetResolution
{
    private TargetResolution() { }

    /// <summary>The target was (or was forced to be) a file path.</summary>
    public sealed record File(string Path) : TargetResolution;

    /// <summary>The target resolved to exactly one symbol (by id, or a unique name).</summary>
    public sealed record Symbol(IndexedSymbol Value) : TargetResolution;

    /// <summary>The name matched more than one symbol; the caller disambiguates.</summary>
    public sealed record Candidates(IReadOnlyList<IndexedSymbol> Matches) : TargetResolution;

    /// <summary>
    /// Nothing matched (a name not in the index, or an id-shaped string absent from it). Optionally carries
    /// agent-self-correction data: <paramref name="Suggestions"/> are up to a handful of close symbols
    /// (near-miss names, or — when <paramref name="ScopeMissed"/> is set — real matches a wrong <c>scope</c>
    /// filtered out). Both are additive defaults so existing constructions and pattern matches compile
    /// unchanged.
    /// </summary>
    public sealed record NotFound(
        string Target,
        IReadOnlyList<IndexedSymbol>? Suggestions = null,
        string? ScopeMissed = null) : TargetResolution
    {
        /// <summary>
        /// The shared compact not-found message (inspect/impact/trace render this verbatim). Without
        /// suggestions it is the historical message; with them the agent gets a one-turn correction.
        /// </summary>
        public string RenderMessage()
        {
            if (Suggestions is not { Count: > 0 })
                return $"'{Target}' not found. Try search to locate it.";

            string list = string.Join(", ", Suggestions.Select(s => $"{s.Name} ({s.FilePath}:{s.StartLine})"));
            return ScopeMissed is null
                ? $"'{Target}' not found. Closest: {list}. Try search to locate it."
                : $"'{Target}' not found in scope '{ScopeMissed}'. Found in: {list}. Pass one of those files as scope, or drop scope.";
        }
    }
}
