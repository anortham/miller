namespace Miller.Testing.Parsing;

internal static class RustCoverageFlagPolicy
{
    private const string CargoEncodedRustFlags = "CARGO_ENCODED_RUSTFLAGS";
    private const string RustFlags = "RUSTFLAGS";
    private const string InstrumentCoverageFlag = "-C instrument-coverage";
    private const char EncodedSeparator = '\u001f';

    internal static IReadOnlyDictionary<string, string?> Create(string projectRoot)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var key in Environment.GetEnvironmentVariables().Keys)
        {
            if (key is string variable)
                environment[variable] = Environment.GetEnvironmentVariable(variable);
        }

        return Create(projectRoot, environment);
    }

    internal static IReadOnlyDictionary<string, string?> Create(
        string projectRoot,
        IReadOnlyDictionary<string, string?> ambientEnvironment) =>
        Create(projectRoot, ambientEnvironment, EnvironmentNameComparison);

    internal static IReadOnlyDictionary<string, string?> Create(
        string projectRoot,
        IReadOnlyDictionary<string, string?> ambientEnvironment,
        StringComparison environmentNameComparison)
    {
        if (TryGetEnvironmentVariable(
                ambientEnvironment,
                CargoEncodedRustFlags,
                environmentNameComparison,
                out var encoded)
            && encoded is not null)
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [CargoEncodedRustFlags] = AppendEncoded(encoded),
            };
        }

        if (TryGetEnvironmentVariable(
                ambientEnvironment,
                RustFlags,
                environmentNameComparison,
                out var plain)
            && plain is not null)
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [RustFlags] = AppendPlain(plain),
            };
        }

        var uncomposableEnvironmentSource = ambientEnvironment.FirstOrDefault(entry =>
            entry.Key.StartsWith("CARGO_", environmentNameComparison)
            && entry.Key.EndsWith("_RUSTFLAGS", environmentNameComparison)
            && !string.Equals(entry.Key, CargoEncodedRustFlags, environmentNameComparison)
            && !string.IsNullOrWhiteSpace(entry.Value));
        if (uncomposableEnvironmentSource.Key is not null)
        {
            throw new ContinuousTestProviderException(
                $"Rust per-test coverage cannot safely compose flags from {uncomposableEnvironmentSource.Key}.");
        }

        foreach (var configPath in CargoConfigPaths(
                     projectRoot,
                     ambientEnvironment,
                     environmentNameComparison))
        {
            if (!File.Exists(configPath))
                continue;

            try
            {
                if (File.ReadLines(configPath).Any(DefinesRustFlags))
                {
                    throw new ContinuousTestProviderException(
                        $"Rust per-test coverage cannot safely compose rustflags from '{configPath}'.");
                }
            }
            catch (ContinuousTestProviderException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ContinuousTestProviderException(
                    $"Rust per-test coverage cannot safely inspect Cargo config '{configPath}'.",
                    exception);
            }
        }

        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [RustFlags] = InstrumentCoverageFlag,
        };
    }

    private static IEnumerable<string> CargoConfigPaths(
        string projectRoot,
        IReadOnlyDictionary<string, string?> ambientEnvironment,
        StringComparison environmentNameComparison)
    {
        var paths = new HashSet<string>(PathComparer);
        for (var directory = new DirectoryInfo(Path.GetFullPath(projectRoot)); directory is not null; directory = directory.Parent)
        {
            var cargoDirectory = Path.Combine(directory.FullName, ".cargo");
            if (paths.Add(Path.Combine(cargoDirectory, "config.toml")))
                yield return Path.Combine(cargoDirectory, "config.toml");
            if (paths.Add(Path.Combine(cargoDirectory, "config")))
                yield return Path.Combine(cargoDirectory, "config");
        }

        var cargoHome = TryGetEnvironmentVariable(
                ambientEnvironment,
                "CARGO_HOME",
                environmentNameComparison,
                out var configuredCargoHome)
            && !string.IsNullOrWhiteSpace(configuredCargoHome)
                ? configuredCargoHome
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cargo");
        var fullCargoHome = Path.GetFullPath(cargoHome);
        if (paths.Add(Path.Combine(fullCargoHome, "config.toml")))
            yield return Path.Combine(fullCargoHome, "config.toml");
        if (paths.Add(Path.Combine(fullCargoHome, "config")))
            yield return Path.Combine(fullCargoHome, "config");
    }

    private static bool DefinesRustFlags(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] == '#')
            return false;

        const string key = "rustflags";
        var offset = 0;
        while (offset < trimmed.Length)
        {
            var keyIndex = trimmed.IndexOf(key, offset, StringComparison.OrdinalIgnoreCase);
            if (keyIndex < 0)
                return false;

            var afterKey = keyIndex + key.Length;
            while (afterKey < trimmed.Length
                   && (char.IsWhiteSpace(trimmed[afterKey]) || trimmed[afterKey] is '\'' or '"'))
            {
                afterKey++;
            }

            if (afterKey < trimmed.Length && trimmed[afterKey] == '=')
                return true;

            offset = keyIndex + key.Length;
        }

        return false;
    }

    private static bool TryGetEnvironmentVariable(
        IReadOnlyDictionary<string, string?> environment,
        string name,
        StringComparison comparison,
        out string? value)
    {
        foreach (var entry in environment)
        {
            if (!string.Equals(entry.Key, name, comparison))
                continue;

            value = entry.Value;
            return true;
        }

        value = null;
        return false;
    }

    private static string AppendPlain(string existing)
    {
        var arguments = existing.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ContainsInstrumentation(arguments))
            return existing;

        if (string.IsNullOrEmpty(existing))
            return InstrumentCoverageFlag;

        return char.IsWhiteSpace(existing[^1])
            ? existing + InstrumentCoverageFlag
            : $"{existing} {InstrumentCoverageFlag}";
    }

    private static string AppendEncoded(string existing)
    {
        var arguments = existing.Split(EncodedSeparator);
        if (ContainsInstrumentation(arguments))
            return existing;

        return string.IsNullOrEmpty(existing)
            ? $"-C{EncodedSeparator}instrument-coverage"
            : existing[^1] == EncodedSeparator
                ? $"{existing}-C{EncodedSeparator}instrument-coverage"
                : $"{existing}{EncodedSeparator}-C{EncodedSeparator}instrument-coverage";
    }

    private static bool ContainsInstrumentation(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], "-Cinstrument-coverage", StringComparison.Ordinal)
                || string.Equals(arguments[index], "-C=instrument-coverage", StringComparison.Ordinal)
                || string.Equals(arguments[index], "-C", StringComparison.Ordinal)
                && index + 1 < arguments.Count
                && string.Equals(arguments[index + 1], "instrument-coverage", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison EnvironmentNameComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
