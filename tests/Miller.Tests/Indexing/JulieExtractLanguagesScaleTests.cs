using System.Text.Json;
using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Server.Hosting;
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
    private readonly ITestOutputHelper _output;

    public JulieExtractLanguagesScaleTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void QuerySupportedExtensions_LiveBinary_ReturnsBroadMultiLanguageSet()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        var runner = new JulieExtractRunner(binary);

        IReadOnlySet<string>? extensions = runner.QuerySupportedExtensions();

        Assert.NotNull(extensions);
        // The catalog is intentionally broad; pin a generous floor (a new pin only ever grows
        // the set) and spot-check across language families so a one-language regression cannot hide.
        Assert.True(extensions!.Count >= 50, $"expected a broad catalog, got {extensions.Count} extensions");
        foreach (string expected in new[] { "cs", "rs", "ts", "py", "vue", "md", "sql", "yaml" })
            Assert.Contains(expected, extensions);
        // Normalization contract: dot-less, case-insensitive membership.
        Assert.DoesNotContain(".cs", extensions);
        Assert.Contains("CS", extensions);
    }

    [Fact]
    public void LanguagesJson_LiveBinary_ClassifiesEveryTestRoleExactlyOncePerLanguage()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        string json = ScaleTestSupport.RunJulie(binary, "languages", "--json");
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement languages = doc.RootElement.GetProperty("languages").GetProperty("languages");
        // Same generous-floor convention as the extension probe above: a new pin only ever grows the set.
        int languageCount = languages.GetArrayLength();
        Assert.True(languageCount >= 36, $"expected at least 36 languages, got {languageCount}");

        string[] expectedRoles = ["test_case", "test_container", "test_lifecycle"];
        int supportedCount = 0;
        int notApplicableCount = 0;
        int openGapCount = 0;
        foreach (JsonElement language in languages.EnumerateArray())
        {
            string name = language.GetProperty("language").GetString()!;
            JsonElement coverage = language.GetProperty("kind_coverage").GetProperty("test_detection");
            var classifications = expectedRoles.ToDictionary(static role => role, static _ => 0, StringComparer.Ordinal);

            foreach (string bucket in new[] { "supported", "not_applicable" })
            {
                foreach (JsonElement role in coverage.GetProperty(bucket).EnumerateArray())
                {
                    CountRole(name, bucket, role.GetString()!, classifications);
                    if (bucket == "supported") supportedCount++; else notApplicableCount++;
                }
            }

            foreach (JsonElement gap in coverage.GetProperty("open_gaps").EnumerateArray())
            {
                Assert.Equal(JsonValueKind.Object, gap.ValueKind);
                Assert.False(string.IsNullOrWhiteSpace(gap.GetProperty("reason").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(gap.GetProperty("required_closure").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(gap.GetProperty("planned_closure_task").GetString()));
                CountRole(name, "open_gaps", gap.GetProperty("kind").GetString()!, classifications);
                openGapCount++;
            }

            Assert.All(classifications, pair =>
                Assert.True(pair.Value == 1,
                    $"{name}.{pair.Key} must be classified exactly once, observed {pair.Value}"));
        }

        _output.WriteLine(
            "languages={0} role_cells={1} supported={2} not_applicable={3} open_gaps={4}",
            languageCount, languageCount * expectedRoles.Length,
            supportedCount, notApplicableCount, openGapCount);
    }

    [Fact]
    public void DiscoveryLimits_LiveBinary_MatchMillerMirroredConstants()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        string json = ScaleTestSupport.RunJulie(binary, "languages", "--json");
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement limits = doc.RootElement.GetProperty("languages").GetProperty("discovery_limits");

        Assert.Equal(
            ExtractSourceLimits.DefaultMaxSourceFileBytes,
            limits.GetProperty("max_source_file_bytes").GetInt64());

        string[] publishedSuffixes = limits.GetProperty("hard_exclude_suffixes")
            .EnumerateArray().Select(static s => s.GetString()!).ToArray();
        Assert.Equal(ExtractSourceLimits.HardExcludeSuffixes, publishedSuffixes);

        string[] publishedDirectories = limits.GetProperty("hard_exclude_directories")
            .EnumerateArray().Select(static s => s.GetString()!).ToArray();
        Assert.NotEmpty(publishedDirectories);
        string[] unwatched = publishedDirectories
            .Where(static d => !WatchPathFilter.SkippedDirectorySegments.Contains(d))
            .ToArray();
        Assert.True(unwatched.Length == 0,
            $"julie hard-excludes {string.Join(", ", unwatched)}; WatchPathFilter would still submit files there");

        _output.WriteLine(
            "max_source_file_bytes={0} suffixes={1} directories={2}",
            limits.GetProperty("max_source_file_bytes").GetInt64(),
            publishedSuffixes.Length, publishedDirectories.Length);
    }

    private static void CountRole(
        string language,
        string bucket,
        string role,
        Dictionary<string, int> classifications)
    {
        Assert.True(classifications.ContainsKey(role),
            $"{language}.{bucket} contains unknown test-detection role '{role}'");
        classifications[role]++;
    }

}
