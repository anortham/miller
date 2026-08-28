using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Miller.Testing.Providers.Qml;

public sealed record QtVersion(int Major, int Minor, int Patch)
{
    public bool IsSupported => Major is 5 or 6;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

public static class QmakeQuickTestTooling
{
    public static string ResolveQmakePath() =>
        LocateOnPath(OperatingSystem.IsWindows() ? "qmake6.exe" : "qmake6")
        ?? LocateOnPath(OperatingSystem.IsWindows() ? "qmake.exe" : "qmake")
        ?? "qmake";

    public static string ResolveMakePath()
    {
        string[] names = OperatingSystem.IsWindows()
            ? ["nmake.exe", "jom.exe", "mingw32-make.exe", "make.exe"]
            : ["make", "gmake"];
        return names.Select(LocateOnPath).FirstOrDefault(path => path is not null) ?? "make";
    }

    public static ImmutableArray<string> BuildVersionArguments() => ["-v"];

    public static ImmutableArray<string> BuildQtVersionArguments() => ["-query", "QT_VERSION"];

    public static ImmutableArray<string> BuildMakeVersionArguments(string? makePath = null)
    {
        string name = string.IsNullOrWhiteSpace(makePath) ? string.Empty : Path.GetFileNameWithoutExtension(makePath);
        return string.Equals(name, "nmake", StringComparison.OrdinalIgnoreCase) ? ["/?"] : ["--version"];
    }

    public static ImmutableArray<string> BuildConfigureArguments(string projectPath, string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        return ["-o", Path.Combine(outputDirectory, "Makefile"), Path.GetFullPath(projectPath)];
    }

    public static ImmutableArray<string> BuildBuildArguments() => [];

    public static ImmutableArray<string> BuildCheckArguments(
        string resultArtifactPath,
        QtVersion version,
        IEnumerable<string>? importPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resultArtifactPath);
        ArgumentNullException.ThrowIfNull(version);
        string fullResultPath = Path.GetFullPath(resultArtifactPath);
        string? resultDirectory = Path.GetDirectoryName(fullResultPath);
        if (string.IsNullOrWhiteSpace(resultDirectory)
            || !string.Equals(Path.GetFileName(resultDirectory), "TestResults", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "QTest result artifacts must be written directly under a generation TestResults directory.",
                nameof(resultArtifactPath));
        }

        string logger = LoggerFormat(version);
        var testArguments = new List<string> { "-o", $"{fullResultPath},{logger}" };
        if (importPaths is not null)
        {
            foreach (string importPath in importPaths)
            {
                if (string.IsNullOrWhiteSpace(importPath))
                    throw new ArgumentException("QML import paths must not be empty.", nameof(importPaths));
                testArguments.Add("-import");
                testArguments.Add(Path.GetFullPath(importPath));
            }
        }

        return ["check", $"TESTARGS={string.Join(' ', testArguments.Select(QuoteMakeValue))}"];
    }

    public static string LoggerFormat(QtVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Major switch
        {
            5 => "xunitxml",
            6 => "junitxml",
            _ => throw new ContinuousTestProviderException(
                $"Qt {version.Major} is unsupported for Qt Quick Test continuous testing; Qt 5 or Qt 6 is required."),
        };
    }

    public static QtVersion ParseQmakeVersion(TestProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ExitCode != 0)
            throw new ContinuousTestProviderException(
                $"qmake -v failed with exit code {result.ExitCode}: {FailureText(result)}");
        return ParseQmakeVersion(result.RequireCompleteStandardOutput("qmake -v"));
    }

    public static QtVersion ParseQmakeVersion(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            throw new ContinuousTestProviderException("qmake -v produced no complete version output.");

        var matches = Regex.Matches(
            output,
            @"(?m)^\s*Using Qt version\s+(?<major>\d{1,9})\.(?<minor>\d{1,9})\.(?<patch>\d{1,9})(?:[^\r\n]*)?\s*$",
            RegexOptions.CultureInvariant);
        if (matches.Count != 1)
            throw new ContinuousTestProviderException(
                "qmake -v did not produce exactly one complete 'Using Qt version major.minor.patch' line.");

        return ParseQtVersion(
            matches[0].Groups["major"].Value,
            matches[0].Groups["minor"].Value,
            matches[0].Groups["patch"].Value,
            "qmake -v");
    }

    public static QtVersion ParseQtVersion(TestProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ExitCode != 0)
            throw new ContinuousTestProviderException(
                $"qmake -query QT_VERSION failed with exit code {result.ExitCode}: {FailureText(result)}");
        return ParseQtVersion(result.RequireCompleteStandardOutput("qmake Qt version query"));
    }

    public static QtVersion ParseQtVersion(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            throw new ContinuousTestProviderException("qmake Qt version query produced no complete version output.");

        var match = Regex.Match(
            output.Trim(),
            @"^(?<major>\d{1,9})\.(?<minor>\d{1,9})\.(?<patch>\d{1,9})$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            throw new ContinuousTestProviderException(
                "qmake Qt version query did not produce one complete major.minor.patch version.");

        return ParseQtVersion(
            match.Groups["major"].Value,
            match.Groups["minor"].Value,
            match.Groups["patch"].Value,
            "qmake Qt version query");
    }

    public static bool HasCheckTarget(string makefileText)
    {
        if (string.IsNullOrWhiteSpace(makefileText))
            return false;
        return Regex.IsMatch(
            makefileText,
            @"(?m)^\s*(?:\.PHONY\s*:\s*)?check\s*:",
            RegexOptions.CultureInvariant);
    }

    public static string ParseTarget(string projectText, string projectPath)
    {
        ArgumentNullException.ThrowIfNull(projectText);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var matches = Regex.Matches(
            projectText,
            @"(?m)^\s*TARGET\s*(?:\+=|=)\s*(?<target>[^#\r\n]+)",
            RegexOptions.CultureInvariant);
        string? target = matches
            .Select(match => match.Groups["target"].Value.Trim())
            .SelectMany(value => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(value => value.Length > 0);
        target ??= Path.GetFileNameWithoutExtension(projectPath);
        if (target.Length == 0
            || target.Any(char.IsWhiteSpace)
            || target.Contains('$', StringComparison.Ordinal)
            || target.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || target.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new ContinuousTestProviderException(
                $"qmake project '{projectPath}' does not declare a usable TARGET.");
        return target;
    }

    public static IReadOnlyList<string> ParseImportPaths(string projectText, string projectDirectory)
    {
        ArgumentNullException.ThrowIfNull(projectText);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        var paths = new List<string>();
        foreach (Match match in Regex.Matches(
                     projectText,
                     @"(?m)^\s*IMPORTPATH\s*(?:\+=|=)\s*(?<paths>[^#\r\n]+)",
                     RegexOptions.CultureInvariant))
        {
            foreach (string value in match.Groups["paths"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (value.Contains("$$", StringComparison.Ordinal))
                    continue;
                paths.Add(Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(projectDirectory, value)));
            }
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string QuoteMakeValue(string value) =>
        value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value;

    private static QtVersion ParseQtVersion(
        string majorText,
        string minorText,
        string patchText,
        string context)
    {
        if (!int.TryParse(majorText, NumberStyles.None, CultureInfo.InvariantCulture, out int major)
            || !int.TryParse(minorText, NumberStyles.None, CultureInfo.InvariantCulture, out int minor)
            || !int.TryParse(patchText, NumberStyles.None, CultureInfo.InvariantCulture, out int patch))
        {
            throw new ContinuousTestProviderException($"{context} contained an invalid Qt version.");
        }

        var version = new QtVersion(major, minor, patch);
        if (!version.IsSupported)
            throw new ContinuousTestProviderException(
                $"Qt {version} is unsupported for Qt Quick Test continuous testing; Qt 5 or Qt 6 is required.");
        return version;
    }

    private static string FailureText(TestProcessResult result)
    {
        var text = !string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardError
            : result.StandardOutput;
        return string.IsNullOrWhiteSpace(text) ? "no diagnostic output" : text.Trim();
    }

    private static string? LocateOnPath(string fileName)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;
        foreach (string directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;
            string candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
