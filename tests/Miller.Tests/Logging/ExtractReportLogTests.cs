using Miller.Indexing;
using Miller.Server.Logging;
using Xunit;

namespace Miller.Tests.Logging;

public sealed class ExtractReportLogTests
{
    private static ExtractReport Report(string status, long filesFailed, long? revision, params ReportDiagnostic[] errors) =>
        new(
            ReportSchemaVersion: 1,
            Status: status,
            Operation: "scan",
            Mode: "full",
            Input: null,
            Artifact: null,
            Tool: null,
            RevisionBlock: revision is { } rev ? new ExtractRevision(rev, rev) : null,
            Counts: new ExtractCounts(
                FilesScanned: 0, FilesChanged: 0, FilesUnchanged: 0, FilesUnsupported: 0,
                FilesDeleted: 0, FilesFailed: filesFailed, RowsWritten: null, Totals: null),
            Errors: errors,
            Warnings: System.Array.Empty<ReportDiagnostic>());

    private static ReportDiagnostic ParseError(string rootRelativePath) =>
        new("parse_error", "tree-sitter failed", Path: "/abs/" + rootRelativePath,
            RootRelativePath: rootRelativePath, Recoverable: true);

    private static ReportDiagnostic SlowFileWarning(string rootRelativePath) =>
        new("slow_file_skipped", "file exceeded extraction timeout", Path: "/abs/" + rootRelativePath,
            RootRelativePath: rootRelativePath, Recoverable: true);

    [Fact]
    public void HealthyReport_ReturnsNull()
    {
        Assert.Null(ExtractReportLog.DescribeWarning(Report("ok", filesFailed: 0, revision: 7)));
    }

    [Fact]
    public void PartialReport_NamesFailedCount_Codes_AndAffectedPaths()
    {
        var report = Report("partial", filesFailed: 2, revision: 9,
            ParseError("broken/a.rs"), ParseError("broken/b.rs"));

        string? warning = ExtractReportLog.DescribeWarning(report);

        Assert.Equal(
            "julie-extract returned a PARTIAL artifact — 2 file(s) failed to parse and are absent from the index " +
            "(revision 9). Codes: parse_error, parse_error. Affected: broken/a.rs, broken/b.rs.",
            warning);
    }

    [Fact]
    public void PartialReport_NoStructuredErrors_StillWarns_WithPlaceholders()
    {
        string? warning = ExtractReportLog.DescribeWarning(Report("partial", filesFailed: 1, revision: 4));

        Assert.NotNull(warning);
        Assert.Contains("1 file(s)", warning!);
        Assert.Contains("(no structured errors)", warning);
        Assert.Contains("(paths unavailable)", warning);
    }

    [Fact]
    public void NoChangeReport_WithSlowFileWarning_NamesCodeAndAffectedPath()
    {
        ExtractReport report = Report("no_change", filesFailed: 0, revision: 7) with
        {
            Warnings = [SlowFileWarning("generated/slow.kt")],
        };

        string? warning = ExtractReportLog.DescribeWarning(report);

        Assert.NotNull(warning);
        Assert.Contains("slow_file_skipped", warning, StringComparison.Ordinal);
        Assert.Contains("generated/slow.kt", warning, StringComparison.Ordinal);
    }
}
