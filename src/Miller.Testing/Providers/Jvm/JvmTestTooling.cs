using System.Security.Cryptography;
using System.Text;

namespace Miller.Testing.Providers.Jvm;

internal readonly record struct JvmTestCaseIdentity(
    string WorkspaceId,
    string ProjectPath,
    string Backend,
    string ClassName,
    string MethodName);

internal static class JvmTestTooling
{
    internal static string ProjectRoot(ContinuousTestWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        string projectPath = Path.GetFullPath(workspace.ProjectPath);
        return Directory.Exists(projectPath)
            ? projectPath
            : Path.GetDirectoryName(projectPath) ?? workspace.WorkspaceRoot;
    }

    internal static string Selector(string className, string methodName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        return className + "." + methodName;
    }

    internal static string EncodeCaseId(
        string workspaceId,
        string projectPath,
        string backend,
        string className,
        string methodName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backend);
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        return string.Join(
            ':',
            "jvm-test",
            Encode(workspaceId),
            Encode(Path.GetFullPath(projectPath)),
            Encode(backend),
            Encode(className),
            Encode(methodName));
    }

    internal static bool TryDecodeCaseId(
        string id,
        out JvmTestCaseIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(id))
            return false;

        string[] parts = id.Split(':');
        if (parts.Length != 6 || !string.Equals(parts[0], "jvm-test", StringComparison.Ordinal))
            return false;
        if (!TryDecode(parts[1], out string workspaceId)
            || !TryDecode(parts[2], out string projectPath)
            || !TryDecode(parts[3], out string backend)
            || !TryDecode(parts[4], out string className)
            || !TryDecode(parts[5], out string methodName))
        {
            return false;
        }

        try
        {
            identity = new JvmTestCaseIdentity(
                workspaceId,
                Path.GetFullPath(projectPath),
                backend,
                className,
                methodName);
            return !string.IsNullOrWhiteSpace(identity.WorkspaceId)
                && !string.IsNullOrWhiteSpace(identity.ProjectPath)
                && !string.IsNullOrWhiteSpace(identity.Backend)
                && !string.IsNullOrWhiteSpace(identity.ClassName)
                && !string.IsNullOrWhiteSpace(identity.MethodName);
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

    internal static string? LanguageLabel(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return null;

        return Path.GetExtension(sourcePath).ToLowerInvariant() switch
        {
            ".java" => "java",
            ".kt" or ".kts" => "kotlin",
            ".scala" => "scala",
            _ => null,
        };
    }

    internal static string GradleBuildRoot(CtGenerationPaths paths) =>
        Path.Combine(paths.GenerationRoot, "gradle-build");

    internal static string GradleUserHome(CtGenerationPaths paths) =>
        Path.Combine(paths.GenerationRoot, "gradle-home");

    internal static string GradleProjectCache(CtGenerationPaths paths) =>
        Path.Combine(paths.GenerationRoot, "gradle-project-cache");

    internal static string GradleInitScript(CtGenerationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.EnsureDirectories();
        string path = Path.Combine(paths.GenerationRoot, "gradle-init.gradle");
        File.WriteAllText(path, InitScriptText);
        return path;
    }

    internal static bool IsInside(string root, string candidate)
    {
        string fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullCandidate = Path.GetFullPath(candidate);
        return string.Equals(fullRoot, fullCandidate, PathComparison)
            || fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, PathComparison)
            || fullCandidate.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, PathComparison);
    }

    internal static string NormalizeSourcePath(string? sourcePath, string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return string.Empty;
        string full = Path.GetFullPath(
            Path.IsPathRooted(sourcePath)
                ? sourcePath
                : Path.Combine(projectRoot, sourcePath));
        string relative = Path.GetRelativePath(projectRoot, full).Replace('\\', '/');
        return relative == "." || relative.StartsWith("../", StringComparison.Ordinal)
            ? string.Empty
            : relative;
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool TryDecode(string value, out string decoded)
    {
        decoded = string.Empty;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(
                value.Replace('-', '+').Replace('_', '/')
                + new string('=', (4 - value.Length % 4) % 4)));
            return decoded.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private const string InitScriptText = """
        def millerCtBuildRoot = System.getenv('MILLER_CT_GRADLE_BUILD_ROOT')
        if (millerCtBuildRoot == null || millerCtBuildRoot.trim().isEmpty()) {
            throw new GradleException('MILLER_CT_GRADLE_BUILD_ROOT is required for Miller continuous testing')
        }

        def millerCtConfigureProject = { project ->
            def projectSegment = 'project-' + java.util.Base64.getUrlEncoder().withoutPadding().encodeToString(
                project.path.getBytes(java.nio.charset.StandardCharsets.UTF_8))
            project.buildDir = new File(millerCtBuildRoot, projectSegment)
            project.tasks.withType(org.gradle.api.tasks.testing.Test).configureEach { testTask ->
                testTask.reports.junitXml.outputLocation = new File(project.buildDir, 'test-results/test')
            }
        }

        gradle.beforeProject(millerCtConfigureProject)
        """;
}
