using System.Text;
using Miller.Core.Editing;
using Xunit;

namespace Miller.Tests.Editing;

/// <summary>
/// The byte-span splicer (M6 decision log #2). Edits are absolute UTF-8 byte ranges applied right-to-left,
/// byte-exact. These pins cover: single replace, multi-edit ordering, pure insertion (zero-width span),
/// UTF-8 multibyte correctness (offsets must be byte offsets not char offsets), and the two reject paths
/// (overlap, out-of-range).
/// </summary>
public sealed class TextSplicerTests
{
    private static int ByteLen(string s) => Encoding.UTF8.GetByteCount(s);

    [Fact]
    public void Apply_NoEdits_ReturnsContentUnchanged()
    {
        Assert.Equal("hello world", TextSplicer.Apply("hello world", []));
    }

    [Fact]
    public void Apply_SingleReplace_SplicesByteRange()
    {
        // "hello world": replace bytes [6,11) ("world") with "there".
        var result = TextSplicer.Apply("hello world", [new TextEdit(6, 11, "there")]);
        Assert.Equal("hello there", result);
    }

    [Fact]
    public void Apply_MultipleEdits_AppliedRightToLeft_AllLandCorrectly()
    {
        // Two non-overlapping edits supplied out of order; both must land at their ORIGINAL offsets,
        // which only holds if the splicer applies highest-offset-first.
        const string content = "the quick brown fox"; // bytes: quick=[4,9), fox=[16,19)
        var result = TextSplicer.Apply(content,
        [
            new TextEdit(16, 19, "cat"),   // supplied first but is the rightmost edit
            new TextEdit(4, 9, "slow"),
        ]);
        Assert.Equal("the slow brown cat", result);
    }

    [Fact]
    public void Apply_PureInsertion_ZeroWidthSpan_InsertsWithoutDeleting()
    {
        // Zero-width span at byte 5 inserts; nothing is removed.
        var result = TextSplicer.Apply("abcdefgh", [new TextEdit(5, 5, "-X-")]);
        Assert.Equal("abcde-X-fgh", result);
    }

    [Fact]
    public void Apply_InsertAtStartAndEnd_BothLand()
    {
        var content = "core";
        var result = TextSplicer.Apply(content,
        [
            new TextEdit(0, 0, "<<"),
            new TextEdit(ByteLen(content), ByteLen(content), ">>"),
        ]);
        Assert.Equal("<<core>>", result);
    }

    [Fact]
    public void Apply_MultibyteAccentBeforeSpan_SpanIsByteIndexed_NotCharIndexed()
    {
        // "café list": 'é' is 2 UTF-8 bytes, so "list" begins at BYTE 6 though it is CHAR 5.
        // A char-indexed splicer would corrupt this; a byte-indexed one replaces "list" exactly.
        const string content = "café list";
        var listStart = ByteLen("café "); // = 6 bytes
        var result = TextSplicer.Apply(content,
            [new TextEdit(listStart, listStart + ByteLen("list"), "code")]);
        Assert.Equal("café code", result);
    }

    [Fact]
    public void Apply_MultibyteEmojiBeforeSpan_PreservesEmojiBytes()
    {
        // A 4-byte emoji ahead of the edit; the splice must not slice into or shift the emoji bytes.
        const string content = "🚀 launch now";
        var nowStart = ByteLen("🚀 launch "); // emoji=4 bytes + " launch "
        var result = TextSplicer.Apply(content,
            [new TextEdit(nowStart, nowStart + ByteLen("now"), "today")]);
        Assert.Equal("🚀 launch today", result);
    }

    [Fact]
    public void Apply_InsertReplacementContainingMultibyte_DecodesCleanly()
    {
        // The replacement itself contains multibyte text; the round-trip must reproduce it byte-for-byte.
        var result = TextSplicer.Apply("name = X", [new TextEdit(7, 8, "café☕")]);
        Assert.Equal("name = café☕", result);
    }

    [Theory]
    // Overlap shapes: identical, nested, partial-tail-into-head. All must throw.
    [InlineData(2, 6, 4, 8)]   // partial overlap
    [InlineData(2, 6, 2, 6)]   // identical
    [InlineData(2, 8, 4, 6)]   // nested
    [InlineData(2, 6, 5, 9)]   // tail overlaps head
    public void Apply_OverlappingEdits_Throws(int aStart, int aEnd, int bStart, int bEnd)
    {
        var ex = Assert.Throws<ArgumentException>(() => TextSplicer.Apply(
            "abcdefghij",
            [new TextEdit(aStart, aEnd, "?"), new TextEdit(bStart, bEnd, "?")]));
        Assert.Contains("overlap", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_AdjacentEdits_DoNotCountAsOverlap()
    {
        // [2,4) and [4,6) share only the exclusive end/inclusive start boundary — not an overlap.
        var result = TextSplicer.Apply("abcdef", [new TextEdit(2, 4, "X"), new TextEdit(4, 6, "Y")]);
        Assert.Equal("abXY", result);
    }

    [Theory]
    [InlineData(-1, 3)]   // negative start
    [InlineData(3, 2)]    // end before start
    [InlineData(0, 99)]   // end past content
    [InlineData(99, 99)]  // start past content
    public void Apply_OutOfRangeSpan_Throws(int start, int end)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            TextSplicer.Apply("abcdef", [new TextEdit(start, end, "?")]));
        Assert.NotNull(ex);
    }

    [Fact]
    public void Apply_InsertAtExactEndOfContent_IsInRange()
    {
        // Byte == length is a valid zero-width insertion point (append).
        var result = TextSplicer.Apply("abc", [new TextEdit(3, 3, "!")]);
        Assert.Equal("abc!", result);
    }
}
