namespace Miller.Testing;

public sealed record ContinuousTestProjectWorkItem(
    ContinuousTestProject Project,
    ContinuousTestWorkspace Workspace);

public static class ContinuousTestProjectInventory
{
    private static readonly HashSet<string> SkipDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".miller",
        "bin",
        "obj",
        "node_modules",
        "dist",
        "vendor",
        ".vs",
        "TestResults",
        "target",
        "__pycache__",
        ".venv",
        "venv",
        "packages",
    };

    private static readonly HashSet<string> DotnetProjectExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj",
        ".fsproj",
        ".vbproj",
    };

    private static readonly HashSet<string> PythonProjectNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "pyproject.toml",
        "pytest.ini",
        "tox.ini",
        "setup.cfg",
        "setup.py",
    };

    public static IReadOnlyList<ContinuousTestProject> Discover(string workspaceRoot, string workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        string root = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(root))
            return [];

        var projects = new List<ContinuousTestProject>();
        foreach (string path in EnumerateCandidateFiles(root))
        {
            if (!TryIdentify(path, out string? framework))
                continue;
            projects.Add(new ContinuousTestProject(
                Id: ProjectId(workspaceId, root, path),
                WorkspaceId: workspaceId,
                ProjectPath: path,
                Framework: framework));
        }

        return projects
            .OrderBy(project => project.ProjectPath, StringComparer.Ordinal)
            .ToArray();
    }

    public static ContinuousTestProject? Identify(string workspaceRoot, string workspaceId, string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        string full = Path.GetFullPath(projectPath);
        if (!File.Exists(full))
            return null;
        if (!TryIdentify(full, out string? framework))
            framework = FrameworkFallback(full);
        return new ContinuousTestProject(
            Id: ProjectId(workspaceId, workspaceRoot, full),
            WorkspaceId: workspaceId,
            ProjectPath: full,
            Framework: framework);
    }

    public static string ProjectId(string workspaceId, string workspaceRoot, string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        string relative = Path.GetRelativePath(Path.GetFullPath(workspaceRoot), Path.GetFullPath(projectPath))
            .Replace('\\', '/');
        return "ct-project:" + relative;
    }

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

    private static IEnumerable<string> EnumerateCandidateFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string file in files)
            {
                if (IsCandidateFileName(file))
                    yield return file;
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string child in children)
            {
                if (!SkipDirectoryNames.Contains(Path.GetFileName(child)))
                    pending.Push(child);
            }
        }
    }

    private static bool IsCandidateFileName(string path)
    {
        string name = Path.GetFileName(path);
        return DotnetProjectExtensions.Contains(Path.GetExtension(path))
            || string.Equals(name, "Cargo.toml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "package.json", StringComparison.OrdinalIgnoreCase)
            || PythonProjectNames.Contains(name);
    }

    private static bool TryIdentify(string path, out string? framework)
    {
        string name = Path.GetFileName(path);
        if (string.Equals(name, "Cargo.toml", StringComparison.OrdinalIgnoreCase))
        {
            framework = "cargo";
            return true;
        }

        if (PythonProjectNames.Contains(name))
        {
            framework = "pytest";
            return true;
        }

        if (string.Equals(name, "package.json", StringComparison.OrdinalIgnoreCase))
        {
            string text = ReadHead(path);
            if (ContainsToken(text, "vitest"))
            {
                framework = "vitest";
                return true;
            }

            if (ContainsToken(text, "jest"))
            {
                framework = "jest";
                return true;
            }

            framework = null;
            return false;
        }

        if (DotnetProjectExtensions.Contains(Path.GetExtension(path)))
        {
            string text = ReadHead(path);
            string fileName = Path.GetFileNameWithoutExtension(path);
            bool namedTest = fileName.Contains("Test", StringComparison.OrdinalIgnoreCase);
            bool sdkTest = ContainsToken(text, "Microsoft.NET.Sdk.Test")
                || ContainsToken(text, "Microsoft.NET.Test.Sdk");
            if (ContainsToken(text, "xunit"))
            {
                framework = "xunit";
                return true;
            }

            if (ContainsToken(text, "NUnit") || ContainsToken(text, "nunit"))
            {
                framework = "nunit";
                return true;
            }

            if (ContainsToken(text, "MSTest") || ContainsToken(text, "mstest"))
            {
                framework = "mstest";
                return true;
            }

            if (sdkTest || namedTest)
            {
                framework = "dotnet";
                return true;
            }

            framework = null;
            return false;
        }

        framework = null;
        return false;
    }

    private static string? FrameworkFallback(string path)
    {
        string name = Path.GetFileName(path);
        if (DotnetProjectExtensions.Contains(Path.GetExtension(path)))
            return "dotnet";
        if (string.Equals(name, "Cargo.toml", StringComparison.OrdinalIgnoreCase))
            return "cargo";
        if (string.Equals(name, "package.json", StringComparison.OrdinalIgnoreCase))
            return "node-test";
        if (PythonProjectNames.Contains(name))
            return "pytest";
        return null;
    }

    private static string ReadHead(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            int length = (int)Math.Min(stream.Length, 64 * 1024);
            var buffer = new byte[length];
            int read = stream.Read(buffer, 0, buffer.Length);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static bool ContainsToken(string text, string token) =>
        text.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return relative == "."
            || (!relative.StartsWith("..", PathComparison) && !Path.IsPathRooted(relative));
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
