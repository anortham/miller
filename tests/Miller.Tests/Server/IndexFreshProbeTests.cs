using Miller.Indexing;
using Miller.Server.Hosting;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins <see cref="IndexFreshProbe"/> — the singleton the telemetry filter reads to populate <c>index_fresh</c>
/// (decision-8). It combines the held index's built revision against the latest persisted revision AND the
/// indexer's queue-empty state: both must agree for "fresh". A transient revision-read failure yields null
/// ("not measured"), never a fabricated value. No SQLite/timer — the latest-revision and queue-empty inputs are
/// injected suppliers so the truth table is asserted directly.
/// </summary>
public sealed class IndexFreshProbeTests
{
    private static IndexHolder Holder(long builtRevision)
    {
        using var fx = JulieDbFixture.CreateDefault();
        return new IndexHolder(MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath)), builtRevision);
    }

    [Fact]
    public void Compute_BuiltEqualsLatest_AndQueueEmpty_IsFresh()
    {
        var probe = new IndexFreshProbe(Holder(5), latestRevision: () => 5, queueEmpty: () => true);
        Assert.Equal(true, probe.Compute());
    }

    [Fact]
    public void Compute_WriterAhead_IsNotFresh()
    {
        var probe = new IndexFreshProbe(Holder(5), latestRevision: () => 6, queueEmpty: () => true);
        Assert.Equal(false, probe.Compute());
    }

    [Fact]
    public void Compute_EqualRevisionButPendingEvents_IsNotFresh()
    {
        var probe = new IndexFreshProbe(Holder(5), latestRevision: () => 5, queueEmpty: () => false);
        Assert.Equal(false, probe.Compute());
    }

    [Fact]
    public void Compute_RevisionReadThrows_ReturnsNull_NotAFabricatedValue()
    {
        var probe = new IndexFreshProbe(
            Holder(5),
            latestRevision: () => throw new InvalidOperationException("db hiccup"),
            queueEmpty: () => true);
        Assert.Null(probe.Compute());
    }

    [Fact]
    public void Ctor_NullArguments_Throw()
    {
        var holder = Holder(1);
        Assert.Throws<ArgumentNullException>(() => new IndexFreshProbe(null!, () => 1, () => true));
        Assert.Throws<ArgumentNullException>(() => new IndexFreshProbe(holder, null!, () => true));
        Assert.Throws<ArgumentNullException>(() => new IndexFreshProbe(holder, () => 1, null!));
    }
}
