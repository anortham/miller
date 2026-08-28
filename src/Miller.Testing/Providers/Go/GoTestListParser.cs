using System.Text.RegularExpressions;

namespace Miller.Testing;

internal sealed record GoTestListResult(
    IReadOnlyList<string> Names,
    bool HasMalformedLines);

internal static partial class GoTestListParser
{
    internal static GoTestListResult Parse(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return new([], false);

        var names = new List<string>();
        var malformed = false;
        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || IsKnownSummary(line))
                continue;
            if (TopLevelTestRegex().IsMatch(line))
            {
                names.Add(line);
                continue;
            }

            if (line.StartsWith("Test", StringComparison.Ordinal))
            {
                if (line.Contains('/', StringComparison.Ordinal))
                    continue;
                malformed = true;
                continue;
            }

            if (line.StartsWith("Example", StringComparison.Ordinal)
                || line.StartsWith("Benchmark", StringComparison.Ordinal)
                || line.StartsWith("Fuzz", StringComparison.Ordinal))
                continue;

            malformed = true;
        }

        return new(names.Distinct(StringComparer.Ordinal).ToArray(), malformed);
    }

    private static bool IsKnownSummary(string line) =>
        line.StartsWith("ok ", StringComparison.Ordinal)
        || line.StartsWith("? ", StringComparison.Ordinal)
        || line.Equals("FAIL", StringComparison.Ordinal)
        || line.StartsWith("FAIL\t", StringComparison.Ordinal)
        || line.StartsWith("# ", StringComparison.Ordinal)
        || line.StartsWith("warning:", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("---", StringComparison.Ordinal);

    [GeneratedRegex(@"^Test[A-Z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex TopLevelTestRegex();
}
