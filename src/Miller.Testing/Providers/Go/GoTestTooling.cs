using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Miller.Testing;

internal static partial class GoTestTooling
{
    internal const string Framework = "go";
    internal const int MinimumMajor = 1;
    internal const int MinimumMinor = 24;

    internal sealed record GoPackageInfo(
        string ImportPath,
        string Directory,
        string ModulePath,
        IReadOnlyList<string> TestFiles);

    private static readonly HashSet<string> SelectionFlags = new(StringComparer.Ordinal)
    {
        "-run",
        "-list",
        "-bench",
        "-fuzz",
        "-skip",
        "-count",
    };

    internal static TestProcessCommand BuildVersionCommand(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        string? ambientGoFlags = null) =>
        new(
            "go",
            ["version"],
            ProjectRoot(workspace),
            Environment(workspace, paths, ambientGoFlags));

    internal static TestProcessCommand BuildListCommand(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        string? ambientGoFlags = null) =>
        new(
            "go",
            ["list", "-json", "./..."],
            ProjectRoot(workspace),
            Environment(workspace, paths, ambientGoFlags));

    internal static TestProcessCommand BuildEnvironmentCommand(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        string? ambientGoFlags = null) =>
        new(
            "go",
            ["env", "-json", "GOVERSION", "GOWORK", "GOOS", "GOARCH", "CGO_ENABLED", "GOFLAGS", "GOMOD"],
            ProjectRoot(workspace),
            Environment(workspace, paths, ambientGoFlags));

    internal static TestProcessCommand BuildTestListCommand(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        string importPath,
        string? ambientGoFlags = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(importPath);
        return new(
            "go",
            ["test", "-list", "^Test", "-count=1", importPath],
            ProjectRoot(workspace),
            Environment(workspace, paths, ambientGoFlags));
    }

    internal static TestProcessCommand BuildRunCommand(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        string importPath,
        IReadOnlyList<string> testNames,
        string? ambientGoFlags = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(importPath);
        ArgumentNullException.ThrowIfNull(testNames);

        var args = new List<string> { "test", "-json", "-count=1" };
        if (testNames.Count > 0)
        {
            args.Add("-run");
            args.Add(TopLevelRunExpression(testNames));
        }

        args.Add(importPath);
        return new("go", args, ProjectRoot(workspace), Environment(workspace, paths, ambientGoFlags));
    }

    internal static IReadOnlyDictionary<string, string> ParseEnvironment(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            throw new ContinuousTestProviderException("go env returned no environment metadata.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ContinuousTestProviderException("go env returned a non-object environment document.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    throw new ContinuousTestProviderException(
                        $"go env returned a non-string value for '{property.Name}'.");
                values[property.Name] = property.Value.GetString() ?? string.Empty;
            }

            return values;
        }
        catch (JsonException exception)
        {
            throw new ContinuousTestProviderException("go env returned malformed JSON.", exception);
        }
    }

    internal static IReadOnlyList<GoPackageInfo> ParsePackageList(
        string? output,
        string fallbackModulePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackModulePath);
        if (string.IsNullOrWhiteSpace(output))
            throw new ContinuousTestProviderException("go list returned no package metadata.");

        var packages = new List<GoPackageInfo>();
        var reader = new Utf8JsonReader(
            Encoding.UTF8.GetBytes(output),
            new JsonReaderOptions { AllowMultipleValues = true });
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.Comment)
                    continue;
                try
                {
                    if (reader.TokenType != JsonTokenType.StartObject)
                        throw new ContinuousTestProviderException("go list returned an incomplete package record.");
                    using JsonDocument document = JsonDocument.ParseValue(ref reader);
                    JsonElement root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object
                        || !TryString(root, "ImportPath", out string? importPath)
                        || !TryString(root, "Dir", out string? directory))
                        throw new ContinuousTestProviderException("go list returned an incomplete package record.");
                    if (root.TryGetProperty("Incomplete", out JsonElement incomplete)
                        && incomplete.ValueKind == JsonValueKind.True)
                        throw new ContinuousTestProviderException(
                            $"go list marked package '{importPath}' incomplete.");
                    if (root.TryGetProperty("Error", out JsonElement error)
                        && error.ValueKind is not JsonValueKind.Null)
                        throw new ContinuousTestProviderException(
                            $"go list reported an error for package '{importPath}'.");

                    string modulePath = fallbackModulePath;
                    if (root.TryGetProperty("Module", out JsonElement module)
                        && module.ValueKind == JsonValueKind.Object
                        && TryString(module, "Path", out string? listedModule)
                        && !string.IsNullOrWhiteSpace(listedModule))
                        modulePath = listedModule;

                    var files = new List<string>();
                    AddFileNames(root, "TestGoFiles", files);
                    AddFileNames(root, "XTestGoFiles", files);
                    if (files.Count > 0)
                    {
                        packages.Add(new GoPackageInfo(
                            importPath!,
                            Path.GetFullPath(directory!),
                            modulePath,
                            files.Distinct(StringComparer.Ordinal).ToArray()));
                    }
                }
                catch (JsonException exception)
                {
                    throw new ContinuousTestProviderException("go list returned malformed JSON.", exception);
                }
            }
        }
        catch (JsonException exception)
        {
            throw new ContinuousTestProviderException("go list returned malformed JSON.", exception);
        }

        return packages
            .GroupBy(package => package.ImportPath, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(package => package.ImportPath, StringComparer.Ordinal)
            .ToArray();
    }

    internal static IReadOnlyDictionary<string, string?> Environment(
        ContinuousTestWorkspace workspace,
        CtGenerationPaths paths,
        string? ambientGoFlags = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(paths);

        Directory.CreateDirectory(CtGenerationPaths.CacheDirectory(workspace, "go"));
        Directory.CreateDirectory(paths.TempDirectory);

        string? goWork = workspace.Metadata.TryGetValue("go_work", out object? value)
            && value is string configured
            && !string.IsNullOrWhiteSpace(configured)
            && !string.Equals(configured, "off", StringComparison.OrdinalIgnoreCase)
            && File.Exists(configured)
            ? Path.GetFullPath(configured)
            : null;
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GOCACHE"] = CtGenerationPaths.CacheDirectory(workspace, "go"),
            ["GOTMPDIR"] = paths.TempDirectory,
            ["GOWORK"] = goWork ?? "off",
            ["GOFLAGS"] = SanitizeGoFlags(ambientGoFlags ?? System.Environment.GetEnvironmentVariable("GOFLAGS")),
        };
        return new ReadOnlyDictionary<string, string?>(environment);
    }

    internal static string TopLevelRunExpression(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var unique = names
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Select(Regex.Escape)
            .ToArray();
        if (unique.Length == 0)
            throw new ArgumentException("must contain at least one test name", nameof(names));

        return $"^(?:{string.Join("|", unique)})$";
    }

    internal static string SanitizeGoFlags(string? flags)
    {
        if (string.IsNullOrWhiteSpace(flags))
            return string.Empty;

        var kept = new List<string>();
        string[] tokens = flags.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < tokens.Length; index++)
        {
            string token = tokens[index];
            string name = token;
            int equals = token.IndexOf('=', StringComparison.Ordinal);
            if (equals >= 0)
                name = token[..equals];
            if (SelectionFlags.Contains(name))
            {
                if (equals < 0 && index + 1 < tokens.Length)
                    index++;
                continue;
            }

            kept.Add(token);
        }

        return string.Join(" ", kept);
    }

    internal static string EncodeCaseId(
        string workspaceId,
        string projectPath,
        string modulePath,
        string importPath,
        string testName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modulePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(importPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(testName);
        return string.Join(
            ':',
            "go-test",
            Encode(workspaceId),
            Encode(Path.GetFullPath(projectPath)),
            Encode(modulePath),
            Encode(importPath),
            "test",
            Encode(testName));
    }

    internal static bool TryDecodeCaseId(
        string id,
        out GoTestCaseIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(id))
            return false;
        string[] parts = id.Split(':');
        if (parts.Length != 7
            || !string.Equals(parts[0], "go-test", StringComparison.Ordinal)
            || !string.Equals(parts[5], "test", StringComparison.Ordinal))
            return false;
        try
        {
            if (!TryDecode(parts[1], out string workspaceId)
                || !TryDecode(parts[2], out string projectPath)
                || !TryDecode(parts[3], out string modulePath)
                || !TryDecode(parts[4], out string importPath)
                || !TryDecode(parts[6], out string testName))
                return false;
            identity = new GoTestCaseIdentity(workspaceId, projectPath, modulePath, importPath, testName);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static bool TryParseVersion(string? output, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(output))
            return false;
        Match match = VersionRegex().Match(output);
        if (!match.Success)
            return false;
        string[] components = match.Groups["version"].Value.Split('.');
        if (!int.TryParse(components[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major)
            || !int.TryParse(components[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minor))
            return false;
        int patch = components.Length > 2
            && int.TryParse(components[2], NumberStyles.None, CultureInfo.InvariantCulture, out int parsedPatch)
            ? parsedPatch
            : 0;
        version = new Version(major, minor, patch);
        return true;
    }

    internal static bool IsSupportedVersion(Version version) =>
        version.Major > MinimumMajor
        || version.Major == MinimumMajor && version.Minor >= MinimumMinor;

    internal static string? ReadModulePath(string goModPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goModPath);
        try
        {
            foreach (string line in File.ReadLines(goModPath))
            {
                string text = line.Trim();
                if (text.StartsWith("module ", StringComparison.Ordinal))
                {
                    string module = text["module ".Length..].Trim();
                    int comment = module.IndexOf("//", StringComparison.Ordinal);
                    if (comment >= 0)
                        module = module[..comment].Trim();
                    return module.Length == 0 ? null : module;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    internal static string ProjectRoot(ContinuousTestWorkspace workspace) =>
        string.Equals(Path.GetFileName(workspace.ProjectPath), "go.mod", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(workspace.ProjectPath) ?? workspace.WorkspaceRoot
            : workspace.ProjectPath;

    private static bool TryString(JsonElement root, string property, out string? value)
    {
        value = root.TryGetProperty(property, out JsonElement element)
            && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static void AddFileNames(JsonElement root, string property, List<string> files)
    {
        if (!root.TryGetProperty(property, out JsonElement values)
            || values.ValueKind != JsonValueKind.Array)
            return;
        foreach (JsonElement value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                files.Add(value.GetString()!);
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

    [GeneratedRegex(@"\bgo(?<version>\d+\.\d+(?:\.\d+)?)\b", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();
}

internal readonly record struct GoTestCaseIdentity(
    string WorkspaceId,
    string ProjectPath,
    string ModulePath,
    string ImportPath,
    string TestName);
