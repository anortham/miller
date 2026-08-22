using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Miller.Testing.Parsing;

public sealed class TestArtifactParseException : Exception
{
    public string Code { get; }

    public TestArtifactParseException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = "test_artifact.parse_error";
    }
}

public sealed record ParsedTestArtifactRun(
    string Framework,
    IReadOnlyList<ParsedTestArtifactCase> Cases);

/// <summary>
/// One case from a junit/xunit report.
///
/// <para><paramref name="File"/> is the source file the reporter named on the case, verbatim and usually
/// absolute, or null when the report names none. It is the only per-file signal some reporters give:
/// node's junit reporter writes one document for the whole run and puts the SAME classname on every case,
/// so without this attribute a partially red node:test suite cannot be told apart file by file.</para>
/// </summary>
public sealed record ParsedTestArtifactCase(
    string SuiteName,
    string? ClassName,
    string Name,
    string Selector,
    string Framework,
    string Status,
    double? DurationSeconds,
    string? FailureText,
    string? File = null);

public static class JunitTestResultParser
{
    public static ParsedTestArtifactRun Parse(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("must not be empty", nameof(path));

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(stream, SafeXmlReaderSettings());
            var document = XDocument.Load(reader, LoadOptions.None);
            var root = document.Root ?? throw new XmlException("document has no root element");
            return IsXunitDocument(root) ? ParseXunit(root, path) : ParseJunit(root, path);
        }
        catch (XmlException ex) when (IsUnsafeXmlException(ex))
        {
            throw new TestArtifactParseException("unsafe XML: DTDs and external entities are not allowed", ex);
        }
        catch (XmlException ex)
        {
            throw new TestArtifactParseException("malformed XML: " + ex.Message, ex);
        }
    }

    private static XmlReaderSettings SafeXmlReaderSettings() =>
        new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };

    private static bool IsUnsafeXmlException(XmlException ex) =>
        ex.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("entity", StringComparison.OrdinalIgnoreCase);

    private static bool IsXunitDocument(XElement root) =>
        string.Equals(root.Name.LocalName, "assembly", StringComparison.Ordinal)
        || root.Descendants().Any(element => string.Equals(element.Name.LocalName, "test", StringComparison.Ordinal));

    private static ParsedTestArtifactRun ParseJunit(XElement root, string path)
    {
        var framework = FrameworkName(AttributeValue(root, "name") ?? AttributeValue(root, "test-framework") ?? Path.GetFileNameWithoutExtension(path));
        var suiteName = AttributeValue(root, "name") ?? "junit";
        var cases = root
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "testcase", StringComparison.Ordinal))
            .Select(testcase =>
            {
                var className = AttributeValue(testcase, "classname");
                var name = AttributeValue(testcase, "name") ?? AttributeValue(testcase, "method") ?? "unknown";
                var (status, failureText) = JunitStatus(testcase);
                return new ParsedTestArtifactCase(
                    SuiteName: suiteName,
                    ClassName: className,
                    Name: name,
                    Selector: Selector(className, name, path),
                    Framework: framework,
                    Status: status,
                    DurationSeconds: Duration(AttributeValue(testcase, "time")),
                    FailureText: failureText,
                    File: NullIfBlank(AttributeValue(testcase, "file")));
            })
            .ToArray();
        return new ParsedTestArtifactRun(framework, cases);
    }

    private static ParsedTestArtifactRun ParseXunit(XElement root, string path)
    {
        var suiteName = AttributeValue(root, "name") ?? "xunit";
        var cases = root
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "test", StringComparison.Ordinal))
            .Select(test =>
            {
                var className = AttributeValue(test, "type");
                var name = AttributeValue(test, "method") ?? LastSegment(AttributeValue(test, "name")) ?? "unknown";
                return new ParsedTestArtifactCase(
                    SuiteName: suiteName,
                    ClassName: className,
                    Name: name,
                    Selector: Selector(className, name, path),
                    Framework: "xunit",
                    Status: XunitStatus(AttributeValue(test, "result")),
                    DurationSeconds: Duration(AttributeValue(test, "time")),
                    FailureText: FailureText(test));
            })
            .ToArray();
        return new ParsedTestArtifactRun("xunit", cases);
    }

    private static (string Status, string? FailureText) JunitStatus(XElement testcase)
    {
        if (Child(testcase, "failure") is { } failure)
            return ("failed", ElementText(failure));
        if (Child(testcase, "error") is { } error)
            return ("errored", ElementText(error));
        if (Child(testcase, "skipped") is not null)
            return ("skipped", null);
        return ("passed", null);
    }

    private static string XunitStatus(string? value) =>
        (value ?? "").ToLowerInvariant() switch
        {
            "pass" or "passed" => "passed",
            "fail" or "failed" => "failed",
            "skip" or "skipped" => "skipped",
            _ => "passed",
        };

    private static string Selector(string? className, string name, string path) =>
        className is { Length: > 0 }
            ? className.Replace('.', '/') + "::" + name
            : Path.GetFileName(path) + "::" + name;

    private static double? Duration(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
            ? duration
            : null;
    }

    private static string? FailureText(XElement element)
    {
        var failure = Child(element, "failure");
        if (failure is null)
            return null;
        return Child(failure, "message") is { } message ? ElementText(message) : ElementText(failure);
    }

    private static string? ElementText(XElement element)
    {
        var text = element.Value.Trim();
        return text.Length > 0 ? text : AttributeValue(element, "message");
    }

    private static string FrameworkName(string value)
    {
        var normalized = value.ToLowerInvariant();
        if (normalized.Contains("pytest", StringComparison.Ordinal))
            return "pytest";
        if (normalized.Contains("xunit", StringComparison.Ordinal))
            return "xunit";
        return normalized.Length == 0 ? "junit" : normalized;
    }

    private static string? LastSegment(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        var separator = value.LastIndexOf('.');
        return separator < 0 ? value : value[(separator + 1)..];
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static XElement? Child(XElement element, string localName) =>
        element.Elements().FirstOrDefault(child => string.Equals(child.Name.LocalName, localName, StringComparison.Ordinal));

    private static string? AttributeValue(XElement element, string name) =>
        element.Attribute(name)?.Value;
}
