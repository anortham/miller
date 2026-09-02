using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace Miller.Testing.Providers.Php;

internal static class PhpTestTooling
{
    internal const string ProjectFileName = "composer.json";
    internal const string PhpUnitFramework = "phpunit";
    internal const string PestFramework = "pest";

    internal static string? DetectFramework(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        if (!File.Exists(projectPath))
            return null;

        string text;
        try
        {
            text = File.ReadAllText(projectPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (text.Contains("pestphp/pest", StringComparison.OrdinalIgnoreCase))
            return PestFramework;
        if (text.Contains("phpunit/phpunit", StringComparison.OrdinalIgnoreCase))
            return PhpUnitFramework;
        return null;
    }

    internal static TestProcessCommand BuildDiscoveryCommand(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        string framework)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(framework);
        paths.EnsureDirectories();
        string artifactPath = Path.Combine(paths.ResultsDirectory, "php-discovery.xml");
        ResetArtifact(artifactPath);
        return BuildCommand(workspace, paths, framework, ["--list-tests-xml", artifactPath]);
    }

    internal static TestProcessCommand BuildRunCommand(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        string framework,
        string artifactPath,
        IReadOnlyList<string> selectors,
        bool wholeSuite)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(framework);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        ArgumentNullException.ThrowIfNull(selectors);
        paths.EnsureDirectories();
        ResetArtifact(artifactPath);

        var arguments = new List<string> { "--log-junit", Path.GetFullPath(artifactPath) };
        if (!wholeSuite)
        {
            if (selectors.Count == 0)
                throw new ContinuousTestProviderException(
                    "PHP test run request selected no test case IDs; an empty selection cannot be reported green.");
            arguments.Add("--filter");
            arguments.Add(BuildFilter(selectors));
        }

        return BuildCommand(workspace, paths, framework, arguments);
    }

    internal static string ProjectRoot(ContinuousTestWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        string projectPath = Path.GetFullPath(workspace.ProjectPath);
        return IsPhpProjectFile(projectPath)
            ? Path.GetDirectoryName(projectPath) ?? workspace.WorkspaceRoot
            : Directory.Exists(projectPath)
                ? projectPath
                : Path.GetDirectoryName(projectPath) ?? workspace.WorkspaceRoot;
    }

    internal static bool IsPhpProjectFile(string path) =>
        string.Equals(Path.GetFileName(path), ProjectFileName, StringComparison.OrdinalIgnoreCase);

    internal static string RunnerPath(ContinuousTestWorkspace workspace, string framework)
    {
        string root = ProjectRoot(workspace);
        string basePath = Path.Combine(root, "vendor", "bin", framework);
        string[] candidates = OperatingSystem.IsWindows()
            ? [basePath + ".bat", basePath, basePath + ".cmd", basePath + ".exe"]
            : [basePath];
        string? runner = candidates.FirstOrDefault(File.Exists);
        if (runner is null)
            throw new ContinuousTestProviderException(
                $"PHP {framework} runner is missing at '{basePath}'. Run composer install to restore vendor/bin/{framework}.");
        return runner;
    }

    internal static string EncodeCaseId(
        string workspaceId,
        string projectPath,
        string className,
        string methodName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        return string.Join(':',
            "php-test",
            Encode(workspaceId),
            Encode(Path.GetFullPath(projectPath)),
            Encode(NormalizeClassName(className)),
            Encode(methodName.Trim()));
    }

    internal static bool TryDecodeCaseId(string id, out PhpTestCaseIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(id))
            return false;

        string[] parts = id.Split(':');
        if (parts.Length != 5 || !string.Equals(parts[0], "php-test", StringComparison.Ordinal))
            return false;
        if (!TryDecode(parts[1], out string workspaceId)
            || !TryDecode(parts[2], out string projectPath)
            || !TryDecode(parts[3], out string className)
            || !TryDecode(parts[4], out string methodName))
        {
            return false;
        }

        try
        {
            identity = new PhpTestCaseIdentity(
                workspaceId,
                Path.GetFullPath(projectPath),
                NormalizeClassName(className),
                methodName.Trim());
            return identity.ClassName.Length > 0 && identity.MethodName.Length > 0;
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

    internal static string NormalizeClassName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string normalized = value.Trim();
        while (normalized.Contains("\\\\", StringComparison.Ordinal))
            normalized = normalized.Replace("\\\\", "\\", StringComparison.Ordinal);
        return normalized;
    }

    internal static string NormalizeMethodName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }

    internal static bool TrySplitSelector(
        string value,
        out string className,
        out string methodName)
    {
        className = string.Empty;
        methodName = string.Empty;
        int separator = value.IndexOf("::", StringComparison.Ordinal);
        if (separator <= 0 || separator + 2 >= value.Length)
            return false;

        className = NormalizeClassName(value[..separator]);
        methodName = NormalizeMethodName(value[(separator + 2)..]);
        return true;
    }

    internal static string ResultArtifactPath(CtGenerationPaths paths, string runId, int? part = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId))).ToLowerInvariant();
        string suffix = part is null ? string.Empty : $".part{part.Value:D3}";
        return Path.Combine(paths.ResultsDirectory, $"run-{hash}{suffix}.xml");
    }

    private static TestProcessCommand BuildCommand(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        string framework,
        IReadOnlyList<string> arguments)
    {
        string root = ProjectRoot(workspace);
        return new TestProcessCommand(
            RunnerPath(workspace, framework),
            arguments.ToArray(),
            root,
            WorkspaceEnvironment(workspace, paths));
    }

    private static string BuildFilter(IReadOnlyList<string> selectors)
    {
        string[] escaped = selectors
            .Select(selector => RegexEscape(selector))
            .ToArray();
        return $"^(?:{string.Join('|', escaped)})$";
    }

    private static string RegexEscape(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        const string metacharacters = @"\\^$.*+?()[]{}|/";
        var builder = new StringBuilder(value.Length * 2);
        foreach (char character in value)
        {
            if (metacharacters.IndexOf(character) >= 0)
                builder.Append('\\');
            builder.Append(character);
        }
        return builder.ToString();
    }

    private static IReadOnlyDictionary<string, string?> WorkspaceEnvironment(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths)
    {
        Directory.CreateDirectory(paths.TempDirectory);
        return new ReadOnlyDictionary<string, string?>(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [CtEnvironment.WorkspaceRoot] = workspace.WorkspaceRoot,
                [CtEnvironment.DaemonWorkspaceRoot] = null,
                ["TMPDIR"] = paths.TempDirectory,
                ["TMP"] = paths.TempDirectory,
                ["TEMP"] = paths.TempDirectory,
            });
    }

    private static void ResetArtifact(string artifactPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        try
        {
            if (File.Exists(artifactPath))
                File.Delete(artifactPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool TryDecode(string value, out string decoded)
    {
        decoded = string.Empty;
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            return decoded.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

internal readonly record struct PhpTestCaseIdentity(
    string WorkspaceId,
    string ProjectPath,
    string ClassName,
    string MethodName)
{
    public string Selector => $"{ClassName}::{MethodName}";
}
