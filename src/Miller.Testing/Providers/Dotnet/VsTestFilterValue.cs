using System.Text;

namespace Miller.Testing;

/// <summary>
/// Escapes one VALUE so it can sit inside a VSTest <c>--filter</c> expression without changing that
/// expression's structure.
///
/// The generic (mstest/nunit) run path joins selection terms with <c>|</c> and wraps the result as
/// <c>(terms)&amp;exclusion</c>, so every character VSTest's filter grammar reserves — <c>( ) &amp; | =
/// ! ~</c>, and the backslash that escapes them — must be neutralized inside a value. Unescaped, an
/// mstest DataRow/DynamicData display name such as <c>Cases (1,2)</c> hands VSTest bare parentheses it
/// reads as grouping: the likely result is a filter parse error, which fails the run and reports a false
/// red, and a leniently parsed expression executes a different set than the caller selected. Trait
/// values reach the exclusion clause the same way, so they take the same treatment.
///
/// The comma keeps its historic percent-encoding rather than a backslash, because that is VSTest's own
/// documented spelling for a comma inside a parameterized name, and <c>%2C</c> introduces no reserved
/// character of its own.
/// </summary>
internal static class VsTestFilterValue
{
    /// <summary>
    /// The characters VSTest's filter grammar reserves. The backslash is first in spirit, not in order:
    /// escaping walks the value ONCE, so an existing backslash cannot be re-escaped by a later pass.
    /// </summary>
    private const string Reserved = "\\()&|=!~";

    /// <summary>Everything <see cref="Escape"/> rewrites, so an untouched value returns without a copy.</summary>
    private const string Rewritten = Reserved + ",";

    /// <summary>Escapes <paramref name="value"/> for use as the right-hand side of a filter clause.</summary>
    internal static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.AsSpan().IndexOfAny(Rewritten) < 0)
            return value;

        var escaped = new StringBuilder(value.Length + 8);
        foreach (var character in value)
        {
            if (character == ',')
            {
                escaped.Append("%2C");
                continue;
            }

            if (Reserved.Contains(character, StringComparison.Ordinal))
                escaped.Append('\\');

            escaped.Append(character);
        }

        return escaped.ToString();
    }
}
