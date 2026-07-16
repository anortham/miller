using System.Reflection;

namespace Miller.Server.Cli;

/// <summary>
/// Renders the Miller routing block for the <c>miller rules</c> verb. The block is embedded from
/// <c>hooks/miller-routing-block.md</c> at build time (see Miller.Server.csproj) — a release archive ships no
/// repo checkout, so the verb reads the compiled assembly, never the filesystem.
///
/// <para>This is the instruction tier of guidance delivery: harnesses with no plugin/hook support still get the
/// routing table by pasting it into the file they always load. Each supported harness's file format was verified
/// against that harness's official documentation and is recorded in
/// <c>docs/contracts/rules-v1.md</c>; a harness whose format could not be verified is dropped rather than
/// guessed. Rendering is print-only — Miller never writes into a user's project.</para>
/// </summary>
public static class RulesRender
{
    private const string ResourceSuffix = "miller-routing-block.md";

    /// <summary>A harness that can carry the routing block, with the file it goes in and how to frame it.</summary>
    /// <param name="Name">The <c>--harness</c> selector.</param>
    /// <param name="TargetPath">Where the rendered content belongs, relative to the project root.</param>
    /// <param name="Note">One-line placement guidance printed alongside the target path.</param>
    /// <param name="Frame">Wraps the routing block in the harness's file format.</param>
    public sealed record Harness(string Name, string TargetPath, string Note, Func<string, string> Frame);

    private static readonly IReadOnlyList<Harness> Supported =
    [
        new("cursor", ".cursor/rules/miller.mdc",
            "Create the file; alwaysApply keeps the block in every chat session.",
            static block => $"---\nalwaysApply: true\n---\n\n{block}"),
        new("windsurf", ".windsurf/rules/miller.md",
            "Current Devin Desktop builds also read .devin/rules/; .windsurf/rules/ stays supported.",
            static block => $"---\ntrigger: always_on\n---\n\n{block}"),
        new("cline", ".clinerules/miller.md",
            "Create the file; rules without frontmatter are always active.",
            static block => block),
        new("kiro", ".kiro/steering/miller.md",
            "Create the file; inclusion: always keeps the block in every interaction.",
            static block => $"---\ninclusion: always\n---\n\n{block}"),
        new("copilot", ".github/copilot-instructions.md",
            "Create the file, or append the block if it already exists.",
            static block => block),
        new("agents", "AGENTS.md",
            "Append the block to the repository-root AGENTS.md, or create it.",
            static block => block),
    ];

    /// <summary>The pinned harness list, in <c>--harness</c> listing order.</summary>
    public static IReadOnlyList<Harness> SupportedHarnesses => Supported;

    /// <summary>The harness names as a usage-syntax alternation, e.g. <c>cursor|windsurf|…</c>.</summary>
    public static string HarnessChoices => string.Join('|', Supported.Select(static h => h.Name));

    /// <summary>The harness names as a readable list, e.g. <c>cursor, windsurf, …</c>.</summary>
    public static string HarnessNames => string.Join(", ", Supported.Select(static h => h.Name));

    /// <summary>
    /// The routing block embedded in this assembly. Throws <see cref="InvalidOperationException"/> if the
    /// resource is missing — a packaging error (the EmbeddedResource was dropped from the csproj) that should
    /// fail loudly rather than silently shipping a `rules` verb with nothing to print.
    /// </summary>
    public static string LoadRoutingBlock()
    {
        Assembly assembly = typeof(RulesRender).Assembly;
        string resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceSuffix}' not found in {assembly.GetName().Name}. " +
                "It must be declared as <EmbeddedResource> in Miller.Server.csproj.");

        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Could not open embedded resource stream '{resourceName}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n").TrimEnd('\n');
    }

    /// <summary>Looks up a supported harness by name, case-insensitively.</summary>
    public static Harness? FindHarness(string name) =>
        Supported.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The bare routing block, as <c>miller rules</c> prints it.</summary>
    public static string Render() => LoadRoutingBlock();

    /// <summary>The routing block framed in <paramref name="harness"/>'s file format.</summary>
    public static string Render(Harness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);
        return harness.Frame(LoadRoutingBlock());
    }
}
