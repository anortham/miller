using Miller.Indexing;
using Miller.Server.Hosting;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the freshness poll-then-swap decision (the testable seam of <c>FreshnessService</c>): on each poll, if
/// the latest persisted revision EXCEEDS the held index's built revision, rebuild a new index and atomically
/// swap it (publishing the new revision); otherwise do nothing. No SQLite, no timer, no subprocess — the
/// "latest revision" is a supplied long and the rebuild is an injected factory whose invocations are counted.
/// Covers: a bump triggers exactly one rebuild+swap to the new revision, an equal revision is a no-op, an
/// older revision (defensive) is a no-op, and a second poll at the same revision does NOT rebuild again
/// (idempotent — no churn while the writer is idle).
/// </summary>
public sealed class FreshnessPollerTests
{
    private static MillerRepositoryIndex BuildIndex(JulieDbFixture fx) =>
        MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath));

    [Fact]
    public void PollOnce_LatestExceedsBuilt_RebuildsAndSwapsToNewRevision()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var initial = BuildIndex(fx);
        var rebuilt = BuildIndex(fx);
        var holder = new IndexHolder(initial, builtRevision: 1);

        int rebuilds = 0;
        bool swapped = FreshnessPoller.PollOnce(holder, latestRevision: 2, rebuild: () =>
        {
            rebuilds++;
            return rebuilt;
        });

        Assert.True(swapped);
        Assert.Equal(1, rebuilds);
        Assert.Same(rebuilt, holder.Current);
        Assert.Equal(2, holder.BuiltRevision);
    }

    [Fact]
    public void PollOnce_EqualRevision_DoesNotRebuild()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var initial = BuildIndex(fx);
        var holder = new IndexHolder(initial, builtRevision: 5);

        int rebuilds = 0;
        bool swapped = FreshnessPoller.PollOnce(holder, latestRevision: 5, rebuild: () =>
        {
            rebuilds++;
            return BuildIndex(fx);
        });

        Assert.False(swapped);
        Assert.Equal(0, rebuilds);
        Assert.Same(initial, holder.Current);
        Assert.Equal(5, holder.BuiltRevision);
    }

    [Fact]
    public void PollOnce_OlderRevision_DoesNotRebuild()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var initial = BuildIndex(fx);
        var holder = new IndexHolder(initial, builtRevision: 9);

        int rebuilds = 0;
        bool swapped = FreshnessPoller.PollOnce(holder, latestRevision: 4, rebuild: () =>
        {
            rebuilds++;
            return BuildIndex(fx);
        });

        Assert.False(swapped);
        Assert.Equal(0, rebuilds);
        Assert.Same(initial, holder.Current);
    }

    [Fact]
    public void PollOnce_SecondPollAtSameRevision_DoesNotRebuildAgain()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var holder = new IndexHolder(BuildIndex(fx), builtRevision: 1);

        int rebuilds = 0;
        Func<MillerRepositoryIndex> rebuild = () => { rebuilds++; return BuildIndex(fx); };

        Assert.True(FreshnessPoller.PollOnce(holder, latestRevision: 2, rebuild));  // first: bump -> rebuild
        Assert.False(FreshnessPoller.PollOnce(holder, latestRevision: 2, rebuild)); // second: same -> no churn

        Assert.Equal(1, rebuilds);
        Assert.Equal(2, holder.BuiltRevision);
    }

    [Fact]
    public void PollOnce_NullHolder_Throws()
    {
        using var fx = JulieDbFixture.CreateDefault();
        Assert.Throws<ArgumentNullException>(() =>
            FreshnessPoller.PollOnce(null!, latestRevision: 1, rebuild: () => BuildIndex(fx)));
    }

    [Fact]
    public void PollOnce_NullRebuild_Throws()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var holder = new IndexHolder(BuildIndex(fx), builtRevision: 1);
        Assert.Throws<ArgumentNullException>(() =>
            FreshnessPoller.PollOnce(holder, latestRevision: 2, rebuild: null!));
    }
}
