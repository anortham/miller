using System.Text;

namespace Miller.Testing;

/// <summary>
/// The little bit of shell shape this provider has to understand about a package script: where its tokens
/// are, and whether it is ONE command or several chained together.
/// </summary>
internal static class NodeCommandLine
{
    /// <summary>
    /// The operators that turn a package script into more than one command. Deliberately literal and
    /// deliberately short: <c>&amp;&amp;</c>, <c>||</c>, <c>;</c>, a pipe, a bare background <c>&amp;</c>,
    /// and a newline. Anything longer would start guessing at shell grammar, and the only decision riding
    /// on this is "may arguments be appended to the end of this script?", for which any of them means no.
    /// </summary>
    private static bool IsChainOperatorCharacter(char character) =>
        character is '&' or '|' or ';' or '\n' or '\r';

    /// <summary>
    /// True when appending arguments to this script would NOT deliver them to the command that needs them.
    ///
    /// <para>A package manager appends what follows <c>--</c> to the END of the script, so
    /// <c>jest --env node</c> receives the reporter flags and <c>a &amp;&amp; b</c> hands them to <c>b</c>
    /// alone — every other command in the chain runs unreported, and the report the provider then reads
    /// does not exist. vercel/ms passes both halves of
    /// <c>pnpm run test:nodejs &amp;&amp; pnpm run test:edge</c> by hand while continuous testing marked
    /// all four of its files red (dogfood finding F10, 2026-08-21).</para>
    ///
    /// <para>Quoted text is not shell syntax, so <c>jest --testPathPattern "a|b"</c> is one command.</para>
    /// </summary>
    internal static bool IsChained(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        var inQuote = false;
        var quoteCharacter = '\0';
        foreach (var character in command)
        {
            if ((character == '"' || character == '\'') && (!inQuote || character == quoteCharacter))
            {
                inQuote = !inQuote;
                quoteCharacter = inQuote ? character : '\0';
                continue;
            }

            if (!inQuote && IsChainOperatorCharacter(character))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The command script split at its unquoted chain operators. A script with no operator yields itself,
    /// so a caller never has to branch on <see cref="IsChained"/> first.
    /// </summary>
    internal static IReadOnlyList<string> SplitChainedSegments(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return [];

        var segments = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;
        var quoteCharacter = '\0';
        foreach (var character in command)
        {
            if ((character == '"' || character == '\'') && (!inQuote || character == quoteCharacter))
            {
                inQuote = !inQuote;
                quoteCharacter = inQuote ? character : '\0';
                current.Append(character);
                continue;
            }

            if (!inQuote && IsChainOperatorCharacter(character))
            {
                AddSegment(segments, current);
                continue;
            }

            current.Append(character);
        }

        AddSegment(segments, current);
        return segments;
    }

    private static void AddSegment(List<string> segments, StringBuilder current)
    {
        var segment = current.ToString().Trim();
        current.Clear();
        if (segment.Length > 0)
            segments.Add(segment);
    }

    /// <summary>
    /// The tokens of one command line, honouring single and double quotes. Quote characters are consumed;
    /// what they enclose stays one token.
    /// </summary>
    internal static IReadOnlyList<string> SplitCommand(string command)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;
        var quoteCharacter = '\0';
        foreach (var character in command)
        {
            if ((character == '"' || character == '\'') && (!inQuote || character == quoteCharacter))
            {
                inQuote = !inQuote;
                quoteCharacter = inQuote ? character : '\0';
                continue;
            }

            if (char.IsWhiteSpace(character) && !inQuote)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
            result.Add(current.ToString());
        return result;
    }
}
