using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing;

/// <summary>
/// The ONE live status projection: the selected key comes from the live index cursor, never from
/// stored <c>ct.db</c> rows. The old <c>TestsCore.SelectedFrom</c> derived the key from the rows it
/// was judging, so uniformly stale rows read green forever and consecutive reads flipped keys
/// (observed live: rev 32424 in one read, 32161 in the next).
/// </summary>
public sealed class ContinuousTestStatusProjectionTests
{
    private static readonly CtFreshnessKey OldKey = new("ctgen1:store:fam:view:gen-1", 41);
    private static readonly CtFreshnessKey LiveKey = new("ctgen1:store:fam:view:gen-1", 58);

    [Fact]
    public void Rows_committed_at_an_old_key_with_a_newer_live_cursor_are_stale_never_green()
    {
        var statuses = new[]
        {
            Row("test:a", ContinuousTestState.Green, OldKey),
            Row("test:b", ContinuousTestState.Green, OldKey),
        };

        ContinuousTestProjectedStatus projected =
            ContinuousTestStatusProjection.Project(LiveKey, statuses);

        Assert.Equal(ContinuousTestVerdict.Partial, projected.Verdict);
        Assert.Equal(LiveKey, projected.SelectedKey);
        Assert.Equal(2, projected.StaleCount);
    }

    [Fact]
    public void Rows_committed_at_the_live_key_are_green_with_zero_stale()
    {
        var statuses = new[]
        {
            Row("test:a", ContinuousTestState.Green, LiveKey),
            Row("test:b", ContinuousTestState.Skipped, LiveKey),
        };

        ContinuousTestProjectedStatus projected =
            ContinuousTestStatusProjection.Project(LiveKey, statuses);

        Assert.Equal(ContinuousTestVerdict.Green, projected.Verdict);
        Assert.Equal(0, projected.StaleCount);
    }

    [Fact]
    public void No_live_cursor_is_unknown_with_no_key_even_when_every_row_is_green()
    {
        var statuses = new[] { Row("test:a", ContinuousTestState.Green, OldKey) };

        ContinuousTestProjectedStatus projected =
            ContinuousTestStatusProjection.Project(liveKey: null, statuses);

        Assert.Equal(ContinuousTestVerdict.Unknown, projected.Verdict);
        Assert.Null(projected.SelectedKey);
    }

    [Fact]
    public void No_live_cursor_reports_the_stored_stale_row_count()
    {
        var statuses = new[]
        {
            Row("test:a", ContinuousTestState.Stale, OldKey),
            Row("test:b", ContinuousTestState.Green, OldKey),
        };

        ContinuousTestProjectedStatus projected =
            ContinuousTestStatusProjection.Project(liveKey: null, statuses);

        Assert.Equal(1, projected.StaleCount);
    }

    [Fact]
    public void The_same_revision_under_a_different_generation_identity_is_stale()
    {
        var rebuilt = new CtFreshnessKey("ctgen1:store:fam:view:gen-2", OldKey.Revision);
        var statuses = new[] { Row("test:a", ContinuousTestState.Green, OldKey) };

        ContinuousTestProjectedStatus projected =
            ContinuousTestStatusProjection.Project(rebuilt, statuses);

        Assert.Equal(ContinuousTestVerdict.Partial, projected.Verdict);
        Assert.Equal(1, projected.StaleCount);
    }

    [Fact]
    public void A_red_row_at_the_live_key_is_red()
    {
        var statuses = new[]
        {
            Row("test:a", ContinuousTestState.Green, LiveKey),
            Row("test:b", ContinuousTestState.Red, LiveKey),
        };

        ContinuousTestProjectedStatus projected =
            ContinuousTestStatusProjection.Project(LiveKey, statuses);

        Assert.Equal(ContinuousTestVerdict.Red, projected.Verdict);
    }

    [Fact]
    public void A_running_or_unknown_row_makes_the_verdict_unknown()
    {
        var running = new[]
        {
            Row("test:a", ContinuousTestState.Green, LiveKey),
            Row("test:b", ContinuousTestState.Running, LiveKey),
        };

        Assert.Equal(
            ContinuousTestVerdict.Unknown,
            ContinuousTestStatusProjection.Project(LiveKey, running).Verdict);
    }

    [Fact]
    public void An_unhealthy_watch_or_an_empty_row_set_is_unknown()
    {
        var statuses = new[] { Row("test:a", ContinuousTestState.Green, LiveKey) };

        Assert.Equal(
            ContinuousTestVerdict.Unknown,
            ContinuousTestStatusProjection.Project(LiveKey, statuses, watchHealthy: false).Verdict);
        Assert.Equal(
            ContinuousTestVerdict.Unknown,
            ContinuousTestStatusProjection.Project(LiveKey, []).Verdict);
    }

    [Fact]
    public void A_green_row_rides_a_watermark_that_covers_the_live_key()
    {
        var statuses = new[] { Row("test:a", ContinuousTestState.Green, OldKey) };
        var watermarks = new Dictionary<string, CtFreshnessKey>
        {
            ["test:a"] = LiveKey,
        };

        ContinuousTestProjectedStatus projected =
            ContinuousTestStatusProjection.Project(LiveKey, statuses, watermarks);

        Assert.Equal(ContinuousTestVerdict.Green, projected.Verdict);
        Assert.Equal(0, projected.StaleCount);
    }

    [Fact]
    public void Only_green_rows_ride_the_watermark_a_red_stays_where_it_ran()
    {
        var statuses = new[] { Row("test:a", ContinuousTestState.Red, OldKey) };
        var watermarks = new Dictionary<string, CtFreshnessKey>
        {
            ["test:a"] = LiveKey,
        };

        ContinuousTestProjectedStatus projected =
            ContinuousTestStatusProjection.Project(LiveKey, statuses, watermarks);

        Assert.Equal(ContinuousTestVerdict.Partial, projected.Verdict);
        Assert.Equal(1, projected.StaleCount);
    }

    [Fact]
    public void A_watermark_on_a_different_generation_identity_rescues_nothing()
    {
        var statuses = new[] { Row("test:a", ContinuousTestState.Green, OldKey) };
        var watermarks = new Dictionary<string, CtFreshnessKey>
        {
            ["test:a"] = new CtFreshnessKey("ctgen1:store:fam:view:gen-2", 99),
        };

        Assert.Equal(
            ContinuousTestVerdict.Partial,
            ContinuousTestStatusProjection.Project(LiveKey, statuses, watermarks).Verdict);
    }

    private static ContinuousTestStatus Row(string id, ContinuousTestState state, CtFreshnessKey key) =>
        new(
            WorkspaceId: "ws:1",
            TestCaseId: id,
            State: state,
            IndexIdentity: key.IndexIdentity,
            Revision: key.Revision,
            ProvenFreshKey: state is ContinuousTestState.Green
                or ContinuousTestState.Red
                or ContinuousTestState.Skipped
                ? key
                : null);
}
