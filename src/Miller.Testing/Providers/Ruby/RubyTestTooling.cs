using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace Miller.Testing;

internal static class RubyTestTooling
{
    internal const string Framework = "rspec";
    internal const string ProjectFileName = "Gemfile";

    internal static TestProcessCommand BuildDiscoveryCommand(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(paths);
        return BuildCommand(workspace, paths, ["--dry-run", "--format", "json"]);
    }

    internal static TestProcessCommand BuildRunCommand(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        string artifactPath,
        IReadOnlyList<string> selectors,
        bool wholeSuite)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        ArgumentNullException.ThrowIfNull(selectors);

        var arguments = new List<string> { "--format", "json", "--out", Path.GetFullPath(artifactPath) };
        if (!wholeSuite)
            arguments.AddRange(selectors);
        return BuildCommand(workspace, paths, arguments);
    }

    internal static string ProjectRoot(ContinuousTestWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        string projectPath = Path.GetFullPath(workspace.ProjectPath);
        return IsRubyProjectFile(projectPath)
            ? Path.GetDirectoryName(projectPath) ?? workspace.WorkspaceRoot
            : Directory.Exists(projectPath)
                ? projectPath
                : Path.GetDirectoryName(projectPath) ?? workspace.WorkspaceRoot;
    }

    internal static bool IsRubyProjectFile(string path) =>
        string.Equals(Path.GetFileName(path), ProjectFileName, StringComparison.OrdinalIgnoreCase);

    internal static string EncodeCaseId(
        string workspaceId,
        string projectPath,
        string specFilePath,
        string exampleId,
        string? selector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(specFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(exampleId);

        var parts = new List<string>
        {
            "ruby-test",
            Encode(workspaceId),
            Encode(Path.GetFullPath(projectPath)),
            Encode(NormalizeRelativePath(specFilePath)),
            Encode(exampleId),
        };
        if (!string.IsNullOrWhiteSpace(selector))
            parts.Add(Encode(selector));
        return string.Join(':', parts);
    }

    internal static bool TryDecodeCaseId(
        string id,
        out RubyTestCaseIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(id))
            return false;

        string[] parts = id.Split(':');
        if (parts.Length is not (5 or 6)
            || !string.Equals(parts[0], "ruby-test", StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryDecode(parts[1], out string workspaceId)
            || !TryDecode(parts[2], out string projectPath)
            || !TryDecode(parts[3], out string specFilePath)
            || !TryDecode(parts[4], out string exampleId))
        {
            return false;
        }

        string? selector = null;
        if (parts.Length == 6)
        {
            if (!TryDecode(parts[5], out selector) || string.IsNullOrWhiteSpace(selector))
                return false;
        }

        try
        {
            identity = new RubyTestCaseIdentity(
                workspaceId,
                Path.GetFullPath(projectPath),
                NormalizeRelativePath(specFilePath),
                exampleId,
                selector);
            return true;
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

    internal static string NormalizeRelativePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized;
    }

    internal static bool TryRelativeSpecPath(
        string projectRoot,
        string reportedPath,
        out string relativePath)
    {
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(reportedPath))
            return false;

        string fullPath = Path.GetFullPath(
            Path.IsPathRooted(reportedPath)
                ? reportedPath
                : Path.Combine(projectRoot, reportedPath));
        string relative = Path.GetRelativePath(Path.GetFullPath(projectRoot), fullPath);
        if (relative == "."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison)
            || relative.StartsWith("../", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            return false;
        }

        relativePath = NormalizeRelativePath(relative);
        return relativePath.Length > 0;
    }

    internal static string ResultArtifactPath(CtGenerationPaths paths, string runId)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        string runHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId))).ToLowerInvariant();
        return Path.Combine(paths.ResultsDirectory, $"run-{runHash}.rspec.json");
    }

    private static TestProcessCommand BuildCommand(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        IReadOnlyList<string> rspecArguments)
    {
        string projectRoot = ProjectRoot(workspace);
        var environment = new ReadOnlyDictionary<string, string?>(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["BUNDLE_GEMFILE"] = Path.Combine(projectRoot, ProjectFileName),
            });

        if (!string.IsNullOrWhiteSpace(workspace.Command))
        {
            IReadOnlyList<string> command = SplitCommand(workspace.Command);
            if (command.Count == 0)
                throw new ContinuousTestProviderException("Ruby test command must not be empty.");
            return new TestProcessCommand(
                command[0],
                command.Skip(1).Concat(rspecArguments).ToArray(),
                projectRoot,
                environment);
        }

        string lockPath = Path.Combine(projectRoot, "Gemfile.lock");
        if (File.Exists(lockPath))
        {
            return new TestProcessCommand(
                "bundle",
                new[] { "exec", "rspec" }.Concat(rspecArguments).ToArray(),
                projectRoot,
                environment);
        }

        return new TestProcessCommand(
            "rspec",
            rspecArguments.ToArray(),
            projectRoot,
            environment);
    }

    private static IReadOnlyList<string> SplitCommand(string command)
    {
        var tokens = new List<string>();
        var token = new StringBuilder();
        bool quoted = false;
        char quote = '\0';
        foreach (char character in command)
        {
            if (quoted)
            {
                if (character == quote)
                    quoted = false;
                else
                    token.Append(character);
                continue;
            }

            if (character is '\'' or '"')
            {
                quoted = true;
                quote = character;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (token.Length > 0)
                {
                    tokens.Add(token.ToString());
                    token.Clear();
                }
            }
            else
            {
                token.Append(character);
            }
        }

        if (quoted)
            throw new ContinuousTestProviderException("Ruby test command contains an unterminated quote.");
        if (token.Length > 0)
            tokens.Add(token.ToString());
        return tokens;
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

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

internal readonly record struct RubyTestCaseIdentity(
    string WorkspaceId,
    string ProjectPath,
    string SpecFilePath,
    string ExampleId,
    string? Selector);
