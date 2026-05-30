using Miller.Indexing;

namespace Miller.Server.Logging;

/// <summary>
/// The pure, I/O-free helper that turns a caught extract exception into the two strings the
/// <see cref="Miller.Server.Hosting.IndexerCore"/> catch sites log (m8-design §D3): the structured julie error
/// <c>codes</c> (already surfaced today) plus a bounded <c>stderrTail</c> of julie's raw process stderr (the
/// missing piece — <see cref="System.Exception.ToString()"/> omits the custom <see cref="JulieExtractException.StandardError"/>
/// property, so <c>{Exception}</c> never shows it).
///
/// <para><b>Behavior-preserving codes.</b> The <see cref="ExtractErrorDescription.Codes"/> wording byte-matches
/// the inline string the catch sites used before this helper existed: a
/// <see cref="JulieExtractFailedException"/> with no structured errors (or any non-failed exception) yields
/// <c>"(no structured errors)"</c>; one with errors yields <c>string.Join(", ", Errors.Select(e =&gt; e.Code))</c>.
/// Sourcing <c>codes</c> from here lets the three branches drop the duplicated inline expression.</para>
///
/// <para><b>Bounded tail.</b> <see cref="ExtractErrorDescription.StderrTail"/> is at most <see cref="MaxTail"/>
/// characters; a longer stderr is truncated to its LAST <see cref="MaxTail"/> chars (the tail carries the actual
/// failure, not the banner) prefixed with a single ellipsis so a reader sees it was clipped. Null-safe
/// throughout: a null/empty stderr, or an exception that carries none, yields an empty tail.</para>
/// </summary>
public static class ExtractErrorLog
{
    /// <summary>
    /// The maximum number of characters of <see cref="JulieExtractException.StandardError"/> kept in the tail.
    /// Long enough to capture a Rust panic / clap usage block, short enough to keep a log line readable.
    /// </summary>
    public const int MaxTail = 600;

    // The single-char marker prepended to a truncated tail so a reader knows the head was clipped.
    private const char Ellipsis = '…';

    /// <summary>
    /// Describe a caught extract exception for logging: its structured julie error <paramref name="ex"/> codes
    /// and a bounded tail of its raw stderr. Pure — no I/O. See the type remarks for the exact per-type rules.
    /// </summary>
    /// <param name="ex">The caught exception (any type; null tolerated, treated as the generic case).</param>
    /// <returns>The codes string + bounded stderr tail to interpolate into the catch-site log templates.</returns>
    public static ExtractErrorDescription Describe(Exception? ex) => ex switch
    {
        // The exit-1 failure report: join its structured codes (matching the prior inline wording exactly) and
        // surface the stderr tail. Errors is never null (the exception's ctor takes a non-null list), but the
        // count gate already distinguishes the empty case, so no extra null guard is needed here.
        JulieExtractFailedException jf => new ExtractErrorDescription(
            jf.Errors.Count == 0
                ? "(no structured errors)"
                : string.Join(", ", jf.Errors.Select(e => e.Code)),
            Tail(jf.StandardError)),

        // A base extract failure (an unexpected exit code, a usage/argv error): no structured codes, but its
        // stderr is the whole diagnosis, so surface the tail.
        JulieExtractException je => new ExtractErrorDescription("(no structured errors)", Tail(je.StandardError)),

        // Anything else (a JSON parse error, an exec failure, a null exception): no codes, no stderr to show.
        _ => new ExtractErrorDescription("(no structured errors)", ""),
    };

    // Return at most MaxTail trailing characters of s (the tail holds the actual failure), with a single
    // ellipsis prefix when the head was clipped. Null/empty -> empty string. Pure.
    private static string Tail(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        if (s.Length <= MaxTail)
            return s;
        return Ellipsis + s[^MaxTail..];
    }
}

/// <summary>
/// The two log-ready strings <see cref="ExtractErrorLog.Describe"/> produces from a caught extract exception:
/// the structured julie error <see cref="Codes"/> and a bounded <see cref="StderrTail"/> of julie's raw stderr.
/// </summary>
/// <param name="Codes">
/// The joined structured error codes, or <c>"(no structured errors)"</c> when there are none — byte-matching the
/// wording the catch sites used inline before this helper.
/// </param>
/// <param name="StderrTail">
/// At most <see cref="ExtractErrorLog.MaxTail"/> trailing characters of the process stderr (ellipsis-prefixed
/// when truncated), or an empty string when there is none.
/// </param>
public readonly record struct ExtractErrorDescription(string Codes, string StderrTail);
