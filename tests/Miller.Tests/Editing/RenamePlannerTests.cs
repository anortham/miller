using Miller.Core.Editing;
using Xunit;

namespace Miller.Tests.Editing;

/// <summary>
/// The workspace-wide rename planner (M6 decision log #5, Components/1). Pins: a multi-site/multi-file plan
/// (each occurrence → an exact-span rewrite to the new name); the def-site token is rewritten like any other
/// site; invalid new_name → an InvalidNewName error and no edits; the preview summary reports the total count
/// and per-file grouping; a homonym site (an unrelated same-name symbol the Server supplies) IS included and
/// IS visible in the preview — pinning the documented name-based behavior. Applying the plan through the
/// splicer is asserted to produce the renamed content.
/// </summary>
public sealed class RenamePlannerTests
{
    // "var Total = 0; return Total + Total;" — three occurrences of "Total" (ASCII → char==byte).
    private const string FileA = "var Total = 0; return Total + Total;";

    private static RenameSite SiteAt(string content, int ordinal, bool isDef = false)
    {
        // The ordinal-th (0-based) occurrence of "Total".
        var idx = -1;
        for (var k = 0; k <= ordinal; k++)
            idx = content.IndexOf("Total", idx + 1, StringComparison.Ordinal);
        return new RenameSite(idx, idx + "Total".Length, StartLine: 1, IsDefinition: isDef);
    }

    [Fact]
    public void Plan_SingleFile_RewritesEveryOccurrenceToNewName()
    {
        var input = new RenameFileInput("/repo/A.cs", FileA,
            [SiteAt(FileA, 0, isDef: true), SiteAt(FileA, 1), SiteAt(FileA, 2)]);

        var plan = RenamePlanner.Plan("Total", "Sum", [input]);

        Assert.True(plan.IsSuccess);
        var planned = Assert.Single(plan.PlannedEdits);
        Assert.Equal("/repo/A.cs", planned.FilePath);
        Assert.Equal(3, planned.Edits.Count);
        Assert.All(planned.Edits, e => Assert.Equal("Sum", e.Replacement));
        // The PlannedEdit's NewContent must already reflect the splice.
        Assert.Equal("var Sum = 0; return Sum + Sum;", planned.NewContent);
        Assert.Equal(FileA, planned.OldContent);
    }

    [Fact]
    public void Plan_MultiFile_ProducesOnePlannedEditPerFile()
    {
        const string fileB = "Total();";
        var inputs = new[]
        {
            new RenameFileInput("/repo/A.cs", FileA, [SiteAt(FileA, 0, true), SiteAt(FileA, 1), SiteAt(FileA, 2)]),
            new RenameFileInput("/repo/B.cs", fileB, [new RenameSite(0, 5, 1)]),
        };

        var plan = RenamePlanner.Plan("Total", "Sum", inputs);

        Assert.True(plan.IsSuccess);
        Assert.Equal(2, plan.PlannedEdits.Count);
        Assert.Equal("var Sum = 0; return Sum + Sum;", plan.PlannedEdits[0].NewContent);
        Assert.Equal("Sum();", plan.PlannedEdits[1].NewContent);
    }

    [Fact]
    public void Plan_DefinitionSite_IsRewrittenLikeAnyOtherSite()
    {
        // The def site (IsDefinition=true) is the first occurrence; it must be among the rewritten edits.
        var input = new RenameFileInput("/repo/A.cs", FileA, [SiteAt(FileA, 0, isDef: true)]);
        var plan = RenamePlanner.Plan("Total", "Sum", [input]);

        Assert.True(plan.IsSuccess);
        var edit = Assert.Single(plan.PlannedEdits[0].Edits);
        // The first "Total" in FileA begins after "var " → byte 4.
        Assert.Equal(4, edit.StartByte);
        Assert.Equal(9, edit.EndByte);
        Assert.Equal("Sum", edit.Replacement);
    }

    [Fact]
    public void Plan_Summary_ReportsTotalAndPerFileCounts()
    {
        const string fileB = "Total; Total;";
        var inputs = new[]
        {
            new RenameFileInput("/repo/A.cs", FileA, [SiteAt(FileA, 0, true), SiteAt(FileA, 1), SiteAt(FileA, 2)]),
            new RenameFileInput("/repo/B.cs", fileB, [new RenameSite(0, 5, 1), new RenameSite(7, 12, 1)]),
        };

        var plan = RenamePlanner.Plan("Total", "Sum", inputs);

        Assert.True(plan.IsSuccess);
        Assert.Equal(5, plan.TotalSites);
        Assert.Equal(2, plan.Summary.Count);
        Assert.Equal(new RenameFileSummary("/repo/A.cs", 3), plan.Summary[0]);
        Assert.Equal(new RenameFileSummary("/repo/B.cs", 2), plan.Summary[1]);
    }

    [Fact]
    public void Plan_HomonymSite_IsIncludedAndVisibleInPreview()
    {
        // FileC contains an UNRELATED symbol also named "Total" (a homonym). Because target_symbol_id is NULL
        // at extract, the Server supplies it as a site and the planner rewrites it too. The documented
        // name-based behavior: the homonym IS renamed and IS counted in the preview summary.
        const string homonymFile = "class Report { int Total; }"; // an unrelated 'Total' field
        var totalIdx = homonymFile.IndexOf("Total", StringComparison.Ordinal);
        var inputs = new[]
        {
            new RenameFileInput("/repo/A.cs", FileA, [SiteAt(FileA, 0, true)]),
            new RenameFileInput("/repo/Report.cs", homonymFile, [new RenameSite(totalIdx, totalIdx + 5, 1)]),
        };

        var plan = RenamePlanner.Plan("Total", "Sum", inputs);

        Assert.True(plan.IsSuccess);
        Assert.Equal(2, plan.TotalSites);
        // The homonym file appears in the preview summary with its 1 site.
        Assert.Contains(plan.Summary, s => s.FilePath == "/repo/Report.cs" && s.SiteCount == 1);
        // And it is actually rewritten.
        Assert.Equal("class Report { int Sum; }", plan.PlannedEdits.Single(p => p.FilePath == "/repo/Report.cs").NewContent);
    }

    [Fact]
    public void Plan_RenameAfterMultibyte_UsesByteSpansVerbatim()
    {
        // 'é' is 2 bytes; the Server-supplied site already carries the byte span, so the planner just applies it.
        const string content = "café Total done"; // "Total" begins at byte 6
        const int totalByte = 6;
        var input = new RenameFileInput("/repo/M.cs", content, [new RenameSite(totalByte, totalByte + 5, 1, true)]);

        var plan = RenamePlanner.Plan("Total", "Sum", [input]);

        Assert.True(plan.IsSuccess);
        Assert.Equal("café Sum done", plan.PlannedEdits[0].NewContent);
    }

    [Theory]
    [InlineData("")]            // empty
    [InlineData("  ")]          // whitespace
    [InlineData("123abc")]      // starts with a digit
    [InlineData("has space")]   // contains a space
    [InlineData("a-b")]         // contains a hyphen
    [InlineData("a.b")]         // contains a dot (a member path, not a bare identifier)
    public void Plan_InvalidNewName_ReturnsInvalidNewNameError_NoEdits(string badName)
    {
        var input = new RenameFileInput("/repo/A.cs", FileA, [SiteAt(FileA, 0, true)]);
        var plan = RenamePlanner.Plan("Total", badName, [input]);

        Assert.False(plan.IsSuccess);
        Assert.Equal(EditErrorKind.InvalidNewName, plan.Error!.Kind);
        Assert.Empty(plan.PlannedEdits);
    }

    [Theory]
    [InlineData("Sum")]
    [InlineData("_private")]
    [InlineData("camelCase")]
    [InlineData("with_underscores_123")]
    [InlineData("PascalCase")]
    public void IsValidIdentifier_AcceptsPlausibleIdentifiers(string name)
    {
        Assert.True(RenamePlanner.IsValidIdentifier(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1leading")]
    [InlineData("has space")]
    [InlineData("a-b")]
    [InlineData("a.b")]
    [InlineData("a(b)")]
    public void IsValidIdentifier_RejectsImplausibleIdentifiers(string name)
    {
        Assert.False(RenamePlanner.IsValidIdentifier(name));
    }

    [Fact]
    public void Plan_FileWithNoSites_IsOmittedFromPlanAndSummary()
    {
        // The Server may hand a file with an empty site list (defensive); it contributes nothing.
        var inputs = new[]
        {
            new RenameFileInput("/repo/A.cs", FileA, [SiteAt(FileA, 0, true)]),
            new RenameFileInput("/repo/Empty.cs", "no matches here", []),
        };

        var plan = RenamePlanner.Plan("Total", "Sum", inputs);

        Assert.True(plan.IsSuccess);
        Assert.Single(plan.PlannedEdits);
        Assert.DoesNotContain(plan.Summary, s => s.FilePath == "/repo/Empty.cs");
    }

    [Fact]
    public void Plan_NoFilesAtAll_SucceedsWithZeroSites()
    {
        // Renaming a name with zero occurrences anywhere is a no-op success (the tool reports "0 sites").
        var plan = RenamePlanner.Plan("Total", "Sum", []);
        Assert.True(plan.IsSuccess);
        Assert.Equal(0, plan.TotalSites);
        Assert.Empty(plan.PlannedEdits);
    }

    // ---- splice-failure containment (pure-planner contract: returns EditError, never throws) ----
    // The Server reads occurrence spans from julie's identifiers table but splices against CURRENT disk
    // content. If the disk drifted since the index was built, a span can fall past EOF (or two spans can
    // overlap). TextSplicer.Apply throws on those; RenamePlanner.Plan must CATCH and return a clean
    // EditError instead of letting the exception escape the pure planner.

    [Fact]
    public void Plan_SiteSpanPastEndOfContent_ReturnsEditError_DoesNotThrow()
    {
        // Content is 8 bytes ("Total();"); a site claims [0, 99) — past EOF (the file shrank since indexing).
        const string content = "Total();";
        var input = new RenameFileInput("/repo/Drifted.cs", content, [new RenameSite(0, 99, 1, true)]);

        var plan = RenamePlanner.Plan("Total", "Sum", [input]);

        Assert.False(plan.IsSuccess);
        Assert.NotNull(plan.Error);
        Assert.Empty(plan.PlannedEdits);
        Assert.Contains("Drifted.cs", plan.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_OverlappingSites_ReturnsEditError_DoesNotThrow()
    {
        // Two sites that overlap ([0,5) and [3,8)) — drift produced an inconsistent set; Apply would throw.
        const string content = "TotalTotal";
        var input = new RenameFileInput("/repo/Overlap.cs", content,
            [new RenameSite(0, 5, 1, true), new RenameSite(3, 8, 1)]);

        var plan = RenamePlanner.Plan("Total", "Sum", [input]);

        Assert.False(plan.IsSuccess);
        Assert.NotNull(plan.Error);
        Assert.Empty(plan.PlannedEdits);
    }

    [Fact]
    public void Plan_OneFileDrifts_DoesNotEmitPartialEditsForEarlierFiles()
    {
        // A valid file precedes a drifted one. The drift must fail the WHOLE plan cleanly (all-or-nothing
        // preview), not throw mid-loop after the first file's edits were already accumulated.
        var inputs = new[]
        {
            new RenameFileInput("/repo/Good.cs", "Total();", [new RenameSite(0, 5, 1, true)]),
            new RenameFileInput("/repo/Bad.cs", "Total();", [new RenameSite(0, 999, 1)]),
        };

        var plan = RenamePlanner.Plan("Total", "Sum", inputs);

        Assert.False(plan.IsSuccess);
        Assert.Empty(plan.PlannedEdits);
        Assert.Equal(0, plan.TotalSites);
    }
}
