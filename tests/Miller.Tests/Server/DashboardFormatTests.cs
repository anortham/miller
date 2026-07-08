using Miller.Dashboard;
using Xunit;

namespace Miller.Tests.Server;

public sealed class DashboardFormatTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RelativeTime_UnderFiveSeconds_ReadsJustNow()
    {
        Assert.Equal("just now", DashboardFormat.RelativeTime(Now, Now));
        Assert.Equal("just now", DashboardFormat.RelativeTime(Now.AddSeconds(-4), Now));
    }

    [Fact]
    public void RelativeTime_FutureValue_ClampsToJustNow()
    {
        // now - value is negative; JS clamps seconds to >= 0, so must we.
        Assert.Equal("just now", DashboardFormat.RelativeTime(Now.AddSeconds(30), Now));
    }

    [Theory]
    [InlineData(5, "5s ago")]
    [InlineData(59, "59s ago")]
    public void RelativeTime_SecondsBucket(int agoSeconds, string expected)
    {
        Assert.Equal(expected, DashboardFormat.RelativeTime(Now.AddSeconds(-agoSeconds), Now));
    }

    [Theory]
    [InlineData(60, "1m ago")]
    [InlineData(90, "1m ago")]
    [InlineData(3599, "59m ago")]
    public void RelativeTime_MinutesBucket(int agoSeconds, string expected)
    {
        Assert.Equal(expected, DashboardFormat.RelativeTime(Now.AddSeconds(-agoSeconds), Now));
    }

    [Theory]
    [InlineData(3600, "1h ago")]
    [InlineData(86399, "23h ago")]
    public void RelativeTime_HoursBucket(int agoSeconds, string expected)
    {
        Assert.Equal(expected, DashboardFormat.RelativeTime(Now.AddSeconds(-agoSeconds), Now));
    }

    [Theory]
    [InlineData(86400, "1d ago")]
    [InlineData(259200, "3d ago")]
    public void RelativeTime_DaysBucket(int agoSeconds, string expected)
    {
        Assert.Equal(expected, DashboardFormat.RelativeTime(Now.AddSeconds(-agoSeconds), Now));
    }

    [Fact]
    public void RelativeTime_String_ParsesIsoRoundtripValue()
    {
        Assert.Equal("1m ago", DashboardFormat.RelativeTime("2026-06-12T09:59:00.000Z", Now));
    }

    [Fact]
    public void RelativeTime_String_Unparseable_FallsBackToRawValue()
    {
        Assert.Equal("not-a-date", DashboardFormat.RelativeTime("not-a-date", Now));
    }

    [Fact]
    public void RelativeTime_String_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DashboardFormat.RelativeTime((string?)null, Now));
        Assert.Equal(string.Empty, DashboardFormat.RelativeTime(string.Empty, Now));
    }

    [Fact]
    public void AbsoluteShort_HumanizesUtcTimestamp_WithoutRawIsoOffset()
    {
        string label = DashboardFormat.AbsoluteShort("2026-06-12T10:00:00.000Z");

        Assert.Equal("Jun 12, 10:00 UTC", label);
        Assert.DoesNotContain("+00:00", label);
        Assert.DoesNotContain("T10:00", label);
    }

    [Fact]
    public void AbsoluteShort_Unparseable_FallsBackToRawValue()
    {
        Assert.Equal("garbage", DashboardFormat.AbsoluteShort("garbage"));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(999, "999 B")]
    public void FormatBytes_BytesTier(long bytes, string expected)
    {
        Assert.Equal(expected, DashboardFormat.FormatBytes(bytes));
    }

    [Theory]
    [InlineData(1000, "1.0 KB")]
    [InlineData(14500, "14.5 KB")]
    [InlineData(999999, "1000.0 KB")]
    public void FormatBytes_KilobytesTier(long bytes, string expected)
    {
        Assert.Equal(expected, DashboardFormat.FormatBytes(bytes));
    }

    [Theory]
    [InlineData(1_000_000, "1.0 MB")]
    [InlineData(3_500_000, "3.5 MB")]
    public void FormatBytes_MegabytesTier(long bytes, string expected)
    {
        Assert.Equal(expected, DashboardFormat.FormatBytes(bytes));
    }

    [Theory]
    [InlineData(1_000_000_000, "1.0 GB")]
    [InlineData(3_000_000_000, "3.0 GB")]
    [InlineData(12_340_000_000, "12.3 GB")]
    public void FormatBytes_GigabytesTier(long bytes, string expected)
    {
        Assert.Equal(expected, DashboardFormat.FormatBytes(bytes));
    }
}
