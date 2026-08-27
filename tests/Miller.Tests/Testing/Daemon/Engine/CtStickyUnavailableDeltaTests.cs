using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing.Testing;
using Miller.Testing;
using Miller.Tests.Indexing.Resolution;
using Miller.Tests.Testing.Selection;
using Xunit;

namespace Miller.Tests.Testing.Daemon.Engine;

/// <summary>
/// An <c>unavailable_delta</c> poll is not the string <c>degraded</c>, so the loop used to record it
/// as a HEALTHY poll. The condition is sticky by design — the poller must not absorb an interval it
/// could not read — so a genuinely unreadable delta (a base that keeps moving, a store read failure)
/// turned the daemon into a 250 ms poll loop that enqueued nothing and reported itself as healthy.
/// These tests hold the bounded, reported behaviour, and pin that a TRUNCATED impact read is not
/// unavailable: it enqueues as an Unknown selection, so it never feeds this pause.
/// </summary>
public sealed class CtStickyUnavailableDeltaTests
{
    private const string FixtureIdentity = "ctgen1:artifact:art-1:blake3";

    [Fact]
    public void One_unavailable_answer_is_tolerated()
    {
        var tracker = new CtUnavailableDeltaTracker(limit: 3);
        Assert.False(tracker.RecordUnavailable("moving_cursor"));
        Assert.Equal(1, tracker.Streak);
        Assert.Null(tracker.StuckReason);
    }

    [Fact]
    public void The_limit_of_consecutive_unavailable_answers_reports_stuck_with_the_delta_reason()
    {
        var tracker = new CtUnavailableDeltaTracker(limit: 3);
        Assert.False(tracker.RecordUnavailable("moving_cursor"));
        Assert.False(tracker.RecordUnavailable("moving_cursor"));
        Assert.True(tracker.RecordUnavailable("moving_cursor"));
        Assert.True(tracker.RecordUnavailable("moving_cursor"));

        Assert.NotNull(tracker.StuckReason);
        Assert.Contains("impact unavailable", tracker.StuckReason, StringComparison.Ordinal);
        Assert.Contains("moving_cursor", tracker.StuckReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Any_other_answer_clears_the_streak_and_the_reason()
    {
        var tracker = new CtUnavailableDeltaTracker(limit: 2);
        Assert.False(tracker.RecordUnavailable(null));
        Assert.True(tracker.RecordUnavailable(null));
        Assert.NotNull(tracker.StuckReason);

        tracker.RecordOther();

        Assert.Equal(0, tracker.Streak);
        Assert.Null(tracker.StuckReason);
        Assert.False(tracker.RecordUnavailable(null));
    }

    [Fact]
    public void A_poll_backoff_slows_the_poll_but_leaves_accepted_work_free_to_run()
    {
        DateTimeOffset now = new(2026, 8, 22, 0, 0, 0, TimeSpan.Zero);
        var backoff = new CtDegradationBackoff(
            clock: () => now,
            jitter: () => 0.5,
            baseDelay: TimeSpan.FromSeconds(10));

        backoff.RecordPollDegraded();

        Assert.False(backoff.CanPoll);
        Assert.True(backoff.CanEnqueue, "a stuck poll must not block work accepted at a readable base");

        now = now.AddSeconds(30);
        Assert.True(backoff.CanPoll);

        backoff.RecordHealthy();
        Assert.True(backoff.CanPoll);
        Assert.True(backoff.CanEnqueue);
    }

    [Fact]
    public async Task A_sticky_unavailable_delta_stops_the_four_hertz_loop_and_names_the_reason()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-sticky-").FullName;
        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot>? run = null;
        try
        {
            var source = new ScriptedRevisionSource();
            source.Observations.Enqueue(Observation(2));
            source.Observations.Enqueue(Observation(3));
            var impact = new ScriptedImpactSource
            {
                Result = new ContinuousTestImpactResult(EngineTestSupport.WorkspaceId, [], [], [])
                {
                    Outcome = ContinuousTestImpactOutcome.Unavailable,
                    Reason = "moving_cursor",
                },
            };
            var enqueuer = new RecordingEnqueuer();
            var reasons = new List<string>();
            var statusPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            run = ContinuousTestDaemonHost.RunAsync(
                root,
                new ContinuousTestDaemonHostOptions
                {
                    Enabled = true,
                    AcquireLease = false,
                    WorkspaceId = EngineTestSupport.WorkspaceId,
                    Enqueuer = enqueuer,
                    Poller = new ContinuousTestRevisionPoller(source, impact),
                    PollInterval = TimeSpan.FromMilliseconds(1),
                    StatusWriter = (_, reason) =>
                    {
                        lock (reasons) reasons.Add(reason);
                        if (reason.Contains("impact unavailable", StringComparison.Ordinal)
                            && reason.Contains("moving_cursor", StringComparison.Ordinal))
                            statusPublished.TrySetResult();
                    },
                },
                cts.Token);

            await statusPublished.Task.WaitAsync(
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);

            Assert.Empty(enqueuer.Changes);
            Assert.InRange(
                source.RefreshCount,
                2,
                (CtUnavailableDeltaTracker.DefaultLimit + 2) * 3);

            string[] published;
            lock (reasons)
                published = [.. reasons];
            Assert.Contains(
                published,
                reason => reason.Contains("impact unavailable", StringComparison.Ordinal)
                    && reason.Contains("moving_cursor", StringComparison.Ordinal));
        }
        finally
        {
            await cts.CancelAsync();
            if (run is not null)
            {
                try { await run; } catch (OperationCanceledException) { }
            }

            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_stuck_poll_publishes_the_pause_in_the_status_record_and_clears_on_recovery()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-pause-").FullName;
        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot>? run = null;
        try
        {
            var source = new ScriptedRevisionSource();
            source.Observations.Enqueue(Observation(2));
            source.Observations.Enqueue(Observation(3));
            var impact = new ScriptedImpactSource
            {
                Result = new ContinuousTestImpactResult(EngineTestSupport.WorkspaceId, [], [], [])
                {
                    Outcome = ContinuousTestImpactOutcome.Unavailable,
                    Reason = "moving_cursor",
                },
            };
            var diagnostics = new List<string>();
            run = ContinuousTestDaemonHost.RunAsync(
                root,
                new ContinuousTestDaemonHostOptions
                {
                    Enabled = true,
                    WorkspaceId = EngineTestSupport.WorkspaceId,
                    Enqueuer = new RecordingEnqueuer(),
                    Poller = new ContinuousTestRevisionPoller(source, impact),
                    PollInterval = TimeSpan.FromMilliseconds(1),
                    Diagnostic = line => { lock (diagnostics) diagnostics.Add(line); },
                },
                cts.Token);

            CtDaemonStatusRecord paused = await WaitForRecordAsync(root, record => record.AutoRunsPaused);
            Assert.Equal("impact unavailable (moving_cursor)", paused.PauseReason);
            Assert.Contains("impact unavailable", paused.Reason, StringComparison.Ordinal);

            impact.Result = new ContinuousTestImpactResult(EngineTestSupport.WorkspaceId, [], [], [])
            {
                Outcome = ContinuousTestImpactOutcome.Empty,
                FromRevision = 2,
                ToRevision = 3,
            };
            CtDaemonStatusRecord resumed = await WaitForRecordAsync(root, record => !record.AutoRunsPaused);
            Assert.Null(resumed.PauseReason);

            string[] lines;
            lock (diagnostics)
                lines = [.. diagnostics];
            Assert.Single(
                lines,
                line => line.Contains("auto-runs paused", StringComparison.Ordinal)
                    && line.Contains("moving_cursor", StringComparison.Ordinal));
            Assert.Single(lines, line => line.Contains("auto-runs resumed", StringComparison.Ordinal));
        }
        finally
        {
            await cts.CancelAsync();
            if (run is not null)
            {
                try { await run; } catch (OperationCanceledException) { }
            }

            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void An_old_status_record_without_the_pause_fields_reads_not_paused()
    {
        const string json =
            """
            {"state":"running","reason":"idle","identity":null,
             "updated_at_utc":"2026-08-26T00:00:00.0000000+00:00"}
            """;

        CtDaemonStatusRecord? record = JsonSerializer.Deserialize(
            json, CtDaemonJsonContext.Default.CtDaemonStatusRecord);

        Assert.NotNull(record);
        Assert.False(record.AutoRunsPaused);
        Assert.Null(record.PauseReason);
    }

    [Fact]
    public async Task A_truncated_impact_read_enqueues_and_never_feeds_the_pause()
    {
        using ResolutionArtifactFixture fixture = ResolutionArtifactFixture.Create();
        fixture.AddFile("file-service", "src/Service.cs");
        fixture.AddSymbol("file-service", "cls-service", "Service", "class", "src/Service.cs");
        string root = CreateWorkspaceRoot(fixture, out string dbPath);
        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot>? run = null;
        try
        {
            Execute(
                dbPath,
                """
                CREATE TABLE revision_file_changes (path TEXT, revision_id INTEGER, change_kind TEXT);
                INSERT INTO revision_file_changes VALUES ('src/Service.cs', 2, 'updated');
                INSERT INTO extraction_revisions VALUES (2);
                """);
            var workspace = EngineTestSupport.Workspace(root);
            var source = new ScriptedRevisionSource();
            source.Observations.Enqueue(FixtureObservation(1));
            source.Observations.Enqueue(FixtureObservation(2));
            var facts = new FakeCtFactSource();
            facts.Inner.Symbols.Add(FakeMillerFactSource.Symbol("sym:service", "Service", "src/Service.cs"));
            facts.Inner.ImpactTruncatedByLimit = true;
            var enqueuer = new RecordingEnqueuer();
            var reasons = new List<string>();
            run = ContinuousTestDaemonHost.RunAsync(
                root,
                new ContinuousTestDaemonHostOptions
                {
                    Enabled = true,
                    AcquireLease = false,
                    WorkspaceId = EngineTestSupport.WorkspaceId,
                    Enqueuer = enqueuer,
                    Poller = new ContinuousTestRevisionPoller(
                        source,
                        new MillerFactImpactSource(_ => facts)),
                    PollInterval = TimeSpan.FromMilliseconds(1),
                    Projects =
                    [
                        new ContinuousTestProject("proj:1", EngineTestSupport.WorkspaceId, workspace.ProjectPath),
                    ],
                    StatusWriter = (_, reason) => { lock (reasons) reasons.Add(reason); },
                },
                cts.Token);

            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while ((enqueuer.Changes.Count == 0
                    || source.RefreshCount < CtUnavailableDeltaTracker.DefaultLimit + 2)
                && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }

            ContinuousTestDaemonChange change = Assert.Single(enqueuer.Changes);
            Assert.Equal(ContinuousTestDeltaCompleteness.Complete, change.DeltaCompleteness);
            Assert.Equal(["src/Service.cs"], change.ChangedPaths);
            Assert.True(source.RefreshCount >= CtUnavailableDeltaTracker.DefaultLimit + 2);
            string[] published;
            lock (reasons)
                published = [.. reasons];
            Assert.DoesNotContain(
                published,
                reason => reason.Contains("impact unavailable", StringComparison.Ordinal));
        }
        finally
        {
            await cts.CancelAsync();
            if (run is not null)
            {
                try { await run; } catch (OperationCanceledException) { }
            }

            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<CtDaemonStatusRecord> WaitForRecordAsync(
        string root, Func<CtDaemonStatusRecord, bool> accept)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (CtDaemonLease.TryReadStatus(root) is { } record && accept(record))
                return record;
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException(
            "the status record did not reach the expected pause state; last: "
            + (CtDaemonLease.TryReadStatus(root) is { } last ? CtDaemonJson.Serialize(last) : "none"));
    }

    private static ContinuousTestRevisionObservation Observation(long revision) =>
        new(
            EngineTestSupport.WorkspaceId,
            new CtFreshnessKey(EngineTestSupport.Identity, revision),
            true,
            "fresh",
            DateTimeOffset.UtcNow);

    private static ContinuousTestRevisionObservation FixtureObservation(long revision) =>
        new(
            EngineTestSupport.WorkspaceId,
            new CtFreshnessKey(FixtureIdentity, revision),
            true,
            "fresh",
            DateTimeOffset.UtcNow);

    private static string CreateWorkspaceRoot(ResolutionArtifactFixture fixture, out string dbPath)
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-sticky-trunc-").FullName;
        string millerDir = Path.Combine(root, ".miller");
        Directory.CreateDirectory(millerDir);
        dbPath = Path.Combine(millerDir, "symbols.db");
        SqliteConnection.ClearAllPools();
        File.Copy(fixture.DbPath, dbPath);
        return root;
    }

    private static void Execute(string dbPath, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
