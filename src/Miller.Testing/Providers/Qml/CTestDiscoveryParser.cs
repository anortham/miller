using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace Miller.Testing.Providers.Qml;

public sealed record CTestDiscoveryResult(
    int SchemaMajor,
    int SchemaMinor,
    ImmutableArray<CTestDiscoveredTest> Tests);

public sealed record CTestDiscoveredTest(
    string Name,
    ImmutableArray<string> Command,
    ImmutableArray<string> Labels,
    string? WorkingDirectory,
    ImmutableDictionary<string, object?> Metadata)
{
    public IReadOnlyDictionary<string, object?> Properties => Metadata;
}

public static class CTestDiscoveryParser
{
    private const int SupportedSchemaMajor = 1;
    private const int MaxTests = 100_000;
    private const int MaxNameCharacters = 4_096;
    private const int MaxCommandArguments = 4_096;
    private const int MaxLabels = 1_024;
    private const int MaxMetadataEntries = 128;
    private const int MaxMetadataValueCharacters = 16_384;

    public static CTestDiscoveryResult Parse(TestProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ExitCode != 0)
            throw new ContinuousTestProviderException(
                $"CTest discovery failed with exit code {result.ExitCode}: {FailureText(result)}");

        return Parse(result.RequireCompleteStandardOutput("CTest JSON discovery"));
    }

    public static CTestDiscoveryResult Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ContinuousTestProviderException("CTest JSON discovery produced no output.");

        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    MaxDepth = 64,
                    CommentHandling = JsonCommentHandling.Disallow,
                    AllowTrailingCommas = false,
                });
            return ParseDocument(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new ContinuousTestProviderException(
                $"CTest discovery output was not valid JSON: {exception.Message}", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new ContinuousTestProviderException(
                $"CTest discovery JSON had an invalid shape: {exception.Message}", exception);
        }
    }

    private static CTestDiscoveryResult ParseDocument(JsonElement root)
    {
        RequireKind(root, JsonValueKind.Object, "root");
        var kind = RequiredPropertyString(root, "kind", "root");
        if (!string.Equals(kind, "ctestInfo", StringComparison.Ordinal))
            throw Invalid("root.kind must be 'ctestInfo'.");

        if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Object)
            throw Invalid("root.version must be an object for CTest JSON schema v1.");
        var major = RequiredPropertyInt(version, "major", "version");
        var minor = RequiredPropertyInt(version, "minor", "version");
        if (major != SupportedSchemaMajor || minor < 0)
            throw Invalid($"CTest JSON schema version {major}.{minor} is unsupported; schema version 1 is required.");

        if (!root.TryGetProperty("tests", out var testsElement) || testsElement.ValueKind != JsonValueKind.Array)
            throw Invalid("root.tests must be an array.");
        if (testsElement.GetArrayLength() == 0)
            throw Invalid("CTest discovery returned zero tests; --no-tests=error semantics require at least one target.");
        if (testsElement.GetArrayLength() > MaxTests)
            throw Invalid($"CTest discovery returned more than the {MaxTests} target limit.");

        var tests = new List<CTestDiscoveredTest>(testsElement.GetArrayLength());
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in testsElement.EnumerateArray())
        {
            var test = ParseTest(element);
            if (!names.Add(test.Name))
                throw Invalid($"CTest discovery returned duplicate target name '{test.Name}'.");
            tests.Add(test);
        }

        tests.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        return new CTestDiscoveryResult(major, minor, [.. tests]);
    }

    private static CTestDiscoveredTest ParseTest(JsonElement element)
    {
        RequireKind(element, JsonValueKind.Object, "test");
        var name = RequiredPropertyString(element, "name", "test");
        if (name.Length > MaxNameCharacters)
            throw Invalid($"CTest target name exceeds the {MaxNameCharacters}-character limit.");

        if (!element.TryGetProperty("command", out var commandElement)
            || commandElement.ValueKind != JsonValueKind.Array)
            throw Invalid($"CTest target '{name}' must contain a command array.");
        if (commandElement.GetArrayLength() == 0)
            throw Invalid($"CTest target '{name}' has an empty command array.");
        if (commandElement.GetArrayLength() > MaxCommandArguments)
            throw Invalid($"CTest target '{name}' has more than the {MaxCommandArguments}-argument limit.");

        var command = commandElement
            .EnumerateArray()
            .Select((argument, index) => RequiredStringValue(argument, $"command[{index}]", $"test '{name}'"))
            .ToImmutableArray();
        var properties = ParseProperties(element, name);
        var labels = ParseLabels(properties, name);
        var workingDirectory = PropertyString(properties, "WORKING_DIRECTORY", name);
        return new CTestDiscoveredTest(name, command, labels, workingDirectory, properties);
    }

    private static ImmutableDictionary<string, object?> ParseProperties(JsonElement test, string testName)
    {
        if (!test.TryGetProperty("properties", out var propertiesElement))
            return ImmutableDictionary<string, object?>.Empty;
        if (propertiesElement.ValueKind != JsonValueKind.Array)
            throw Invalid($"CTest target '{testName}' properties must be an array.");
        if (propertiesElement.GetArrayLength() > MaxMetadataEntries)
            throw Invalid($"CTest target '{testName}' has more than the {MaxMetadataEntries}-property limit.");

        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
        foreach (var property in propertiesElement.EnumerateArray())
        {
            RequireKind(property, JsonValueKind.Object, $"properties for '{testName}'");
            var propertyName = RequiredPropertyString(property, "name", $"properties for '{testName}'");
            if (!builder.TryAdd(propertyName, PropertyValue(property, propertyName, testName)))
                throw Invalid($"CTest target '{testName}' has duplicate property '{propertyName}'.");
        }

        return builder.ToImmutable();
    }

    private static object? PropertyValue(JsonElement property, string propertyName, string testName)
    {
        if (!property.TryGetProperty("value", out var value))
            throw Invalid($"CTest target '{testName}' property '{propertyName}' has no value.");

        object? parsed = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => BoundedNumber(value, propertyName, testName),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => ParseArrayValue(value, propertyName, testName),
            _ => throw Invalid($"CTest target '{testName}' property '{propertyName}' has an unsupported object value."),
        };

        if (parsed is string text && text.Length > MaxMetadataValueCharacters)
            throw Invalid($"CTest target '{testName}' property '{propertyName}' exceeds the metadata value limit.");
        return parsed;
    }

    private static string BoundedNumber(JsonElement value, string propertyName, string testName)
    {
        var text = value.GetRawText();
        if (text.Length > MaxMetadataValueCharacters)
            throw Invalid($"CTest target '{testName}' property '{propertyName}' exceeds the metadata value limit.");
        return text;
    }

    private static ImmutableArray<string> ParseArrayValue(JsonElement value, string propertyName, string testName)
    {
        if (value.GetArrayLength() > MaxMetadataEntries)
            throw Invalid($"CTest target '{testName}' property '{propertyName}' exceeds the array limit.");

        var values = new List<string>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw Invalid($"CTest target '{testName}' property '{propertyName}' contains a non-string value.");
            var text = item.GetString() ?? string.Empty;
            if (text.Length > MaxMetadataValueCharacters)
                throw Invalid($"CTest target '{testName}' property '{propertyName}' exceeds the metadata value limit.");
            values.Add(text);
        }

        return [.. values];
    }

    private static ImmutableArray<string> ParseLabels(
        ImmutableDictionary<string, object?> properties,
        string testName)
    {
        if (!properties.TryGetValue("LABELS", out var value) || value is null)
            return [];
        if (value is string label)
            return [label];
        if (value is not ImmutableArray<string> labels)
            throw Invalid($"CTest target '{testName}' LABELS must contain strings.");
        if (labels.Length > MaxLabels)
            throw Invalid($"CTest target '{testName}' has more than the {MaxLabels}-label limit.");
        return labels;
    }

    private static string? PropertyString(
        ImmutableDictionary<string, object?> properties,
        string propertyName,
        string testName)
    {
        if (!properties.TryGetValue(propertyName, out var value) || value is null)
            return null;
        if (value is string text)
            return text;
        throw Invalid($"CTest target '{testName}' property '{propertyName}' must be a string.");
    }

    private static string RequiredPropertyString(JsonElement parent, string propertyName, string context)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
            throw Invalid($"CTest {context} field '{propertyName}' is missing.");
        return RequiredStringValue(value, propertyName, context);
    }

    private static string RequiredStringValue(JsonElement element, string propertyName, string context)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw Invalid($"CTest {context} field '{propertyName}' must be a string.");
        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw Invalid($"CTest {context} field '{propertyName}' must not be empty.");
        return value;
    }

    private static int RequiredPropertyInt(JsonElement parent, string propertyName, string context)
    {
        if (!parent.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var value))
            throw Invalid($"CTest {context} field '{propertyName}' must be an integer.");
        return value;
    }

    private static void RequireKind(JsonElement element, JsonValueKind expected, string context)
    {
        if (element.ValueKind != expected)
            throw Invalid($"CTest {context} must be a JSON {expected.ToString().ToLowerInvariant()}.");
    }

    private static ContinuousTestProviderException Invalid(string message) =>
        new(message);

    private static string FailureText(TestProcessResult result)
    {
        var text = !string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardError
            : result.StandardOutput;
        return string.IsNullOrWhiteSpace(text) ? "no diagnostic output" : text.Trim();
    }
}
