using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the anti-flap cooldown state machine (version-aware leadership D4): after abdicating to a
/// newer-extractor challenger, claims are suppressed while &lt;60s elapsed AND the challenger pid is alive;
/// either expiry or challenger death resumes claims permanently (until the next yield). Pure — injected clock
/// and alive probe, no processes, no timers.
/// </summary>
public sealed class YieldCooldownTests
{
    [Fact]
    public void SuppressesClaim_WhileRequesterAliveAndWithinDuration()
    {
        var now = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        var cooldown = new YieldCooldown(() => now, _ => true);

        cooldown.Begin(4242);

        Assert.True(cooldown.SuppressesClaim());
        now += TimeSpan.FromSeconds(59);
        Assert.True(cooldown.SuppressesClaim()); // still inside the window, challenger alive
    }

    [Fact]
    public void SuppressesClaim_AfterDurationElapsed_ResumesClaims()
    {
        var now = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        var cooldown = new YieldCooldown(() => now, _ => true);

        cooldown.Begin(4242);
        now += YieldCooldown.Duration;

        Assert.False(cooldown.SuppressesClaim());
        // The cooldown stays cleared: a later probe must not re-arm it even if the challenger is still alive.
        Assert.False(cooldown.SuppressesClaim());
    }

    [Fact]
    public void SuppressesClaim_WhenRequesterDies_ResumesClaimsBeforeExpiry()
    {
        var now = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        bool alive = true;
        var cooldown = new YieldCooldown(() => now, _ => alive);

        cooldown.Begin(4242);
        Assert.True(cooldown.SuppressesClaim());

        alive = false; // the challenger died without claiming; the abdicator must not freeze the workspace
        Assert.False(cooldown.SuppressesClaim());

        alive = true; // resurrection (pid reuse) must NOT re-arm a cleared cooldown
        Assert.False(cooldown.SuppressesClaim());
    }

    [Fact]
    public void SuppressesClaim_ProbesTheRecordedRequesterPid()
    {
        var now = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        int probedPid = 0;
        var cooldown = new YieldCooldown(() => now, pid =>
        {
            probedPid = pid;
            return true;
        });

        cooldown.Begin(777);
        Assert.True(cooldown.SuppressesClaim());
        Assert.Equal(777, probedPid);
    }

    [Fact]
    public void Begin_WithNonPositivePid_ArmsNothing()
    {
        var now = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        var cooldown = new YieldCooldown(() => now, _ => true);

        cooldown.Begin(0);
        Assert.False(cooldown.SuppressesClaim());

        // A bad Begin also clears any earlier active cooldown rather than leaving stale state behind.
        cooldown.Begin(4242);
        Assert.True(cooldown.SuppressesClaim());
        cooldown.Begin(-1);
        Assert.False(cooldown.SuppressesClaim());
    }

    [Fact]
    public void Begin_RestartsTheWindow()
    {
        var now = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        var cooldown = new YieldCooldown(() => now, _ => true);

        cooldown.Begin(4242);
        now += TimeSpan.FromSeconds(45);
        cooldown.Begin(4343); // a second yield re-arms a fresh 60s window toward the new challenger

        now += TimeSpan.FromSeconds(30); // 75s after the first Begin, 30s after the second
        Assert.True(cooldown.SuppressesClaim());
    }

    [Fact]
    public void SuppressesClaim_NeverActiveBeforeBegin()
    {
        var cooldown = new YieldCooldown(() => DateTimeOffset.UtcNow, _ => true);
        Assert.False(cooldown.SuppressesClaim());
    }
}
