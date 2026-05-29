using System.Globalization;

namespace Miller.Core.Diff;

/// <summary>A contiguous run of NEW-side (post-image) line numbers a hunk touched, 1-based and inclusive.</summary>
/// <param name="StartLine">First changed line on the new side. For a deletion-only hunk this is the anchor line.</param>
/// <param name="EndLine">Last changed line on the new side; equals <see cref="StartLine"/> for a single line.</param>
public sealed record LineRange(int StartLine, int EndLine);

/// <summary>One file in a parsed unified diff: its path and the new-side line ranges its hunks changed.</summary>
/// <param name="Path">The file path (the <c>+++ b/…</c> target with the <c>b/</c> prefix stripped; the old-side
/// path for a deleted file; the rename target for a <c>diff --git</c> rename).</param>
/// <param name="Changed">The new-side changed ranges, in hunk order; empty when the file has no hunks (e.g. a
/// pure rename with no content change).</param>
public sealed record DiffFile(string Path, IReadOnlyList<LineRange> Changed);

/// <summary>
/// The pure unified-diff PARSER for M5 decision D5 (NOT the M6 <see cref="Editing.UnifiedDiff"/> renderer). It
/// extracts, per file, the NEW-side changed line ranges from the <c>@@ -a,b +c,d @@</c> hunk headers so the
/// <c>impact</c> tool can map a diff to the symbols whose <c>[start_line, end_line]</c> intersect a change.
///
/// <para>Path resolution prefers the <c>+++ b/&lt;path&gt;</c> header (the new side), strips the <c>b/</c>
/// prefix, and falls back to the <c>--- a/&lt;path&gt;</c> old side when the new side is <c>/dev/null</c> (a
/// deletion). A <c>diff --git a/… b/…</c> line opens a new file using its <c>b/</c> path (so a rename with no
/// content hunks still yields a <see cref="DiffFile"/>); a following <c>+++</c> refines it. Robust by design:
/// missing counts imply <c>1</c>, deletion-only hunks (<c>+c,0</c>) yield the zero-width anchor range
/// <c>[c, c]</c>, CRLF is tolerated, orphan/malformed hunks are dropped, and any unparseable input yields the
/// files it COULD parse — the parser never throws.</para>
///
/// <para><b>Header/body disambiguation (the load-bearing correctness property).</b> A unified-diff hunk BODY
/// line is the original source prefixed by a single <c>' '</c>/<c>'-'</c>/<c>'+'</c>, so a removed
/// <c>"-- comment"</c> reads as <c>"--- …"</c> and an added <c>"++x"</c> as <c>"+++ …"</c> — colliding with the
/// <c>--- a/…</c> / <c>+++ b/…</c> file-header prefixes. Classifying by prefix alone would mis-attribute or drop
/// those changed ranges (a silent wrong/empty <c>impact</c> result, not a crash). The parser therefore tracks an
/// in-hunk state: standard hunks are self-delimiting via their <c>@@</c> counts (the old side spans
/// context+deletion lines, the new side context+addition lines), so while consuming a hunk body EVERY
/// <c>' '</c>/<c>'-'</c>/<c>'+'</c> line is body — never a header — until both counts are exhausted, at which
/// point header detection resumes. A line that is none of those (or a new <c>@@</c>/<c>---</c>/<c>diff --git</c>)
/// ends a truncated hunk early.</para>
/// </summary>
public static class DiffTargets
{
    /// <summary>
    /// Parse <paramref name="unifiedDiff"/> into the changed files and their new-side line ranges. A null,
    /// empty, or non-diff input yields an empty list; partially malformed input yields the parseable files.
    /// </summary>
    /// <param name="unifiedDiff">Standard unified-diff text (git or plain), any line endings.</param>
    public static IReadOnlyList<DiffFile> Parse(string unifiedDiff)
    {
        if (string.IsNullOrEmpty(unifiedDiff))
            return [];

        var files = new List<DiffFile>();

        // The file currently being accumulated. Null until a header (diff --git / --- / +++) establishes a path.
        // Hunks seen with no current file are orphans and are dropped.
        string? currentPath = null;
        List<LineRange> currentRanges = [];
        var fileOpen = false;

        // True when the in-progress file was opened by a "diff --git" line. A following "--- " then refines the
        // same file rather than starting a new one; without it (a plain diff) "--- " is itself the file boundary.
        var openedByGit = false;

        // The old-side path captured from "--- a/…" so a "+++ /dev/null" (a deletion) can fall back to it.
        string? pendingOldPath = null;

        // Hunk-body state (finding 1). Unified-diff hunk BODY lines are the original source prefixed by a single
        // ' '/'-'/'+', so a removed "-- comment" reads as "--- …" and an added "++x" as "+++ …" — colliding with
        // the "--- a/…" / "+++ b/…" file-HEADER prefixes. Header detection is therefore valid ONLY outside a hunk
        // body. Standard hunks are self-delimiting via their @@ counts: the old side spans (context + deletions)
        // lines, the new side (context + additions) lines. We track the remaining lines on each side and treat
        // every line as body until BOTH budgets reach zero, at which point the body ends and headers resume.
        var inHunk = false;
        var oldRemaining = 0; // context + deletion lines still expected on the old side of the current hunk
        var newRemaining = 0; // context + addition lines still expected on the new side of the current hunk

        void Flush()
        {
            if (fileOpen)
                files.Add(new DiffFile(currentPath ?? string.Empty, currentRanges));
            currentPath = null;
            currentRanges = [];
            fileOpen = false;
            openedByGit = false;
            pendingOldPath = null;
            inHunk = false;
            oldRemaining = 0;
            newRemaining = 0;
        }

        foreach (var rawLine in EnumerateLines(unifiedDiff))
        {
            // Strip a trailing '\r' so CRLF diffs parse identically to LF.
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;

            // Inside a hunk body, consume body lines using the @@ counts so content that mimics a header
            // ("--- old note", "+++ bullet") is never misread as one. A '\' (no-newline marker) is metadata, not
            // a body line. Anything that is NOT a ' '/'+'/'-' body line ends the hunk early (a malformed/truncated
            // hunk) and falls through to header handling below.
            if (inHunk)
            {
                if (line.Length == 0 || line[0] == ' ')
                {
                    if (oldRemaining > 0) oldRemaining--;
                    if (newRemaining > 0) newRemaining--;
                    if (oldRemaining == 0 && newRemaining == 0) inHunk = false;
                    continue;
                }
                if (line[0] == '-')
                {
                    if (oldRemaining > 0) oldRemaining--;
                    if (oldRemaining == 0 && newRemaining == 0) inHunk = false;
                    continue;
                }
                if (line[0] == '+')
                {
                    if (newRemaining > 0) newRemaining--;
                    if (oldRemaining == 0 && newRemaining == 0) inHunk = false;
                    continue;
                }
                if (line.StartsWith('\\'))
                    continue; // "\ No newline at end of file" — metadata within the body, not a counted line

                // A non-body line inside an unfinished hunk (e.g. a new "@@"/"---"/"diff --git" before the counts
                // were exhausted): the hunk is over; stop counting and let the header branches below handle it.
                inHunk = false;
            }

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                // A new file begins. Emit the previous one, then seed the path from the "b/<path>" token so a
                // rename with no hunks still produces a file. A later "+++" header refines this.
                Flush();
                currentPath = ExtractGitNewPath(line);
                fileOpen = currentPath is not null;
                openedByGit = fileOpen;
                continue;
            }

            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                // Old-side header. In a plain (non-git) diff it is itself the file boundary, so flush first; for a
                // git diff the file is already open (openedByGit) and we only record the old-side path.
                if (!openedByGit)
                    Flush();

                pendingOldPath = StripPrefixedPath(line[4..], "a/");
                // A "--- " with a usable old path opens a file even before "+++" is seen (covers deletions whose
                // new side is /dev/null); "+++" below sets the authoritative path.
                if (pendingOldPath is not null)
                {
                    currentPath = pendingOldPath;
                    fileOpen = true;
                }
                continue;
            }

            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var newSide = line[4..].Trim();
                var path = newSide is "/dev/null"
                    ? pendingOldPath          // deletion: identify by the old-side path
                    : StripPrefixedPath(newSide, "b/");

                if (path is not null)
                {
                    currentPath = path;
                    fileOpen = true;
                }
                continue;
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                if (!fileOpen)
                    continue; // orphan hunk with no file to attach to → drop

                var hunk = ParseHunkHeader(line);
                if (hunk is not null)
                {
                    currentRanges.Add(hunk.NewRange);
                    // Open the body. A zero-count side contributes no body lines on that side. A body with both
                    // counts zero (degenerate) leaves inHunk false, so the next line is treated as a header.
                    oldRemaining = hunk.OldCount;
                    newRemaining = hunk.NewCount;
                    inHunk = oldRemaining > 0 || newRemaining > 0;
                }
                continue;
            }

            // Any other line outside a hunk body (git metadata, prose) carries no header/hunk info for this parser.
        }

        Flush();
        return files;
    }

    /// <summary>Split on '\n' without allocating a trailing empty element for a terminating newline.</summary>
    private static IEnumerable<string> EnumerateLines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        if (start < text.Length)
            yield return text[start..];
    }

    /// <summary>
    /// Strip a leading <paramref name="prefix"/> (<c>a/</c> or <c>b/</c>) from a diff path token, trimming any
    /// trailing tab-delimited timestamp git/diff appends. Returns null for <c>/dev/null</c> or an empty token.
    /// </summary>
    private static string? StripPrefixedPath(string token, string prefix)
    {
        token = token.Trim();
        // git/unified diffs may append "\t<timestamp>" after the path; keep only the path part.
        var tab = token.IndexOf('\t');
        if (tab >= 0)
            token = token[..tab];

        if (token.Length == 0 || token == "/dev/null")
            return null;

        if (token.StartsWith(prefix, StringComparison.Ordinal))
            token = token[prefix.Length..];

        return token.Length == 0 ? null : token;
    }

    /// <summary>
    /// Extract the new-side path from a <c>diff --git a/&lt;old&gt; b/&lt;new&gt;</c> line. Uses the <c>b/</c>
    /// segment so renames anchor on the destination. Returns null if the line has no <c>b/</c> token.
    /// </summary>
    private static string? ExtractGitNewPath(string line)
    {
        // Format: "diff --git a/<old> b/<new>". Paths with spaces are rare and not quoted here; take the last
        // " b/" occurrence as the new-path marker (the old path is the " a/" token before it).
        const string marker = " b/";
        var idx = line.LastIndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return null;
        var path = line[(idx + marker.Length)..].Trim();
        return path.Length == 0 ? null : path;
    }

    /// <summary>The parsed counts of an <c>@@ -a,b +c,d @@</c> hunk header: the new-side <see cref="LineRange"/>
    /// plus both sides' line counts (used to delimit the hunk BODY so content lines are never misread as
    /// headers). A missing <c>,b</c>/<c>,d</c> implies a count of 1.</summary>
    private sealed record HunkHeader(LineRange NewRange, int OldCount, int NewCount);

    /// <summary>
    /// Parse an <c>@@ -a,b +c,d @@</c> header into its old-side count <c>b</c>, new-side count <c>d</c>, and the
    /// inclusive new-side line range: <c>[c, c + d - 1]</c> for <c>d ≥ 1</c>, or the zero-width anchor
    /// <c>[c, c]</c> when <c>d == 0</c> (a deletion-only hunk). A missing <c>,b</c>/<c>,d</c> implies a count of 1.
    /// Returns null for a malformed header.
    /// </summary>
    private static HunkHeader? ParseHunkHeader(string header)
    {
        // Locate the "-a,b" group (after "@@ ") and the "+c,d" group (the '+' that follows it).
        var minus = header.IndexOf('-');
        var plus = header.IndexOf('+');
        if (minus < 0 || plus < 0)
            return null;

        // The old side may be absent or malformed; default its count to 1 (a hunk always touches ≥1 old line
        // unless it is a pure addition, which carries an explicit ",0"). Parse it best-effort.
        int oldCount = ParseCount(header.AsSpan(), minus + 1, out _);

        // The new side drives the reported range; a malformed new side makes the whole header unusable.
        int newCount = ParseCount(header.AsSpan(), plus + 1, out int newStart);
        if (newStart < 0)
            return null;

        // Zero-count side (pure deletion) → the anchor line is the new-side start; report a zero-width range.
        var newRange = newCount <= 0
            ? new LineRange(newStart, newStart)
            : new LineRange(newStart, newStart + newCount - 1);

        return new HunkHeader(newRange, Math.Max(oldCount, 0), Math.Max(newCount, 0));
    }

    /// <summary>
    /// Read a unified-diff <c>&lt;start&gt;[,&lt;count&gt;]</c> group from <paramref name="span"/> starting at
    /// <paramref name="at"/> (just past the leading <c>-</c>/<c>+</c>). Sets <paramref name="start"/> to the line
    /// number (or <c>-1</c> if absent/malformed) and returns the count (a missing <c>,count</c> implies 1, a
    /// malformed group yields 0).
    /// </summary>
    private static int ParseCount(ReadOnlySpan<char> span, int at, out int start)
    {
        start = -1;

        var i = at;
        var tokenStart = i;
        while (i < span.Length && (char.IsDigit(span[i]) || span[i] == ','))
            i++;
        var token = span[tokenStart..i];
        if (token.Length == 0)
            return 0;

        var comma = token.IndexOf(',');
        if (comma < 0)
        {
            if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out start))
            {
                start = -1;
                return 0;
            }
            return 1; // implied count
        }

        var startSpan = token[..comma];
        var countSpan = token[(comma + 1)..];
        if (!int.TryParse(startSpan, NumberStyles.None, CultureInfo.InvariantCulture, out start))
        {
            start = -1;
            return 0;
        }
        if (countSpan.Length == 0 ||
            !int.TryParse(countSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int count))
        {
            // A "<start>," with no count is malformed; the start parsed but the count did not.
            return 0;
        }
        return count;
    }
}
