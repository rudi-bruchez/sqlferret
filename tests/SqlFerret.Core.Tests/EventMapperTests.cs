using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;
using Xunit;

public class EventMapperTests
{
    private sealed record FakeEvent(string Name, DateTime Timestamp,
        IReadOnlyDictionary<string, object?> Fields,
        IReadOnlyDictionary<string, object?> Actions) : IXeEventData;

    [Fact]
    public void Maps_rpc_completed_with_params_and_metrics()
    {
        var ev = new FakeEvent("rpc_completed", new DateTime(2026, 1, 1),
            Fields: new Dictionary<string, object?>
            {
                ["statement"] = "exec dbo.GetOrder @OrderId = 123",
                ["object_name"] = "dbo.GetOrder",
                ["duration"] = 4000L,
                ["cpu_time"] = 1000L,
                ["logical_reads"] = 50L
            },
            Actions: new Dictionary<string, object?>
            {
                ["database_name"] = "Sales",
                ["session_id"] = 57
            });

        var e = EventMapper.Map(ev, "s_0.xel", 7);

        Assert.Equal(EventClass.RpcCall, e.EventClass);
        Assert.Equal("dbo.GetOrder", e.ObjectName);
        Assert.Equal(4000L, e.DurationUs);
        Assert.Equal("Sales", e.DatabaseName);
        Assert.Equal(57, e.SessionId);
        Assert.Single(e.Parameters);
        Assert.Equal(7, e.FileOffset);
    }

    [Fact]
    public void Event_without_sql_is_unknown()
    {
        var ev = new FakeEvent("login", new DateTime(2026, 1, 1),
            new Dictionary<string, object?>(), new Dictionary<string, object?>());
        var e = EventMapper.Map(ev, "s_0.xel", 0);
        Assert.Equal(EventClass.Unknown, e.EventClass);
        Assert.Equal("", e.SqlTextRaw);
    }
}
