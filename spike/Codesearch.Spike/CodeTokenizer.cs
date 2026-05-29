namespace Codesearch.Spike;

/// <summary>
/// Span-based, allocation-conscious port of julie's code-aware identifier splitting
/// (src/search/tokenizer.rs: split_camel_case / split_snake_case / pretokenize_code).
///
/// Given "getHTTPResponseCode" it emits the lowercased original plus component parts:
/// ["gethttpresponsecode", "get", "http", "response", "code"].
///
/// The scan over the input is allocation-free (ReadOnlySpan&lt;char&gt;); the only
/// allocations are the emitted token strings themselves, which any index ultimately needs.
/// This is the ~300 LOC the host would own if it splits in C# rather than consuming a
/// julie-emitted `search_tokens` column.
/// </summary>
public static class CodeTokenizer
{
    /// <summary>Tokenize <paramref name="text"/>, appending lowercased tokens to <paramref name="output"/>.</summary>
    public static void Tokenize(ReadOnlySpan<char> text, List<string> output)
    {
        int i = 0, n = text.Length;
        while (i < n)
        {
            while (i < n && !IsWordChar(text[i])) i++;   // skip delimiters
            if (i >= n) break;
            int start = i;
            while (i < n && IsWordChar(text[i])) i++;     // take a word run
            EmitWord(text.Slice(start, i - start), output);
        }
    }

    private static void EmitWord(ReadOnlySpan<char> word, List<string> output)
    {
        if (word.IsEmpty) return;
        output.Add(ToLower(word));                         // keep original (lowercased)

        int segStart = 0;
        for (int i = 1; i < word.Length; i++)
        {
            if (IsBoundary(word, i))
            {
                output.Add(ToLower(word.Slice(segStart, i - segStart)));
                segStart = i;
            }
        }
        if (segStart > 0)                                  // only emit tail if we actually split
            output.Add(ToLower(word.Slice(segStart)));
    }

    /// <summary>Boundary rules: camelCase (aB), acronym end (HTTPServer -&gt; HTTP|Server), and letter/digit transitions (Vector512 -&gt; vector|512).</summary>
    private static bool IsBoundary(ReadOnlySpan<char> w, int i)
    {
        char p = w[i - 1], c = w[i];
        // lower/digit -> upper  (getUser, user2Name)
        if ((char.IsAsciiLetterLower(p) || char.IsAsciiDigit(p)) && char.IsAsciiLetterUpper(c))
            return true;
        // UPPER UPPER lower -> split before the trailing upper (HTTPServer -> HTTP|Server)
        if (char.IsAsciiLetterUpper(p) && char.IsAsciiLetterUpper(c)
            && i + 1 < w.Length && char.IsAsciiLetterLower(w[i + 1]))
            return true;
        // letter <-> digit boundary (Vector512 -> vector|512, utf8 -> utf|8)
        if ((char.IsAsciiLetter(p) && char.IsAsciiDigit(c)) || (char.IsAsciiDigit(p) && char.IsAsciiLetter(c)))
            return true;
        return false;
    }

    private static bool IsWordChar(char c)
        => char.IsAsciiLetterOrDigit(c) || (c > 127 && char.IsLetterOrDigit(c));

    private static string ToLower(ReadOnlySpan<char> s)
    {
        Span<char> buf = s.Length <= 256 ? stackalloc char[s.Length] : new char[s.Length];
        int written = s.ToLowerInvariant(buf);
        return new string(buf[..written]);
    }

    // ---- naive baseline: what you'd write reaching for Regex/string.Split, for comparison ----

    private static readonly System.Text.RegularExpressions.Regex CamelRx = new(
        @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[A-Za-z])(?=[0-9])|(?<=[0-9])(?=[A-Za-z])",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static List<string> TokenizeNaive(string text)
    {
        var output = new List<string>();
        foreach (var word in System.Text.RegularExpressions.Regex.Split(text, "[^A-Za-z0-9]+"))
        {
            if (word.Length == 0) continue;
            output.Add(word.ToLowerInvariant());
            var parts = CamelRx.Split(word);
            if (parts.Length > 1)
                foreach (var part in parts)
                    output.Add(part.ToLowerInvariant());
        }
        return output;
    }
}
