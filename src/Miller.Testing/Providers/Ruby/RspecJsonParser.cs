using System.Globalization;
using System.Text.Json;
using Miller.Testing.Parsing;

namespace Miller.Testing;

internal sealed record RspecJsonExample(
    string Id,
    string Description,
    string FullDescription,
    string Status,
    string FilePath,
    int? LineNumber,
    double? RunTime,
    string? PendingMessage,
    string? FailureMessage);

internal sealed record RspecJsonParseResult(
    string? Version,
    IReadOnlyList<RspecJsonExample> Examples,
    int? DeclaredExampleCount,
    int? DeclaredFailureCount,
    int? DeclaredPendingCount,
    int ErrorsOutsideExamplesCount,
    IReadOnlyList<string> Diagnostics)
{
    public bool HasAggregateMismatch => Diagnostics.Any(static diagnostic =>
        diagnostic.StartsWith("RSpec summary", StringComparison.Ordinal));
}

internal static class RspecJsonParser
{
    internal static RspecJsonParseResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new TestArtifactParseException("RSpec JSON report was empty.");

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new TestArtifactParseException("RSpec JSON report root was not an object.");
            if (!root.TryGetProperty("examples", out JsonElement examples)
                || examples.ValueKind != JsonValueKind.Array)
            {
                throw new TestArtifactParseException("RSpec JSON report did not contain an examples array.");
            }

            string? version = OptionalString(root, "version");
            var parsed = new List<RspecJsonExample>();
            foreach (JsonElement element in examples.EnumerateArray())
                parsed.Add(ParseExample(element));

            var diagnostics = new List<string>();
            int? declaredExampleCount = null;
            int? declaredFailureCount = null;
            int? declaredPendingCount = null;
            int errorsOutsideExamplesCount = 0;
            if (root.TryGetProperty("summary", out JsonElement summary))
            {
                if (summary.ValueKind != JsonValueKind.Object)
                    throw new TestArtifactParseException("RSpec JSON report summary was not an object.");
                declaredExampleCount = OptionalInt(summary, "example_count");
                declaredFailureCount = OptionalInt(summary, "failure_count");
                declaredPendingCount = OptionalInt(summary, "pending_count");
                errorsOutsideExamplesCount = OptionalInt(summary, "errors_outside_of_examples_count") ?? 0;
                if (errorsOutsideExamplesCount < 0)
                    throw new TestArtifactParseException("RSpec JSON report had a negative outside-example error count.");

                CheckAggregate(
                    diagnostics,
                    "example_count",
                    declaredExampleCount,
                    parsed.Count);
                CheckAggregate(
                    diagnostics,
                    "failure_count",
                    declaredFailureCount,
                    parsed.Count(example => example.Status == "failed"));
                CheckAggregate(
                    diagnostics,
                    "pending_count",
                    declaredPendingCount,
                    parsed.Count(example => example.Status == "skipped"));
            }

            return new(
                version,
                parsed,
                declaredExampleCount,
                declaredFailureCount,
                declaredPendingCount,
                errorsOutsideExamplesCount,
                diagnostics);
        }
        catch (TestArtifactParseException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new TestArtifactParseException("malformed RSpec JSON: " + exception.Message, exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new TestArtifactParseException("malformed RSpec JSON: " + exception.Message, exception);
        }
    }

    internal static RspecJsonParseResult ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (IOException exception)
        {
            throw new TestArtifactParseException($"could not read RSpec JSON report '{path}'.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new TestArtifactParseException($"could not read RSpec JSON report '{path}'.", exception);
        }
    }

    private static RspecJsonExample ParseExample(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new TestArtifactParseException("RSpec JSON report contained a non-object example.");

        string id = RequiredString(element, "id");
        string filePath = RequiredString(element, "file_path");
        string status = NormalizeStatus(RequiredString(element, "status"));
        string description = OptionalString(element, "description") ?? id;
        string fullDescription = OptionalString(element, "full_description") ?? description;
        int? lineNumber = OptionalInt(element, "line_number");
        if (lineNumber is < 1)
            lineNumber = null;
        double? runTime = OptionalDouble(element, "run_time");
        if (runTime is < 0 or double.NaN or double.PositiveInfinity or double.NegativeInfinity)
            runTime = null;
        string? pendingMessage = OptionalString(element, "pending_message");
        string? failureMessage = null;
        if (element.TryGetProperty("exception", out JsonElement exception))
        {
            if (exception.ValueKind != JsonValueKind.Object)
                throw new TestArtifactParseException($"RSpec example '{id}' had a non-object exception.");
            failureMessage = OptionalString(exception, "message");
        }

        return new(
            id,
            description,
            fullDescription,
            status,
            filePath,
            lineNumber,
            runTime,
            pendingMessage,
            failureMessage);
    }

    private static string RequiredString(JsonElement element, string property)
    {
        string? value = OptionalString(element, property);
        if (string.IsNullOrWhiteSpace(value))
            throw new TestArtifactParseException($"RSpec JSON example was missing '{property}'.");
        return value;
    }

    private static string? OptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? OptionalInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetInt32(out int integer)
            ? integer
            : throw new TestArtifactParseException($"RSpec JSON property '{property}' was not a 32-bit integer.");
    }

    private static double? OptionalDouble(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetDouble(out double number) && double.IsFinite(number)
            ? number
            : throw new TestArtifactParseException($"RSpec JSON property '{property}' was not a finite number.");
    }

    private static void CheckAggregate(
        ICollection<string> diagnostics,
        string property,
        int? declared,
        int actual)
    {
        if (declared is not null && declared.Value != actual)
        {
            diagnostics.Add(
                $"RSpec summary {property} declares {declared.Value.ToString(CultureInfo.InvariantCulture)}, "
                + $"but example rows contain {actual.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    private static string NormalizeStatus(string status) =>
        status.Trim().ToLowerInvariant() switch
        {
            "passed" or "pass" => "passed",
            "failed" or "fail" => "failed",
            "pending" or "skipped" or "skip" => "skipped",
            _ => "failed",
        };
}
