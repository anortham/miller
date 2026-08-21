using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.ControlPlane;

/// <summary>
/// <c>daemon.lease.json</c> has always recorded <c>miller_version</c> and nothing ever read it. After
/// an upgrade the old daemon kept running old code, <c>tests status</c> called it healthy, and
/// <c>tests start</c> answered exit 0 without starting anything.
///
/// <para>Two rules carry the verdict. SAMENESS compares the whole build string, so an agent swarm on
/// one build never warns and never contends. DIRECTION compares <c>major.minor.patch</c> numerically,
/// because version strings are not orderable as text — <c>"1.9.0"</c> sorts above <c>"1.13.0"</c>, and
/// a text comparison would call a newer daemon older and authorize a kill.</para>
/// </summary>
public sealed class CtDaemonVersionTests
{
    [Theory]
    // No build recorded: a lease written before the field existed, or by a build that could not
    // resolve its own version. Unproven direction never authorizes stopping another process.
    [InlineData("1.13.0+abc", null, CtDaemonVersionMatch.Unknown, false, false)]
    [InlineData("1.13.0+abc", "", CtDaemonVersionMatch.Unknown, false, false)]
    // One build across concurrent agents. This is the CT half of "equal versions never thrash".
    [InlineData("1.13.0+abc", "1.13.0+abc", CtDaemonVersionMatch.Same, false, false)]
    // The ordering case: text comparison puts "1.13.0" BELOW "1.9.0" and would authorize a kill.
    [InlineData("1.9.0+abc", "1.13.0+def", CtDaemonVersionMatch.DaemonNewer, true, false)]
    [InlineData("1.13.0+abc", "1.9.0+def", CtDaemonVersionMatch.DaemonOlder, true, true)]
    // The dogfood case: same release, rebuilt from a different commit.
    [InlineData("1.13.0+abc", "1.13.0+def", CtDaemonVersionMatch.BuildDiffers, true, true)]
    // Unorderable: report the mismatch, refuse the replace.
    [InlineData("1.13.0+abc", "nightly", CtDaemonVersionMatch.Unknown, true, false)]
    public void The_verdict_reports_the_mismatch_and_gates_the_replace(
        string ownVersion,
        string? daemonVersion,
        CtDaemonVersionMatch expectedMatch,
        bool expectedMismatch,
        bool expectedMayReplace)
    {
        CtDaemonVersionVerdict verdict = CtDaemonVersion.Evaluate(ownVersion, daemonVersion);

        Assert.Equal(expectedMatch, verdict.Match);
        Assert.Equal(expectedMismatch, verdict.Mismatch);
        Assert.Equal(expectedMayReplace, verdict.MayReplace);
        Assert.Equal(ownVersion, verdict.OwnVersion);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Reason), "every verdict must explain itself");
    }

    /// <summary>A mismatch reason must name BOTH builds, because the reader acts on the difference.</summary>
    [Fact]
    public void A_mismatch_reason_names_both_builds()
    {
        CtDaemonVersionVerdict verdict = CtDaemonVersion.Evaluate("1.13.0+bbb", "1.9.0+aaa");

        Assert.Contains("1.9.0+aaa", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("1.13.0+bbb", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_lease_reports_no_live_daemon()
    {
        CtDaemonVersionVerdict verdict = CtDaemonVersion.ForLease("1.13.0+abc", liveLease: null);

        Assert.Equal(CtDaemonVersionMatch.None, verdict.Match);
        Assert.Null(verdict.DaemonVersion);
        Assert.False(verdict.Mismatch);
        Assert.False(verdict.MayReplace);
        Assert.Equal("no live daemon", verdict.Reason);
    }

    [Fact]
    public void A_live_lease_is_compared_by_the_build_it_records()
    {
        var lease = new CtDaemonLeaseRecord(
            new CtDaemonLeaseIdentity(1234, DateTimeOffset.UnixEpoch),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            "C:/does/not/matter",
            "1.9.0+aaa");

        CtDaemonVersionVerdict verdict = CtDaemonVersion.ForLease("1.13.0+bbb", lease);

        Assert.Equal(CtDaemonVersionMatch.DaemonOlder, verdict.Match);
        Assert.Equal("1.9.0+aaa", verdict.DaemonVersion);
        Assert.True(verdict.MayReplace);
    }
}
