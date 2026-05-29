using Miller.Core.Freshness;
using Miller.Server.Hosting;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the M6 freshness gate (m6-design Components/3, impl-order step 6): SHA256 the indexed snapshot
/// (<c>ReadIndexedFileText</c>) against the current disk text and run it through
/// <see cref="Miller.Core.Freshness.StalenessCheck"/>. Indexed==disk → Fresh; differ → Stale; missing indexed
/// content → Stale (can't verify) unless <c>allow_stale</c>. Driven against the synthesized
/// <see cref="JulieDbFixture.CreateForEdit"/> (no julie-server binary) — fast suite.
/// </summary>
public sealed class FreshnessGateTests
{
    [Fact]
    public void Check_IndexedTextEqualsDiskText_IsFresh()
    {
        using var fx = JulieDbFixture.CreateForEdit();

        // Disk text byte-identical to the indexed snapshot → Fresh.
        var result = FreshnessGate.Check(fx.DbPath, "orders/OrderService.cs", JulieDbFixture.OrderServiceContent);

        Assert.Equal(FreshnessResult.Fresh, result.Result);
        Assert.True(result.IndexedContentFound);
    }

    [Fact]
    public void Check_DiskTextDiffersFromIndexed_IsStale()
    {
        using var fx = JulieDbFixture.CreateForEdit();

        // The file changed on disk since it was indexed → Stale.
        string mutated = JulieDbFixture.OrderServiceContent.Replace("Total", "Sum");
        var result = FreshnessGate.Check(fx.DbPath, "orders/OrderService.cs", mutated);

        Assert.Equal(FreshnessResult.Stale, result.Result);
        Assert.True(result.IndexedContentFound);
    }

    [Fact]
    public void Check_Utf8MultibyteFile_RoundTripsWithoutFalseStale()
    {
        // The accented Café.cs would false-positive Stale if the indexed snapshot decoded the 'é' lossily.
        // Byte-identical disk text must read Fresh through the SHA256 comparison.
        using var fx = JulieDbFixture.CreateForEdit();

        var result = FreshnessGate.Check(fx.DbPath, "unicode/Café.cs", JulieDbFixture.CafeContent);

        Assert.Equal(FreshnessResult.Fresh, result.Result);
    }

    [Fact]
    public void Check_MissingIndexedContent_IsStale_WhenNotAllowingStale()
    {
        using var fx = JulieDbFixture.CreateForEdit();

        // A file julie never indexed has no snapshot to compare → can't verify → Stale.
        var result = FreshnessGate.Check(fx.DbPath, "ghost/Unknown.cs", "anything on disk");

        Assert.Equal(FreshnessResult.Stale, result.Result);
        Assert.False(result.IndexedContentFound);
    }

    // The gate reports the verdict; the TOOL decides whether allow_stale overrides it. But the gate exposes
    // IndexedContentFound so the tool can craft the right message ("no indexed snapshot" vs "changed on disk").
    [Fact]
    public void Check_MissingIndexedContent_ReportsNotFound_SoToolCanDistinguish()
    {
        using var fx = JulieDbFixture.CreateForEdit();

        var found = FreshnessGate.Check(fx.DbPath, "orders/OrderService.cs", JulieDbFixture.OrderServiceContent);
        var missing = FreshnessGate.Check(fx.DbPath, "ghost/Unknown.cs", "x");

        Assert.True(found.IndexedContentFound);
        Assert.False(missing.IndexedContentFound);
    }

    [Fact]
    public void Check_NullDbPath_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => FreshnessGate.Check(null!, "f.cs", "x"));
    }

    [Fact]
    public void Check_NullDiskText_Throws()
    {
        using var fx = JulieDbFixture.CreateForEdit();
        Assert.Throws<ArgumentNullException>(
            () => FreshnessGate.Check(fx.DbPath, "orders/OrderService.cs", null!));
    }
}
