using System.Globalization;

namespace Miller.Server.Cli;

/// <summary>
/// A tiny, dependency-free parse of one CLI verb's argument tail (everything AFTER the verb token). Supports
/// positional tokens (the query/target), <c>--name value</c>, <c>--name=value</c>, and presence-only boolean
/// flags (<c>--json</c>). Boolean flag names are declared by the caller so a boolean never swallows the following
/// positional — e.g. <c>search --json foo</c> keeps <c>foo</c> as the query rather than reading it as json's value.
/// Deliberately NOT a full getopt: Miller's CLI surface is small and this keeps the parse obvious and AOT-clean.
/// </summary>
internal sealed class CliOptions
{
    private readonly List<string> _positionals = new();
    private readonly Dictionary<string, string?> _flags = new(StringComparer.OrdinalIgnoreCase);

    private CliOptions()
    {
    }

    /// <summary>The non-flag tokens in order (the query words / the target).</summary>
    public IReadOnlyList<string> Positionals => _positionals;

    /// <summary>The positionals joined with a single space — the natural form of a multi-word query.</summary>
    public string Query => string.Join(' ', _positionals);

    /// <summary>
    /// Parse the verb's argument tail. <paramref name="booleanFlags"/> names the flags that take NO value
    /// (presence ⇒ true); every other <c>--flag</c> consumes the following token (or its <c>=value</c>).
    /// </summary>
    public static CliOptions Parse(IReadOnlyList<string> args, params string[] booleanFlags)
    {
        ArgumentNullException.ThrowIfNull(args);
        var booleans = new HashSet<string>(booleanFlags, StringComparer.OrdinalIgnoreCase);
        var options = new CliOptions();

        for (int i = 0; i < args.Count; i++)
        {
            string token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length == 2)
            {
                // A bare token, or a lone "--", is positional. NOTE: "--" is NOT a GNU end-of-options separator —
                // a following "--flag" is still parsed as a flag; Miller has no dashed query that needs escaping.
                options._positionals.Add(token);
                continue;
            }

            string name = token[2..];
            int eq = name.IndexOf('=', StringComparison.Ordinal);
            if (eq >= 0)
            {
                options._flags[name[..eq]] = name[(eq + 1)..];
                continue;
            }

            if (booleans.Contains(name))
            {
                options._flags[name] = null; // presence ⇒ true; never consumes a following token
                continue;
            }

            if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options._flags[name] = args[++i];
            }
            else
            {
                options._flags[name] = null; // a value flag with no value — recorded as present/empty
            }
        }

        return options;
    }

    /// <summary>True when <paramref name="name"/> was supplied (with or without a value).</summary>
    public bool Has(string name) => _flags.ContainsKey(name);

    /// <summary>The string value of <paramref name="name"/>, or <paramref name="fallback"/> when absent/valueless.</summary>
    public string? Value(string name, string? fallback = null) =>
        _flags.TryGetValue(name, out string? value) && value is not null ? value : fallback;

    /// <summary>The integer value of <paramref name="name"/>, or <paramref name="fallback"/> when absent/unparseable.</summary>
    public int Int(string name, int fallback) =>
        _flags.TryGetValue(name, out string? value)
        && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : fallback;
}
