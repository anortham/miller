using Miller.Indexing;

namespace Miller.Server.Logging;

/// <summary>
/// Produces operator-facing warning text from successful or partial julie-extract reports.
/// </summary>
public static class ExtractReportLog
{
    /// <summary>
    /// Returns warning text with diagnostic codes and affected paths, or <c>null</c> for a healthy report.
    /// </summary>
    /// <param name="report">The report returned by an extract scan/update/delete.</param>
    public static string? DescribeWarning(ExtractReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.IsPartial)
            return DescribePartial(report);
        if (report.Warnings.Count == 0)
            return null;

        string codes = string.Join(", ", report.Warnings.Select(warning => warning.Code));
        string affected = DescribeAffectedPaths(report.Warnings);
        return $"julie-extract returned {report.Warnings.Count} warning(s). Codes: {codes}. Affected: {affected}.";
    }

    private static string DescribePartial(ExtractReport report)
    {
        string codes = report.Errors.Count == 0
            ? "(no structured errors)"
            : string.Join(", ", report.Errors.Select(e => e.Code));

        string affected = DescribeAffectedPaths(report.Errors);
        string revision = report.Revision?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";
        return $"julie-extract returned a PARTIAL artifact — {report.FilesFailed} file(s) failed to parse and are " +
               $"absent from the index (revision {revision}). Codes: {codes}. Affected: {affected}.";
    }

    private static string DescribeAffectedPaths(IEnumerable<ReportDiagnostic> diagnostics)
    {
        string[] paths = diagnostics
            .Select(diagnostic => diagnostic.RootRelativePath ?? diagnostic.Path)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToArray();
        return paths.Length == 0 ? "(paths unavailable)" : string.Join(", ", paths);
    }
}
