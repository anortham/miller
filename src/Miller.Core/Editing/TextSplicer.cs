using System.Text;

namespace Miller.Core.Editing;

/// <summary>
/// Applies a set of byte-span <see cref="TextEdit"/>s to a string, UTF-8 byte-exact (decision log #2). The
/// content is encoded to UTF-8, edits are validated non-overlapping + in-range, sorted by ascending offset, and
/// applied in one left-to-right pass with a cursor (each source segment copied once), then the result decoded
/// back. This is the one place that turns a plan into spliced text; it is pure (no I/O) and language-agnostic —
/// it never inspects what it splices.
/// </summary>
public static class TextSplicer
{
    /// <summary>
    /// Splice every <paramref name="edits"/> range into <paramref name="content"/> and return the result.
    /// </summary>
    /// <param name="content">The source text whose UTF-8 bytes the spans index into.</param>
    /// <param name="edits">
    /// Non-overlapping byte-span replacements (any order). A zero-width span is a pure insertion.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> or <paramref name="edits"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An edit's span is negative, inverted, or past the content's byte length.</exception>
    /// <exception cref="ArgumentException">Two edits overlap.</exception>
    public static string Apply(string content, IReadOnlyList<TextEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(edits);

        if (edits.Count == 0)
            return content;

        var bytes = Encoding.UTF8.GetBytes(content);
        var byteLength = bytes.Length;

        // Validate each span is well-formed and in range before doing any work.
        foreach (var edit in edits)
        {
            if (edit.StartByte < 0)
                throw new ArgumentOutOfRangeException(nameof(edits),
                    $"Edit start byte {edit.StartByte} is negative.");
            if (edit.EndByte < edit.StartByte)
                throw new ArgumentOutOfRangeException(nameof(edits),
                    $"Edit end byte {edit.EndByte} precedes start byte {edit.StartByte}.");
            if (edit.EndByte > byteLength)
                throw new ArgumentOutOfRangeException(nameof(edits),
                    $"Edit end byte {edit.EndByte} is past the content's {byteLength}-byte length.");
        }

        // Sort a copy by start ascending so we can both detect overlap and apply right-to-left.
        var ordered = edits.OrderBy(e => e.StartByte).ThenBy(e => e.EndByte).ToArray();

        // Overlap = the next edit starts before the previous edit ends. Adjacency (next.Start == prev.End)
        // is allowed. A zero-width insert sharing an offset with another edit's boundary is adjacency, not
        // overlap (covered by the same strict-less-than test).
        for (var i = 1; i < ordered.Length; i++)
        {
            if (ordered[i].StartByte < ordered[i - 1].EndByte)
            {
                throw new ArgumentException(
                    $"Edits overlap: [{ordered[i - 1].StartByte},{ordered[i - 1].EndByte}) and " +
                    $"[{ordered[i].StartByte},{ordered[i].EndByte}).",
                    nameof(edits));
            }
        }

        // Apply in ascending offset order (left-to-right with cursor advancement) so each segment is written once.
        var builder = new MemoryStream();
        var cursor = 0;
        foreach (var edit in ordered)
        {
            builder.Write(bytes, cursor, edit.StartByte - cursor);
            var replacementBytes = Encoding.UTF8.GetBytes(edit.Replacement);
            builder.Write(replacementBytes, 0, replacementBytes.Length);
            cursor = edit.EndByte;
        }
        builder.Write(bytes, cursor, byteLength - cursor);

        return Encoding.UTF8.GetString(builder.GetBuffer(), 0, (int)builder.Length);
    }
}
