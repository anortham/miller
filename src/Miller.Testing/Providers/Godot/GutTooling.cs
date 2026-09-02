using System.Globalization;
using System.Text.RegularExpressions;
using Miller.Testing.Providers.Shared;

namespace Miller.Testing.Providers.Godot;

internal static class GutTooling
{
    internal const string Framework = "gut";
    internal const int MinimumGodotMajor = 4;
    internal const int MinimumGutMajor = 9;

    internal static TestProcessCommand BuildVersionCommand(
        string executable,
        GodotProjectShadowResult shadow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(shadow);
        return new(
            executable,
            ["--version"],
            shadow.ProjectMirrorRoot,
            BuildEnvironment(shadow));
    }

    internal static TestProcessCommand BuildImportCommand(
        string executable,
        GodotProjectShadowResult shadow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(shadow);
        return new(
            executable,
            ["--headless", "--path", shadow.ProjectMirrorRoot, "--import"],
            shadow.ProjectMirrorRoot,
            BuildEnvironment(shadow));
    }

    internal static TestProcessCommand BuildRunCommand(
        string executable,
        GodotProjectShadowResult shadow,
        string configPath,
        string reportPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(shadow);
        string configResPath = NormalizeResPath(configPath);
        string reportResPath = NormalizeResPath(reportPath);
        return new(
            executable,
            [
                "--headless",
                "--path",
                shadow.ProjectMirrorRoot,
                "-s",
                "addons/gut/gut_cmdln.gd",
                "-gexit",
                "-gdisable_colors",
                $"-gconfig={configResPath}",
                $"-gjunit_xml_file={reportResPath}",
            ],
            shadow.ProjectMirrorRoot,
            BuildEnvironment(shadow));
    }

    internal static IReadOnlyDictionary<string, string?> BuildEnvironment(
        GodotProjectShadowResult shadow)
    {
        ArgumentNullException.ThrowIfNull(shadow);
        string home = Path.GetFullPath(shadow.GodotHomeRoot);
        CtWorkspaceMirror.EnsurePathHasNoReparsePoint(home);
        Directory.CreateDirectory(home);

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            AddChild(environment, "USERPROFILE", home, "profile");
            AddChild(environment, "APPDATA", home, "appdata");
            AddChild(environment, "LOCALAPPDATA", home, "localappdata");
            AddChild(environment, "HOME", home, "home");
            AddChild(environment, "TEMP", home, "temp");
            AddChild(environment, "TMP", home, "tmp");
        }
        else
        {
            AddChild(environment, "HOME", home, "home");
            AddChild(environment, "XDG_DATA_HOME", home, "data");
            AddChild(environment, "XDG_CONFIG_HOME", home, "config");
            AddChild(environment, "XDG_CACHE_HOME", home, "cache");
            AddChild(environment, "TMPDIR", home, "tmpdir");
            AddChild(environment, "TEMP", home, "temp");
            AddChild(environment, "TMP", home, "tmp");
        }

        return environment;
    }

    internal static string ResolveGodotExecutable()
    {
        string? configured = Environment.GetEnvironmentVariable("GODOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string? resolved = ResolveCandidate(configured.Trim());
            if (resolved is null)
                throw new ContinuousTestProviderException(
                    $"GODOT points to an unavailable executable: '{configured.Trim()}'.");
            return resolved;
        }

        string[] names = OperatingSystem.IsWindows()
            ? ["godot", "godot4", "godot.exe", "godot4.exe", "godot.cmd", "godot4.cmd", "godot.bat", "godot4.bat"]
            : ["godot", "godot4", "godot.exe", "godot4.exe"];
        foreach (string name in names)
        {
            string? resolved = ResolveCandidate(name);
            if (resolved is not null)
                return resolved;
        }

        throw new ContinuousTestProviderException(
            "Godot 4 executable was not found. Set GODOT or add godot/godot4 to PATH.");
    }

    internal static int ParseGodotMajor(TestProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return ParseGodotMajor(result.RequireCompleteStandardOutput("Godot version probe"));
    }

    internal static int ParseGodotMajor(string output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        Match labeled = Regex.Match(
            output,
            @"(?i)\bGodot(?:\s+Engine)?(?:\s+v)?\s*(?<major>\d+)\.\d+",
            RegexOptions.CultureInvariant);
        Match plain = Regex.Match(
            output.Trim(),
            @"\A(?<major>\d+)\.\d+(?:\.(?:\d+|[A-Za-z][A-Za-z0-9_-]*))+\z",
            RegexOptions.CultureInvariant);
        Match match = labeled.Success ? labeled : plain;
        if (!match.Success
            || !int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int major))
            throw new ContinuousTestProviderException("Godot version probe returned no parseable semantic version.");
        return major;
    }

    internal static int ReadGutMajor(string pluginPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginPath);
        if (!File.Exists(pluginPath))
            throw new ContinuousTestProviderException(
                $"GUT plugin evidence is missing at '{pluginPath}'.");

        string text;
        try
        {
            text = File.ReadAllText(pluginPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ContinuousTestProviderException(
                $"GUT plugin evidence could not be read at '{pluginPath}'.", exception);
        }

        Match match = Regex.Match(
            text,
            "(?im)^\\s*version\\s*=\\s*[\\\"'](?<major>\\d+)(?:\\.\\d+){0,3}[\\\"']\\s*$",
            RegexOptions.CultureInvariant);
        if (!match.Success
            || !int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int major))
            throw new ContinuousTestProviderException(
                $"GUT plugin evidence has no parseable version at '{pluginPath}'.");
        return major;
    }

    internal static void EnsureSupportedGutProject(GodotProjectShadowResult shadow)
    {
        ArgumentNullException.ThrowIfNull(shadow);
        string pluginPath = Path.Combine(shadow.ProjectMirrorRoot, "addons", "gut", "plugin.cfg");
        CtWorkspaceMirror.EnsurePathHasNoReparsePoint(pluginPath);
        int major = ReadGutMajor(pluginPath);
        if (major < MinimumGutMajor)
            throw new ContinuousTestProviderException(
                $"GUT major version {major} is unsupported; Miller CT requires GUT {MinimumGutMajor}.");
    }

    internal static string NormalizeResPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string value = path.Trim().Replace('\\', '/');
        if (value.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
            value = value[6..];
        else if (value.StartsWith("res:", StringComparison.OrdinalIgnoreCase))
            throw new ContinuousTestProviderException($"invalid Godot resource path '{path}'.");

        if (value.Length == 0 || value.StartsWith("/", StringComparison.Ordinal) || IsWindowsRooted(value))
            throw new ContinuousTestProviderException($"Godot resource path is not a relative res:// path: '{path}'.");

        var segments = new List<string>();
        foreach (string segment in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is ".")
                continue;
            if (segment is "..")
                throw new ContinuousTestProviderException($"Godot resource path escapes res://: '{path}'.");
            segments.Add(segment);
        }

        if (segments.Count == 0)
            throw new ContinuousTestProviderException($"Godot resource path is empty: '{path}'.");
        return "res://" + string.Join('/', segments);
    }

    private static void AddChild(
        IDictionary<string, string?> environment,
        string key,
        string root,
        string name)
    {
        string path = Path.GetFullPath(Path.Combine(root, name));
        if (!IsContainedChild(path, root))
            throw new IOException($"Godot environment path escapes its home root: '{path}'.");
        CtWorkspaceMirror.EnsurePathHasNoReparsePoint(path);
        Directory.CreateDirectory(path);
        environment[key] = path;
    }

    private static string? ResolveCandidate(string candidate)
    {
        if (Path.IsPathRooted(candidate) || candidate.Contains(Path.DirectorySeparatorChar) || candidate.Contains(Path.AltDirectorySeparatorChar))
        {
            string full = Path.GetFullPath(candidate);
            return ExistingExecutable(full);
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string full = Path.GetFullPath(Path.Combine(directory, candidate));
            string? executable = ExistingExecutable(full);
            if (executable is not null)
                return executable;
        }
        return null;
    }

    private static string? ExistingExecutable(string path)
    {
        if (File.Exists(path))
            return path;
        if (!OperatingSystem.IsWindows() || Path.HasExtension(path))
            return null;
        foreach (string suffix in new[] { ".exe", ".cmd", ".bat" })
        {
            string candidate = path + suffix;
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static bool IsContainedChild(string path, string root)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return !string.Equals(fullPath, fullRoot, comparison)
            && fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static bool IsWindowsRooted(string value) =>
        value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':';
}
