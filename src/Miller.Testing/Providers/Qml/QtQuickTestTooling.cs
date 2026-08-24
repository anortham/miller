using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Miller.Testing.Providers.Qml;

public sealed record CMakeVersion(int Major, int Minor, int Patch)
{
    public bool IsSupported => Major > 3 || Major == 3 && Minor >= 21;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

public static class QtQuickTestTooling
{
    public const int MinimumCMakeMajor = 3;
    public const int MinimumCMakeMinor = 21;

    public static CMakeVersion ParseCMakeVersion(TestProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ExitCode != 0)
            throw new ContinuousTestProviderException(
                $"cmake --version failed with exit code {result.ExitCode}: {FailureText(result)}");

        return ParseCMakeVersion(result.RequireCompleteStandardOutput("cmake --version"));
    }

    public static CMakeVersion ParseCMakeVersion(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            throw InvalidVersionOutput("cmake --version produced no complete version output.");

        var matches = Regex.Matches(
            output,
            @"(?m)^\s*cmake version (?<major>\d{1,9})\.(?<minor>\d{1,9})\.(?<patch>\d{1,9})(?:[^\r\n]*)?\s*$",
            RegexOptions.CultureInvariant);
        if (matches.Count != 1)
            throw InvalidVersionOutput(
                "cmake --version did not produce exactly one complete 'cmake version major.minor.patch' line.");

        var match = matches[0];
        if (!int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
            throw InvalidVersionOutput("cmake --version contained a version component outside the supported range.");

        var version = new CMakeVersion(major, minor, patch);
        if (!version.IsSupported)
            throw new ContinuousTestProviderException(
                $"CMake {version} is unsupported; Qt Quick Test continuous testing requires CMake "
                + $"{MinimumCMakeMajor}.{MinimumCMakeMinor} or newer.");

        return version;
    }

    public static ImmutableArray<string> BuildCMakeVersionArguments() => ["--version"];

    public static ImmutableArray<string> BuildCTestDiscoveryArguments(string buildDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildDirectory);
        return ["--test-dir", buildDirectory, "--show-only=json-v1"];
    }

    public static ImmutableArray<string> BuildCTestRunArguments(
        string buildDirectory,
        string resultArtifactPath,
        IEnumerable<string> selectedNames,
        bool wholeSuite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultArtifactPath);
        ArgumentNullException.ThrowIfNull(selectedNames);

        var arguments = new List<string>
        {
            "--test-dir",
            buildDirectory,
            "--output-junit",
            resultArtifactPath,
            "--no-tests=error",
            "--output-on-failure",
        };
        if (!wholeSuite)
        {
            arguments.Add("-R");
            arguments.Add(ExactTestNameRegex(selectedNames));
        }

        return [.. arguments];
    }

    public static string ExactTestNameRegex(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return $"^(?:{Regex.Escape(name)})$";
    }

    public static string ExactTestNameRegex(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var ordered = names
            .Select(name => name ?? throw new ArgumentException("test names must not contain null", nameof(names)))
            .Select(name => string.IsNullOrWhiteSpace(name)
                ? throw new ArgumentException("test names must not be empty", nameof(names))
                : name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length == 0)
            throw new ArgumentException("test names must contain at least one name", nameof(names));

        return $"^(?:{string.Join('|', ordered.Select(Regex.Escape))})$";
    }

    public static ImmutableDictionary<string, string?> WithDefaultQtPlatform(
        IReadOnlyDictionary<string, string?>? environment)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (environment is not null)
        {
            foreach (var pair in environment)
                builder[pair.Key] = pair.Value;
        }

        if (!builder.Keys.Any(key => string.Equals(key, "QT_QPA_PLATFORM", StringComparison.OrdinalIgnoreCase)))
            builder["QT_QPA_PLATFORM"] = "offscreen";
        return builder.ToImmutable();
    }

    private static ContinuousTestProviderException InvalidVersionOutput(string message) =>
        new(message);

    private static string FailureText(TestProcessResult result)
    {
        var text = !string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardError
            : result.StandardOutput;
        return string.IsNullOrWhiteSpace(text) ? "no diagnostic output" : text.Trim();
    }
}
