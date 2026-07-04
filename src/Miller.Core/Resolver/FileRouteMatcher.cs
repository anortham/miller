namespace Miller.Core.Resolver;

public static class FileRouteMatcher
{
    public static bool Matches(string referenceRoute, string fileRoute)
    {
        var referenceSegments = RouteSegments(referenceRoute);
        var fileSegments = RouteSegments(fileRoute);

        var referenceIndex = 0;
        for (var fileIndex = 0; fileIndex < fileSegments.Length; fileIndex++)
        {
            var fileSegment = fileSegments[fileIndex];
            var isLastFileSegment = fileIndex == fileSegments.Length - 1;

            if (IsOptionalCatchAllSegment(fileSegment))
                return isLastFileSegment;

            if (IsCatchAllSegment(fileSegment))
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
        IsDynamicSegment(fileSegment) || string.Equals(referenceSegment, fileSegment, StringComparison.Ordinal);

    internal static string[] RouteSegments(string route)
    {
        if (string.IsNullOrWhiteSpace(route))
            return [];

        var normalized = route.Trim().Replace('\\', '/');
        var suffixStart = SuffixStart(normalized);
        if (suffixStart >= 0)
            normalized = normalized[..suffixStart];

        return normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => !IsRouteGroup(segment))
            .ToArray();
    }

    private static bool IsRouteGroup(string segment) =>
        segment.Length >= 2 && segment[0] == '(' && segment[^1] == ')';

    private static int SuffixStart(string route)
    {
        for (var index = 0; index < route.Length; index++)
        {
            if (route[index] == '#')
                return index;

            if (route[index] == '?' && !IsColonOptionalMarker(route, index) && !IsBraceOptionalMarker(route, index))
                return index;
        }

        return -1;
    }

    // Laravel's optional route param is the brace form "{id?}" (effective_route_template preserves the raw '?'; only
    // normalize_brace_template folds it away). The '?' immediately before the segment-closing '}' is that marker, not
    // a query-string separator — without this, SuffixStart truncates "/users/{id?}" to "/users/{id" (an unmatchable
    // literal) and every optional-param Laravel route inside a prefix group loses its bridge edge.
    private static bool IsBraceOptionalMarker(string route, int markerIndex)
    {
        if (markerIndex + 1 >= route.Length || route[markerIndex + 1] != '}')
            return false;

        var segmentStart = route.LastIndexOf('/', markerIndex - 1);
        segmentStart = segmentStart < 0 ? 0 : segmentStart + 1;
        return segmentStart < markerIndex && route[segmentStart] == '{';
    }

    internal static bool IsDynamicSegment(string segment) =>
        IsBraceDynamic(segment) ||
        IsColonDynamic(segment) ||
        (segment.Length >= 3 &&
            segment[0] == '[' &&
            segment[^1] == ']' &&
            !IsCatchAllSegment(segment) &&
            !IsOptionalCatchAllSegment(segment));

    private static bool IsBraceDynamic(string segment) =>
        segment.Length >= 2 && segment[0] == '{' && segment[^1] == '}';

    internal static bool IsCatchAllSegment(string segment) =>
        (segment.StartsWith("[...", StringComparison.Ordinal) && segment.EndsWith(']')) ||
        IsColonCatchAll(segment);

    internal static bool IsOptionalCatchAllSegment(string segment) =>
        (segment.StartsWith("[[...", StringComparison.Ordinal) && segment.EndsWith("]]", StringComparison.Ordinal)) ||
        IsColonOptionalCatchAll(segment);

    private static bool IsColonDynamic(string segment) =>
        IsColonParameter(segment, string.Empty);

    private static bool IsColonCatchAll(string segment) =>
        IsColonParameter(segment, "*");

    private static bool IsColonOptionalCatchAll(string segment) =>
        IsColonParameter(segment, "*?") ||
        IsColonParameter(segment, "?");

    private static bool IsColonParameter(string segment, string suffix)
    {
        if (segment.Length <= 1 + suffix.Length || segment[0] != ':')
            return false;

        if (suffix.Length > 0 && !segment.EndsWith(suffix, StringComparison.Ordinal))
            return false;

        var nameEnd = suffix.Length == 0 ? segment.Length : segment.Length - suffix.Length;
        if (nameEnd <= 1)
            return false;

        for (var index = 1; index < nameEnd; index++)
        {
            var c = segment[index];
            if (index == 1)
            {
                if (!IsIdentifierStart(c))
                    return false;
            }
            else if (!IsIdentifierPart(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsColonOptionalMarker(string route, int markerIndex)
    {
        var segmentStart = route.LastIndexOf('/', markerIndex - 1);
        segmentStart = segmentStart < 0 ? 0 : segmentStart + 1;
        if (segmentStart >= markerIndex || route[segmentStart] != ':')
            return false;

        var nameEnd = route[markerIndex - 1] == '*' ? markerIndex - 1 : markerIndex;
        if (nameEnd <= segmentStart + 1)
            return false;

        for (var index = segmentStart + 1; index < nameEnd; index++)
        {
            var c = route[index];
            if (index == segmentStart + 1)
            {
                if (!IsIdentifierStart(c))
                    return false;
            }
            else if (!IsIdentifierPart(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIdentifierStart(char c) =>
        c == '_' ||
        (c >= 'A' && c <= 'Z') ||
        (c >= 'a' && c <= 'z');

    private static bool IsIdentifierPart(char c) =>
        IsIdentifierStart(c) ||
        (c >= '0' && c <= '9');
}
