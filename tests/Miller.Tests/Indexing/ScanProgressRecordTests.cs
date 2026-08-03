using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins Miller's read of julie-extract's progress-file contract v1: the parser must survive every damaged
/// shape the contract says a consumer will see — a torn trailing line, a blank line, one malformed record in
/// the middle — because it only ever runs while Miller is already reporting a failure.
/// </summary>
public sealed class ScanProgressRecordTests
{
    private const string Record =
        """{"progress_schema_version":1,"pid":48213,"phase":"extraction_spool","elapsed_ms":4231,""" +
        """ "files_discovered":1786,"files_supported":1786,""" +
        """ "files_extracted":1024,"files_spooled":1024}""";

    [Fact]
    public void TryParse_ARecordOfTheDocumentedShape_ReadsEveryCounter()
    {
        var parsed = ScanProgressRecord.TryParse(Record);

        Assert.NotNull(parsed);
        Assert.Equal("extraction_spool", parsed!.Phase);
        Assert.Equal(4231, parsed.ElapsedMs);
        Assert.Equal(1786, parsed.FilesDiscovered);
        Assert.Equal(1786, parsed.FilesSupported);
        Assert.Equal(1024, parsed.FilesExtracted);
        Assert.Equal(1024, parsed.FilesSpooled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"phase\":")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    public void TryParse_AnythingUnusable_IsNullRatherThanAThrow(string? line)
    {
        Assert.Null(ScanProgressRecord.TryParse(line));
    }

    [Fact]
    public void TryParse_UnknownFieldsAndMissingCounters_AreToleratedPerTheContract()
    {
        var parsed = ScanProgressRecord.TryParse("""{"phase":"discovery","invented_later":true}""");

        Assert.NotNull(parsed);
        Assert.Equal("discovery", parsed!.Phase);
        Assert.Equal(0, parsed.FilesExtracted);
    }

    [Fact]
    public void LastIn_ATornTrailingLine_FallsBackToTheLastCompleteRecord()
    {
        string text = Record + "\n" + """{"phase":"artifact_write","elapsed""";

        Assert.Equal("extraction_spool", ScanProgressRecord.LastIn(text)!.Phase);
    }

    [Fact]
    public void LastIn_ATrailingRecordThatParsesButLostItsNewline_IsStillDroppedAsAnIncompleteTail()
    {
        string text = Record + "\n" + """{"phase":"artifact_write","files_extracted":74000}""";

        Assert.Equal("extraction_spool", ScanProgressRecord.LastIn(text)!.Phase);
    }

    [Fact]
    public void LastIn_ASingleUnterminatedRecord_IsNoRecordAtAll()
    {
        Assert.Null(ScanProgressRecord.LastIn(Record));
    }

    [Fact]
    public void LastIn_AMalformedRecordMidFile_DoesNotStopTheRead()
    {
        string text = Record + "\n" + """{"phase":"tor""" + "\n"
            + """{"phase":"artifact_write","files_extracted":74000}""" + "\n";

        var parsed = ScanProgressRecord.LastIn(text);

        Assert.Equal("artifact_write", parsed!.Phase);
        Assert.Equal(74000, parsed.FilesExtracted);
    }

    [Fact]
    public void LastIn_AnEmptyOrAbsentFile_IsNull()
    {
        Assert.Null(ScanProgressRecord.LastIn(null));
        Assert.Null(ScanProgressRecord.LastIn(""));
        Assert.Null(ScanProgressRecord.LastIn("\n\n\n"));
    }

    [Fact]
    public void DescribeLastProgress_NamesThePhaseAndHowFarItGot()
    {
        string path = Path.Combine(Path.GetTempPath(), $"miller-progress-{Guid.NewGuid():N}.progress");
        File.WriteAllText(path, Record + "\n");
        try
        {
            string? described = ScanProgressRecord.DescribeLastProgress(path);

            Assert.NotNull(described);
            Assert.Contains("extraction_spool", described!, StringComparison.Ordinal);
            Assert.Contains("1024/1786", described, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DescribeLastProgress_AnAbsentOrUnnamedFile_IsNullRatherThanAThrow()
    {
        Assert.Null(ScanProgressRecord.DescribeLastProgress(null));
        Assert.Null(ScanProgressRecord.DescribeLastProgress("   "));
        Assert.Null(ScanProgressRecord.DescribeLastProgress(
            Path.Combine(Path.GetTempPath(), $"miller-absent-{Guid.NewGuid():N}.progress")));
    }
}
