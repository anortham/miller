namespace Miller.Core.Resolver;

public static class FileRouteMatcher
{
    public static bool Matches(string referenceRoute, string fileRoute)
    {
        var referenceSegments = Segments(referenceRoute);
        var fileSegments = Segments(fileRoute);

        var referenceIndex = 0;
        for (var fileIndex = 0; fileIndex < fileSegments.Length; fileIndex++)
        {
            var fileSegment = fileSegments[fileIndex];
            var isLastFileSegment = fileIndex == fileSegments.Length - 1;

            if (IsOptionalCatchAll(fileSegment))
                return isLastFileSegment;

            if (IsCatchAll(fileSegment))
                return isLastFileSegment && referenceIndex < referenceSegments.Length;

            if (referenceIndex >= referenceSegments.Length)
                return false;

            if (!SegmentMatches(referenceSegments[referenceIndex], fileSegment))
                return false;

            referenceIndex++;
        }

        return referenceIndex == referenceSegments.Length;
    }

    private static bool SegmentMatches(string referenceSegment, string fileSegment) =>
        IsDynamic(fileSegment) || string.Equals(referenceSegment, fileSegment, StringComparison.Ordinal);

    private static string[] Segments(string route)
    {
        if (string.IsNullOrWhiteSpace(route))
            return [];

        var normalized = route.Trim().Replace('\\', '/');
        var suffixStart = normalized.IndexOfAny(['?', '#']);
        if (suffixStart >= 0)
            normalized = normalized[..suffixStart];

        return normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => !IsRouteGroup(segment))
            .ToArray();
    }

    private static bool IsRouteGroup(string segment) =>
        segment.Length >= 2 && segment[0] == '(' && segment[^1] == ')';

    private static bool IsDynamic(string segment) =>
        IsBraceDynamic(segment) ||
        (segment.Length >= 3 &&
            segment[0] == '[' &&
            segment[^1] == ']' &&
            !IsCatchAll(segment) &&
            !IsOptionalCatchAll(segment));

    private static bool IsBraceDynamic(string segment) =>
        segment.Length >= 2 && segment[0] == '{' && segment[^1] == '}';

    private static bool IsCatchAll(string segment) =>
        segment.StartsWith("[...", StringComparison.Ordinal) && segment.EndsWith(']');

    private static bool IsOptionalCatchAll(string segment) =>
        segment.StartsWith("[[...", StringComparison.Ordinal) && segment.EndsWith("]]", StringComparison.Ordinal);
}
