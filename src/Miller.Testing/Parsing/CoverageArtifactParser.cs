using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Miller.Testing.Parsing;

public sealed record ParsedCoverageArtifactRun(
    IReadOnlyList<ParsedCoverageArtifactFile> Files);

public sealed record ParsedCoverageArtifactFile(
    string Format,
    string SourcePath,
    IReadOnlyList<ParsedCoverageLineHit> LineHits,
    string? TestName = null);

public sealed record ParsedCoverageLineHit(
    int LineNumber,
    int Hits);

public static class CoverageArtifactParser
{
    public static ParsedCoverageArtifactRun Parse(string path, string parser)
    {
        return parser switch
        {
            "lcov" => ParseLcov(path),
            "cobertura" => ParseCobertura(path),
            _ => throw new TestArtifactParseException($"unsupported coverage parser: {parser}"),
        };
    }

    public static ParsedCoverageArtifactRun ParseLcov(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("must not be empty", nameof(path));

        var files = new List<ParsedCoverageArtifactFile>();
        string? currentPath = null;
        string? currentTestName = null;
        var lineHits = new List<ParsedCoverageLineHit>();

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("TN:", StringComparison.Ordinal))
            {
                currentTestName = line[3..].Length == 0 ? null : line[3..];
                continue;
            }

            if (line.StartsWith("SF:", StringComparison.Ordinal))
            {
                AddCurrentFile();
                currentPath = line[3..];
                lineHits.Clear();
                continue;
            }

            if (line.StartsWith("DA:", StringComparison.Ordinal))
            {
                lineHits.Add(ParseDa(line[3..]));
                continue;
            }

            if (line == "end_of_record")
            {
                AddCurrentFile();
                currentPath = null;
                currentTestName = null;
                lineHits.Clear();
            }
        }

        AddCurrentFile();
        return new ParsedCoverageArtifactRun(files);

        void AddCurrentFile()
        {
            if (string.IsNullOrWhiteSpace(currentPath))
                return;

            files.Add(new ParsedCoverageArtifactFile(
                Format: "lcov",
                SourcePath: currentPath,
                LineHits: lineHits.ToArray(),
                TestName: currentTestName));
        }
    }

    public static ParsedCoverageArtifactRun ParseCobertura(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("must not be empty", nameof(path));

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(stream, SafeXmlReaderSettings());
            var document = XDocument.Load(reader, LoadOptions.None);
            var files = document
                .Descendants()
                .Where(element => string.Equals(element.Name.LocalName, "class", StringComparison.Ordinal))
                .Select(classElement =>
                {
                    var sourcePath = AttributeValue(classElement, "filename");
                    if (string.IsNullOrWhiteSpace(sourcePath))
                        return null;

                    var lineHits = classElement
                        .Descendants()
                        .Where(element => string.Equals(element.Name.LocalName, "line", StringComparison.Ordinal))
                        .Select(line => LineHitOrNull(line))
                        .Where(hit => hit is not null)
                        .Cast<ParsedCoverageLineHit>()
                        .ToArray();
                    return new ParsedCoverageArtifactFile(
                        Format: "cobertura",
                        SourcePath: sourcePath,
                        LineHits: lineHits);
                })
                .Where(file => file is not null)
                .Cast<ParsedCoverageArtifactFile>()
                .ToArray();
            return new ParsedCoverageArtifactRun(files);
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

    private static ParsedCoverageLineHit ParseDa(string value)
    {
        var parts = value.Split(',', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lineNumber) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hits))
            throw new TestArtifactParseException($"invalid LCOV DA line: {value}");

        return new ParsedCoverageLineHit(lineNumber, hits);
    }

    private static ParsedCoverageLineHit? LineHitOrNull(XElement line)
    {
        var lineNumber = AttributeValue(line, "number");
        var hits = AttributeValue(line, "hits");
        if (lineNumber is null || hits is null)
            return null;
        if (!int.TryParse(lineNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLineNumber) ||
            !int.TryParse(hits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedHits))
            throw new TestArtifactParseException($"invalid Cobertura line hit: {line}");
        return new ParsedCoverageLineHit(parsedLineNumber, parsedHits);
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

    private static string? AttributeValue(XElement element, string name) =>
        element.Attribute(name)?.Value;
}
