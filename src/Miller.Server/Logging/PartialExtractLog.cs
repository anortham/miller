using Miller.Indexing;

namespace Miller.Server.Logging;

/// <summary>
/// The pure, I/O-free helper that turns a PARTIAL julie-extract report into the operator warning the scan
/// callers log. <see cref="JulieExtractRunner.Interpret"/> RETURNS a <c>status=="partial"</c> report (rather than
/// throwing) when the artifact is consistent but one or more files failed to parse — so the usable rows still
/// load. The documented contract is that the CALLER then surfaces the dropped files as a WARNING; otherwise a
/// partial scan is hidden behind a clean "scan complete" / "primed" and an operator never learns that
/// search/inspect/trace are silently missing those files' symbols.
///
/// <para>Mirrors <see cref="ExtractErrorLog"/>: a pure describe (no <c>ILogger</c> dependency, trivially unit
/// tested) whose returned string the catch/return sites interpolate into their own <c>LogWarning</c> template.
/// Returns <c>null</c> for a healthy report so a call site can branch with <c>is { } w</c>.</para>
/// </summary>
public static class PartialExtractLog
{
    /// <summary>
    /// A ready-to-log operator warning for a PARTIAL report — the failed-file count, the diagnostic codes, and the
    /// affected paths — or <c>null</c> when <paramref name="report"/> is not partial (nothing to warn about).
    /// </summary>
    /// <param name="report">The report returned by an extract scan/update/delete.</param>
    public static string? DescribePartial(ExtractReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!report.IsPartial)
            return null;

        string codes = report.Errors.Count == 0
            ? "(no structured errors)"
            : string.Join(", ", report.Errors.Select(e => e.Code));

        var paths = report.Errors
            .Select(e => e.RootRelativePath ?? e.Path)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();
        string affected = paths.Length == 0 ? "(paths unavailable)" : string.Join(", ", paths);

        string revision = report.Revision?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";
        return $"julie-extract returned a PARTIAL artifact — {report.FilesFailed} file(s) failed to parse and are " +
               $"absent from the index (revision {revision}). Codes: {codes}. Affected: {affected}.";
    }
}
