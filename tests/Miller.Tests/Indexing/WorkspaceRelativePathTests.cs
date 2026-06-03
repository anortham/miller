using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the workspace trust boundary (§7): a julie root-relative path resolves to an absolute path
/// under the workspace root, and a rooted or root-escaping path resolves to null so a corrupt/tampered
/// artifact can never disclose a file outside the workspace. Pure path math — no disk access.
/// </summary>
public sealed class WorkspaceRelativePathTests
{
    private static readonly string Root =
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "miller-wrp-root"));

    [Fact]
    public void ResolveUnderRoot_NestedRelativePath_ReturnsAbsoluteUnderRoot()
    {
        string? abs = WorkspaceRelativePath.ResolveUnderRoot(Root, "docs/guide.md");

        Assert.Equal(Path.Combine(Root, "docs", "guide.md"), abs);
    }

    [Fact]
    public void ResolveUnderRoot_RootedPath_ReturnsNull()
    {
        string rooted = Path.Combine(Root, "x.md"); // absolute

        Assert.Null(WorkspaceRelativePath.ResolveUnderRoot(Root, rooted));
    }

    [Fact]
    public void ResolveUnderRoot_ParentEscape_ReturnsNull()
    {
        Assert.Null(WorkspaceRelativePath.ResolveUnderRoot(Root, Path.Combine("..", "evil.md")));
    }

    [Fact]
    public void ResolveUnderRoot_SiblingPrefixAttack_ReturnsNull()
    {
        // root ".../miller-wrp-root"; "../miller-wrp-root-evil/secret" must NOT count as under root.
        string rel = Path.Combine("..", "miller-wrp-root-evil", "secret");

        Assert.Null(WorkspaceRelativePath.ResolveUnderRoot(Root, rel));
    }

    [Fact]
    public void ResolveUnderRoot_DotDotThatStaysUnderRoot_ReturnsAbsolute()
    {
        string? abs = WorkspaceRelativePath.ResolveUnderRoot(Root, Path.Combine("a", "..", "b.md"));

        Assert.Equal(Path.Combine(Root, "b.md"), abs);
    }
}
