using Miller.Indexing;
using Miller.Server.Logging;
using Xunit;

namespace Miller.Tests.Logging;

/// <summary>
/// Pins <see cref="PartialExtractLog.DescribePartial"/> — the pure helper the scan callers use to surface a
/// PARTIAL julie-extract report (the artifact loaded, but a file failed to parse, so its symbols are absent).
/// A healthy report yields null (no warning); a partial report yields a warning naming the failed-file count,
/// the diagnostic codes, and the affected paths so the loss is never hidden behind a clean "scan complete".
/// </summary>
public sealed class PartialExtractLogTests
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

    [Fact]
    public void HealthyReport_ReturnsNull()
    {
        Assert.Null(PartialExtractLog.DescribePartial(Report("ok", filesFailed: 0, revision: 7)));
    }

    [Fact]
    public void PartialReport_NamesFailedCount_Codes_AndAffectedPaths()
    {
        var report = Report("partial", filesFailed: 2, revision: 9,
            ParseError("broken/a.rs"), ParseError("broken/b.rs"));

        string? warning = PartialExtractLog.DescribePartial(report);

        Assert.NotNull(warning);
        Assert.Contains("PARTIAL", warning!);
        Assert.Contains("2 file(s)", warning);       // the dropped-file count
        Assert.Contains("revision 9", warning);
        Assert.Contains("parse_error", warning);     // the diagnostic code
        Assert.Contains("broken/a.rs", warning);     // the affected paths (prefers root_relative_path)
        Assert.Contains("broken/b.rs", warning);
    }

    [Fact]
    public void PartialReport_NoStructuredErrors_StillWarns_WithPlaceholders()
    {
        // A partial with files_failed but an empty errors[] still warns (the count alone is the signal); the
        // codes/paths degrade to explicit placeholders rather than an empty, misleading line.
        string? warning = PartialExtractLog.DescribePartial(Report("partial", filesFailed: 1, revision: 4));

        Assert.NotNull(warning);
        Assert.Contains("1 file(s)", warning!);
        Assert.Contains("(no structured errors)", warning);
        Assert.Contains("(paths unavailable)", warning);
    }
}
