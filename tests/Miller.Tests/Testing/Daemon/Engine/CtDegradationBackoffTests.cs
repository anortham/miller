using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.Engine;

public sealed class CtDegradationBackoffTests
{
    [Fact]
    public void Degraded_index_is_not_ready_to_enqueue()
    {
        var backoff = new CtDegradationBackoff(
            clock: () => new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero),
            jitter: () => 0.0);
        Assert.True(backoff.CanPoll);
        backoff.RecordDegraded();
        Assert.False(backoff.CanPoll);
        Assert.False(backoff.CanEnqueue);
    }

    [Fact]
    public void Recovered_index_clears_backoff()
    {
        DateTimeOffset now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        var backoff = new CtDegradationBackoff(clock: () => now, jitter: () => 0.0);
        backoff.RecordDegraded();
        backoff.RecordHealthy();
        Assert.True(backoff.CanPoll);
        Assert.True(backoff.CanEnqueue);
    }

    [Fact]
    public void Jittered_delay_elapses_before_the_next_poll()
    {
        DateTimeOffset now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        var backoff = new CtDegradationBackoff(
            clock: () => now,
            jitter: () => 0.5,
            baseDelay: TimeSpan.FromSeconds(4));
        backoff.RecordDegraded();
        Assert.False(backoff.CanPoll);
        now = now.AddSeconds(2);
        Assert.False(backoff.CanPoll);
        now = now.AddSeconds(2);
        Assert.True(backoff.CanPoll);
    }
}
