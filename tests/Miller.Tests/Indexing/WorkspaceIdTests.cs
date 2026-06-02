using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class WorkspaceIdTests
{
    [Fact]
    public void FromCanonicalRoot_ReturnsStableSha256Hex()
    {
        string id = WorkspaceId.FromCanonicalRoot("/abs/work/repo");

        Assert.Equal("a0efc97f7ea34ca9673db9e8a54459b869b3de0f386f8140de8177c6b947a311", id);
        Assert.Equal(64, id.Length);
        Assert.Matches("^[0-9a-f]{64}$", id);
    }

    [Fact]
    public void FromCanonicalRoot_DifferentCanonicalRoots_ProduceDifferentIds()
    {
        string first = WorkspaceId.FromCanonicalRoot("/abs/work/repo");
        string second = WorkspaceId.FromCanonicalRoot("/abs/work/other");

        Assert.NotEqual(first, second);
        Assert.Equal("d212ea6b079be327899802dc85e6aadabe27e942ffdec4d734add2867b2af5f0", second);
    }

    [Fact]
    public void Display_UsesSanitizedLeafAndShortHashPrefix()
    {
        const string id = "a0efc97f7ea34ca9673db9e8a54459b869b3de0f386f8140de8177c6b947a311";

        string display = WorkspaceId.Display("/abs/work/My Repo! #1", id);

        Assert.Equal("My-Repo-1-a0efc97f7ea3", display);
    }

    [Fact]
    public void FromCanonicalRoot_IsPureFunctionOfRoot_NotDerivedFromAnyArtifact()
    {
        // v1 julie-extract stores artifact_id + root_path in artifact_metadata; it does NOT echo a
        // workspace_id. Miller's workspace_id is its OWN registry identity, derived solely from the
        // canonical root, never read back from a julie DB. Same root in, same id out, every time.
        const string root = "/abs/work/repo";
        string a = WorkspaceId.FromCanonicalRoot(root);
        string b = WorkspaceId.FromCanonicalRoot(root);
        Assert.Equal(a, b);
        Assert.Equal("a0efc97f7ea34ca9673db9e8a54459b869b3de0f386f8140de8177c6b947a311", a);
    }
}
