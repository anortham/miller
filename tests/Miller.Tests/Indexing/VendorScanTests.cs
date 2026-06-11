using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the pure vendor-directory detection (the consumer-side port of julie's
/// <c>analyze_vendor_patterns()</c>) over FAKE trees — zero filesystem, zero subprocess. The heuristics under
/// test: vendor-NAMED dirs (or ancestors) with &gt;5 files recursively; jquery*/bootstrap* clusters and
/// minified-file concentration in arbitrarily named dirs; and — equally load-bearing — NO false positives on
/// ordinary source layouts (<c>src/</c>, <c>tests/</c>, <c>packages/</c>, <c>lib/</c>).
/// </summary>
public sealed class VendorScanTests
{
    [Fact]
    public void NodeModulesStyleTree_DetectsTheVendorRoot()
    {
        string[] tree =
        {
            "src/app.ts",
            "src/util.ts",
            "node_modules/lodash/index.js",
            "node_modules/lodash/fp.js",
            "node_modules/react/index.js",
            "node_modules/react/cjs/react.development.js",
            "node_modules/react/cjs/react.production.js",
            "node_modules/left-pad/index.js",
        };

        var detected = VendorScan.DetectVendorDirectories(tree);

        Assert.Equal(new[] { "node_modules" }, detected);
    }

    [Fact]
    public void VendorNamedAncestor_QualifiesEvenWhenFilesSitInSubdirectories()
    {
        // No directory holds >5 files DIRECTLY; the ancestor "third-party" holds 6 recursively.
        string[] tree =
        {
            "third-party/a/one.js",
            "third-party/a/two.js",
            "third-party/b/three.js",
            "third-party/b/four.js",
            "third-party/c/five.js",
            "third-party/c/six.js",
            "src/main.cs",
        };

        var detected = VendorScan.DetectVendorDirectories(tree);

        Assert.Equal(new[] { "third-party" }, detected);
    }

    [Fact]
    public void MinifiedCluster_DetectsAnUnusuallyNamedLibraryDir()
    {
        // "assets/js" is not a vendor NAME, but 11 of its 12 files are .min.* — julie's medium-confidence
        // signal (minified > 10 and more than half the dir).
        var tree = new List<string> { "assets/js/site.js" };
        for (int i = 0; i < 11; i++)
            tree.Add($"assets/js/lib{i}.min.js");
        tree.Add("src/main.cs");

        var detected = VendorScan.DetectVendorDirectories(tree);

        Assert.Equal(new[] { "assets/js" }, detected);
    }

    [Fact]
    public void JqueryCluster_DetectsALibraryDir()
    {
        string[] tree =
        {
            "wwwroot/scripts/jquery.js",
            "wwwroot/scripts/jquery.validate.js",
            "wwwroot/scripts/jquery.ui.js",
            "wwwroot/scripts/jquery.cookie.js",
            "src/Pages/Index.cshtml",
        };

        var detected = VendorScan.DetectVendorDirectories(tree);

        Assert.Equal(new[] { "wwwroot/scripts" }, detected);
    }

    [Fact]
    public void CleanRepo_NoFalsePositives_OnSourceAndTestLayouts()
    {
        string[] tree =
        {
            "src/Miller.Core/Search/Bm25.cs",
            "src/Miller.Core/Search/Tokenizer.cs",
            "src/Miller.Server/Program.cs",
            "src/Miller.Server/Tools/SearchTool.cs",
            "src/Miller.Server/Tools/InspectTool.cs",
            "src/Miller.Server/Tools/WorkspaceTool.cs",
            "tests/Miller.Tests/SearchTests.cs",
            "tests/Miller.Tests/InspectTests.cs",
            "tests/Miller.Tests/WorkspaceTests.cs",
            "tests/Miller.Tests/Conventions/GuardTests.cs",
            "packages/app/index.ts",        // monorepo layout — NOT vendor (julie's own carve-out)
            "lib/runner.rb",                // Ruby/Elixir source dir — NOT vendor
            "bin/install.sh",               // user CLI scripts — NOT vendor
            "README.md",
        };

        Assert.Empty(VendorScan.DetectVendorDirectories(tree));
    }

    [Fact]
    public void VendorNamedDir_AtOrBelowThreshold_IsNotDetected()
    {
        // Exactly 5 files = not "more than 5" — julie's threshold is strict.
        string[] tree =
        {
            "out/a.js", "out/b.js", "out/c.js", "out/d.js", "out/e.js",
            "src/main.cs",
        };

        Assert.Empty(VendorScan.DetectVendorDirectories(tree));
    }

    [Fact]
    public void Output_IsSortedDeduplicated_WithForwardSlashes_AcceptingEitherInputSeparator()
    {
        var tree = new List<string>();
        for (int i = 0; i < 6; i++)
            tree.Add($@"vendor\lib{i}.php");     // backslash input (Windows watcher paths)
        for (int i = 0; i < 6; i++)
            tree.Add($"build/js/gen{i}.js");

        var detected = VendorScan.DetectVendorDirectories(tree);

        Assert.Equal(new[] { "build", "vendor" }, detected);
    }

    [Fact]
    public void BaselinePatterns_AlwaysRecommendLogExclusion()
    {
        // Logs are index noise (julie parses none; Miller has a dedicated log-scan tool) — *.log is the
        // always-on baseline regardless of detection.
        Assert.Contains("*.log", VendorScan.BaselinePatterns);
    }
}
