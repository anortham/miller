using System.Reflection;
using Miller.Server;
using Miller.Server.Cli;
using Xunit;

namespace Miller.Tests.Server.Cli;

/// <summary>
/// Pins the <c>miller rules</c> verb (T6.1): the routing block ships as an embedded resource, so the verb prints
/// identically from a release archive with no repo checkout, and — like <c>version</c> — it renders without
/// touching a workspace or index. Each <c>--harness</c> variant's file format is verified against that harness's
/// official docs and recorded in <c>docs/contracts/rules-v1.md</c>.
/// </summary>
public sealed class RulesCliTests
{
    private const string RoutingBlockFragment = "One Miller call beats shell greps and full-file reads";

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CliDispatch.Run(args, NoWorkspaceContext(), stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    // Every path points into a directory that does not exist: a verb that hydrated a workspace would fail here,
    // so a clean exit 0 is the proof that `rules` dispatches above index loading.
    private static WorkspaceContext NoWorkspaceContext()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-rules-no-such-workspace");
        return new WorkspaceContext(
            root,
            Path.Combine(root, ".miller", "symbols.db"),
            Path.Combine(root, "telemetry.db"),
            Path.Combine(root, "workspaces.db"),
            Path.Combine(root, ".tools"),
            WorkspaceId: null);
    }

    [Fact]
    public void Rules_PrintsTheEmbeddedRoutingBlock()
    {
        var (code, outText, errText) = Run("rules");

        Assert.Equal(0, code);
        Assert.Empty(errText);
        Assert.Contains(RoutingBlockFragment, outText);
        Assert.Contains("- search — ", outText);
        Assert.Contains("- workspace — ", outText);
    }

    [Fact]
    public void Rules_LoadsFromTheCompiledAssembly_NotTheRepoFile()
    {
        Assembly assembly = typeof(RulesRender).Assembly;

        Assert.Contains(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith("miller-routing-block.md", StringComparison.Ordinal));
        Assert.Contains(RoutingBlockFragment, RulesRender.LoadRoutingBlock());
    }

    [Fact]
    public void Rules_RendersWithoutLoadingAnIndex()
    {
        var (code, outText, _) = Run("rules");

        Assert.Equal(0, code);
        Assert.Contains(RoutingBlockFragment, outText);
    }

    [Theory]
    [InlineData("cursor", ".cursor/rules/miller.mdc", "---\nalwaysApply: true\n---")]
    [InlineData("windsurf", ".windsurf/rules/miller.md", "---\ntrigger: always_on\n---")]
    [InlineData("kiro", ".kiro/steering/miller.md", "---\ninclusion: always\n---")]
    public void Rules_Harness_FramesTheBlockInTheHarnessFormat(string harness, string targetPath, string frontmatter)
    {
        var (code, outText, errText) = Run("rules", "--harness", harness);

        Assert.Equal(0, code);
        Assert.StartsWith(frontmatter, outText.ReplaceLineEndings("\n"));
        Assert.Contains(RoutingBlockFragment, outText);
        Assert.Contains(targetPath, errText);
    }

    [Theory]
    [InlineData("cline", ".clinerules/miller.md")]
    [InlineData("copilot", ".github/copilot-instructions.md")]
    [InlineData("agents", "AGENTS.md")]
    public void Rules_PlainMarkdownHarness_EmitsTheBlockUnwrapped(string harness, string targetPath)
    {
        var (code, outText, errText) = Run("rules", "--harness", harness);

        Assert.Equal(0, code);
        Assert.StartsWith("# Miller", outText.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("---\n", outText.ReplaceLineEndings("\n")[..40]);
        Assert.Contains(targetPath, errText);
    }

    [Fact]
    public void Rules_Harness_KeepsStdoutFreeOfThePathNote()
    {
        var (_, outText, errText) = Run("rules", "--harness", "cursor");

        Assert.DoesNotContain("write to:", outText);
        Assert.Contains("write to:", errText);
    }

    [Fact]
    public void Rules_EverySupportedHarness_RendersAndCarriesTheBlock()
    {
        foreach (RulesRender.Harness harness in RulesRender.SupportedHarnesses)
        {
            var (code, outText, errText) = Run("rules", "--harness", harness.Name);

            Assert.Equal(0, code);
            Assert.Contains(RoutingBlockFragment, outText);
            Assert.Contains(harness.TargetPath, errText);
        }
    }

    [Fact]
    public void Rules_UnknownHarness_IsAUsageError()
    {
        var (code, outText, errText) = Run("rules", "--harness", "notepad");

        Assert.Equal(2, code);
        Assert.Empty(outText);
        Assert.Contains("notepad", errText);
        Assert.Contains("cursor", errText);
    }

    [Fact]
    public void Rules_Usage_ListsHarnessesAsAnAlternation()
    {
        var (_, _, errText) = Run("rules", "--harness", "notepad");

        Assert.Contains("usage: miller rules [--harness cursor|windsurf|cline|kiro|copilot|agents]", errText);
    }

    [Fact]
    public void Rules_HelpText_DocumentsTheVerbAndEveryHarness()
    {
        var (code, outText, _) = Run("help");

        Assert.Equal(0, code);
        Assert.Contains("rules", outText);
        Assert.Contains($"[--harness {RulesRender.HarnessChoices}]", outText);
    }

    [Fact]
    public void Rules_HarnessWithoutAValue_IsAUsageError()
    {
        var (code, outText, _) = Run("rules", "--harness");

        Assert.Equal(2, code);
        Assert.Empty(outText);
    }

    [Fact]
    public void Rules_PositionalArgument_IsAUsageError()
    {
        var (code, outText, _) = Run("rules", "cursor");

        Assert.Equal(2, code);
        Assert.Empty(outText);
    }

    [Fact]
    public void Rules_IsACliInvocation()
    {
        Assert.True(CliDispatch.IsCliInvocation(new[] { "rules" }));
    }
}
