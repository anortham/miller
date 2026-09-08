using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Providers.Node;

public sealed class VitestExactFileRecipeTests
{
    [Theory]
    [InlineData("/repo", "/repo")]
    [InlineData("C:/repo", "C:/repo")]
    public void Exact_file_globs_preserve_root_and_escape_each_path_segment(string root, string prefix)
    {
        IReadOnlyList<string> globs = JavaScriptTestProvider.VitestExactFileExclusions(root, "nested [x] (y) !,/mapped [x] (y) !,.test.ts");
        Assert.Equal([
            prefix + "/!(nested \\[x\\] \\(y\\) \\!,)",
            prefix + "/nested \\[x\\] \\(y\\) \\!,/!(mapped \\[x\\] \\(y\\) \\!,.test.ts)",
        ], globs);
    }
}
