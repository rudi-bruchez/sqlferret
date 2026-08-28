using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Model;
using Xunit;

public class EventMapperTests
{
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

    [Fact]
    public void IsPlanProfile_matches_exact_name_only()
    {
        Assert.True(EventMapper.IsPlanProfile("query_post_execution_plan_profile"));
        Assert.True(EventMapper.IsPlanProfile("QUERY_POST_EXECUTION_PLAN_PROFILE"));
        Assert.False(EventMapper.IsPlanProfile("query_post_execution_showplan"));
        Assert.False(EventMapper.IsPlanProfile("rpc_completed"));
        Assert.False(EventMapper.IsPlanProfile("query_post_execution_plan_profile_x"));
    }

    [Fact]
    public void ExtractShowplanXml_reads_the_field_or_returns_null()
    {
        var withXml = new FakeEvent("query_post_execution_plan_profile", new DateTime(2026, 8, 4),
            new Dictionary<string, object?> { ["showplan_xml"] = "<ShowPlanXML/>" },
            new Dictionary<string, object?>());
        Assert.Equal("<ShowPlanXML/>", EventMapper.ExtractShowplanXml(withXml));

        var without = new FakeEvent("query_post_execution_plan_profile", new DateTime(2026, 8, 4),
            new Dictionary<string, object?>(), new Dictionary<string, object?>());
        Assert.Null(EventMapper.ExtractShowplanXml(without));
    }

    [Fact]
    public void Classify_still_returns_Unknown_for_plan_profile()
    {
        // Le routage se fait AVANT Map ; Classify ne doit pas être modifiée.
        var ev = new FakeEvent("query_post_execution_plan_profile", new DateTime(2026, 8, 4),
            new Dictionary<string, object?> { ["showplan_xml"] = "<ShowPlanXML/>" },
            new Dictionary<string, object?>());
        Assert.Equal(EventClass.Unknown, EventMapper.Map(ev, "s.xel", 0).EventClass);
    }
}
