using System.Text.Json;
using Miller.SearchQuality;
using Xunit;

namespace Miller.Tests.SearchQuality;

public sealed class SearchQualityCliTests : IDisposable
{
    private readonly string _dir;

    public SearchQualityCliTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-search-quality-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Init_WritesStarterSuiteToRequestedPath()
    {
        string casesPath = Path.Combine(_dir, "cases.json");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = SearchQualityCli.Run(["init", "--cases", casesPath], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(casesPath));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(casesPath));
        Assert.Equal(3, document.RootElement.GetProperty("repositories").GetArrayLength());
        Assert.True(document.RootElement.GetProperty("cases").GetArrayLength() >= 6);
        Assert.Contains(casesPath, stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Init_RefusesToOverwriteWithoutForce()
    {
        string casesPath = Path.Combine(_dir, "cases.json");
        File.WriteAllText(casesPath, "{}");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = SearchQualityCli.Run(["init", "--cases", casesPath], stdout, stderr);

        Assert.Equal(1, exitCode);
        Assert.Equal("{}", File.ReadAllText(casesPath));
        Assert.Contains("--force", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveDefaultJulieCommand_PrefersReleaseBinaryOverDebugBinary()
    {
        string julieRoot = Path.Combine(_dir, "julie");
        string release = Path.Combine(julieRoot, "target", "release", "julie-server");
        string debug = Path.Combine(julieRoot, "target", "debug", "julie-server");
        Directory.CreateDirectory(Path.GetDirectoryName(release)!);
        Directory.CreateDirectory(Path.GetDirectoryName(debug)!);
        File.WriteAllText(release, "");
        File.WriteAllText(debug, "");

        string command = SearchQualityCli.ResolveDefaultJulieCommand(julieRoot);

        Assert.Equal(release, command);
    }

    [Fact]
    public void BuildJulieArgs_IncludesSupportedSearchFilters()
    {
        var repo = new RepositorySpec { Name = "openclaw", Root = _dir };
        var searchCase = new SearchCaseSpec
        {
            Id = "case",
            Repository = "openclaw",
            Query = "media/server",
            Mode = "file",
            Language = "typescript",
            FilePattern = "src/media/**",
            ExcludeTests = true,
        };

        IReadOnlyList<string> args = SearchQualityCli.BuildJulieArgs(repo, searchCase, limit: 5);

        Assert.Contains("--language", args);
        Assert.Contains("typescript", args);
        Assert.Contains("--file-pattern", args);
        Assert.Contains("src/media/**", args);
        Assert.Contains("--exclude-tests", args);
    }
}
