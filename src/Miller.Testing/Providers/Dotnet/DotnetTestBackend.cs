using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Miller.Testing;

internal enum DotnetTestBackendKind
{
    Unknown,
    VSTest,
    XunitV3,
    MicrosoftTestingPlatform,
}

internal sealed record DotnetTestBackendEvidence(
    DotnetTestBackendKind Backend,
    string? Framework,
    string? GlobalJsonTestRunner,
    string? ProjectSdk,
    IReadOnlyList<string> PackageIds,
    IReadOnlyDictionary<string, string?> StaticProperties,
    IReadOnlyDictionary<string, string?> EvaluatedProperties,
    bool IsEvaluated,
    bool IsComplete,
    string? Diagnostic);

internal static class DotnetTestBackend
{
    internal const int MaxPropertyProbeOutputCharacters = 64 * 1024;
    internal const string MetadataBackend = "dotnet_backend";
    internal const string MetadataEvidenceState = "dotnet_backend_evidence_state";
    internal const string MetadataGlobalJsonRunner = "dotnet_global_json_test_runner";
    internal const string MetadataProjectSdk = "dotnet_project_sdk";
    internal const string MetadataStaticPropertyPrefix = "dotnet_static_property_";
    internal const string MetadataEvaluatedPropertyPrefix = "dotnet_evaluated_property_";

    internal static readonly IReadOnlyList<string> PropertyNames =
    [
        "UseVSTest",
        "EnableMSTestRunner",
        "EnableNUnitRunner",
        "UseMicrosoftTestingPlatformRunner",
        "TestingPlatformDotnetTestSupport",
    ];

    private static readonly HashSet<string> XunitV2Packages = new(StringComparer.OrdinalIgnoreCase)
    {
        "xunit",
        "xunit.core",
        "xunit.assert",
        "xunit.abstractions",
        "xunit.extensibility.core",
        "xunit.extensibility.execution",
    };

    private static readonly HashSet<string> MstestPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "mstest.testadapter",
        "mstest.testframework",
        "mstest.testframework.extensions",
    };

    private static readonly HashSet<string> NUnitPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "nunit",
        "nunit.framework",
        "nunit3testadapter",
    };

    private static readonly HashSet<string> GenericTestPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft.net.test.sdk",
        "microsoft.testing.platform",
    };

    internal static DotnetTestBackendEvidence ReadStatic(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        string fullPath = Path.GetFullPath(projectPath);
        var empty = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var packages = Array.Empty<string>();
        string? projectSdk = null;
        string? diagnostic = null;
        bool complete = TryReadBounded(fullPath, out string projectText, out diagnostic);
        if (complete)
        {
            try
            {
                XDocument document = XDocument.Parse(projectText, LoadOptions.None);
                XElement? project = document.Root;
                if (project is null)
                {
                    complete = false;
                    diagnostic = "The .NET project file has no root element.";
                }
                else
                {
                    projectSdk = project.Attribute("Sdk")?.Value.Trim();
                    if (string.IsNullOrWhiteSpace(projectSdk))
                    {
                        projectSdk = project
                            .Elements()
                            .FirstOrDefault(element => element.Name.LocalName.Equals("Sdk", StringComparison.OrdinalIgnoreCase))
                            ?.Attribute("Name")?.Value.Trim();
                    }

                    packages = PackageReferenceIds(projectText).ToArray();
                    foreach (string propertyName in PropertyNames)
                    {
                        XElement? property = project
                            .Descendants()
                            .FirstOrDefault(element =>
                                element.Parent?.Name.LocalName.Equals("PropertyGroup", StringComparison.OrdinalIgnoreCase) == true
                                && element.Name.LocalName.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
                        if (property is not null)
                            empty[propertyName] = property.Value.Trim();
                    }
                }
            }
            catch (Exception exception) when (exception is XmlException or InvalidOperationException)
            {
                complete = false;
                diagnostic = $"The .NET project file could not be parsed: {exception.Message}";
            }
        }

        string? globalRunner = null;
        string? globalDiagnostic = null;
        string? globalPath = FindNearestGlobalJson(Path.GetDirectoryName(fullPath)!);
        if (globalPath is not null)
        {
            if (!TryReadGlobalRunner(globalPath, out globalRunner, out globalDiagnostic))
            {
                complete = false;
                diagnostic = globalDiagnostic;
            }
        }

        return new DotnetTestBackendEvidence(
            Backend: StaticBackend(projectSdk, packages, globalRunner),
            Framework: null,
            GlobalJsonTestRunner: globalRunner,
            ProjectSdk: projectSdk,
            PackageIds: packages,
            StaticProperties: empty,
            EvaluatedProperties: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
            IsEvaluated: false,
            IsComplete: complete,
            Diagnostic: diagnostic);
    }

    internal static TestProcessCommand BuildPropertyProbeCommand(
        ContinuousTestWorkspace workspace,
        string dotnetPath = "dotnet")
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (string.IsNullOrWhiteSpace(dotnetPath))
            throw new ArgumentException("must not be empty", nameof(dotnetPath));

        return new TestProcessCommand(
            dotnetPath,
            [
                "msbuild",
                workspace.ProjectPath,
                "-nologo",
                "-getProperty:" + string.Join(',', PropertyNames),
            ],
            workspace.WorkspaceRoot);
    }

    internal static bool TryParsePropertyProbe(
        string output,
        bool truncated,
        out IReadOnlyDictionary<string, string?> properties,
        out string? diagnostic)
    {
        properties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        diagnostic = null;
        if (truncated || output.Length > MaxPropertyProbeOutputCharacters)
        {
            diagnostic = "MSBuild runner property output was truncated or exceeded the capture bound.";
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostic = "MSBuild runner property output was not a JSON object.";
                return false;
            }

            JsonElement propertyRoot = document.RootElement;
            if (document.RootElement.TryGetProperty("Properties", out JsonElement nested))
            {
                if (nested.ValueKind != JsonValueKind.Object)
                {
                    diagnostic = "MSBuild runner property output had an invalid Properties object.";
                    return false;
                }

                propertyRoot = nested;
            }

            var parsed = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (string propertyName in PropertyNames)
            {
                if (!propertyRoot.TryGetProperty(propertyName, out JsonElement value)
                    || value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    diagnostic = $"MSBuild runner property output was incomplete; '{propertyName}' was missing.";
                    return false;
                }

                string? text = value.ValueKind == JsonValueKind.Null ? null : value.ToString();
                if (!string.IsNullOrWhiteSpace(text) && !bool.TryParse(text, out _))
                {
                    diagnostic = $"MSBuild runner property output had an invalid boolean for '{propertyName}'.";
                    return false;
                }

                parsed[propertyName] = text;
            }

            properties = parsed;
            return true;
        }
        catch (JsonException exception)
        {
            diagnostic = $"MSBuild runner property output was not valid JSON: {exception.Message}";
            return false;
        }
    }

    internal static DotnetTestBackendEvidence WithEvaluatedProperties(
        DotnetTestBackendEvidence evidence,
        string output,
        bool truncated)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!TryParsePropertyProbe(output, truncated, out IReadOnlyDictionary<string, string?> properties, out string? diagnostic))
        {
            return evidence with
            {
                Backend = DotnetTestBackendKind.Unknown,
                IsEvaluated = true,
                IsComplete = false,
                Diagnostic = diagnostic,
            };
        }

        return evidence with
        {
            EvaluatedProperties = properties,
            IsEvaluated = true,
            IsComplete = evidence.IsComplete,
            Diagnostic = evidence.IsComplete ? null : evidence.Diagnostic,
        };
    }

    internal static DotnetTestBackendEvidence Resolve(
        string? framework,
        DotnetTestBackendEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        string normalizedFramework = framework?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!evidence.IsComplete)
        {
            return evidence with
            {
                Backend = DotnetTestBackendKind.Unknown,
                Framework = framework,
                Diagnostic = evidence.Diagnostic ?? "The .NET runner evidence was incomplete.",
            };
        }

        if (normalizedFramework == "xunit" && evidence.PackageIds.Any(XunitV2Packages.Contains))
        {
            return evidence with
            {
                Backend = DotnetTestBackendKind.Unknown,
                Framework = framework,
                Diagnostic = ContinuousTestFrameworkSupport.XunitV2Reason,
            };
        }

        DotnetTestBackendKind backend;
        if (IsMtpRunner(evidence.GlobalJsonTestRunner))
        {
            backend = DotnetTestBackendKind.MicrosoftTestingPlatform;
        }
        else if (IsVSTestRunner(evidence.GlobalJsonTestRunner))
        {
            backend = DotnetTestBackendKind.VSTest;
        }
        else if (!string.IsNullOrWhiteSpace(evidence.GlobalJsonTestRunner))
        {
            return evidence with
            {
                Backend = DotnetTestBackendKind.Unknown,
                Framework = framework,
                Diagnostic = $"Unsupported global.json test.runner value '{evidence.GlobalJsonTestRunner}'.",
            };
        }
        else if (evidence.IsEvaluated)
        {
            backend = ResolveEvaluated(normalizedFramework, evidence);
        }
        else if (normalizedFramework == "xunit"
            && (evidence.Backend == DotnetTestBackendKind.XunitV3
                || evidence.PackageIds.Any(IsXunitV3Package)))
        {
            backend = DotnetTestBackendKind.XunitV3;
        }
        else if (normalizedFramework == "xunit")
        {
            return evidence with
            {
                Backend = DotnetTestBackendKind.Unknown,
                Framework = framework,
                Diagnostic = "xUnit v3 package evidence was not available.",
            };
        }
        else if (normalizedFramework == ContinuousTestFrameworkSupport.XunitV2)
        {
            backend = DotnetTestBackendKind.Unknown;
        }
        else if (string.Equals(evidence.ProjectSdk, "MSTest.Sdk", StringComparison.OrdinalIgnoreCase))
        {
            backend = DotnetTestBackendKind.Unknown;
        }
        else
        {
            backend = DotnetTestBackendKind.VSTest;
        }

        return evidence with { Backend = backend, Framework = framework };
    }

    internal static IReadOnlyDictionary<string, object?> ToMetadata(DotnetTestBackendEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [MetadataBackend] = evidence.Backend.ToString(),
            [MetadataEvidenceState] = evidence.IsEvaluated ? "evaluated" : "static",
            [MetadataGlobalJsonRunner] = evidence.GlobalJsonTestRunner,
            [MetadataProjectSdk] = evidence.ProjectSdk,
            ["dotnet_package_ids"] = evidence.PackageIds.ToArray(),
        };
        foreach ((string name, string? value) in evidence.StaticProperties)
            metadata[MetadataStaticPropertyPrefix + name] = value;
        foreach ((string name, string? value) in evidence.EvaluatedProperties)
            metadata[MetadataEvaluatedPropertyPrefix + name] = value;
        if (!string.IsNullOrWhiteSpace(evidence.Diagnostic))
            metadata["dotnet_backend_diagnostic"] = evidence.Diagnostic;
        return metadata;
    }

    internal static IEnumerable<string> PackageReferenceIds(string projectText)
    {
        const string elementName = "PackageReference";
        var cursor = 0;
        while ((cursor = projectText.IndexOf('<' + elementName, cursor, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int start = cursor + elementName.Length + 1;
            int close = projectText.IndexOf('>', start);
            string tag = close < 0 ? projectText[start..] : projectText[start..close];
            if (AttributeValue(tag, "Include") is { } include)
                yield return include;
            if (AttributeValue(tag, "Update") is { } update)
                yield return update;
            if (close < 0)
                yield break;
            cursor = close + 1;
        }
    }

    private static bool IsXunitV3Package(string package) =>
        string.Equals(package, "xunit.v3", StringComparison.OrdinalIgnoreCase)
        || package.StartsWith("xunit.v3.", StringComparison.OrdinalIgnoreCase);

    internal static bool IsMstestProject(string projectText, string? projectSdk = null) =>
        MstestPackages.Overlaps(PackageReferenceIds(projectText))
        || string.Equals(projectSdk, "MSTest.Sdk", StringComparison.OrdinalIgnoreCase);

    internal static bool IsNUnitProject(string projectText) =>
        NUnitPackages.Overlaps(PackageReferenceIds(projectText));

    internal static bool IsXunitProject(string projectText) =>
        PackageReferenceIds(projectText).Any(IsXunitPackage);

    internal static bool IsGenericTestProject(string projectText) =>
        GenericTestPackages.Overlaps(PackageReferenceIds(projectText));

    internal static string XunitFramework(string projectText)
    {
        string[] packageIds = PackageReferenceIds(projectText).ToArray();
        if (packageIds.Any(package =>
                string.Equals(package, "xunit.v3", StringComparison.OrdinalIgnoreCase)
                || package.StartsWith("xunit.v3.", StringComparison.OrdinalIgnoreCase)))
        {
            return "xunit";
        }

        return packageIds.Any(package => XunitV2Packages.Contains(package))
            ? ContinuousTestFrameworkSupport.XunitV2
            : "xunit";
    }

    private static DotnetTestBackendKind ResolveEvaluated(
        string framework,
        DotnetTestBackendEvidence evidence)
    {
        IReadOnlyDictionary<string, string?> properties = evidence.EvaluatedProperties;
        if (BoolProperty(properties, "UseVSTest") == true)
            return DotnetTestBackendKind.VSTest;

        bool mtpEnabled = BoolProperty(properties, "EnableMSTestRunner") == true
            || BoolProperty(properties, "EnableNUnitRunner") == true
            || BoolProperty(properties, "UseMicrosoftTestingPlatformRunner") == true
            || BoolProperty(properties, "TestingPlatformDotnetTestSupport") == true;
        if (mtpEnabled)
            return DotnetTestBackendKind.MicrosoftTestingPlatform;

        if (string.Equals(evidence.ProjectSdk, "MSTest.Sdk", StringComparison.OrdinalIgnoreCase)
            && BoolProperty(properties, "UseVSTest") != true)
        {
            return DotnetTestBackendKind.MicrosoftTestingPlatform;
        }

        return framework == "xunit"
            ? DotnetTestBackendKind.XunitV3
            : DotnetTestBackendKind.VSTest;
    }

    private static DotnetTestBackendKind StaticBackend(
        string? projectSdk,
        IReadOnlyList<string> packageIds,
        string? globalRunner)
    {
        if (IsMtpRunner(globalRunner))
            return DotnetTestBackendKind.MicrosoftTestingPlatform;
        if (IsVSTestRunner(globalRunner))
            return DotnetTestBackendKind.VSTest;
        if (packageIds.Any(package =>
                string.Equals(package, "xunit.v3", StringComparison.OrdinalIgnoreCase)
                || package.StartsWith("xunit.v3.", StringComparison.OrdinalIgnoreCase)))
        {
            return DotnetTestBackendKind.XunitV3;
        }

        return DotnetTestBackendKind.Unknown;
    }

    private static bool IsXunitPackage(string package) =>
        XunitV2Packages.Contains(package)
        || string.Equals(package, "xunit.v3", StringComparison.OrdinalIgnoreCase)
        || package.StartsWith("xunit.v3.", StringComparison.OrdinalIgnoreCase);

    private static bool? BoolProperty(IReadOnlyDictionary<string, string?> properties, string name)
    {
        if (!properties.TryGetValue(name, out string? value) || string.IsNullOrWhiteSpace(value))
            return null;
        return bool.TryParse(value, out bool parsed) ? parsed : null;
    }

    private static bool IsMtpRunner(string? value) =>
        string.Equals(value?.Trim(), "Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value?.Trim(), "MTP", StringComparison.OrdinalIgnoreCase);

    private static bool IsVSTestRunner(string? value) =>
        string.Equals(value?.Trim(), "VSTest", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value?.Trim(), "Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadBounded(string path, out string text, out string? diagnostic)
    {
        text = string.Empty;
        diagnostic = null;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxPropertyProbeOutputCharacters)
            {
                diagnostic = "The .NET project file exceeds the bounded static evidence limit.";
                return false;
            }

            var buffer = new byte[(int)stream.Length];
            int read = 0;
            while (read < buffer.Length)
            {
                int count = stream.Read(buffer, read, buffer.Length - read);
                if (count == 0)
                    break;
                read += count;
            }

            text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            if (read != buffer.Length)
            {
                diagnostic = "The .NET project file could not be read completely.";
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostic = $"The .NET project file could not be read: {exception.Message}";
            return false;
        }
    }

    private static string? FindNearestGlobalJson(string directory)
    {
        string? current = Path.GetFullPath(directory);
        while (!string.IsNullOrEmpty(current))
        {
            string candidate = Path.Combine(current, "global.json");
            if (File.Exists(candidate))
                return candidate;
            string? parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.Ordinal))
                break;
            current = parent;
        }

        return null;
    }

    private static bool TryReadGlobalRunner(
        string path,
        out string? runner,
        out string? diagnostic)
    {
        runner = null;
        diagnostic = null;
        if (!TryReadBounded(path, out string text, out diagnostic))
            return false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostic = "global.json must contain a JSON object.";
                return false;
            }

            if (!document.RootElement.TryGetProperty("test", out JsonElement test))
                return true;
            if (test.ValueKind != JsonValueKind.Object)
            {
                diagnostic = "global.json 'test' must be a JSON object.";
                return false;
            }

            if (!test.TryGetProperty("runner", out JsonElement value)
                || value.ValueKind == JsonValueKind.Null)
            {
                return true;
            }
            if (value.ValueKind != JsonValueKind.String)
            {
                diagnostic = "global.json 'test.runner' must be a string.";
                return false;
            }

            runner = value.GetString()?.Trim();
            return true;
        }
        catch (JsonException exception)
        {
            diagnostic = $"global.json could not be parsed: {exception.Message}";
            return false;
        }
    }

    private static string? AttributeValue(string tag, string name)
    {
        int at = tag.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        while (at >= 0)
        {
            int cursor = at + name.Length;
            while (cursor < tag.Length && char.IsWhiteSpace(tag[cursor]))
                cursor++;
            if (cursor < tag.Length
                && tag[cursor] == '='
                && (at == 0 || char.IsWhiteSpace(tag[at - 1])))
            {
                cursor++;
                while (cursor < tag.Length && char.IsWhiteSpace(tag[cursor]))
                    cursor++;
                if (cursor < tag.Length && tag[cursor] is '"' or '\'')
                {
                    char quote = tag[cursor];
                    int end = tag.IndexOf(quote, cursor + 1);
                    if (end > 0)
                        return tag[(cursor + 1)..end].Trim();
                }

                return null;
            }

            at = tag.IndexOf(name, at + 1, StringComparison.OrdinalIgnoreCase);
        }

        return null;
    }
}
