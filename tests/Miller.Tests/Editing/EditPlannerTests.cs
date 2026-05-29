using System.Text;
using Miller.Core.Editing;
using Xunit;

namespace Miller.Tests.Editing;

/// <summary>
/// The pure per-operation planner (M6 Components/1, decision log #7). Each test pins the exact byte-span
/// <see cref="TextEdit"/>(s) an operation produces over a known fixture, then confirms applying them through
/// <see cref="TextSplicer"/> yields the intended text. Covers replace_text first/last/all + not-found,
/// body vs signature span math, insert before/after positions, add_doc line→byte + newline, and the
/// NULL-body-span reject path. add_doc is asserted to insert the caller's text verbatim (no synthesized "///").
/// </summary>
public sealed class EditPlannerTests
{
    private static int ByteLen(string s) => Encoding.UTF8.GetByteCount(s);

    // A symbol span fixture mirroring the spec's verified example: a method with start/body spans.
    private static SymbolEditSpan MethodSpan(int start, int end, int bodyStart, int bodyEnd, int line = 3)
        => new(start, end, bodyStart, bodyEnd, line, "Total");

    // ---- ReplaceText ----------------------------------------------------------------------------

    [Fact]
    public void ReplaceText_First_TargetsOnlyEarliestMatch()
    {
        const string content = "foo bar foo baz foo";
        var plan = EditPlanner.ReplaceText(content, "foo", Occurrence.First);

        Assert.True(plan.IsSuccess);
        var edit = Assert.Single(plan.Edits);
        Assert.Equal(new TextEdit(0, 3, ""), edit with { Replacement = "" });
        Assert.Equal("X bar foo baz foo", TextSplicer.Apply(content, [edit with { Replacement = "X" }]));
    }

    [Fact]
    public void ReplaceText_Last_TargetsOnlyFinalMatch()
    {
        const string content = "foo bar foo baz foo";
        var lastStart = content.LastIndexOf("foo", StringComparison.Ordinal); // char==byte here (ASCII)
        var plan = EditPlanner.ReplaceText(content, "foo", Occurrence.Last);

        Assert.True(plan.IsSuccess);
        var edit = Assert.Single(plan.Edits);
        Assert.Equal(lastStart, edit.StartByte);
        Assert.Equal(lastStart + 3, edit.EndByte);
        Assert.Equal("foo bar foo baz X", TextSplicer.Apply(content, plan.Edits.Select(e => e with { Replacement = "X" }).ToArray()));
    }

    [Fact]
    public void ReplaceText_All_TargetsEveryMatch()
    {
        const string content = "foo bar foo baz foo";
        var plan = EditPlanner.ReplaceText(content, "foo", Occurrence.All);

        Assert.True(plan.IsSuccess);
        Assert.Equal(3, plan.Edits.Count);
        var applied = TextSplicer.Apply(content, plan.Edits.Select(e => e with { Replacement = "X" }).ToArray());
        Assert.Equal("X bar X baz X", applied);
    }

    [Fact]
    public void ReplaceText_All_OverlappingPattern_DoesNotProduceOverlappingEdits()
    {
        // "aaaa" with pattern "aa": non-overlapping scan yields matches at [0,2) and [2,4), not [1,3).
        const string content = "aaaa";
        var plan = EditPlanner.ReplaceText(content, "aa", Occurrence.All);

        Assert.True(plan.IsSuccess);
        Assert.Equal(2, plan.Edits.Count);
        // Applying must not throw (would throw if overlapping) and must replace cleanly.
        Assert.Equal("bb", TextSplicer.Apply(content, plan.Edits.Select(e => e with { Replacement = "b" }).ToArray()));
    }

    [Fact]
    public void ReplaceText_NotFound_ReturnsTextNotFoundError()
    {
        var plan = EditPlanner.ReplaceText("hello world", "absent", Occurrence.First);

        Assert.False(plan.IsSuccess);
        Assert.Equal(EditErrorKind.TextNotFound, plan.Error!.Kind);
        Assert.Contains("absent", plan.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceText_All_NotFound_ReturnsTextNotFoundError()
    {
        var plan = EditPlanner.ReplaceText("hello world", "absent", Occurrence.All);

        Assert.False(plan.IsSuccess);
        Assert.Equal(EditErrorKind.TextNotFound, plan.Error!.Kind);
    }

    [Fact]
    public void ReplaceText_EmptyOldText_ReturnsMissingArgumentError()
    {
        var plan = EditPlanner.ReplaceText("hello", "", Occurrence.First);

        Assert.False(plan.IsSuccess);
        Assert.Equal(EditErrorKind.MissingArgument, plan.Error!.Kind);
    }

    [Fact]
    public void ReplaceText_MatchAfterMultibyte_ProducesByteOffsets()
    {
        // "café foo": 'é' is 2 bytes, so "foo" starts at byte 6 (char 5). The edit span must be byte-based.
        const string content = "café foo";
        var plan = EditPlanner.ReplaceText(content, "foo", Occurrence.First);

        Assert.True(plan.IsSuccess);
        var edit = Assert.Single(plan.Edits);
        Assert.Equal(ByteLen("café "), edit.StartByte); // 6, not 5
        Assert.Equal(ByteLen("café ") + 3, edit.EndByte);
        Assert.Equal("café bar", TextSplicer.Apply(content, [edit with { Replacement = "bar" }]));
    }

    // ---- ReplaceSymbolBody / Signature ---------------------------------------------------------

    [Fact]
    public void ReplaceSymbolBody_TargetsBodySpan()
    {
        // body span = [bodyStart, bodyEnd)
        var span = MethodSpan(start: 81, end: 130, bodyStart: 112, bodyEnd: 130);
        var plan = EditPlanner.ReplaceSymbolBody(span, "{ return 0; }");

        Assert.True(plan.IsSuccess);
        var edit = Assert.Single(plan.Edits);
        Assert.Equal(112, edit.StartByte);
        Assert.Equal(130, edit.EndByte);
        Assert.Equal("{ return 0; }", edit.Replacement);
    }

    [Fact]
    public void ReplaceSymbolSignature_TargetsStartToBodyStart()
    {
        // signature span = [start, bodyStart)
        var span = MethodSpan(start: 81, end: 130, bodyStart: 112, bodyEnd: 130);
        var plan = EditPlanner.ReplaceSymbolSignature(span, "public int Total() ");

        Assert.True(plan.IsSuccess);
        var edit = Assert.Single(plan.Edits);
        Assert.Equal(81, edit.StartByte);
        Assert.Equal(112, edit.EndByte);
        Assert.Equal("public int Total() ", edit.Replacement);
    }

    [Fact]
    public void ReplaceSymbolBody_NullBodySpan_ReturnsBodySpanUnavailable()
    {
        // A field-like symbol: no body span.
        var span = new SymbolEditSpan(10, 20, BodyStartByte: null, BodyEndByte: null, StartLine: 2, Name: "count");
        var plan = EditPlanner.ReplaceSymbolBody(span, "x");

        Assert.False(plan.IsSuccess);
        Assert.Equal(EditErrorKind.BodySpanUnavailable, plan.Error!.Kind);
        Assert.Contains("count", plan.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceSymbolSignature_NullBodyStart_ReturnsBodySpanUnavailable()
    {
        // Signature span needs body_start as its exclusive end; without it the op is undefined.
        var span = new SymbolEditSpan(10, 20, BodyStartByte: null, BodyEndByte: null, StartLine: 2, Name: "count");
        var plan = EditPlanner.ReplaceSymbolSignature(span, "x");

        Assert.False(plan.IsSuccess);
        Assert.Equal(EditErrorKind.BodySpanUnavailable, plan.Error!.Kind);
    }

    [Fact]
    public void ReplaceSymbolBody_EmptyNewText_ReturnsMissingArgument()
    {
        var span = MethodSpan(81, 130, 112, 130);
        var plan = EditPlanner.ReplaceSymbolBody(span, "");
        Assert.False(plan.IsSuccess);
        Assert.Equal(EditErrorKind.MissingArgument, plan.Error!.Kind);
    }

    // ---- InsertBefore / InsertAfter ------------------------------------------------------------

    [Fact]
    public void InsertBefore_IsZeroWidthAtStartByte()
    {
        var span = MethodSpan(start: 81, end: 130, bodyStart: 112, bodyEnd: 130);
        var plan = EditPlanner.InsertBefore(span, "[Obsolete]\n");

        Assert.True(plan.IsSuccess);
        var edit = Assert.Single(plan.Edits);
        Assert.Equal(81, edit.StartByte);
        Assert.Equal(81, edit.EndByte); // zero-width
        Assert.Equal("[Obsolete]\n", edit.Replacement);
    }

    [Fact]
    public void InsertAfter_IsZeroWidthAtEndByte()
    {
        var span = MethodSpan(start: 81, end: 130, bodyStart: 112, bodyEnd: 130);
        var plan = EditPlanner.InsertAfter(span, "\n// done");

        Assert.True(plan.IsSuccess);
        var edit = Assert.Single(plan.Edits);
        Assert.Equal(130, edit.StartByte);
        Assert.Equal(130, edit.EndByte); // zero-width
        Assert.Equal("\n// done", edit.Replacement);
    }

    [Fact]
    public void InsertBefore_EmptyNewText_ReturnsMissingArgument()
    {
        var plan = EditPlanner.InsertBefore(MethodSpan(81, 130, 112, 130), "");
        Assert.False(plan.IsSuccess);
        Assert.Equal(EditErrorKind.MissingArgument, plan.Error!.Kind);
    }

    // ---- degenerate-span rejection (verified-fact #1, decision log #7) --------------------------
    // ExtractReader.ReadEditSpan substitutes 0 for a NULL start_byte/end_byte, producing a degenerate
    // [0, 0) span. The spec (ReadEditSpan comment + decision-7) requires the planner to REJECT such a span
    // rather than silently splice at file position 0. These pin that the insert/add_doc ops refuse it.

    [Fact]
    public void InsertBefore_DegenerateZeroSpan_ReturnsInvalidSpan_NotSilentInsertAtZero()
    {
        var span = new SymbolEditSpan(0, 0, BodyStartByte: null, BodyEndByte: null, StartLine: 0, Name: "Ghost");
        var plan = EditPlanner.InsertBefore(span, "[Obsolete]\n");

        Assert.False(plan.IsSuccess);
        Assert.Equal(EditErrorKind.InvalidSpan, plan.Error!.Kind);
        Assert.Contains("Ghost", plan.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InsertAfter_DegenerateZeroSpan_ReturnsInvalidSpan_NotSilentInsertAtZero()
    {
        var span = new SymbolEditSpan(0, 0, BodyStartByte: null, BodyEndByte: null, StartLine: 0, Name: "Ghost");
        var plan = EditPlanner.InsertAfter(span, "\n// done");

        Assert.False(plan.IsSuccess);
        Assert.Equal(EditErrorKind.InvalidSpan, plan.Error!.Kind);
        Assert.Contains("Ghost", plan.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddDoc_DegenerateZeroSpan_ReturnsInvalidSpan_NotSilentInsertAtZero()
    {
        const string content = "class Foo { }\n";
        var span = new SymbolEditSpan(0, 0, BodyStartByte: null, BodyEndByte: null, StartLine: 0, Name: "Ghost");
        var plan = EditPlanner.AddDoc(content, span, "/// doc");

        Assert.False(plan.IsSuccess);
        Assert.Equal(EditErrorKind.InvalidSpan, plan.Error!.Kind);
        Assert.Contains("Ghost", plan.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InsertBefore_RealSpanStartingAtByteZero_IsNotRejected()
    {
        // A legitimate symbol at the top of the file has start_byte=0 but a non-zero end_byte; it is NOT
        // degenerate and must still produce a valid zero-width insert at byte 0.
        var span = new SymbolEditSpan(0, 25, BodyStartByte: 12, BodyEndByte: 25, StartLine: 1, Name: "Foo");
        var plan = EditPlanner.InsertBefore(span, "[Obsolete]\n");

        Assert.True(plan.IsSuccess);
        var edit = Assert.Single(plan.Edits);
        Assert.Equal(0, edit.StartByte);
        Assert.Equal(0, edit.EndByte);
    }

    // ---- AddDoc (line→byte) --------------------------------------------------------------------

    [Fact]
    public void AddDoc_InsertsCallerTextPlusNewline_AtStartOfSymbolLine()
    {
        // 3 lines; symbol starts on line 3. The doc is inserted at the byte offset of line 3's start.
        const string content = "line1\nline2\nclass Foo { }\n";
        var line3Start = ByteLen("line1\nline2\n"); // byte offset where line 3 begins
        var span = new SymbolEditSpan(line3Start, ByteLen(content) - 1, null, null, StartLine: 3, Name: "Foo");

        var plan = EditPlanner.AddDoc(content, span, "/// The widget.");

        Assert.True(plan.IsSuccess);
        var edit = Assert.Single(plan.Edits);
        Assert.Equal(line3Start, edit.StartByte);
        Assert.Equal(line3Start, edit.EndByte); // zero-width insert
        // The planner appends a newline AFTER the caller's text, and adds no comment prefix of its own.
        Assert.Equal("/// The widget.\n", edit.Replacement);

        var result = TextSplicer.Apply(content, plan.Edits);
        Assert.Equal("line1\nline2\n/// The widget.\nclass Foo { }\n", result);
    }

    [Fact]
    public void AddDoc_OnFirstLine_InsertsAtByteZero()
    {
        const string content = "class Foo { }\n";
        var span = new SymbolEditSpan(0, ByteLen(content) - 1, null, null, StartLine: 1, Name: "Foo");

        var plan = EditPlanner.AddDoc(content, span, "# doc");

        Assert.True(plan.IsSuccess);
        var edit = Assert.Single(plan.Edits);
        Assert.Equal(0, edit.StartByte);
        Assert.Equal(0, edit.EndByte);
        Assert.Equal("# doc\n", edit.Replacement); // verbatim caller text (note: NOT "///"), + newline
        Assert.Equal("# doc\nclass Foo { }\n", TextSplicer.Apply(content, plan.Edits));
    }

    [Fact]
    public void AddDoc_LineStartIsByteOffset_AfterMultibyteLines()
    {
        // A preceding line contains multibyte chars; line→byte mapping must count bytes, not chars.
        const string content = "// café ☕ header\nclass Foo { }\n";
        var firstLineBytes = ByteLen("// café ☕ header\n");
        var span = new SymbolEditSpan(firstLineBytes, ByteLen(content) - 1, null, null, StartLine: 2, Name: "Foo");

        var plan = EditPlanner.AddDoc(content, span, "/// doc");

        Assert.True(plan.IsSuccess);
        var edit = Assert.Single(plan.Edits);
        Assert.Equal(firstLineBytes, edit.StartByte); // byte offset of line 2, not char offset
        Assert.Equal("// café ☕ header\n/// doc\nclass Foo { }\n", TextSplicer.Apply(content, plan.Edits));
    }

    [Fact]
    public void AddDoc_DoesNotSynthesizeCommentPrefix_ForAnyLanguage()
    {
        // Pin the language-agnostic rule: the replacement equals exactly the caller's text + one newline,
        // with no "///", "#", "--", or any other prefix added by the planner.
        const string content = "fn main() {}\n";
        var span = new SymbolEditSpan(0, ByteLen(content) - 1, null, null, StartLine: 1, Name: "main");

        var plan = EditPlanner.AddDoc(content, span, "RAW_DOC_TEXT");

        Assert.True(plan.IsSuccess);
        Assert.Equal("RAW_DOC_TEXT\n", Assert.Single(plan.Edits).Replacement);
    }

    [Fact]
    public void AddDoc_EmptyNewText_ReturnsMissingArgument()
    {
        const string content = "class Foo { }\n";
        var span = new SymbolEditSpan(0, 5, null, null, StartLine: 1, Name: "Foo");
        var plan = EditPlanner.AddDoc(content, span, "");
        Assert.False(plan.IsSuccess);
        Assert.Equal(EditErrorKind.MissingArgument, plan.Error!.Kind);
    }
}
