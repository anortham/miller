using System.Text.Json;

namespace Miller.Testing;

internal static class MtpTestListParser
{
    internal static IReadOnlyList<ProviderTestCase> Parse(
        string output,
        MtpVersion version,
        string framework,
        bool truncated = false,
        string? workspaceRoot = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(framework);
        if (truncated)
            throw new ContinuousTestProviderException("MTP test listing was truncated and cannot be trusted.");
        if (version.CompareTo(MtpVersion.MinimumSupported) < 0)
            throw new ContinuousTestProviderException(
                $"Microsoft.Testing.Platform {version} cannot provide the supported test-list contract; MTP 1.7.0 or newer is required.");

        string trimmed = output.TrimStart();
        if (!version.SupportsJsonListAndReports
            && trimmed.Length > 0
            && (trimmed[0] == '{' || trimmed[0] == '['))
            throw new ContinuousTestProviderException(
                $"MTP JSON test listing requires Microsoft.Testing.Platform {MtpVersion.JsonListAndReportMinimum} or newer.");

        return version.SupportsJsonListAndReports
            ? ParseJson(output, framework, workspaceRoot)
            : ParseText(output, framework);
    }

    private static IReadOnlyList<ProviderTestCase> ParseText(string output, string framework)
    {
        string[] lines = output.Split(['\r', '\n'], StringSplitOptions.None);
        int header = Array.FindIndex(
            lines,
            line => line.Trim().Equals("The following Tests are available:", StringComparison.OrdinalIgnoreCase));
        int start = header >= 0
            ? header + 1
            : 0;
        int summary = Array.FindIndex(
            lines,
            start,
            lines.Length - start,
            line => line.TrimStart().StartsWith("Test discovery summary:", StringComparison.OrdinalIgnoreCase));
        if (header < 0 && summary < 0)
            throw new ContinuousTestProviderException(
                "MTP text test listing did not contain the documented test-list header or summary.");

        var cases = new List<ProviderTestCase>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        int end = summary >= 0 ? summary : lines.Length;
        for (int index = start; index < end; index++)
        {
            if (header < 0 && lines[index].Length == lines[index].TrimStart().Length)
                continue;
            string name = lines[index].Trim();
            if (name.Length == 0)
                continue;
            if (name.StartsWith("Total tests", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Test Run", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Passed!", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Failed!", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!identities.Add(name))
                continue;
            cases.Add(CreateCase(framework, name, name));
        }

        if (cases.Count == 0)
            throw new ContinuousTestProviderException(
                header >= 0
                    ? "MTP text test listing contained the header but no test cases."
                    : "MTP text test listing contained only the discovery summary and no test cases.");
        return cases;
    }

    private static IReadOnlyList<ProviderTestCase> ParseJson(
        string output,
        string framework,
        string? workspaceRoot)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(output);
        }
        catch (JsonException exception)
        {
            throw new ContinuousTestProviderException(
                "MTP JSON test listing was malformed: " + exception.Message,
                exception);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetProperty(root, "schemaVersion", out JsonElement schemaVersion)
                || !schemaVersion.TryGetInt32(out int schema)
                || schema != 1)
            {
                throw new ContinuousTestProviderException(
                    "MTP JSON test listing used an unsupported schema version.");
            }
            JsonElement rows = root;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (!TryGetProperty(root, "tests", out rows)
                    && !TryGetProperty(root, "testCases", out rows))
                {
                    throw new ContinuousTestProviderException(
                        "MTP JSON test listing was incomplete; no tests array was present.");
                }
            }

            if (rows.ValueKind != JsonValueKind.Array)
                throw new ContinuousTestProviderException(
                    "MTP JSON test listing was incomplete; tests was not an array.");

            var cases = new List<ProviderTestCase>();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement row in rows.EnumerateArray())
            {
                string? fullyQualifiedName;
                string? displayName;
                string? sourcePath = null;
                if (row.ValueKind == JsonValueKind.String)
                {
                    fullyQualifiedName = row.GetString();
                    displayName = fullyQualifiedName;
                }
                else if (row.ValueKind == JsonValueKind.Object)
                {
                    fullyQualifiedName = OptionalString(row, "fullyQualifiedName")
                        ?? OptionalString(row, "FullyQualifiedName")
                        ?? OptionalString(row, "name")
                        ?? OptionalString(row, "Name");
                    displayName = OptionalString(row, "displayName")
                        ?? OptionalString(row, "DisplayName")
                        ?? fullyQualifiedName;
                    if (fullyQualifiedName is null
                        && TryGetProperty(row, "type", out JsonElement type)
                        && type.ValueKind == JsonValueKind.Object)
                    {
                        string? @namespace = OptionalString(type, "namespace");
                        string? typeName = OptionalString(type, "typeName");
                        string? methodName = OptionalString(type, "methodName");
                        fullyQualifiedName = string.Join(
                            ".",
                            new[] { @namespace, typeName, methodName }
                                .Where(value => !string.IsNullOrWhiteSpace(value)));
                        displayName = OptionalString(row, "displayName")
                            ?? OptionalString(row, "DisplayName")
                            ?? fullyQualifiedName;
                    }
                }
                else
                {
                    throw new ContinuousTestProviderException(
                        "MTP JSON test listing contained a test row with an unsupported shape.");
                }

                if (string.IsNullOrWhiteSpace(fullyQualifiedName)
                    || string.IsNullOrWhiteSpace(displayName))
                {
                    throw new ContinuousTestProviderException(
                        "MTP JSON test listing contained a test row without a test identity.");
                }

                if (workspaceRoot is not null
                    && TryGetProperty(row, "location", out JsonElement location)
                    && location.ValueKind == JsonValueKind.Object)
                {
                    sourcePath = NormalizeSourcePath(
                        OptionalString(location, "file"),
                        workspaceRoot);
                }

                string identity = fullyQualifiedName + "\u0000" + displayName;
                if (!identities.Add(identity))
                    continue;
                cases.Add(CreateCase(framework, fullyQualifiedName, displayName, sourcePath));
            }

            if (cases.Count == 0)
                throw new ContinuousTestProviderException(
                    "MTP JSON test listing contained no test cases.");
            return cases;
        }
    }

    private static ProviderTestCase CreateCase(
        string framework,
        string fullyQualifiedName,
        string displayName,
        string? sourcePath = null)
    {
        string id = string.Equals(fullyQualifiedName, displayName, StringComparison.Ordinal)
            || string.Equals(
                fullyQualifiedName[(fullyQualifiedName.LastIndexOf('.') + 1)..],
                displayName,
                StringComparison.Ordinal)
            ? $"{framework}:{fullyQualifiedName}"
            : $"{framework}:{fullyQualifiedName}::display={displayName}";
        int separator = fullyQualifiedName.LastIndexOf('.');
        string? className = separator > 0 ? fullyQualifiedName[..separator] : null;
        string methodName = separator >= 0 && separator < fullyQualifiedName.Length - 1
            ? fullyQualifiedName[(separator + 1)..]
            : fullyQualifiedName;
        return new ProviderTestCase(
            Id: id,
            DisplayName: displayName,
            FullyQualifiedName: fullyQualifiedName,
            Selector: fullyQualifiedName,
            Framework: framework,
            SourcePath: sourcePath,
            Metadata: new Dictionary<string, object?>
            {
                ["class"] = className,
                ["method"] = methodName,
                ["selector_kind"] = "FullyQualifiedName",
            },
            SymbolName: methodName,
            SymbolPath: sourcePath);
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
            return true;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? OptionalString(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? NormalizeSourcePath(string? path, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path, workspaceRoot);
        }
        catch (ArgumentException)
        {
            return null;
        }
        string relativePath = Path.GetRelativePath(Path.GetFullPath(workspaceRoot), fullPath);
        if (Path.IsPathRooted(relativePath)
            || relativePath == ".."
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            return null;
        return relativePath.Replace(Path.DirectorySeparatorChar, '/');
    }
}
