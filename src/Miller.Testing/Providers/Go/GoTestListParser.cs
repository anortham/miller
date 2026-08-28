using System.Text;

namespace Miller.Testing;

internal sealed record GoTestListResult(
    IReadOnlyList<string> Names,
    bool HasMalformedLines);

internal static class GoTestListParser
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
            if (line.StartsWith("Test", StringComparison.Ordinal)
                && line.Contains('/', StringComparison.Ordinal))
                continue;
            if (IsTopLevelTestName(line))
            {
                names.Add(line);
                continue;
            }

            if (line.StartsWith("Test", StringComparison.Ordinal))
            {
                if (line.Contains('/', StringComparison.Ordinal))
                    continue;
                if (IsGoIdentifier(line))
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

    private static bool IsTopLevelTestName(string line)
    {
        if (!line.StartsWith("Test", StringComparison.Ordinal))
            return false;
        if (!IsGoIdentifier(line))
            return false;

        Rune[] suffix = line["Test".Length..].EnumerateRunes().ToArray();
        return suffix.Length == 0 || !Rune.IsLower(suffix[0]);
    }

    private static bool IsGoIdentifier(string line)
    {
        Rune[] runes = line.EnumerateRunes().ToArray();
        if (runes.Length == 0 || !(runes[0].Value == '_' || Rune.IsLetter(runes[0])))
            return false;
        return runes.Skip(1).All(rune => rune.Value == '_' || Rune.IsLetter(rune) || Rune.IsDigit(rune));
    }

    private static bool IsKnownSummary(string line) =>
        line.StartsWith("ok ", StringComparison.Ordinal)
        || line.StartsWith("? ", StringComparison.Ordinal)
        || line.Equals("FAIL", StringComparison.Ordinal)
        || line.StartsWith("FAIL\t", StringComparison.Ordinal)
        || line.StartsWith("# ", StringComparison.Ordinal)
        || line.StartsWith("warning:", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("---", StringComparison.Ordinal);

}
