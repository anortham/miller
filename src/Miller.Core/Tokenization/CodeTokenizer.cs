namespace Miller.Core.Tokenization;

/// <summary>
/// Span-based, allocation-conscious port of julie's code-aware identifier splitting
/// (src/search/tokenizer.rs: split_camel_case / split_snake_case / pretokenize_code), carried
/// over verbatim from the verified spike (<c>spike/Codesearch.Spike/CodeTokenizer.cs</c>).
///
/// Given <c>getHTTPResponseCode</c> it emits the lowercased original plus component parts:
/// <c>[gethttpresponsecode, get, http, response, code]</c>. The scan over the input is
/// allocation-free (<see cref="ReadOnlySpan{T}"/>); the only allocations are the emitted token
/// strings, which any index ultimately needs.
///
/// No stopwords, no min-length, no stemming. Single-char and digit tokens are kept. This is pure
/// logic with zero I/O — the logic↔infrastructure seam Miller.Core is built to protect.
/// </summary>
public static class CodeTokenizer
{
    /// <summary>
    /// Tokenize <paramref name="text"/>, APPENDING lowercased tokens to <paramref name="output"/>.
    /// The list is not cleared, so a caller may reuse one buffer across documents (clear between docs).
    /// </summary>
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
        output.Add(ToLower(word));                         // keep original (lowercased) first

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

    /// <summary>
    /// Boundary rules (split BEFORE index <paramref name="i"/>): camelCase (aB), acronym end
    /// (HTTPServer -&gt; HTTP|Server), and letter/digit transitions either direction (Vector512 -&gt; vector|512).
    /// </summary>
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
}
