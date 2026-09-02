using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Miller.Indexing;
using Miller.Testing.Providers.Php;
using Miller.Testing.Providers.Qml;

namespace Miller.Testing;

/// <summary>
/// One enabled project with its materialized run workspace.
/// <see cref="BuildRootFallbackReason"/> is null for the default workspace-local build root; when the
/// workspace root is too long for that root (see
/// <see cref="ContinuousTestProjectInventory.WorkspaceRootLengthBudget"/>), it names why the legacy
/// machine temp root was chosen instead, so callers can report the choice.
/// </summary>
public sealed record ContinuousTestProjectWorkItem(
    ContinuousTestProject Project,
    ContinuousTestWorkspace Workspace,
    string? BuildRootFallbackReason = null);

public static class ContinuousTestProjectInventory
{
    /// <summary>
    /// Width of every build-root path segment, matching the generation id's own hash width. See
    /// <see cref="ShortSegment"/> for why the segments are hashes and not the ids themselves.
    /// </summary>
    internal const int SegmentHashLength = 12;

    /// <summary>
    /// Name prefix of a workspace-local build root directly under <c>.miller</c>. The prefix is what
    /// lets peer-root scans tell a build root apart from every other <c>.miller</c> entry (logs,
    /// sidecar databases, the legacy <c>ct/</c> control directory).
    /// </summary>
    internal const string WorkspaceLocalBuildRootPrefix = "ct-";

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

    private static readonly HashSet<string> QmlSourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".qml",
    };

    private static readonly HashSet<string> QuickTestRunnerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c",
        ".cc",
        ".cpp",
        ".cxx",
        ".h",
        ".hh",
        ".hpp",
        ".hxx",
        ".m",
        ".mm",
    };

    private sealed record QmlProjectEvidence(
        string ProjectPath,
        string ConfigureRoot,
        string EvidenceRoot);

    private sealed record QmakeProjectEvidence(
        string ProjectPath,
        string ConfigureRoot,
        string EvidenceRoot);

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

        string[] candidateFiles = EnumerateCandidateFiles(root).ToArray();
        var projects = new List<ContinuousTestProject>();
        foreach (string path in candidateFiles)
        {
            if (IsCMakeLists(path))
                continue;
            if (!TryIdentify(path, out string? framework, out IReadOnlyList<string> excludeTraits))
                continue;
            projects.Add(new ContinuousTestProject(
                Id: ProjectId(workspaceId, root, path),
                WorkspaceId: workspaceId,
                ProjectPath: path,
                Framework: framework,
                ExcludeTraits: excludeTraits,
                Metadata: ProjectMetadata(path, root)));
        }

        foreach (QmlProjectEvidence evidence in DiscoverQmlProjects(candidateFiles))
        {
            projects.Add(new ContinuousTestProject(
                Id: ProjectId(workspaceId, root, evidence.ProjectPath),
                WorkspaceId: workspaceId,
                ProjectPath: evidence.ProjectPath,
                Framework: "qt-quick-test",
                Metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["backend"] = QtQuickTestBackendIds.CMake,
                    ["configure_root"] = evidence.ConfigureRoot,
                    ["evidence_root"] = evidence.EvidenceRoot,
                }));
        }

        foreach (QmakeProjectEvidence evidence in DiscoverQmakeProjects(candidateFiles))
        {
            projects.Add(new ContinuousTestProject(
                Id: ProjectId(workspaceId, root, evidence.ProjectPath),
                WorkspaceId: workspaceId,
                ProjectPath: evidence.ProjectPath,
                Framework: "qt-quick-test",
                Metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["backend"] = QtQuickTestBackendIds.Qmake,
                    ["configure_root"] = evidence.ConfigureRoot,
                    ["evidence_root"] = evidence.EvidenceRoot,
                }));
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
        if (IsCMakeLists(full))
        {
            string[] candidateFiles = EnumerateCandidateFiles(Path.GetFullPath(workspaceRoot)).ToArray();
            QmlProjectEvidence? qmlEvidence = DiscoverQmlProjects(candidateFiles)
                .FirstOrDefault(evidence =>
                    PathComparer.Equals(evidence.ProjectPath, full)
                    || IsInside(evidence.ConfigureRoot, full));
            if (qmlEvidence is not null)
            {
                return new ContinuousTestProject(
                    Id: ProjectId(workspaceId, workspaceRoot, qmlEvidence.ProjectPath),
                    WorkspaceId: workspaceId,
                    ProjectPath: qmlEvidence.ProjectPath,
                    Framework: "qt-quick-test",
                    Metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["backend"] = QtQuickTestBackendIds.CMake,
                        ["configure_root"] = qmlEvidence.ConfigureRoot,
                        ["evidence_root"] = qmlEvidence.EvidenceRoot,
                    });
            }
        }
        if (string.Equals(Path.GetExtension(full), ".pro", StringComparison.OrdinalIgnoreCase))
        {
            string[] candidateFiles = EnumerateCandidateFiles(Path.GetFullPath(workspaceRoot)).ToArray();
            QmakeProjectEvidence? qmakeEvidence = DiscoverQmakeProjects(candidateFiles)
                .FirstOrDefault(evidence => PathComparer.Equals(evidence.ProjectPath, full));
            if (qmakeEvidence is not null)
            {
                return new ContinuousTestProject(
                    Id: ProjectId(workspaceId, workspaceRoot, qmakeEvidence.ProjectPath),
                    WorkspaceId: workspaceId,
                    ProjectPath: qmakeEvidence.ProjectPath,
                    Framework: "qt-quick-test",
                    Metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["backend"] = QtQuickTestBackendIds.Qmake,
                        ["configure_root"] = qmakeEvidence.ConfigureRoot,
                        ["evidence_root"] = qmakeEvidence.EvidenceRoot,
                    });
            }
        }
        if (!TryIdentify(full, out string? framework, out IReadOnlyList<string> excludeTraits))
        {
            framework = FrameworkFallback(full);
            excludeTraits = DotnetProjectExtensions.Contains(Path.GetExtension(full))
                ? ParseDefaultFilterExclusions(ReadHead(full))
                : [];

            // The fallback deliberately accepts a project file whose contents name no framework — a .csproj
            // that references a shared test library still runs under dotnet test. It cannot accept a file it
            // has no framework for at all: that path returned a project with a NULL framework, so
            // `tests enable --project go.mod` enabled a Go module file, rendered as "(unknown)", and gave the
            // workspace a project no provider can ever run.
            if (framework is null)
                return null;
        }
        return new ContinuousTestProject(
            Id: ProjectId(workspaceId, workspaceRoot, full),
            WorkspaceId: workspaceId,
            ProjectPath: full,
            Framework: framework,
            ExcludeTraits: excludeTraits,
            Metadata: ProjectMetadata(full, Path.GetFullPath(workspaceRoot)));
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
        bool overBudget = root.Length > WorkspaceRootLengthBudget;
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

            string? fallbackReason = null;
            string buildRoot;
            if (overBudget)
            {
                buildRoot = Path.Combine(
                    CtTempPaths.BuildRoot,
                    ShortSegment(project.WorkspaceId),
                    ShortSegment(project.Id));
                fallbackReason =
                    $"workspace root is {root.Length} characters, over the {WorkspaceRootLengthBudget}-character "
                    + $"budget for a workspace-local build root; building under {buildRoot}";
            }
            else
            {
                buildRoot = Path.Combine(
                    root,
                    ".miller",
                    WorkspaceLocalBuildRootPrefix + ShortSegment(project.Id));
            }

            var workspace = new ContinuousTestWorkspace(
                WorkspaceId: project.WorkspaceId,
                WorkspaceRoot: root,
                ProjectPath: projectPath,
                BuildOutputRoot: buildRoot,
                Framework: project.Framework,
                Command: project.Command,
                ExcludeTraits: project.ExcludeTraits,
                Metadata: project.Metadata);
            workItems.Add(new ContinuousTestProjectWorkItem(project, workspace, fallbackReason));
        }

        return workItems;
    }

    /// <summary>
    /// The whole-path bound the build root protects: Windows MAX_PATH is 260 characters, and a
    /// machine without long paths enabled fails there.
    /// </summary>
    internal const int WindowsPathBudget = 260;

    /// <summary>
    /// The longest file name a provider composes under a generation's TestResults directory:
    /// <c>run-&lt;64-character run hash&gt;.part000.junit.xml</c>, one chunk of a split xunit v3 run.
    /// </summary>
    private const int LongestProviderArtifactNameLength = 86;

    /// <summary>
    /// Every character below the workspace root in the deepest composed provider artifact path:
    /// <c>/.miller/ct-&lt;project&gt;/g&lt;generation&gt;/TestResults/&lt;artifact&gt;</c>.
    /// </summary>
    private static readonly int WorkspaceLocalTailLength =
        ("/.miller/" + WorkspaceLocalBuildRootPrefix).Length
        + SegmentHashLength
        + "/g".Length + SegmentHashLength
        + "/TestResults/".Length
        + LongestProviderArtifactNameLength;

    /// <summary>
    /// The longest workspace root whose workspace-local build root keeps the deepest composed
    /// provider artifact path inside <see cref="WindowsPathBudget"/>. A longer root falls back to
    /// the legacy machine temp build root (<see cref="CtTempPaths.BuildRoot"/>): tests that walk up
    /// from the build output to find the repo root are broken for such a project either way, and
    /// MAX_PATH breakage is worse.
    /// </summary>
    internal static readonly int WorkspaceRootLengthBudget = WindowsPathBudget - WorkspaceLocalTailLength;

    /// <summary>
    /// One fixed-width path segment for an identifier of any length.
    ///
    /// The default build root lives inside the workspace at
    /// <c>&lt;workspace&gt;/.miller/ct-&lt;project segment&gt;</c>, so tests that walk up from
    /// the test binary to find the repo root pass under continuous testing with zero project-side
    /// configuration. The flattened shape keeps the deepest assembly directory
    /// (<c>.miller/ct-&lt;project&gt;/g&lt;generation&gt;/out/&lt;ProjectName&gt;</c>) exactly five
    /// levels below the workspace root, so walk-up helpers capped at eight ascents clear it with
    /// margin even after burning one on a trailing separator. The root is per-workspace already, so
    /// only the project needs a segment there; the over-budget fallback under
    /// <see cref="CtTempPaths.BuildRoot"/> keeps both the workspace and the project segment because
    /// that root is machine-shared.
    ///
    /// Windows MAX_PATH is 260 characters and a machine without long paths enabled fails there. The
    /// composed continuous-test artifact path stacks the build root, a generation id, a results
    /// directory, and a provider file name whose run hash alone is 64 characters - so a build root
    /// that spelled the workspace id (a full 64-character digest) and the project id (a mangled
    /// relative path) in full handed pytest a 263-character <c>--junitxml</c> path before the
    /// project's own directory nesting was even counted. Every segment is therefore the same 12 hex
    /// characters <see cref="CtGenerationPaths"/> and <see cref="CtTempPaths"/> already use.
    ///
    /// Nothing reads an id back out of these segments: the temp and generation helpers hash the
    /// whole composed root, and <c>ct.db</c> stores it as an opaque key.
    ///
    /// A Miller workspace id is already a SHA-256 hex digest, so its own prefix is reused rather
    /// than re-hashed: the fallback directory name is then the same 12 characters
    /// <c>workspace list</c> prints as the display id, which a person can match by eye. Anything
    /// else - a continuous-test project id is a path, not a digest - is hashed first.
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
                if (IsReparsePoint(child))
                    continue;
                if (IsSeparateCheckout(child, ownGitAdminDirs))
                    continue;
                pending.Push(child);
            }
        }
    }

    /// <summary>
    /// True when <paramref name="directory"/> is a junction, a directory symlink, or any other reparse
    /// point, which the walk must not descend into.
    ///
    /// A reparse point re-enters a tree the walk has already covered, so ONE physical project is
    /// discovered under several logical paths and each copy becomes a separately enabled project that
    /// builds and runs on every change. A link that points at its own ancestor repeats that per loop
    /// level - 63 copies of one project, measured. The containment checks stay LOGICAL; only the descent
    /// is refused, which is the same guard the indexing walk applies.
    ///
    /// A dangling or unreadable link throws here, and that answers SKIP: the walk cannot enumerate a
    /// directory it cannot read anyway, and the two exception kinds are the ones the walk already treats
    /// as "move on".
    /// </summary>
    private static bool IsReparsePoint(string directory)
    {
        try
        {
            return (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
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

    private static IReadOnlyList<QmlProjectEvidence> DiscoverQmlProjects(IReadOnlyList<string> candidateFiles)
    {
        var candidates = new List<QmlProjectEvidence>();
        foreach (string path in candidateFiles.Where(IsCMakeLists))
        {
            if (TryDiscoverQmlProject(path, candidateFiles, out QmlProjectEvidence? evidence)
                && evidence is not null)
                candidates.Add(evidence);
        }

        var projects = new List<QmlProjectEvidence>(candidates.Count);
        foreach (QmlProjectEvidence candidate in candidates
                     .OrderBy(evidence => PathDepth(evidence.ConfigureRoot))
                     .ThenBy(evidence => evidence.ProjectPath, PathComparer))
        {
            if (projects.Any(project => IsIncludedProject(project, candidate, candidateFiles)))
                continue;
            projects.Add(candidate);
        }

        return projects;
    }

    private static IReadOnlyList<QmakeProjectEvidence> DiscoverQmakeProjects(
        IReadOnlyList<string> candidateFiles)
    {
        var projects = new List<QmakeProjectEvidence>();
        foreach (string path in candidateFiles
                     .Where(path => string.Equals(Path.GetExtension(path), ".pro", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => path, PathComparer))
        {
            if (TryDiscoverQmakeProject(path, candidateFiles, out QmakeProjectEvidence? evidence)
                && evidence is not null)
                projects.Add(evidence);
        }

        return projects;
    }

    private static bool TryDiscoverQmakeProject(
        string projectPath,
        IReadOnlyList<string> candidateFiles,
        out QmakeProjectEvidence? evidence)
    {
        evidence = null;
        string configureRoot = DirectoryOf(projectPath);
        if (!QmakeQuickTestTooling.TryReadProjectModel(projectPath, out QmakeProjectModel? projectModel)
            || projectModel is null)
            return false;

        if (!QmakeQuickTestTooling.HasVariableValue(projectModel.EffectiveText, "CONFIG", "qmltestcase")
            && !(QmakeQuickTestTooling.HasVariableValue(projectModel.EffectiveText, "QT", "qmltest")
                && QmakeQuickTestTooling.HasVariableValue(projectModel.EffectiveText, "CONFIG", "testcase")))
            return false;

        string[] qmlTestPaths = candidateFiles
            .Where(path => IsInside(configureRoot, path)
                && !IsOwnedByIndependentQmakeProject(projectPath, path, candidateFiles, projectModel)
                && IsQmlTestEvidence(path))
            .ToArray();
        bool runner = candidateFiles
            .Where(path => IsInside(configureRoot, path)
                && !IsOwnedByIndependentQmakeProject(projectPath, path, candidateFiles, projectModel)
                && QuickTestRunnerExtensions.Contains(Path.GetExtension(path)))
            .Any(IsQmakeQuickTestRunner);
        if (!runner || qmlTestPaths.Length == 0)
            return false;

        evidence = new QmakeProjectEvidence(
            Path.GetFullPath(projectPath),
            configureRoot,
            CommonDirectory(qmlTestPaths));
        return true;
    }

    private static bool IsQmakeQuickTestRunner(string path)
    {
        if (!TryReadBounded(path, out string text))
            return false;
        string code = CodeWithoutCommentsOrStrings(text);
        return ContainsCodeToken(code, "QUICK_TEST_MAIN")
            || ContainsCodeToken(code, "QUICK_TEST_MAIN_WITH_SETUP")
            || ContainsCodeToken(code, "QUICK_TEST_OPENGL_MAIN");
    }

    private static bool IsOwnedByIndependentQmakeProject(
        string projectPath,
        string candidatePath,
        IReadOnlyList<string> candidateFiles,
        QmakeProjectModel projectModel)
    {
        string configureRoot = DirectoryOf(projectPath);
        foreach (string nestedProject in candidateFiles.Where(path =>
                     string.Equals(Path.GetExtension(path), ".pro", StringComparison.OrdinalIgnoreCase)
                     && !PathComparer.Equals(path, projectPath)))
        {
            string nestedRoot = DirectoryOf(nestedProject);
            if (IsInside(configureRoot, nestedRoot)
                && !PathComparer.Equals(nestedRoot, configureRoot)
                && IsInside(nestedRoot, candidatePath)
                && !projectModel.IncludedFiles.Contains(candidatePath, PathComparer))
                return true;
        }

        return false;
    }

    private static bool TryDiscoverQmlProject(
        string cmakePath,
        IReadOnlyList<string> candidateFiles,
        out QmlProjectEvidence? evidence)
    {
        evidence = null;
        string configureRoot = DirectoryOf(cmakePath);
        if (!ContainsCMakeProjectDeclaration(ReadHead(cmakePath)))
            return false;

        string[] projectFiles = CMakeProjectFiles(cmakePath, candidateFiles)
            .OrderBy(path => path, PathComparer)
            .ToArray();
        string? quickTestPath = projectFiles.FirstOrDefault(IsQuickTestEvidence);
        string[] qmlTestPaths = projectFiles.Where(IsQmlTestEvidence).ToArray();
        if (quickTestPath is null
            || qmlTestPaths.Length == 0
            || !HasCMakeTestRegistration(projectFiles))
            return false;

        evidence = new QmlProjectEvidence(
            ProjectPath: Path.GetFullPath(cmakePath),
            ConfigureRoot: configureRoot,
            EvidenceRoot: CommonDirectory(qmlTestPaths));
        return true;
    }

    private static bool IsQuickTestEvidence(string path)
    {
        string extension = Path.GetExtension(path);
        if (!IsCMakeLists(path) && !QuickTestRunnerExtensions.Contains(extension))
            return false;

        string code = CodeWithoutCommentsOrStrings(ReadHead(path));
        return ContainsCodeToken(code, "Qt6::QuickTest")
            || ContainsCodeToken(code, "Qt5::QuickTest")
            || ContainsCodeToken(code, "Qt::QuickTest")
            || ContainsCodeToken(code, "QUICK_TEST_MAIN")
            || ContainsCodeToken(code, "QUICK_TEST_MAIN_WITH_SETUP")
            || ContainsCodeToken(code, "QUICK_TEST_OPENGL_MAIN");
    }

    // add_test plus the wrapper generation: qt_add_test, ecm_add_test, gtest/catch/doctest
    // *_discover_tests — all register CTest entries the literal token check cannot see.
    private static readonly Regex CMakeTestRegistrationCall = new(
        @"(?<![A-Za-z0-9_])[A-Za-z0-9_]*(?:add_test|_discover_tests)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool HasCMakeTestRegistration(IEnumerable<string> subtreeFiles) =>
        subtreeFiles
            .Where(IsCMakeLists)
            .Any(path => CMakeTestRegistrationCall.IsMatch(CodeWithoutCommentsOrStrings(ReadHead(path))));

    private static bool IsIncludedProject(
        QmlProjectEvidence parent,
        QmlProjectEvidence candidate,
        IReadOnlyList<string> candidateFiles)
    {
        if (!IsInside(parent.ConfigureRoot, candidate.ConfigureRoot)
            || PathComparer.Equals(parent.ConfigureRoot, candidate.ConfigureRoot))
            return false;

        return IncludedCMakeRoots(parent.ProjectPath, candidateFiles)
            .Contains(candidate.ConfigureRoot);
    }

    private static IReadOnlyList<string> CMakeProjectFiles(
        string cmakePath,
        IReadOnlyList<string> candidateFiles)
    {
        string configureRoot = DirectoryOf(cmakePath);
        IReadOnlySet<string> includedRoots = IncludedCMakeRoots(cmakePath, candidateFiles);
        string[] cmakeRoots = candidateFiles
            .Where(IsCMakeLists)
            .Select(DirectoryOf)
            .Distinct(PathComparer)
            .ToArray();

        return candidateFiles
            .Where(path =>
            {
                if (!IsInside(configureRoot, path))
                    return false;
                string? owningRoot = cmakeRoots
                    .Where(root => IsInside(root, path))
                    .OrderByDescending(PathDepth)
                    .FirstOrDefault();
                return owningRoot is not null && includedRoots.Contains(owningRoot);
            })
            .ToArray();
    }

    private static IReadOnlySet<string> IncludedCMakeRoots(
        string cmakePath,
        IReadOnlyList<string> candidateFiles)
    {
        var cmakeByRoot = candidateFiles
            .Where(IsCMakeLists)
            .ToDictionary(DirectoryOf, PathComparer);
        var roots = new HashSet<string>(PathComparer);
        var pending = new Stack<string>();
        string configureRoot = DirectoryOf(cmakePath);
        roots.Add(configureRoot);
        pending.Push(configureRoot);
        while (pending.Count > 0)
        {
            string root = pending.Pop();
            if (!cmakeByRoot.TryGetValue(root, out string? currentCMakePath))
                continue;

            foreach (string argument in CMakeCallFirstArguments(ReadHead(currentCMakePath), "add_subdirectory"))
            {
                if (!TryResolveSubdirectory(root, argument, out string? childRoot)
                    || childRoot is null
                    || !cmakeByRoot.ContainsKey(childRoot)
                    || !roots.Add(childRoot))
                    continue;
                pending.Push(childRoot);
            }
        }

        return roots;
    }

    private static IEnumerable<string> CMakeCallFirstArguments(string text, string call)
    {
        string code = CodeWithoutCommentsOrStrings(text);
        int index = 0;
        while ((index = IndexOfCodeToken(code, call, index)) >= 0)
        {
            int open = index + call.Length;
            while (open < code.Length && char.IsWhiteSpace(code[open]))
                open++;
            if (open >= code.Length || code[open] != '(')
            {
                index = open;
                continue;
            }

            int close = FindClosingParenthesis(code, open);
            if (close < 0)
                yield break;
            string arguments = text[(open + 1)..close];
            if (TryFirstCMakeArgument(arguments, out string? argument) && argument is not null)
                yield return argument;
            index = close + 1;
        }
    }

    private static int FindClosingParenthesis(string code, int open)
    {
        int depth = 0;
        for (int index = open; index < code.Length; index++)
        {
            if (code[index] == '(')
                depth++;
            else if (code[index] == ')' && --depth == 0)
                return index;
        }

        return -1;
    }

    private static bool TryFirstCMakeArgument(string arguments, out string? argument)
    {
        string text = arguments.Trim();
        if (text.Length == 0)
        {
            argument = null;
            return false;
        }

        if (text[0] == '"')
        {
            int close = text.IndexOf('"', 1);
            if (close <= 1)
            {
                argument = null;
                return false;
            }

            argument = text[1..close];
            return true;
        }

        if (text[0] == '[' || text.Contains('$', StringComparison.Ordinal))
        {
            argument = null;
            return false;
        }

        int end = 0;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != ';')
            end++;
        if (end == 0)
        {
            argument = null;
            return false;
        }

        argument = text[..end];
        return true;
    }

    private static bool TryResolveSubdirectory(
        string parentRoot,
        string argument,
        out string? childRoot)
    {
        childRoot = null;
        if (string.IsNullOrWhiteSpace(argument))
            return false;

        try
        {
            string path = Path.IsPathRooted(argument)
                ? argument
                : Path.Combine(parentRoot, argument);
            childRoot = Path.GetFullPath(path);
            return Directory.Exists(childRoot);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsQmlTestEvidence(string path)
    {
        if (!QmlSourceExtensions.Contains(Path.GetExtension(path)))
            return false;

        string name = Path.GetFileName(path);
        return name.StartsWith("tst_", StringComparison.OrdinalIgnoreCase)
            || ContainsCodeToken(CodeWithoutCommentsOrStrings(ReadHead(path)), "TestCase");
    }

    private static bool IsCMakeLists(string path) =>
        string.Equals(Path.GetFileName(path), "CMakeLists.txt", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsCMakeProjectDeclaration(string text) =>
        ContainsCodeCall(CodeWithoutCommentsOrStrings(text), "project");

    private static bool ContainsCodeCall(string code, string token)
    {
        int index = 0;
        while ((index = IndexOfCodeToken(code, token, index)) >= 0)
        {
            int cursor = index + token.Length;
            while (cursor < code.Length && char.IsWhiteSpace(code[cursor]))
                cursor++;
            if (cursor < code.Length && code[cursor] == '(')
                return true;
            index = cursor;
        }

        return false;
    }

    private static bool ContainsCodeToken(string code, string token) =>
        IndexOfCodeToken(code, token, 0) >= 0;

    private static int IndexOfCodeToken(string code, string token, int start)
    {
        int index = code.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            bool leftBoundary = index == 0 || !IsCodeTokenCharacter(code[index - 1]);
            int end = index + token.Length;
            bool rightBoundary = end == code.Length || !IsCodeTokenCharacter(code[end]);
            if (leftBoundary && rightBoundary)
                return index;
            index = code.IndexOf(token, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return -1;
    }

    private static bool IsCodeTokenCharacter(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or ':';

    private static string CodeWithoutCommentsOrStrings(string text)
    {
        var output = text.ToCharArray();
        bool lineComment = false;
        bool blockComment = false;
        bool quoted = false;
        bool escaped = false;
        for (int index = 0; index < output.Length; index++)
        {
            char current = output[index];
            if (lineComment)
            {
                if (current == '\n' || current == '\r')
                    lineComment = false;
                else
                    output[index] = ' ';
                continue;
            }

            if (blockComment)
            {
                if (current == '*' && index + 1 < output.Length && output[index + 1] == '/')
                {
                    output[index++] = ' ';
                    output[index] = ' ';
                    blockComment = false;
                }
                else if (current != '\n' && current != '\r')
                {
                    output[index] = ' ';
                }
                continue;
            }

            if (quoted)
            {
                if (escaped)
                {
                    output[index] = ' ';
                    escaped = false;
                }
                else if (current == '\\')
                {
                    output[index] = ' ';
                    escaped = true;
                }
                else if (current == '"')
                {
                    output[index] = ' ';
                    quoted = false;
                }
                else if (current != '\n' && current != '\r')
                {
                    output[index] = ' ';
                }
                continue;
            }

            if (current == '#')
            {
                output[index] = ' ';
                lineComment = true;
            }
            else if (current == '/' && index + 1 < output.Length && output[index + 1] == '/')
            {
                output[index++] = ' ';
                output[index] = ' ';
                lineComment = true;
            }
            else if (current == '/' && index + 1 < output.Length && output[index + 1] == '*')
            {
                output[index++] = ' ';
                output[index] = ' ';
                blockComment = true;
            }
            else if (current == '"')
            {
                output[index] = ' ';
                quoted = true;
            }
        }

        return new string(output);
    }

    private static int PathDepth(string path) =>
        Path.GetFullPath(path).Count(character => character == Path.DirectorySeparatorChar);

    private static string CommonDirectory(IEnumerable<string> paths)
    {
        string[] normalized = paths.Select(DirectoryOf).Distinct(PathComparer).ToArray();
        if (normalized.Length == 0)
            throw new InvalidOperationException("QML evidence requires at least one path");

        string[] segments = normalized[0]
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        int shared = segments.Length;
        foreach (string path in normalized.Skip(1))
        {
            string[] current = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            shared = Math.Min(shared, current.Length);
            for (int index = 0; index < shared; index++)
            {
                if (!string.Equals(segments[index], current[index], PathComparison))
                {
                    shared = index;
                    break;
                }
            }
        }

        if (shared == 0)
            return Path.GetPathRoot(normalized[0]) ?? normalized[0];
        string result = string.Join(Path.DirectorySeparatorChar, segments.Take(shared));
        return Path.IsPathRooted(result) ? result : Path.DirectorySeparatorChar + result;
    }

    private static bool IsCandidateFileName(string path)
    {
        string name = Path.GetFileName(path);
        return DotnetProjectExtensions.Contains(Path.GetExtension(path))
            || string.Equals(name, "Cargo.toml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "go.mod", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "package.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Gemfile", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "pom.xml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "build.gradle", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "build.gradle.kts", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "build.sbt", StringComparison.OrdinalIgnoreCase)
            || PhpTestProvider.IsPhpProjectFile(path)
            || PythonProjectNames.Contains(name)
            || IsCMakeLists(path)
            || string.Equals(Path.GetExtension(path), ".pro", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(path), ".pri", StringComparison.OrdinalIgnoreCase)
            || QmlSourceExtensions.Contains(Path.GetExtension(path))
            || QuickTestRunnerExtensions.Contains(Path.GetExtension(path));
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

        if (string.Equals(name, "pom.xml", StringComparison.OrdinalIgnoreCase))
        {
            framework = "maven";
            return true;
        }

        if (string.Equals(name, "build.gradle", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "build.gradle.kts", StringComparison.OrdinalIgnoreCase))
        {
            framework = "gradle";
            return true;
        }

        if (string.Equals(name, "build.sbt", StringComparison.OrdinalIgnoreCase))
        {
            framework = "sbt";
            return true;
        }

        if (string.Equals(name, "go.mod", StringComparison.OrdinalIgnoreCase))
        {
            framework = "go";
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

        if (string.Equals(name, "Gemfile", StringComparison.OrdinalIgnoreCase))
        {
            string text = ReadHead(path);
            if (ContainsToken(text, "rspec"))
            {
                framework = "rspec";
                return true;
            }

            if (ContainsToken(text, "minitest"))
            {
                framework = "minitest";
                return true;
            }

            framework = null;
            return false;
        }

        if (PhpTestProvider.IsPhpProjectFile(path))
        {
            string text = ReadHead(path);
            if (text.Contains("pestphp/pest", StringComparison.OrdinalIgnoreCase))
            {
                framework = "pest";
                return true;
            }

            if (text.Contains("phpunit/phpunit", StringComparison.OrdinalIgnoreCase))
            {
                framework = "phpunit";
                return true;
            }

            framework = null;
            return false;
        }

        if (DotnetProjectExtensions.Contains(Path.GetExtension(path)))
        {
            string text = ReadHead(path);
            excludeTraits = ParseDefaultFilterExclusions(text);
            string? projectSdk = DotnetTestBackend.ReadStatic(path).ProjectSdk;
            if (DotnetTestBackend.IsXunitProject(text))
            {
                framework = DotnetTestBackend.XunitFramework(text);
                return true;
            }

            if (DotnetTestBackend.IsNUnitProject(text))
            {
                framework = "nunit";
                return true;
            }

            if (DotnetTestBackend.IsMstestProject(text, projectSdk))
            {
                framework = "mstest";
                return true;
            }

            if (DotnetTestBackend.IsGenericTestProject(text))
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

    private static IReadOnlyDictionary<string, object?>? ProjectMetadata(string projectPath, string workspaceRoot)
    {
        if (DotnetProjectExtensions.Contains(Path.GetExtension(projectPath)))
            return DotnetTestBackend.ToMetadata(DotnetTestBackend.ReadStatic(projectPath));

        if (!string.Equals(Path.GetFileName(projectPath), "go.mod", StringComparison.OrdinalIgnoreCase))
            return null;

        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (GoTestTooling.ReadModulePath(projectPath) is { } modulePath)
            metadata["module"] = modulePath;

        string root = Path.GetFullPath(workspaceRoot);
        string goWork = Path.Combine(root, "go.work");
        if (File.Exists(goWork) && GoWorkspaceIncludes(goWork, Path.GetDirectoryName(projectPath)!))
            metadata["go_work"] = goWork;

        return metadata;
    }

    private static bool GoWorkspaceIncludes(string goWorkPath, string moduleDirectory)
    {
        bool inUseBlock = false;
        string workRoot = Path.GetDirectoryName(goWorkPath)!;
        try
        {
            foreach (string rawLine in File.ReadLines(goWorkPath))
            {
                string line = StripComment(rawLine).Trim();
                if (line.Length == 0)
                    continue;
                if (inUseBlock)
                {
                    if (line == ")")
                    {
                        inUseBlock = false;
                        continue;
                    }

                    if (GoWorkspacePathMatches(line, workRoot, moduleDirectory))
                        return true;
                    continue;
                }

                if (line.Equals("use (", StringComparison.Ordinal))
                {
                    inUseBlock = true;
                    continue;
                }

                if (line.StartsWith("use ", StringComparison.Ordinal)
                    && GoWorkspacePathMatches(line["use ".Length..].Trim(), workRoot, moduleDirectory))
                    return true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    private static bool GoWorkspacePathMatches(string usePath, string workRoot, string moduleDirectory)
    {
        if (!TryUnquoteGoWorkspacePath(usePath, out string path))
            return false;
        try
        {
            string candidate = Path.GetFullPath(Path.Combine(workRoot, path));
            return PathComparer.Equals(candidate, Path.GetFullPath(moduleDirectory));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryUnquoteGoWorkspacePath(string value, out string path)
    {
        path = string.Empty;
        if (value.Length == 0)
            return false;

        bool quoted = value[0] == '"' || value[^1] == '"';
        if (!quoted)
        {
            if (value.Contains('"', StringComparison.Ordinal))
                return false;
            path = value;
            return true;
        }

        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
            return false;

        string inner = value[1..^1];
        if (inner.Length == 0
            || inner.Contains('"', StringComparison.Ordinal)
            || inner.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        path = inner;
        return true;
    }

    /// <summary>
    /// Which xunit generation a dotnet project references: <c>xunit</c> for the v3 shape continuous testing
    /// runs, or <see cref="ContinuousTestFrameworkSupport.XunitV2"/> for the one it cannot.
    ///
    /// <para>CT runs the built self-executing test assembly, which only xUnit v3 produces. A v2 project
    /// builds a dll plus <c>testhost.exe</c>, so CT used to fail LATE, during discovery, with the raw OS
    /// error for a missing <c>&lt;Project&gt;.exe</c> — a message that names a missing file and therefore
    /// reads as a broken build. <c>dotnet new xunit</c> still scaffolds v2, so this is the default trap.</para>
    ///
    /// <para>The decision reads PACKAGE IDS, never the raw text: every xunit project contains the word
    /// <c>xunit</c> somewhere, and <c>xunit.runner.visualstudio</c> / <c>xunit.analyzers</c> ship for both
    /// generations, so neither word settles anything. A <c>xunit.v3</c> id wins outright — a project that
    /// carries both is building against v3. Only the ids below prove v2.</para>
    ///
    /// <para>A project this cannot classify — one whose only xunit id is a shared runner package — keeps
    /// <c>xunit</c>, the answer it has always had. Guessing v2 there would refuse an enable on a project
    /// that runs perfectly well, and the missing-executable message the provider now raises names the same
    /// cause at the same moment the old raw error appeared.</para>
    /// </summary>
    internal static string XunitFramework(string projectText)
    => DotnetTestBackend.XunitFramework(projectText);

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
        if (string.Equals(name, "pom.xml", StringComparison.OrdinalIgnoreCase))
            return "maven";
        if (string.Equals(name, "build.gradle", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "build.gradle.kts", StringComparison.OrdinalIgnoreCase))
            return "gradle";
        if (string.Equals(name, "build.sbt", StringComparison.OrdinalIgnoreCase))
            return "sbt";
        if (string.Equals(name, "package.json", StringComparison.OrdinalIgnoreCase))
            return "node-test";
        if (string.Equals(name, "Gemfile", StringComparison.OrdinalIgnoreCase))
            return "rspec";
        if (PhpTestProvider.IsPhpProjectFile(path))
            return PhpTestTooling.DetectFramework(path) ?? "phpunit";
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
            int read = 0;
            while (read < buffer.Length)
            {
                int chunk = stream.Read(buffer, read, buffer.Length - read);
                if (chunk == 0)
                    break;
                read += chunk;
            }
            return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static bool TryReadBounded(string path, out string text)
    {
        text = string.Empty;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 64 * 1024)
                return false;
            var buffer = new byte[(int)stream.Length];
            int read = 0;
            while (read < buffer.Length)
            {
                int chunk = stream.Read(buffer, read, buffer.Length - read);
                if (chunk == 0)
                    break;
                read += chunk;
            }
            text = Encoding.UTF8.GetString(buffer, 0, read);
            return read == buffer.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
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
