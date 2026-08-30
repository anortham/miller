using Miller.Indexing.Store;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class StoreMaintenanceRunnerTests
{
    [Fact]
    public void APrunedRowCountIsReadOutOfTheMaintenanceReport()
    {
        StoreMaintenanceOutcome outcome = StoreMaintenanceRunner.ReadPrunedRequestRows(
            """{"action":"gc","counts":{"archived_requests":7,"pruned_request_rows":2163},"failure_class":"none"}""");

        Assert.Equal(2163, outcome.PrunedRequestRows);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public void TheProducersOwnErrorIsReadOutOfAFailedReport()
    {
        string? reported = StoreMaintenanceRunner.ReadReportedError(
            """{"action":"gc","failure_class":"invalid_arguments","error":{"class":"invalid_arguments","code":"view_not_found","message":"store has no view abc"}}""");

        Assert.Equal("view_not_found: store has no view abc", reported);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"action":"gc","error":null}""")]
    [InlineData("""{"action":"gc"}""")]
    [InlineData("""{"action":"gc","error":{"code":7,"message":["a"]}}""")]
    [InlineData("""{"action":"gc","error":{"code":{},"message":{}}}""")]
    public void AReportWithNoReadableErrorNamesNone(string reportJson) =>
        Assert.Null(StoreMaintenanceRunner.ReadReportedError(reportJson));

    [Fact]
    public void ANonzeroExitReportsTheProducersOwnErrorAlongsideTheExitCode()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake producer uses a POSIX executable.");

        string root = Path.Combine(Path.GetTempPath(), $"miller-store-maintenance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string report =
                """{"action":"gc","failure_class":"invalid_arguments","error":{"class":"invalid_arguments","code":"store_locked","message":"another writer holds the store"}}""";
            string binary = Path.Combine(root, "julie-extract");
            File.WriteAllText(binary, $"#!/bin/sh\nprintf '%s\\n' '{report}'\nexit 9\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    binary,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            StoreMaintenanceOutcome outcome = StoreMaintenanceRunner.Run(
                binary, root, TimeSpan.FromSeconds(5));

            Assert.Equal(0, outcome.PrunedRequestRows);
            Assert.Contains("exited 9", outcome.Error, StringComparison.Ordinal);
            Assert.Contains("another writer holds the store", outcome.Error, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("""{"action":"gc"}""")]
    [InlineData("""{"action":"gc","counts":{}}""")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("""{"counts":7}""")]
    [InlineData("""{"counts":{"pruned_request_rows":"2163"}}""")]
    [InlineData("""{"counts":{"pruned_request_rows":null}}""")]
    public void AReportThatNamesNoCountIsAnErrorRatherThanAZero(string reportJson)
    {
        StoreMaintenanceOutcome outcome = StoreMaintenanceRunner.ReadPrunedRequestRows(reportJson);

        Assert.Equal(0, outcome.PrunedRequestRows);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public void CombiningOutcomesSumsRowsAndJoinsEveryError()
    {
        StoreMaintenanceOutcome combined = StoreMaintenanceOutcome.Combine(
            StoreMaintenanceOutcome.Combine(
                new StoreMaintenanceOutcome(4, null),
                new StoreMaintenanceOutcome(0, "store busy")),
            new StoreMaintenanceOutcome(6, "timed out"));

        Assert.Equal(10, combined.PrunedRequestRows);
        Assert.Equal("store busy; timed out", combined.Error);
        Assert.True(combined.HasReport);
    }

    [Fact]
    public void AnEmptyOutcomeReportsNothing()
    {
        Assert.False(StoreMaintenanceOutcome.None.HasReport);
        Assert.False(new StoreMaintenanceOutcome(0, null).HasReport);
        Assert.True(new StoreMaintenanceOutcome(0, "broken").HasReport);
    }

    [Fact]
    public void AToolsRootWithNoExtractorHandsBackNoCallback()
    {
        Assert.Null(StoreMaintenanceRunner.ForToolsRoot(null));
        Assert.Null(StoreMaintenanceRunner.ForToolsRoot("   "));
        Assert.Null(StoreMaintenanceRunner.ForToolsRoot(
            Path.Combine(Path.GetTempPath(), "miller-no-tools-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void AMissingStoreRootIsSilentRatherThanAnError()
    {
        StoreMaintenanceOutcome outcome = StoreMaintenanceRunner.Run(
            "julie-extract",
            Path.Combine(Path.GetTempPath(), "miller-no-store-" + Guid.NewGuid().ToString("N")));

        Assert.False(outcome.HasReport);
    }
}
