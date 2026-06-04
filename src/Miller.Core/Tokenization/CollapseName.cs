namespace Miller.Core.Tokenization;

/// <summary>
/// Collapses an identifier to a single separator-free, lowercased run of word characters: every
/// separator (<c>_ - . :: </c>, whitespace, punctuation) is dropped and what remains is case-folded
/// (<c>format_external_extract</c> and <c>FormatExternalExtract</c> both → <c>formatexternalextract</c>).
///
/// This is the recall key the word <see cref="CodeTokenizer"/> cannot produce: it splits on those same
/// separators, so snake_case spellings never yield a joined whole-identifier token. The collapsed form
/// makes interior- and boundary-crossing substring matches (e.g. <c>tionprov</c> →
/// <c>IAuthenticationProvider</c>) reachable and language-uniform. "Word character" matches the
/// tokenizer exactly (ASCII letters/digits plus non-ASCII letters/digits) so the two transforms agree.
///
/// Span-based and allocation-conscious like <see cref="CodeTokenizer"/>: the only allocation is the
/// returned string. Pure logic, zero I/O — it belongs in Miller.Core.
/// </summary>
public static class CollapseName
{
    /// <summary>
    /// Returns the collapsed, lowercased run of <paramref name="text"/>, or <see cref="string.Empty"/>
    /// when it has no word characters.
    /// </summary>
    public static string Of(ReadOnlySpan<char> text)
    {
        Span<char> buf = text.Length <= 256 ? stackalloc char[text.Length] : new char[text.Length];
        int written = 0;
        foreach (char c in text)
        {
            if (IsWordChar(c))
                buf[written++] = char.ToLowerInvariant(c);
        }
        return written == 0 ? string.Empty : new string(buf[..written]);
    }

    private static bool IsWordChar(char c)
        => char.IsAsciiLetterOrDigit(c) || (c > 127 && char.IsLetterOrDigit(c));
}
