using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    /// <summary>
    /// Directory names that hold test DATA rather than test projects. A manifest below one of them is a
    /// parser fixture, not a suite: the dogfood repository julie-extractors enabled
    /// <c>fixtures/extraction/toml/cargo_deps/Cargo.toml</c> and a fixture <c>pyproject.toml</c> as real
    /// projects, and continuous testing tried to build them.
    ///
    /// The list stays SMALL and LITERAL. A name that also spells a real source directory (<c>data</c>,
    /// <c>samples</c>, <c>examples</c>) would silently stop testing a project someone ships, which is the
    /// worse failure of the two. The rule prunes the WALK only - <see cref="Identify"/> still accepts a
    /// path a person names, because <c>tests enable</c> carries their intent.
    /// </summary>
    private static readonly HashSet<string> FixtureDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "fixtures",
        "__fixtures__",
        "testdata",
    };

    private static readonly HashSet<string> DotnetProjectExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj",
        ".fsproj",
        ".vbproj",
    };

    /// <summary>
    /// Every python config file that enables a pytest project, in the order pytest ITSELF reads them
    /// when it picks a rootdir: <c>pytest.ini</c> wins over everything (even when empty), then
    /// <c>pyproject.toml</c>, then <c>tox.ini</c>, then <c>setup.cfg</c>. <c>setup.py</c> is no config
    /// file at all and comes last.
    ///
    /// The order is load-bearing because <see cref="CollapsePytestConfigRoots"/> enables exactly ONE
    /// project per directory and names the winner as the project path: the file Miller names is then the
    /// file pytest reads. The dogfood repository more-itertools carries <c>pyproject.toml</c>,
    /// <c>setup.cfg</c> and <c>tox.ini</c> side by side, and each one used to enable its own project -
    /// three projects for one suite, so every change ran it three times.
    /// </summary>
    private static readonly string[] PythonProjectPriority =
    [
        "pytest.ini",
        "pyproject.toml",
        "tox.ini",
        "setup.cfg",
        "setup.py",
    ];

    /// <summary>
    /// The same names as <see cref="PythonProjectPriority"/>, as a set. It is DERIVED from the ordered
    /// list so the two can never disagree about which files enable a pytest project.
    /// </summary>
    private static readonly HashSet<string> PythonProjectNames =
        new(PythonProjectPriority, StringComparer.OrdinalIgnoreCase);

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

        return CollapsePytestConfigRoots(SuppressCargoWorkspaceMembers(projects))
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
                string childName = Path.GetFileName(child);
                if (SkipDirectoryNames.Contains(childName) || FixtureDirectoryNames.Contains(childName))
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

    /// <summary>
    /// Keeps ONE pytest project for each directory: the config file highest in
    /// <see cref="PythonProjectPriority"/>. A python package commonly carries several of these files at
    /// once, and every one of them named the same suite - so the suite ran once per file.
    ///
    /// The rule is per DIRECTORY, never per repository: two independent packages stay two projects.
    /// Nothing else is touched, so a directory that holds both a <c>pyproject.toml</c> and a
    /// <c>package.json</c> keeps its pytest project and its javascript project.
    /// </summary>
    private static List<ContinuousTestProject> CollapsePytestConfigRoots(List<ContinuousTestProject> projects)
    {
        var winners = new Dictionary<string, ContinuousTestProject>(PathComparer);
        foreach (ContinuousTestProject project in projects)
        {
            if (!PythonProjectNames.Contains(Path.GetFileName(project.ProjectPath)))
                continue;
            string directory = DirectoryOf(project.ProjectPath);
            if (!winners.TryGetValue(directory, out ContinuousTestProject? standing)
                || PytestConfigRank(project.ProjectPath) < PytestConfigRank(standing.ProjectPath))
            {
                winners[directory] = project;
            }
        }

        if (winners.Count == projects.Count(static project =>
                PythonProjectNames.Contains(Path.GetFileName(project.ProjectPath))))
        {
            return projects;
        }

        var kept = new List<ContinuousTestProject>(projects.Count);
        foreach (ContinuousTestProject project in projects)
        {
            if (!PythonProjectNames.Contains(Path.GetFileName(project.ProjectPath)))
            {
                kept.Add(project);
                continue;
            }

            if (ReferenceEquals(winners[DirectoryOf(project.ProjectPath)], project))
                kept.Add(project);
        }

        return kept;
    }

    /// <summary>
    /// Position of a python config file in <see cref="PythonProjectPriority"/>. A name outside the list
    /// ranks last, which can only happen if a caller hands in a path this inventory never discovered.
    /// </summary>
    private static int PytestConfigRank(string path)
    {
        int index = Array.FindIndex(
            PythonProjectPriority,
            name => string.Equals(name, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue : index;
    }

    /// <summary>
    /// Drops the member crates of a cargo workspace. One <c>cargo test</c> at the workspace root already
    /// builds and runs every member, so a member's own <c>Cargo.toml</c> enabling a second project ran
    /// the whole suite twice - once per crate and once again for the root.
    ///
    /// A crate is dropped only when this parser can PROVE it is a member: the workspace root names it in
    /// <c>members</c> and does not name it in <c>exclude</c>. Everything else is kept. The two mistakes
    /// are not equal - a kept member runs a suite twice, while a wrongly dropped crate stops being
    /// tested at all and reports green - so every doubt (a workspace that lists no members, a glob shape
    /// this cannot read, an unreadable manifest) resolves toward keeping the candidate.
    /// </summary>
    private static List<ContinuousTestProject> SuppressCargoWorkspaceMembers(List<ContinuousTestProject> projects)
    {
        var roots = new List<CargoWorkspaceRoot>();
        foreach (ContinuousTestProject project in projects)
        {
            if (!IsCargoManifest(project.ProjectPath))
                continue;
            CargoWorkspaceRoot? root = TryReadCargoWorkspaceRoot(project.ProjectPath);
            if (root is not null)
                roots.Add(root);
        }

        if (roots.Count == 0)
            return projects;

        var kept = new List<ContinuousTestProject>(projects.Count);
        foreach (ContinuousTestProject project in projects)
        {
            if (IsCargoManifest(project.ProjectPath)
                && roots.Any(root => root.Covers(project.ProjectPath)))
            {
                continue;
            }

            kept.Add(project);
        }

        return kept;
    }

    private static bool IsCargoManifest(string path) =>
        string.Equals(Path.GetFileName(path), "Cargo.toml", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The member and exclude lists of one cargo workspace root, as workspace-relative path patterns.
    /// </summary>
    private sealed record CargoWorkspaceRoot(
        string Directory,
        IReadOnlyList<string> Members,
        IReadOnlyList<string> Exclude)
    {
        /// <summary>
        /// True when a <c>cargo test</c> at this workspace root already covers
        /// <paramref name="manifestPath"/>. The root's OWN manifest is never covered - it is the project
        /// that does the covering.
        /// </summary>
        public bool Covers(string manifestPath)
        {
            string directory = DirectoryOf(manifestPath);
            if (PathComparer.Equals(directory, Directory))
                return false;

            string relative = Path.GetRelativePath(Directory, directory).Replace('\\', '/');
            if (relative.StartsWith("..", PathComparison) || Path.IsPathRooted(relative))
                return false;
            if (Exclude.Any(pattern => PatternCoversPath(pattern, relative)))
                return false;
            return Members.Any(pattern => GlobMatches(pattern, relative));
        }
    }

    /// <summary>
    /// Reads the <c>[workspace]</c> table out of a <c>Cargo.toml</c>, or null when the manifest declares
    /// no workspace. The parse is deliberately literal - a line-oriented scan of the one table, not a
    /// TOML reader - because the only question is which relative paths the workspace names.
    ///
    /// Only the <c>[workspace]</c> table itself counts. <c>[workspace.package]</c>,
    /// <c>[workspace.dependencies]</c> and the rest are different tables, and a <c>members</c> key under
    /// one of them means something else.
    /// </summary>
    private static CargoWorkspaceRoot? TryReadCargoWorkspaceRoot(string manifestPath)
    {
        string text = ReadHead(manifestPath);
        if (text.Length == 0)
            return null;

        bool declaresWorkspace = false;
        bool inWorkspaceTable = false;
        var members = new List<string>();
        var exclude = new List<string>();
        var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string trimmed = StripComment(line).Trim();
            if (trimmed.Length == 0)
                continue;

            if (trimmed.StartsWith('['))
            {
                inWorkspaceTable = string.Equals(trimmed, "[workspace]", StringComparison.Ordinal);
                declaresWorkspace |= inWorkspaceTable;
                continue;
            }

            if (!inWorkspaceTable)
                continue;

            if (TryReadKey(trimmed, "members", out string? membersValue))
                ReadPathArray(membersValue, reader, members);
            else if (TryReadKey(trimmed, "exclude", out string? excludeValue))
                ReadPathArray(excludeValue, reader, exclude);
        }

        return declaresWorkspace
            ? new CargoWorkspaceRoot(DirectoryOf(manifestPath), members, exclude)
            : null;
    }

    /// <summary>
    /// Splits a <c>key = value</c> line, returning the value when the key matches. A key this does not
    /// recognize is simply not read, so an unusual manifest names no members and suppresses nothing.
    /// </summary>
    private static bool TryReadKey(string line, string key, out string value)
    {
        value = string.Empty;
        int equals = line.IndexOf('=');
        if (equals <= 0)
            return false;
        if (!string.Equals(line[..equals].Trim(), key, StringComparison.Ordinal))
            return false;
        value = line[(equals + 1)..];
        return true;
    }

    /// <summary>
    /// Collects the quoted strings of a TOML array that may span several lines, stopping at the closing
    /// bracket. An array that never closes inside the manifest head yields whatever it named so far.
    /// </summary>
    private static void ReadPathArray(string firstLine, StringReader reader, List<string> destination)
    {
        string current = firstLine;
        while (true)
        {
            foreach (string entry in QuotedStrings(current))
                destination.Add(entry);
            if (current.Contains(']', StringComparison.Ordinal))
                return;

            string? next = reader.ReadLine();
            if (next is null)
                return;
            current = StripComment(next);
        }
    }

    /// <summary>Every single- or double-quoted string in one line of TOML.</summary>
    private static IEnumerable<string> QuotedStrings(string line)
    {
        int index = 0;
        while (index < line.Length)
        {
            char quote = line[index];
            if (quote is not ('"' or '\''))
            {
                index++;
                continue;
            }

            int close = line.IndexOf(quote, index + 1);
            if (close < 0)
                yield break;
            yield return line[(index + 1)..close];
            index = close + 1;
        }
    }

    /// <summary>
    /// Removes a trailing TOML comment. A <c>#</c> inside a quoted string is not a comment, so the scan
    /// tracks quotes rather than cutting at the first hash.
    /// </summary>
    private static string StripComment(string line)
    {
        char quote = '\0';
        for (int index = 0; index < line.Length; index++)
        {
            char ch = line[index];
            if (quote != '\0')
            {
                if (ch == quote)
                    quote = '\0';
                continue;
            }

            if (ch is '"' or '\'')
                quote = ch;
            else if (ch == '#')
                return line[..index];
        }

        return line;
    }

    /// <summary>
    /// True when an exclude pattern covers a path: the pattern matches it, or the path sits BELOW a
    /// literal excluded directory. An excluded subtree is outside the workspace run, so everything in it
    /// keeps its own project.
    /// </summary>
    private static bool PatternCoversPath(string pattern, string relativePath)
    {
        if (GlobMatches(pattern, relativePath))
            return true;
        string normalized = pattern.Replace('\\', '/').Trim('/');
        return normalized.Length > 0
            && relativePath.StartsWith(normalized + "/", PathComparison);
    }

    /// <summary>
    /// Matches a cargo member pattern against a workspace-relative directory path. <c>*</c> matches
    /// inside one path segment and <c>**</c> matches any run of segments, which is what cargo's glob
    /// syntax means. A pattern carrying anything else (<c>?</c>, a character class) is NOT understood,
    /// and an unmatched pattern keeps the candidate - see <see cref="SuppressCargoWorkspaceMembers"/>.
    /// </summary>
    private static bool GlobMatches(string pattern, string relativePath)
    {
        string normalized = pattern.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0
            || normalized.Contains('?', StringComparison.Ordinal)
            || normalized.Contains('[', StringComparison.Ordinal))
        {
            return false;
        }

        return MatchSegments(normalized.Split('/'), 0, relativePath.Split('/'), 0);
    }

    private static bool MatchSegments(string[] pattern, int patternIndex, string[] path, int pathIndex)
    {
        if (patternIndex == pattern.Length)
            return pathIndex == path.Length;

        if (string.Equals(pattern[patternIndex], "**", StringComparison.Ordinal))
        {
            for (int next = pathIndex; next <= path.Length; next++)
            {
                if (MatchSegments(pattern, patternIndex + 1, path, next))
                    return true;
            }

            return false;
        }

        return pathIndex < path.Length
            && SegmentMatches(pattern[patternIndex], path[pathIndex])
            && MatchSegments(pattern, patternIndex + 1, path, pathIndex + 1);
    }

    private static bool SegmentMatches(string pattern, string segment)
    {
        if (!pattern.Contains('*', StringComparison.Ordinal))
            return string.Equals(pattern, segment, PathComparison);

        string[] parts = pattern.Split('*');
        int cursor = 0;
        for (int index = 0; index < parts.Length; index++)
        {
            string part = parts[index];
            if (part.Length == 0)
                continue;

            if (index == 0)
            {
                if (!segment.StartsWith(part, PathComparison))
                    return false;
                cursor = part.Length;
                continue;
            }

            if (index == parts.Length - 1)
                return segment.EndsWith(part, PathComparison) && segment.Length - part.Length >= cursor;

            int found = segment.IndexOf(part, cursor, PathComparison);
            if (found < 0)
                return false;
            cursor = found + part.Length;
        }

        return true;
    }

    private static string DirectoryOf(string path) =>
        Path.GetDirectoryName(Path.GetFullPath(path)) ?? Path.GetFullPath(path);

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

            if (DeclaresNodeTestRunnerScript(text))
            {
                framework = "node-test";
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
    /// True when a package manifest declares a script that runs node's OWN test runner — the framework
    /// <see cref="JavaScriptTestProvider"/> calls <c>node-test</c> and already knows how to run. Without
    /// this, a repository whose only suite is <c>node --test</c> enabled no continuous-test project at
    /// all, however many test files it held.
    ///
    /// The manifest is parsed as JSON so that only script COMMANDS decide: a dependency NAME that happens
    /// to carry the same text (<c>@rollup/plugin-node-resolve</c>) must never enable a project. A manifest
    /// this cannot parse declares nothing.
    /// </summary>
    internal static bool DeclaresNodeTestRunnerScript(string packageJsonText)
    {
        if (string.IsNullOrWhiteSpace(packageJsonText))
            return false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(packageJsonText);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("scripts", out JsonElement scripts)
                || scripts.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (JsonProperty script in scripts.EnumerateObject())
            {
                if (script.Value.ValueKind == JsonValueKind.String
                    && JavaScriptTestProvider.IsNodeTestRunnerCommand(script.Value.GetString()))
                {
                    return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
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

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
