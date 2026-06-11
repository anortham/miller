using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Live contract for the watcher gate's catalog probe: the REAL <c>julie-extract languages --json</c> fetch
/// (the only process-spawning piece of the supported-extension gate — the membership decision is pure and
/// fast-suite-pinned in <c>WatchPathFilterTests</c>/<c>JulieExtractRunnerTests</c>). Asserts the pinned
/// binary actually claims a broad multi-language set, so the gate never silently narrows to a subset.
/// </summary>
[Trait("Category", "Scale")]
public sealed class JulieExtractLanguagesScaleTests
{
    [Fact]
    public void QuerySupportedExtensions_LiveBinary_ReturnsBroadMultiLanguageSet()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        var runner = new JulieExtractRunner(binary);

        IReadOnlySet<string>? extensions = runner.QuerySupportedExtensions();

        Assert.NotNull(extensions);
        // The pinned 2.4.0 catalog claims 65 extensions; pin a generous floor (a new pin only ever grows
        // the set) and spot-check across language families so a one-language regression cannot hide.
        Assert.True(extensions!.Count >= 50, $"expected a broad catalog, got {extensions.Count} extensions");
        foreach (string expected in new[] { "cs", "rs", "ts", "py", "vue", "md", "sql", "yaml" })
            Assert.Contains(expected, extensions);
        // Normalization contract: dot-less, case-insensitive membership.
        Assert.DoesNotContain(".cs", extensions);
        Assert.Contains("CS", extensions);
    }
}
