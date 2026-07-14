using Xunit;

namespace Miller.Tests.Conventions;

public sealed class SourceTextConventionTests
{
    [Fact]
    public void CSharpSourceFiles_DoNotContainNulBytes()
    {
        string sourceRoot = Path.Combine(ScaleTestSupport.RepoRoot(), "src");
        string[] paths = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !ContainsDirectory(path, "bin") && !ContainsDirectory(path, "obj"))
            .Where(path => Array.IndexOf(File.ReadAllBytes(path), (byte)0) >= 0)
            .Select(path => Path.GetRelativePath(ScaleTestSupport.RepoRoot(), path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(paths.Length == 0, "C# source files contain NUL bytes:\n  " + string.Join("\n  ", paths));
    }

    private static bool ContainsDirectory(string path, string directoryName) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains(directoryName, StringComparer.OrdinalIgnoreCase);
}
