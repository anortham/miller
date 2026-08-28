using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Miller.Testing.Providers.Qml;

public sealed record QtVersion(int Major, int Minor, int Patch)
{
    public bool IsSupported => Major is 5 or 6;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

internal sealed record QmakeProjectModel(
    string RootPath,
    IReadOnlyList<string> IncludedFiles,
    string EffectiveText)
{
    public string RootDirectory => Path.GetDirectoryName(RootPath) ?? RootPath;
}

public static class QmakeQuickTestTooling
{
    private const int MaxProjectCharacters = 64 * 1024;
    private const int MaxIncludedFiles = 128;

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

    internal static bool TryReadProjectModel(string projectPath, out QmakeProjectModel? model)
    {
        model = null;
        if (string.IsNullOrWhiteSpace(projectPath)
            || !string.Equals(Path.GetExtension(projectPath), ".pro", StringComparison.OrdinalIgnoreCase))
            return false;

        string rootPath;
        try
        {
            rootPath = Path.GetFullPath(projectPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }

        if (!TryReadBoundedText(rootPath, out string rootText))
            return false;

        var files = new List<string> { rootPath };
        var texts = new List<string> { rootText };
        var pending = new Queue<(string Path, string Text)>();
        pending.Enqueue((rootPath, rootText));
        var visited = new HashSet<string>(PathComparer) { rootPath };
        string rootDirectory = Path.GetDirectoryName(rootPath) ?? rootPath;
        while (pending.Count > 0)
        {
            (string includingPath, string includingText) = pending.Dequeue();
            if (!TryReadLiteralIncludes(includingText, out IReadOnlyList<string> includes))
                return false;

            foreach (string include in includes)
            {
                string fullInclude;
                try
                {
                    fullInclude = Path.GetFullPath(Path.Combine(
                        Path.GetDirectoryName(includingPath) ?? rootDirectory,
                        include));
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
                {
                    return false;
                }

                if (!IsInside(rootDirectory, fullInclude)
                    || !string.Equals(Path.GetExtension(fullInclude), ".pri", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!visited.Add(fullInclude))
                    continue;
                if (files.Count >= MaxIncludedFiles
                    || !TryReadBoundedText(fullInclude, out string includedText))
                    return false;
                files.Add(fullInclude);
                texts.Add(includedText);
                pending.Enqueue((fullInclude, includedText));
            }
        }

        model = new QmakeProjectModel(rootPath, files, string.Join('\n', texts));
        return true;
    }

    internal static bool HasVariableValue(string text, string variable, string expected) =>
        QmakeVariableValues(text, variable).Contains(expected, StringComparer.OrdinalIgnoreCase);

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

    private static HashSet<string> QmakeVariableValues(string text, string variable)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(
                     StripQmakeComments(text),
                     $@"(?im)^\s*{Regex.Escape(variable)}\s*(?<operator>\+=|-=|=)\s*(?<values>[^\r\n]+)"))
        {
            string operation = match.Groups["operator"].Value;
            var words = match.Groups["values"].Value
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(word => word.Trim().Trim('"', '\''))
                .Where(word => word.Length > 0)
                .ToArray();
            if (operation == "=")
                values.Clear();
            if (operation == "-=")
            {
                foreach (string word in words)
                    values.Remove(word);
            }
            else
            {
                foreach (string word in words)
                    values.Add(word);
            }
        }
        return values;
    }

    private static bool TryReadLiteralIncludes(string text, out IReadOnlyList<string> includes)
    {
        string code = StripQmakeComments(text);
        var values = new List<string>();
        MatchCollection matches = Regex.Matches(
            code,
            @"(?im)^\s*include\s*\(\s*(?<path>[^)\r\n]+?)\s*\)\s*$");
        foreach (Match match in matches)
        {
            string value = match.Groups["path"].Value.Trim().Trim('"', '\'');
            if (value.Length == 0
                || value.Contains('$', StringComparison.Ordinal)
                || !string.Equals(Path.GetExtension(value), ".pri", StringComparison.OrdinalIgnoreCase)
                || Path.IsPathRooted(value))
            {
                includes = [];
                return false;
            }
            values.Add(value);
        }

        int includeCalls = Regex.Matches(code, @"(?im)\binclude\s*\(").Count;
        if (includeCalls != matches.Count)
        {
            includes = [];
            return false;
        }

        includes = values;
        return true;
    }

    private static bool TryReadBoundedText(string path, out string text)
    {
        text = string.Empty;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxProjectCharacters)
                return false;
            var buffer = new byte[(int)stream.Length];
            int read = 0;
            while (read < buffer.Length)
            {
                int chunk = stream.Read(buffer, read, buffer.Length - read);
                if (chunk == 0)
                    return false;
                read += chunk;
            }
            text = Encoding.UTF8.GetString(buffer);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string StripQmakeComments(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (string line in text.Split('\n'))
        {
            bool quoted = false;
            int end = line.Length;
            for (int index = 0; index < line.Length; index++)
            {
                if (line[index] == '"')
                    quoted = !quoted;
                else if (line[index] == '#' && !quoted)
                {
                    end = index;
                    break;
                }
            }
            builder.Append(line.AsSpan(0, end)).Append('\n');
        }
        return builder.ToString();
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool IsInside(string root, string path)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative == "."
            || (!relative.StartsWith("..", PathComparison) && !Path.IsPathRooted(relative));
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
