using SqlFerret.Core.Server;
using Xunit;

public class QdsConvertTests
{
    // Query Store datetime columns are datetimeoffset; SqlDataReader boxes them as DateTimeOffset,
    // which Convert.ToDateTime cannot unbox (InvalidCastException). AsDateTime must handle it.
    [Fact]
    public void AsDateTime_unboxes_DateTimeOffset_to_utc()
    {
        var dto = new DateTimeOffset(2026, 6, 24, 14, 0, 0, TimeSpan.FromHours(2)); // 12:00 UTC
        var result = QdsConvert.AsDateTime(dto);
        Assert.Equal(new DateTime(2026, 6, 24, 12, 0, 0, DateTimeKind.Utc), result);
        Assert.Equal(DateTimeKind.Utc, result.Kind);
    }

    [Fact]
    public void AsDateTime_passes_through_DateTime()
    {
        var dt = new DateTime(2026, 6, 24, 9, 30, 0);
        Assert.Equal(dt, QdsConvert.AsDateTime(dt));
    }

    [Theory]
    [InlineData(0.0, 0L)]
    [InlineData(1.5, 1500L)]
    [InlineData(0.4, 400L)]      // sub-millisecond wait must NOT collapse to 0
    [InlineData(0.001, 1L)]
    [InlineData(1234.0, 1234000L)]
    public void MsToUs_preserves_sub_millisecond(double ms, long expectedUs)
        => Assert.Equal(expectedUs, QdsConvert.MsToUs(ms));

    [Fact]
    public void WindowBound_null_uses_fallback()
    {
        Assert.Equal(DateTimeOffset.MinValue, QdsConvert.WindowBound(null, DateTimeOffset.MinValue));
        Assert.Equal(DateTimeOffset.MaxValue, QdsConvert.WindowBound(null, DateTimeOffset.MaxValue));
    }

    [Fact]
    public void WindowBound_treats_value_as_utc()
    {
        var d = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Unspecified);
        var bound = QdsConvert.WindowBound(d, DateTimeOffset.MinValue);
        Assert.Equal(TimeSpan.Zero, bound.Offset);
        Assert.Equal(new DateTime(2026, 6, 1, 8, 0, 0), bound.DateTime);
    }
}
