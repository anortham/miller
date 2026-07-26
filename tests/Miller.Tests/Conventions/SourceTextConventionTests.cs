using System.Diagnostics;
using System.Text;
using Xunit;

namespace Miller.Tests.Conventions;

[Trait("Category", "Scale")]
public sealed class SourceTextConventionTests
{
    [Fact]
    public void TrackedTextFiles_DoNotContainRawControlBytes()
    {
        string repoRoot = ScaleTestSupport.RepoRoot();
        using var process = Process.Start(new ProcessStartInfo("git", "ls-files -z")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);

        string[] paths = Encoding.UTF8.GetString(output.ToArray())
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(IsTrackedTextPath)
            .Where(path => File.ReadAllBytes(Path.Combine(repoRoot, path)).Any(IsDisallowedControlByte))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            paths.Length == 0,
            "Tracked text files contain raw control bytes:\n  " + string.Join("\n  ", paths));
    }

    private static bool IsTrackedTextPath(string path) =>
        TextExtensions.Contains(Path.GetExtension(path)) ||
        TextFileNames.Contains(Path.GetFileName(path));

    private static bool IsDisallowedControlByte(byte value) =>
        value is < 0x20 and not (0x09 or 0x0A or 0x0D) or 0x7F;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".css", ".csv", ".editorconfig", ".html", ".js", ".json", ".jsonl",
        ".md", ".mjs", ".props", ".ps1", ".razor", ".sh", ".slnx", ".sql", ".targets", ".toml",
        ".ts", ".tsv", ".txt", ".xml", ".yaml", ".yml",
    };

    private static readonly HashSet<string> TextFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".editorconfig", ".gitattributes", ".gitignore", "Dockerfile", "LICENSE",
    };
}
