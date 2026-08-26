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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("""{"action":"gc"}""")]
    [InlineData("""{"action":"gc","counts":{}}""")]
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
