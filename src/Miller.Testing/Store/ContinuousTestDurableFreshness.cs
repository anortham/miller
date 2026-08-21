using System.Globalization;

namespace Miller.Testing;

/// <summary>
/// Shared "committed fresh at key K" and "watermark covers K" rules. Freshness is always the
/// composite <see cref="CtFreshnessKey"/>; a matching revision number on a different index
/// identity is not fresh.
/// </summary>
public static class ContinuousTestDurableFreshness
{
    private const string DiscoveryFailureSource = "ct-project-status";
    private const string DiscoveryFailureKind = "ct-project-discovery-failure";

    public static bool IsCommittedFreshAt(ContinuousTestStatus status, CtFreshnessKey selected)
    {
        ArgumentNullException.ThrowIfNull(status);

        bool committed = status.State is ContinuousTestState.Green
            or ContinuousTestState.Red
            or ContinuousTestState.Skipped;
        return committed
            && string.Equals(status.IndexIdentity, selected.IndexIdentity, StringComparison.Ordinal)
            && status.Revision == selected.Revision;
    }

    public static bool IsWatermarkFreshAt(CtFreshnessKey watermark, CtFreshnessKey selected) =>
        string.Equals(watermark.IndexIdentity, selected.IndexIdentity, StringComparison.Ordinal)
        && watermark.Revision >= selected.Revision;

    /// <summary>
    /// THE per-row freshness rule: committed at the selected key, or GREEN with a watermark that
    /// covers it. Only greens ride the watermark — a red or skipped row stays where it ran until
    /// its test reruns. Every consumer (the status projection, the queue's fresh-case trim) must
    /// use this one rule instead of re-deriving it.
    /// </summary>
    public static bool IsFreshAt(
        ContinuousTestStatus status,
        CtFreshnessKey selected,
        IReadOnlyDictionary<string, CtFreshnessKey>? watermarks)
    {
        if (IsCommittedFreshAt(status, selected))
            return true;

        return status.State == ContinuousTestState.Green
            && watermarks is not null
            && watermarks.TryGetValue(status.TestCaseId, out CtFreshnessKey watermark)
            && IsWatermarkFreshAt(watermark, selected);
    }

    public static IReadOnlyList<string> NormalizeDeltaPaths(IReadOnlyList<string> changedPaths)
    {
        ArgumentNullException.ThrowIfNull(changedPaths);
        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        return changedPaths
            .Select(path =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(path, nameof(changedPaths));
                return path.Replace('\\', '/').TrimStart('/');
            })
            .Distinct(comparer)
            .Order(comparer)
            .ToArray();
    }

    public static bool TryGetCompleteDelta(
        ContinuousTestDaemonChange change,
        out long fromRevision,
        out long toRevision,
        out IReadOnlyList<string> changedPaths)
    {
        ArgumentNullException.ThrowIfNull(change);
        fromRevision = 0;
        toRevision = 0;
        changedPaths = [];
        if (change.DeltaCompleteness != ContinuousTestDeltaCompleteness.Complete
            || change.DeltaFromRevision is not { } from
            || change.DeltaToRevision is not { } to
            || from >= to)
        {
            return false;
        }

        fromRevision = from;
        toRevision = to;
        changedPaths = NormalizeDeltaPaths(change.ChangedPaths);
        return changedPaths.Count > 0;
    }

    public static bool HasActiveDiscoveryFailure(
        IReadOnlyList<ContinuousTestCase> testCases,
        string projectPath)
    {
        ArgumentNullException.ThrowIfNull(testCases);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        return testCases.Any(row =>
            string.Equals(row.Source, DiscoveryFailureSource, StringComparison.Ordinal)
            && string.Equals(MetadataString(row, "kind"), DiscoveryFailureKind, StringComparison.Ordinal)
            && ProjectPathMatches(MetadataString(row, "ct_project_path"), projectPath));
    }

    private static bool ProjectPathMatches(string? testCaseProjectPath, string projectPath)
    {
        if (string.IsNullOrWhiteSpace(testCaseProjectPath))
            return false;

        return string.Equals(
            Path.GetFullPath(testCaseProjectPath),
            Path.GetFullPath(projectPath),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string? MetadataString(ContinuousTestCase row, string name)
    {
        if (!row.Metadata.TryGetValue(name, out object? item) || item is null)
            return null;
        return Convert.ToString(item, CultureInfo.InvariantCulture);
    }
}
