// tests/SqlFerret.Tui.Tests/TestProject.cs
using SqlFerret.Core.Filtering;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Storage;

public static class TestProject
{
    private sealed record FakeEvent(
        string Name,
        DateTime Timestamp,
        IReadOnlyDictionary<string, object?> Fields,
        IReadOnlyDictionary<string, object?> Actions) : IXeEventData;

    /// <summary>
    /// Opens a temp DuckDB, ingests the supplied rows via IngestionService with
    /// RedactionMode.Full, and returns the open DuckDbProject (caller disposes).
    /// </summary>
    /// <param name="rows">
    /// Each tuple: (eventName, sql, durationUs, isRpc).
    /// eventName "sql_batch_completed" → sql in batch_text field.
    /// eventName "rpc_completed"        → sql in statement field + object_name populated.
    /// </param>
    public static DuckDbProject SeedFrom(
        IEnumerable<(string eventName, string sql, long durationUs, bool isRpc)> rows)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf_tui_{Guid.NewGuid():N}.duckdb");
        var project = DuckDbProject.Open(path);

        var events = rows.Select((r, i) => BuildEvent(r.eventName, r.sql, r.durationUs, r.isRpc, i)).ToList();

        var svc = new IngestionService(project,
            new IngestionOptions(RedactionMode.Full, Array.Empty<FilterRule>()));

        svc.Ingest("test/", events);

        return project;
    }

    private static (IXeEventData, string, long) BuildEvent(
        string eventName, string sql, long durationUs, bool isRpc, int index)
    {
        var fields = new Dictionary<string, object?>();
        if (isRpc || eventName.Contains("rpc", StringComparison.OrdinalIgnoreCase))
        {
            fields["statement"] = sql;
            fields["object_name"] = sql.Split(' ').Skip(1).FirstOrDefault() ?? "dbo.Proc";
        }
        else
        {
            fields["batch_text"] = sql;
        }
        fields["duration"] = durationUs;

        var actions = new Dictionary<string, object?>
        {
            ["database_name"] = "TestDb",
            ["session_id"] = 1,
        };

        var ts = new DateTime(2026, 1, 1, 0, 0, index % 60);
        return (new FakeEvent(eventName, ts, fields, actions), "test_0.xel", (long)index);
    }
}
