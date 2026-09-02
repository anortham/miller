using System.Xml;
using System.Xml.Linq;
using Miller.Testing.Parsing;

namespace Miller.Testing.Providers.Php;

internal sealed record PhpListedTest(
    string ClassName,
    string MethodName,
    string Selector,
    string? FilePath);

internal static class PhpListTestsXmlParser
{
    private const string PhpUnit12Namespace = "https://xml.phpunit.de/testSuite";

    internal static IReadOnlyList<PhpListedTest> Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        try
        {
            using var text = new StringReader(xml);
            using XmlReader reader = XmlReader.Create(text, SafeXmlReaderSettings());
            XDocument document = XDocument.Load(reader, LoadOptions.None);
            XElement root = document.Root ?? throw new XmlException("document has no root element");
            return root.Name.LocalName switch
            {
                "tests" => ParsePhpUnit10(root),
                "testSuite" when root.Name.NamespaceName == PhpUnit12Namespace => ParsePhpUnit12(root),
                "testSuite" => throw new XmlException(
                    $"unsupported PHPUnit test suite namespace '{root.Name.NamespaceName}'"),
                _ => throw new XmlException($"unsupported PHPUnit listing root '{root.Name.LocalName}'"),
            };
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
            throw new TestArtifactParseException("malformed PHPUnit listing XML: " + exception.Message, exception);
        }
    }

    private static IReadOnlyList<PhpListedTest> ParsePhpUnit10(XElement root)
    {
        var tests = new List<PhpListedTest>();
        var selectors = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement classElement in root.Elements().Where(element =>
                     string.Equals(element.Name.LocalName, "testCaseClass", StringComparison.Ordinal)))
        {
            ParseClass(classElement, "testCaseMethod", tests, selectors);
        }

        return tests;
    }

    private static IReadOnlyList<PhpListedTest> ParsePhpUnit12(XElement root)
    {
        XElement? testsElement = root.Elements().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, "tests", StringComparison.Ordinal));
        if (testsElement is null)
            throw new TestArtifactParseException("PHPUnit 11/12 listing is missing its <tests> container.");

        var tests = new List<PhpListedTest>();
        var selectors = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement classElement in testsElement.Elements().Where(element =>
                     string.Equals(element.Name.LocalName, "testClass", StringComparison.Ordinal)))
        {
            ParseClass(classElement, "testMethod", tests, selectors);
        }

        return tests;
    }

    private static void ParseClass(
        XElement classElement,
        string methodElementName,
        ICollection<PhpListedTest> tests,
        ISet<string> selectors)
    {
        string className = RequiredAttribute(classElement, "name", "test class");
        string? filePath = OptionalAttribute(classElement, "file");
        foreach (XElement methodElement in classElement.Elements().Where(element =>
                     string.Equals(element.Name.LocalName, methodElementName, StringComparison.Ordinal)))
        {
            string id = RequiredAttribute(methodElement, "id", "test method");
            int separator = id.IndexOf("::", StringComparison.Ordinal);
            if (separator <= 0 || separator + 2 >= id.Length)
                throw new TestArtifactParseException(
                    $"PHPUnit listing test method id '{id}' is not a class::method selector.");

            string idClass = NormalizeClassName(id[..separator]);
            string normalizedClass = NormalizeClassName(className);
            if (!string.Equals(idClass, normalizedClass, StringComparison.Ordinal))
                throw new TestArtifactParseException(
                    $"PHPUnit listing test method id '{id}' disagrees with enclosing class '{className}'.");

            string methodName = id[(separator + 2)..].Trim();
            if (methodName.Length == 0)
                throw new TestArtifactParseException($"PHPUnit listing test method id '{id}' has an empty method.");

            string selector = normalizedClass + "::" + methodName;
            if (!selectors.Add(selector))
                throw new TestArtifactParseException(
                    $"PHPUnit listing returned duplicate test method '{selector}'.");
            tests.Add(new PhpListedTest(normalizedClass, methodName, selector, filePath));
        }
    }

    private static string RequiredAttribute(XElement element, string name, string kind)
    {
        string? value = OptionalAttribute(element, name);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new TestArtifactParseException($"PHPUnit listing {kind} is missing '{name}'.");
    }

    private static string? OptionalAttribute(XElement element, string name)
    {
        string? value = element.Attribute(name)?.Value.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string NormalizeClassName(string value)
    {
        string normalized = value.Trim();
        while (normalized.Contains("\\\\", StringComparison.Ordinal))
            normalized = normalized.Replace("\\\\", "\\", StringComparison.Ordinal);
        return normalized;
    }

    private static XmlReaderSettings SafeXmlReaderSettings() =>
        new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };

    private static bool IsUnsafeXmlException(XmlException exception) =>
        exception.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("entity", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("external", StringComparison.OrdinalIgnoreCase);
}
