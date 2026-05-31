namespace Miller.Core.Resolver;

/// <summary>
/// Folds a type name to a canonical, lowercased <b>stem</b> so an entity and its DTO/interface/plural collapse to the
/// same key — the "safe finisher" of the cross-language resolver (design §4). It is NEVER the sole signal for an
/// entity↔DTO or entity↔table edge; it only confirms a pairing a structural breadcrumb already proposed.
///
/// <para>The fold, in order: strip an interface/field affix prefix (<c>I</c> before a CamelCase word, or a leading
/// <c>_</c>); strip ONE trailing role suffix (<c>Dto</c>/<c>Model</c>/<c>Request</c>/<c>Response</c>/<c>View</c>/
/// <c>VM</c>/<c>Entity</c>) as a whole trailing token; fold plural→singular; lowercase. Each step is applied on the
/// already-reduced string so combined cases (e.g. <c>IUserDto</c>, <c>IUsers</c>) reduce fully. Pure and
/// deterministic.</para>
/// </summary>
public static class NameNormalizer
{
    // Trailing role suffixes, longest-first so "VM" never pre-empts a longer match and a whole token is removed.
    // Compared case-insensitively but only stripped when they form the tail of the (still PascalCase) name.
    private static readonly string[] Suffixes =
    [
        "Response", "Request", "Entity", "Model", "View", "Dto", "VM",
    ];

    /// <summary>
    /// Reduce <paramref name="name"/> to its canonical stem. Blank/whitespace input yields <see cref="string.Empty"/>.
    /// </summary>
    /// <param name="name">The type name as written (e.g. <c>IUserDto</c>, <c>Categories</c>).</param>
    /// <returns>The lowercased canonical stem (e.g. <c>user</c>, <c>category</c>).</returns>
    public static string Stem(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var s = name.Trim();
        if (s.Length == 0)
            return string.Empty;

        s = StripPrefix(s);
        s = StripSuffix(s);
        s = Singularize(s);
        return s.ToLowerInvariant();
    }

    /// <summary>Strip a leading <c>_</c>, or a leading <c>I</c> that prefixes a CamelCase word (interface convention).</summary>
    private static string StripPrefix(string s)
    {
        if (s.StartsWith('_'))
            return s.TrimStart('_');

        // "IUser"/"IUsers": leading 'I' followed by an uppercase letter => interface prefix. "Identity" keeps its 'I'
        // (followed by a lowercase letter), and a bare "I" is left alone.
        if (s.Length >= 2 && s[0] == 'I' && char.IsUpper(s[1]))
            return s[1..];

        return s;
    }

    /// <summary>Strip ONE trailing role suffix when it forms the tail token of the name (longest match wins).</summary>
    private static string StripSuffix(string s)
    {
        foreach (var suffix in Suffixes)
        {
            if (s.Length <= suffix.Length)
                continue;
            if (!s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            // Only a whole trailing token: the char before the suffix must be a boundary (uppercase start of the
            // suffix region). This stops "Modeller" losing "Model" (next char 'l' is lowercase => not a token tail).
            int tailStart = s.Length - suffix.Length;
            if (char.IsUpper(s[tailStart]))
                return s[..tailStart];
        }
        return s;
    }

    /// <summary>
    /// Fold a likely English plural to its singular: <c>ies→y</c>; <c>(x|s|z|ch|sh)es→drop es</c>; trailing
    /// <c>s</c>→drop (but not <c>ss</c>). Conservative: leaves a word it cannot confidently singularize unchanged.
    /// </summary>
    private static string Singularize(string s)
    {
        if (s.Length <= 1)
            return s;

        // "Categories" -> "Category"
        if (s.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && s.Length > 3)
            return s[..^3] + "y";

        // "Boxes"/"Buses"/"Quizzes"/"Matches"/"Dishes" -> drop "es"
        if (s.EndsWith("es", StringComparison.OrdinalIgnoreCase) && s.Length > 2)
        {
            var stem = s[..^2];
            char last = char.ToLowerInvariant(stem[^1]);
            if (last is 'x' or 's' or 'z')
                return stem;
            if (stem.Length >= 2)
            {
                var lastTwo = stem[^2..].ToLowerInvariant();
                if (lastTwo is "ch" or "sh")
                    return stem;
            }
        }

        // Plain trailing "s" (but not "ss" like "Address", and not a single-letter result).
        if (s.EndsWith('s') && !s.EndsWith("ss", StringComparison.OrdinalIgnoreCase) && s.Length > 1)
            return s[..^1];

        return s;
    }
}
