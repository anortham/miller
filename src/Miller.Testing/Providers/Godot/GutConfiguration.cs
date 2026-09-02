using System.Text.Json;
using Miller.Testing.Providers.Shared;

namespace Miller.Testing.Providers.Godot;

internal sealed record GutScript(string ResPath, string MirrorPath);

internal sealed class GutConfiguration
{
    private static readonly HashSet<string> RunnerOwnedKeys = new(StringComparer.Ordinal)
    {
        "dirs",
        "tests",
        "include_subdirs",
        "should_exit",
        "exit_on_success",
        "disable_colors",
        "junit_xml_file",
        "junit_xml_timestamp",
        "selected",
        "unit_test_name",
        "inner_class",
    };

    private readonly IReadOnlyDictionary<string, JsonElement> _properties;

    private GutConfiguration(
        IReadOnlyDictionary<string, JsonElement> properties,
        IReadOnlyList<string> dirs,
        IReadOnlyList<string> tests,
        bool includeSubdirs,
        string prefix,
        string suffix)
    {
        _properties = properties;
        Dirs = dirs;
        Tests = tests;
        IncludeSubdirs = includeSubdirs;
        Prefix = prefix;
        Suffix = suffix;
    }

    internal IReadOnlyList<string> Dirs { get; }

    internal IReadOnlyList<string> Tests { get; }

    internal bool IncludeSubdirs { get; }

    internal string Prefix { get; }

    internal string Suffix { get; }

    internal static GutConfiguration Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions { AllowTrailingCommas = true });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Invalid("GUT config must contain a JSON object.");

            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!properties.TryAdd(property.Name, property.Value.Clone()))
                    throw Invalid($"GUT config contains duplicate key '{property.Name}'.");
            }

            return new(
                properties,
                ReadStringArray(root, "dirs"),
                ReadStringArray(root, "tests"),
                ReadBoolean(root, "include_subdirs", defaultValue: false),
                ReadString(root, "prefix", "test_"),
                ReadString(root, "suffix", ".gd"));
        }
        catch (ContinuousTestProviderException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ContinuousTestProviderException("GUT config is malformed JSON.", exception);
        }
    }

    internal static GutConfiguration Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            return Parse("{}");
        try
        {
            CtWorkspaceMirror.EnsurePathHasNoReparsePoint(path);
            return Parse(File.ReadAllText(path));
        }
        catch (ContinuousTestProviderException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ContinuousTestProviderException($"GUT config could not be read: '{path}'.", exception);
        }
    }

    internal IReadOnlyList<GutScript> DiscoverScripts(string mirrorRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mirrorRoot);
        string root = Path.GetFullPath(mirrorRoot);
        CtWorkspaceMirror.EnsurePathHasNoReparsePoint(root);
        if (!Directory.Exists(root))
            throw new ContinuousTestProviderException($"GUT project mirror is missing: '{mirrorRoot}'.");

        var scripts = new Dictionary<string, GutScript>(StringComparer.Ordinal);
        var caseFolded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string configuredTest in Tests)
        {
            string resPath = GutTooling.NormalizeResPath(configuredTest);
            AddScript(root, resPath, scripts, caseFolded, explicitTest: true);
        }

        foreach (string configuredDirectory in Dirs)
        {
            string directoryResPath = GutTooling.NormalizeResPath(configuredDirectory);
            string directoryPath = MirrorPath(root, directoryResPath);
            if (!Directory.Exists(directoryPath))
                throw new ContinuousTestProviderException(
                    $"GUT configured directory does not exist: '{directoryResPath}'.");
            foreach (string path in EnumerateDirectory(directoryPath, IncludeSubdirs))
            {
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                string resPath = GutTooling.NormalizeResPath("res://" + relative);
                if (MatchesConvention(resPath))
                    AddScript(root, resPath, scripts, caseFolded, explicitTest: false);
            }
        }

        return scripts.Values.OrderBy(script => script.ResPath, StringComparer.Ordinal).ToArray();
    }

    internal string SerializeDerived(
        IEnumerable<string> selectedResPaths,
        string reportResPath)
    {
        ArgumentNullException.ThrowIfNull(selectedResPaths);
        string normalizedReportPath = GutTooling.NormalizeResPath(reportResPath);
        var selected = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in selectedResPaths)
        {
            string normalized = GutTooling.NormalizeResPath(path);
            if (seen.Add(normalized))
                selected.Add(normalized);
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach ((string name, JsonElement value) in _properties)
            {
                if (RunnerOwnedKeys.Contains(name))
                    continue;
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }

            writer.WritePropertyName("dirs");
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WritePropertyName("tests");
            writer.WriteStartArray();
            foreach (string path in selected)
                writer.WriteStringValue(path);
            writer.WriteEndArray();
            writer.WriteBoolean("include_subdirs", false);
            writer.WriteString("prefix", Prefix);
            writer.WriteString("suffix", Suffix);
            writer.WriteBoolean("should_exit", true);
            writer.WriteBoolean("exit_on_success", false);
            writer.WriteBoolean("disable_colors", true);
            writer.WriteString("junit_xml_file", normalizedReportPath);
            writer.WriteBoolean("junit_xml_timestamp", false);
            writer.WriteString("selected", string.Empty);
            writer.WriteString("unit_test_name", string.Empty);
            writer.WriteString("inner_class", string.Empty);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void AddScript(
        string root,
        string resPath,
        IDictionary<string, GutScript> scripts,
        IDictionary<string, string> caseFolded,
        bool explicitTest)
    {
        if (!resPath.EndsWith(".gd", StringComparison.OrdinalIgnoreCase))
            throw new ContinuousTestProviderException(
                $"GUT {(explicitTest ? "test" : "directory")} entry is not a .gd script: '{resPath}'.");
        string path = MirrorPath(root, resPath);
        if (!File.Exists(path))
            throw new ContinuousTestProviderException($"GUT configured test script does not exist: '{resPath}'.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ContinuousTestProviderException($"GUT test script is a reparse point: '{resPath}'.");

        if (scripts.ContainsKey(resPath))
            return;
        if (caseFolded.TryGetValue(resPath, out string? existing))
            throw new ContinuousTestProviderException(
                $"GUT test script case collision between '{existing}' and '{resPath}'.");
        caseFolded.Add(resPath, resPath);
        scripts.Add(resPath, new GutScript(resPath, path));
    }

    private bool MatchesConvention(string resPath)
    {
        string name = resPath[(resPath.LastIndexOf('/') + 1)..];
        return name.StartsWith(Prefix, StringComparison.Ordinal)
            && name.EndsWith(Suffix, StringComparison.Ordinal);
    }

    private static IEnumerable<string> EnumerateDirectory(string root, bool recursive)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(current);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ContinuousTestProviderException(
                    $"GUT configured directory could not be enumerated: '{current}'.", exception);
            }

            foreach (string entry in entries.Order(StringComparer.Ordinal))
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new ContinuousTestProviderException(
                        $"GUT configured path could not be inspected: '{entry}'.", exception);
                }
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new ContinuousTestProviderException($"GUT configured path is a reparse point: '{entry}'.");
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (recursive)
                        pending.Push(entry);
                    continue;
                }
                yield return entry;
            }
        }
    }

    private static string MirrorPath(string root, string resPath)
    {
        string relative = resPath[6..].Replace('/', Path.DirectorySeparatorChar);
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsContained(path, root))
            throw new ContinuousTestProviderException($"GUT resource path escapes the project root: '{resPath}'.");
        return path;
    }

    private static bool IsContained(string path, string root)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(fullPath, fullRoot, comparison)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
            return [];
        if (value.ValueKind != JsonValueKind.Array)
            throw Invalid($"GUT config field '{name}' must be an array of strings.");
        var values = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw Invalid($"GUT config field '{name}' must contain only non-empty strings.");
            values.Add(item.GetString()!);
        }
        return values;
    }

    private static bool ReadBoolean(JsonElement root, string name, bool defaultValue)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
            return defaultValue;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Invalid($"GUT config field '{name}' must be a boolean.");
        return value.GetBoolean();
    }

    private static string ReadString(JsonElement root, string name, string defaultValue)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
            return defaultValue;
        if (value.ValueKind != JsonValueKind.String)
            throw Invalid($"GUT config field '{name}' must be a string.");
        return value.GetString() ?? string.Empty;
    }

    private static ContinuousTestProviderException Invalid(string message) =>
        new(message);
}
