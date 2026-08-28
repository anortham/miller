using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Miller.Testing.Parsing;

namespace Miller.Testing.Providers.Qml;

public static class QTestResultParser
{
    public static ParsedTestArtifactRun Parse(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("must not be empty", nameof(path));

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(
                stream,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                });
            var document = XDocument.Load(reader, LoadOptions.None);
            XElement root = document.Root ?? throw new XmlException("document has no root element");
            string suiteName = root.Attribute("name")?.Value ?? "qtest";
            var cases = root
                .Descendants()
                .Where(element => string.Equals(element.Name.LocalName, "testcase", StringComparison.Ordinal))
                .Select(testCase => ParseCase(testCase, suiteName, path))
                .ToArray();
            if (cases.Length == 0)
                throw new ContinuousTestProviderException(
                    $"QTest report '{path}' contained zero test cases.");
            return new ParsedTestArtifactRun("qt-quick-test", cases);
        }
        catch (XmlException exception) when (IsUnsafeXmlException(exception))
        {
            throw new TestArtifactParseException(
                "unsafe XML: DTDs and external entities are not allowed", exception);
        }
        catch (XmlException exception)
        {
            throw new TestArtifactParseException("malformed XML: " + exception.Message, exception);
        }
    }

    private static ParsedTestArtifactCase ParseCase(XElement element, string suiteName, string path)
    {
        string name = element.Attribute("name")?.Value
            ?? element.Attribute("method")?.Value
            ?? throw new ContinuousTestProviderException(
                $"QTest report '{path}' contained a test case without a name.");
        if (string.IsNullOrWhiteSpace(name))
            throw new ContinuousTestProviderException(
                $"QTest report '{path}' contained a test case without a name.");

        string? className = NullIfBlank(element.Attribute("classname")?.Value);
        (string status, string? failureText) = Status(element);
        return new ParsedTestArtifactCase(
            suiteName,
            className,
            name,
            className is { Length: > 0 }
                ? className.Replace('.', '/') + "::" + name
                : Path.GetFileName(path) + "::" + name,
            "qt-quick-test",
            status,
            Duration(element.Attribute("time")?.Value),
            failureText,
            NullIfBlank(element.Attribute("file")?.Value));
    }

    private static (string Status, string? FailureText) Status(XElement element)
    {
        XElement? failure = Child(element, "failure");
        if (failure is not null)
            return ("failed", ElementText(failure));
        XElement? error = Child(element, "error");
        if (error is not null)
            return ("errored", ElementText(error));
        if (Child(element, "skipped") is not null)
            return ("skipped", null);

        return (element.Attribute("result")?.Value.ToLowerInvariant() switch
        {
            "pass" or "passed" => "passed",
            "fail" or "failed" => "failed",
            "skip" or "skipped" or "notrun" => "skipped",
            null or "" => "passed",
            _ => "errored",
        }, null);
    }

    private static XElement? Child(XElement element, string name) =>
        element.Elements().FirstOrDefault(child => string.Equals(child.Name.LocalName, name, StringComparison.Ordinal));

    private static string? ElementText(XElement element)
    {
        string text = element.Value.Trim();
        if (text.Length > 0)
            return text;
        string? message = element.Attribute("message")?.Value;
        return string.IsNullOrWhiteSpace(message) ? null : message;
    }

    private static double? Duration(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double duration)
            ? duration
            : null;

    private static bool IsUnsafeXmlException(XmlException exception) =>
        exception.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("entity", StringComparison.OrdinalIgnoreCase);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
