using SqlFerret.Core.Server;
using Xunit;

public class QueryStoreWindowTests
{
    static readonly DateTime Now = new(2026, 6, 24, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void No_flags_is_unbounded()
    {
        var w = QueryStoreWindow.Parse(null, null, null, Now);
        Assert.False(w.IsBounded);
        Assert.Null(w.From); Assert.Null(w.To);
    }

    [Fact]
    public void Last_hours_sets_from_to_now_minus_span()
    {
        var w = QueryStoreWindow.Parse(null, null, "24h", Now);
        Assert.True(w.IsBounded);
        Assert.Equal(Now.AddHours(-24), w.From);
        Assert.Equal(Now, w.To);
    }

    [Fact]
    public void Last_days_supported()
    {
        var w = QueryStoreWindow.Parse(null, null, "7d", Now);
        Assert.Equal(Now.AddDays(-7), w.From);
        Assert.Equal(Now, w.To);
    }

    [Fact]
    public void Explicit_from_to_parsed()
    {
        var w = QueryStoreWindow.Parse("2026-06-01T00:00:00", "2026-06-02T00:00:00", null, Now);
        Assert.Equal(new DateTime(2026, 6, 1), w.From);
        Assert.Equal(new DateTime(2026, 6, 2), w.To);
    }

    [Fact]
    public void Last_with_from_or_to_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => QueryStoreWindow.Parse("2026-06-01", null, "24h", Now));
        Assert.Throws<ArgumentException>(() => QueryStoreWindow.Parse(null, "2026-06-01", "24h", Now));
    }

    [Theory]
    [InlineData("24")]     // no unit
    [InlineData("24x")]    // bad unit
    [InlineData("abc")]    // not a number
    [InlineData("-3h")]    // negative
    public void Invalid_last_is_rejected(string bad)
        => Assert.Throws<ArgumentException>(() => QueryStoreWindow.Parse(null, null, bad, Now));

    [Fact]
    public void Invalid_datetime_is_rejected()
        => Assert.Throws<ArgumentException>(() => QueryStoreWindow.Parse("not-a-date", null, null, Now));
}
