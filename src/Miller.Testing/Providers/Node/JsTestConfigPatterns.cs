using System.Text;
using System.Text.Json;

namespace Miller.Testing;

internal sealed record JsRunnerVersionEvidence(
    bool InstalledManifestFound,
    string? InstalledVersion,
    IReadOnlyList<string> DependencyRanges,
    string? Diagnostic);

internal sealed record JsTestConfigResult(
    bool HasDiscoveryProperty,
    IReadOnlyList<string>? IncludePatterns,
    IReadOnlyList<string> ExcludePatterns,
    bool HasExcludeProperty,
    string? RootDir,
    string? Diagnostic)
{
    internal JsTestPatternSet ToPatternSet(string framework, IReadOnlyList<string> defaults)
    {
        if (Diagnostic is not null)
            throw new ContinuousTestProviderException(Diagnostic);

        var include = HasDiscoveryProperty ? IncludePatterns ?? [] : defaults;
        if (string.Equals(framework, "jest", StringComparison.OrdinalIgnoreCase))
            return JsTestPatternSet.ForJest(include);

        // Vitest keeps its default excludes when a config overrides only `include`;
        // an explicit `exclude` replaces them.
        var exclude = HasExcludeProperty || !HasDiscoveryProperty
            ? ExcludePatterns
            : JsFrameworkTestFileDiscovery.VitestDefaultExcludes;
        return JsTestPatternSet.ForVitest(include, exclude);
    }
}

internal static class JsTestConfigPatterns
{
    private const int MaxConfigBytes = 64 * 1024;

    private static readonly string[] JestConfigNames =
    [
        "jest.config.js",
        "jest.config.ts",
        "jest.config.mjs",
        "jest.config.cjs",
        "jest.config.mts",
        "jest.config.cts",
        "jest.config.json",
    ];

    private static readonly string[] VitestConfigNames =
    [
        "vitest.config.ts",
        "vitest.config.mts",
        "vitest.config.cts",
        "vitest.config.js",
        "vitest.config.mjs",
        "vitest.config.cjs",
    ];

    private static readonly string[] ViteConfigNames =
    [
        "vite.config.ts",
        "vite.config.mts",
        "vite.config.cts",
        "vite.config.js",
        "vite.config.mjs",
        "vite.config.cjs",
    ];

    internal static JsTestConfigResult ReadJest(string packageRoot)
    {
        foreach (var name in JestConfigNames)
        {
            var path = Path.Combine(packageRoot, name);
            if (!File.Exists(path))
                continue;

            return ReadConfigFile(path, packageRoot, "jest", name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        }

        return ReadPackageJsonJest(packageRoot);
    }

    internal static JsTestConfigResult ReadVitest(string packageRoot)
    {
        var path = FirstExisting(packageRoot, VitestConfigNames);
        if (path is null)
            path = FirstExisting(packageRoot, ViteConfigNames);
        return path is null
            ? NoProperty
            : ReadConfigFile(path, packageRoot, "vitest", path.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
    }

    internal static JsRunnerVersionEvidence ReadRunnerVersionEvidence(string packageRoot, string packageName)
    {
        var installedPath = Path.Combine(packageRoot, "node_modules", packageName, "package.json");
        if (File.Exists(installedPath))
        {
            var read = ReadBounded(installedPath);
            if (read.Diagnostic is not null)
                return new(true, null, [], read.Diagnostic);
            if (read.Truncated)
                return new(true, null, [], "installed runner manifest is truncated at the supported 64 KiB bound");

            try
            {
                using var document = JsonDocument.Parse(read.Text);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return new(true, null, [], "installed runner manifest root must be an object");
                var version = document.RootElement.TryGetProperty("version", out var value)
                    && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
                return new(true, version, [], null);
            }
            catch (JsonException exception)
            {
                return new(true, null, [], $"installed runner manifest is malformed ({exception.Message})");
            }
        }

        var packagePath = Path.Combine(packageRoot, "package.json");
        if (!File.Exists(packagePath))
            return new(false, null, [], null);

        var packageRead = ReadBounded(packagePath);
        if (packageRead.Diagnostic is not null)
            return new(false, null, [], packageRead.Diagnostic);
        if (packageRead.Truncated)
            return new(false, null, [], "package.json is truncated at the supported 64 KiB bound");

        try
        {
            using var document = JsonDocument.Parse(packageRead.Text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new(false, null, [], "package.json root must be an object");

            var ranges = new List<string>();
            foreach (var section in new[] { "dependencies", "devDependencies", "optionalDependencies", "peerDependencies" })
            {
                if (!document.RootElement.TryGetProperty(section, out var dependencies))
                    continue;
                if (dependencies.ValueKind != JsonValueKind.Object)
                    return new(false, null, [], $"package.json '{section}' must be an object");
                if (!dependencies.TryGetProperty(packageName, out var value))
                    continue;
                if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                    return new(false, null, [], $"package.json '{section}.{packageName}' is not a literal version range");
                ranges.Add(value.GetString()!);
            }

            return new(false, null, ranges, null);
        }
        catch (JsonException exception)
        {
            return new(false, null, [], $"package.json is malformed ({exception.Message})");
        }
    }

    internal static IReadOnlyList<string>? ReadJestTestMatch(string packageRoot)
    {
        var result = ReadJest(packageRoot);
        if (result.Diagnostic is not null)
            throw new ContinuousTestProviderException(result.Diagnostic);
        return result.HasDiscoveryProperty ? result.IncludePatterns : null;
    }

    internal static IReadOnlyList<string>? ReadVitestInclude(string packageRoot)
    {
        var result = ReadVitest(packageRoot);
        if (result.Diagnostic is not null)
            throw new ContinuousTestProviderException(result.Diagnostic);
        return result.HasDiscoveryProperty ? result.IncludePatterns : null;
    }

    private static JsTestConfigResult ReadPackageJsonJest(string packageRoot)
    {
        var path = Path.Combine(packageRoot, "package.json");
        if (!File.Exists(path))
            return NoProperty;

        var read = ReadBounded(path);
        if (read.Diagnostic is not null)
            return Failure("jest", path, read.Diagnostic);
        if (read.Truncated)
            return Failure("jest", path, "configuration is truncated at the supported 64 KiB bound");

        try
        {
            using var document = JsonDocument.Parse(read.Text);
            if (!document.RootElement.TryGetProperty("jest", out var jest))
                return NoProperty;
            if (jest.ValueKind == JsonValueKind.Object)
                return Normalize(ParseJsonObject(jest, packageRoot, path, "jest"), packageRoot, path, "jest");
            if (jest.ValueKind != JsonValueKind.String)
                return Failure("jest", path, "package.json 'jest' must be an object or a package-relative JSON path");

            var reference = ResolveReference(packageRoot, jest.GetString(), path, "jest");
            if (reference.Diagnostic is not null)
                return reference.DiagnosticResult!;
            return ReadConfigFile(reference.Path!, packageRoot, "jest", json: true);
        }
        catch (JsonException exception)
        {
            return Failure("jest", path, $"package.json is malformed ({exception.Message})");
        }
    }

    private static JsTestConfigResult ReadConfigFile(
        string path,
        string packageRoot,
        string framework,
        bool json)
    {
        var read = ReadBounded(path);
        if (read.Diagnostic is not null)
            return Failure(framework, path, read.Diagnostic);
        if (read.Truncated)
            return Failure(framework, path, "configuration is truncated at the supported 64 KiB bound");

        try
        {
            var parsed = json
                ? ParseJsonConfig(read.Text, packageRoot, path, framework)
                : ParseJavaScriptConfig(read.Text, packageRoot, path, framework);
            return Normalize(parsed, packageRoot, path, framework);
        }
        catch (JsConfigParseException exception)
        {
            return Failure(framework, path, exception.Message);
        }
        catch (JsonException exception)
        {
            return Failure(framework, path, $"configuration is malformed ({exception.Message})");
        }
    }

    private static JsTestConfigResult ParseJsonConfig(
        string text,
        string packageRoot,
        string path,
        string framework)
    {
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return Failure(framework, path, "configuration root must be an object");
        return ParseJsonObject(document.RootElement, packageRoot, path, framework);
    }

    private static JsTestConfigResult ParseJsonObject(
        JsonElement root,
        string packageRoot,
        string path,
        string framework)
    {
        if (root.TryGetProperty("testRegex", out _)
            && string.Equals(framework, "jest", StringComparison.OrdinalIgnoreCase))
            return Failure(framework, path, "Jest 'testRegex' is unsupported; use a literal 'testMatch' array");

        var rootDir = ReadJsonStringProperty(root, "rootDir", framework, path, out var rootDirError);
        if (rootDirError is not null)
            return Failure(framework, path, rootDirError);

        if (string.Equals(framework, "jest", StringComparison.OrdinalIgnoreCase))
        {
            if (!root.TryGetProperty("testMatch", out var testMatch))
                return new(false, null, [], false, rootDir, null);
            if (!TryReadJsonStringList(testMatch, out var patterns))
                return Failure(framework, path, "Jest 'testMatch' must be a literal array of strings");
            return new(true, patterns, [], false, rootDir, null);
        }

        if (!root.TryGetProperty("test", out var test))
            return new(false, null, [], false, rootDir, null);
        if (test.ValueKind != JsonValueKind.Object)
            return Failure(framework, path, "Vitest 'test' must be a literal object");

        var hasInclude = test.TryGetProperty("include", out var include);
        var hasExclude = test.TryGetProperty("exclude", out var exclude);
        IReadOnlyList<string> includePatterns = [];
        IReadOnlyList<string> excludePatterns = [];
        if (hasInclude && !TryReadJsonStringList(include, out includePatterns))
            return Failure(framework, path, "Vitest 'test.include' must be a literal array of strings");
        if (hasExclude && !TryReadJsonStringList(exclude, out excludePatterns))
            return Failure(framework, path, "Vitest 'test.exclude' must be a literal array of strings");

        return new(
            hasInclude,
            hasInclude ? includePatterns : null,
            hasExclude ? excludePatterns : [],
            hasExclude,
            rootDir,
            null);
    }

    private static JsTestConfigResult ParseJavaScriptConfig(
        string text,
        string packageRoot,
        string path,
        string framework)
    {
        var parser = new JsConfigParser(text);
        var root = parser.ParseRoot();
        if (root.ReferencePath is not null)
        {
            var reference = ResolveReference(packageRoot, root.ReferencePath, path, framework);
            if (reference.Diagnostic is not null)
                return reference.DiagnosticResult!;
            return ReadConfigFile(reference.Path!, packageRoot, framework, json: true);
        }

        if (root.Object is null)
            return Failure(framework, path, "configuration must directly export a literal object");
        if (root.Object.HasSpread)
            return Failure(framework, path, "configuration spreads are unsupported");
        if (root.Object.HasComputedProperty)
            return Failure(framework, path, "computed configuration properties are unsupported");

        if (root.Object.Values.TryGetValue("testRegex", out _)
            && string.Equals(framework, "jest", StringComparison.OrdinalIgnoreCase))
            return Failure(framework, path, "Jest 'testRegex' is unsupported; use a literal 'testMatch' array");

        var rootDir = ReadJavaScriptStringProperty(root.Object, "rootDir", framework, path, out var rootDirError);
        if (rootDirError is not null)
            return Failure(framework, path, rootDirError);

        if (string.Equals(framework, "jest", StringComparison.OrdinalIgnoreCase))
        {
            if (!root.Object.Values.TryGetValue("testMatch", out var testMatch))
                return new(false, null, [], false, rootDir, null);
            if (!TryReadJavaScriptStringList(testMatch, out var patterns))
                return Failure(framework, path, "Jest 'testMatch' must be a literal array of strings");
            return new(true, patterns, [], false, rootDir, null);
        }

        if (!root.Object.Values.TryGetValue("test", out var test))
            return new(false, null, [], false, rootDir, null);
        if (test.Object is null || test.HasUnsupported || test.Object.HasSpread || test.Object.HasComputedProperty)
            return Failure(framework, path, "Vitest 'test' must be a literal object");

        var hasInclude = test.Object.Values.TryGetValue("include", out var include);
        var hasExclude = test.Object.Values.TryGetValue("exclude", out var exclude);
        IReadOnlyList<string> includePatterns = [];
        IReadOnlyList<string> excludePatterns = [];
        if (hasInclude && !TryReadJavaScriptStringList(include!, out includePatterns))
            return Failure(framework, path, "Vitest 'test.include' must be a literal array of strings");
        if (hasExclude && !TryReadJavaScriptStringList(exclude!, out excludePatterns))
            return Failure(framework, path, "Vitest 'test.exclude' must be a literal array of strings");

        return new(
            hasInclude,
            hasInclude ? includePatterns : null,
            hasExclude ? excludePatterns : [],
            hasExclude,
            rootDir,
            null);
    }

    private static JsTestConfigResult Normalize(
        JsTestConfigResult result,
        string packageRoot,
        string path,
        string framework)
    {
        if (result.Diagnostic is not null)
            return result;

        var rootDir = result.RootDir;
        var rootPrefix = string.Empty;
        if (!string.IsNullOrWhiteSpace(rootDir))
        {
            if (rootDir.Contains("<rootDir>", StringComparison.OrdinalIgnoreCase))
                return Failure(framework, path, "rootDir cannot reference <rootDir>");

            var fullRoot = Path.GetFullPath(Path.IsPathRooted(rootDir)
                ? rootDir
                : Path.Combine(packageRoot, rootDir));
            if (!IsWithin(packageRoot, fullRoot))
                return Failure(framework, path, "rootDir must stay inside the package root");
            rootPrefix = NormalizePath(Path.GetRelativePath(packageRoot, fullRoot));
            if (rootPrefix == ".")
                rootPrefix = string.Empty;
        }

        var include = NormalizeRootDir(result.IncludePatterns ?? [], rootPrefix);
        var exclude = NormalizeRootDir(result.ExcludePatterns, rootPrefix);
        return result with { IncludePatterns = include, ExcludePatterns = exclude };
    }

    private static IReadOnlyList<string> NormalizeRootDir(IReadOnlyList<string> patterns, string rootPrefix)
    {
        return patterns
            .Select(pattern => pattern.Replace("<rootDir>", rootPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(NormalizePath)
            .ToArray();
    }

    private static string? ReadJavaScriptStringProperty(
        JsObjectValue root,
        string property,
        string framework,
        string path,
        out string? error)
    {
        error = null;
        if (!root.Values.TryGetValue(property, out var value))
            return null;
        if (value.Kind != JsValueKind.String)
        {
            error = $"{framework} '{property}' must be a literal string";
            return null;
        }

        return value.StringValue;
    }

    private static string? ReadJsonStringProperty(
        JsonElement root,
        string property,
        string framework,
        string path,
        out string? error)
    {
        error = null;
        if (!root.TryGetProperty(property, out var value))
            return null;
        if (value.ValueKind != JsonValueKind.String)
        {
            error = $"{framework} '{property}' must be a literal string";
            return null;
        }

        return value.GetString();
    }

    private static bool TryReadJavaScriptStringList(JsValue value, out IReadOnlyList<string> patterns)
    {
        if (value.Kind != JsValueKind.Array || value.ArrayValues is null)
        {
            patterns = [];
            return false;
        }

        var values = new List<string>();
        foreach (var item in value.ArrayValues)
        {
            if (item.Kind != JsValueKind.String || string.IsNullOrWhiteSpace(item.StringValue))
            {
                patterns = [];
                return false;
            }
            values.Add(item.StringValue!);
        }

        patterns = values;
        return true;
    }

    private static bool TryReadJsonStringList(JsonElement value, out IReadOnlyList<string> patterns)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            patterns = [];
            return false;
        }

        var values = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                patterns = [];
                return false;
            }
            var pattern = item.GetString();
            if (string.IsNullOrWhiteSpace(pattern))
            {
                patterns = [];
                return false;
            }
            values.Add(pattern!);
        }

        patterns = values;
        return true;
    }

    private static ReferenceResult ResolveReference(
        string packageRoot,
        string? reference,
        string sourcePath,
        string framework)
    {
        if (string.IsNullOrWhiteSpace(reference)
            || Path.IsPathRooted(reference)
            || !reference.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return new(null, Failure(framework, sourcePath, "referenced Jest config must be a package-relative .json path"));

        var fullPath = Path.GetFullPath(Path.Combine(packageRoot, reference));
        if (!IsWithin(packageRoot, fullPath))
            return new(null, Failure(framework, sourcePath, "referenced Jest config must stay inside the package root"));
        if (!File.Exists(fullPath))
            return new(null, Failure(framework, sourcePath, $"referenced Jest config '{reference}' does not exist"));
        return new(fullPath, null);
    }

    private static JsTestConfigResult Failure(string framework, string path, string reason) =>
        new(
            false,
            null,
            [],
            false,
            null,
            $"JavaScript {framework} discovery config '{path}' is unsupported: {reason}.");

    private static JsTestConfigResult NoProperty => new(false, null, [], false, null, null);

    private static string? FirstExisting(string packageRoot, IReadOnlyList<string> names) =>
        names.Select(name => Path.Combine(packageRoot, name)).FirstOrDefault(File.Exists);

    private static BoundedRead ReadBounded(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var bytes = new byte[MaxConfigBytes];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0)
                    break;
                count += read;
            }

            var truncated = count == bytes.Length && stream.ReadByte() >= 0;
            return new(Encoding.UTF8.GetString(bytes, 0, count), truncated, null);
        }
        catch (IOException exception)
        {
            return new(string.Empty, false, $"configuration could not be read ({exception.Message})");
        }
        catch (UnauthorizedAccessException exception)
        {
            return new(string.Empty, false, $"configuration could not be read ({exception.Message})");
        }
    }

    private static bool IsWithin(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(candidate);
        return string.Equals(fullCandidate, fullRoot.TrimEnd(Path.DirectorySeparatorChar), PathComparison)
            || fullCandidate.StartsWith(fullRoot, PathComparison);
    }

    private static string NormalizePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record BoundedRead(string Text, bool Truncated, string? Diagnostic);

    private sealed record ReferenceResult(string? Path, JsTestConfigResult? DiagnosticResult)
    {
        internal string? Diagnostic => DiagnosticResult?.Diagnostic;
    }

    private sealed class JsConfigParseException : Exception
    {
        internal JsConfigParseException(string message)
            : base(message)
        {
        }
    }

    private enum JsValueKind
    {
        Unsupported,
        String,
        Array,
        Object,
    }

    private sealed class JsValue
    {
        internal JsValueKind Kind { get; init; }

        internal string? StringValue { get; init; }

        internal JsValue[]? ArrayValues { get; init; }

        internal JsObjectValue? Object { get; init; }

        internal string? ReferencePath { get; init; }

        internal bool HasUnsupported { get; init; }
    }

    private sealed class JsObjectValue
    {
        internal Dictionary<string, JsValue> Values { get; } = new(StringComparer.Ordinal);

        internal bool HasSpread { get; set; }

        internal bool HasComputedProperty { get; set; }
    }

    private sealed record ParsedRoot(JsObjectValue? Object, string? ReferencePath);

    private enum JsTokenKind
    {
        Identifier,
        String,
        Punctuation,
        End,
    }

    private readonly record struct JsToken(JsTokenKind Kind, string Value, bool Template);

    private sealed class JsConfigParser
    {
        private readonly IReadOnlyList<JsToken> _tokens;
        private int _index;

        internal JsConfigParser(string text)
        {
            _tokens = Tokenize(text);
        }

        internal ParsedRoot ParseRoot()
        {
            SkipImports();
            if (Match("export"))
                Require("default");
            else if (Match("module"))
            {
                Require(".");
                Require("exports");
                Require("=");
            }

            var value = ParseValue();
            while (Match(";"))
            {
            }
            if (Current.Kind != JsTokenKind.End)
                throw new JsConfigParseException("configuration contains unsupported trailing expressions");

            if (value.ReferencePath is not null)
                return new(null, value.ReferencePath);
            return new(value.Object, null);
        }

        private void SkipImports()
        {
            while (Match("import"))
            {
                var depth = 0;
                while (Current.Kind != JsTokenKind.End)
                {
                    if (depth == 0 && (Current.Value == ";" || Current.Value is "export" or "module"))
                        break;
                    if (Current.Value is "(" or "[" or "{")
                        depth++;
                    else if (Current.Value is ")" or "]" or "}")
                        depth--;
                    _index++;
                }
                Match(";");
            }
        }

        private JsValue ParseValue()
        {
            var token = Current;
            if (token.Kind == JsTokenKind.String)
            {
                _index++;
                return new() { Kind = JsValueKind.String, StringValue = token.Value };
            }
            if (Match("{"))
                return new() { Kind = JsValueKind.Object, Object = ParseObject() };
            if (Match("["))
                return new() { Kind = JsValueKind.Array, ArrayValues = ParseArray() };
            if (token.Kind == JsTokenKind.Identifier && string.Equals(token.Value, "defineConfig", StringComparison.Ordinal)
                && Peek(1).Value == "(")
            {
                _index += 2;
                var value = ParseValue();
                Require(")");
                return value;
            }
            if (token.Kind == JsTokenKind.Identifier && string.Equals(token.Value, "require", StringComparison.Ordinal)
                && Peek(1).Value == "(")
            {
                _index += 2;
                if (Current.Kind != JsTokenKind.String)
                    throw new JsConfigParseException("referenced config path must be a literal string");
                var path = Current.Value;
                _index++;
                Require(")");
                return new() { ReferencePath = path, Kind = JsValueKind.Unsupported };
            }

            return SkipUnsupportedValue();
        }

        private JsObjectValue ParseObject()
        {
            var value = new JsObjectValue();
            while (Current.Kind != JsTokenKind.End && Current.Value != "}")
            {
                if (Match(","))
                    continue;
                if (Match("..."))
                {
                    value.HasSpread = true;
                    SkipUnsupportedValue();
                    continue;
                }

                if (Match("["))
                {
                    value.HasComputedProperty = true;
                    SkipUntil("]");
                    Require("]");
                    if (Match(":"))
                        SkipUnsupportedValue();
                    continue;
                }

                var key = Current;
                if (key.Kind is not (JsTokenKind.Identifier or JsTokenKind.String))
                {
                    value.HasComputedProperty = true;
                    SkipUnsupportedValue();
                    continue;
                }
                _index++;
                if (!Match(":"))
                {
                    value.HasComputedProperty = true;
                    SkipUnsupportedValue();
                    continue;
                }
                value.Values[key.Value] = ParseValue();
                Match(",");
            }

            Require("}");
            return value;
        }

        private JsValue[] ParseArray()
        {
            var values = new List<JsValue>();
            while (Current.Kind != JsTokenKind.End && Current.Value != "]")
            {
                if (Match(","))
                    continue;
                if (Match("..."))
                {
                    values.Add(new() { Kind = JsValueKind.Unsupported, HasUnsupported = true });
                    SkipUnsupportedValue();
                    continue;
                }
                values.Add(ParseValue());
                if (!Match(",") && Current.Value != "]")
                    SkipUntil("]");
            }

            Require("]");
            return values.ToArray();
        }

        private JsValue SkipUnsupportedValue()
        {
            var depth = 0;
            if (Current.Kind != JsTokenKind.End)
                _index++;
            while (Current.Kind != JsTokenKind.End)
            {
                if (depth == 0 && Current.Value is "," or "}" or "]" or ")")
                    break;
                if (Current.Value is "(" or "[" or "{")
                    depth++;
                else if (Current.Value is ")" or "]" or "}")
                    depth--;
                _index++;
            }
            return new() { Kind = JsValueKind.Unsupported, HasUnsupported = true };
        }

        private void SkipUntil(string token)
        {
            var depth = 0;
            while (Current.Kind != JsTokenKind.End)
            {
                if (depth == 0 && Current.Value == token)
                    return;
                if (Current.Value is "(" or "[" or "{")
                    depth++;
                else if (Current.Value is ")" or "]" or "}")
                    depth--;
                _index++;
            }
        }

        private bool Match(string value)
        {
            if (string.Equals(Current.Value, value, StringComparison.Ordinal))
            {
                _index++;
                return true;
            }
            return false;
        }

        private void Require(string value)
        {
            if (!Match(value))
                throw new JsConfigParseException($"configuration is malformed near '{Current.Value}'");
        }

        private JsToken Current => _tokens[Math.Min(_index, _tokens.Count - 1)];

        private JsToken Peek(int offset) => _tokens[Math.Min(_index + offset, _tokens.Count - 1)];

        private static IReadOnlyList<JsToken> Tokenize(string text)
        {
            var tokens = new List<JsToken>();
            var index = 0;
            while (index < text.Length)
            {
                if (char.IsWhiteSpace(text[index]))
                {
                    index++;
                    continue;
                }
                if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '/')
                {
                    index += 2;
                    while (index < text.Length && text[index] is not '\n' and not '\r')
                        index++;
                    continue;
                }
                if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '*')
                {
                    index += 2;
                    var end = text.IndexOf("*/", index, StringComparison.Ordinal);
                    if (end < 0)
                        throw new JsConfigParseException("configuration contains an unterminated comment");
                    index = end + 2;
                    continue;
                }
                if (text[index] is '\'' or '"' or '`')
                {
                    var quote = text[index++];
                    var start = index;
                    var interpolation = false;
                    while (index < text.Length)
                    {
                        if (text[index] == '\\')
                        {
                            index += 2;
                            continue;
                        }
                        if (quote == '`' && text[index] == '$' && index + 1 < text.Length && text[index + 1] == '{')
                            interpolation = true;
                        if (text[index] == quote)
                            break;
                        index++;
                    }
                    if (index >= text.Length)
                        throw new JsConfigParseException("configuration contains an unterminated string literal");
                    if (interpolation)
                        throw new JsConfigParseException("template interpolation is unsupported in discovery patterns");
                    tokens.Add(new(JsTokenKind.String, text[start..index], quote == '`'));
                    index++;
                    continue;
                }
                if (char.IsLetter(text[index]) || text[index] is '_' or '$')
                {
                    var start = index++;
                    while (index < text.Length
                        && (char.IsLetterOrDigit(text[index]) || text[index] is '_' or '$'))
                        index++;
                    tokens.Add(new(JsTokenKind.Identifier, text[start..index], false));
                    continue;
                }
                if (text[index] == '.' && index + 2 < text.Length && text.AsSpan(index, 3).SequenceEqual("..."))
                {
                    tokens.Add(new(JsTokenKind.Punctuation, "...", false));
                    index += 3;
                    continue;
                }
                tokens.Add(new(JsTokenKind.Punctuation, text[index].ToString(), false));
                index++;
            }
            tokens.Add(new(JsTokenKind.End, string.Empty, false));
            return tokens;
        }
    }
}
