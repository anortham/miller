namespace Miller.Testing;

public sealed record ContinuousTestProjectWorkItem(
    ContinuousTestProject Project,
    ContinuousTestWorkspace Workspace);

public static class ContinuousTestProjectInventory
{
    public static IReadOnlyList<ContinuousTestWorkspace> MaterializeWorkspaces(
        IEnumerable<ContinuousTestProject> projects,
        string workspaceRoot) =>
        MaterializeProjectWorkItems(projects, workspaceRoot)
            .Select(row => row.Workspace)
            .ToArray();

    public static IReadOnlyList<ContinuousTestProjectWorkItem> MaterializeProjectWorkItems(
        IEnumerable<ContinuousTestProject> projects,
        string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(projects);
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("must not be empty", nameof(workspaceRoot));

        string root = Path.GetFullPath(workspaceRoot);
        var workItems = new List<ContinuousTestProjectWorkItem>();
        foreach (ContinuousTestProject project in projects.Where(static project => project.Enabled))
        {
            string projectPath = Path.GetFullPath(project.ProjectPath);
            if (!IsInside(root, projectPath))
            {
                throw new ArgumentException(
                    "continuous test project path must live inside the workspace root",
                    nameof(ContinuousTestProject.ProjectPath));
            }

            string buildRoot = Path.Combine(
                CtTempPaths.Root,
                "build",
                SafeSegment(project.WorkspaceId),
                SafeSegment(project.Id));
            var workspace = new ContinuousTestWorkspace(
                WorkspaceId: project.WorkspaceId,
                WorkspaceRoot: root,
                ProjectPath: projectPath,
                BuildOutputRoot: buildRoot,
                Framework: project.Framework,
                Command: project.Command,
                ExcludeTraits: project.ExcludeTraits,
                Metadata: project.Metadata);
            workItems.Add(new ContinuousTestProjectWorkItem(project, workspace));
        }

        return workItems;
    }

    private static string SafeSegment(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '-').ToArray();
        string segment = new(chars);
        return string.IsNullOrWhiteSpace(segment) ? "project" : segment.Trim('-');
    }

    private static bool IsInside(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return relative == "."
            || (!relative.StartsWith("..", PathComparison) && !Path.IsPathRooted(relative));
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
