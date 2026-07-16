using Xunit;

namespace Miller.Tests.Conventions;

/// <summary>
/// The drift guard for the inline-signature render width. <c>search</c>, <c>inspect</c>, and <c>context</c> all
/// truncate signatures into the SAME compact output an agent reads, so the width is a cross-tool contract rather
/// than three private preferences: if one renderer's copy is tuned and the others are not, identical symbols
/// render at different widths depending on which tool produced the line, and nothing fails.
///
/// The guard asserts the value has exactly ONE home (<see cref="Miller.Server.Tools.ToolRenderLimits"/>) and that
/// each of the three renderers references it by name, so a future edit lands in one place or not at all.
///
/// This is a SOURCE scan, mirroring <see cref="ScaleTraitConventionTests"/>: "no second copy of this literal
/// exists" is a property of the text, not of the compiled metadata a reflection test could read.
///
/// <para>It filters comments per LINE rather than reusing that guard's character-wise stripper, which does not
/// track string literals: <c>SearchTool</c>'s <c>file_pattern</c> description contains the glob <c>src/ui/**</c>,
/// whose <c>/*</c> sends a character-wise stripper into block-comment mode and silently swallows the rest of the
/// file — including the very references this guard exists to find. Declarations and references always sit on
/// their own code lines, so dropping comment-led lines is both sufficient and immune to that trap.</para>
/// </summary>
public sealed class SignatureMaxLengthConventionTests
{
    private const string ConstName = "SignatureMaxLength";
    private const string SharedHome = "ToolRenderLimits.cs";
    private const string SharedReference = "ToolRenderLimits." + ConstName;

    private static readonly string[] Renderers = ["SearchTool.cs", "InspectTool.cs", "ContextTool.cs"];

    [Fact]
    public void SignatureMaxLength_IsDeclaredExactlyOnce_SoTheThreeRenderersCannotDrift()
    {
        var declaringFiles = ServerSources()
            .Where(path => DeclaresConst(CodeOf(path)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal([SharedHome], declaringFiles);
    }

    [Theory]
    [InlineData("SearchTool.cs")]
    [InlineData("InspectTool.cs")]
    [InlineData("ContextTool.cs")]
    public void EachCompactRenderer_ReferencesTheSharedConst(string renderer)
    {
        string code = CodeOf(Assert.Single(ServerSources(), p => Path.GetFileName(p) == renderer));

        Assert.Contains(SharedReference, code, StringComparison.Ordinal);
        Assert.False(DeclaresConst(code), $"{renderer} still declares its own {ConstName}.");
    }

    [Fact]
    public void TheGuardsOwnCommentFilter_SurvivesAGlobLiteralContainingASlashStar()
    {
        string code = CodeOf(Assert.Single(ServerSources(), static p => Path.GetFileName(p) == "SearchTool.cs"));

        Assert.Contains("src/ui/**", code, StringComparison.Ordinal);
        Assert.Contains(SharedReference, code, StringComparison.Ordinal);
    }

    private static List<string> ServerSources()
    {
        string serverRoot = Path.Combine(ScaleTestSupport.RepoRoot(), "src", "Miller.Server");
        Assert.True(Directory.Exists(serverRoot), $"Could not locate the server source root at '{serverRoot}'.");

        var sources = Directory.EnumerateFiles(serverRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static p => !IsUnderBinOrObj(p))
            .ToList();

        Assert.True(sources.Count > 10,
            $"Expected to scan the Miller.Server sources but found only {sources.Count} .cs files under " +
            $"'{serverRoot}'. This guard must not pass vacuously.");

        foreach (string renderer in Renderers)
        {
            Assert.True(
                sources.Any(p => Path.GetFileName(p) == renderer),
                $"The guard did not find '{renderer}'. It was renamed or moved without updating this guard.");
        }

        return sources;
    }

    private static bool DeclaresConst(string code) =>
        code.Contains("const int " + ConstName, StringComparison.Ordinal);

    private static bool IsUnderBinOrObj(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.Ordinal)
            || normalized.Contains("/obj/", StringComparison.Ordinal);
    }

    private static string CodeOf(string path) =>
        string.Join('\n', File.ReadAllLines(path).Where(static line => !IsCommentLine(line)));

    private static bool IsCommentLine(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("/*", StringComparison.Ordinal)
            || trimmed.StartsWith('*');
    }
}
