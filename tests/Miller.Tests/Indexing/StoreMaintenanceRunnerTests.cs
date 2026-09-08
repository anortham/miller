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
    [InlineData("""{"counts":{"pruned_request_rows":-1}}""")]
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

    [Fact]
    public void ParseReport_WhenFailed_DoesNotFabricateZeroUsageAndSetsUnavailable()
    {
        // Pinned incident replay: julie-extract maintenance raced with coord.db change,
        // producing failure_class = stale_plan with zeroes in retention fields.
        string failedJson =
            """
            {
              "action": "inspect",
              "disposition": "failed",
              "failure_class": "stale_plan",
              "error": {
                "class": "stale_plan",
                "code": "maintenance_inspection_raced",
                "message": "coordinator queue changed while planning"
              },
              "retention": {
                "pressure": false,
                "compaction_required": false,
                "target_bytes": 0,
                "retained_logical_bytes": 0,
                "physical_target_bytes": 0,
                "physical_current_bytes": 0,
                "physical_breach_streak": 0,
                "physical_breach_limit": 10
              }
            }
            """;

        StoreMaintenanceReport report = StoreMaintenanceRunner.ParseReport(failedJson);

        Assert.False(report.IsAvailable);
        Assert.Equal("stale_plan", report.FailureClass);
        Assert.Equal("maintenance_inspection_raced: coordinator queue changed while planning", report.ErrorMessage);
        Assert.Null(report.Retention);
        Assert.Null(report.Capacity);
    }

    [Fact]
    public void ParseReport_WhenSuccessfulWithPressure_ParsesRetentionAndCapacity()
    {
        string successJson =
            """
            {
              "action": "inspect",
              "disposition": "ok",
              "failure_class": "none",
              "retention": {
                "pressure": true,
                "compaction_required": true,
                "target_bytes": 50000000,
                "retained_logical_bytes": 75000000,
                "ceiling_bytes": 100000000,
                "physical_current_bytes": 145000000,
                "physical_baseline_bytes": 80000000,
                "physical_target_bytes": 100000000,
                "physical_ceiling_bytes": 200000000,
                "physical_target_breached": true,
                "physical_ceiling_breached": false,
                "physical_breach_streak": 6,
                "physical_breach_limit": 5
              },
              "capacity": {
                "measured_bytes": 95000000,
                "free_bytes": 5000000,
                "store_page_bytes": 4096,
                "store_freelist_bytes": 0,
                "store_wal_bytes": 1000000,
                "staged_generation_bytes": 500000,
                "gc_fits": true,
                "promotion_fits": true
              },
              "readers": {
                "protected_reader_count": 2,
                "definitively_dead_reader_count": 0,
                "retained_unknown_reader_count": 1,
                "removed_reader_count": 0,
                "reader_warnings": [
                  {
                    "pin_id": "view-abc",
                    "warning_code": "long_lived_pin"
                  }
                ]
              }
            }
            """;

        StoreMaintenanceReport report = StoreMaintenanceRunner.ParseReport(successJson);

        Assert.True(report.IsAvailable);
        Assert.Equal("none", report.FailureClass);
        Assert.Null(report.ErrorMessage);
        Assert.NotNull(report.Retention);
        Assert.True(report.Retention.Pressure);
        Assert.True(report.Retention.CompactionRequired);
        Assert.Equal(50_000_000, report.Retention.TargetBytes);
        Assert.Equal(75_000_000, report.Retention.RetainedLogicalBytes);
        Assert.Equal(100_000_000, report.Retention.PhysicalTargetBytes);
        Assert.Equal(145_000_000, report.Retention.PhysicalCurrentBytes);
        Assert.Equal(6, report.Retention.PhysicalBreachStreak);
        Assert.Equal(5, report.Retention.PhysicalBreachLimit);
        Assert.NotNull(report.Readers);
        Assert.Equal(2, report.Readers.ProtectedReaderCount);
        Assert.Equal(1, report.Readers.RetainedUnknownReaderCount);
        Assert.Single(report.Readers.ReaderWarnings);
        Assert.Equal("view-abc", report.Readers.ReaderWarnings[0].PinId);
        Assert.Equal("long_lived_pin", report.Readers.ReaderWarnings[0].WarningCode);

        Assert.NotNull(report.Capacity);
        Assert.Equal(95_000_000, report.Capacity.MeasuredBytes);
        Assert.Equal(5_000_000, report.Capacity.FreeBytes);
        Assert.True(report.Capacity.GcFits);
        Assert.True(report.Capacity.PromotionFits);
    }

    [Fact]
    public void SnapshotPersistence_CanSaveAndReadRecordedSnapshot()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"miller-snapshot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string sampleJson =
                """
                {
                  "action": "inspect",
                  "disposition": "ok",
                  "failure_class": "none",
                  "retention": {
                    "pressure": false,
                    "compaction_required": false,
                    "target_bytes": 10000000,
                    "retained_logical_bytes": 8000000,
                    "ceiling_bytes": 20000000,
                    "physical_current_bytes": 15000000,
                    "physical_baseline_bytes": 10000000,
                    "physical_target_bytes": 20000000,
                    "physical_ceiling_bytes": 30000000,
                    "physical_target_breached": false,
                    "physical_ceiling_breached": false,
                    "physical_breach_streak": 0,
                    "physical_breach_limit": 5
                  },
                  "capacity": {
                    "measured_bytes": 18000000,
                    "free_bytes": 12000000,
                    "store_page_bytes": 4096,
                    "store_freelist_bytes": 0,
                    "store_wal_bytes": 0,
                    "staged_generation_bytes": 0,
                    "gc_fits": true,
                    "promotion_fits": true
                  }
                }
                """;

            bool saved = StoreMaintenanceRunner.TrySaveRecordedSnapshot(tempDir, sampleJson);
            Assert.True(saved);

            StoreMaintenanceReport? loaded = StoreMaintenanceRunner.TryReadRecordedSnapshot(tempDir);
            Assert.NotNull(loaded);
            Assert.True(loaded.IsAvailable);
            Assert.Equal("none", loaded.FailureClass);
            Assert.NotNull(loaded.Retention);
            Assert.Equal(10_000_000, loaded.Retention.TargetBytes);
            Assert.Equal(8_000_000, loaded.Retention.RetainedLogicalBytes);
            Assert.Equal(20_000_000, loaded.Retention.PhysicalTargetBytes);
            Assert.Equal(15_000_000, loaded.Retention.PhysicalCurrentBytes);
            Assert.NotNull(loaded.Capacity);
            Assert.Equal(18_000_000, loaded.Capacity.MeasuredBytes);
            Assert.Equal(12_000_000, loaded.Capacity.FreeBytes);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void TryReadRecordedSnapshot_WhenMissingOrCorrupt_ReturnsNull()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"miller-snapshot-corrupt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            Assert.Null(StoreMaintenanceRunner.TryReadRecordedSnapshot(tempDir));

            File.WriteAllText(Path.Combine(tempDir, "maintenance-snapshot.json"), "corrupted-non-json-content");
            Assert.Null(StoreMaintenanceRunner.TryReadRecordedSnapshot(tempDir));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }
}
