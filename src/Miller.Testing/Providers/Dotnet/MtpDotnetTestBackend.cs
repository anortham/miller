using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Miller.Testing;

internal static class MtpDotnetTestBackend
{
    internal static TestProcessCommand BuildInfoCommand(
        string dotnetPath,
        string targetPath,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        ValidateCommandInputs(dotnetPath, targetPath, workingDirectory);
        return new TestProcessCommand(
            dotnetPath,
            ["exec", targetPath, "--info"],
            workingDirectory,
            environment);
    }

    internal static TestProcessCommand BuildDiscoverCommand(
        string dotnetPath,
        string targetPath,
        string workingDirectory,
        MtpVersion version,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        ValidateCommandInputs(dotnetPath, targetPath, workingDirectory);
        return new TestProcessCommand(
            dotnetPath,
            ["exec", targetPath, .. MtpTestTooling.BuildListArguments(version)],
            workingDirectory,
            environment);
    }

    internal static TestProcessCommand BuildRunCommand(
        string dotnetPath,
        string targetPath,
        string workingDirectory,
        MtpVersion version,
        string resultArtifactPath,
        string framework,
        IReadOnlyList<string> selectedTestCaseIds,
        bool wholeSuite,
        bool filterCapabilityProven,
        bool hasTrxReportExtension = true,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        ValidateCommandInputs(dotnetPath, targetPath, workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(framework);
        ArgumentNullException.ThrowIfNull(selectedTestCaseIds);
        if (!wholeSuite && !filterCapabilityProven)
            throw new ContinuousTestProviderException(
                $"MTP framework '{framework}' has no proven selection filter; refusing a partial run.");

        string? filter = wholeSuite ? null : BuildFilter(framework, selectedTestCaseIds);
        if (!wholeSuite && string.IsNullOrWhiteSpace(filter))
            throw new ContinuousTestProviderException(
                $"MTP framework '{framework}' could not compose a selection filter; refusing a partial run.");

        IReadOnlyList<string> appArguments = MtpTestTooling.BuildRunArguments(
            version,
            resultArtifactPath,
            filter,
            wholeSuite,
            hasTrxReportExtension);
        return new TestProcessCommand(
            dotnetPath,
            ["exec", targetPath, .. appArguments],
            workingDirectory,
            environment);
    }

    internal static ProviderRunResult ParseTrxResult(
        string xml,
        string framework,
        string selectedRevision,
        string indexIdentity,
        IReadOnlyList<string> selectedTestCaseIds,
        string artifactPath)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentException.ThrowIfNullOrWhiteSpace(framework);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexIdentity);
        ArgumentNullException.ThrowIfNull(selectedTestCaseIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);

        XDocument document;
        try
        {
            using var reader = XmlReader.Create(
                new StringReader(xml),
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                });
            document = XDocument.Load(reader);
        }
        catch (XmlException exception)
        {
            throw new ContinuousTestProviderException(
                "Malformed MTP TRX result artifact: " + exception.Message,
                exception);
        }

        XElement root = document.Root
            ?? throw new ContinuousTestProviderException("MTP TRX result artifact was empty.");
        XNamespace ns = root.Name.Namespace;
        var namesByDefinitionId = root
            .Descendants(ns + "UnitTest")
            .Select(row =>
            (
                Id: row.Attribute("id")?.Value,
                Name: TrxDefinitionName(row, ns)
            ))
            .Where(row => !string.IsNullOrWhiteSpace(row.Id) && !string.IsNullOrWhiteSpace(row.Name))
            .ToDictionary(row => row.Id!, row => row.Name!, StringComparer.Ordinal);
        var results = new List<ProviderCaseResult>();
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement row in root.Descendants(ns + "UnitTestResult"))
        {
            string? displayName = row.Attribute("testName")?.Value;
            string? definitionId = row.Attribute("testId")?.Value;
            string? fullyQualifiedName = definitionId is not null
                && namesByDefinitionId.TryGetValue(definitionId, out string? definitionName)
                ? definitionName
                : displayName;
            string? outcome = row.Attribute("outcome")?.Value;
            if (string.IsNullOrWhiteSpace(fullyQualifiedName) || string.IsNullOrWhiteSpace(outcome))
                throw new ContinuousTestProviderException(
                    "MTP TRX result contained a test row without a name or outcome.");

            string? testCaseId = ResolveSelectedCase(
                framework,
                fullyQualifiedName,
                displayName ?? fullyQualifiedName,
                selectedTestCaseIds);
            if (selectedTestCaseIds.Count > 0 && testCaseId is null)
                continue;
            testCaseId ??= BuildCaseId(framework, fullyQualifiedName, displayName ?? fullyQualifiedName);
            reported.Add(testCaseId);
            results.Add(new ProviderCaseResult(
                Id: row.Attribute("executionId")?.Value ?? ResultId(artifactPath, testCaseId),
                TestCaseId: testCaseId,
                Status: MapOutcome(outcome),
                ResultRevision: selectedRevision,
                IndexIdentity: indexIdentity,
                DurationSeconds: ParseDuration(row.Attribute("duration")?.Value),
                FailureSummary: FailureSummary(row, ns),
                Metadata: new Dictionary<string, object?>
                {
                    ["artifact_path"] = artifactPath,
                    ["framework"] = framework,
                    ["outcome"] = outcome,
                }));
        }

        if (selectedTestCaseIds.Count > 0)
        {
            string[] missing = selectedTestCaseIds
                .Where(id => !reported.Contains(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (missing.Length > 0)
                throw new ContinuousTestProviderException(
                    "MTP TRX result was incomplete; no result was reported for selected test case(s): "
                    + string.Join(", ", missing));
        }

        if (results.Count == 0)
            throw new ContinuousTestProviderException(
                "MTP TRX result artifact contained no test cases.");

        var times = root.Element(ns + "Times");
        DateTimeOffset? startedAt = ParseDateTime(times?.Attribute("start")?.Value);
        DateTimeOffset? endedAt = ParseDateTime(times?.Attribute("finish")?.Value);
        string runId = root.Attribute("id")?.Value is { Length: > 0 } id
            ? $"mtp:{id}"
            : $"mtp:{Path.GetFileNameWithoutExtension(artifactPath)}";
        return new ProviderRunResult(
            runId,
            AggregateStatus(results.Select(result => result.Status)),
            startedAt,
            endedAt,
            results,
            artifactPath);
    }

    internal static ProviderRunResult ParseMachineResult(
        string output,
        string framework,
        string selectedRevision,
        string indexIdentity,
        IReadOnlyList<string> selectedTestCaseIds,
        bool truncated = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (truncated)
            throw new ContinuousTestProviderException("MTP machine result output was truncated.");
        ArgumentException.ThrowIfNullOrWhiteSpace(framework);
        ArgumentNullException.ThrowIfNull(selectedTestCaseIds);

        var rows = new List<(string Fqn, string DisplayName, string Status, double? Duration)>();
        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new ContinuousTestProviderException("MTP machine result row was not an object.");
                string? eventName = OptionalString(root, "event");
                if (eventName is not ("test_case" or "case_result"))
                    continue;
                string fqn = RequiredString(root, "fully_qualified_name", "test_case_id");
                string display = OptionalString(root, "display_name") ?? fqn;
                string status = RequiredString(root, "status");
                double? duration = OptionalDouble(root, "duration_seconds");
                rows.Add((fqn, display, status.ToLowerInvariant(), duration));
            }
            catch (JsonException exception)
            {
                throw new ContinuousTestProviderException(
                    "Malformed MTP machine result output: " + exception.Message,
                    exception);
            }
        }

        var results = new List<ProviderCaseResult>();
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string fqn, string display, string status, double? duration) in rows)
        {
            string? id = ResolveSelectedCase(framework, fqn, display, selectedTestCaseIds);
            if (selectedTestCaseIds.Count > 0 && id is null)
                continue;
            id ??= BuildCaseId(framework, fqn, display);
            if (!reported.Add(id))
                continue;
            results.Add(new ProviderCaseResult(
                ResultId("machine", id),
                id,
                status is "passed" or "failed" or "skipped" or "errored" ? status : "errored",
                selectedRevision,
                indexIdentity,
                duration));
        }

        string[] missing = selectedTestCaseIds
            .Where(id => !reported.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
            throw new ContinuousTestProviderException(
                "MTP machine result output was incomplete; no result was reported for selected test case(s): "
                + string.Join(", ", missing));
        if (results.Count == 0)
            throw new ContinuousTestProviderException("MTP machine result output contained no test cases.");

        return new ProviderRunResult(
            "mtp:machine",
            AggregateStatus(results.Select(result => result.Status)),
            CaseResults: results);
    }

    private static string? BuildFilter(string framework, IReadOnlyList<string> selectedTestCaseIds)
    {
        if (!framework.Equals("mstest", StringComparison.OrdinalIgnoreCase)
            && !framework.Equals("nunit", StringComparison.OrdinalIgnoreCase)
            && !framework.Equals("xunit", StringComparison.OrdinalIgnoreCase))
            return null;

        var terms = selectedTestCaseIds
            .Select(id => GenericSelector(id, framework))
            .Where(selector => !string.IsNullOrWhiteSpace(selector))
            .Distinct(StringComparer.Ordinal)
            .Select(selector =>
            {
                string value = selector!;
                string property = value.Contains('.', StringComparison.Ordinal) ? "FullyQualifiedName" : "Name";
                string operation = value.Contains('(', StringComparison.Ordinal) ? "~" : "=";
                string comparable = operation == "~" ? value[..value.IndexOf('(')].TrimEnd() : value;
                return $"{property}{operation}{VsTestFilterValue.Escape(comparable)}";
            })
            .ToArray();
        return terms.Length == 0 ? null : string.Join("|", terms);
    }

    private static string? ResolveSelectedCase(
        string framework,
        string fullyQualifiedName,
        string displayName,
        IReadOnlyList<string> selectedTestCaseIds)
    {
        foreach (string id in selectedTestCaseIds)
        {
            string? selector = GenericSelector(id, framework);
            string? selectedDisplay = GenericDisplayName(id);
            if ((string.Equals(selector, fullyQualifiedName, StringComparison.Ordinal)
                || string.Equals(selector, displayName, StringComparison.Ordinal)
                || (selector is not null
                    && selector.Contains('.', StringComparison.Ordinal)
                    && selector[(selector.LastIndexOf('.') + 1)..]
                        .Equals(displayName, StringComparison.Ordinal)))
                && (selectedDisplay is null
                    || string.Equals(selectedDisplay, displayName, StringComparison.Ordinal)
                    || string.Equals(selectedDisplay, fullyQualifiedName, StringComparison.Ordinal)))
                return id;
        }

        if (selectedTestCaseIds.Count != 1)
            return null;

        string selected = selectedTestCaseIds[0];
        string? selectedSelector = GenericSelector(selected, framework);
        if (selectedSelector is null)
            return null;
        if (selectedSelector.Equals(fullyQualifiedName, StringComparison.Ordinal)
            || selectedSelector.Equals(displayName, StringComparison.Ordinal)
            || (selectedSelector.Contains('.', StringComparison.Ordinal)
                && selectedSelector[(selectedSelector.LastIndexOf('.') + 1)..]
                    .Equals(displayName, StringComparison.Ordinal)))
            return selected;
        return null;
    }

    private static string? GenericSelector(string id, string framework)
    {
        string prefix = framework + ":";
        if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        string selector = id[prefix.Length..];
        int displayMarker = selector.IndexOf("::display=", StringComparison.Ordinal);
        return displayMarker < 0 ? selector : selector[..displayMarker];
    }

    private static string? GenericDisplayName(string id)
    {
        int marker = id.IndexOf("::display=", StringComparison.Ordinal);
        return marker < 0 ? null : id[(marker + "::display=".Length)..];
    }

    private static string BuildCaseId(string framework, string fqn, string displayName) =>
        string.Equals(fqn, displayName, StringComparison.Ordinal)
            ? $"{framework}:{fqn}"
            : $"{framework}:{fqn}::display={displayName}";

    private static string MapOutcome(string outcome) =>
        outcome.Trim().ToLowerInvariant() switch
        {
            "passed" => "passed",
            "skipped" or "notexecuted" or "inconclusive" => "skipped",
            "failed" or "error" or "timeout" or "aborted" => "failed",
            _ => "errored",
        };

    private static string? FailureSummary(XElement row, XNamespace ns) =>
        row.Descendants(ns + "ErrorInfo")
            .Select(info => info.Element(ns + "Message")?.Value.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? TrxDefinitionName(XElement row, XNamespace ns)
    {
        string? name = row.Attribute("name")?.Value;
        XElement? method = row.Element(ns + "TestMethod");
        string? className = method?.Attribute("className")?.Value;
        string? methodName = method?.Attribute("name")?.Value;
        if (!string.IsNullOrWhiteSpace(className) && !string.IsNullOrWhiteSpace(methodName))
        {
            int parameterStart = methodName.IndexOf(" (", StringComparison.Ordinal);
            if (parameterStart > 0)
                methodName = methodName[..parameterStart];
            return className + "." + methodName;
        }
        return name;
    }

    private static double? ParseDuration(string? value) =>
        TimeSpan.TryParse(value, out TimeSpan duration) ? duration.TotalSeconds : null;

    private static DateTimeOffset? ParseDateTime(string? value) =>
        DateTimeOffset.TryParse(value, out DateTimeOffset parsed) ? parsed : null;

    private static string ResultId(string artifactPath, string testCaseId) =>
        "mtp:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(artifactPath + "\u0000" + testCaseId)))
            .ToLowerInvariant();

    private static string RequiredString(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (element.TryGetProperty(name, out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString()!;
        }

        throw new ContinuousTestProviderException(
            "MTP machine result row was incomplete; test identity was missing.");
    }

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? OptionalDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value)
            && value.TryGetDouble(out double parsed)
            ? parsed
            : null;

    private static string AggregateStatus(IEnumerable<string> statuses) =>
        statuses.Any(status => status is "failed" or "errored")
            ? "failed"
            : statuses.Any(status => status != "skipped")
                ? "passed"
                : "skipped";

    private static void ValidateCommandInputs(string dotnetPath, string targetPath, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dotnetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
    }
}
