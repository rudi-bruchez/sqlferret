using SqlFerret.Core.Filtering;
using SqlFerret.Core.Model;
using Xunit;

public class FilterCompilerTests
{
    [Fact]
    public void View_exclude_in_list_becomes_not_in()
    {
        var r = new FilterRule("noise", "object_name", "in",
            ["sp_cursorclose", "sp_unprepare"], null, "view", "exclude", true);
        var where = FilterCompiler.ToWhereClause([r]);
        Assert.Contains("object_name NOT IN ('sp_cursorclose', 'sp_unprepare')", where);
    }

    [Fact]
    public void View_numeric_gt_unquoted()
    {
        var r = new FilterRule("slow", "duration_us", "gt", null, "10000", "view", "keep", true);
        var where = FilterCompiler.ToWhereClause([r]);
        Assert.Contains("duration_us > 10000", where);
    }

    [Fact]
    public void Disabled_and_ingest_rules_ignored_in_where()
    {
        var disabled = new FilterRule("d", "database_name", "eq", null, "tempdb", "view", "exclude", false);
        var ingest = new FilterRule("i", "is_system", "eq", null, "true", "ingest", "exclude", true);
        Assert.Equal("1=1", FilterCompiler.ToWhereClause([disabled, ingest]));
    }

    [Fact]
    public void Ingest_predicate_drops_excluded_object()
    {
        var r = new FilterRule("noise", "object_name", "eq", null, "sp_reset_connection", "ingest", "exclude", true);
        var keep = FilterCompiler.ToIngestPredicate([r]);
        var dropped = new ExecutionEvent { EventName = "rpc_completed", SqlTextRaw = "x", XeFileName = "a", ObjectName = "sp_reset_connection" };
        var kept = new ExecutionEvent { EventName = "rpc_completed", SqlTextRaw = "x", XeFileName = "a", ObjectName = "dbo.Real" };
        Assert.False(keep(dropped));
        Assert.True(keep(kept));
    }

    [Fact]
    public void Sql_injection_in_value_is_escaped()
    {
        var r = new FilterRule("x", "login_name", "eq", null, "a'); DROP TABLE executions;--", "view", "exclude", true);
        var where = FilterCompiler.ToWhereClause([r]);
        Assert.Contains("''", where);            // quote doubled
        Assert.Contains("'a''); DROP TABLE executions;--'", where); // value escaped: single quote doubled, string literal not closed early
    }
}
