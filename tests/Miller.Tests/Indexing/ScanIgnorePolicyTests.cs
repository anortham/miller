using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins Miller's own <c>--ignore-file</c> contribution: the generated invariant file, always last, exclusions
/// only, on scans and on single-file updates. Nothing user-authored is ever routed this way — that is the
/// seeder's in-tree copy (<c>JulieIgnoreSeederTests</c>). Tiny temp trees — fast suite, no subprocess.
/// </summary>
public sealed class ScanIgnorePolicyTests : IDisposable
{
    private readonly string _dir;

    public ScanIgnorePolicyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-scan-ignore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string NewDirectory(params string[] segments)
    {
        string path = Path.Combine(new[] { _dir }.Concat(segments).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void InvariantPatternsCoverTheDirectoriesJulieExtractDoesNotHardExclude()
    {
        Assert.Contains(".miller/", ScanIgnorePolicy.InvariantPatterns);
        Assert.Contains(".worktrees/", ScanIgnorePolicy.InvariantPatterns);
        Assert.Contains(".claude/worktrees/", ScanIgnorePolicy.InvariantPatterns);
    }

    [Fact]
    public void InvariantContentCarriesNoWhitelistPattern()
    {
        string content = ScanIgnorePolicy.RenderInvariantContent();

        foreach (string line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Assert.False(line.TrimStart().StartsWith('!'), $"whitelist pattern in the invariant file: '{line}'");
    }

    [Fact]
    public void InvariantContentIsPatternsAndCommentsOnly()
    {
        string content = ScanIgnorePolicy.RenderInvariantContent();

        foreach (string line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Assert.True(
                line.StartsWith('#') || ScanIgnorePolicy.InvariantPatterns.Contains(line),
                $"unexpected non-pattern line: '{line}'");
    }

    [Fact]
    public void PrepareForScanWritesTheInvariantFileUnderTheMillerDirectory()
    {
        string root = NewDirectory("plain");

        IReadOnlyList<string> paths = ScanIgnorePolicy.PrepareForScan(root);

        string expected = Path.Combine(root, ".miller", "invariant.julieignore");
        Assert.Equal(new[] { expected }, paths);
        Assert.Equal(ScanIgnorePolicy.RenderInvariantContent(), File.ReadAllText(expected));
    }

    [Fact]
    public void PrepareForScanRewritesTheInvariantFileAndDiscardsUserEdits()
    {
        string root = NewDirectory("edited");
        ScanIgnorePolicy.PrepareForScan(root);
        string path = ScanIgnorePolicy.InvariantIgnorePathFor(root);
        File.WriteAllText(path, "!.worktrees/\n");

        ScanIgnorePolicy.PrepareForScan(root);

        Assert.Equal(ScanIgnorePolicy.RenderInvariantContent(), File.ReadAllText(path));
    }

    [Fact]
    public void PrepareForScanOnAMissingRootWritesNothingAndDoesNotCreateTheRoot()
    {
        string missing = Path.Combine(_dir, "not-there");

        Assert.Empty(ScanIgnorePolicy.PrepareForScan(missing));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void ForFileUpdateCarriesTheInvariantFileAScanAlreadyWrote()
    {
        string root = NewDirectory("scanned");
        ScanIgnorePolicy.PrepareForScan(root);

        Assert.Equal(
            new[] { ScanIgnorePolicy.InvariantIgnorePathFor(root) }, ScanIgnorePolicy.ForFileUpdate(root));
    }

    [Fact]
    public void ForFileUpdateBeforeAnyScanCarriesNothingAndWritesNothing()
    {
        string root = NewDirectory("unscanned");

        Assert.Empty(ScanIgnorePolicy.ForFileUpdate(root));
        Assert.False(File.Exists(ScanIgnorePolicy.InvariantIgnorePathFor(root)));
    }
}
