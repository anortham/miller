using Miller.Server.Workspaces;
using Xunit;

namespace Miller.Tests.Server;

public sealed class BackgroundRefreshGateTests
{
    [Fact]
    public void TryEnter_WhileARefreshIsInFlight_AdmitsOnlyTheFirstCaller()
    {
        var gate = new BackgroundRefreshGate(TimeSpan.Zero);

        Assert.True(gate.TryEnter("ws-a"));
        for (int i = 0; i < 9; i++)
            Assert.False(gate.TryEnter("ws-a"));
    }

    [Fact]
    public void TryEnter_AfterRelease_AdmitsTheNextCallerWhenTheCooldownIsZero()
    {
        var gate = new BackgroundRefreshGate(TimeSpan.Zero);

        Assert.True(gate.TryEnter("ws-a"));
        gate.Release("ws-a");

        Assert.True(gate.TryEnter("ws-a"));
    }

    [Fact]
    public void TryEnter_WithinTheCooldownAfterRelease_IsRefused()
    {
        long now = 1_000;
        var gate = new BackgroundRefreshGate(TimeSpan.FromSeconds(5), () => now);

        Assert.True(gate.TryEnter("ws-a"));
        gate.Release("ws-a");

        // One tool call resolves several read contexts in a row (SearchTool resolves up to three). With the real
        // thread pool an early refresh can finish between two of them, so the in-flight bit alone lets the SAME
        // call start a second scan. The cooldown is what closes that.
        now += 4_999;
        Assert.False(gate.TryEnter("ws-a"));
    }

    [Fact]
    public void TryEnter_AfterTheCooldownElapses_IsAdmitted()
    {
        long now = 1_000;
        var gate = new BackgroundRefreshGate(TimeSpan.FromSeconds(5), () => now);

        Assert.True(gate.TryEnter("ws-a"));
        gate.Release("ws-a");
        now += 5_000;

        Assert.True(gate.TryEnter("ws-a"));
    }

    [Fact]
    public void Cooldown_StartsAtReleaseNotAtEntry()
    {
        long now = 1_000;
        var gate = new BackgroundRefreshGate(TimeSpan.FromSeconds(5), () => now);

        Assert.True(gate.TryEnter("ws-a"));

        // A long scan holds the in-flight bit the whole time; the cooldown must not expire underneath it.
        now += 60_000;
        Assert.False(gate.TryEnter("ws-a"));

        gate.Release("ws-a");
        Assert.False(gate.TryEnter("ws-a"));
    }

    [Fact]
    public void TryEnter_IsPerWorkspace()
    {
        long now = 1_000;
        var gate = new BackgroundRefreshGate(TimeSpan.FromSeconds(5), () => now);

        Assert.True(gate.TryEnter("ws-a"));
        Assert.True(gate.TryEnter("ws-b"));

        gate.Release("ws-a");
        Assert.False(gate.TryEnter("ws-a"));
        Assert.False(gate.TryEnter("ws-b"));
    }

    [Fact]
    public void DefaultCooldown_IsPositive()
    {
        // A zero default would restore the hole the cooldown exists to close, so pin it as non-zero.
        Assert.True(BackgroundRefreshGate.DefaultCooldown > TimeSpan.Zero);
    }

    [Fact]
    public void Constructor_RejectsANegativeCooldown()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackgroundRefreshGate(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Release_AnUnknownWorkspace_StartsItsCooldownRatherThanThrowing()
    {
        long now = 1_000;
        var gate = new BackgroundRefreshGate(TimeSpan.FromSeconds(5), () => now);

        gate.Release("ws-a");

        Assert.False(gate.TryEnter("ws-a"));
    }
}
