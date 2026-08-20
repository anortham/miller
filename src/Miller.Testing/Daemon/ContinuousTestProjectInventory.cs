using System.Security.Cryptography;
using System.Text;
using Miller.Indexing;

namespace Miller.Testing;

public sealed record ContinuousTestProjectWorkItem(
    ContinuousTestProject Project,
    ContinuousTestWorkspace Workspace);

public static class ContinuousTestProjectInventory
{
    /// <summary>
    /// Width of every build-root path segment, matching the generation id's own hash width. See
    /// <see cref="ShortSegment"/> for why the segments are hashes and not the ids themselves.
    /// </summary>
    internal const int SegmentHashLength = 12;

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
            if (!TryIdentify(path, out string? framework, out IReadOnlyList<string> excludeTraits))
                continue;
            projects.Add(new ContinuousTestProject(
                Id: ProjectId(workspaceId, root, path),
                WorkspaceId: workspaceId,
                ProjectPath: path,
                Framework: framework,
                ExcludeTraits: excludeTraits));
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
        if (!TryIdentify(full, out string? framework, out IReadOnlyList<string> excludeTraits))
        {
            framework = FrameworkFallback(full);
            excludeTraits = DotnetProjectExtensions.Contains(Path.GetExtension(full))
                ? ParseDefaultFilterExclusions(ReadHead(full))
                : [];
        }
        return new ContinuousTestProject(
            Id: ProjectId(workspaceId, workspaceRoot, full),
            WorkspaceId: workspaceId,
            ProjectPath: full,
            Framework: framework,
            ExcludeTraits: excludeTraits);
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
                ShortSegment(project.WorkspaceId),
                ShortSegment(project.Id));
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

    /// <summary>
    /// One fixed-width path segment for an identifier of any length.
    ///
    /// Windows MAX_PATH is 260 characters and a machine without long paths enabled fails there. The
    /// composed continuous-test artifact path stacks the ambient temp root, this build root, a
    /// generation id, a results directory, and a provider file name whose run hash alone is 64
    /// characters - so a build root that spelled the workspace id (a full 64-character digest) and
    /// the project id (a mangled relative path) in full handed pytest a 263-character
    /// <c>--junitxml</c> path before the project's own directory nesting was even counted. Both
    /// segments are therefore the same 12 hex characters
    /// <see cref="CtGenerationPaths"/> and <see cref="CtTempPaths"/> already use.
    ///
    /// Nothing reads an id back out of these segments: the coordinator walks only the LAYOUT (the
    /// parent directory is the workspace, its children are that workspace's projects), the temp and
    /// generation helpers hash the whole composed root, and <c>ct.db</c> stores it as an opaque key.
    ///
    /// A Miller workspace id is already a SHA-256 hex digest, so its own prefix is reused rather
    /// than re-hashed: the directory name is then the same 12 characters <c>workspace list</c>
    /// prints as the display id, which a person can match by eye. Anything else - a continuous-test
    /// project id is a path, not a digest - is hashed first.
    /// </summary>
    private static string ShortSegment(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return IsSha256Hex(value)
            ? value[..SegmentHashLength]
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
                .ToLowerInvariant()[..SegmentHashLength];
    }

    private static bool IsSha256Hex(string value) =>
        value.Length == 64
        && value.All(static ch => ch is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static IEnumerable<string> EnumerateCandidateFiles(string root)
    {
        IReadOnlyList<string> ownGitAdminDirs = OwnGitAdminDirs(root);
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
                if (SkipDirectoryNames.Contains(Path.GetFileName(child)))
                    continue;
                if (IsSeparateCheckout(child, ownGitAdminDirs))
                    continue;
                pending.Push(child);
            }
        }
    }

    /// <summary>
    /// True when <paramref name="directory"/> is the root of a SEPARATE checkout - a linked worktree or a
    /// plain nested clone. Such a checkout has its own branch, its own index, and its own CT rows, so its
    /// test projects belong to that workspace and the walk stops at its root rather than building and
    /// running another branch's source under this workspace's freshness key.
    ///
    /// A SUBMODULE is not a separate checkout for this purpose and must stay in the walk. Its source sits
    /// in this working tree, this workspace's index covers it, and a developer who breaks one of its tests
    /// has to see the verdict go red. Treating every <c>.git</c> marker alike dropped every submodule's
    /// test projects from the inventory and reported green for a repository whose tests continuous testing
    /// had silently stopped running - the worst failure mode this system has.
    ///
    /// The shapes are told apart by the marker itself, never by the directory name:
    /// <list type="bullet">
    ///   <item>a <c>.git</c> DIRECTORY is a plain clone. It owns a whole admin directory of its own, so it
    ///     is always a separate checkout.</item>
    ///   <item>a <c>.git</c> FILE holds <c>gitdir: &lt;path&gt;</c>. Git puts a submodule's git directory
    ///     under <c>&lt;admin dir&gt;/modules/...</c> and a linked worktree's under
    ///     <c>&lt;admin dir&gt;/worktrees/...</c>, so the directory stays in the walk only when that target
    ///     resolves inside the <c>modules</c> directory of an admin dir THIS root owns. A worktree of this
    ///     very repository is still skipped, because its target lands under <c>worktrees</c>.</item>
    ///   <item>a <c>.git</c> file whose pointer cannot be read or parsed proves nothing, so the directory
    ///     is treated as a separate checkout.</item>
    /// </list>
    /// </summary>
    private static bool IsSeparateCheckout(string directory, IReadOnlyList<string> ownGitAdminDirs)
    {
        string marker = Path.Combine(directory, ".git");
        if (Directory.Exists(marker))
            return true;
        if (!File.Exists(marker))
            return false;

        string? gitDir = ReadGitDirPointer(marker, directory);
        return gitDir is null || !IsOwnSubmoduleGitDir(gitDir, ownGitAdminDirs);
    }

    /// <summary>
    /// The git administrative directories this workspace root owns: its own git directory, plus the
    /// repository's shared git directory when the root is itself a linked worktree (git may hold a
    /// submodule under either one). An empty list means the root is not a git checkout at all, so every
    /// nested <c>.git</c> marker below it belongs to some other repository.
    /// </summary>
    private static IReadOnlyList<string> OwnGitAdminDirs(string root)
    {
        GitWorktreeLayout? layout = GitWorktreeLayout.Resolve(root);
        if (layout is null)
            return [];
        if (layout.IsLinkedWorktree)
            return [layout.GitDir, layout.CommonDir];
        return [layout.GitDir];
    }

    /// <summary>
    /// Reads the <c>gitdir:</c> target out of a <c>.git</c> FILE as an absolute path, or null when the file
    /// cannot be read or carries no such line. Git resolves a relative target against the working-tree
    /// directory that holds the marker, so <paramref name="directory"/> is the base.
    /// </summary>
    private static string? ReadGitDirPointer(string markerFile, string directory)
    {
        try
        {
            return GitWorktreeLayout.ParseGitFile(File.ReadAllText(markerFile), directory);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when a <c>gitdir:</c> target is a submodule of THIS checkout, which is the case only when it
    /// resolves inside the <c>modules</c> directory of an admin dir this root owns. The target existing on
    /// disk is deliberately not required: the path shape alone decides, so a submodule whose git directory
    /// is momentarily unreadable keeps its test projects instead of losing them.
    /// </summary>
    private static bool IsOwnSubmoduleGitDir(string gitDir, IReadOnlyList<string> ownGitAdminDirs)
    {
        foreach (string admin in ownGitAdminDirs)
        {
            if (IsInside(Path.Combine(admin, "modules"), gitDir))
                return true;
        }

        return false;
    }

    private static bool IsCandidateFileName(string path)
    {
        string name = Path.GetFileName(path);
        return DotnetProjectExtensions.Contains(Path.GetExtension(path))
            || string.Equals(name, "Cargo.toml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "package.json", StringComparison.OrdinalIgnoreCase)
            || PythonProjectNames.Contains(name);
    }

    private static bool TryIdentify(string path, out string? framework, out IReadOnlyList<string> excludeTraits)
    {
        excludeTraits = [];
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
            bool sdkTest = ContainsToken(text, "Microsoft.NET.Sdk.Test")
                || ContainsToken(text, "Microsoft.NET.Test.Sdk")
                || ContainsToken(text, "Microsoft.Testing.Platform");
            excludeTraits = ParseDefaultFilterExclusions(text);
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

            if (sdkTest)
            {
                framework = "dotnet";
                return true;
            }

            excludeTraits = [];
            framework = null;
            return false;
        }

        framework = null;
        return false;
    }

    /// <summary>
    /// Maps a project's default VSTest case filter (<c>VSTestTestCaseFilter</c>) onto trait
    /// exclusions, so a continuous run honors the same default suite as a bare
    /// <c>dotnet test</c>. Only pure conjunctions of <c>Name!=Value</c> terms translate; any
    /// other filter shape seeds nothing rather than a partial, wrong exclusion set.
    /// </summary>
    internal static IReadOnlyList<string> ParseDefaultFilterExclusions(string projectText)
    {
        const string openTag = "<VSTestTestCaseFilter>";
        const string closeTag = "</VSTestTestCaseFilter>";
        int start = projectText.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return [];
        start += openTag.Length;
        int end = projectText.IndexOf(closeTag, start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
            return [];

        string filter = projectText[start..end].Replace("&amp;", "&", StringComparison.Ordinal).Trim();
        if (filter.Length == 0)
            return [];

        var exclusions = new List<string>();
        foreach (string segment in filter.Split('&'))
        {
            string term = segment.Trim();
            int op = term.IndexOf("!=", StringComparison.Ordinal);
            if (op <= 0)
                return [];

            string traitName = term[..op].Trim();
            string traitValue = term[(op + 2)..].Trim();
            if (traitName.Length == 0 || traitValue.Length == 0)
                return [];
            if (!IsPlainFilterToken(traitName) || !IsPlainFilterToken(traitValue))
                return [];

            exclusions.Add(traitName + "=" + traitValue);
        }

        return exclusions;
    }

    private static bool IsPlainFilterToken(string value) =>
        value.All(static ch => char.IsLetterOrDigit(ch) || ch is '_' or '.' or '-');

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
