using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.Engine;

public sealed class ContinuousTestProjectInventoryTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-inventory-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Materialize_keeps_build_output_outside_the_workspace()
    {
        string project = Path.Combine(_root, "src", "App.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, "<Project />");
        var items = ContinuousTestProjectInventory.MaterializeProjectWorkItems(
            [new ContinuousTestProject("proj:1", "ws:1", project, Framework: "xunit")],
            _root);

        ContinuousTestProjectWorkItem item = Assert.Single(items);
        Assert.Equal(Path.GetFullPath(project), item.Workspace.ProjectPath);
        Assert.False(IsInside(_root, item.Workspace.BuildOutputRoot));
        Assert.Contains("miller-ct", item.Workspace.BuildOutputRoot, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_projects_are_skipped()
    {
        string project = Path.Combine(_root, "src", "App.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, "<Project />");
        var items = ContinuousTestProjectInventory.MaterializeProjectWorkItems(
            [new ContinuousTestProject("proj:1", "ws:1", project, Enabled: false)],
            _root);
        Assert.Empty(items);
    }

    private static bool IsInside(string root, string path)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative == "."
            || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }
}
