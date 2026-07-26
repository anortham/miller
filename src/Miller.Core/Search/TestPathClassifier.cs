using System.Text.RegularExpressions;

namespace Miller.Core.Search;

public static partial class TestPathClassifier
{
    private static readonly HashSet<string> TestSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "test", "tests", "__tests__", "spec", "specs", "testdata", "fixtures",
    };

    private static readonly string[] FileNameInfixes = [".test.", ".spec.", ".tests."];
    private static readonly string[] PascalSuffixes = ["Test", "Tests", "Spec", "Specs"];

    public static bool Check(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        string[] segments = filePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (TestSegments.Contains(segments[i]))
                return true;
            if (segments[i].EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                || segments[i].EndsWith(".Test", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        string fileName = segments[^1];
        if (TestSegments.Contains(fileName))
            return true;

        foreach (string infix in FileNameInfixes)
            if (fileName.Contains(infix, StringComparison.OrdinalIgnoreCase))
                return true;

        return StemLooksLikeTest(StripExtension(fileName));
    }

    private static bool StemLooksLikeTest(string stem)
    {
        if (stem.Length == 0)
            return false;
        if (BoundaryTestToken().IsMatch(stem))
            return true;
        foreach (string suffix in PascalSuffixes)
            if (stem.Length > suffix.Length && stem.EndsWith(suffix, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static string StripExtension(string fileName)
    {
        string extension = Path.GetExtension(fileName);
        return extension.Length > 0 ? fileName[..^extension.Length] : fileName;
    }

    [GeneratedRegex(@"(^|[._-])(test|tests|spec|specs)([._-]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BoundaryTestToken();
}
