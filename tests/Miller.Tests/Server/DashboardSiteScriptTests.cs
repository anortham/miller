using System.Text.RegularExpressions;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// The client-churn contract for <c>wwwroot/js/dashboard-site.js</c>. The suite runs no JavaScript, so
/// these assertions read the shipped script text — the same approach as
/// <c>DashboardActivityFeedTests.DashboardSite_OwnsTheSortAndFilterControllers</c>. Patterns tolerate
/// whitespace so reformatting the file does not fail the build.
/// </summary>
public sealed class DashboardSiteScriptTests
{
    [Fact]
    public void UpdateRelativeTimes_WritesTheLabelOnlyWhenItChanged()
    {
        string body = FunctionBody(SiteScript(), "function updateRelativeTimes");

        Assert.Matches(@"if\s*\(\s*el\.textContent\s*!==\s*label\s*\)\s*\{\s*el\.textContent\s*=\s*label\s*;", body);
        Assert.Matches(@"el\.title\s*!==\s*stamp", body);
    }

    [Fact]
    public void UpdateRelativeTimes_LeavesTheHiddenTabGuardToItsCallers()
    {
        string body = FunctionBody(SiteScript(), "function updateRelativeTimes");

        Assert.DoesNotContain("hidden", body, StringComparison.Ordinal);
    }

    [Fact]
    public void RelativeTimeInterval_DoesNoWorkWhileTheTabIsHidden()
    {
        string js = SiteScript();
        string body = FunctionBody(js, "window.setInterval");

        Assert.Matches(@"if\s*\(\s*document\.hidden\s*\)\s*\{\s*return\s*;\s*\}", body);
        Assert.Matches(@"updateRelativeTimes\(\s*document\s*\)", body);
        Assert.Matches(@"\}\s*,\s*5000\s*\)", js);
    }

    [Fact]
    public void VisibilityChange_RepaintsRelativeTimesWhenTheTabReturns()
    {
        string body = FunctionBody(SiteScript(), "document.addEventListener('visibilitychange'");

        Assert.Matches(@"applyVisibilityPolling\(\s*\)", body);
        Assert.Matches(
            @"if\s*\(\s*!\s*document\.hidden\s*\)\s*\{\s*updateRelativeTimes\(\s*document\s*\)\s*;",
            body);
    }

    [Fact]
    public void RehydrateSortableTables_TakesTheSwappedSubtreeAsItsScope()
    {
        string js = SiteScript();
        string body = FunctionBody(js, "function rehydrateSortableTables");

        Assert.Matches(@"function\s+rehydrateSortableTables\s*\(\s*scope\s*\)", js);
        Assert.True(
            Regex.Matches(body, @"panelInScope\(").Count >= 2,
            "both the sortable-table loop and the workspace-index tail must be scope-guarded");
    }

    [Fact]
    public void AfterSwap_RehydratesOnlyTheSwappedSubtree()
    {
        string body = FunctionBody(SiteScript(), "document.addEventListener('htmx:afterSwap'");

        Assert.Matches(@"rehydrateSortableTables\(\s*event\.target\s*\)", body);
    }

    [Fact]
    public void FirstPaint_RehydratesTheWholeDocument()
    {
        string body = FunctionBody(SiteScript(), "document.addEventListener('DOMContentLoaded'");

        Assert.Matches(@"rehydrateSortableTables\(\s*\)", body);
    }

    [Fact]
    public void Idiomorph_IgnoresTheActiveInputValueBeforeAnySwapCanFire()
    {
        string js = SiteScript();

        Assert.Matches(@"typeof\s+Idiomorph\s*!==\s*['""]undefined['""]", js);
        Assert.Matches(@"Idiomorph\.defaults\.ignoreActiveValue\s*=\s*true\s*;", js);

        int config = js.IndexOf("ignoreActiveValue", StringComparison.Ordinal);
        int firstListener = js.IndexOf("document.addEventListener", StringComparison.Ordinal);
        Assert.True(config >= 0 && config < firstListener);
    }

    [Fact]
    public void WorkspaceFilterValue_IsStillRestoredForABlurredInput()
    {
        string body = FunctionBody(SiteScript(), "function rehydrateSortableTables");

        Assert.Matches(
            @"filter\.value\s*!==\s*workspaceIndexState\.query\s*\)\s*\{\s*filter\.value\s*=\s*workspaceIndexState\.query\s*;",
            body);
    }

    private static string SiteScript() =>
        File.ReadAllText(Path.Combine(
            ScaleTestSupport.RepoRoot(),
            "src",
            "Miller.Dashboard",
            "wwwroot",
            "js",
            "dashboard-site.js"));

    /// <summary>
    /// Returns the brace-balanced body that follows <paramref name="signature"/>. The script carries no
    /// braces inside string or regex literals, so counting braces is exact here.
    /// </summary>
    private static string FunctionBody(string js, string signature)
    {
        int start = js.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature + " is missing from dashboard-site.js");

        int open = js.IndexOf('{', start);
        Assert.True(open >= 0, signature + " has no body");

        int depth = 0;
        for (int i = open; i < js.Length; i++)
        {
            if (js[i] == '{')
            {
                depth++;
            }
            else if (js[i] == '}' && --depth == 0)
            {
                return js.Substring(open + 1, i - open - 1);
            }
        }

        throw new InvalidOperationException(signature + " has no closing brace");
    }
}
