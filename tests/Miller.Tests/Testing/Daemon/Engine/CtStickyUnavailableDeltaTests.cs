using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.Engine;

/// <summary>
/// An <c>unavailable_delta</c> poll is not the string <c>degraded</c>, so the loop used to record it
/// as a HEALTHY poll. The condition is sticky by design — the poller must not absorb an interval it
/// could not read — so one truncating edit turned the daemon into a 250 ms poll loop that enqueued
/// nothing and reported itself as healthy. These tests hold the bounded, reported behaviour.
/// </summary>
public sealed class CtStickyUnavailableDeltaTests
{
    [Fact]
    public void One_unavailable_answer_is_tolerated()
    {
        var tracker = new CtUnavailableDeltaTracker(limit: 3);
        Assert.False(tracker.RecordUnavailable("impact_truncated"));
        Assert.Equal(1, tracker.Streak);
        Assert.Null(tracker.StuckReason);
    }

    [Fact]
    public void The_limit_of_consecutive_unavailable_answers_reports_stuck_with_the_delta_reason()
    {
        var tracker = new CtUnavailableDeltaTracker(limit: 3);
        Assert.False(tracker.RecordUnavailable("impact_truncated"));
        Assert.False(tracker.RecordUnavailable("impact_truncated"));
        Assert.True(tracker.RecordUnavailable("impact_truncated"));
        Assert.True(tracker.RecordUnavailable("impact_truncated"));

        Assert.NotNull(tracker.StuckReason);
        Assert.Contains("impact unavailable", tracker.StuckReason, StringComparison.Ordinal);
        Assert.Contains("impact_truncated", tracker.StuckReason, StringComparison.Ordinal);
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
                    Reason = "impact_truncated",
                },
            };
            var enqueuer = new RecordingEnqueuer();
            var reasons = new List<string>();
            using var cts = new CancellationTokenSource();
            Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
                root,
                new ContinuousTestDaemonHostOptions
                {
                    Enabled = true,
                    AcquireLease = false,
                    WorkspaceId = EngineTestSupport.WorkspaceId,
                    Enqueuer = enqueuer,
                    Poller = new ContinuousTestRevisionPoller(source, impact),
                    PollInterval = TimeSpan.FromMilliseconds(1),
                    StatusWriter = (_, reason) => { lock (reasons) reasons.Add(reason); },
                },
                cts.Token);

            // Long enough that an unbacked-off loop polls many times over the limit — the tolerated
            // streak is two seconds of production ticks, and the backoff that follows is five.
            await Task.Delay(1500, TestContext.Current.CancellationToken);
            await cts.CancelAsync();
            try { await run; } catch (OperationCanceledException) { }

            Assert.Empty(enqueuer.Changes);
            Assert.InRange(
                source.RefreshCount,
                2,
                CtUnavailableDeltaTracker.DefaultLimit + 4);

            string[] published;
            lock (reasons)
                published = [.. reasons];
            Assert.Contains(
                published,
                reason => reason.Contains("impact unavailable", StringComparison.Ordinal)
                    && reason.Contains("impact_truncated", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static ContinuousTestRevisionObservation Observation(long revision) =>
        new(
            EngineTestSupport.WorkspaceId,
            new CtFreshnessKey(EngineTestSupport.Identity, revision),
            true,
            "fresh",
            DateTimeOffset.UtcNow);
}
