using Miller.Core.Diff;
using Xunit;

namespace Miller.Tests.Diff;

/// <summary>
/// The unified-diff PARSER (M5 decision D5 seed extraction; not the M6 diff renderer). Pins extraction of each
/// file's path and its NEW-side (post-image) changed line ranges from the <c>@@ -a,b +c,d @@</c> hunk headers:
/// single/multi hunk, multiple files, implied <c>,1</c> counts, added files (<c>/dev/null</c> source), deleted
/// files (<c>/dev/null</c> target → path from the old side), <c>diff --git</c> rename headers, deletion-only
/// hunks (anchor line still emitted), CRLF tolerance, and garbage input yielding an empty list (never throws).
/// Asserts on concrete paths and ranges.
/// </summary>
public sealed class DiffTargetsTests
{
    private static DiffFile Single(IReadOnlyList<DiffFile> files)
    {
        Assert.Single(files);
        return files[0];
    }

    [Fact]
    public void Parse_SingleFileSingleHunk_ExtractsPathAndNewRange()
    {
        var diff =
            "--- a/src/Foo.cs\n" +
            "+++ b/src/Foo.cs\n" +
            "@@ -10,3 +12,4 @@\n" +
            " context\n" +
            "-old\n" +
            "+new1\n" +
            "+new2\n";

        var file = Single(DiffTargets.Parse(diff));

        Assert.Equal("src/Foo.cs", file.Path);
        // New side starts at line 12, spans 4 lines → [12, 15].
        Assert.Equal([new LineRange(12, 15)], file.Changed);
    }

    [Fact]
    public void Parse_MultipleFiles_ExtractsEachWithItsRanges()
    {
        var diff =
            "--- a/A.cs\n" +
            "+++ b/A.cs\n" +
            "@@ -1,1 +1,2 @@\n" +
            " x\n" +
            "+y\n" +
            "--- a/B.cs\n" +
            "+++ b/B.cs\n" +
            "@@ -5,2 +5,2 @@\n" +
            "-p\n" +
            "+q\n";

        var files = DiffTargets.Parse(diff);

        Assert.Equal(2, files.Count);
        Assert.Equal("A.cs", files[0].Path);
        Assert.Equal([new LineRange(1, 2)], files[0].Changed);
        Assert.Equal("B.cs", files[1].Path);
        Assert.Equal([new LineRange(5, 6)], files[1].Changed);
    }

    [Fact]
    public void Parse_MultiHunkOneFile_ExtractsEveryHunkRange()
    {
        var diff =
            "--- a/F.cs\n" +
            "+++ b/F.cs\n" +
            "@@ -1,1 +1,1 @@\n" +
            "-a\n" +
            "+A\n" +
            "@@ -20,2 +20,3 @@\n" +
            " b\n" +
            "+c\n" +
            " d\n";

        var file = Single(DiffTargets.Parse(diff));

        Assert.Equal("F.cs", file.Path);
        Assert.Equal([new LineRange(1, 1), new LineRange(20, 22)], file.Changed);
    }

    [Fact]
    public void Parse_ImpliedCount_TreatsMissingCountAsOne()
    {
        // "@@ -5 +5 @@" — both counts implied as 1 → new range [5, 5].
        var diff =
            "--- a/G.cs\n" +
            "+++ b/G.cs\n" +
            "@@ -5 +5 @@\n" +
            "-old\n" +
            "+new\n";

        var file = Single(DiffTargets.Parse(diff));

        Assert.Equal([new LineRange(5, 5)], file.Changed);
    }

    [Fact]
    public void Parse_AddedFile_UsesNewSidePath_DevNullSourceIgnored()
    {
        var diff =
            "--- /dev/null\n" +
            "+++ b/src/New.cs\n" +
            "@@ -0,0 +1,3 @@\n" +
            "+line1\n" +
            "+line2\n" +
            "+line3\n";

        var file = Single(DiffTargets.Parse(diff));

        Assert.Equal("src/New.cs", file.Path);
        Assert.Equal([new LineRange(1, 3)], file.Changed);
    }

    [Fact]
    public void Parse_DeletedFile_UsesOldSidePath_WhenNewSideIsDevNull()
    {
        var diff =
            "--- a/src/Gone.cs\n" +
            "+++ /dev/null\n" +
            "@@ -1,3 +0,0 @@\n" +
            "-line1\n" +
            "-line2\n" +
            "-line3\n";

        var file = Single(DiffTargets.Parse(diff));

        // New side is /dev/null → fall back to the old-side path so the file is still identified.
        Assert.Equal("src/Gone.cs", file.Path);
        // New count 0 → the deletion anchor is the new-side start line (0). Range [0, 0].
        Assert.Equal([new LineRange(0, 0)], file.Changed);
    }

    [Fact]
    public void Parse_DeletionOnlyHunkInExistingFile_EmitsAnchorLine()
    {
        // A hunk that only deletes lines (+c,0) inside an otherwise-present file still yields its anchor line c.
        var diff =
            "--- a/H.cs\n" +
            "+++ b/H.cs\n" +
            "@@ -8,2 +7,0 @@\n" +
            "-dead1\n" +
            "-dead2\n";

        var file = Single(DiffTargets.Parse(diff));

        Assert.Equal("H.cs", file.Path);
        // New count 0 → anchor line 7, zero-width range represented as [7, 7].
        Assert.Equal([new LineRange(7, 7)], file.Changed);
    }

    [Fact]
    public void Parse_GitRenameHeader_UsesNewPath()
    {
        // A pure rename with content change: the +++ header carries the new path; that is what we anchor on.
        var diff =
            "diff --git a/old/Name.cs b/new/Name.cs\n" +
            "similarity index 95%\n" +
            "rename from old/Name.cs\n" +
            "rename to new/Name.cs\n" +
            "--- a/old/Name.cs\n" +
            "+++ b/new/Name.cs\n" +
            "@@ -3,1 +3,1 @@\n" +
            "-x\n" +
            "+y\n";

        var file = Single(DiffTargets.Parse(diff));

        Assert.Equal("new/Name.cs", file.Path);
        Assert.Equal([new LineRange(3, 3)], file.Changed);
    }

    [Fact]
    public void Parse_GitRenameWithoutContentChange_StillReportsNewPath()
    {
        // A rename with no hunks (no @@) still produces a DiffFile for the new path, with no ranges.
        var diff =
            "diff --git a/old/Name.cs b/new/Name.cs\n" +
            "similarity index 100%\n" +
            "rename from old/Name.cs\n" +
            "rename to new/Name.cs\n";

        var file = Single(DiffTargets.Parse(diff));

        Assert.Equal("new/Name.cs", file.Path);
        Assert.Empty(file.Changed);
    }

    [Fact]
    public void Parse_CrlfLineEndings_AreTolerated()
    {
        var diff =
            "--- a/C.cs\r\n" +
            "+++ b/C.cs\r\n" +
            "@@ -2,1 +2,2 @@\r\n" +
            " keep\r\n" +
            "+add\r\n";

        var file = Single(DiffTargets.Parse(diff));

        Assert.Equal("C.cs", file.Path);
        Assert.Equal([new LineRange(2, 3)], file.Changed);
    }

    [Fact]
    public void Parse_EmptyString_ReturnsEmpty()
    {
        Assert.Empty(DiffTargets.Parse(string.Empty));
    }

    [Fact]
    public void Parse_GarbageNonDiff_ReturnsEmpty()
    {
        var garbage = "this is not a diff\njust some prose\n42\n";

        Assert.Empty(DiffTargets.Parse(garbage));
    }

    [Fact]
    public void Parse_HunkBeforeAnyHeader_IsIgnored_DoesNotThrow()
    {
        // A malformed lead-in hunk with no preceding +++ header has no file to attach to → dropped, not thrown.
        var diff =
            "@@ -1,1 +1,1 @@\n" +
            "-orphan\n" +
            "+ORPHAN\n" +
            "--- a/Real.cs\n" +
            "+++ b/Real.cs\n" +
            "@@ -2,1 +2,1 @@\n" +
            "-x\n" +
            "+y\n";

        var file = Single(DiffTargets.Parse(diff));

        Assert.Equal("Real.cs", file.Path);
        Assert.Equal([new LineRange(2, 2)], file.Changed);
    }

    [Fact]
    public void Parse_MalformedHunkHeader_IsSkipped_ValidOnesKept()
    {
        // The first @@ line is malformed (no + side); it is skipped, the valid one is kept. Never throws.
        var diff =
            "--- a/M.cs\n" +
            "+++ b/M.cs\n" +
            "@@ garbage @@\n" +
            "@@ -4,1 +4,2 @@\n" +
            " z\n" +
            "+w\n";

        var file = Single(DiffTargets.Parse(diff));

        Assert.Equal("M.cs", file.Path);
        Assert.Equal([new LineRange(4, 5)], file.Changed);
    }

    // ---- in-hunk body lines that LOOK like file headers (finding 1) ----
    // Unified-diff hunk body lines are the original text prefixed by a single '-' or '+'. A deletion whose
    // content begins with "-- " yields "--- ..."; an addition whose content begins with "++ " yields "+++ ...".
    // The parser must NOT mistake these for the "--- a/…" / "+++ b/…" file headers. Header detection is only
    // valid OUTSIDE a hunk body; inside a hunk, the @@ counts delimit the body exactly.

    [Fact]
    public void Parse_RemovedLineStartingWithDashDash_IsBody_NotAnOldSideHeader()
    {
        // A SQL/Lua/Haskell comment ("-- note") removed inside a hunk reads as "--- old note" on the diff line.
        // It must stay a body line of schema.sql, NOT open a bogus file named "old note".
        var diff =
            "--- a/schema.sql\n" +
            "+++ b/schema.sql\n" +
            "@@ -1,2 +1,2 @@\n" +
            " context\n" +
            "--- old note\n" +
            "+-- new note\n";

        var file = Single(DiffTargets.Parse(diff));

        Assert.Equal("schema.sql", file.Path);
        Assert.Equal([new LineRange(1, 2)], file.Changed);
    }

    [Fact]
    public void Parse_AddedLineStartingWithPlusPlus_IsBody_NotANewSideHeader()
    {
        // A markdown bullet / "++x" added inside a hunk reads as "+++ bullet added" on the diff line. It must
        // stay a body line of notes.md, NOT overwrite the file path to "bullet added".
        var diff =
            "--- a/notes.md\n" +
            "+++ b/notes.md\n" +
            "@@ -1,1 +1,2 @@\n" +
            " first\n" +
            "+++ bullet added\n";

        var file = Single(DiffTargets.Parse(diff));

        Assert.Equal("notes.md", file.Path);
        // Old count 1, new count 2 → new-side range [1, 2].
        Assert.Equal([new LineRange(1, 2)], file.Changed);
    }

    [Fact]
    public void Parse_MultiHunk_FirstHunkHasDashDashDeletion_SecondHunkStaysOnSameFile()
    {
        // The empirically-confirmed regression: a "--"-prefixed deletion in hunk 1 must not strip the SECOND
        // real hunk off schema.sql and re-attach it to a phantom file. Both hunks stay on schema.sql.
        var diff =
            "--- a/schema.sql\n" +
            "+++ b/schema.sql\n" +
            "@@ -1,2 +1,2 @@\n" +
            " context\n" +
            "--- old note\n" +
            "+-- new note\n" +
            "@@ -50,1 +50,2 @@\n" +
            " keep\n" +
            "+added\n";

        var file = Single(DiffTargets.Parse(diff));

        Assert.Equal("schema.sql", file.Path);
        // Hunk 1 new range [1,2]; hunk 2 new range [50,51] — both on the ONE file.
        Assert.Equal([new LineRange(1, 2), new LineRange(50, 51)], file.Changed);
    }

    [Fact]
    public void Parse_ContextLineStartingWithPlusPlus_IsBody_NotANewSideHeader()
    {
        // A context (unchanged) line is prefixed by a single space, so " ++x" is " " + "++x". This must not be
        // confused for a header either — context lines also belong to the hunk body.
        var diff =
            "--- a/code.cpp\n" +
            "+++ b/code.cpp\n" +
            "@@ -3,3 +3,3 @@\n" +
            " ++counter; // context\n" +
            "-old\n" +
            "+new\n";

        var file = Single(DiffTargets.Parse(diff));

        Assert.Equal("code.cpp", file.Path);
        Assert.Equal([new LineRange(3, 5)], file.Changed);
    }

    [Fact]
    public void Parse_NextFileHeaderAfterHunkBody_StillOpensTheSecondFile()
    {
        // After a hunk body completes (its @@ counts are exhausted), a real "--- a/…" header for the NEXT file
        // must still be recognized. This guards against an over-eager fix that never re-enables header detection.
        var diff =
            "--- a/A.cs\n" +
            "+++ b/A.cs\n" +
            "@@ -1,1 +1,2 @@\n" +
            " x\n" +
            "+y\n" +
            "--- a/B.cs\n" +
            "+++ b/B.cs\n" +
            "@@ -5,2 +5,2 @@\n" +
            "-p\n" +
            "+q\n";

        var files = DiffTargets.Parse(diff);

        Assert.Equal(2, files.Count);
        Assert.Equal("A.cs", files[0].Path);
        Assert.Equal([new LineRange(1, 2)], files[0].Changed);
        Assert.Equal("B.cs", files[1].Path);
        Assert.Equal([new LineRange(5, 6)], files[1].Changed);
    }
}
