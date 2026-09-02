using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Miller.Testing.Parsing;

namespace Miller.Testing.Providers.Shared;

/// <summary>One test case read from a JUnit XML report.</summary>
public sealed record JUnitXmlTestCase(
    string SuiteName,
    string? ClassName,
    string Name,
    string Status,
    double? DurationSeconds,
    string? FailureMessage,
    string? FailureText);

/// <summary>A discrepancy between a report aggregate attribute and its case rows.</summary>
public sealed record JUnitXmlAggregateMismatch(
    string ElementName,
    string AttributeName,
    string DeclaredValue,
    int ActualValue)
{
    public override string ToString() =>
        $"{ElementName} {AttributeName} declares {DeclaredValue}, but case rows contain {ActualValue}.";
}

/// <summary>The parsed cases and diagnostics retained from a JUnit XML report.</summary>
public sealed record JUnitXmlParseResult(
    IReadOnlyList<JUnitXmlTestCase> Cases,
    IReadOnlyList<JUnitXmlAggregateMismatch> AggregateMismatches,
    IReadOnlyList<string> Diagnostics)
{
    public bool HasAggregateMismatch => AggregateMismatches.Count != 0;

    public bool IsAggregateConsistent => !HasAggregateMismatch;
}

/// <summary>Reads the common JUnit XML dialects emitted by continuous-test providers.</summary>
public static class JUnitXmlResultParser
{
    public static JUnitXmlParseResult Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        try
        {
            using var text = new StringReader(xml);
            using XmlReader reader = XmlReader.Create(text, SafeXmlReaderSettings());
            XDocument document = XDocument.Load(reader, LoadOptions.None);
            XElement root = document.Root ?? throw new XmlException("document has no root element");
            if (!IsRoot(root))
                throw new XmlException($"unsupported root element '{root.Name.LocalName}'");

            XElement[] caseElements = root
                .DescendantsAndSelf()
                .Where(IsTestCase)
                .ToArray();
            if (caseElements.Length == 0)
                throw new TestArtifactParseException("JUnit XML report contained zero test cases.");

            var diagnostics = new List<string>();
            var cases = new List<JUnitXmlTestCase>(caseElements.Length);
            var statuses = new Dictionary<XElement, string>(ReferenceEqualityComparer.Instance);
            foreach (XElement element in caseElements)
            {
                XElement? suite = element.Ancestors().FirstOrDefault(IsTestSuite);
                string suiteName = AttributeValue(suite, "name") ?? "junit";
                string? className = AttributeValue(element, "classname")
                    ?? AttributeValue(element, "class");
                string name = AttributeValue(element, "name")
                    ?? AttributeValue(element, "method")
                    ?? throw new TestArtifactParseException(
                        "JUnit XML report contained a test case without a name.");
                (string status, string? failureMessage, string? failureText) =
                    ReadStatus(element, name, diagnostics);
                statuses[element] = status;
                double? duration = ReadDuration(AttributeValue(element, "time"), name, diagnostics);
                cases.Add(new JUnitXmlTestCase(
                    suiteName,
                    className,
                    name,
                    status,
                    duration,
                    failureMessage,
                    failureText));
            }

            IReadOnlyList<JUnitXmlAggregateMismatch> mismatches =
                ReadAggregateMismatches(root, caseElements, statuses);
            return new(cases, mismatches, diagnostics);
        }
        catch (TestArtifactParseException)
        {
            throw;
        }
        catch (XmlException exception) when (IsUnsafeXmlException(exception))
        {
            throw new TestArtifactParseException(
                "unsafe XML: DTDs and external entities are not allowed",
                exception);
        }
        catch (XmlException exception)
        {
            throw new TestArtifactParseException("malformed XML: " + exception.Message, exception);
        }
    }

    public static JUnitXmlParseResult ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path));
    }

    private static XmlReaderSettings SafeXmlReaderSettings() =>
        new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };

    private static bool IsRoot(XElement element) =>
        string.Equals(element.Name.LocalName, "testsuite", StringComparison.Ordinal)
        || string.Equals(element.Name.LocalName, "testsuites", StringComparison.Ordinal);

    private static bool IsTestSuite(XElement element) =>
        string.Equals(element.Name.LocalName, "testsuite", StringComparison.Ordinal);

    private static bool IsTestCase(XElement element) =>
        string.Equals(element.Name.LocalName, "testcase", StringComparison.Ordinal);

    private static (string Status, string? FailureMessage, string? FailureText) ReadStatus(
        XElement element,
        string name,
        ICollection<string> diagnostics)
    {
        XElement? failure = Child(element, "failure");
        XElement? error = Child(element, "error");
        XElement? skipped = Child(element, "skipped");
        if (failure is not null && error is not null)
            diagnostics.Add($"test case '{name}' contains both failure and error elements.");
        if (failure is not null)
            return ("failed", AttributeValue(failure, "message"), ElementText(failure));
        if (error is not null)
            return ("errored", AttributeValue(error, "message"), ElementText(error));
        if (skipped is not null)
            return ("skipped", null, null);

        string? rawStatus = AttributeValue(element, "status");
        return rawStatus?.ToLowerInvariant() switch
        {
            null or "" or "pass" or "passed" => ("passed", null, null),
            "fail" or "failed" => ("failed", null, null),
            "skip" or "skipped" or "pending" or "notrun" => ("skipped", null, null),
            "error" or "errored" => ("errored", null, null),
            _ => UnknownStatus(rawStatus, name, diagnostics),
        };
    }

    private static (string Status, string? FailureMessage, string? FailureText) UnknownStatus(
        string status,
        string name,
        ICollection<string> diagnostics)
    {
        diagnostics.Add($"test case '{name}' has unsupported status '{status}'.");
        return ("errored", null, null);
    }

    private static double? ReadDuration(
        string? value,
        string name,
        ICollection<string> diagnostics)
    {
        if (value is null)
            return null;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double duration)
            && double.IsFinite(duration)
            && duration >= 0)
            return duration;
        diagnostics.Add($"test case '{name}' has an invalid duration '{value}'.");
        return null;
    }

    private static IReadOnlyList<JUnitXmlAggregateMismatch> ReadAggregateMismatches(
        XElement root,
        IReadOnlyList<XElement> caseElements,
        IReadOnlyDictionary<XElement, string> statuses)
    {
        var casesBySuite = new Dictionary<XElement, List<XElement>>(ReferenceEqualityComparer.Instance);
        foreach (XElement testCase in caseElements)
        {
            foreach (XElement suite in testCase.Ancestors().Where(IsTestSuite))
            {
                if (!casesBySuite.TryGetValue(suite, out List<XElement>? suiteCases))
                {
                    suiteCases = [];
                    casesBySuite.Add(suite, suiteCases);
                }
                suiteCases.Add(testCase);
            }
        }

        var mismatches = new List<JUnitXmlAggregateMismatch>();
        foreach (XElement suite in root.DescendantsAndSelf().Where(IsTestSuite))
        {
            if (casesBySuite.TryGetValue(suite, out List<XElement>? suiteCases))
                CheckAggregate(suite, suiteCases, statuses, mismatches);
            else
                CheckAggregate(suite, [], statuses, mismatches);
        }
        if (string.Equals(root.Name.LocalName, "testsuites", StringComparison.Ordinal))
            CheckAggregate(root, caseElements, statuses, mismatches);
        return mismatches;
    }

    private static void CheckAggregate(
        XElement element,
        IReadOnlyList<XElement> caseElements,
        IReadOnlyDictionary<XElement, string> statuses,
        ICollection<JUnitXmlAggregateMismatch> mismatches)
    {
        int failures = caseElements.Count(testCase => statuses[testCase] == "failed");
        int errors = caseElements.Count(testCase => statuses[testCase] == "errored");
        int skipped = caseElements.Count(testCase => statuses[testCase] == "skipped");
        CheckCount(element, "tests", caseElements.Count, mismatches);
        CheckCount(element, "failures", failures, mismatches);
        CheckCount(element, "errors", errors, mismatches);
        CheckCount(element, "skipped", skipped, mismatches);
    }

    private static void CheckCount(
        XElement element,
        string attributeName,
        int actual,
        ICollection<JUnitXmlAggregateMismatch> mismatches)
    {
        string? declared = AttributeValue(element, attributeName, trim: false);
        if (declared is null)
            return;
        if (!int.TryParse(declared, NumberStyles.Integer, CultureInfo.InvariantCulture, out int expected)
            || expected != actual)
        {
            mismatches.Add(new JUnitXmlAggregateMismatch(
                element.Name.LocalName,
                attributeName,
                declared,
                actual));
        }
    }

    private static XElement? Child(XElement element, string localName) =>
        element.Elements().FirstOrDefault(child =>
            string.Equals(child.Name.LocalName, localName, StringComparison.Ordinal));

    private static string? ElementText(XElement element)
    {
        string text = element.Value.Trim();
        return text.Length == 0 ? null : text;
    }

    private static string? AttributeValue(
        XElement? element,
        string name,
        bool trim = true)
    {
        string? value = element?.Attribute(name)?.Value;
        if (value is null)
            return null;
        if (trim)
            value = value.Trim();
        return value.Length == 0 ? null : value;
    }

    private static bool IsUnsafeXmlException(XmlException exception) =>
        exception.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("entity", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("external", StringComparison.OrdinalIgnoreCase);
}
