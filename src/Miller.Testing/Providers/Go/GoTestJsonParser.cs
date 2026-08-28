using System.Text.Json;

namespace Miller.Testing;

internal sealed record GoTestJsonEvent(
    string Action,
    string? Package,
    string? Test,
    double? Elapsed,
    string? Output,
    string? FailedBuild);

internal sealed record GoBuildJsonEvent(
    string Action,
    string? ImportPath,
    string? Output);

internal sealed record GoTestJsonParseResult(
    IReadOnlyList<GoTestJsonEvent> TestEvents,
    IReadOnlyList<GoBuildJsonEvent> BuildEvents,
    IReadOnlyList<string> UnknownActions,
    bool HasMalformedLines);

internal static class GoTestJsonParser
{
    private static readonly HashSet<string> TestActions = new(StringComparer.Ordinal)
    {
        "start",
        "run",
        "pause",
        "cont",
        "pass",
        "fail",
        "output",
        "skip",
        "bench",
    };

    private static readonly HashSet<string> BuildActions = new(StringComparer.Ordinal)
    {
        "build-output",
        "build-fail",
    };

    internal static GoTestJsonParseResult Parse(string? output)
    {
        var tests = new List<GoTestJsonEvent>();
        var builds = new List<GoBuildJsonEvent>();
        var unknown = new List<string>();
        var malformed = false;
        if (string.IsNullOrEmpty(output))
            return new(tests, builds, unknown, false);

        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !TryString(root, "Action", out string? action)
                    || string.IsNullOrWhiteSpace(action))
                {
                    malformed = true;
                    continue;
                }

                if (BuildActions.Contains(action))
                {
                    builds.Add(new(
                        action,
                        OptionalString(root, "ImportPath"),
                        OptionalString(root, "Output")));
                }
                else if (TestActions.Contains(action))
                {
                    tests.Add(new(
                        action,
                        OptionalString(root, "Package"),
                        OptionalString(root, "Test"),
                        OptionalDouble(root, "Elapsed"),
                        OptionalString(root, "Output"),
                        OptionalString(root, "FailedBuild")));
                }
                else
                {
                    unknown.Add(action);
                }
            }
            catch (JsonException)
            {
                malformed = true;
            }
        }

        return new(tests, builds, unknown.Distinct(StringComparer.Ordinal).ToArray(), malformed);
    }

    private static bool TryString(JsonElement root, string property, out string? value)
    {
        value = OptionalString(root, property);
        return value is not null;
    }

    private static string? OptionalString(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? OptionalDouble(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out double number)
            ? number
            : null;
}
